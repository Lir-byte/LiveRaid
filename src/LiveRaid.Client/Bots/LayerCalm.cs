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

        public static string Summary => $"idle-layer flips of QuestingBots not passed on {Held}";

        public static void Clear()
        {
            _states.Clear();
            Held = 0;
        }

        internal static void Calm(CustomLayer layer, ref bool result)
        {
            if (!Settings.LayerCalm.Value || layer?.BotOwner == null)
            {
                return;
            }

            string id = layer.BotOwner.ProfileId;
            if (!_states.TryGetValue(id, out State s))
            {
                _states[id] = new State { Last = result, ChangedAt = Time.time };
                return;
            }

            if (result == s.Last)
            {
                return;
            }

            if (Time.time - s.ChangedAt < 1f)
            {
                result = s.Last;
                Held++;
                return;
            }

            s.Last = result;
            s.ChangedAt = Time.time;
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
