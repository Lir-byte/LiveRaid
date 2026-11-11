using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// EFT treats a bot of the same role as a friend before it looks at any hostility chance ("IsSuitable": BEAR group sees a BEAR,
    /// USEC group sees a USEC). QuestingBots cuts that rule only while its "pmcs_always_hostile_against_pmcs" is on.
    /// This keeps the online rule whatever QuestingBots is set to: a PMC bot is the enemy of every PMC bot of another squad.
    /// Members of one squad stay allies (same bot group, the group's allies list, or the same group id).
    /// </summary>
    public class PmcSameSideHostilityPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<BotsGroup, BotOwner> InitialBot = AccessTools.FieldRefAccess<BotsGroup, BotOwner>("_initialBot");
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>Pairs turned hostile this raid (0 while QuestingBots already does the job).</summary>
        public static int Flipped { get; private set; }

        public static void Clear()
        {
            Flipped = 0;
            Reported.Clear();
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

        [PatchPostfix]
        protected static void PatchPostfix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            if (__result || !Settings.PmcAlwaysHostile.Value)
            {
                return;
            }

            try
            {
                if (IsOtherPmcSquad(__instance, player))
                {
                    __result = true;
                    Flipped++;
                    BotOwner own = InitialBot(__instance);
                    string key = own?.ProfileId + player.ProfileId;
                    if (Reported.Count < 10 && Reported.Add(key))
                    {
                        Log.Info($"LR Hostility: {own?.Profile?.Nickname} ({own?.Profile?.Info?.Side}) and {player.Profile?.Nickname} ({player.Side}) are different squads of PMCs - enemies, as online");
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("PmcSameSideHostilityPatch", e);
            }
        }

        private static bool IsOtherPmcSquad(BotsGroup group, IPlayer player)
        {
            if (player == null || !player.IsAI || (player.Side != EPlayerSide.Usec && player.Side != EPlayerSide.Bear))
            {
                return false;
            }

            BotOwner own = InitialBot(group);
            BotOwner other = player.AIData?.BotOwner;
            if (own == null || other == null || own.ProfileId == other.ProfileId || Bots.SquadTies.MateOfGroup(group, player, out _))
            {
                return false;
            }

            EPlayerSide ownSide = own.Profile?.Info?.Side ?? EPlayerSide.Savage;
            if (ownSide != EPlayerSide.Usec && ownSide != EPlayerSide.Bear)
            {
                return false;
            }

            // Still spawning (no group yet) or the same squad: never make squadmates enemies
            BotsGroup otherGroup = other.BotsGroup;
            if (otherGroup == null || otherGroup == group || group.Allies.Contains(player))
            {
                return false;
            }

            // QuestingBots' own squad record (the rule its own patch uses to keep squadmates friendly)
            QuestingBots.Components.Spawning.BotGenerationManager generators = Singleton<GameWorld>.Instance?.GetComponent<QuestingBots.Components.Spawning.BotGenerationManager>();
            if (generators != null && generators.TryGetBotGroupFromAnyGenerator(own, out QuestingBots.Models.BotSpawnInfo squad) && squad.ContainsProfile(player.Profile))
            {
                return false;
            }

            string ownGroupId = own.GetPlayer?.GroupId;
            return string.IsNullOrEmpty(ownGroupId) || ownGroupId != player.GroupId;
        }
    }

    /// <summary>
    /// Raiders and rogues (pmcBot, exUsec) and the scavs that play like players (player scavs, "chelobots") are enemies, as online: EFT keeps AI scavs and
    /// raiders friendly, and a bot that plays a player scav is an AI scav to it. Raid 2026-11-11: Гепард (pmcBot, in its Warn layer) stood 14 m from Игорян Кассир
    /// walking to the hermetic door and let him pass. Both ways: the raider takes the player scav for an enemy and the player scav takes the raider for one.
    /// </summary>
    public class RaiderPScavHostilityPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<BotsGroup, BotOwner> InitialBot = AccessTools.FieldRefAccess<BotsGroup, BotOwner>("_initialBot");

        public static int Flipped { get; private set; }

        public static string Summary => $"raiders and player scavs made enemies of each other: {Flipped}";

        public static void Clear() => Flipped = 0;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

        private static bool IsRaider(BotOwner bot)
        {
            WildSpawnType role = bot.Profile.Info.Settings.Role;
            return role == WildSpawnType.pmcBot || role == WildSpawnType.exUsec;
        }

        /// <summary>Gluhar and his three kinds of guard (Reserve): the ones EFT keeps friendly to AI scavs and "warns first" at bots.</summary>
        private static bool IsGluharCrew(BotOwner bot)
        {
            WildSpawnType role = bot.Profile.Info.Settings.Role;
            return role == WildSpawnType.bossGluhar || role == WildSpawnType.followerGluharAssault || role == WildSpawnType.followerGluharScout
                   || role == WildSpawnType.followerGluharSecurity || role == WildSpawnType.followerGluharSnipe;
        }

        private static bool IsLiveBot(BotOwner bot)
        {
            Bots.BotKind kind = Bots.BotProfiles.KindOf(bot);
            return kind == Bots.BotKind.Scav || kind == Bots.BotKind.PlayerScav || kind == Bots.BotKind.Pmc;
        }

        [PatchPostfix]
        protected static void PatchPostfix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            if (__result || player == null || !player.IsAI || (!Settings.RaidersHostileToPScavs.Value && !Settings.GluharHostileToAll.Value))
            {
                return;
            }

            try
            {
                BotOwner own = InitialBot(__instance);
                BotOwner other = player.AIData?.BotOwner;
                if (own == null || other == null || own.ProfileId == other.ProfileId)
                {
                    return;
                }

                bool raiders = Settings.RaidersHostileToPScavs.Value;
                bool raiderVsPScav = raiders && IsRaider(own) && Bots.BotProfiles.KindOf(other) == Bots.BotKind.PlayerScav;
                bool pscavVsRaider = raiders && IsRaider(other) && Bots.BotProfiles.KindOf(own) == Bots.BotKind.PlayerScav;
                // Gluhar and his guard against every AI scav, player scav and AI PMC, both ways: EFT keeps them friendly to AI scavs and makes them WARN the rest
                // (the Warn layer: Шапка stood a minute 50 m from Meldonin; a guard shot a player scav only after the warning ran out)
                bool gluhar = Settings.GluharHostileToAll.Value && ((IsGluharCrew(own) && IsLiveBot(other)) || (IsGluharCrew(other) && IsLiveBot(own)));
                if (raiderVsPScav || pscavVsRaider || gluhar)
                {
                    __result = true;
                    Flipped++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("RaiderPScavHostilityPatch", e);
            }
        }
    }
}
