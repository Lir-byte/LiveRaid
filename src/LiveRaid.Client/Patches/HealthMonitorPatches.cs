using System.Reflection;
using EFT;
using HarmonyLib;
using QuestingBots.BotLogic.BotMonitor;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots' "needs to heal" only when the bot has something to heal with (F12 -> 9 "Heal at the first quiet moment").
    /// QuestingBots stops a bot's quest ("StopToHeal") for any wound (FirstAid.Have2Do / SurgicalKit.HaveWork), but its recovery layer treats only with
    /// medicine in the bag - a bot with a wound and no medkit stood there for good (raid 2026-10-22 15:57: ScottieKnowz 13 minutes at 54-58 % hp in
    /// "StopToHeal", in three stretches up to 3.5 min). A wounded player without meds goes on.
    /// Session 16: bandaging is LiveRaid's heal layer's job alone (it starts at the first quiet moment and is not taken away); QuestingBots' stop for it was a
    /// second healer on the same bot (Heal &lt;-&gt; BotRecovery ping-pong, two callers of the same first aid). QuestingBots still stops a bot for surgery
    /// (a destroyed limb) when it has a kit - LiveRaid's layer does not do surgery.
    /// </summary>
    public class QbNeedsToHealPatch : ModulePatch
    {
        private static readonly MethodInfo BotOwnerGetter = AccessTools.PropertyGetter(typeof(AbstractBotMonitor), "BotOwner");

        public static int Ignored { get; private set; }

        public static void ClearCounters() => Ignored = 0;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(QuestingBots.BotLogic.BotMonitor.Monitors.BotHealthMonitor), "needsToHeal");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref bool __result)
        {
            if (!__result || !Settings.HealFirst.Value)
            {
                return;
            }

            try
            {
                BotOwner bot = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                BotMedecine med = bot?.Medecine;
                if (med?.FirstAid == null || med.SurgicalKit == null || med.Using)
                {
                    return;
                }

                bool canTreat = med.SurgicalKit.HaveWork && med.SurgicalKit.HaveSmth2Use;
                if (!canTreat)
                {
                    __result = false;
                    Ignored++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbNeedsToHealPatch", e);
            }
        }
    }
}
