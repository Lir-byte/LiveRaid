using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Raid;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// BigBrain layer, priority 95 (above SAIN combat and threat avoidance, below SAIN's debug layer): when a bot's path goes through a
    /// LiveRaid route link, it takes the bot along the recorded footholds instead of letting it walk straight into the rubble.
    /// If no human player can see the bot it is simply moved to the far end. Active only for the few seconds of the climb.
    /// </summary>
    public class RouteTraverseLayer : CustomLayer
    {
        public const int LayerPriority = 95;

        private const float EndReach = 1.4f;
        private static readonly Dictionary<string, float> _cooldown = new Dictionary<string, float>();

        // Loop guard: the same bot started the same climb again and again when it did not get up (raid 2026-10-19: 3 tries a minute)
        private static readonly Dictionary<string, List<float>> _starts = new Dictionary<string, List<float>>();

        private float _nextCheck;
        private readonly NavMeshPath _path = new NavMeshPath();

        internal List<Vector3> Way;
        internal List<RoutePoint> WayPoints;
        internal Route Route;
        internal float GiveUpAt;

        // Where the bot should stand after a climb, checked once a moment later
        internal Vector3? ExpectAt;
        internal Vector3 ExpectFrom;
        internal float ExpectCheckAt;
        private int _failedArrivals;

        public RouteTraverseLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            Registry[botOwner.ProfileId] = this;
        }

        internal static readonly Dictionary<string, RouteTraverseLayer> Registry = new Dictionary<string, RouteTraverseLayer>();

        // Bots being moved along a route right now: EFT's own mover must not pull them back onto the navmesh meanwhile
        internal static readonly HashSet<string> Traversing = new HashSet<string>();

        public static void Clear()
        {
            _cooldown.Clear();
            _starts.Clear();
            Registry.Clear();
            Traversing.Clear();
        }

        public override string GetName() => "LiveRaid Route";

        /// <summary>
        /// Puts the bot at a route end so that EFT's mover accepts it there. <c>BotMover.Teleport</c> alone keeps the mover's last
        /// navmesh link at the old end, and for a second after a good link <c>SetPlayerToNavMesh</c> casts with the old height
        /// (<c>_lastGoodCastPoint.y</c>): on top of the cliff that cast fails and <c>FindBetterPosition</c> teleports the bot back down
        /// next to the previous link (raid 2026-10-19: "did not stay at the end, 4.6 m off" every time).
        /// </summary>
        internal static void PlaceOnNavmesh(BotOwner bot, Vector3 end)
        {
            Vector3 at = NavMesh.SamplePosition(end, out NavMeshHit hit, 1f, NavMesh.AllAreas) ? hit.position : end;
            BotMover mover = bot.Mover;
            mover.Teleport(at);
            mover._prevSuccessLinkedFrom = at;
            mover._prevLinkPos = at;
            mover._lastGoodCastPoint = at;
            mover._lastGoodCastPointTime = Time.time;
            mover._prevPosLinkedTime = Time.time;
            mover._linkedToNavmeshInitially = true;
        }

        public override bool IsActive()
        {
            try
            {
                return CheckActive();
            }
            catch (System.Exception e)
            {
                Diag.Report("RouteTraverseLayer", e);
                if (Way != null)
                {
                    Done();
                }

                return false;
            }
        }

        private bool CheckActive()
        {
            if (Way != null)
            {
                return true;
            }

            if (ExpectAt.HasValue && Time.time > ExpectCheckAt)
            {
                // Failed = back down at the other end's level. Walking on from the end is fine (the old 2 m test flagged every
                // bot that simply went on: "4.0 m off" while it was already up on the cliff)
                Vector3 now = BotOwner.Position;
                float off = Vector3.Distance(now, ExpectAt.Value);
                float levelGap = Mathf.Abs(ExpectFrom.y - ExpectAt.Value.y);
                bool failed = levelGap > 1.2f
                    ? Mathf.Abs(now.y - ExpectFrom.y) < Mathf.Abs(now.y - ExpectAt.Value.y) - 0.5f
                    : (now - ExpectFrom).sqrMagnitude < 1.5f * 1.5f;
                ExpectAt = null;
                if (failed)
                {
                    // A long ban left the bot standing at the start of the link forever: its path still crosses it and the mover
                    // cannot walk a link (raid 2026-10-19 01:34). Short pauses only, the next try is relinked again
                    _failedArrivals++;
                    float pause = _failedArrivals >= 2 ? 60f : 15f;
                    Log.Warn($"LR Route: {BotOwner.Profile.Nickname} did not stay at the end of the route ({off:0.0} m off) - tries again in {pause:0} s");
                    _cooldown[BotOwner.ProfileId] = Time.time + pause;
                    if (_failedArrivals >= 2)
                    {
                        _failedArrivals = 0;
                    }
                }
                else
                {
                    _failedArrivals = 0;
                }
            }

            if (!Settings.Routes.Value || Routes.Active.Count == 0 || BotOwner.BotState != EBotState.Active || Time.time < _nextCheck)
            {
                return false;
            }

            _nextCheck = Time.time + 0.3f;
            if (_cooldown.TryGetValue(BotOwner.ProfileId, out float until) && Time.time < until)
            {
                return false;
            }

            // Never in a fight: at priority 95 the climb takes the bot away from SAIN (a PMC stood 15 s "climbing" next to the player)
            if (!Tactics.TacticalTasks.NoThreat(BotOwner))
            {
                return false;
            }

            Vector3 pos = BotOwner.Position;
            foreach (Route route in Routes.Active)
            {
                if (!route.Linked)
                {
                    continue;
                }

                bool atStart = (route.Start - pos).sqrMagnitude < EndReach * EndReach;
                bool atEnd = !atStart && (route.End - pos).sqrMagnitude < EndReach * EndReach;
                if (!atStart && !atEnd)
                {
                    continue;
                }

                Vector3 far = atStart ? route.End : route.Start;
                if (!PathGoesThrough(far))
                {
                    continue;
                }

                Begin(route, atStart);
                return Way != null;
            }

            return false;
        }

        /// <summary>Does the bot's current way lead across the link (the next corners include the far end)?</summary>
        private bool PathGoesThrough(Vector3 far)
        {
            BotMover mover = BotOwner.Mover;
            if (mover == null)
            {
                return false;
            }

            if ((mover.CurrentCornerPoint - far).sqrMagnitude < 0.8f * 0.8f)
            {
                return true;
            }

            Vector3 dest = mover.RealDestPoint;
            if ((dest - BotOwner.Position).sqrMagnitude < 2f * 2f)
            {
                return false;
            }

            if (!NavMesh.CalculatePath(BotOwner.Position, dest, NavMesh.AllAreas, _path) || _path.corners.Length < 2)
            {
                return false;
            }

            for (int i = 1; i < Mathf.Min(4, _path.corners.Length); i++)
            {
                if ((_path.corners[i] - far).sqrMagnitude < 0.8f * 0.8f)
                {
                    return true;
                }
            }

            return false;
        }

        private void Begin(Route route, bool forward)
        {
            string key = BotOwner.ProfileId + route.Id;
            if (!_starts.TryGetValue(key, out List<float> starts))
            {
                _starts[key] = starts = new List<float>();
            }

            starts.RemoveAll(t => Time.time - t > 120f);
            starts.Add(Time.time);
            if (starts.Count >= 3)
            {
                Log.Warn($"LR Route: {BotOwner.Profile.Nickname} keeps coming back to route {route.Id} ({starts.Count} times in 2 min) - leaves it alone for 3 min");
                _cooldown[BotOwner.ProfileId] = Time.time + 180f;
                starts.Clear();
                return;
            }

            Route = route;
            WayPoints = new List<RoutePoint>(route.Points);
            if (!forward)
            {
                WayPoints.Reverse();
            }

            Way = new List<Vector3>();
            foreach (RoutePoint p in WayPoints)
            {
                Way.Add(p.Pos);
            }

            Way[Way.Count - 1] = forward ? route.End : route.Start;
            GiveUpAt = Time.time + 6f + 1.5f * Way.Count;
            Traversing.Add(BotOwner.ProfileId);
            Log.Info($"LR Route: {BotOwner.Profile.Nickname} takes route {route.Id} {(forward ? "up" : "back")} ({Way.Count} points)");
        }

        internal void Done(float cooldown = 8f)
        {
            Traversing.Remove(BotOwner.ProfileId);
            Way = null;
            WayPoints = null;
            Route = null;
            _cooldown[BotOwner.ProfileId] = Time.time + cooldown;
        }

        public override Action GetNextAction() => new Action(typeof(RouteTraverseLogic), "route");

        public override bool IsCurrentActionEnding() => Way == null;

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            BotOwner.StopMove();
            base.Start();
        }

        public override void Stop()
        {
            if (Way != null)
            {
                // Taken over mid-route (should not happen at this priority): do not leave the bot hanging on a railing
                PlaceOnNavmesh(BotOwner, Way[Way.Count - 1]);
                Done();
            }

            BotOwner.PatrollingData.Unpause();
            BotOwner.SetPose(1f);
            base.Stop();
        }
    }

    /// <summary>While a bot is moved along a route its mover sleeps; otherwise it would snap the bot back to the nearest navmesh.</summary>
    public class BotMoverTraversePatch : ModulePatch
    {
        private static readonly FieldInfo _player = AccessTools.Field(typeof(BotMover), "_player");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), nameof(BotMover.ManualUpdate));

        [PatchPrefix]
        protected static bool PatchPrefix(BotMover __instance)
        {
            try
            {
                return RunPrefix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("BotMoverTraversePatch", e);
                return true;
            }
        }

        private static bool RunPrefix(BotMover __instance)
        {
            if (RouteTraverseLayer.Traversing.Count == 0)
            {
                return true;
            }

            Player player = _player?.GetValue(__instance) as Player;
            return player == null || !RouteTraverseLayer.Traversing.Contains(player.ProfileId);
        }
    }

    public class RouteTraverseLogic : CustomLogic
    {
        private const float Speed = 1.4f;
        private int _index;
        private bool _decided;
        private Vector3 _pos;

        public RouteTraverseLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        // BigBrain reuses the logic object: every climb starts from scratch
        // (a stale "already decided" made every second try walk instead of being moved while nobody watched)
        public override void Start()
        {
            _index = 0;
            _decided = false;
            _pos = BotOwner.Position;
            base.Start();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Move();
            }
            catch (System.Exception e)
            {
                Diag.Report("RouteTraverseLogic", e);
                if (RouteTraverseLayer.Registry.TryGetValue(BotOwner.ProfileId, out RouteTraverseLayer layer) && layer.Way != null)
                {
                    Arrive(layer, layer.Way[layer.Way.Count - 1], "error");
                }
            }
        }

        private void Move()
        {
            if (!RouteTraverseLayer.Registry.TryGetValue(BotOwner.ProfileId, out RouteTraverseLayer layer) || layer.Way == null)
            {
                return;
            }

            List<Vector3> way = layer.Way;
            Vector3 end = way[way.Count - 1];

            // Nobody watching: no need to act the climb out
            if (!_decided)
            {
                _decided = true;
                _pos = BotOwner.Position;
                if (Settings.RouteTeleportUnseen.Value && !SeenByPlayer())
                {
                    Arrive(layer, end, "unseen");
                    return;
                }
            }

            // Shot at or an enemy showed up mid-climb: finish at the nearer end and hand the bot back to SAIN
            if (!Tactics.TacticalTasks.NoThreat(BotOwner))
            {
                Vector3 start = way[0];
                Arrive(layer, (_pos - start).sqrMagnitude < (_pos - end).sqrMagnitude ? start : end, "fight");
                return;
            }

            if (Time.time > layer.GiveUpAt || _index >= way.Count)
            {
                Arrive(layer, end, _index >= way.Count ? "walked" : "timed out");
                return;
            }

            Vector3 target = way[_index];
            RoutePoint point = layer.WayPoints[_index];
            BotOwner.SetPose(point.Pose);
            BotOwner.Steering.LookToPoint(target + Vector3.up * 1.4f + (target - _pos).normalized * 2f);

            // The bot follows a point we move ourselves: its body (gravity, collisions) never lands exactly on a foothold,
            // and waiting for it to do so kept the bot on the first step until the timeout
            _pos = Vector3.MoveTowards(_pos, target, Speed * Time.deltaTime);

            // Through the mover, which allows the jump: a bare Player.Teleport was undone by EFT's off-navmesh snap-back
            BotOwner.Mover.Teleport(_pos);
            if ((_pos - target).sqrMagnitude < 0.0001f)
            {
                _index++;
            }
        }

        private void Arrive(RouteTraverseLayer layer, Vector3 end, string how)
        {
            RouteTraverseLayer.Traversing.Remove(BotOwner.ProfileId);
            RouteTraverseLayer.PlaceOnNavmesh(BotOwner, end);
            BotOwner.SetPose(1f);
            Log.Debug($"{BotOwner.Profile.Nickname} finished route {layer.Route?.Id} ({how})");
            // Checked only when the bot went across (a fight can stop it at the end it started from)
            layer.ExpectAt = (end - layer.Way[0]).sqrMagnitude > 0.01f ? end : (Vector3?)null;
            layer.ExpectFrom = layer.Way[0];
            layer.ExpectCheckAt = Time.time + 1.5f;
            layer.Done(how == "fight" ? 30f : 8f);
        }

        /// <summary>Could the human watching the raid see this bot right now (you, or the camera after your death)?</summary>
        private bool SeenByPlayer() => ViewerSight.CanSee(BotOwner.Position + Vector3.up * 1.5f);
    }
}
