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
            public Vector3 Stand;
            public string Name => Exit.Settings.Name;
            public float Timer => Exit.Settings.ExfiltrationTime;

            public bool Open => Exit.Status == EExfiltrationStatus.RegularMode || Exit.Status == EExfiltrationStatus.Countdown;

            public bool Present => Exit.Status != EExfiltrationStatus.NotPresent;

            /// <summary>The next switch a bot has to pull (null: all on).</summary>
            public Switch NextSwitch => Chain.FirstOrDefault(s => s != null && s.DoorState != EDoorState.Open);
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
            ExfiltrationPoint[] pmcExits = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (pmcExits == null)
            {
                return _exits;
            }

            // The location's own list of interactive objects (what the game syncs them by): a scene-wide FindObjectsOfType cost ~90 ms in one frame
            Switch[] switches = LocationScene.GetAllObjects<WorldInteractiveObject>().OfType<Switch>().ToArray();
            HashSet<Switch> automatic = new HashSet<Switch>(switches.Where(s => s.NextSwitches != null).SelectMany(s => s.NextSwitches)
                                                                 .Where(n => n?.Switch != null).Select(n => n.Switch));
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

                exit = new SwitchExit { Exit = last.ExfiltrationPoint };
                List<Switch> chain = new List<Switch>();
                for (Switch s = last; s != null && !chain.Contains(s) && chain.Count < 8; s = s.PreviousSwitch)
                {
                    chain.Add(s);
                }

                chain.Reverse();

                // Switches another switch turns on by itself are not pulled by hand
                exit.Chain = chain.Where(s => !automatic.Contains(s)).ToList();
                AIExfiltrationPoint ai = aiPoints?.FirstOrDefault(p => p.Name == exit.Name);
                exit.Stand = ai != null ? ai.Position : StandPoint(exit.Exit);
                _exits.Add(exit);
            }

            if (_exits.Count > 0)
            {
                Log.Info($"LR Extract: switch exits on {location}: " +
                         string.Join(", ", _exits.Select(e => $"{e.Name.Localized()} ({e.Chain.Count} switch{(e.Chain.Count == 1 ? "" : "es")}, {e.Timer:0} s, {e.Exit.Status})")));
            }

            return _exits;
        }

        private static Vector3 StandPoint(ExfiltrationPoint exit)
        {
            BoxCollider zone = exit.GetComponent<BoxCollider>();
            Vector3 center = zone != null ? zone.bounds.center : exit.transform.position;
            return NavMesh.SamplePosition(center, out NavMeshHit hit, 4f, NavMesh.AllAreas) ? hit.position : center;
        }

        private static Vector3 SwitchStand(Switch s) =>
            NavMesh.SamplePosition(s.transform.position, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ? hit.position : s.transform.position;

        /// <summary>
        /// A switch exit this bot could take now, and how far its whole way is (switches first, then the exit). Null: none usable
        /// (absent this raid, no path, or a chain switch that is locked for good).
        /// </summary>
        public static SwitchExit Choose(BotOwner bot, out float way)
        {
            way = float.MaxValue;
            SwitchExit best = null;
            NavMeshPath path = new NavMeshPath();
            foreach (SwitchExit exit in Exits())
            {
                if (!exit.Present || exit.Chain.Count == 0)
                {
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
                        float leg = PathLength(from, at, path);
                        if (leg < 0f)
                        {
                            ok = false;
                            break;
                        }

                        total += leg;
                        from = at;
                    }
                }

                float last = ok ? PathLength(from, exit.Stand, path) : -1f;
                if (!ok || last < 0f)
                {
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

        private static float PathLength(Vector3 a, Vector3 b, NavMeshPath path)
        {
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                return -1f;
            }

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            return length;
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

                return !Tactics.HitReaction.InRealFight(BotOwner, 10f) && BotOwner.Medecine?.Using != true;
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

        public SwitchExtractLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            _pulledAt = -1f;
            base.Start();
        }

        public override void Stop()
        {
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

        private void Step(SwitchExtract.Plan plan)
        {
            SwitchExtract.SwitchExit exit = plan.Exit;
            string name = BotOwner.Profile.Nickname;

            // Stuck on the way for long: give up the plan
            if ((BotOwner.Position - plan.LastPos).sqrMagnitude > 1f)
            {
                plan.LastPos = BotOwner.Position;
                plan.StuckSince = Time.time;
            }
            else if (plan.Stage != SwitchExtract.Stage.Timer && Time.time - plan.StuckSince > 40f)
            {
                SwitchExtract.Drop(BotOwner, "stuck on the way");
                return;
            }

            if (plan.Stage == SwitchExtract.Stage.ToSwitch)
            {
                if (exit.Open || exit.NextSwitch == null)
                {
                    if (!exit.Open && Time.time - plan.StageSince < 25f)
                    {
                        // All switches on, the exit opens after the switch's delay: wait by the last switch
                        BotOwner.StopMove();
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
                if (target.DoorState == EDoorState.Locked)
                {
                    SwitchExtract.Drop(BotOwner, $"switch {target.Id} is locked");
                    return;
                }

                Vector3 at = NavMesh.SamplePosition(target.transform.position, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ? hit.position : target.transform.position;
                if ((BotOwner.Position - at).sqrMagnitude > 1.3f * 1.3f)
                {
                    Move(at, sprint: (BotOwner.Position - at).sqrMagnitude > 25f * 25f);
                    return;
                }

                BotOwner.StopMove();
                BotOwner.Steering.LookToPoint(target.transform.position);
                if (target.DoorState == EDoorState.Interacting)
                {
                    return;
                }

                if (_pulledAt < 0f || Time.time - _pulledAt > 4f)
                {
                    _pulledAt = Time.time;
                    InteractionResult result = target.GetInteractionResult(EInteractionType.Open, BotOwner);
                    BotOwner.InteractWithWorldInteractiveObject(target, result);
                    SwitchExtract.CountPulled();
                    Log.Info($"LR Extract: {name} pulls switch {target.Id} for exit {exit.Name.Localized()}");
                }

                return;
            }

            if (!exit.Open)
            {
                // Closed again (a timed exit) before we got there: switches again, if they can be pulled
                plan.Stage = SwitchExtract.Stage.ToSwitch;
                plan.StageSince = Time.time;
                BotOwner.Exfiltration?.ResetLeaveTime();
                return;
            }

            bool inside = InExitZone(exit, BotOwner.Position) || (BotOwner.Position - exit.Stand).sqrMagnitude < 1.5f * 1.5f;
            if (!inside)
            {
                if (plan.Stage == SwitchExtract.Stage.Timer)
                {
                    plan.Stage = SwitchExtract.Stage.ToExit;
                    BotOwner.Exfiltration?.ResetLeaveTime();
                }

                Move(exit.Stand, sprint: (BotOwner.Position - exit.Stand).sqrMagnitude > 20f * 20f);
                return;
            }

            BotOwner.StopMove();
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
