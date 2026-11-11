using System.Collections.Generic;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// QuestingBots' idle layer does not flicker (F12 -> 2 "Calm the flicker of QuestingBots' idle layer"). Raid 2026-11-02 14:11: 526 layer ping-pongs,
    /// 205 of them "PatrolAssault -> BotRecoveryLayer (0.0-0.2 s) -> PatrolAssault" and back - the scav patrols (and the scavs between two steps of a quest)
    /// started and stopped their walk several times a second. BotRecoveryLayer.IsActive answers from the questing decision of the moment (WaitForAssignment
    /// in and out of CheckForLoot, a cover point just reached or lost) and is asked every 100 ms.
    /// Here its answer may change at most once a second: a flip inside a second of the last one is not passed on. Combat, looting and every other layer sit
    /// above or beside it and are not touched; a second of the idle layer too long or too short is invisible, a walk that stops and starts is not.
    /// </summary>
    public static class LayerCalm
    {
        private class State
        {
            public bool Last;
            public float ChangedAt = -99f;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();

        public static int Held { get; private set; }
        public static int HeldLoot { get; private set; }

        public static string Summary => $"layer flips not passed on: QuestingBots' idle layer {Held}, LootingBots' loot layer {HeldLoot}";

        public static void Clear()
        {
            _states.Clear();
            Held = HeldLoot = 0;
        }

        internal static void Calm(CustomLayer layer, ref bool result) => Calm(layer, ref result, 1f, "idle");

        /// <summary>The one arbiter of the flicker: an answer of a layer that flips again within <paramref name="hold"/> seconds of its last flip is not passed on.</summary>
        internal static void Calm(CustomLayer layer, ref bool result, float hold, string tag)
        {
            if (!Settings.LayerCalm.Value || layer?.BotOwner == null)
            {
                return;
            }

            string id = layer.BotOwner.ProfileId + "|" + tag;
            if (!_states.TryGetValue(id, out State s))
            {
                _states[id] = new State { Last = result, ChangedAt = Time.time };
                return;
            }

            if (result == s.Last)
            {
                return;
            }

            // Never while the bot is in a real fight or treating itself: those answers must go through at once
            if (Time.time - s.ChangedAt < hold && !Tactics.HitReaction.InRealFight(layer.BotOwner, 15f) && layer.BotOwner.Medecine?.Using != true && layer.BotOwner.Medecine?.FirstAid?.Have2Do != true)
            {
                result = s.Last;
                if (tag == "loot")
                {
                    HeldLoot++;
                }
                else
                {
                    Held++;
                }

                return;
            }

            s.Last = result;
            s.ChangedAt = Time.time;
        }
    }

    /// <summary>
    /// LootingBots' layer ("Looting") answers "active" while it scans or loots and "inactive" in between: raids 2026-11-07/08 counted "Looting <-> PatrolAssault" 100-140 times
    /// a raid (and "Looting <-> BotObjectiveLayer" in dozens) - the bot walked, stopped, walked. Its Peaceful action is what runs in a gap, so a flip held back for a
    /// second (1.5 s) costs nothing, and one sentence of the arbiter covers it as it covers QuestingBots' idle layer.
    /// </summary>
    public class LbLootingLayerCalmPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("LootingBots.LootingLayer"), "IsActive");

        [PatchPostfix]
        protected static void PatchPostfix(CustomLayer __instance, ref bool __result)
        {
            try
            {
                LayerCalm.Calm(__instance, ref __result, 1.5f, "loot");
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootingLayerCalmPatch", e);
            }
        }
    }

    public class QbRecoveryLayerCalmPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.Recovery.BotRecoveryLayer"), "IsActive");

        [PatchPostfix]
        protected static void PatchPostfix(CustomLayer __instance, ref bool __result)
        {
            try
            {
                LayerCalm.Calm(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbRecoveryLayerCalmPatch", e);
            }
        }
    }
}
