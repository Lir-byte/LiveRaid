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
}
