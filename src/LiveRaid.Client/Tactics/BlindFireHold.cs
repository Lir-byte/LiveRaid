using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.Mover;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Shooting over cover held steady (F12 -> 9 "Hold the blind-fire pose"). A player's tester (2026-11-02): "reider123 shot from behind cover and kept
    /// spamming the raise-the-gun-over-the-corner key, switching the pose on for a split second and off again" - the "Somali" blind fire.
    /// Not LiveRaid's: SAIN's BlindFireController decides it every 0.2 s from a few raycasts (is the line to the last known place blocked by 5 m of wall:
    /// then overhead = 1, else a side shot = -1, else 0) and from CheckAllowBlindFire (enemy seen, bot not moving, in cover, can shoot...). Any of them
    /// flips with the sway of the animation or the enemy's flickering visibility; "0" resets the pose at once, and the next check sets it again with no
    /// delay at all (noLastBlindFire) - on, off, on, off at the rate of the checks.
    /// Here (a) the raycast answer is kept for 2 s once it is non-zero (a flip from up to the side and back, or to nothing, is held), and no new pose
    /// starts within 0.8 s of dropping the last one; (b) SAIN's reset is held back for 1.5 s after the pose was set unless the bot moves, has no round
    /// left or has just been hit. A pose that is really over (the enemy gone, the bot left cover) still ends: the holds are seconds, not minutes.
    /// </summary>
    public static class BlindFireHold
    {
        private class State
        {
            public int Last;
            public float ChangedAt = -99f;
            public float SetAt = -99f;
            public bool On;
        }

        private const float HoldOn = 2f;
        private const float HoldOff = 0.8f;
        private const float HoldReset = 1.5f;

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();

        public static int Held { get; private set; }
        public static int ResetsHeld { get; private set; }
        public static int Poses { get; private set; }

        public static string Summary => $"blind-fire pose: started {Poses} times, answers held steady {Held}, resets held back {ResetsHeld}";

        public static void Clear()
        {
            _states.Clear();
            Held = ResetsHeld = Poses = 0;
        }

        private static State Of(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            return s;
        }

        internal static void Stabilise(BlindFireController c, ref int result)
        {
            BotOwner bot = c.BotOwner;
            if (!Settings.BlindFireHold.Value || bot == null)
            {
                return;
            }

            State s = Of(bot);
            float now = Time.time;
            if (result == s.Last)
            {
                return;
            }

            if (s.Last != 0 && now - s.ChangedAt < HoldOn)
            {
                // Up, then the side or nothing a moment later: the same pose still stands
                result = s.Last;
                Held++;
                return;
            }

            if (s.Last == 0 && result != 0 && now - s.ChangedAt < HoldOff)
            {
                result = 0;
                Held++;
                return;
            }

            s.Last = result;
            s.ChangedAt = now;
        }

        internal static void WasSet(BlindFireController c, int value)
        {
            BotOwner bot = c.BotOwner;
            if (bot == null)
            {
                return;
            }

            State s = Of(bot);
            if (value != 0)
            {
                if (!s.On)
                {
                    Poses++;
                }

                s.On = true;
                s.SetAt = Time.time;
            }
            else
            {
                s.On = false;
            }
        }

        /// <summary>True when SAIN's reset of the pose should be skipped this time.</summary>
        internal static bool KeepPose(BlindFireController c)
        {
            BotOwner bot = c.BotOwner;
            if (!Settings.BlindFireHold.Value || bot == null || c.ActiveBlindFireSetting == 0)
            {
                return false;
            }

            State s = Of(bot);
            if (Time.time - s.SetAt > HoldReset)
            {
                return false;
            }

            if (c.Bot.Mover.Moving || bot.WeaponManager?.HaveBullets != true || HitReaction.SinceHit(bot) < 1f)
            {
                return false;
            }

            ResetsHeld++;
            return true;
        }
    }

    /// <summary>The raycast answer of SAIN's blind-fire check is held for a moment.</summary>
    public class SainBlindFireStablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BlindFireController), "checkBlindFire");

        [PatchPostfix]
        protected static void PatchPostfix(BlindFireController __instance, ref int __result)
        {
            try
            {
                BlindFireHold.Stabilise(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainBlindFireStablePatch", e);
            }
        }
    }

    /// <summary>SAIN's reset of the pose (the cause of the on-off-on flicker) is held back for a moment.</summary>
    public class SainBlindFireResetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BlindFireController), nameof(BlindFireController.ResetBlindFire));

        [PatchPrefix]
        protected static bool PatchPrefix(BlindFireController __instance)
        {
            try
            {
                return !BlindFireHold.KeepPose(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainBlindFireResetPatch", e);
                return true;
            }
        }
    }

    public class SainBlindFireSetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BlindFireController), nameof(BlindFireController.SetBlindFire));

        [PatchPostfix]
        protected static void PatchPostfix(BlindFireController __instance, int value)
        {
            try
            {
                BlindFireHold.WasSet(__instance, value);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainBlindFireSetPatch", e);
            }
        }
    }
}
