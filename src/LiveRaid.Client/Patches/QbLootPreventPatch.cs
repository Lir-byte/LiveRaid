using System.Reflection;
using EFT;
using HarmonyLib;
using LootingBots.Components;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots' "keep the bot from looting" (its hearing monitor the moment the bot turns suspicious, its "no looting" after a quest step) goes
    /// straight to LootingBots' StopLooting - a loot job under way was cut with no word in the log. Raid 2026-10-30 23:28: half of the cut body searches had
    /// no reason of LiveRaid's behind them; Kolka Pankreatit went through the same body six times, cut after 4-6 s each time, lying and crouching alike.
    /// While a job runs, LiveRaid's own danger check (LootDanger: someone heard or seen close by, shooting) decides whether it stops; with no job
    /// running QuestingBots' call goes through as before (it only holds off the next scan). F12 -> 1 "Harmonize behaviour".
    /// </summary>
    public class QbLootPreventPatch : ModulePatch
    {
        private static readonly MethodInfo OwnerGetter = AccessTools.PropertyGetter(typeof(AbstractBotMonitor), "BotOwner");

        public static int Kept { get; private set; }

        public static void Clear() => Kept = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLootingMonitor), nameof(BotLootingMonitor.TryPreventBotFromLooting));

        [PatchPrefix]
        protected static bool PatchPrefix(BotLootingMonitor __instance, ref bool __result)
        {
            if (!Settings.Harmonize.Value)
            {
                return true;
            }

            try
            {
                BotOwner bot = OwnerGetter?.Invoke(__instance, null) as BotOwner;
                LootingBrain brain = bot?.GetPlayer?.GetComponent<LootingBrain>();
                if (brain == null || !brain.LootTaskRunning || Tactics.LootDanger.Why(bot) != null)
                {
                    return true;
                }

                Kept++;
                __result = false;
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("QbLootPreventPatch", e);
                return true;
            }
        }
    }
}
