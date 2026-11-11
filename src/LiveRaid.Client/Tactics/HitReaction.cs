using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Being shot snaps a bot out of whatever it was doing. Raid 2026-10-19 05:38: Mattdokn stood on the game's exfil layer while a PMC
    /// emptied a magazine into his back (hp 20 -> 14 %), because that layer only yields to an enemy in EFT's own memory, which SAIN does not
    /// keep. Here every hit is noticed (EFT's Memory.LastTimeHit): the shooter becomes an enemy of the bot's whole group, the bot turns to
    /// where the shots came from, LiveRaid's own errands are dropped, and its squad is called (<see cref="SquadAssist"/>).
    /// "In a real fight" (hit, shot at, an enemy seen recently and close) is also what lets other systems decide whether a bot may get on.
    /// </summary>
    public static class HitReaction
    {
        public class Hit
        {
            public float At;
            public IPlayer By;
            public Vector3 From;
        }

        // A field of Player, not a property: looked up as a property it was always null and no hit was ever reacted to (raids up to 2026-10-22: "hit reactions 0")
        private static readonly System.Reflection.FieldInfo LastAggressor = HarmonyLib.AccessTools.Field(typeof(Player), "LastAggressor");
        private static readonly Dictionary<string, float> _seen = new Dictionary<string, float>();
        private static readonly Dictionary<string, Hit> _hits = new Dictionary<string, Hit>();
        private static readonly Dictionary<string, float> _underFireSeen = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _nearMissAt = new Dictionary<string, float>();
        private static float _next;

        public static int Reactions { get; private set; }

        public static int NearMisses { get; private set; }

        public static void Clear()
        {
            _seen.Clear();
            _hits.Clear();
            _underFireSeen.Clear();
            _nearMissAt.Clear();
            Reactions = 0;
            NearMisses = 0;
        }

        public static bool TryGetHit(BotOwner bot, out Hit hit) => _hits.TryGetValue(bot.ProfileId, out hit);

        public static float SinceHit(BotOwner bot) => bot != null && _hits.TryGetValue(bot.ProfileId, out Hit h) ? Time.time - h.At : float.MaxValue;

        /// <summary>Hit or shot at lately, or an enemy seen lately and close: a fight is going on right here.</summary>
        public static bool InRealFight(BotOwner bot, float memory = 20f)
        {
            if (bot == null || bot.IsDead)
            {
                return false;
            }

            if (SinceHit(bot) < memory || (bot.Memory != null && Time.time - bot.Memory.UnderFireTime < memory * 0.5f))
            {
                return true;
            }

            // Plain time windows on purpose: stretching this with the mind phase (session 7) kept whole scav groups standing
            // "in a fight" that was over or far away
            var enemy = SainBot.Of(bot)?.GoalEnemy;
            return enemy != null && (enemy.IsVisible || enemy.TimeSinceSeen < memory) && enemy.RealDistance < 150f;
        }

        /// <summary>
        /// A fight right here, for a bot that must not be drawn away from where it stands (aboard the armoured train): hit or shot at lately, or an enemy IN SIGHT
        /// within <paramref name="closeMeters"/>. An enemy farther off, or only known, is not a reason to leave the carriage.
        /// </summary>
        public static bool InCloseFight(BotOwner bot, float memory, float closeMeters)
        {
            if (bot == null || bot.IsDead)
            {
                return false;
            }

            if (SinceHit(bot) < memory || (bot.Memory != null && Time.time - bot.Memory.UnderFireTime < memory * 0.5f))
            {
                return true;
            }

            var enemy = SainBot.Of(bot)?.GoalEnemy;
            return enemy != null && enemy.IsVisible && enemy.RealDistance < closeMeters;
        }

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.Memory == null || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                CheckNearMiss(bot, player);
                float lastHit = bot.Memory.LastTimeHit;
                if (lastHit <= 0f || (_seen.TryGetValue(bot.ProfileId, out float seen) && lastHit <= seen + 0.01f))
                {
                    continue;
                }

                _seen[bot.ProfileId] = lastHit;
                IPlayer by = LastAggressor?.GetValue(player) as IPlayer;
                if (by == null || by.ProfileId == player.ProfileId || LootBrain.SquadIds(bot).Contains(by.ProfileId))
                {
                    continue;
                }

                React(bot, player, by);
            }
        }

        /// <summary>
        /// Shot at but not hit: a bullet flew by or struck next to the bot. SAIN notices that itself (it marks the shooter as known,
        /// puts the bot under fire and suppresses it) - but LiveRaid reacted only to hits, so a bot facing away kept its errand
        /// (a guard post, a watch, a walk to a far fight) and its squad heard nothing. Now a near miss does what a hit does, short of
        /// the damage: the errand is dropped, the bot turns to where the shots came from (SAIN's estimate, not the shooter's real place)
        /// unless it already sees an enemy, and the squad is called. At most once in 8 s per bot.
        /// </summary>
        private static void CheckNearMiss(BotOwner bot, Player player)
        {
            float underFire = bot.Memory.UnderFireTime;
            if (underFire <= 0f || (_underFireSeen.TryGetValue(bot.ProfileId, out float seen) && underFire <= seen + 0.01f))
            {
                return;
            }

            _underFireSeen[bot.ProfileId] = underFire;
            if (SinceHit(bot) < 3f || (_nearMissAt.TryGetValue(bot.ProfileId, out float last) && Time.time - last < 8f))
            {
                return;
            }

            var sain = SainBot.Of(bot);
            IPlayer source = sain?.Memory?.LastUnderFireSource;
            if (source == null || source.ProfileId == player.ProfileId || LootBrain.SquadIds(bot).Contains(source.ProfileId))
            {
                return;
            }

            _nearMissAt[bot.ProfileId] = Time.time;
            NearMisses++;
            Vector3 from = sain.Memory.UnderFireFromPosition;
            if (from == Vector3.zero)
            {
                from = source.Position;
            }

            string doing = bot.Brain?.ActiveLayerName() ?? "-";
            bool dropped = false;
            if (TacticalTasks.TryGet(bot, out TacticalTask task) && task.Kind != TaskKind.Assist)
            {
                TacticalTasks.Cancel(bot, "shot at (near miss)");
                dropped = true;
            }

            var enemy = sain.GoalEnemy;
            if (enemy == null || !enemy.IsVisible)
            {
                bot.Steering.LookToPoint(from + Vector3.up * 1.4f);
            }

            Log.Detail("combat", $"{bot.Profile.Nickname} shot at (missed) by {source.Profile?.Nickname ?? "someone"} from ~{Vector3.Distance(bot.Position, from):0} m " +
                                 $"while '{doing}'" + (dropped ? $" ({task.Kind} dropped)" : "") + " - turns that way, squad alerted");
            SquadAssist.Alarm(bot, source, from);
        }

        private static void React(BotOwner bot, Player player, IPlayer by)
        {
            Vector3 from = by.Position;
            bool fresh = SinceHit(bot) > 6f;
            _hits[bot.ProfileId] = new Hit { At = Time.time, By = by, From = from };
            if (!fresh)
            {
                return;
            }

            Reactions++;
            string doing = bot.Brain?.ActiveLayerName() ?? "-";
            if (TacticalTasks.TryGet(bot, out TacticalTask task))
            {
                TacticalTasks.Cancel(bot, "shot at");
            }

            // The whole group knows the shooter at once (EFT shares a group's enemies); SAIN picks it up from there
            try
            {
                bot.BotsGroup?.AddEnemy(by, EBotEnemyCause.followGetHit);
            }
            catch
            {
                // a player the group cannot take as an enemy (same side rules of a special role)
            }

            var enemy = SainBot.Of(bot)?.GoalEnemy;
            if (enemy == null || !enemy.IsVisible)
            {
                bot.Steering.LookToPoint(from + Vector3.up * 1.4f);
            }

            float dist = Vector3.Distance(player.Position, from);
            Log.Detail("combat", $"{bot.Profile.Nickname} hit by {by.Profile?.Nickname ?? "someone"} from {dist:0} m while '{doing}'" +
                                 (task != null ? $" ({task.Kind} dropped)" : "") + " - turns to the shooter, squad alerted");
            SquadAssist.Alarm(bot, by, from);
        }
    }
}
