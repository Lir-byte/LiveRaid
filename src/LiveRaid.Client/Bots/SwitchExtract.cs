using System;
using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using QuestingBots.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Leaving through an exit that a switch opens (Interchange saferoom, Reserve bunker and D-2, Labs gates and lifts...), like players:
    /// run to the switch (the first one of its chain that is still off), pull it, and when the exit opens run there, stand its timer and
    /// leave the raid. If someone else pulled it first, the bot sees the switch already on and goes straight to the open exit.
    /// The game's own bot exfil only uses exits without requirements, so these exits were never used by bots before.
    /// A squad does it together: the leader picks the exit, the followers get the same plan; whoever reaches a switch first pulls it.
    /// </summary>
    public static class SwitchExtract
    {
        public class SwitchExit
        {
            public ExfiltrationPoint Exit;
            public List<Switch> Chain = new List<Switch>();
            private Vector3 _stand;
            private float _standAt = -99f;

            /// <summary>
            /// Where a bot stands to take the exit. The armoured train's zone rides on the train: its stand point is taken again each second (the train arrives
            /// somewhere else than the zone sat when the raid began).
            /// </summary>
            public Vector3 Stand
            {
                get
                {
                    if (IsTrain && Time.time - _standAt > 1f)
                    {
                        _standAt = Time.time;
                        _stand = StandPoint(Exit);
                    }

                    return _stand;
                }
                set { _stand = value; }
            }

            /// <summary>The armoured train (Reserve): open to board from its arrival, leaves a while after.</summary>
            public bool IsTrain => IsTrainExit(Exit);

            /// <summary>
            /// No switch at all: an exit the game's own bot exfil does not know (it has only the "AI exit points" of the map - Reserve: D-2 and the bunker
            /// door) and so never took a bot to - the sewer manhole (no backpack), the cliff descent (items) and the like. LiveRaid walks the bot there.
            /// </summary>
            public bool Direct;

            /// <summary>Who may use it: the PMC list of the map, the scav list (player scavs), or both (shared exits).</summary>
            public bool ForPmc, ForScav;

            public string Name => Exit.Settings.Name;
            public float Timer => Exit.Settings.ExfiltrationTime;

            public bool Open => Exit.Status == EExfiltrationStatus.RegularMode || Exit.Status == EExfiltrationStatus.Countdown
                                || (IsTrain && Exit.Status == EExfiltrationStatus.AwaitsManualActivation);

            public bool Present => Exit.Status != EExfiltrationStatus.NotPresent;

            /// <summary>The next switch a bot has to pull (null: all on).</summary>
            public Switch NextSwitch => Chain.FirstOrDefault(s => s != null && s.DoorState != EDoorState.Open);

            /// <summary>
            /// The doors the switches of this exit open (EFT Switch.Door): the exit's status turns on the moment the door STARTS to move, so "the exit is open"
            /// is not yet "the way is open" (Reserve D-2, raid 2026-11-11 15:23: Barbaric went in through the door that was still opening).
            /// </summary>
            public List<Door> Gates = new List<Door>();
        }

        public enum Stage
        {
            ToSwitch,
            ToExit,
            Timer,
        }

        public class Plan
        {
            public SwitchExit Exit;
            public Stage Stage;
            public float StartedAt;
            public float StageSince;
            public float TimerStart;
            public float StuckSince;
            public Vector3 LastPos;
            public string Reason;

            /// <summary>Where the bot has been (one point a second, the last 30): the way it came in is the way it covers its back on.</summary>
            public readonly List<Vector3> Trail = new List<Vector3>();
            public float TrailAt;
            public int Rescues;
        }

        private static List<SwitchExit> _exits;
        private static string _exitsLocation;
        private static readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>();

        public static int Pulled { get; private set; }

        /// <summary>The bot being removed through a switch exit right now (its own log line, not the generic one).</summary>
        internal static BotOwner LeavingNow;
        public static int LeftThrough { get; private set; }

        public static void Clear()
        {
            _exits = null;
            _exitsLocation = null;
            _whyNot.Clear();
            _plans.Clear();
            Pulled = LeftThrough = 0;
        }

        public static bool TryGetPlan(BotOwner bot, out Plan plan)
        {
            plan = null;
            return bot != null && _plans.TryGetValue(bot.ProfileId, out plan);
        }

        public static bool HasPlan(BotOwner bot) => bot != null && _plans.ContainsKey(bot.ProfileId);

        /// <summary>Switch-opened exits of this map for PMCs (found once per raid).</summary>
        public static List<SwitchExit> Exits()
        {
            string location = Singleton<GameWorld>.Instance?.LocationId;
            if (_exits != null && _exitsLocation == location)
            {
                return _exits;
            }

            _exitsLocation = location;
            _exits = new List<SwitchExit>();
            ExfiltrationPoint[] pmcOnly = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (pmcOnly == null)
            {
                return _exits;
            }

            ScavExfiltrationPoint[] scavList = ExfiltrationController.Instance?.ScavExfiltrationPoints ?? new ScavExfiltrationPoint[0];
            ExfiltrationPoint[] pmcExits = pmcOnly.Concat(scavList.Where(e => e != null && !pmcOnly.Contains(e))).ToArray();

            // The location's own list of interactive objects (what the game syncs them by): a scene-wide FindObjectsOfType cost ~90 ms in one frame
            Switch[] switches = LocationScene.GetAllObjects<WorldInteractiveObject>().OfType<Switch>().ToArray();
            // A switch another one OPENS by itself is not pulled by hand. One it only UNLOCKS is (D-2 of Reserve: the power lever unlocks the button at the
            // door - counting the button as automatic left the chain with the lever alone, and the exit never opened). Who triggers whom is kept too:
            // an exit whose last switch is opened by another one (the bunker's hermetic door) is reached through that one.
            HashSet<Switch> automatic = new HashSet<Switch>();
            Dictionary<Switch, Switch> triggeredBy = new Dictionary<Switch, Switch>();
            foreach (Switch s in switches)
            {
                if (s.NextSwitches == null)
                {
                    continue;
                }

                foreach (Switch.SwitchAndOperation n in s.NextSwitches)
                {
                    if (n?.Switch == null || n.Switch == s)
                    {
                        continue;
                    }

                    triggeredBy[n.Switch] = s;
                    if (n.Operation == EInteractionType.Open)
                    {
                        automatic.Add(n.Switch);
                    }
                }
            }
            List<AIExfiltrationPoint> aiPoints = null;
            try
            {
                aiPoints = Singleton<IBotGame>.Instance?.BotsController?.CoversData?.Patrols?.ExfiltrationPoints;
            }
            catch
            {
                // no AI exit data on this map
            }

            foreach (Switch last in switches.Where(s => s.ExfiltrationPoint != null && pmcExits.Contains(s.ExfiltrationPoint)))
            {
                SwitchExit exit = _exits.FirstOrDefault(e => e.Exit == last.ExfiltrationPoint);
                if (exit != null)
                {
                    continue;
                }

                exit = new SwitchExit { Exit = last.ExfiltrationPoint, ForPmc = pmcOnly.Contains(last.ExfiltrationPoint), ForScav = scavList.Contains(last.ExfiltrationPoint) };
                List<Switch> chain = new List<Switch>();
                for (Switch s = last; s != null && !chain.Contains(s) && chain.Count < 8;
                     s = s.PreviousSwitch != null ? s.PreviousSwitch : triggeredBy.TryGetValue(s, out Switch by) ? by : null)
                {
                    chain.Add(s);
                }

                chain.Reverse();

                // Switches another switch turns on by itself are not pulled by hand
                exit.Chain = chain.Where(s => !automatic.Contains(s)).ToList();
                exit.Gates = switches.Where(s => s != null && s.Door != null && (chain.Contains(s) || s.ExfiltrationPoint == last.ExfiltrationPoint))
                    .Select(s => s.Door).Distinct().ToList();
                AIExfiltrationPoint ai = aiPoints?.FirstOrDefault(p => p.Name == exit.Name);
                exit.Stand = ai != null ? ai.Position : StandPoint(exit.Exit);
                _exits.Add(exit);
            }

            // Exits with no switch the game's bot exfil does not know: LiveRaid takes the bot there on its own legs (conditions are the bot's, ExitRules)
            foreach (ExfiltrationPoint ex in pmcExits)
            {
                if (ex?.Settings == null || ex.Status == EExfiltrationStatus.NotPresent || _exits.Any(e => e.Exit == ex)
                    || (aiPoints != null && aiPoints.Any(p => p != null && p.Name == ex.Settings.Name)))
                {
                    continue;
                }

                _exits.Add(new SwitchExit { Exit = ex, Direct = true, Stand = StandPoint(ex), ForPmc = pmcOnly.Contains(ex), ForScav = scavList.Contains(ex) });
            }

            if (_exits.Count > 0)
            {
                Log.Info($"LR Extract: switch exits on {location}: " +
                         string.Join(", ", _exits.Select(e => e.Direct ? $"{e.Name.Localized()} (own way, {e.Timer:0} s, {e.Exit.Status})"
                         : $"{e.Name.Localized()} ({e.Chain.Count} switch{(e.Chain.Count == 1 ? "" : "es")}, {e.Timer:0} s, {e.Exit.Status})")));
                foreach (SwitchExit e in _exits)
                {
                    if (e.Direct)
                    {
                        Log.Info($"LR Extract:   {e.Name.Localized()}: an exit of its own (no switch, not an AI exit point), stand ({e.Stand.x:0.0}, {e.Stand.y:0.0}, {e.Stand.z:0.0}), " +
                                 $"{e.Exit.Status}, needs: " + (e.Exit.Requirements == null || e.Exit.Requirements.Length == 0 ? "nothing"
                                     : string.Join(" + ", e.Exit.Requirements.Where(r => r != null).Select(r => $"{r.Requirement} {r.Id} {r.Count} {r.RequiredSlot}"))));
                        continue;
                    }

                    Log.Info($"LR Extract:   {e.Name.Localized()}: stand ({e.Stand.x:0.0}, {e.Stand.y:0.0}, {e.Stand.z:0.0}); pull in turn: " +
                             (e.Chain.Count == 0 ? "nothing to pull by hand" : string.Join(" -> ", e.Chain.Select(s =>
                                 $"{s.Id} at ({s.transform.position.x:0.0}, {s.transform.position.y:0.0}, {s.transform.position.z:0.0}) {s.DoorState}" +
                                 $"{(NavMesh.SamplePosition(s.transform.position, out _, 5f, NavMesh.AllAreas) ? "" : " OFF THE NAVMESH")}"))));
                }
            }

            return _exits;
        }

        /// <summary>The exit needs the train (EFT TrainRequirement: the exit opens when the locomotive has arrived and works until it has gone).</summary>
        public static bool IsTrainExit(ExfiltrationPoint exit) =>
            exit?.Requirements != null && exit.Requirements.Any(r => r != null && r.Requirement == ERequirementState.Train);

        private static Vector3 StandPoint(ExfiltrationPoint exit)
        {
            BoxCollider zone = exit.GetComponent<BoxCollider>();
            Vector3 center = zone != null ? zone.bounds.center : exit.transform.position;
            if (NavMesh.SamplePosition(center, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                return hit.position;
            }

            // A zone on a train carriage: the mesh is at the platform beside it, within the box when there is any
            if (zone != null && NavMesh.SamplePosition(zone.ClosestPoint(center + Vector3.down * 3f), out hit, 8f, NavMesh.AllAreas))
            {
                return hit.position;
            }

            return NavMesh.SamplePosition(center, out hit, 15f, NavMesh.AllAreas) ? hit.position : center;
        }

        internal static Vector3 SwitchStand(Switch s) =>
            NavMesh.SamplePosition(s.transform.position, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ? hit.position
            : NavMesh.SamplePosition(s.transform.position, out hit, 5f, NavMesh.AllAreas) ? hit.position : s.transform.position;

        private static readonly HashSet<string> _whyNot = new HashSet<string>();
        private static Vector3 _pathEnd;
        private static bool _pathEndKnown;

        /// <summary>Why an exit was no use to a bot - once per exit and reason (and per two minutes: the doors on the way come and go).</summary>
        private static void WhyNot(SwitchExit exit, string why)
        {
            int cut = why.IndexOf(" - ", StringComparison.Ordinal);
            string key = exit.Name + "|" + (cut > 0 ? why.Substring(0, cut) : why) + "|" + (int)(Time.time / 120f);
            if (_whyNot.Add(key))
            {
                Log.Info($"LR Extract: switch exit {exit.Name.Localized()} is no use now: {why}{DoorsNearEnd()}");
            }
        }

        /// <summary>The doors by the end of a path that stopped short - what cut the way (a shut door, a locked one), for the next log reading.</summary>
        private static string DoorsNearEnd()
        {
            if (!_pathEndKnown)
            {
                return "";
            }

            try
            {
                var near = LocationScene.GetAllObjects<WorldInteractiveObject>().OfType<Door>()
                    .Where(d => d != null && (d.transform.position - _pathEnd).sqrMagnitude < 5f * 5f).Take(4).ToList();
                return near.Count == 0 ? "; no door within 5 m of that point"
                    : "; doors there: " + string.Join(", ", near.Select(d => $"{d.Id} {d.DoorState}{(string.IsNullOrEmpty(d.KeyId) ? "" : " key " + d.KeyId)}"));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// A switch exit this bot could take now, and how far its whole way is (switches first, then the exit). Null: none usable
        /// (absent this raid, no path, or a chain switch that is locked for good).
        /// </summary>
        public static SwitchExit Choose(BotOwner bot, out float way)
        {
            way = float.MaxValue;
            bool scav = BotProfiles.KindOf(bot) == BotKind.PlayerScav;
            SwitchExit best = null;
            NavMeshPath path = new NavMeshPath();
            foreach (SwitchExit exit in Exits())
            {
                if (!exit.Present || !(scav ? exit.ForScav : exit.ForPmc))
                {
                    continue;
                }

                if (exit.Direct)
                {
                    if (!exit.Open)
                    {
                        continue;
                    }

                    if (!ExitRules.Allows(bot, exit.Exit, out _))
                    {
                        WhyNot(exit, "a condition of the exit this bot does not meet (item, empty slot, money)");
                        continue;
                    }
                }
                else if (exit.Chain.Count == 0 && !exit.Open)
                {
                    WhyNot(exit, "closed, and nothing a bot could pull opens it");
                    continue;
                }

                Vector3 from = bot.Position;
                float total = 0f;
                bool ok = true;
                if (!exit.Open)
                {
                    foreach (Switch s in exit.Chain.Where(s => s.DoorState != EDoorState.Open))
                    {
                        Vector3 at = SwitchStand(s);
                        if (!TryReach(from, at, path, out Vector3 stand, out _, out float leg))
                        {
                            WhyNot(exit, $"no way on foot to switch {s.Id} at ({at.x:0.0}, {at.y:0.0}, {at.z:0.0}) - {_pathNote}");
                            ok = false;
                            break;
                        }

                        total += leg;
                        from = stand;
                    }
                }

                float last = -1f;
                if (ok)
                {
                    last = TryReach(from, exit.Stand, path, out _, out _, out float lastLeg) ? lastLeg : -1f;
                }

                if (ok && last < 0f && !exit.Open && exit.Chain.Count > 0)
                {
                    // The way to a closed exit may well be shut until the switch is pulled (a blast door across the tunnel): the plan is not thrown away
                    // for it - the last stretch is guessed, and the bot finds out at the open door
                    last = Vector3.Distance(from, exit.Stand) * 1.5f;
                }

                if (!ok || last < 0f)
                {
                    if (ok)
                    {
                        WhyNot(exit, $"no way on foot to its stand point ({exit.Stand.x:0.0}, {exit.Stand.y:0.0}, {exit.Stand.z:0.0}) - {_pathNote}");
                    }

                    continue;
                }

                total += last;
                if (total < way)
                {
                    way = total;
                    best = exit;
                }
            }

            return best;
        }

        private static string _pathNote = "";

        public enum ReachMode
        {
            /// <summary>On foot to the point (or to where the path ends within arm's reach of it).</summary>
            Walk,

            /// <summary>
            /// The navmesh stops short of the point - the switch is down a shaft or behind a stair the bots' mesh does not cover (Reserve: the D-2
            /// power lever 8 m under the mesh, the bunker's button 22 m down). The bot walks to where the mesh ends, spends the time it would take
            /// to get there, and the switch is pulled for it. Players do it by hand; for a bot there is no mesh to walk on.
            /// </summary>
            Hidden,
        }

        /// <summary>
        /// Where a bot stands to reach <paramref name="target"/> and how: a complete path (Walk, to the point); a path that stops within 4.5 m of it
        /// (Walk, to its end - a lever on a wall); one that stops within 20 m across and 30 m up/down (Hidden, to its end). Else false, the note says why.
        /// </summary>
        public static bool TryReach(Vector3 from, Vector3 target, NavMeshPath path, out Vector3 stand, out ReachMode mode, out float length) =>
            TryReach(from, target, path, out stand, out mode, out length, out _);

        /// <summary>
        /// As above, and <paramref name="via"/>: when Unity's search gave up on a long way (a "partial" path that stops short, ending right above or beside
        /// the target), our own search over the whole mesh (Raid/LongPath) gives via-points - the bot walks them in turn, each leg short enough for Unity.
        /// </summary>
        public static bool TryReach(Vector3 from, Vector3 target, NavMeshPath path, out Vector3 stand, out ReachMode mode, out float length, out Vector3[] via)
        {
            via = null;
            stand = target;
            mode = ReachMode.Walk;
            length = -1f;
            _pathEndKnown = false;
            if (!NavMesh.CalculatePath(from, target, NavMesh.AllAreas, path) || path.corners.Length == 0)
            {
                _pathNote = "no path at all";
                return false;
            }

            float sum = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                sum += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            if (path.status == NavMeshPathStatus.PathComplete)
            {
                length = sum;
                return true;
            }

            // Unity's search gave up before the target: the whole mesh is searched here
            if (Raid.LongPath.TryFind(from, target, out Vector3[] long_, out float longLength))
            {
                via = long_;
                stand = long_[long_.Length - 1];
                length = longLength;
                return true;
            }

            if (Raid.LongPath.LastDeferred)
            {
                // The way search is still being made on its worker thread: the bot asks again (not the shaft shortcut for a way that may well exist)
                _pathNote = "the way search of the whole map is not ready yet";
                return false;
            }

            Vector3 end = path.corners[path.corners.Length - 1];
            Vector3 gap = target - end;
            float across = new Vector2(gap.x, gap.z).magnitude;
            float vertical = Mathf.Abs(gap.y);
            float total = gap.magnitude;
            if (total <= 4.5f)
            {
                stand = end;
                length = sum;
                return true;
            }

            if (across <= 20f && vertical <= 30f && total <= 40f)
            {
                stand = end;
                mode = ReachMode.Hidden;
                length = sum + total;
                return true;
            }

            _pathEnd = end;
            _pathEndKnown = true;
            _pathNote = $"the path ends {total:0} m short ({across:0} across, {vertical:0} up or down) at ({end.x:0.0}, {end.y:0.0}, {end.z:0.0})";
            return false;
        }

        public static void Assign(BotOwner bot, SwitchExit exit, string why)
        {
            _plans[bot.ProfileId] = new Plan
            {
                Exit = exit,
                Stage = exit.Open ? Stage.ToExit : Stage.ToSwitch,
                StartedAt = Time.time,
                StageSince = Time.time,
                LastPos = bot.Position,
                StuckSince = Time.time,
                Reason = why,
            };
        }

        /// <summary>Gives the plan up (exit gone, no way, stuck): the bot leaves the usual way.</summary>
        public static void Drop(BotOwner bot, string why)
        {
            if (!_plans.TryGetValue(bot.ProfileId, out Plan plan))
            {
                return;
            }

            _plans.Remove(bot.ProfileId);
            bot.Exfiltration?.ResetLeaveTime();
            NoWayOut.SwitchFailed(bot);
            if (!NoWayOut.HasOrdinaryExit(bot))
            {
                // Reserve and the like: no ordinary exit for a bot either - it stays in the raid rather than idle about as "leaving" (NoWayOut)
                Log.Info($"LR Extract: {bot.Profile.Nickname} gives up the switch exit {plan.Exit.Name.Localized()} ({why})");
                NoWayOut.GiveRaidBack(bot, BotProfiles.Get(bot), "the switch exit came to nothing and no other exit is open to it");
                return;
            }

            Log.Info($"LR Extract: {bot.Profile.Nickname} gives up the switch exit {plan.Exit.Name.Localized()} ({why}) and leaves through an ordinary exit");
            ExtractPlanner.ExtractTheUsualWay(bot);
        }

        internal static void CountPulled() => Pulled++;

        internal static void CountLeft() => LeftThrough++;

        internal static void Forget(BotOwner bot) => _plans.Remove(bot.ProfileId);
    }

    /// <summary>
    /// Priority 24: above QuestingBots, LiveRaid's tactics and unsticking and SAIN's squad layer - leaving is the plan now - but off during
    /// a real fight (hit, shot at, an enemy seen close), when SAIN fights; and while the bot is bandaging.
    /// </summary>
    public class SwitchExtractLayer : CustomLayer
    {
        public const int LayerPriority = 24;

        public SwitchExtractLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Switch Exit";

        public override Action GetNextAction() => new Action(typeof(SwitchExtractLogic), "switch exit");

        public override bool IsCurrentActionEnding() => !SwitchExtract.HasPlan(BotOwner);

        public override bool IsActive()
        {
            try
            {
                if (!SwitchExtract.TryGetPlan(BotOwner, out SwitchExtract.Plan plan) || BotOwner.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(BotOwner))
                {
                    return false;
                }

                if (!plan.Exit.Present)
                {
                    SwitchExtract.Drop(BotOwner, "the exit is closed this raid");
                    return false;
                }

                if (Time.time - plan.StartedAt > 600f)
                {
                    SwitchExtract.Drop(BotOwner, "took too long");
                    return false;
                }

                // Aboard the armoured train a far enemy is no reason to climb down and hunt him (raid 2026-11-11 16:06: Тихон Зверь boarded at 30:40, SAIN's search took
                // him out of the zone after Гадюка / Пантера / Скорпион 50-90 m off, and he was killed at 34:53, thirty seconds before the train left)
                bool boarded = plan.Exit.IsTrain && plan.Stage == SwitchExtract.Stage.Timer;
                bool busy = (boarded ? Tactics.HitReaction.InCloseFight(BotOwner, 10f, 35f) : Tactics.HitReaction.InRealFight(BotOwner, 10f)) || BotOwner.Medecine?.Using == true;
                if (busy)
                {
                    // The time a fight or a bandaging takes is not time "stuck on the way": raid 2026-11-11 15:23, Meldonin's plan was thrown away at 8:18
                    // ("stuck on the way") by the 40 s clock that had run on through a fight and a treatment
                    plan.StuckSince = Time.time;
                    plan.LastPos = BotOwner.Position;
                }

                return !busy;
            }
            catch (Exception e)
            {
                Diag.Report("SwitchExtractLayer", e);
                return false;
            }
        }
    }

    public class SwitchExtractLogic : CustomLogic
    {
        private float _nextMove;
        private float _pulledAt = -1f;
        private float _lockedSince = -1f;
        private int _pulls;
        private object _reachFor;
        private bool _reachOk;
        private float _reachAt;
        private Vector3 _stand;
        private Vector3[] _via;
        private int _viaAt;
        private SwitchExtract.ReachMode _mode;
        private float _hiddenSince = -1f;
        private static readonly NavMeshPath _scratch = new NavMeshPath();

        public SwitchExtractLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            _pulledAt = -1f;
            _pulls = 0;
            _reachFor = null;
            _hiddenSince = -1f;
            _gateSince = -1f;
            _bagSince = -1f;
            _gateDone = false;
            _tWay = null;
            _tSettled = false;
            _tNearSince = -1f;
            _spotFor = null;
            _spot = null;
            base.Start();
        }

        public override void Stop()
        {
            LeaveInterior();
            BotOwner.PatrollingData.Unpause();
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (!SwitchExtract.TryGetPlan(BotOwner, out SwitchExtract.Plan plan))
            {
                return;
            }

            try
            {
                Step(plan);
            }
            catch (Exception e)
            {
                Diag.Report("SwitchExtractLogic", e);
                SwitchExtract.Drop(BotOwner, "error");
            }
        }

        /// <summary>Where this bot stands to reach the target (cached per target; a failed try is not repeated for 5 s).</summary>
        private bool Reach(object key, Vector3 target)
        {
            if (_reachFor == key && (_reachOk || Time.time - _reachAt < 5f))
            {
                return _reachOk;
            }

            _reachFor = key;
            _reachAt = Time.time;
            _hiddenSince = -1f;
            _via = null;
            _viaAt = 0;
            _reachOk = SwitchExtract.TryReach(BotOwner.Position, target, _scratch, out _stand, out _mode, out _, out _via);
            return _reachOk;
        }

        /// <summary>The next point to walk to: the via-points of a long way in turn (each within 3.5 m counts), then <paramref name="final"/>.</summary>
        private Vector3 Next(Vector3 final)
        {
            if (_via == null)
            {
                return final;
            }

            while (_viaAt < _via.Length - 1 && (BotOwner.Position - _via[_viaAt]).sqrMagnitude < 3.5f * 3.5f)
            {
                _viaAt++;
            }

            return _via[_viaAt];
        }

        /// <summary>The time a bot spends getting down to a point the mesh does not cover: by the distance, 8-30 s.</summary>
        private static float HiddenWait(Vector3 stand, Vector3 target) => Mathf.Clamp(Vector3.Distance(stand, target) / 1.3f, 8f, 30f);

        private void Step(SwitchExtract.Plan plan)
        {
            SwitchExtract.SwitchExit exit = plan.Exit;
            string name = BotOwner.Profile.Nickname;

            if (Time.time - plan.TrailAt >= 1f)
            {
                plan.TrailAt = Time.time;
                plan.Trail.Add(BotOwner.Position);
                if (plan.Trail.Count > 30)
                {
                    plan.Trail.RemoveAt(0);
                }
            }

            // Stuck on the way for long: first the last step of a player who is wedged (put back on the navmesh a few metres on, as FrozenWatch does after
            // minutes), the plan is given up only when that has not helped (raid 2026-11-11 15:23: Meldonin stood 40 s with EFT's CanWalk false, no treatment, no
            // enemy at hand - and lost a plan he had pulled the hermetic lever for)
            if ((BotOwner.Position - plan.LastPos).sqrMagnitude > 1f)
            {
                plan.LastPos = BotOwner.Position;
                plan.StuckSince = Time.time;
            }
            else if (plan.Stage != SwitchExtract.Stage.Timer && _gateSince < 0f && Time.time - plan.StuckSince > 40f)
            {
                if (plan.Rescues < 2 && Raid.RaidMonitor.RescueFrozen(BotOwner, plan.StuckSince, Time.time))
                {
                    plan.Rescues++;
                    plan.StuckSince = Time.time;
                    plan.LastPos = BotOwner.Position;
                    Log.Info($"LR Extract: {name} stood still on its way out for 40 s - put back on the navmesh a little farther on ({plan.Rescues} of 2)");
                    return;
                }

                if (Time.time - plan.StuckSince > 90f || plan.Rescues >= 2)
                {
                    SwitchExtract.Drop(BotOwner, "stuck on the way");
                    return;
                }
            }

            if (plan.Stage == SwitchExtract.Stage.ToSwitch)
            {
                if (exit.Open || exit.NextSwitch == null)
                {
                    if (!exit.Open && Time.time - plan.StageSince < 25f)
                    {
                        // All switches on, the exit opens after the switch's delay: wait by the last switch, back to the room it came through
                        BotOwner.StopMove();
                        Switch last = exit.Chain.LastOrDefault(s => s != null);
                        CoverBack(plan, last != null ? last.transform.position : exit.Gates.Count > 0 ? exit.Gates[0].transform.position : exit.Stand);
                        return;
                    }

                    if (!exit.Open)
                    {
                        SwitchExtract.Drop(BotOwner, "the exit did not open");
                        return;
                    }

                    plan.Stage = SwitchExtract.Stage.ToExit;
                    plan.StageSince = Time.time;
                    Log.Info($"LR Extract: {name} - exit {exit.Name.Localized()} is open, runs there");
                    return;
                }

                Switch target = exit.NextSwitch;
                Vector3 want = SwitchExtract.SwitchStand(target);
                if (!Reach(target, want))
                {
                    SwitchExtract.Drop(BotOwner, $"no way on foot to switch {target.Id}");
                    return;
                }

                Vector3 at = _stand;
                if (target.DoorState == EDoorState.Locked)
                {
                    // The switch before it unlocks this one a moment after it is pulled (its animation and delay): go there meanwhile, give up only
                    // when it has stayed locked for long with the bot standing at it
                    if (_lockedSince < 0f)
                    {
                        _lockedSince = Time.time;
                    }

                    bool there = (BotOwner.Position - at).sqrMagnitude <= 2f * 2f;
                    if (there && Time.time - _lockedSince > 30f)
                    {
                        SwitchExtract.Drop(BotOwner, $"switch {target.Id} stays locked");
                        return;
                    }

                    if (there)
                    {
                        BotOwner.StopMove();
                        BotOwner.Steering.LookToPoint(target.transform.position);
                        return;
                    }
                }
                else
                {
                    _lockedSince = -1f;
                }

                if ((BotOwner.Position - at).sqrMagnitude > 1.3f * 1.3f)
                {
                    Move(Next(at), sprint: (BotOwner.Position - at).sqrMagnitude > 25f * 25f);
                    return;
                }

                BotOwner.StopMove();
                BotOwner.Steering.LookToPoint(target.transform.position);
                if (target.DoorState == EDoorState.Interacting)
                {
                    return;
                }

                if (_mode == SwitchExtract.ReachMode.Hidden)
                {
                    // The switch is out of the mesh (down a shaft): the bot spends the time it would take to get there, then it is pulled
                    if (_hiddenSince < 0f)
                    {
                        _hiddenSince = Time.time;
                        Log.Info($"LR Extract: {name} is at the end of the way to switch {target.Id} ({Vector3.Distance(at, want):0} m of it off the mesh) - " +
                                 $"takes {HiddenWait(at, want):0} s to get there");
                    }

                    if (Time.time - _hiddenSince < HiddenWait(at, want))
                    {
                        return;
                    }
                }

                if (_pulledAt < 0f || Time.time - _pulledAt > 4f)
                {
                    _pulledAt = Time.time;
                    // By hand first (its animation); when that changed nothing, or the switch is out of reach, the game's own state change
                    if (_mode == SwitchExtract.ReachMode.Walk && _pulls == 0)
                    {
                        InteractionResult result = target.GetInteractionResult(EInteractionType.Open, BotOwner);
                        BotOwner.InteractWithWorldInteractiveObject(target, result);
                    }
                    else
                    {
                        target.Interact(EInteractionType.Open);
                    }

                    _pulls++;
                    SwitchExtract.CountPulled();
                    Log.Info($"LR Extract: {name} pulls switch {target.Id} for exit {exit.Name.Localized()}" +
                             (_mode == SwitchExtract.ReachMode.Hidden ? " (from where the mesh ends)" : _pulls > 1 ? " (the game's own way: the hand did nothing)" : ""));
                }

                return;
            }

            if (!exit.Open)
            {
                // Closed again (a timed exit) before we got there: switches again, if they can be pulled
                LeaveInterior();
                plan.Stage = SwitchExtract.Stage.ToSwitch;
                plan.StageSince = Time.time;
                BotOwner.Exfiltration?.ResetLeaveTime();
                return;
            }

            if (exit.IsTrain)
            {
                TrainStep(plan, exit, name);
                return;
            }

            // The exit's status is on, but its door is still opening: nobody walks through a leaf in motion - the bot stands guard first
            if (plan.Stage != SwitchExtract.Stage.Timer && GateWait(plan, exit, name))
            {
                return;
            }

            // Its own place in the zone (random, apart from the others, the more hidden from outside the better); no mesh in the zone - the stand point
            if (_spotFor != exit)
            {
                _spotFor = exit;
                _spot = null;
                _spotSince = Time.time;
                if (!exit.Direct && !exit.IsTrain)
                {
                    _spot = LiveRaid.Client.Raid.ExitSpots.Pick(exit.Exit, BotOwner.Position, exit.Stand);
                    if (_spot.HasValue)
                    {
                        Log.Info($"LR Extract: {name} takes its own place in exit {exit.Name.Localized()} at ({_spot.Value.x:0.0}, {_spot.Value.z:0.0}), {(_spot.Value - exit.Stand).magnitude:0.0} m from the usual stand point");
                    }
                }
            }

            Vector3 standAt = _spot ?? exit.Stand;
            bool inside = _spot.HasValue
                ? (BotOwner.Position - standAt).sqrMagnitude < 1.5f * 1.5f || (InExitZone(exit, BotOwner.Position) && Time.time - _spotSince > 40f)
                : InExitZone(exit, BotOwner.Position) || (BotOwner.Position - standAt).sqrMagnitude < 1.5f * 1.5f;
            Vector3 goal = standAt;
            if (!inside && Reach(_spot.HasValue ? (object)_spotFor : exit, standAt))
            {
                goal = _stand;
                if (_mode == SwitchExtract.ReachMode.Hidden && (BotOwner.Position - goal).sqrMagnitude <= 2f * 2f)
                {
                    // The stand point is off the mesh (D-2 is 20 m under it): at the end of the way the bot goes down for a while and is in the zone
                    BotOwner.StopMove();
                    if (_hiddenSince < 0f)
                    {
                        _hiddenSince = Time.time;
                        Log.Info($"LR Extract: {name} is at the end of the way to exit {exit.Name.Localized()} ({Vector3.Distance(goal, exit.Stand):0} m of it off the mesh) - " +
                                 $"takes {HiddenWait(goal, exit.Stand):0} s to get down");
                    }

                    if (Time.time - _hiddenSince >= HiddenWait(goal, exit.Stand))
                    {
                        inside = true;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            if (!inside)
            {
                if (plan.Stage == SwitchExtract.Stage.Timer)
                {
                    plan.Stage = SwitchExtract.Stage.ToExit;
                    BotOwner.Exfiltration?.ResetLeaveTime();
                }

                Move(Next(goal), sprint: (BotOwner.Position - goal).sqrMagnitude > 20f * 20f);
                return;
            }

            BotOwner.StopMove();
            if (exit.Direct && BackpackExit.Blocks(exit.Exit, BotOwner))
            {
                // The manhole takes nobody with a backpack: BackpackExit throws it off here, the timer starts after. Raid 2026-11-11 16:06: three bots stood at the manhole
                // for 2-4 minutes and the bag was never thrown (0 of 3) - a plan that waits for it more than 25 s is given up
                if (_bagSince < 0f)
                {
                    _bagSince = Time.time;
                }

                plan.StuckSince = Time.time;
                if (Time.time - _bagSince > 25f)
                {
                    Log.Info($"LR Extract: {name} has stood {Time.time - _bagSince:0} s at {exit.Name.Localized()} and its backpack is still on - gives the exit up");
                    _bagSince = -1f;
                    SwitchExtract.Drop(BotOwner, "the backpack could not be thrown off");
                }

                return;
            }

            _bagSince = -1f;

            if (plan.Stage != SwitchExtract.Stage.Timer)
            {
                plan.Stage = SwitchExtract.Stage.Timer;
                plan.TimerStart = Time.time;
                BotOwner.Exfiltration?.SetLeaveTime(Time.time + exit.Timer);
                Log.Detail("task", $"{name} stands in exit {exit.Name.Localized()} for its {exit.Timer:0} s timer");
            }

            if (Time.time - plan.TimerStart >= exit.Timer)
            {
                SwitchExtract.CountLeft();
                Log.Info($"LR Extract: {name} left the raid through switch exit {exit.Name.Localized()} after standing {Time.time - plan.TimerStart:0} s " +
                         $"(plan took {(Time.time - plan.StartedAt) / 60f:0.0} min)");
                SwitchExtract.Forget(BotOwner);
                SwitchExtract.LeavingNow = BotOwner;
                try
                {
                    BotOwner.LeaveData.RemoveFromMap();
                }
                finally
                {
                    SwitchExtract.LeavingNow = null;
                }
            }
        }

        private float _bagSince = -1f;
        private object _spotFor;
        private Vector3? _spot;
        private float _spotSince;
        // 1.4.15: the wait for the door was too short (a bot at D-2 walked through the leaf): 2.5 times the old time
        private const float GateWaitFactor = 2.5f;
        private float _gateSince = -1f;
        private bool _gateDone;
        private float _gateCap;
        private float _gateTurnAt;
        private float _gateScan;

        /// <summary>How long a door takes to move by its own animation curve (EFT plays it at speed 1: the last key of the curve), 0 when unknown.</summary>
        private static float GateSeconds(Door door)
        {
            try
            {
                AnimationCurve curve = door?.ProgressCurve;
                return curve != null && curve.length > 0 ? curve.keys[curve.length - 1].time : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// The exit is "open" the moment its door starts moving. A player who has pressed the button at D-2 stands and watches the room's entrance until the leaf is
        /// back, then walks in. Here: while a door of the exit is not yet open (and the bot is within 25 m of it), the bot stands, faces the way it came in and
        /// scans a little to either side; it goes in once the door is open (at most its animation + 2.5 s, 6-25 s). An exit with no door on record: 8 s.
        /// </summary>
        private bool GateWait(SwitchExtract.Plan plan, SwitchExtract.SwitchExit exit, string name)
        {
            if (exit.Direct || exit.IsTrain || _gateDone)
            {
                return false;
            }

            Door moving = exit.Gates.FirstOrDefault(d => d != null && d.DoorState != EDoorState.Open);
            Vector3 at = moving != null ? moving.transform.position : exit.Stand;
            bool noRecord = exit.Gates.Count == 0;
            if (moving == null && !noRecord)
            {
                if (_gateSince >= 0f)
                {
                    Log.Info($"LR Extract: {name} - the door of exit {exit.Name.Localized()} is open after {Time.time - _gateSince:0.0} s, goes in");
                    _gateSince = -1f;
                }

                return false;
            }

            if (_gateSince < 0f)
            {
                if (noRecord && Time.time - plan.StageSince > 30f)
                {
                    return false;
                }

                if ((BotOwner.Position - at).sqrMagnitude > 25f * 25f)
                {
                    return false;
                }

                _gateSince = Time.time;
                float seconds = noRecord ? 0f : GateSeconds(moving);
                _gateCap = GateWaitFactor * (noRecord ? 8f : Mathf.Clamp(seconds + 2.5f, 6f, 25f));
                Log.Info($"LR Extract: {name} stands guard facing the way it came while the door of exit {exit.Name.Localized()} opens " +
                         (noRecord ? $"(no door on record, {_gateCap:0} s)" : $"({moving.Id} {moving.DoorState}, its animation {seconds:0.0} s, waits at most {_gateCap:0} s)"));
            }

            if (Time.time - _gateSince >= _gateCap)
            {
                Log.Info($"LR Extract: {name} has guarded {Time.time - _gateSince:0} s{(noRecord ? "" : " and the door is still not open")} - goes in");
                _gateSince = -1f;
                _gateDone = true;
                return false;
            }

            plan.StuckSince = Time.time;
            BotOwner.StopMove();
            CoverBack(plan, at);
            return true;
        }

        /// <summary>
        /// Stands facing the way the bot came from (the oldest point of its trail that is 7+ m off, else the side opposite <paramref name="front"/>), the gaze
        /// turned 25-30 degrees to one side and the other every 3 s: its back is to the thing it waits on, its aim on the entrance.
        /// </summary>
        private void CoverBack(SwitchExtract.Plan plan, Vector3 front)
        {
            Vector3 pos = BotOwner.Position;
            Vector3 back = Vector3.zero;
            bool known = false;
            for (int i = plan.Trail.Count - 1; i >= 0; i--)
            {
                Vector3 d = plan.Trail[i] - pos;
                d.y = 0f;
                if (d.sqrMagnitude >= 7f * 7f)
                {
                    back = pos + d.normalized * 10f;
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                Vector3 away = pos - front;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f)
                {
                    away = -BotOwner.LookDirection;
                    away.y = 0f;
                }

                back = pos + away.normalized * 10f;
            }

            if (Time.time >= _gateScan)
            {
                _gateScan = Time.time + 3f;
                _gateTurnAt = (_gateTurnAt >= 0f ? -1f : 1f) * (25f + 5f * (Time.time % 2f));
            }

            Vector3 dir = back - pos;
            dir.y = 0f;
            dir = Quaternion.Euler(0f, _gateTurnAt, 0f) * dir;
            BotOwner.SetPose(1f);
            BotOwner.Steering.LookToPoint(pos + dir + Vector3.up * 1.4f);
        }

        private float _trainDeparting = -1f;

        /// <summary>
        /// The armoured train: a bot walks to the zone while the train stands (EFT lets it go "when it pulls out": TrainRequirement is met on Departing),
        /// boards (within 9 m of the zone or inside it), waits there for the departure - which may be minutes - and leaves 5 s after the train starts to go.
        /// </summary>
        private List<Vector3> _tWay;
        private int _tIdx;
        private Vector3 _tPos;
        private bool _tSettled;
        private float _tNearSince = -1f;
        private float _tGiveUp;
        private float _tLook;

        /// <summary>The bot stops being "inside" the carriage: the mover wakes up and puts it on the nearest mesh, the place is free for another.</summary>
        private void LeaveInterior()
        {
            if (_tWay == null)
            {
                return;
            }

            _tWay = null;
            _tSettled = false;
            RouteTraverseLayer.Traversing.Remove(BotOwner.ProfileId);
            LiveRaid.Client.Raid.TrainInterior.Release(BotOwner);
            if (NavMesh.SamplePosition(BotOwner.Position, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            {
                RouteTraverseLayer.PlaceOnNavmesh(BotOwner, hit.position);
            }
        }

        private bool BeginInterior(SwitchExtract.SwitchExit exit, string name)
        {
            int entry = LiveRaid.Client.Raid.TrainInterior.Nearest(BotOwner.Position);
            if ((LiveRaid.Client.Raid.TrainInterior.SlotPos(entry) - BotOwner.Position).magnitude > 12f)
            {
                return false;
            }

            int slot = LiveRaid.Client.Raid.TrainInterior.Claim(BotOwner, entry);
            if (slot < 0)
            {
                return false;
            }

            _tWay = new List<Vector3>();
            int dir = slot >= entry ? 1 : -1;
            for (int i = entry; i != slot + dir; i += dir)
            {
                _tWay.Add(LiveRaid.Client.Raid.TrainInterior.SlotPos(i));
            }

            _tIdx = 0;
            _tPos = BotOwner.Position;
            _tSettled = false;
            _tGiveUp = Time.time + 10f + 2.5f * _tWay.Count;
            RouteTraverseLayer.Traversing.Add(BotOwner.ProfileId);
            BotOwner.StopMove();
            Log.Info($"LR Extract: {name} steps into the train and goes to place {slot + 1} of {LiveRaid.Client.Raid.TrainInterior.Count} ({_tWay.Count} points along the carriage)");
            return true;
        }

        /// <summary>Walks the owner's line of footholds to its own place, then stands there (looks around) until the train goes.</summary>
        private void TrainInside(SwitchExtract.Plan plan, SwitchExtract.SwitchExit exit, string name)
        {
            plan.StuckSince = Time.time;
            if (!_tSettled)
            {
                Vector3 target = _tWay[_tIdx];
                BotOwner.SetPose(1f);
                BotOwner.Steering.LookToPoint(target + Vector3.up * 1.4f + (target - _tPos).normalized * 2f);
                _tPos = Vector3.MoveTowards(_tPos, target, 1.4f * Time.deltaTime);
                BotOwner.Mover.Teleport(_tPos);
                if ((_tPos - target).sqrMagnitude < 0.0001f)
                {
                    _tIdx++;
                }

                if (_tIdx < _tWay.Count && Time.time < _tGiveUp)
                {
                    return;
                }

                _tSettled = true;
                _tLook = 0f;
                plan.Stage = SwitchExtract.Stage.Timer;
                plan.TimerStart = Time.time;
                BotOwner.SetPose(UnityEngine.Random.value < 0.3f ? 0.55f : 1f);
                Log.Info($"LR Extract: {name} has boarded the train - stands inside at ({_tPos.x:0}, {_tPos.z:0}), waits for it to pull out");
                return;
            }

            BotOwner.StopMove();
            if (Time.time >= _tLook)
            {
                _tLook = Time.time + 4f + 5f * UnityEngine.Random.value;
                float a = UnityEngine.Random.value * 360f;
                BotOwner.Steering.LookToPoint(_tPos + Quaternion.Euler(0f, a, 0f) * Vector3.forward * 5f + Vector3.up * 1.4f);
            }

            TrainDepart(plan, exit, name);
        }

        private void TrainStep(SwitchExtract.Plan plan, SwitchExtract.SwitchExit exit, string name)
        {
            BoxCollider zoneBox = exit.Exit.GetComponent<BoxCollider>();
            Vector3 center = zoneBox != null ? zoneBox.bounds.center : exit.Exit.transform.position;
            bool interior = LiveRaid.Client.Raid.TrainInterior.Available(center);
            if (interior && _tWay != null)
            {
                TrainInside(plan, exit, name);
                return;
            }

            Vector3 goal = exit.Stand;
            float dGoal = (BotOwner.Position - goal).magnitude;
            bool near = InExitZone(exit, BotOwner.Position) || dGoal < 9f;
            if (interior && near)
            {
                // Steps in from the stand point (or from anywhere inside the zone); stuck short of it for 15 s - from where it is
                bool close = dGoal < 3f || InExitZone(exit, BotOwner.Position);
                if (!close)
                {
                    if (_tNearSince < 0f)
                    {
                        _tNearSince = Time.time;
                    }

                    close = Time.time - _tNearSince > 15f;
                }

                if (close && BeginInterior(exit, name))
                {
                    TrainInside(plan, exit, name);
                    return;
                }

                if (!close)
                {
                    near = false;
                }
            }
            else if (!near)
            {
                _tNearSince = -1f;
            }

            if (!near)
            {
                _trainDeparting = -1f;
                if (Reach(exit, goal))
                {
                    goal = _stand;
                }

                Move(Next(goal), sprint: (BotOwner.Position - goal).sqrMagnitude > 20f * 20f);
                return;
            }

            BotOwner.StopMove();
            plan.StuckSince = Time.time;
            BotOwner.Steering.LookToPoint(exit.Exit.transform.position);
            if (plan.Stage != SwitchExtract.Stage.Timer)
            {
                plan.Stage = SwitchExtract.Stage.Timer;
                plan.TimerStart = Time.time;
                Log.Info($"LR Extract: {name} has boarded the train ({(BotOwner.Position - exit.Exit.transform.position).magnitude:0} m from its zone) - waits for it to pull out");
            }

            TrainDepart(plan, exit, name);
        }

        /// <summary>Aboard: waits for the train to start (Countdown) and leaves the raid 5 s later.</summary>
        private void TrainDepart(SwitchExtract.Plan plan, SwitchExtract.SwitchExit exit, string name)
        {
            if (exit.Exit.Status != EExfiltrationStatus.Countdown)
            {
                _trainDeparting = -1f;
                return;
            }

            if (_trainDeparting < 0f)
            {
                _trainDeparting = Time.time;
            }

            if (Time.time - _trainDeparting >= exit.Timer)
            {
                SwitchExtract.CountLeft();
                Log.Info($"LR Extract: {name} left the raid on the train after waiting {Time.time - plan.TimerStart:0} s aboard (plan took {(Time.time - plan.StartedAt) / 60f:0.0} min)");
                SwitchExtract.Forget(BotOwner);
                SwitchExtract.LeavingNow = BotOwner;
                RouteTraverseLayer.Traversing.Remove(BotOwner.ProfileId);
                LiveRaid.Client.Raid.TrainInterior.Release(BotOwner);
                _tWay = null;
                try
                {
                    BotOwner.LeaveData.RemoveFromMap();
                }
                finally
                {
                    SwitchExtract.LeavingNow = null;
                }
            }
        }

        private void Move(Vector3 to, bool sprint)
        {
            if (Time.time < _nextMove)
            {
                return;
            }

            _nextMove = Time.time + 1f;
            BotOwner.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Sprint(sprint);
            BotOwner.GoToPoint(to, !sprint, -1f, false, false);
            BotOwner.Steering.LookToMovingDirection();
        }

        private static bool InExitZone(SwitchExtract.SwitchExit exit, Vector3 pos)
        {
            BoxCollider zone = exit.Exit.GetComponent<BoxCollider>();
            if (zone == null)
            {
                return false;
            }

            Vector3 p = pos + Vector3.up * 0.5f;
            return (zone.ClosestPoint(p) - p).sqrMagnitude < 0.01f;
        }
    }
}
