using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// "Head on a swivel" (F12 -> 3). A bot that knows trouble is about (BotMind Alert or Settling: heard an enemy, a fight just over) and walks on its
    /// way looked only where it walked. A player glances into every doorway he passes and towards where the enemy or the noise was. While walking,
    /// about half the time the bot looks at a door or doorway 4-15 m off its way (seen from where it is), the enemy's last known place or the last
    /// noise it heard, for 0.8-1.6 s, then where it goes again.
    /// </summary>
    public static class Swivel
    {
        private class State
        {
            public float Until;
            public Vector3? At;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();

        public static int Glances { get; private set; }

        public static void Clear()
        {
            _states.Clear();
            Glances = 0;
        }

        /// <summary>Where the bot looks instead of the way it walks right now, or null.</summary>
        internal static Vector3? LookAt(BotOwner bot)
        {
            if (!Settings.Enabled.Value || !Settings.Swivel.Value || bot == null || bot.IsDead)
            {
                return null;
            }

            MindPhase phase = BotMind.PhaseOf(bot);
            if (phase != MindPhase.Alert && phase != MindPhase.Settling)
            {
                return null;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other || bot.GetPlayer?.IsSprintEnabled == true)
            {
                return null;
            }

            float now = Time.time;
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            if (now < s.Until)
            {
                return s.At;
            }

            // Next glance: half the time the way it walks
            if (_rng.NextDouble() < 0.5)
            {
                s.At = null;
                s.Until = now + 1f + (float)_rng.NextDouble();
                return null;
            }

            s.At = Pick(bot);
            s.Until = now + 0.8f + (float)_rng.NextDouble() * 0.8f;
            if (s.At != null)
            {
                Glances++;
            }

            return s.At;
        }

        private static Vector3? Pick(BotOwner bot)
        {
            Vector3 eye = bot.Position + Vector3.up * 1.5f;
            Vector3 heading = bot.LookDirection;
            heading.y = 0f;
            List<Vector3> options = new List<Vector3>();
            Enemy enemy = SainBot.Of(bot)?.GoalEnemy;
            if (enemy?.LastKnownPosition is Vector3 last && (last - bot.Position).sqrMagnitude < 120f * 120f)
            {
                options.Add(last + Vector3.up * 1.4f);
                options.Add(last + Vector3.up * 1.4f);
            }

            if (NoiseResponse.LastHeard(bot, 30f, out Vector3 noise))
            {
                options.Add(noise + Vector3.up * 1.4f);
            }

            foreach (Door d in DoorAssistLayer.Doors)
            {
                if (d == null || d.DoorState == EDoorState.Locked)
                {
                    continue;
                }

                Vector3 at = d.transform.position + Vector3.up * 1.2f;
                Vector3 to = at - eye;
                float dist = to.magnitude;
                if (dist < 4f || dist > 15f || Mathf.Abs(to.y) > 3f)
                {
                    continue;
                }

                Vector3 flat = to;
                flat.y = 0f;
                if (heading.sqrMagnitude > 0.01f && Vector3.Angle(heading, flat) > 110f)
                {
                    continue;
                }

                if (!Physics.Linecast(eye, at, LayersMaskController.HighPolyWithTerrainMask))
                {
                    options.Add(at);
                }
            }

            return options.Count == 0 ? (Vector3?)null : options[_rng.Next(options.Count)];
        }
    }

    /// <summary>EFT's "look where you walk" for bots (QuestingBots, LiveRaid's layers): a watchful bot glances about instead (Swivel).</summary>
    public class SwivelLookPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotSteering), nameof(BotSteering.LookToMovingDirection), new[] { typeof(float) });

        [PatchPrefix]
        protected static bool PatchPrefix(BotSteering __instance, float rotateSpeed)
        {
            try
            {
                Vector3? at = Swivel.LookAt(__instance._owner);
                if (at == null)
                {
                    return true;
                }

                __instance.LookToPoint(at.Value, rotateSpeed);
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("SwivelLookPatch", e);
                return true;
            }
        }
    }

    /// <summary>
    /// The long gun takes the high ground (F12 -> 3 "Long gun takes the high ground"). A squad's marksman or sniper walked in the column like the rest.
    /// Now and then (every 2-4 minutes, more for doctrines that like the range) a calm member with a long gun, whose leader is on the move, takes a spot
    /// 3-12 m above the squad within 15-45 m of the leader that sees out over the way ahead, watches from it 15-30 s and then follows on.
    /// </summary>
    public static class Overwatch
    {
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static float _tick;

        public static int Posts { get; private set; }

        public static void Clear()
        {
            _next.Clear();
            _tick = 0f;
            Posts = 0;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.Overwatch.Value || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind != BotKind.Pmc || !CombatSense.IsLongGun(p) || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotOwner leader = TacticalTasks.SquadLeaderOf(bot);
                if (leader == null || leader == bot || leader.IsDead || leader.Mover?.HasPathAndNoComplete != true)
                {
                    continue;
                }

                if (!_next.TryGetValue(bot.ProfileId, out float next))
                {
                    _next[bot.ProfileId] = next = now + 60f + (float)_rng.NextDouble() * 120f;
                }

                if (now < next || TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || ExtractPlanner.IsLeaving(bot))
                {
                    continue;
                }

                DoctrineDeck.Member d = SquadDoctrines.Of(bot);
                float range = d != null ? d[DoctrineDeck.Range] : 0.6f;
                _next[bot.ProfileId] = now + (120f + (float)_rng.NextDouble() * 120f) * (1.4f - range);
                Vector3? spot = HighSpot(bot, leader);
                if (spot == null)
                {
                    continue;
                }

                float hold = 15f + (float)_rng.NextDouble() * 15f;
                Vector3 look = spot.Value + leader.LookDirection * 40f;
                TacticalTask task = new TacticalTask
                {
                    Kind = TaskKind.Relocate, StandAt = spot, LookAt = look, Speed = 1f, Sprint = true, SprintBeyond = 15f, MovePose = 1f,
                    Until = now + Vector3.Distance(bot.Position, spot.Value) / 3f + 10f,
                    OnFinished = b => TacticalTasks.Set(b, new TacticalTask { Kind = TaskKind.Listen, LookAt = look, Until = Time.time + hold }),
                };
                if (TacticalTasks.Set(bot, task))
                {
                    Posts++;
                    SquadBrain.Say(bot, EPhraseTrigger.Covering, 0.4f);
                    Log.Info($"LR Squad: {p.Name} takes the high ground {spot.Value.y - leader.Position.y:0} m above the squad to cover its way ({hold:0} s)");
                }
            }
        }

        private static Vector3? HighSpot(BotOwner bot, BotOwner leader)
        {
            NavMeshPath path = new NavMeshPath();
            Vector3 best = default;
            float bestScore = float.MinValue;
            Vector3 heading = leader.LookDirection;
            heading.y = 0f;
            for (int i = 0; i < 24; i++)
            {
                float angle = (float)_rng.NextDouble() * 360f;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                Vector3 want = leader.Position + dir * (15f + (float)_rng.NextDouble() * 30f) + Vector3.up * 8f;
                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                {
                    continue;
                }

                float rise = hit.position.y - leader.Position.y;
                if (rise < 3f || rise > 12f)
                {
                    continue;
                }

                Vector3 eye = hit.position + Vector3.up * 1.5f;
                Vector3 ahead = leader.Position + (heading.sqrMagnitude > 0.01f ? heading.normalized : dir) * 35f + Vector3.up;
                if (Physics.Linecast(eye, ahead, LayersMaskController.HighPolyWithTerrainMask)
                    || !NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++)
                {
                    len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                }

                if (len > 80f || !Roomy(hit.position) || !WayBack(hit.position, leader.Position, len, path))
                {
                    continue;
                }

                float score = rise * 0.3f - len * 0.03f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hit.position;
                }
            }

            return bestScore > float.MinValue ? best : (Vector3?)null;
        }

        /// <summary>
        /// Room to stand and turn: navmesh a metre round the spot on most sides, solid ground under it and nothing in the body's way. Raid 15:59:
        /// The_Antman took a post on a strip by the old gas station where no side step fitted and stuck there until he was taken over.
        /// </summary>
        private static bool Roomy(Vector3 spot)
        {
            int sides = 0;
            for (int k = 0; k < 6; k++)
            {
                Vector3 probe = spot + Quaternion.Euler(0f, k * 60f, 0f) * Vector3.forward * 1.1f;
                if (NavMesh.SamplePosition(probe, out NavMeshHit near, 0.4f, NavMesh.AllAreas) && Mathf.Abs(near.position.y - spot.y) < 0.5f
                    && !NavMesh.Raycast(spot, near.position, out _, NavMesh.AllAreas))
                {
                    sides++;
                }
            }

            if (sides < 4)
            {
                return false;
            }

            if (!Physics.Raycast(spot + Vector3.up, Vector3.down, out RaycastHit ground, 1.8f, LayersMaskController.PlayerStaticCollisionsMask)
                || Mathf.Abs(ground.point.y - spot.y) > 0.5f)
            {
                return false;
            }

            return !Physics.CheckCapsule(spot + Vector3.up * 0.6f, spot + Vector3.up * 1.6f, 0.3f, LayersMaskController.PlayerStaticCollisionsMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>From the post back down to the squad on foot, about as long as the way up (a one-way drop or link would leave the bot up there).</summary>
        private static bool WayBack(Vector3 spot, Vector3 leader, float wayUp, NavMeshPath path)
        {
            if (!NavMesh.CalculatePath(spot, leader, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                return false;
            }

            float len = 0f;
            for (int k = 1; k < path.corners.Length; k++)
            {
                len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
            }

            return len <= Mathf.Max(wayUp * 1.6f + 15f, 30f) && len <= 110f;
        }
    }
}
