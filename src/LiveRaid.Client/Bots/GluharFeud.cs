using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Gluhar and his guard against the bots, for real (1.4.9, F12 "Gluhar and guard are enemies of all bots"). Raid 2026-11-11 16:06: Секач walked 40 s within 14-26 m of an AI scav
    /// (Антон Морисбудиш) and passed Гарри Корж at 3 m with no enemy at all. EFT's <c>BotsGroup.CheckAndAddEnemy</c> sends away anyone in the group's "always friendly" types
    /// BEFORE it asks <c>IsPlayerEnemy</c>, so that the IsPlayerEnemy flip alone never made the AI scavs enemies of the guard (player scavs and PMCs came by other routes).
    /// Here, every 3 seconds, a crew member's group and a bot within 150 m of it that are not yet enemies are made enemies in both directions with
    /// <c>BotsGroup.AddEnemy</c> (the call EFT's own warning code makes).
    /// </summary>
    public static class GluharFeud
    {
        private static float _next;
        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static readonly EBotEnemyCause[] Causes = { EBotEnemyCause.warn, EBotEnemyCause.checkAddTODO, EBotEnemyCause.addPlayer };

        public static int Made { get; private set; }

        public static string Summary => $"Gluhar's crew, raiders and the bots made enemies by LiveRaid: {Made}";

        public static void Clear()
        {
            _next = 0f;
            Made = 0;
            _noted.Clear();
        }

        public static bool IsCrew(WildSpawnType role) =>
            role == WildSpawnType.bossGluhar || role == WildSpawnType.followerGluharAssault || role == WildSpawnType.followerGluharScout
            || role == WildSpawnType.followerGluharSecurity || role == WildSpawnType.followerGluharSnipe
            // 1.4.10: raiders and rogues too (raid 2026-11-11 17:27: Росомаха, pmcBot, passed AI scavs - EFT keeps raiders friends with them)
            || role == WildSpawnType.pmcBot || role == WildSpawnType.exUsec;

        private static bool IsLive(WildSpawnType role) =>
            role == WildSpawnType.assault || role == WildSpawnType.cursedAssault || role == WildSpawnType.assaultGroup || role == WildSpawnType.pmcUSEC || role == WildSpawnType.pmcBEAR;

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.GluharHostileToAll.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 3f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<BotOwner> crew = new List<BotOwner>();
            List<BotOwner> live = new List<BotOwner>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || bot.Profile?.Info?.Settings == null)
                {
                    continue;
                }

                WildSpawnType role = bot.Profile.Info.Settings.Role;
                if (IsCrew(role))
                {
                    crew.Add(bot);
                }
                else if (IsLive(role))
                {
                    live.Add(bot);
                }
            }

            if (crew.Count == 0 || live.Count == 0)
            {
                return;
            }

            foreach (BotOwner c in crew)
            {
                foreach (BotOwner o in live)
                {
                    if ((c.Position - o.Position).sqrMagnitude > 150f * 150f || SquadTies.AreMates(c.ProfileId, o.ProfileId))
                    {
                        continue;
                    }

                    bool a = Befriend(c.BotsGroup, o.GetPlayer);
                    bool b = Befriend(o.BotsGroup, c.GetPlayer);
                    if ((a || b) && _noted.Count < 12 && _noted.Add(c.ProfileId + o.ProfileId))
                    {
                        Log.Info($"LR Hostility: {c.Profile.Nickname} ({c.Profile.Info.Settings.Role}) and {o.Profile.Nickname} ({o.Profile.Info.Settings.Role}) are enemies now - EFT kept them friends");
                    }
                }
            }
        }

        /// <summary>The group takes the player for an enemy; true when it did not before.</summary>
        private static bool Befriend(BotsGroup group, Player other)
        {
            if (group == null || other == null || group.Enemies.ContainsKey(other))
            {
                return false;
            }

            foreach (EBotEnemyCause cause in Causes)
            {
                try
                {
                    if (group.AddEnemy(other, cause))
                    {
                        Made++;
                        return true;
                    }
                }
                catch (System.Exception e)
                {
                    Diag.Report("GluharFeud", e);
                    return false;
                }
            }

            return false;
        }
    }
}
