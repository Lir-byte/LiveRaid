using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.HealthSystem;
using HarmonyLib;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// What EFT ties to "your player" when a body gets the controls (GamePlayerOwner, PlayerCameraController) and never unties when that body
    /// dies or is handed back - the vanilla game ends the raid instead. Found by decompiling EFT 0.16.9 (session 34):
    /// - EffectsController subscribes to the body's health (pain, contusion, low health, stun, tremor...) when its camera controller is created
    ///   and only forgets the player, not the events, when it is destroyed: the dead bot's radial blur, motion blur, double vision and
    ///   desaturation stayed on the free camera for the rest of the raid - the "map textures stopped loading" look;
    /// - an optic sight the bot aimed through when it died keeps EFT's second (optic) camera rendering from the corpse's scope;
    /// - BetterAudio.SetProtagonist: the listener player for sound propagation (rooms, stairs, isolated volumes) and the hearing skill;
    /// - headphones / helmet deafness the bot's gear put on the world mixer;
    /// - GamePlayerOwner.MyPlayer (sound occlusion) and CameraManager's hand renderers (lens anti-aliasing).
    /// Everything is put back on the free camera / your dead character, and the bot handed back to its AI gets its own settings again.
    /// </summary>
    internal static class YourPlayerState
    {
        private static readonly FieldInfo AccumulatorsField = AccessTools.Field(typeof(EffectsController), "_effectAccumulators");
        private static readonly FieldInfo EffectsPlayerField = AccessTools.Field(typeof(EffectsController), "player");
        private static readonly FieldInfo BlurField = AccessTools.Field(typeof(EffectsController), "_blur");
        private static readonly FieldInfo MyPlayerField = AccessTools.Field(typeof(GamePlayerOwner), "_myPlayer");
        private static readonly FieldInfo ListenerTarget = AccessTools.Field(typeof(AudioListenerConsistencyManager), "_target");

        /// <summary>A bot's own settings that a take-over changes, for handing it back to its AI.</summary>
        internal sealed class BotSettings
        {
            public object Mask;
            public object Sensitivity;
            public object AimingSensitivity;
            public BasePlayerCulling.EMode? Culling;
            public bool HadOwner;
        }

        public static BotSettings Remember(Player bot, FieldInfo sensitivity, FieldInfo aiming, FieldInfo culling)
        {
            BotSettings saved = new BotSettings { HadOwner = bot.HasGamePlayerOwner };
            Try(() => saved.Mask = bot.ProceduralWeaponAnimation.Mask);
            Try(() => saved.Sensitivity = sensitivity?.GetValue(bot));
            Try(() => saved.AimingSensitivity = aiming?.GetValue(bot));
            Try(() => saved.Culling = (culling?.GetValue(bot) as BasePlayerCulling)?.Mode);
            return saved;
        }

        /// <summary>The bot goes back to its AI alive: its weapon animation, mouse functions, culling and owner flag as they were.</summary>
        public static void GiveBack(Player bot, BotSettings saved, FieldInfo sensitivity, FieldInfo aiming, FieldInfo culling)
        {
            if (bot == null || saved == null)
            {
                return;
            }

            Try(() =>
            {
                if (saved.Mask != null)
                {
                    bot.ProceduralWeaponAnimation.Mask = (EProceduralAnimationMask)saved.Mask;
                }
            });
            Try(() =>
            {
                if (saved.Sensitivity != null)
                {
                    sensitivity?.SetValue(bot, saved.Sensitivity);
                }
            });
            Try(() =>
            {
                if (saved.AimingSensitivity != null)
                {
                    aiming?.SetValue(bot, saved.AimingSensitivity);
                }
            });
            Try(() =>
            {
                if (saved.Culling.HasValue && culling?.GetValue(bot) is BasePlayerCulling c)
                {
                    c.SetMode(saved.Culling.Value);
                }
            });
            Try(() => bot.HasGamePlayerOwner = saved.HadOwner);
            Try(() =>
            {
                if (bot.HealthController?.IsAlive == true)
                {
                    bot.POM?.Off();
                }
            });
        }

        /// <summary>
        /// The controls left <paramref name="body"/> (it died or was handed back): the camera, sound and screen effects belong to the free camera
        /// and your dead character again.
        /// </summary>
        public static void Untie(Player body)
        {
            Player me = SpectatorSession.Original;
            ClearCameraEffects(body);
            CloseOptic();
            Try(() =>
            {
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.SetPlayer(null);
                }
            });

            // Sound: occlusion and propagation are worked out from "your player" again (the loose camera overrides the position, SpectatorListenerPatch)
            if (me != null)
            {
                Try(() => MyPlayerField?.SetValue(null, me));
                Try(() =>
                {
                    if (Singleton<BetterAudio>.Instantiated)
                    {
                        BetterAudio audio = Singleton<BetterAudio>.Instance;
                        audio.UnsubscribeProtagonist();
                        audio.SetProtagonist(me);
                    }
                });

                // The bot's helmet / headphones muffled or compressed the whole world; back to what your character wore
                Try(() => me.UpdatePhonesReally());
            }

            Try(() =>
            {
                if (Singleton<BetterAudio>.Instantiated)
                {
                    Singleton<BetterAudio>.Instance.ResetWorldMixerValues();
                }
            });
            Try(() =>
            {
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.method_7();
                }
            });

            // Light/sound environment and the listener's sound room are looked up again for the camera (they were the bot's)
            CullingProxy.ForgetEnvironment();
            SpectatorCamera.Instance?.ForgetListenerRoom();
            TakeOver.RestoreTextureStreaming();
        }

        /// <summary>
        /// EFT's screen effects (EffectsController) follow the health of the body whose camera controller was made last, and keep its active effects
        /// after that body is gone. Unsubscribe from <paramref name="body"/> and drop every effect still on the camera.
        /// </summary>
        public static void ClearCameraEffects(Player body)
        {
            Try(() =>
            {
                foreach (EffectsController effects in UnityEngine.Object.FindObjectsOfType<EffectsController>())
                {
                    Detach(effects, body);
                    Detach(effects, EffectsPlayerField?.GetValue(effects) as Player);
                    EffectsPlayerField?.SetValue(effects, null);
                    if (AccumulatorsField?.GetValue(effects) is IList list)
                    {
                        List<IHealthEffect> active = new List<IHealthEffect>();
                        foreach (object accumulator in list)
                        {
                            if (accumulator is EffectsController.EffectAccumulator a)
                            {
                                active.AddRange(a.ActiveEffects);
                            }
                        }

                        foreach (IHealthEffect effect in active.Distinct())
                        {
                            Try(() => effects.OnEffectRemoved(effect));
                        }
                    }

                    Try(() => (BlurField?.GetValue(effects) as FastBlur)?.Reset());
                }
            });
            SpectatorCamera.ClearDeathEffects();
        }

        private static void Detach(EffectsController effects, Player player)
        {
            if (player == null || player.HealthController == null)
            {
                return;
            }

            Try(() => player.HealthController.EffectStartedEvent -= effects.OnEffectStarted);
            Try(() => player.HealthController.EffectRemovedEvent -= effects.OnEffectRemoved);
            Try(() => player.HealthController.ApplyDamageEvent -= effects.OnHealthApplyDamage);
            Try(() => player.HealthController.DiedEvent -= effects.OnHealthDied);
            Try(() => player.HealthController.BurnEyesEvent -= effects.OnHealthBurnEyes);
            Try(() => player.HealthController.StimulatorBuffEvent -= effects.OnStimulatorBuff);
            Try(() => player.OnDamageReceived -= effects.OnPlayerDamaged);
            Try(() => player.OnGlassesChanged -= effects.OnGlassesChanged);
        }

        private static readonly FieldInfo GridSwitcherField = AccessTools.Field(typeof(PerfectCullingCrossSceneSampler), "_cullingGridContentSwitcher");
        private static bool _cullingBroken;

        public static void Clear() => _cullingBroken = false;

        /// <summary>
        /// Invisible houses and objects after a take-over (sessions 5-34): every PlayerCameraController that is made calls
        /// CameraManager.SetCameraFromSettings, which DESTROYS the "FPS Camera" and makes a new one. The level's occlusion system (Perfect Culling) found its
        /// camera once, when the level loaded, and keeps the destroyed one: the visibility of houses and objects stayed worked out from the place where the old
        /// camera was when you took a bot over - fly away from it and the buildings vanish. In the vanilla game the camera is made once per raid, so nobody
        /// rebinds it. Every frame of the free camera / a take-over: if the occlusion camera is not EFT's current camera, the sampler, its worker threads and
        /// its grid switcher are moved to the current one.
        /// </summary>
        public static void KeepLevelCulling()
        {
            if (_cullingBroken)
            {
                return;
            }

            try
            {
                PerfectCullingCrossSceneSampler sampler = PerfectCullingCrossSceneSampler.Instance;
                Camera cam = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                if (sampler == null)
                {
                    // The sampler went away with the old camera (CullingGuard kept its grids): the current camera gets one
                    CullingGuard.Revive(cam);
                    return;
                }

                CullingGuard.Alive();
                if (cam == null)
                {
                    return;
                }

                PerfectCullingCamera current = sampler.CullingCamera;
                if (current != null && current.gameObject == cam.gameObject)
                {
                    return;
                }

                PerfectCullingCamera fresh = cam.GetComponent<PerfectCullingCamera>() ?? cam.gameObject.AddComponent<PerfectCullingCamera>();
                sampler.CullingCamera = fresh;
                if (PerfectCullingCrossSceneSampler.WorkJob.JobParams != null)
                {
                    PerfectCullingCrossSceneSampler.WorkJob.JobParams.camera = fresh;
                }

                if (GridSwitcherField?.GetValue(null) is CullingGridContentSwitcher switcher)
                {
                    switcher._cullingCamera = fresh;
                }

                fresh.ObservePosition = cam.transform.position;
                fresh.SetDirty();
                Log.Info($"LR Spectator: the level's occlusion camera was {(current == null ? "a destroyed one" : current.name)} - moved to EFT's current camera " +
                         "(houses and objects are worked out from where you look again)");
            }
            catch (Exception e)
            {
                _cullingBroken = true;
                Log.Warn("LR Spectator: could not rebind the level's occlusion camera, houses may vanish after a take-over: " + e.Message);
            }
        }

        public static string CullingCameraText()
        {
            try
            {
                PerfectCullingCamera c = PerfectCullingCrossSceneSampler.Instance?.CullingCamera;
                Camera cam = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                if (PerfectCullingCrossSceneSampler.Instance == null)
                {
                    return "occlusion: none";
                }

                return c == null ? "occlusion camera DESTROYED"
                    : $"occlusion camera {(cam != null && c.gameObject == cam.gameObject ? "current" : "OTHER (" + c.name + ")")} at {c.ObservePosition:F0}";
            }
            catch (Exception e)
            {
                return "occlusion: ? " + e.Message;
            }
        }

        /// <summary>A scope the body aimed through: EFT's optic camera stops rendering.</summary>
        public static void CloseOptic()
        {
            Try(() =>
            {
                OpticCameraManager optics = CameraManager.Exist ? CameraManager.Instance.OpticCameraManager : null;
                OpticSight sight = optics?.CurrentOpticSight;
                if (sight != null)
                {
                    optics.OnOpticSightDisabled(sight);
                    Log.Info("LR Spectator: the optic camera of the scope the body aimed through is switched off");
                }
            });
        }

        /// <summary>One line on what the screen and sound are tied to, to read in LogOutput when something still looks or sounds wrong.</summary>
        public static void Report(string when)
        {
            try
            {
                const float mb = 1024f * 1024f;
                List<string> cams = Camera.allCameras.Where(c => c != null && c.enabled).Select(c => c.name).ToList();
                OpticCameraManager optics = CameraManager.Exist ? CameraManager.Instance.OpticCameraManager : null;
                List<string> effects = new List<string>();
                foreach (EffectsController controller in UnityEngine.Object.FindObjectsOfType<EffectsController>())
                {
                    if (AccumulatorsField?.GetValue(controller) is IList list)
                    {
                        foreach (object accumulator in list)
                        {
                            if (accumulator is EffectsController.EffectAccumulator a && (a.Enabled || a.ActiveEffects.Count > 0))
                            {
                                effects.Add(a.GetType().Name.Replace("Accumulator", "") + ":" + a.ActiveEffects.Count);
                            }
                        }
                    }
                }

                BetterAudio audio = Singleton<BetterAudio>.Instantiated ? Singleton<BetterAudio>.Instance : null;
                Transform listener = Singleton<AudioListenerConsistencyManager>.Instantiated
                    ? ListenerTarget?.GetValue(Singleton<AudioListenerConsistencyManager>.Instance) as Transform
                    : null;
                Log.Info($"LR Spectator state {when}: textures {Texture.currentTextureMemory / mb:0}/{Texture.desiredTextureMemory / mb:0} MB " +
                         $"(target {Texture.targetTextureMemory / mb:0}, budget {QualitySettings.streamingMipmapsMemoryBudget:0}, " +
                         $"reduction {QualitySettings.streamingMipmapsMaxLevelReduction}, streaming {(QualitySettings.streamingMipmapsActive ? "on" : "OFF")}, " +
                         $"pending {Texture.streamingTexturePendingLoadCount}); cameras [{string.Join(", ", cams)}]" +
                         $"{(optics?.CurrentOpticSight != null ? ", optic ON" : "")}; screen effects [{string.Join(", ", effects)}]; " +
                         $"my player {GamePlayerOwner.MyPlayer?.Profile?.Nickname ?? "none"}, sound listener player {audio?.ListenerPlayer?.Profile?.Nickname ?? "none"}, " +
                         $"listener follows {(listener == null ? "nothing" : listener.name)}; " +
                         $"graphics: VRAM {SystemInfo.graphicsMemorySize} MB, texture limit {QualitySettings.globalTextureMipmapLimit}, " +
                         $"lod bias {QualitySettings.lodBias:0.##}, max lod {QualitySettings.maximumLODLevel}, " +
                         $"camera fov {(Camera.main != null ? Camera.main.fieldOfView : 0f):0} far {(Camera.main != null ? Camera.main.farClipPlane : 0f):0} " +
                         $"at {(Camera.main != null ? Camera.main.transform.position.ToString("F0") : "-")}; {CullingCameraText()}; {SmoothieGuard.Text()}");
            }
            catch (Exception e)
            {
                Log.Detail("debug", "LR Spectator: state report failed: " + e.Message);
            }
        }

        /// <summary>The report now and a few seconds later (texture streaming and faded effects take a moment to settle).</summary>
        public static void ReportSoon(string when)
        {
            Report(when);
            SpectatorCamera.Instance?.StartCoroutine(Later(when));
        }

        private static IEnumerator Later(string when)
        {
            yield return new WaitForSecondsRealtime(8f);
            Report(when + " +8 s");
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                // Where it broke, not only what (1.0.2 logged a bare "Object reference not set" twice and nothing told which step it was)
                string at = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                Log.Detail("debug", $"LR Spectator: {e.GetType().Name} {e.Message} {at}");
            }
        }
    }
}
