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

            PerfProbe.Frame();

            int every;
            if (Spectator.SpectatorSession.OriginalDead || Spectator.SpectatorSession.Observer)
            {
                // After your death nobody is "near a human" any more: SAIN put every bot in the far tiers and they all thought slowly
                // (half blind to each other) while you watched. Here the tiers are those of the camera: a bot far from where you look thinks less often,
                // the ones round the camera at full speed (F12 -> 5 "Tiers by the camera")
                if (!Settings.LodByCamera.Value)
                {
                    return true;
                }

                every = PerfProbe.CameraTier(__instance.BotOwner);
            }
            else
            {
                switch (__instance.CurrentAILimit)
                {
                    case AILimitSetting.Far: every = 2; break;
                    case AILimitSetting.VeryFar: every = 3; break;
                    case AILimitSetting.Narnia: every = 5; break;
                    default: return true;
                }

                every += PerfProbe.Pressure;
            }

            if (every <= 1)
            {
                return true;
            }

            if (__instance.GoalEnemy != null || __instance.BotOwner?.Memory?.GoalEnemy != null)
            {
                return true;
            }

            // Stagger bots so the skipped work is spread evenly across frames
            int slot = (__instance.GetInstanceID() & 0x7fffffff) % every;
            bool run = Time.frameCount % every == slot;
            if (!run)
            {
                PerfProbe.Skipped++;
            }

            return run;
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
