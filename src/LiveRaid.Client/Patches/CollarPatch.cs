using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// A loose collar (F12 -> 8 "A loose collar", 1.3.0). QuestingBots' leader stops the instant a follower is past the limit (<c>BotQuestingMonitor.shouldWaitForFollowers</c>), waits
    /// until all are back within the range, then walks on - and a follower that lags by a step every few seconds made the leader run up to the limit, stop, turn to him and go again.
    /// The wait now begins only when the condition has held for 3.5 s in a row (a follower behind a door or a turn is not a straggler); a leader that is already waiting is left to
    /// QuestingBots' own end of the wait. Together with the wider ranges (ModControl: 6-22 m / 24 m / 42 m) the leader keeps going and the followers have room for a will of their own.
    /// </summary>
    public class QbFollowerWaitPatch : ModulePatch
    {
        private const float Debounce = 3.5f;
        private static readonly ConditionalWeakTable<object, StrongBox<float>> Since = new ConditionalWeakTable<object, StrongBox<float>>();

        public static int Debounced { get; private set; }
        public static int Waits { get; private set; }

        public static string Summary => $"leader waits for followers: held back by the debounce {Debounced} ticks, let through {Waits}";

        public static void ClearCounters() => Debounced = Waits = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotQuestingMonitor), "shouldWaitForFollowers");

        [PatchPostfix]
        protected static void PatchPostfix(BotQuestingMonitor __instance, ref bool __result)
        {
            try
            {
                if (!Settings.Enabled.Value || !Settings.LooseCollar.Value)
                {
                    return;
                }

                if (!__result)
                {
                    Since.Remove(__instance);
                    return;
                }

                // Already waiting: QuestingBots ends the wait itself (all within the range)
                if (__instance.ShouldWaitForFollowers)
                {
                    return;
                }

                if (!Since.TryGetValue(__instance, out StrongBox<float> box))
                {
                    Since.Add(__instance, new StrongBox<float>(Time.time));
                    Debounced++;
                    __result = false;
                    return;
                }

                if (Time.time - box.Value < Debounce)
                {
                    Debounced++;
                    __result = false;
                    return;
                }

                Waits++;
            }
            catch (System.Exception e)
            {
                Diag.Report("QbFollowerWaitPatch", e);
            }
        }
    }
}
