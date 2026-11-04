using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// An enemy a few metres away does not go unnoticed (F12 -> 9 "Notice an enemy right next to you").
    ///
    /// Raid 2026-10-26 20:28: NastyInc stood 3-6 m from DasWulf for over a minute (his rifle empty, so he did not shoot), DasWulf looted and went
    /// about his quest step "calm" the whole time and never once turned round - until NastyInc's pistol hit him. A player hears footsteps and breathing
    /// at that range and sees a shape at the edge of his view. SAIN's vision only sees in front of the bot, and a bot busy looting or questing looks
    /// at its work.
    /// Twice a second: an enemy bot within 10 m on the same floor (2.2 m of height), in the open line between their heads and moving - or within 5 m
    /// even when still - that the bot does not know yet, noticed for 1-2.5 s (by skill) in a row, becomes known: the whole group takes him as an
    /// enemy (EFT shares a group's enemies, SAIN picks it up from there) and the bot turns to him. Once per pair per 30 s.
    ///
    /// Heard, not seen (2026-11-02, a tester: "when hostile PMCs or scavs run into each other face to face, some bots ignore the threat"). Traces of
    /// 31.10-02.11: 13 of 225 bots died to a killer within 20 m they never knew of; Илюша Мистер heard a PMC run up 7 m away, stood 4 s "listening"
    /// and was shot - the PMC came up the stairs, 3 m below him, so the same-floor rule above never looked at him. Now an enemy running within 12 m or
    /// walking upright within 6 m - up to 4.5 m above or below when the way between them on foot is short (a stair, not a slab) - is noticed too,
    /// a little faster than by sight, and the bot turns to him.
    /// </summary>
    public static class CloseSense
    {
        private const float Far = 10f;
        private const float Near = 5f;
        private const float HearRun = 12f;
        private const float HearWalk = 6f;
        private const float HearHeight = 4.5f;

        private static readonly Dictionary<long, float> _since = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> _doneAt = new Dictionary<long, float>();
        private static readonly List<Player> _players = new List<Player>();
        private static readonly Dictionary<long, (float at, bool ok)> _connected = new Dictionary<long, (float, bool)>();
        private static readonly UnityEngine.AI.NavMeshPath _path = new UnityEngine.AI.NavMeshPath();
        private static float _next;

        public static int Noticed { get; private set; }
        public static int Heard { get; private set; }

        public static string Summary => $"enemies right next to a bot noticed {Noticed} (of them by their steps {Heard})";

        public static void Clear()
        {
            _since.Clear();
            _doneAt.Clear();
            _connected.Clear();
            _next = 0f;
            Noticed = Heard = 0;
        }

        public static void Tick()
        {
            if (!Settings.CloseNotice.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            _players.Clear();
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p != null && p.HealthController?.IsAlive == true && !Spectator.ObserverMode.Is(p))
                {
                    _players.Add(p);
                }
            }

            float now = Time.time;
            foreach (Player player in _players)
            {
                BotOwner bot = player.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                try
                {
                    Look(bot, player, p, now);
                }
                catch (System.Exception e)
                {
                    Diag.Report("CloseSense", e);
                }
            }
        }

        private static void Look(BotOwner bot, Player me, BotProfile p, float now)
        {
            Vector3 pos = me.Position;
            foreach (Player other in _players)
            {
                if (other == me)
                {
                    continue;
                }

                Vector3 d = other.Position - pos;
                float dy = Mathf.Abs(d.y);
                float flat2 = d.x * d.x + d.z * d.z;
                if (dy > HearHeight || flat2 > HearRun * HearRun)
                {
                    continue;
                }

                long key = ((long)me.Id << 32) | (uint)other.Id;
                float dist = Mathf.Sqrt(flat2);
                Vector3 v = other.Velocity;
                v.y = 0f;
                float speed = v.magnitude;
                bool moving = speed > 0.4f;
                bool running = speed > 3f;
                bool walking = speed > 1.2f && other.Pose == EPlayerPose.Stand;
                bool sightRange = dy <= 2.2f && dist <= Far && (dist <= Near || moving);
                bool hearRange = (running && dist <= HearRun) || (walking && dist <= HearWalk);
                if ((!sightRange && !hearRange) || (_doneAt.TryGetValue(key, out float done) && now - done < 30f) || SquadTies.AreMates(me, other) || Knows(bot, other)
                    || !bot.BotsGroup.IsPlayerEnemy(other) || LootBrain.SquadIds(bot).Contains(other.ProfileId))
                {
                    _since.Remove(key);
                    continue;
                }

                Vector3 eye = pos + Vector3.up * 1.5f;
                Vector3 his = other.Position + Vector3.up * 1.4f;
                bool seen = sightRange && !Physics.Linecast(eye, his, LayersMaskController.HighPolyWithTerrainMask);
                bool heard = !seen && hearRange && (dy <= 2.2f || Connected(key, pos, other.Position, dist, now));
                if (!seen && !heard)
                {
                    _since.Remove(key);
                    continue;
                }

                if (!_since.TryGetValue(key, out float since))
                {
                    _since[key] = now;
                    continue;
                }

                float needed = p.Skill switch
                {
                    SkillTier.Pro => 1f,
                    SkillTier.Experienced => 1.4f,
                    SkillTier.Average => 1.9f,
                    _ => 2.5f,
                };
                if (now - since < (seen ? needed : needed * 0.7f))
                {
                    continue;
                }

                _since.Remove(key);
                _doneAt[key] = now;
                Noticed++;
                if (!seen)
                {
                    Heard++;
                }

                try
                {
                    bot.BotsGroup.AddEnemy(other, EBotEnemyCause.addPlayer);
                }
                catch
                {
                    // a player the group cannot take as an enemy
                }

                bot.Steering.LookToPoint(his);
                Vector3 fwd = me.LookDirection;
                float angle = Vector3.Angle(new Vector3(fwd.x, 0f, fwd.z), new Vector3(d.x, 0f, d.z));
                string where = d.y > 2.2f ? "above it" : d.y < -2.2f ? "below it" : angle > 110f ? "behind it" : angle > 50f ? "at its side" : "in front";
                Log.Detail("combat", () => seen
                    ? $"{p.Name} notices {other.Profile?.Nickname} right next to it ({dist:0.0} m, {where}{(moving ? ", moving" : "")}) - turns to him"
                    : $"{p.Name} hears {other.Profile?.Nickname} {(running ? "running" : "walking")} right by it ({dist:0.0} m, {where}) - turns to him");
                return;
            }
        }

        /// <summary>The way on foot between two points a few metres apart, one above the other, is short - a stair or a ramp, not a floor between (cached 2 s).</summary>
        private static bool Connected(long key, Vector3 a, Vector3 b, float flat, float now)
        {
            if (_connected.TryGetValue(key, out (float at, bool ok) c) && now - c.at < 2f)
            {
                return c.ok;
            }

            bool ok = false;
            if (UnityEngine.AI.NavMesh.SamplePosition(a, out UnityEngine.AI.NavMeshHit ha, 1.2f, UnityEngine.AI.NavMesh.AllAreas)
                && UnityEngine.AI.NavMesh.SamplePosition(b, out UnityEngine.AI.NavMeshHit hb, 1.2f, UnityEngine.AI.NavMesh.AllAreas)
                && UnityEngine.AI.NavMesh.CalculatePath(ha.position, hb.position, UnityEngine.AI.NavMesh.AllAreas, _path)
                && _path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
            {
                float len = 0f;
                Vector3[] corners = _path.corners;
                for (int i = 1; i < corners.Length; i++)
                {
                    len += Vector3.Distance(corners[i - 1], corners[i]);
                }

                ok = len <= flat * 1.6f + 6f;
            }

            _connected[key] = (now, ok);
            return ok;
        }

        private static bool Knows(BotOwner bot, Player other)
        {
            if (bot.Memory?.GoalEnemy?.Person?.ProfileId == other.ProfileId)
            {
                return true;
            }

            var sain = SainBot.Of(bot);
            if (sain == null)
            {
                return bot.BotsGroup?.Enemies != null && bot.BotsGroup.Enemies.ContainsKey(other);
            }

            foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
            {
                if (e != null && e.EnemyPlayer == other && (e.IsVisible || e.TimeSinceSeen < 8f || (e.Heard && e.TimeSinceHeard < 8f)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
