using System;
using System.Collections.Generic;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using LiveRaid.Client.Tactics;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// "Step back and find another way". When the door/nudge help of <see cref="MoveWatch"/> does not free a bot (it is stuck again within 90 s near the
    /// same place), the bot takes a few steps back along its own track and then a detour to the side, and only then carries on - the orders it had
    /// (SAIN's search, a flank, an errand) plan their way afresh from there. A person who walks into a corner does the same: backs out, tries another side.
    ///
    /// Priority 29: above every other layer of ours and SAIN's (not above grenade dodge 80 and the route crossing 95). Short: back 3-5 m (about 3 s), detour
    /// 6-10 m to the side (about 5 s), always cancelled by an enemy in sight or a hit. F12 -> 2 "Back off and re-route when stuck again".
    /// </summary>
    public class BackOffLayer : CustomLayer
    {
        public const int LayerPriority = 29;

        internal class Order
        {
            public Vector3 Stuck;
            public Vector3 Back;
            public Vector3? Detour;
            public Vector3? Goal;
            public string Intent;
            public int Stage;
            public float StageAt;
            public float Until;
            public int Level;
        }

        private static readonly Dictionary<string, Order> _orders = new Dictionary<string, Order>();
        // The way a bot came: a crumb for every metre it walked (raid 15:59: a track of the last ten seconds was all one spot for The_Antman, who had
        // stood there for a minute - there was no "back" to step to)
        private static readonly Dictionary<string, Queue<Vector3>> _trails = new Dictionary<string, Queue<Vector3>>();
        private static readonly Dictionary<string, Vector3> _lastCrumb = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, (Vector3 pos, float at, int count)> _history = new Dictionary<string, (Vector3, float, int)>();

        private static readonly Dictionary<string, float> _last = new Dictionary<string, float>();

        public static int Started { get; private set; }
        public static int Detours { get; private set; }
        public static int Failed { get; private set; }

        public static string Summary => $"backed off {Started} times ({Detours} with a detour, {Failed} found no way)";

        public static void Clear()
        {
            _orders.Clear();
            _trails.Clear();
            _lastCrumb.Clear();
            _history.Clear();
            _last.Clear();
            Started = Detours = Failed = 0;
        }

        /// <summary>Called by <see cref="MoveWatch"/> every half second for each bot: the track, one crumb per metre walked (about the last 24 m).</summary>
        internal static void Record(BotOwner bot)
        {
            if (!_trails.TryGetValue(bot.ProfileId, out Queue<Vector3> q))
            {
                _trails[bot.ProfileId] = q = new Queue<Vector3>();
            }

            Vector3 pos = bot.Position;
            if (_lastCrumb.TryGetValue(bot.ProfileId, out Vector3 last) && (pos - last).sqrMagnitude < 1f)
            {
                return;
            }

            // A bot jerked to and fro on one spot lays no crumbs: they would push the way it came out of the track
            foreach (Vector3 c in q)
            {
                if ((pos - c).sqrMagnitude < 1f)
                {
                    return;
                }
            }

            _lastCrumb[bot.ProfileId] = pos;
            q.Enqueue(pos);
            while (q.Count > 24)
            {
                q.Dequeue();
            }
        }

        public static bool Has(BotOwner bot) => bot != null && _orders.ContainsKey(bot.ProfileId);

        /// <summary>Starts the back-off for a stuck bot. False when it cannot (switched off, no place to step back to).</summary>
        internal static bool Begin(BotOwner bot, Vector3? goal, string intent)
        {
            if (!Settings.BackOffWhenStuck.Value || bot == null || _orders.ContainsKey(bot.ProfileId))
            {
                return false;
            }

            Vector3 pos = bot.Position;
            float now = Time.time;
            // Not in a fight (a bot pinned down under fire stands because it is fighting, not because it is stuck), and not more often than every 25 s per bot:
            // raid 20:11 had 117 back-offs in 22 minutes, most of them break-contact / retreat / assist bots
            if (Tactics.HitReaction.InRealFight(bot, 8f) || (_last.TryGetValue(bot.ProfileId, out float lastAt) && now - lastAt < 25f))
            {
                return false;
            }

            // How often has it been here? The second time near the same place the detour goes further
            int level = 1;
            if (_history.TryGetValue(bot.ProfileId, out var h) && now - h.at < 120f && (h.pos - pos).sqrMagnitude < 8f * 8f)
            {
                level = h.count + 1;
                if (level > 3)
                {
                    // Three back-offs here did not help: the move is given up (MoveWatch)
                    _history.Remove(bot.ProfileId);
                    return false;
                }
            }

            Vector3? back = BackPoint(bot, pos, goal, level);
            if (back == null)
            {
                Failed++;
                return false;
            }

            _history[bot.ProfileId] = (pos, now, level);
            _last[bot.ProfileId] = now;
            Started++;
            _orders[bot.ProfileId] = new Order
            {
                Stuck = pos,
                Back = back.Value,
                Goal = goal,
                Intent = intent,
                Stage = 0,
                StageAt = now,
                Until = now + 14f,
                Level = level,
            };
            Log.Info($"LR Stuck: {bot.Profile.Nickname} ({intent}) backs off {Vector3.Distance(pos, back.Value):0} m and looks for another way (time {level} at this place)");
            return true;
        }

        private static Vector3? BackPoint(BotOwner bot, Vector3 pos, Vector3? goal, int level)
        {
            float need = 3.5f + 1.5f * (level - 1);
            // The way it came
            if (_trails.TryGetValue(bot.ProfileId, out Queue<Vector3> q))
            {
                Vector3? pick = null;
                foreach (Vector3 p in q)
                {
                    Vector3 d = p - pos;
                    d.y = 0f;
                    float flat = d.magnitude;
                    if (flat >= need && flat <= need + 6f && Mathf.Abs(p.y - pos.y) < 1.2f)
                    {
                        pick = p;
                    }
                }

                if (pick.HasValue && NavMesh.SamplePosition(pick.Value, out NavMeshHit onTrail, 1.2f, NavMesh.AllAreas))
                {
                    return onTrail.position;
                }
            }

            // It stood where it is: away from where it wanted to go (or from where it looks)
            Vector3 away = goal.HasValue ? pos - goal.Value : -bot.LookDirection;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = -bot.LookDirection;
                away.y = 0f;
            }

            away.Normalize();
            for (int i = 0; i < 5; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i == 0 ? 0f : (i % 2 == 1 ? 1 : -1) * 35f * ((i + 1) / 2), 0f) * away;
                if (NavMesh.SamplePosition(pos + dir * need, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - pos.y) < 1.2f
                    && (hit.position - pos).sqrMagnitude > 2f * 2f)
                {
                    return hit.position;
                }
            }

            return null;
        }

        /// <summary>A way around: a point 8-12 m to one side of the line the bot was trying to go along, reachable on foot from the point it backed to.</summary>
        private static Vector3? DetourPoint(Order o)
        {
            Vector3 toward = (o.Goal ?? (o.Stuck + (o.Stuck - o.Back))) - o.Back;
            toward.y = 0f;
            if (toward.sqrMagnitude < 0.01f)
            {
                return null;
            }

            toward.Normalize();
            float reach = 7f + 2.5f * o.Level;
            Vector3? best = null;
            float bestLen = float.MaxValue;
            NavMeshPath path = new NavMeshPath();
            float[] angles = { 70f, -70f, 105f, -105f, 40f, -40f };
            // A random side first, so two bots stuck in the same spot do not pick the same way
            int shift = UnityEngine.Random.Range(0, 2);
            for (int i = 0; i < angles.Length; i++)
            {
                float a = angles[(i + shift) % angles.Length];
                Vector3 want = o.Back + Quaternion.Euler(0f, a, 0f) * toward * reach;
                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - o.Back.y) > 1.5f)
                {
                    continue;
                }

                // Not back into the spot it was stuck in
                if ((hit.position - o.Stuck).sqrMagnitude < 3f * 3f)
                {
                    continue;
                }

                if (!NavMesh.CalculatePath(o.Back, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0f;
                for (int c = 1; c < path.corners.Length; c++)
                {
                    len += Vector3.Distance(path.corners[c - 1], path.corners[c]);
                }

                if (len < 4f || len > reach * 2.2f + 6f)
                {
                    continue;
                }

                // The path must not run through the stuck spot itself
                bool throughStuck = false;
                for (int c = 0; c < path.corners.Length; c++)
                {
                    if ((path.corners[c] - o.Stuck).sqrMagnitude < 1.5f * 1.5f)
                    {
                        throughStuck = true;
                        break;
                    }
                }

                if (throughStuck)
                {
                    continue;
                }

                if (len < bestLen)
                {
                    bestLen = len;
                    best = hit.position;
                }
            }

            return best;
        }

        public BackOffLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Back Off";

        public override void Start()
        {
            Tactics.SainHandover.Take(BotOwner);
            base.Start();
        }

        public override bool IsActive()
        {
            try
            {
                if (!_orders.TryGetValue(BotOwner.ProfileId, out Order o))
                {
                    return false;
                }

                if (!Settings.BackOffWhenStuck.Value || BotOwner.IsDead || Time.time > o.Until || BotOwner.Medecine?.Using == true
                    || TacticalTasks.HasContact(BotOwner) || Spectator.TakeOver.IsTaken(BotOwner))
                {
                    Release(o, "done");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Diag.Report("BackOffLayer", e);
                _orders.Remove(BotOwner.ProfileId);
                return false;
            }
        }

        internal static void Release(Order o, string why)
        {
            foreach (KeyValuePair<string, Order> kv in new List<KeyValuePair<string, Order>>(_orders))
            {
                if (kv.Value == o)
                {
                    _orders.Remove(kv.Key);
                    Log.Detail("task", $"back-off of {kv.Key} over: {why}");
                }
            }
        }

        internal static Order Get(BotOwner bot) => bot != null && _orders.TryGetValue(bot.ProfileId, out Order o) ? o : null;

        internal static void Advance(BotOwner bot, Order o)
        {
            if (o.Stage == 0)
            {
                o.Detour = DetourPoint(o);
                o.Stage = 1;
                o.StageAt = Time.time;
                if (o.Detour.HasValue)
                {
                    Detours++;
                }
                else
                {
                    Release(o, "no detour found, carries on from the back point");
                }

                return;
            }

            Release(o, "detour walked");
        }

        public override Action GetNextAction() => new Action(typeof(BackOffLogic), "back off");

        public override bool IsCurrentActionEnding() => !_orders.ContainsKey(BotOwner.ProfileId);

        public override void BuildDebugText(StringBuilder sb)
        {
            Order o = Get(BotOwner);
            if (o != null)
            {
                sb.AppendLine($"LiveRaid: backing off (stage {o.Stage})");
            }
        }
    }

    public class BackOffLogic : CustomLogic
    {
        private float _nextOrder;

        public BackOffLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Stop()
        {
            BotOwner.Sprint(false);
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                BackOffLayer.Order o = BackOffLayer.Get(BotOwner);
                if (o == null)
                {
                    return;
                }

                Vector3? target = o.Stage == 0 ? o.Back : o.Detour;
                if (!target.HasValue)
                {
                    BackOffLayer.Release(o, "no target");
                    return;
                }

                Vector3 pos = BotOwner.Position;
                float limit = o.Stage == 0 ? 4f : 7f;
                if ((pos - target.Value).sqrMagnitude < 1.2f * 1.2f || Time.time - o.StageAt > limit)
                {
                    BackOffLayer.Advance(BotOwner, o);
                    _nextOrder = 0f;
                    return;
                }

                BotOwner.SetPose(1f);
                BotOwner.SetTargetMoveSpeed(1f);
                BotOwner.Sprint(false);
                BotOwner.Steering.LookToMovingDirection();
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 0.8f;
                    Tactics.SainHandover.Take(BotOwner);
                    BotOwner.GoToPoint(target.Value, false, -1f, false, false);
                }
            }
            catch (Exception e)
            {
                Diag.Report("BackOffLogic", e);
            }
        }
    }
}
