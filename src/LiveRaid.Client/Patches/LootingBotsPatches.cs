using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using LootingBots.Components;
using LootingBots.Utilities;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Loot speed (F12 -> 7 "Loot speed x"). Every wait of LootingBots - the pause after taking an item, the time to examine one, the delay
    /// before it starts - goes through one function; here its delay is divided by LiveRaid's factor. LootingBots' config stays as it is.
    /// </summary>
    public class LbLootSpeedPatch : ModulePatch
    {
        protected override System.Reflection.MethodBase GetTargetMethod() =>
            HarmonyLib.AccessTools.Method(typeof(LootingBots.Components.LootingTransactionController), "SimulatePlayerDelayAsync");

        [PatchPrefix]
        protected static void PatchPrefix(ref double delay)
        {
            try
            {
                float speed = Settings.LootSpeed.Value;
                if (speed <= 1.001f)
                {
                    return;
                }

                if (delay == -1d)
                {
                    delay = global::LootingBots.LootingBots.TransactionDelay.Value;
                }

                delay /= speed;
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootSpeedPatch", e);
            }
        }
    }

    /// <summary>
    /// LootingBots picks the nearest body. Before the bot commits, LiveRaid's loot brain decides:
    /// loot now, hide and watch the body, come back later, or leave it.
    /// </summary>
    public class LbCorpseDecisionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingBrain), nameof(LootingBrain.SetLoot));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingBrain __instance, InteractableObject interactableObject, LootFinder.LootType lootType, string lootId)
        {
            try
            {
                return RunPrefix(__instance, interactableObject, lootType, lootId);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbCorpseDecisionPatch", e);
                return true;
            }
        }

        /// <summary>A target is set (whoever found it): QuestingBots stands aside for it at once (Harmonizer).</summary>
        [PatchPostfix]
        protected static void PatchPostfix(LootingBrain __instance)
        {
            try
            {
                Bots.Harmonizer.OnLootSet(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("Harmonizer.OnLootSet", e);
            }
        }

        private static bool RunPrefix(LootingBrain __instance, InteractableObject interactableObject, LootFinder.LootType lootType, string lootId)
        {
            if (lootType != LootFinder.LootType.Corpse || !(interactableObject is Corpse corpse) || __instance.BotOwner == null)
            {
                return true;
            }

            LootDecision decision;
            try
            {
                decision = LootBrain.Decide(__instance.BotOwner, corpse, lootId);
            }
            catch (System.Exception e)
            {
                Log.Warn("loot decision failed, looting as usual: " + e.Message);
                return true;
            }

            if (decision == LootDecision.LootNow)
            {
                return true;
            }

            // LootingBots reserved this body for us before asking; release it for other bots
            ActiveLootCache.Cleanup(lootId, __instance.BotOwner);
            if (decision == LootDecision.Ignore)
            {
                __instance.IgnoreLoot(lootId);
            }

            return false;
        }
    }

    /// <summary>
    /// Before LootingBots starts a loot job: a bot that drew "lie down" at a body goes down first and is held back until it lies (ProneLoot, test function),
    /// then the stop-gaps for a hopeless target and for danger.
    /// </summary>
    public class LbStartLootingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingBrain), nameof(LootingBrain.StartLooting));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingBrain __instance)
        {
            try
            {
                // First: a bot going down for a body is asked again 0.2 s later and must not count as a start that came to nothing
                if (!ProneLoot.Gate(__instance))
                {
                    return false;
                }

                // The same target started again and again with nothing coming of it: given up (LootWatch)
                if (!LootWatch.AllowStart(__instance))
                {
                    return false;
                }

                // Shots, a stranger heard or seen close by: not now (LootDanger)
                if (!LootDanger.AllowStart(__instance))
                {
                    return false;
                }

                RunPrefix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbStartLootingPatch", e);
            }

            return true;
        }

        private static void RunPrefix(LootingBrain __instance)
        {
            BotOwner bot = __instance.BotOwner;
            if (bot != null)
            {
                LootAudit.OnStart(__instance);
                SquadBrain.OnStartedLooting(bot);
                SquadLoot.OnStartedLooting(bot);
                RoomSweep.OnStarted(bot);
            }
        }
    }

    /// <summary>While a bot loots lying down, nothing gets it up except the end of looting (ProneLoot.StandUp lets its own call through).</summary>
    public class BotLayGetUpPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLay), nameof(BotLay.GetUp));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner)
        {
            try
            {
                return RunPrefix(____owner);
            }
            catch (System.Exception e)
            {
                Diag.Report("BotLayGetUpPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(BotOwner ____owner)
        {
            return !ProneLoot.BlocksGetUp(____owner);
        }
    }

    public class LbStopLootingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingBrain), nameof(LootingBrain.StopLooting));

        /// <summary>A running job is cut short from outside (danger, a treatment, a hand-over, the layer left): not the thing's fault (LootAudit).</summary>
        [PatchPrefix]
        protected static void PatchPrefix(LootingBrain __instance)
        {
            try
            {
                LootAudit.OnCut(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbStopLootingPatch.Cut", e);
            }
        }

        [PatchPostfix]
        protected static void PatchPostfix(LootingBrain __instance)
        {
            try
            {
                RunPostfix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbStopLootingPatch", e);
            }
        }

        private static void RunPostfix(LootingBrain __instance)
        {
            BotOwner bot = __instance.BotOwner;

            // Done with one thing in this room: the rest of the room next (RoomSweep checks a moment later)
            RoomSweep.OnLootEnded(bot);
            ProneLoot.OnLootEnded(bot);
        }
    }

    /// <summary>
    /// A loot job that ends by itself (done, nothing fits, timeout) goes through OnLootTaskEnd only - LootingBots calls StopLooting just when its layer
    /// is left - so a bot that lay down over a body was never stood up (ProneLoot, reason 3).
    /// </summary>
    public class LbLootTaskEndPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingBrain), nameof(LootingBrain.OnLootTaskEnd));

        [PatchPrefix]
        protected static void PatchPrefix(LootingBrain __instance, out LootAudit.Snapshot __state)
        {
            __state = null;
            try
            {
                __state = LootAudit.Ending(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootTaskEndPatch.Ending", e);
            }
        }

        [PatchPostfix]
        protected static void PatchPostfix(LootingBrain __instance, bool lootingSuccessful, LootAudit.Snapshot __state)
        {
            try
            {
                LootAudit.Ended(__instance, __state, lootingSuccessful);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootTaskEndPatch.Ended", e);
            }

            try
            {
                ProneLoot.OnLootEnded(__instance.BotOwner);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootTaskEndPatch", e);
            }
        }
    }

    /// <summary>
    /// LootingBots takes the first of the things round the bot by straight distance whose way on foot is short enough (up to 80 m), so a bot going
    /// through a room was sent through a wall or down a floor to a nearer-looking thing. While a bot goes through a room (or a building), only what
    /// lies in that room (building) counts as in range; returning -1 as the distance tells LootingBots to simply try the next candidate. A building the bot
    /// has just finished is out of range for a few minutes (bodies excepted).
    /// </summary>
    public class LbRoomRangePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootFinder), "IsLootInRange");

        [PatchPostfix]
        protected static void PatchPostfix(ref bool __result, LootFinder.LootType lootType, Vector3 destination, ref float dist, BotOwner ____botOwner)
        {
            try
            {
                if (__result && (!RoomSweep.Allows(____botOwner, destination, lootType) || (lootType != LootFinder.LootType.Corpse && !SquadLoot.InReach(____botOwner, destination))))
                {
                    __result = false;
                    dist = -1f;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbRoomRangePatch", e);
            }
        }
    }

    /// <summary>
    /// LootingBots' looting action takes its way point once, when it starts. RoomSweep hands the next thing of a room over the moment the last one is
    /// done; BigBrain then keeps the same action going without starting it again (the same action as before), its way point still the cleared one -
    /// the bot would walk to (0, 0, 0), find no path and give the thing up. A way point of nothing with a target set: the action is started again.
    /// </summary>
    public class LbHandoverPatch : ModulePatch
    {
        private static FieldInfo _destination;
        private static FieldInfo _brain;

        public static int Restarted { get; private set; }

        public static void Clear() => Restarted = 0;

        protected override MethodBase GetTargetMethod()
        {
            System.Type logic = AccessTools.TypeByName("LootingBots.Logic.LootingLogic");
            _destination = AccessTools.Field(logic, "_destination");
            _brain = AccessTools.Field(logic, "_lootingBrain");
            return AccessTools.Method(logic, "Update");
        }

        [PatchPrefix]
        protected static void PatchPrefix(DrakiaXYZ.BigBrain.Brains.CustomLogic __instance)
        {
            try
            {
                if (__instance == null || _destination == null || _brain == null || (Vector3)_destination.GetValue(__instance) != Vector3.zero)
                {
                    return;
                }

                if (_brain.GetValue(__instance) is LootingBrain brain && brain.HasActiveLootable && !brain.LootTaskRunning && brain.Destination != Vector3.zero)
                {
                    __instance.Start();
                    Restarted++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbHandoverPatch", e);
            }
        }
    }
}
