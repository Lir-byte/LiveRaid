using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using QuestingBots.BotLogic.BotMonitor;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The last word on bots that stand about doing nothing (F12 -> 3 "Never stand idle").
    /// Each cause found so far had its own fix (QuestingBots' loot breaks, "stop to heal" without meds, a follower without its leader, a scan that never
    /// starts, ...) and still bots were found standing for minutes for a reason nobody had seen yet (raid 2026-10-22 15:57: 13 min ScottieKnowz,
    /// 6 min Трофим Минский, 2 min iDkGK). This watches the outcome instead of the causes: a bot that has not moved 2 m for 40 s in one of the layers
    /// that do nothing by themselves (QuestingBots' recovery, EFT's patrol idle, LootingBots with no loot in hand, the follower layer with no leader near)
    /// and has no good reason to stand (treating a wound, looting, a task, a room visit, an exit timer):
    ///  - at 40 s QuestingBots is told to go on: its quest, or a new one (a follower: back to its leader, or its own way if it has none);
    ///  - at 75 s it is sent somewhere else on the map, as if it had decided to move on.
    /// Every step is logged ("LR Idle:"), with the layer and QuestingBots' decision, so the next cause is seen at once.
    /// </summary>
    public static class IdleBreaker
    {
        private const float NudgeAfter = 40f;
        private const float MoveAfter = 75f;

        private class State
        {
            public Vector3 At;
            public float Since;
            public bool Nudged;
            public float GoOnUntil;
            // A patrol scav's look-about before it walks on (10-15 s)
            public float PatrolAfter = 12f;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;

        public static int Nudges { get; private set; }
        public static int Moves { get; private set; }

        public static int PatrolWalks { get; private set; }

        public static string Summary => $"idle bots told to go on {Nudges}, sent elsewhere {Moves} (of them patrol scavs walking their beat {PatrolWalks})";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Nudges = Moves = PatrolWalks = 0;
        }

        /// <summary>QbDecisionPatch: the decision QuestingBots must take now for an idle bot, or null.</summary>
        public static EBotQuestingDecision? Override(BotOwner bot, BotQuestingDecisionMonitor monitor)
        {
            if (bot == null || !_states.TryGetValue(bot.ProfileId, out State s) || Time.time >= s.GoOnUntil)
            {
                return null;
            }

            switch (monitor.CurrentDecision)
            {
                case EBotQuestingDecision.Quest:
                case EBotQuestingDecision.FollowBoss:
                case EBotQuestingDecision.Fight:
                case EBotQuestingDecision.HelpBoss:
                case EBotQuestingDecision.Hunt:
                    return null;
            }

            BotOwner leader = TacticalTasks.SquadLeaderOf(bot);
            if (leader != null && leader != bot && !leader.IsDead && (leader.Position - bot.Position).sqrMagnitude > 12f * 12f)
            {
                return EBotQuestingDecision.FollowBoss;
            }

            return bot.GetObjectiveManager()?.IsJobAssignmentActive == true ? EBotQuestingDecision.Quest : EBotQuestingDecision.WaitForAssignment;
        }

        public static void Tick()
        {
            if (!Settings.NeverIdle.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                Diag.Guard("IdleBreaker", () => Check(bot, p, now));
            }
        }

        private static void Check(BotOwner bot, BotProfile p, float now)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { At = bot.Position, Since = now };
            }

            Vector3 pos = bot.Position;
            string layer = DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) ?? "-";
            if ((pos - s.At).sqrMagnitude > 2f * 2f || !IdleLayer(bot, layer) || GoodReason(bot))
            {
                s.At = pos;
                s.Since = now;
                s.Nudged = false;
                s.PatrolAfter = UnityEngine.Random.Range(10f, 15f);
                return;
            }

            float idle = now - s.Since;
            string qb = bot.GetObjectiveManager()?.BotMonitor?.GetMonitor<BotQuestingDecisionMonitor>()?.CurrentDecision.ToString() ?? "-";

            // A patrol scav (its group's leader or alone) that QuestingBots has nothing for in its area: raid 2026-10-31 15:59, Дэнчик Жислый and
            // Эдик Тиктокер stood 65-105 s in QuestingBots' idle layer again and again, until the 75 s step sent them on. A sentry looks about for a few
            // seconds and walks on to another part of his beat
            BotOwner lead = TacticalTasks.SquadLeaderOf(bot);
            bool patrol = Settings.ScavPatrol.Value && idle >= s.PatrolAfter && qb == nameof(EBotQuestingDecision.WaitForAssignment)
                          && (lead == null || lead == bot || lead.IsDead) && ScavPatrol.Applies(bot, out _);
            if (!patrol && !s.Nudged && idle >= NudgeAfter)
            {
                s.Nudged = true;
                s.GoOnUntil = now + 15f;
                Nudges++;

                // A follower by its leader has no quest of its own to go on with (raid 2026-10-26 18:34: kyoukopan stood a minute in QuestingBots' idle
                // layer two metres from his leader, "told to go on" with a quest followers never take): it searches what lies about, or moves to cover the leader
                BotOwner leader = TacticalTasks.SquadLeaderOf(bot);
                if (leader != null && leader != bot && !leader.IsDead && (leader.Position - pos).sqrMagnitude < 30f * 30f)
                {
                    if (RoomSweep.HandNearest(bot, 30f, out string what))
                    {
                        Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {idle:0} s by its leader in {layer} - searches {what} nearby");
                        return;
                    }

                    Vector3? post = PostNear(leader, bot);
                    if (post != null)
                    {
                        TacticalTasks.Set(bot, new TacticalTask
                        {
                            Kind = TaskKind.Relocate,
                            StandAt = post,
                            LookAt = post.Value + (post.Value - leader.Position),
                            Until = now + 20f,
                            Speed = 1f,
                            MovePose = 1f,
                            Sprint = false,
                        });
                        Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {idle:0} s by its leader in {layer} - takes a post {Vector3.Distance(pos, post.Value):0} m off, watching out");
                        return;
                    }
                }

                Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {idle:0} s in {layer} (QuestingBots: {qb}, hp {bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized:P0}) - told to go on");
                return;
            }

            if (!patrol && idle < MoveAfter)
            {
                return;
            }

            s.At = pos;
            s.Since = now;
            s.Nudged = false;
            s.PatrolAfter = UnityEngine.Random.Range(10f, 15f);
            // An AI scav on patrol goes somewhere else in its own area (raid 2026-10-31 05:46: Гурам Стакан was sent 269 m off his) (ScavPatrol)
            float pathLength = 0f;
            Vector3? far = ScavPatrol.Applies(bot, out BotProfile sp) ? ScavPatrol.PointInArea(bot, sp, out pathLength) : RoamPlanner.PickFarPoint(bot, pos, out pathLength);
            if (far == null)
            {
                Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {idle:0} s in {layer} (QuestingBots: {qb}) - no place found to send it to");

                // No way to anywhere is what a bot shut in a room looks like (ShutIn decides by the navmesh)
                ShutIn.PathFailed(bot, "no place it could be sent to");
                return;
            }

            QuestingBots.Controllers.BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
            TacticalTasks.Set(bot, new TacticalTask
            {
                Kind = TaskKind.Relocate,
                StandAt = far,
                LookAt = far.Value,
                Until = now + pathLength / 3.2f + 45f,
                Speed = 1f,
                MovePose = 1f,
                Sprint = false,
            });
            Moves++;
            if (patrol)
            {
                PatrolWalks++;
                Log.Info($"LR Scav: {p.Name} looked about {idle:0} s with nothing to do in its area - walks its beat to ({far.Value.x:0}, {far.Value.z:0}), {pathLength:0} m away");
                return;
            }

            Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {idle:0} s in {layer} (QuestingBots: {qb}) - sent to ({far.Value.x:0}, {far.Value.z:0}), {pathLength:0} m away");
        }

        /// <summary>A point on the navmesh 6-12 m from the leader, away from where the follower stands, to watch the surroundings from.</summary>
        internal static Vector3? PostNear(BotOwner leader, BotOwner bot)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f);
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                Vector3 want = leader.Position + dir * UnityEngine.Random.Range(6f, 12f);
                if ((want - bot.Position).sqrMagnitude < 4f * 4f
                    || !UnityEngine.AI.NavMesh.SamplePosition(want, out UnityEngine.AI.NavMeshHit hit, 2.5f, UnityEngine.AI.NavMesh.AllAreas)
                    || Mathf.Abs(hit.position.y - leader.Position.y) > 2f)
                {
                    continue;
                }

                return hit.position;
            }

            return null;
        }

        /// <summary>A layer that by itself does nothing - the bot stands while it runs.</summary>
        private static bool IdleLayer(BotOwner bot, string layer)
        {
            switch (layer)
            {
                case "BotRecoveryLayer":
                case "PatrolAssault":
                    return true;
                case "BotFollowerLayer":
                {
                    // Standing by a leader that stands is the leader's business; with no leader near it is idle
                    BotOwner leader = TacticalTasks.SquadLeaderOf(bot);
                    return leader == null || leader == bot || leader.IsDead || (leader.Position - bot.Position).sqrMagnitude > 30f * 30f;
                }

                case "Looting":
                {
                    LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                    return brain != null && !brain.IsBotLooting;
                }

                default:
                    return false;
            }
        }

        private static bool GoodReason(BotOwner bot)
        {
            if (bot.Medecine?.Using == true || TacticalTasks.TryGet(bot, out _) || RoomSweep.InVisit(bot) || HitReaction.SinceHit(bot) < 10f || SquadLoot.IsWaiting(bot))
            {
                return true;
            }

            BotMedecine med = bot.Medecine;
            if (med?.FirstAid != null && med.SurgicalKit != null
                && ((med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use) || (med.SurgicalKit.HaveWork && med.SurgicalKit.HaveSmth2Use)))
            {
                // Something to treat and something to treat it with: QuestingBots' recovery layer does that standing (given 3 minutes, then idle anyway)
                return Time.time - (_healSince.TryGetValue(bot.ProfileId, out float t) ? t : (_healSince[bot.ProfileId] = Time.time)) < 180f;
            }

            _healSince.Remove(bot.ProfileId);

            // Waiting on an exit timer
            float leave = bot.Exfiltration?.LeaveTime ?? 0f;
            return leave > 0f && leave < float.MaxValue && leave - Time.time < 120f && leave > Time.time;
        }

        private static readonly Dictionary<string, float> _healSince = new Dictionary<string, float>();
    }
}
