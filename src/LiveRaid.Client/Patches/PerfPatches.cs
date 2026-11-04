using System;
using System.Diagnostics;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using SAIN.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// What a raid costs the CPU, and the one saving that does not touch what a bot does (F12 -> 5 "Tiers by the camera", "Think less when the frame rate is low").
    ///  - Smoothie (animations, culling, physics) and Unity's own threads take what can be spread over cores: Unity's API, EFT's bot brains, SAIN, QuestingBots
    ///    and LootingBots all run on the main thread and cannot be moved off it. What can be done is to ask the main thread for less of the same work.
    ///  - SAIN's tick of a bot (senses, enemy list, decisions) is the biggest piece of bot thinking and can be given a lower rate when nothing happens near
    ///    the bot: no enemy known, far from the person watching. In a raid with a player SAIN's own distance tiers do it (see <see cref="SainLodPatch"/>);
    ///    for an observer or a dead player there are no humans for those tiers, so the tiers are measured from the camera instead.
    ///  - When the smoothed frame rate drops below 38 / 25 fps the far tiers think one / two frames less often still, and come back above 46 / 33 fps.
    /// A bot with an enemy, and every bot near the camera, ticks every frame. Movement, quests, looting, exits are not throttled.
    /// The measured numbers (frame, SAIN tick, skipped ticks) go to the log once a minute as "LR Perf:" and into the raid summary.
    /// </summary>
    public static class PerfProbe
    {
        private static int _frame = -1;
        private static float _ema = 0.016f;
        private static bool _haveView;
        private static Vector3 _view;
        private static float _nextLine;
        private static double _frameSeconds;
        private static int _frames;
        private static long _sainTicks;
        private static int _sainFrames;
        private static long _winSainTicks;
        private static double _winFrameSeconds;
        private static int _winFrames;
        private static long _winSkipped;

        /// <summary>Frames added to the rate of the far tiers by a low frame rate (0, 1 or 2).</summary>
        public static int Pressure { get; private set; }

        public static long Skipped { get; set; }
        public static int PressureRaised { get; private set; }

        public static string Summary
        {
            get
            {
                if (_frames == 0)
                {
                    return "perf: no frames measured";
                }

                double frameMs = _frameSeconds * 1000.0 / _frames;
                double sainMs = _sainFrames > 0 ? _sainTicks * 1000.0 / Stopwatch.Frequency / _sainFrames : 0.0;
                return $"perf: frame {frameMs:0.0} ms on average, SAIN tick {sainMs:0.0} ms ({(frameMs > 0 ? sainMs / frameMs * 100.0 : 0):0} % of the frame), " +
                       $"far bots' thinking skipped {Skipped} times, low frame rate pushed it up {PressureRaised} times";
            }
        }

        public static void Clear()
        {
            _frame = -1;
            _ema = 0.016f;
            _haveView = false;
            _nextLine = 0f;
            _frameSeconds = 0.0;
            _frames = 0;
            _sainTicks = 0;
            _sainFrames = 0;
            _winSainTicks = 0;
            _winFrameSeconds = 0.0;
            _winFrames = 0;
            _winSkipped = 0;
            Pressure = 0;
            PressureRaised = 0;
            Skipped = 0;
        }

        public static void SainTick(long ticks)
        {
            _sainTicks += ticks;
            _winSainTicks += ticks;
            _sainFrames++;
        }

        /// <summary>Once per frame: the smoothed frame time, the pressure step, the position of the camera.</summary>
        public static void Frame()
        {
            if (_frame == Time.frameCount)
            {
                return;
            }

            _frame = Time.frameCount;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            _ema += (dt - _ema) * 0.02f;
            _frameSeconds += dt;
            _frames++;
            _winFrameSeconds += dt;
            _winFrames++;

            int want = Pressure;
            float fps = 1f / Mathf.Max(0.001f, _ema);
            if (!Settings.LodAdaptive.Value)
            {
                want = 0;
            }
            else if (fps < 25f)
            {
                want = 2;
            }
            else if (fps < 38f)
            {
                want = Pressure == 2 && fps < 33f ? 2 : 1;
            }
            else if (fps > 46f)
            {
                want = 0;
            }

            if (want > Pressure)
            {
                PressureRaised++;
            }

            Pressure = want;

            if (CameraManager.Exist)
            {
                Camera cam = CameraManager.Instance.Camera;
                if (cam != null)
                {
                    _view = cam.transform.position;
                    _haveView = true;
                }
            }

            float now = Time.time;
            if (now >= _nextLine)
            {
                if (_nextLine > 0f && _winFrames > 0)
                {
                    double frameMs = _winFrameSeconds * 1000.0 / _winFrames;
                    double sainMs = _winSainTicks * 1000.0 / Stopwatch.Frequency / _winFrames;
                    Log.Info($"LR Perf: last minute {1000.0 / frameMs:0} fps ({frameMs:0.0} ms a frame), SAIN tick {sainMs:0.0} ms a frame ({sainMs / frameMs * 100.0:0} %), " +
                                             $"far bots' ticks skipped {Skipped - _winSkipped}, step {Pressure}");
                }

                _nextLine = now + 60f;
                _winSainTicks = 0;
                _winFrameSeconds = 0.0;
                _winFrames = 0;
                _winSkipped = Skipped;
            }
        }

        /// <summary>The rate for a bot in an observer's raid: 1 = every frame, 2-4 = every 2nd-4th frame, by its distance from the camera.</summary>
        public static int CameraTier(BotOwner bot)
        {
            if (!_haveView || bot == null)
            {
                return 1;
            }

            float near = Settings.LodNear.Value * (Pressure == 2 ? 0.55f : Pressure == 1 ? 0.75f : 1f);
            float d = Vector3.Distance(bot.Position, _view);
            return d < near ? 1 : d < near * 2f ? 2 : d < near * 3.3f ? 3 : 4;
        }
    }

    /// <summary>The time SAIN spends on all its bots in a frame (<c>BotManagerComponent.ManualUpdate</c>).</summary>
    public class PerfSainTickPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotManagerComponent), "ManualUpdate", new[] { typeof(float), typeof(float) });

        [PatchPrefix]
        protected static void PatchPrefix(ref long __state) => __state = Stopwatch.GetTimestamp();

        [PatchPostfix]
        protected static void PatchPostfix(long __state)
        {
            try
            {
                PerfProbe.SainTick(Stopwatch.GetTimestamp() - __state);
                PerfProbe.Frame();
            }
            catch (Exception e)
            {
                Diag.Report("PerfSainTickPatch", e);
            }
        }
    }
}
