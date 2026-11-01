using System.Reflection;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.Preset.Shared.GlobalSettings.Categories.General;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// "Simplify distant bots": SAIN already sorts bots into distance tiers (Far / VeryFar / Narnia) from the nearest human.
    /// Out of combat, far bots run SAIN's sensing and decision tick only every few frames. Movement, QuestingBots
    /// objectives and looting are not throttled, so far bots keep travelling and doing things - nobody sleeps.
    /// Bots with an enemy always tick every frame, so fights far away still play out properly.
    /// </summary>
    public class SainLodPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.ManualUpdate));

        [PatchPrefix]
        protected static bool PatchPrefix(BotComponent __instance)
        {
            try
            {
                return RunPrefix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainLodPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(BotComponent __instance)
        {
            // A bot SAIN has not finished setting up: leave it alone (its GoalEnemy would throw)
            if (!Settings.SimplifyDistantBots.Value || __instance == null || __instance.EnemyController == null)
            {
                return true;
            }

            // After your death nobody is "near a human" any more: SAIN put every bot in the far tiers and they all thought slowly
            // (half blind to each other) while you watched. Full speed then
            if (Spectator.SpectatorSession.OriginalDead || Spectator.SpectatorSession.Observer)
            {
                return true;
            }

            int every;
            switch (__instance.CurrentAILimit)
            {
                case AILimitSetting.Far: every = 2; break;
                case AILimitSetting.VeryFar: every = 3; break;
                case AILimitSetting.Narnia: every = 5; break;
                default: return true;
            }

            if (__instance.GoalEnemy != null || __instance.BotOwner?.Memory?.GoalEnemy != null)
            {
                return true;
            }

            // Stagger bots so the skipped work is spread evenly across frames
            int slot = (__instance.GetInstanceID() & 0x7fffffff) % every;
            return Time.frameCount % every == slot;
        }
    }

    public static class LodSettings
    {
        /// <summary>
        /// SAIN's AI-vs-AI limits are a separate choice from thinking less often: by default far bots keep full vision and hearing
        /// of each other (online, squads fight anywhere on the map, not only near you).
        /// </summary>
        public static void ApplyToSain()
        {
            AILimitSettings limit = GlobalSettingsClass.Instance?.General?.AILimit;
            if (limit == null)
            {
                return;
            }

            bool limitSenses = !Settings.FarBotsFullSenses.Value;
            limit.LimitAIvsAIGlobal = limitSenses;
            limit.LimitAIvsAIVision = limitSenses;
            limit.LimitAIvsAIHearing = limitSenses;
        }
    }
}
