using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using QuestingBots.Configuration;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Tempo like players (F12 -> 3 "Tempo like players"). The bots already walk fast (raids of 2026-10-31: calm PMCs 96-110 m/min), but they linger:
    /// circle a region that is long searched, stand out the waits of a quest point, scan for loot as long as a greedy looter. Players move on.
    ///  - a fast squad (its doctrine's pace: chads and PvP hunters) has less patience at one place (RoamPlanner) and waits less after a quest point
    ///    (QbWaitAfterStepPatch); slow doctrines (looters, questers) keep the times they had;
    ///  - a bot that came into a region it or others had already searched (RegionMemory: its own visit in the last 15 min or a crowd of other visits)
    ///    and has found nothing to loot and no fight there for 2-3 minutes leaves it for another part of the map (RoamPlanner.LeaveRegion);
    ///  - a bot with little greed for loot scans for loot shorter when QuestingBots breaks for it (QbLootScanPatch: down to 65 % at no greed).
    /// </summary>
    public static class Tempo
    {
        private const float SearchedBelow = 0.6f;

        private class Stay
        {
            public (int, int) Region;
            public float Since;
            public float LastBusy;
            public float Limit;
            public bool Left;
        }

        private static readonly Dictionary<string, Stay> _stays = new Dictionary<string, Stay>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int RegionsLeft { get; private set; }
        public static int WaitsCut { get; private set; }
        public static int ScansCut { get; private set; }

        public static string Summary => $"tempo: calm pace PMC {Show(EncounterLog.Pace(0))} m/min, player scavs {Show(EncounterLog.Pace(1))}; searched regions left early {RegionsLeft}, " +
                                        $"waits at quest points cut by pace {WaitsCut}, loot scans cut for little greed {ScansCut}";

        private static string Show(float v) => v < 0f ? "-" : $"{v:0}";

        public static bool On => Settings.Enabled.Value && Settings.TempoLikePlayers.Value;

        public static void Clear()
        {
            _stays.Clear();
            _next = 0f;
            RegionsLeft = WaitsCut = ScansCut = 0;
            QbWaitAfterStepPatch.Reset();
            QbLootScanPatch.Reset();
        }

        /// <summary>1 for a slow or ordinary squad, down to 0.6 for the fastest (doctrine pace 0.35 and below = 1, 0.95 = 0.6).</summary>
        public static float PaceFactor(BotOwner bot)
        {
            if (!On || bot == null)
            {
                return 1f;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            if (d == null)
            {
                return 1f;
            }

            return Mathf.Clamp(1.25f - 0.7f * d[DoctrineDeck.Pace], 0.6f, 1f);
        }

        /// <summary>Loot scan time: 65 % at no greed, full from greed 0.4 up.</summary>
        public static float ScanFactor(BotProfile p)
        {
            if (!On || p == null || p.Kind == BotKind.Other)
            {
                return 1f;
            }

            return Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(p.LootGreed / 0.4f));
        }

        internal static void ScanCut() => ScansCut++;

        internal static void WaitCut() => WaitsCut++;

        public static void Tick()
        {
            if (!On || !Settings.MoveOn.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || TacticalTasks.SquadLeaderOf(bot) != null)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || !RegionMemory.Entered(bot.ProfileId, out (int, int) region, out float fresh, out float at))
                {
                    continue;
                }

                if (!_stays.TryGetValue(bot.ProfileId, out Stay s) || s.Region != region)
                {
                    // 2-3 minutes, less for a fast squad
                    _stays[bot.ProfileId] = s = new Stay { Region = region, Since = at, LastBusy = at, Limit = (120f + 60f * (float)_rng.NextDouble()) * PaceFactor(bot) };
                }

                if (s.Left || fresh >= SearchedBelow)
                {
                    continue;
                }

                // Looting, a body to go through, a fight, a task of its own: busy, the clock starts again
                LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                if ((brain != null && (brain.LootTaskRunning || brain.HasActiveLootable)) || LootJobs.HasWorkNear(bot) || LootJobs.LeaderShouldWait(bot)
                    || HitReaction.InRealFight(bot, 60f) || TacticalTasks.TryGet(bot, out _) || bot.Medecine?.Using == true)
                {
                    s.LastBusy = now;
                    continue;
                }

                if (now - s.LastBusy < s.Limit)
                {
                    continue;
                }

                // Not on the way out
                if (bot.GetObjectiveManager()?.BotMonitor?.GetMonitor<BotExtractMonitor>()?.IsTryingToExtract == true)
                {
                    continue;
                }

                s.Left = true;
                RegionsLeft++;
                float size = Settings.RoamRegionSize.Value;
                Vector3 centre = new Vector3((region.Item1 + 0.5f) * size, bot.Position.y, (region.Item2 + 0.5f) * size);
                RoamPlanner.LeaveRegion(bot, p, centre, $"searched before it came (freshness {fresh:0.00}), nothing found for {(now - s.LastBusy) / 60f:0.0} min");
            }
        }
    }

    /// <summary>
    /// The wait after a quest point (QuestingBots: the step's "waitTimeAfterCompleting", up to 16 s at the rich points of LiveRaid's exploring quests)
    /// is shorter for a fast squad (Tempo.PaceFactor).
    /// </summary>
    public class QbWaitAfterStepPatch : ModulePatch
    {
        private static readonly HashSet<BotJobAssignment> _counted = new HashSet<BotJobAssignment>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotJobAssignment), nameof(BotJobAssignment.HasWaitedLongEnoughAfterEnding));

        internal static void Reset() => _counted.Clear();

        [PatchPostfix]
        protected static void PatchPostfix(BotJobAssignment __instance, ref bool __result)
        {
            if (__result || !Tempo.On)
            {
                return;
            }

            try
            {
                double wait = __instance.QuestObjectiveStepAssignment?.WaitTimeAfterCompleting ?? 0;
                double? since = __instance.TimeSinceEnded();
                if (wait <= 3d || since == null)
                {
                    return;
                }

                float factor = Tempo.PaceFactor(__instance.BotOwner);
                if (factor < 0.99f && since.Value >= wait * factor)
                {
                    __result = true;
                    if (_counted.Count > 2000)
                    {
                        _counted.Clear();
                    }

                    if (_counted.Add(__instance))
                    {
                        Tempo.WaitCut();
                    }
                }
            }
            catch (Exception e)
            {
                Diag.Report("QbWaitAfterStepPatch", e);
            }
        }
    }

    /// <summary>
    /// QuestingBots' loot scan (BreakForLooting.MaxLootScanTime, set by "Thorough looting") is one number for every bot. While one bot is checked,
    /// the number is its own: shorter for a bot with little greed (Tempo.ScanFactor), then put back.
    /// </summary>
    public class QbLootScanPatch : ModulePatch
    {
        private static readonly HashSet<string> _counted = new HashSet<string>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLootingMonitor), "allowedToCheckForLoot");

        [PatchPrefix]
        protected static void PatchPrefix(BotLootingMonitor __instance, out float __state)
        {
            __state = -1f;
            if (!Tempo.On)
            {
                return;
            }

            try
            {
                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                float factor = Tempo.ScanFactor(bot != null ? BotProfiles.Get(bot) : null);
                if (factor >= 0.99f)
                {
                    return;
                }

                BreakForLootingConfig b = Loot();
                if (b == null)
                {
                    return;
                }

                __state = b.MaxLootScanTime;
                b.MaxLootScanTime = __state * factor;
                if (_counted.Count < 500 && _counted.Add(bot.ProfileId))
                {
                    Tempo.ScanCut();
                }
            }
            catch (Exception e)
            {
                __state = -1f;
                Diag.Report("QbLootScanPatch", e);
            }
        }

        // A finalizer, not a postfix: the number goes back even when QuestingBots' method throws
        [PatchFinalizer]
        protected static Exception PatchFinalizer(Exception __exception, float __state)
        {
            BreakForLootingConfig b = __state >= 0f ? Loot() : null;
            if (b != null)
            {
                b.MaxLootScanTime = __state;
            }

            return __exception;
        }

        internal static void Reset() => _counted.Clear();

        private static BreakForLootingConfig _loot;

        // QuestingBots' ConfigUtil is internal: its config through ModControl, once per game (the object stays the same)
        private static BreakForLootingConfig Loot() => _loot ??= Patches.ModControl.QuestingBots()?.Questing?.BotQuestingRequirements?.BreakForLooting;
    }
}
