using System;
using System.Reflection;
using EFT.CameraControl;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// The level's occlusion system (Perfect Culling) lives ON the camera: the "FPS Camera" carries a PerfectCullingCamera and a PerfectCullingCrossSceneSampler.
    /// A take-over makes EFT build a new camera and destroy the old one; the destroyed sampler's OnDestroy runs Shutdown, which stops the work threads and
    /// DISPOSES the level-wide statics that only the level load makes (the grid's content switcher, the visibility sampler), puts
    /// ScreenDistanceSwitcher.AllowRuntimeUpdate back to false and nulls the instance. A sampler that starts on the new camera only re-makes a few native
    /// arrays - the content of the map is never switched again: houses and objects stayed as they were when you took the bot over, "textures stopped loading"
    /// (the state line after leaving a bot said "occlusion: none"). In the vanilla game the camera is made once per raid, so nobody noticed.
    /// While LiveRaid itself rebuilds the camera (<see cref="MarkRebuild"/>) the Shutdown of the sampler that goes away is skipped, so the statics and the
    /// threads live on, and the new camera gets a sampler (a copy of the old one's settings) if its prefab brought none; YourPlayerState.KeepLevelCulling
    /// points the grid switcher and the work job at the new camera.
    /// </summary>
    internal static class CullingGuard
    {
        private static int _until = -1;
        private static int _nextTry;
        private static int _tries;

        /// <summary>The sampler whose Shutdown was skipped: its settings go to the new one.</summary>
        public static PerfectCullingCrossSceneSampler Orphan { get; private set; }

        public static int Kept { get; private set; }
        public static int Revived { get; private set; }

        public static string Summary => $"occlusion samplers kept through a camera rebuild {Kept}, new ones put on the camera {Revived}";

        public static void Clear()
        {
            _until = -1;
            _nextTry = 0;
            _tries = 0;
            Orphan = null;
            Kept = 0;
            Revived = 0;
        }

        /// <summary>Called right before LiveRaid makes EFT rebuild the camera (take-over, leaving a bot): objects destroyed this frame are the old camera's.</summary>
        public static void MarkRebuild() => _until = Time.frameCount + 1;

        public static bool ShouldKeep(PerfectCullingCrossSceneSampler sampler)
        {
            if (!Settings.KeepCulling.Value || sampler == null || Time.frameCount > _until)
            {
                return false;
            }

            Camera now = CameraManager.Exist ? CameraManager.Instance.Camera : null;
            // The camera that stays is not the one this sampler sits on
            return now != null && now.gameObject != sampler.gameObject;
        }

        public static void Skipped(PerfectCullingCrossSceneSampler sampler)
        {
            Orphan = sampler;
            Kept++;
            _tries = 0;
            Log.Info("LR Spectator: the camera was rebuilt - the level's occlusion sampler on the old one is not shut down (its threads and grids stay), the new camera gets one");
        }

        /// <summary>No sampler is alive after a rebuild: put one on the current camera (the old one's settings copied) unless the camera's prefab brought its own.</summary>
        public static void Revive(Camera cam)
        {
            if (Orphan == null || cam == null || Time.frameCount < _nextTry || _tries >= 3)
            {
                return;
            }

            _nextTry = Time.frameCount + 20;
            _tries++;
            try
            {
                PerfectCullingCamera culling = cam.GetComponent<PerfectCullingCamera>() ?? cam.gameObject.AddComponent<PerfectCullingCamera>();
                PerfectCullingCrossSceneSampler existing = cam.GetComponent<PerfectCullingCrossSceneSampler>();
                if (existing != null)
                {
                    // Its own Start makes it the instance; only the camera it should read
                    if (existing.CullingCamera == null)
                    {
                        existing.CullingCamera = culling;
                    }

                    return;
                }

                PerfectCullingCrossSceneSampler fresh = cam.gameObject.AddComponent<PerfectCullingCrossSceneSampler>();
                foreach (FieldInfo field in typeof(PerfectCullingCrossSceneSampler).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.IsInitOnly || field.IsLiteral)
                    {
                        continue;
                    }

                    object value = field.GetValue(Orphan);
                    if (value is UnityEngine.Object unity && unity == null)
                    {
                        continue;
                    }

                    field.SetValue(fresh, value);
                }

                fresh.CullingCamera = culling;
                Revived++;
                Log.Info("LR Spectator: the new camera had no occlusion sampler - one was put on it with the old one's settings");
            }
            catch (Exception e)
            {
                _tries = 3;
                Log.Warn("LR Spectator: could not give the new camera an occlusion sampler: " + e.Message);
            }
        }

        /// <summary>A sampler is alive again: nothing left to bring back.</summary>
        public static void Alive()
        {
            if (Orphan != null && PerfectCullingCrossSceneSampler.Instance != null)
            {
                Orphan = null;
            }
        }
    }
}
