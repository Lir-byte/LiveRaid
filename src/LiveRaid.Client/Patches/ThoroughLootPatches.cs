using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Raid;
using QuestingBots.Components;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots' own quests mostly say "don't loot here" (Inhibit) and bots run on to the next point.
    /// A player who has come to a place for a task looks around it: containers, rooms, bodies. With "Thorough looting" on,
    /// after finishing an objective the bot decides by its character (loot greed, how loot-rich the area is) whether to comb the area.
    /// Camping and sniping objectives are left alone: there the bot is supposed to sit.
    /// </summary>
    public class QbLootAfterObjectivePatch : ModulePatch
    {
        private static readonly System.Random _rng = new System.Random();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.UpdateLootingBehavior));

        [PatchPrefix]
        protected static void PatchPrefix(BotObjectiveManager __instance, ref LootAfterCompleting behavior)
        {
            try
            {
                RunPrefix(__instance, ref behavior);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbLootAfterObjectivePatch", e);
            }
        }

        private static void RunPrefix(BotObjectiveManager __instance, ref LootAfterCompleting behavior)
        {
            if (!Settings.ThoroughLooting.Value || behavior == LootAfterCompleting.Force)
            {
                return;
            }

            BotOwner bot = Traverse.Create(__instance).Field("botOwner").GetValue<BotOwner>();
            BotQuest quest = __instance.CurrentAssignment?.QuestAssignment;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other || quest == null || quest.IsCamping || quest.IsSniping)
            {
                return;
            }

            // Greedy characters comb the place, others only sometimes; loot-rich surroundings tempt everyone
            float chance = Mathf.Clamp(0.25f + 0.4f * p.LootGreed, 0.1f, 0.95f);
            MapIntel intel = MapIntel.Current;
            if (intel != null && bot != null)
            {
                chance += 0.2f * Mathf.Clamp01(intel.LootDensityAround(bot.Position) / 30f);
            }

            if (behavior == LootAfterCompleting.Default)
            {
                chance *= 0.7f;
            }

            if (_rng.NextDouble() < chance)
            {
                Log.Debug($"{p.Name} ({p.Style}) combs the area after its objective ({chance:0.00})");
                behavior = LootAfterCompleting.Force;
            }
        }
    }
}
