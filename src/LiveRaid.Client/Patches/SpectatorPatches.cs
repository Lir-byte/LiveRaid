using System;
using System.Reflection;
using EFT;
using EFT.Achievements;
using EFT.Prestige;
using EFT.Quests;
using HarmonyLib;
using Koenigz.PerfectCulling.EFT;
using LiveRaid.Client.Spectator;
using SAIN.Components;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Your death: EFT calls InitiateGameStopping (death panel, Stop(Killed) in 5 s) from the health controller's death event,
    /// after it already stopped your input. With "Free camera after death" LiveRaid skips it and keeps the raid running.
    /// </summary>
    public class SpectatorDeathPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(BaseLocalGame<EftGamePlayerOwner>.InitiateGameStopping));

        [PatchPrefix]
        protected static bool PatchPrefix(BaseLocalGame<EftGamePlayerOwner> __instance)
        {
            try
            {
                return !SpectatorSession.OnOriginalDied(__instance);
            }
            catch (Exception e)
            {
                // Anything odd: the usual death screen, never a raid that cannot end
                Diag.Report("SpectatorDeathPatch", e);
                return true;
            }
        }
    }

    /// <summary>
    /// EFT works out how muffled every sound is from your player's body (GamePlayerOwner.MyPlayer); with the camera loose after your
    /// death that was the corpse, so sounds next to the camera came through walls. While the camera is loose it is the camera.
    /// </summary>
    public class SpectatorListenerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(Audio.SpatialSystem.SpatialAudioOccluder), "ListenerPos");

        [PatchPrefix]
        protected static bool PatchPrefix(ref UnityEngine.Vector3 __result)
        {
            if (SpectatorSession.State == SpectatorState.Off)
            {
                return true;
            }

            try
            {
                if (SpectatorCamera.ListenerOverride(out UnityEngine.Vector3 position))
                {
                    __result = position;
                    return false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("SpectatorListenerPatch", e);
            }

            return true;
        }
    }

    /// <summary>The black "closing eyes" fade and the death blur: not when the raid goes on after this death.</summary>
    public class SpectatorDeathEffectsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsController), nameof(EffectsController.OnHealthDied));

        [PatchPrefix]
        protected static bool PatchPrefix()
        {
            try
            {
                if (SpectatorSession.SuppressDeathEffects())
                {
                    Log.Detail("debug", "LR Spectator: death fade and blur skipped (the raid goes on)");
                    return false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("SpectatorDeathEffectsPatch", e);
            }

            return true;
        }
    }

    /// <summary>
    /// After your death every end of the raid is that death: the timer (MIA), the taken bot's exit, leaving from the menu.
    /// Runs before SPT's own Stop prefix, which reads the exit status for the end-of-raid profile.
    /// </summary>
    public class SpectatorStopPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocalGame), nameof(LocalGame.Stop));

        [PatchPrefix]
        [HarmonyPriority(Priority.First)]
        protected static void PatchPrefix(LocalGame __instance, string profileId, ref ExitStatus exitStatus, ref string exitName)
        {
            try
            {
                if (__instance.Profile == null || profileId != __instance.Profile.Id)
                {
                    return;
                }

                if (SpectatorSession.Observer)
                {
                    // Observer raid: every end (End raid key, the timer, leaving from the menu, a taken bot's exit) is a run-through
                    // that the server mod turns into "nothing happened"
                    if (exitStatus != ExitStatus.Runner || exitName != ObserverMode.ExitTag)
                    {
                        Log.Info($"LR Spectator: observer raid end '{exitStatus}' recorded as a run-through that does not count");
                    }

                    exitStatus = ExitStatus.Runner;
                    exitName = ObserverMode.ExitTag;
                    SpectatorSession.OnRaidStopping();
                    return;
                }

                if (!SpectatorSession.OriginalDead)
                {
                    return;
                }

                if (exitStatus != ExitStatus.Killed)
                {
                    Log.Info($"LR Spectator: raid end '{exitStatus}'{(exitName != null ? " at " + exitName : "")} recorded as the original death");
                    exitStatus = ExitStatus.Killed;
                    exitName = null;
                }

                SpectatorSession.OnRaidStopping();
            }
            catch (Exception e)
            {
                Diag.Report("SpectatorStopPatch", e);
            }
        }
    }

    /// <summary>A taken-over bot keeps BotState Active (others must still see it), so its own update has to be skipped by hand.</summary>
    public class TakenBotUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner __instance) => TakeOver.Bot == null || !TakeOver.IsTaken(__instance);
    }

    public class TakenBotFixedUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotOwner), nameof(BotOwner.FixedUpdate));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner __instance) => TakeOver.Bot == null || !TakeOver.IsTaken(__instance);
    }

    /// <summary>SAIN ticks its bot classes (vision, steering, sprint, lean, decisions) from its own manager, not from BotOwner.</summary>
    public class TakenBotSainPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.ManualUpdate));

        [PatchPrefix]
        protected static bool PatchPrefix(BotComponent __instance) => TakeOver.Bot == null || !TakeOver.IsTaken(__instance?.BotOwner);
    }

    /// <summary>
    /// A bot has no quest, achievement and prestige controllers (EFT creates them only for you); the inventory screen expects them.
    /// Yours are shown instead - only shown: the quest controller counts your own inventory and character, not the bot's.
    /// If the screen still fails for a bot, the error stays here and the game goes on.
    /// </summary>
    public class TakenBotInventoryPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.ShowInventoryScreen));

        [PatchPrefix]
        protected static void PatchPrefix(EftGamePlayerOwner __instance, ref QuestController questController,
            ref AchievementsController achievementsController, ref PrestigeController prestigeController)
        {
            try
            {
                Player me = SpectatorSession.Original;
                if (me == null || !TakeOver.IsTaken(__instance.Player))
                {
                    return;
                }

                questController = questController ?? me.QuestController;
                achievementsController = achievementsController ?? me.AchievementsController;
                prestigeController = prestigeController ?? me.PrestigeController;
            }
            catch (Exception e)
            {
                Diag.Report("TakenBotInventoryPatch", e);
            }
        }

        [PatchFinalizer]
        protected static Exception PatchFinalizer(Exception __exception, EftGamePlayerOwner __instance)
        {
            if (__exception == null || !TakeOver.IsTaken(__instance?.Player))
            {
                return __exception;
            }

            Log.Error("LR Spectator: the inventory of the taken-over bot could not be opened: " + __exception);
            return null;
        }
    }

    /// <summary>
    /// The shutdown of the level's occlusion sampler that goes away with a camera LiveRaid made EFT rebuild is skipped (see <see cref="CullingGuard"/>):
    /// it would dispose the level-wide grids that only the level load makes.
    /// </summary>
    public class SamplerShutdownPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PerfectCullingCrossSceneSampler), nameof(PerfectCullingCrossSceneSampler.Shutdown));

        [PatchPrefix]
        protected static bool PatchPrefix(PerfectCullingCrossSceneSampler __instance)
        {
            try
            {
                if (CullingGuard.ShouldKeep(__instance))
                {
                    CullingGuard.Skipped(__instance);
                    return false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("SamplerShutdownPatch", e);
            }

            return true;
        }
    }

    /// <summary>
    /// A grenade in the hands of a taken-over bot: the animation played and no grenade flew. EFT's throw (BaseGrenadeHandsController.Throw) asks
    /// "is this an AI bot in the Active state" - the taken bot is (it must stay Active so SAIN and the others still see it) - and then takes the throw
    /// force and direction from the bot brain's BotGrenadeController.AIGreanageThrowData, which nobody fills for a body you play: an exception, caught in
    /// the same method, and a silent return. Here the throw of a taken-over bot goes the way of your own character (force from the game's setting,
    /// direction down the hand, strength and stamina of the body).
    /// </summary>
    public class TakenBotGrenadePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.BaseGrenadeHandsController), nameof(Player.BaseGrenadeHandsController.Throw));

        [PatchPrefix]
        protected static bool PatchPrefix(Player.BaseGrenadeHandsController __instance, float timeSinceSafetyLevelRemoved, bool low)
        {
            try
            {
                Player player = __instance?._player;
                if (player == null || TakeOver.Current == null || player != TakeOver.Current)
                {
                    return true;
                }

                if (player.HealthController == null || !player.HealthController.IsAlive)
                {
                    return false;
                }

                float lowHigh = low ? 0.66f : 1f + (float)player.Skills.StrengthBuffThrowDistanceInc;
                float power = EFTHardSettings.Instance.GrenadeForce;
                UnityEngine.Transform drop = __instance._dropTransform;
                UnityEngine.Vector3 direction;
                float stamina = 1f;
                try
                {
                    stamina = player.Physical.HandsStamina.NormalValue;
                }
                catch
                {
                    // a bot body without hand stamina: a fresh hand
                }

                if (!player.Skills.ThrowingEliteBuff)
                {
                    UnityEngine.Vector3 shake = UnityEngine.Mathf.Clamp01(0.5f - stamina) * UnityEngine.Random.onUnitSphere;
                    direction = (-drop.up * 5f + shake).normalized;
                    lowHigh *= UnityEngine.Mathf.Lerp(0.4f, 1f, stamina + 0.5f);
                }
                else
                {
                    direction = -drop.up;
                }

                __instance.ThrowGrenadeInner(null, timeSinceSafetyLevelRemoved, lowHigh, direction, power, low, withVelocity: true);
                Log.Detail("debug", $"LR Spectator: {player.Profile?.Nickname} throws a grenade as you (force {power * lowHigh:0.#})");
                return false;
            }
            catch (Exception e)
            {
                Diag.Report("TakenBotGrenadePatch", e);
                return true;
            }
        }
    }
}
