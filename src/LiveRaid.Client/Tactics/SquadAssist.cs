using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A squad comes when one of its own is in a fight. In the raids squadmates looted on, waited in cover or sat out QuestingBots'
    /// "wait for the group" 100 m away while their friend was shot. Here a member who is hit, or is shooting at an enemy he sees close by,
    /// calls the others: each squadmate that is not in its own fight runs (far) or moves (near) to a spot next to him on its own side and
    /// looks where the threat is. The errand ends as soon as the helper sees the enemy or is shot at - then SAIN fights.
    /// </summary>
    public static class SquadAssist
    {
        private const float MaxCallRange = 220f;
        private const float MinCallRange = 12f;

        private static readonly Dictionary<string, float> _busyUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _lastAlarm = new Dictionary<string, float>();
        private static float _next;

        public static int Calls { get; private set; }

        public static void Clear()
        {
            _busyUntil.Clear();
            _lastAlarm.Clear();
            Calls = 0;
        }

        /// <summary>Somebody of this bot's squad within <paramref name="range"/> m is in a real fight right now (for QuestingBots' "wait for the group").</summary>
        public static bool SquadInRealFight(BotOwner bot, float range = 9999f)
        {
            HashSet<string> squad = LootBrain.SquadIds(bot);
            if (squad.Count <= 1)
            {
                return false;
            }

            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                BotOwner mate = p?.AIData?.BotOwner;
                if (mate != null && mate != bot && squad.Contains(p.ProfileId) && (p.Position - bot.Position).sqrMagnitude < range * range
                    && HitReaction.InRealFight(mate, 10f))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every second: a member who sees an enemy close by and is trading shots calls the squad too (not only when hit).</summary>
        public static void Tick()
        {
            if (!Settings.SquadTactics.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                BotOwner bot = p?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                // Attacked = shot at (a bullet flew by or hit) with an enemy it knows, or trading shots with one it sees
                var enemy = SainBot.Of(bot)?.GoalEnemy;
                if (enemy == null || enemy.EnemyPlayer == null)
                {
                    continue;
                }

                bool underFire = bot.Memory != null && (bot.Memory.IsUnderFire || Time.time - bot.Memory.UnderFireTime < 4f);
                bool shooting = enemy.IsVisible && enemy.RealDistance < 120f && WorldEvents.ShotBy(bot.ProfileId, 3f);
                if ((underFire && enemy.RealDistance < 160f) || shooting)
                {
                    Alarm(bot, enemy.EnemyPlayer, enemy.EnemyPosition);
                }
            }
        }

        public static void Alarm(BotOwner victim, IPlayer threat, Vector3 threatPos)
        {
            if (!Settings.SquadTactics.Value || victim == null)
            {
                return;
            }

            if (_lastAlarm.TryGetValue(victim.ProfileId, out float last) && Time.time - last < 12f)
            {
                return;
            }

            _lastAlarm[victim.ProfileId] = Time.time;
            HashSet<string> squad = LootBrain.SquadIds(victim);
            if (squad.Count <= 1)
            {
                return;
            }

            Vector3 at = victim.Position;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                BotOwner mate = p?.AIData?.BotOwner;
                if (mate == null || mate == victim || mate.IsDead || !squad.Contains(p.ProfileId) || Spectator.TakeOver.IsTaken(mate))
                {
                    continue;
                }

                float dist = Vector3.Distance(mate.Position, at);
                if (dist > MaxCallRange)
                {
                    continue;
                }

                // Everyone drops what he is doing - first of all the loot - when a squadmate is attacked
                if (Settings.HelpSquadmates.Value && dist < 120f && IsBusyLooting(mate))
                {
                    LootingBots.External.PreventBotFromLooting(mate, 15f);
                    Log.Detail("squad", $"{mate.Profile.Nickname} drops the loot: {victim.Profile.Nickname} is under attack");
                }

                if (threat != null && dist < MinCallRange)
                {
                    // Right next to him: no errand, the threat is shared and SAIN fights it
                    TryAddEnemy(mate, threat);
                    continue;
                }

                if (dist < MinCallRange || HitReaction.InRealFight(mate, 5f)
                    || (_busyUntil.TryGetValue(mate.ProfileId, out float busy) && Time.time < busy))
                {
                    continue;
                }

                // A hurt mate with meds treats himself first (he would be no help at 50 % hp and bleeding, and the run broke off his bandaging)
                if (mate.Medecine?.Using == true || (mate.Medecine?.FirstAid?.Have2Do == true && mate.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.7f))
                {
                    continue;
                }

                if (TacticalTasks.TryGet(mate, out TacticalTask current) && current.Kind == TaskKind.Assist)
                {
                    continue;
                }

                Vector3? spot = SupportSpot(mate, at, threatPos) ?? NearFriend(at);
                if (spot == null)
                {
                    continue;
                }

                if (threat != null)
                {
                    TryAddEnemy(mate, threat);
                }

                // A quest step, an errand, a handover: all of it waits (an Assist outranks every other task)
                if (TacticalTasks.TryGet(mate, out TacticalTask other) && other.Kind != TaskKind.Assist)
                {
                    TacticalTasks.Cancel(mate, $"{victim.Profile.Nickname} is under attack");
                }

                float seconds = Mathf.Clamp(dist / 4f + 15f, 20f, 70f);
                _busyUntil[mate.ProfileId] = Time.time + seconds + 10f;
                TacticalTasks.Set(mate, new TacticalTask
                {
                    Kind = TaskKind.Assist,
                    StandAt = spot,
                    LookAt = threatPos,
                    Until = Time.time + seconds,
                    Speed = 1f,
                    Sprint = dist > 30f,
                    SprintBeyond = 0f,
                    MovePose = 1f,
                });
                Calls++;
                SquadBrain.Say(mate, EPhraseTrigger.Going, 0.35f);
                Log.Info($"LR Squad: {mate.Profile.Nickname} runs to help {victim.Profile.Nickname} ({dist:0} m away, threat {threat?.Profile?.Nickname ?? "?"})");
            }
        }

        private static bool IsBusyLooting(BotOwner bot)
        {
            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            return brain != null && (brain.LootTaskRunning || brain.HasActiveLootable);
        }

        private static void TryAddEnemy(BotOwner mate, IPlayer threat)
        {
            try
            {
                mate.BotsGroup?.AddEnemy(threat, EBotEnemyCause.callBot);
            }
            catch
            {
                // not an enemy this group can take
            }
        }

        /// <summary>No good support spot: a navmesh point a few metres from the friend.</summary>
        private static Vector3? NearFriend(Vector3 friend) =>
            NavMesh.SamplePosition(friend, out NavMeshHit hit, 4f, NavMesh.AllAreas) ? hit.position : (Vector3?)null;

        /// <summary>A navmesh spot 4-9 m from the friend, on the helper's side of him (not out in front between him and the threat).</summary>
        private static Vector3? SupportSpot(BotOwner mate, Vector3 friend, Vector3 threat)
        {
            Vector3 fromThreat = friend - threat;
            fromThreat.y = 0f;
            Vector3 toMate = mate.Position - friend;
            toMate.y = 0f;
            Vector3 dir = (fromThreat.normalized + toMate.normalized).normalized;
            if (dir.sqrMagnitude < 0.01f)
            {
                dir = toMate.sqrMagnitude > 0.01f ? toMate.normalized : Vector3.forward;
            }

            NavMeshPath path = new NavMeshPath();
            foreach (float angle in new[] { 0f, 40f, -40f, 80f, -80f })
            {
                foreach (float r in new[] { 6f, 4f, 9f })
                {
                    Vector3 probe = friend + Quaternion.Euler(0f, angle, 0f) * dir * r;
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 2f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - friend.y) < 2f
                        && NavMesh.CalculatePath(mate.Position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                    {
                        return hit.position;
                    }
                }
            }

            return null;
        }
    }
}
