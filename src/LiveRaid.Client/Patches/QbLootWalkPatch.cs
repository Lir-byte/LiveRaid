using System;
using System.Reflection;
using HarmonyLib;
using LootingBots.Components;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using SPT.Reflection.Patching;
using EFT;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// A walk to a far body is not cut at 7 seconds. Raid 2026-11-02 15:15: Алик Бритва (a scav, a rat) was "running between the loot, the body and the car":
    /// 34 of his loot jobs (all others together fewer) ended "cut short from outside" every 7 s, 48 times "Looting -> BotObjectiveLayer (0.1 s) -> Looting".
    /// QuestingBots' BotLootingMonitor lets a bot break off its quest for loot only while it is looting, scanning, or has searched for less than
    /// MaxLootScanTime (7 s, LiveRaid's value): a bot WALKING to a thing 20-50 m away is neither (the job starts at the thing) - at the seventh second the monitor
    /// said "not allowed", the quest layer took the bot for a moment, LootingBots stopped the job, and next time it all began again from where the bot stood.
    /// Here the monitor's "allowed to check for loot" stays true while LootingBots has a target for the bot (it walks to it or works on it).
    /// </summary>
    public class QbLootWalkPatch : ModulePatch
    {
        public static int Held { get; private set; }

        public static void Clear() => Held = 0;

        private static readonly MethodInfo BotOwnerGetter = AccessTools.PropertyGetter(typeof(AbstractBotMonitor), "BotOwner");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLootingMonitor), "allowedToCheckForLoot");

        [PatchPostfix]
        protected static void PatchPostfix(BotLootingMonitor __instance, ref bool __result)
        {
            if (__result || !Settings.LootWalk.Value)
            {
                return;
            }

            try
            {
                BotOwner bot = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                LootingBrain brain = bot?.GetPlayer?.GetComponent<LootingBrain>();
                if (brain != null && (brain.HasActiveLootable || brain.LootTaskRunning))
                {
                    __result = true;
                    Held++;
                }
            }
            catch (Exception e)
            {
                Diag.Report("QbLootWalkPatch", e);
            }
        }
    }
}
