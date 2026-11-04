using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// A bot going through a body or a container crouches beside it and "looks at the item" (LootingBots: Steering.LookToPoint(item, 180 deg/s) every
    /// 0.2 s). The item is within a step, often right under the bot's weapon root, and the direction from there to the item swings round with every
    /// sway of the animation: the crouching bot turned on the spot. Here, while LootingBots makes that call and the item is nearer than 1.2 m, the bot
    /// keeps the way it faced when it arrived; farther away the look-at works as before. Online the character squats and does not spin.
    /// </summary>
    internal static class LootFacing
    {
        public static BotOwner Bot;
        public static int Frame = -1;
        public static readonly Dictionary<string, Vector3> Held = new Dictionary<string, Vector3>();
        public static int Steadied;

        public static void Clear()
        {
            Bot = null;
            Frame = -1;
            Held.Clear();
            Steadied = 0;
        }
    }

    /// <summary>Marks the frame in which LootingBots' own looting logic (not SAIN, not LiveRaid) is turning the bot towards the loot.</summary>
    public class LbTryLootPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.LootingLogic"), "TryLoot");

        [PatchPrefix]
        protected static void PatchPrefix(object __instance)
        {
            try
            {
                LootFacing.Bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                LootFacing.Frame = Time.frameCount;
            }
            catch (System.Exception e)
            {
                Diag.Report("LbTryLootPatch", e);
            }
        }

        [PatchPostfix]
        protected static void PatchPostfix()
        {
            LootFacing.Bot = null;
        }
    }

    public class LootSteadyLookPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotSteering), nameof(BotSteering.LookToPoint), new[] { typeof(Vector3), typeof(float) });

        [PatchPrefix]
        protected static bool PatchPrefix(BotSteering __instance, Vector3 point, float rotateSpeed)
        {
            try
            {
                return RunPrefix(__instance, point, rotateSpeed);
            }
            catch (System.Exception e)
            {
                Diag.Report("LootSteadyLookPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(BotSteering steering, Vector3 point, float rotateSpeed)
        {
            BotOwner bot = LootFacing.Bot;
            if (bot == null || LootFacing.Frame != Time.frameCount || !Settings.LootStill.Value || !ReferenceEquals(steering._player?.AIData?.BotOwner, bot))
            {
                return true;
            }

            Vector3 flat = point - bot.WeaponRoot.position;
            flat.y = 0f;
            string id = bot.ProfileId;
            if (flat.sqrMagnitude >= 1.2f * 1.2f)
            {
                LootFacing.Held.Remove(id);
                return true;
            }

            if (!LootFacing.Held.TryGetValue(id, out Vector3 dir))
            {
                dir = bot.WeaponRoot.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f)
                {
                    return true;
                }

                LootFacing.Held[id] = dir = dir.normalized;
            }

            steering.LookToDirection(dir, rotateSpeed);
            LootFacing.Steadied++;
            return false;
        }
    }

    /// <summary>
    /// LootingBots starts looting only within 0.92 m of its target point. A point that the navmesh gets no nearer than 1-1.5 m to (a body against a wall,
    /// a table edge) left the bot re-planning a path of a few centimetres every 0.2 s, turning to the "way of the path" each time, until it gave up
    /// on the loot. When the bot has no path left to walk and stands within 1.6 m of the point on the same floor, that is near enough.
    /// </summary>
    public class LbCloseEnoughPatch : ModulePatch
    {
        public static int Accepted;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.LootingLogic"), "IsCloseEnough");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref bool __result, Vector3 ____destination)
        {
            try
            {
                if (__result || !Settings.LootStill.Value)
                {
                    return;
                }

                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                if (LbCrouchWalkPatch.NearEnough(bot))
                {
                    __result = true;
                    Accepted++;
                    return;
                }

                if (bot == null || bot.Mover == null || bot.Mover.HasPathAndNoComplete)
                {
                    return;
                }

                Vector3 d = bot.Position - ____destination;
                if (Mathf.Abs(d.y) < 0.6f)
                {
                    d.y = 0f;
                    if (d.sqrMagnitude < 1.6f * 1.6f)
                    {
                        __result = true;
                        Accepted++;
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbCloseEnoughPatch", e);
            }
        }
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Between two things of the same room a bot stands where it is while LootingBots looks for the next one - before, the scan ran while the bot was
    /// still walking QuestingBots' path out of the building, and it came back in for the next drawer.
    /// </summary>
    public class LbScanHoldPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.FindLootLogic"), "Start");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance)
        {
            try
            {
                if (!Settings.LootStill.Value)
                {
                    return;
                }

                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                if (bot != null && Tactics.RoomSweep.InVisit(bot))
                {
                    bot.StopMove();
                    bot.Sprint(false);
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbScanHoldPatch", e);
            }
        }
    }

    /// <summary>
    /// The walk to the next thing of a loot job (LootingBots' LootingLogic.TryLoot, every 0.2 s):
    ///  - a few steps away (within 4.5 m on the same floor) a player walks there without sprinting (crouched only for the last 1.5 m): LootingBots sprinted it, then crouched it again,
    ///    for every drawer of a room - the bobbing up and down;
    ///  - "turn the way you walk" while the bot does not actually move (a path of centimetres, pressed against a table, a door, another bot) made the
    ///    direction swing with every re-plan: bots spun on the spot (raid 2026-10-21: Hubert 1000 deg every 5 s for half a minute). Not moving, the bot keeps
    ///    the way it faces; stalled 2.5 s within 2.2 m of the spot it starts looting from where it is, stalled 5 s farther away it gives the thing up.
    /// </summary>
    public class LbCrouchWalkPatch : ModulePatch
    {
        private class Approach
        {
            public Vector3 Destination;
            public Vector3 LastPos;
            public float StalledSince = -1f;
            public Vector3 Facing;
        }

        private static readonly Dictionary<string, Approach> _approach = new Dictionary<string, Approach>();
        private static readonly HashSet<string> _nearEnough = new HashSet<string>();
        public static int Unspun;
        public static int GaveUp;

        public static void Clear()
        {
            _approach.Clear();
            _nearEnough.Clear();
            Unspun = GaveUp = 0;
            LbNoSlowEndPatch.Fast = SainLootPatrolPatch.Held = 0;
        }

        /// <summary>LbCloseEnoughPatch: the bot stalled right next to the spot - near enough.</summary>
        internal static bool NearEnough(BotOwner bot) => bot != null && _nearEnough.Remove(bot.ProfileId);

        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.LootingLogic"), "TryLoot");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, Vector3 ____destination)
        {
            try
            {
                if (!Settings.LootStill.Value)
                {
                    return;
                }

                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                LootingBots.Components.LootingBrain brain = bot?.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                if (bot == null || bot.Mover == null || brain == null || brain.LootTaskRunning)
                {
                    if (bot != null)
                    {
                        _approach.Remove(bot.ProfileId);
                    }

                    return;
                }

                Vector3 d = bot.Position - ____destination;
                float dy = Mathf.Abs(d.y);
                d.y = 0f;
                float sq = d.sqrMagnitude;
                if (sq <= 0.85f && dy < 0.5f)
                {
                    // At the spot: LootingBots crouches and starts
                    _approach.Remove(bot.ProfileId);
                    return;
                }

                // Raid 2026-10-31 05:46: Hjal crouch-walked from drawer to drawer across the room - the "goose step" a player does not do. Only the last
                // step or so (the next drawer of the same cabinet) is a shuffle in a crouch; farther it walks upright, without the sprint LootingBots gives it.
                // With "Walk to the loot at full step" not even that: upright to the spot, LootingBots crouches on arrival (the tester's "tiny slow steps")
                if (dy < 1f && sq <= 1.5f * 1.5f && !Settings.LootWalk.Value)
                {
                    bot.Sprint(false);
                    bot.SetPose(0.6f);
                    bot.SetTargetMoveSpeed(0.7f);
                }
                else if (dy < 1f && sq <= 4.5f * 4.5f)
                {
                    bot.Sprint(false);
                    bot.SetPose(1f);
                    bot.SetTargetMoveSpeed(1f);
                }

                Unstall(bot, brain, ____destination, Mathf.Sqrt(sq), dy);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbCrouchWalkPatch", e);
            }
        }

        private static void Unstall(BotOwner bot, LootingBots.Components.LootingBrain brain, Vector3 destination, float flat, float dy)
        {
            float now = Time.time;
            if (!_approach.TryGetValue(bot.ProfileId, out Approach a) || (a.Destination - destination).sqrMagnitude > 0.25f)
            {
                _approach[bot.ProfileId] = new Approach { Destination = destination, LastPos = bot.Position };
                return;
            }

            if ((bot.Position - a.LastPos).sqrMagnitude > 0.3f * 0.3f)
            {
                a.LastPos = bot.Position;
                a.StalledSince = -1f;
                return;
            }

            if (a.StalledSince < 0f)
            {
                a.StalledSince = now;
                Vector3 f = bot.LookDirection;
                f.y = 0f;
                a.Facing = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
            }

            // Standing: no turning after a path that goes nowhere
            bot.Steering.LookToDirection(a.Facing, 180f);
            Unspun++;
            float stalled = now - a.StalledSince;
            if (stalled > 2.5f && flat <= 2.2f && dy <= 1.6f)
            {
                // Not through a wall: stalled by the wall of the next room it would have looted the cabinet on the other side (LootReach)
                if (Tactics.LootReach.RefuseFromHere(bot, brain.LootObjectPosition))
                {
                    GaveUp++;
                    _approach.Remove(bot.ProfileId);
                    Log.Detail("loot", $"{bot.Profile?.Nickname} stands by {brain.ActiveLoot?.name ?? "its loot"} with a wall between them - gives it up instead of looting through it");
                    brain.CleanupLoot(true);
                    return;
                }

                _nearEnough.Add(bot.ProfileId);
                return;
            }

            if (stalled > 5f)
            {
                GaveUp++;
                _approach.Remove(bot.ProfileId);
                Log.Detail("loot", $"{bot.Profile?.Nickname} cannot get to {brain.ActiveLoot?.name ?? "its loot"} ({flat:0.0} m, {dy:0.0} m up/down) - gives it up");
                brain.CleanupLoot(true);
                Bots.ShutIn.PathFailed(bot, $"loot {flat:0} m away");
            }
        }
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// A hurt bot with nothing to treat itself with still loots (F12 -> 7 "Loot while hurt without meds").
    /// LootingBots' layer is active only while the bot has nothing to treat (FirstAid.Have2Do / SurgicalKit.HaveWork false) - meant to let it heal first.
    /// A bot with a wound but no medkit (or no surgical kit for a blacked limb) never heals, so it never looted again for the rest of the raid: QuestingBots
    /// kept stopping it "to check for loot" and it stood there (raid 2026-10-22 13:52: idle loot breaks twice as often among the wounded; Кот Перегар at 85 %
    /// never went to the body of his own kill in ten tries). Here the gate holds only while the bot has something to use on that wound.
    /// </summary>
    public class LbWoundedLootPatch : ModulePatch
    {
        public static int Allowed { get; private set; }

        public static void ClearCounters() => Allowed = Flickers = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.LootingLayer"), "IsActive");

        public static int Flickers { get; private set; }

        [PatchPostfix]
        protected static void PatchPostfix(DrakiaXYZ.BigBrain.Brains.CustomLayer __instance, ref bool __result)
        {
            Rules(__instance, ref __result);
            try
            {
                // Only a scan due, nothing to go to: the scan runs, the layer stays off (Harmonizer)
                Bots.Harmonizer.LootLayer(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("Harmonizer.LootLayer", e);
            }
        }

        private static void Rules(DrakiaXYZ.BigBrain.Brains.CustomLayer __instance, ref bool __result)
        {
            if (__result)
            {
                // A scheduled scan with a full bag: LootingBots' layer came on for one frame, its scan refused to start (no free space) and it went off
                // again - every 5 s, cutting whatever the bot was doing (raid 2026-10-22 15:57: "BotRecoveryLayer -> Looting (0.0 s)" 128 times)
                try
                {
                    BotOwner b = __instance.BotOwner;
                    LootingBots.Components.LootingBrain lb = b?.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                    LootingBots.Components.LootFinder lf = b?.GetPlayer?.GetComponent<LootingBots.Components.LootFinder>();
                    if (Settings.LootStill.Value && lb != null && lf != null && !lb.IsBotLooting && !lf.IsScanRunning && !lb.HasFreeSpace && !lb.ForceBrainEnabled)
                    {
                        __result = false;
                        Flickers++;
                    }
                }
                catch (System.Exception e)
                {
                    Diag.Report("LbWoundedLootPatch", e);
                }

                return;
            }

            if (!Settings.WoundedLoot.Value)
            {
                return;
            }

            try
            {
                BotOwner bot = __instance.BotOwner;
                BotMedecine med = bot?.Medecine;
                if (bot == null || bot.BotState != EBotState.Active || med?.FirstAid == null || med.SurgicalKit == null)
                {
                    return;
                }

                bool wounded = med.FirstAid.Have2Do || med.SurgicalKit.HaveWork;
                bool canTreat = (med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use) || (med.SurgicalKit.HaveWork && med.SurgicalKit.HaveSmth2Use);
                if (!wounded || canTreat || med.Using)
                {
                    return;
                }

                LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                LootingBots.Components.LootFinder finder = bot.GetPlayer?.GetComponent<LootingBots.Components.LootFinder>();
                if (brain != null && finder != null && brain.IsBrainEnabled && (finder.IsScheduledScan || brain.IsBotLooting))
                {
                    __result = true;
                    Allowed++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbWoundedLootPatch", e);
            }
        }
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// No turning on the spot while a bot goes through a body or a container (F12 -> 7 "Squat still while looting").
    /// The earlier fix held the facing only when LootingBots itself turned the bot to a target within 1.2 m - it caught 14 cases in a whole raid
    /// (2026-10-22 13:52) while bots kept spinning 400-650 degrees in 5 s standing at bodies and shelves: a body or a shelf lies farther than that from the
    /// gun, and other steering (sounds heard, the patrol's look-around between two loot jobs) pulled the bot round between LootingBots' turns every 0.2 s.
    /// Here, every frame of LootingBots' looting action: while the bot stands at its loot (the loot job running, or arrived and not walking), the look
    /// direction is the one towards the loot taken when it got there.
    /// </summary>
    public class LootSpinGuardPatch : ModulePatch
    {
        private class Hold
        {
            public LootingBots.Components.LootingBrain Brain;
            public object Loot;
            public Vector3 Dir;
        }

        private static readonly Dictionary<string, Hold> _holds = new Dictionary<string, Hold>();

        public static int Frames { get; private set; }

        public static void Clear()
        {
            _holds.Clear();
            Frames = 0;
        }

        // LootingBots' looting action, after its own update (it turns the bot to the loot every 0.2 s): the facing is set again every frame.
        // 0.4.0 did this inside EFT's BotSteering.Steering - moved out of the steering itself when the game began to crash (raid 2026-10-22 15:02)
        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.LootingLogic"), "Update");

        [PatchPostfix]
        protected static void PatchPostfix(DrakiaXYZ.BigBrain.Brains.CustomLogic __instance)
        {
            if (!Settings.LootStill.Value)
            {
                return;
            }

            try
            {
                BotOwner bot = __instance?.BotOwner;
                if (bot == null || bot.IsDead || bot.GetPlayer == null || bot.Steering == null || Spectator.TakeOver.IsTaken(bot))
                {
                    return;
                }

                if (!_holds.TryGetValue(bot.ProfileId, out Hold h))
                {
                    _holds[bot.ProfileId] = h = new Hold { Brain = bot.GetPlayer.GetComponent<LootingBots.Components.LootingBrain>() };
                }

                LootingBots.Components.LootingBrain brain = h.Brain;
                object loot = brain?.ActiveLoot;
                bool standing = loot != null && (brain.LootTaskRunning || (bot.Mover != null && !bot.Mover.HasPathAndNoComplete && !bot.Mover.IsMoving));
                if (!standing)
                {
                    h.Loot = null;
                    return;
                }

                if (!ReferenceEquals(h.Loot, loot))
                {
                    h.Loot = loot;
                    Vector3 to = brain.LootObjectPosition - bot.WeaponRoot.position;
                    Vector3 flat = new Vector3(to.x, 0f, to.z);
                    if (flat.sqrMagnitude < 0.25f)
                    {
                        // Standing right on it: keep the way the bot faces, looking down a little
                        flat = bot.GetPlayer.LookDirection;
                        flat.y = 0f;
                        if (flat.sqrMagnitude < 0.01f)
                        {
                            flat = Vector3.forward;
                        }

                        to = flat.normalized + Vector3.down * 0.6f;
                    }

                    h.Dir = to.normalized;
                }

                if (IsFinite(h.Dir) && h.Dir.sqrMagnitude > 0.5f)
                {
                    bot.Steering.LookToDirection(h.Dir, 360f);
                    Frames++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LootSpinGuardPatch", e);
            }
        }

        private static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// "Bots stutter and move in tiny slow steps while looting" (a player's tester, 2026-11-02) - F12 -> 7 "Walk to the loot at full step". Not an
    /// anti-freeze: LootingBots walks to a thing with EFT's GoToPoint(slowAtTheEnd: true), and EFT then cuts the speed to (distance left / 7) for the
    /// last 1.5 m (Move.START_SLOW_DIST, SLOW_COEF of the bot settings) - 0.2 of a walk at 1.5 m, 0.14 at 1 m - while LootingBots only starts looting
    /// within 0.92 m. From drawer to drawer of one cabinet the whole way was that crawl. Here the walk of LootingBots asks for no slow end: the ordinary
    /// braking of EFT (from 1.1 m) is left, a fraction of a second.
    /// </summary>
    public class LbMoveToLootPatch : ModulePatch
    {
        [System.ThreadStatic]
        internal static int InMoveFrame;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("LootingBots.Logic.LootingLogic"), "TryMoveToLoot");

        [PatchPrefix]
        protected static void PatchPrefix() => InMoveFrame = Time.frameCount;

        [PatchPostfix]
        protected static void PatchPostfix() => InMoveFrame = -1;
    }

    public class LbNoSlowEndPatch : ModulePatch
    {
        public static int Fast;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToPoint),
                new[] { typeof(Vector3), typeof(bool), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool) });

        [PatchPrefix]
        protected static void PatchPrefix(ref bool slowAtTheEnd)
        {
            if (slowAtTheEnd && LbMoveToLootPatch.InMoveFrame == Time.frameCount && Settings.LootWalk.Value)
            {
                slowAtTheEnd = false;
                Fast++;
            }
        }
    }

    /// <summary>
    /// SAIN puts a calm bot that is not sprinting into the patrol stance of EFT (gun down, the patrol walk) every quarter of a second, and LootingBots
    /// takes it out of it again when it arrives at the loot - on a walk between drawers (never a sprint) the two flipped the stance back and forth, and
    /// each flip is an animation the walk waits for. While the bot has a loot target or a loot job runs, SAIN does not want the patrol stance.
    /// </summary>
    public class SainLootPatrolPatch : ModulePatch
    {
        public static int Held;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAIN.SAINComponent.Classes.Mover.SAINMoverClass), "WantsPatrolStance");

        [PatchPostfix]
        protected static void PatchPostfix(SAIN.SAINComponent.Classes.Mover.SAINMoverClass __instance, ref bool __result)
        {
            if (!__result || !Settings.LootWalk.Value)
            {
                return;
            }

            try
            {
                LootingBots.Components.LootingBrain brain = __instance.BotOwner?.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                if (brain != null && (brain.HasActiveLootable || brain.LootTaskRunning))
                {
                    __result = false;
                    Held++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainLootPatrolPatch", e);
            }
        }
    }
}
