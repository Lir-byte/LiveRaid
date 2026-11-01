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
    /// </summary>
    public static class CloseSense
    {
        private const float Far = 10f;
        private const float Near = 5f;

        private static readonly Dictionary<long, float> _since = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> _doneAt = new Dictionary<long, float>();
        private static readonly List<Player> _players = new List<Player>();
        private static float _next;

        public static int Noticed { get; private set; }

        public static string Summary => $"enemies right next to a bot noticed {Noticed}";

        public static void Clear()
        {
            _since.Clear();
            _doneAt.Clear();
            _next = 0f;
            Noticed = 0;
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
                if (Mathf.Abs(d.y) > 2.2f || d.x * d.x + d.z * d.z > Far * Far)
                {
                    continue;
                }

                long key = ((long)me.Id << 32) | (uint)other.Id;
                float dist = new Vector2(d.x, d.z).magnitude;
                bool moving = other.Velocity.sqrMagnitude > 0.16f;
                if ((dist > Near && !moving) || (_doneAt.TryGetValue(key, out float done) && now - done < 30f) || SquadTies.AreMates(me, other) || Knows(bot, other)
                    || !bot.BotsGroup.IsPlayerEnemy(other) || LootBrain.SquadIds(bot).Contains(other.ProfileId))
                {
                    _since.Remove(key);
                    continue;
                }

                Vector3 eye = pos + Vector3.up * 1.5f;
                Vector3 his = other.Position + Vector3.up * 1.4f;
                if (Physics.Linecast(eye, his, LayersMaskController.HighPolyWithTerrainMask))
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
                if (now - since < needed)
                {
                    continue;
                }

                _since.Remove(key);
                _doneAt[key] = now;
                Noticed++;
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
                Log.Detail("combat", () => $"{p.Name} notices {other.Profile?.Nickname} right next to it ({dist:0.0} m, {(angle > 110f ? "behind it" : angle > 50f ? "at its side" : "in front")}" +
                                           $"{(moving ? ", moving" : "")}) - turns to him");
                return;
            }
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
