using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using LootingBots.Components;
using LootingBots.Utilities;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Keeps the mods from pulling one bot two ways at once (F12 -> 1 "Harmonize behaviour").
    /// Raid 2026-10-22 16:49 (0.5.0), 17 minutes: 508 layer ping-pongs in the trace - BotObjective/BotRecovery 174 (QuestingBots' loot break: the bot
    /// stopped, waited 2.5 s for a scan that found nothing, walked on), BotRecovery/Looting 70, BotFollower/Looting 15, PatrolAssault/Looting 24
    /// (LootingBots took the bot for a 0.1-0.3 s scan every few seconds - the bot stopped, the patrol or the follow restarted).
    ///  - Loot scans run in the background: LootingBots' layer takes the bot only once there is something to go to. The moment it has a target,
    ///    QuestingBots is told to stand aside (its forced loot window) - a bot walking its quest turns to the loot, instead of stopping first.
    ///  - QuestingBots' loot break hands the nearest thing worth taking within 40 m (on foot, the same rules LootingBots uses) to LootingBots at once;
    ///    with nothing near the break is skipped - the bot does not stop.
    ///  - A bot caught flipping between two layers four times within a minute is calmed: if looting is one of them it rests 30 s, if it is
    ///    QuestingBots' loot break its breaks rest a minute.
    /// </summary>
    public static class Harmonizer
    {
        private static FieldInfo _forcedTimer;
        private static bool _looked;

        private class Churn
        {
            public string Pair;
            public readonly List<float> At = new List<float>();
            public float NoBreaksUntil;
        }

        private static readonly Dictionary<string, Churn> _churn = new Dictionary<string, Churn>();

        public static int BackgroundScans { get; private set; }
        public static int LayerKeptOff { get; private set; }
        public static int StoodAside { get; private set; }
        public static int BreaksSkipped { get; private set; }
        public static int BreaksHanded { get; private set; }
        public static int Calmed { get; private set; }

        public static string Summary => $"harmony: loot scans in the background {BackgroundScans} (LootingBots kept off the bot {LayerKeptOff} times), " +
                                        $"QuestingBots stood aside for found loot {StoodAside}, loot breaks: straight to the nearest thing {BreaksHanded}, " +
                                        $"skipped with nothing near {BreaksSkipped}; flipping bots calmed {Calmed}";

        public static void Clear()
        {
            _churn.Clear();
            BackgroundScans = LayerKeptOff = StoodAside = BreaksSkipped = BreaksHanded = Calmed = 0;
        }

        private static bool On => Settings.Harmonize.Value;

        /// <summary>
        /// LootingBots' layer asks to take the bot (after LiveRaid's own rules). Only a scan is due, nothing to go to yet: the scan is started here, the
        /// layer stays off - the bot goes on with what it was doing until the scan has found something.
        /// </summary>
        public static void LootLayer(CustomLayer layer, ref bool active)
        {
            if (!On || !active)
            {
                return;
            }

            BotOwner bot = layer?.BotOwner;
            // A squadmate's handover: both stand for it, no loot of their own meanwhile (SquadShare)
            if (SquadShare.InScene(bot))
            {
                active = false;
                return;
            }

            Player player = bot?.GetPlayer;
            LootingBrain brain = player != null ? player.GetComponent<LootingBrain>() : null;
            LootFinder finder = player != null ? player.GetComponent<LootFinder>() : null;
            if (brain == null || finder == null || brain.IsBotLooting)
            {
                return;
            }

            if (!finder.IsScanRunning && brain.IsBrainEnabled && (brain.HasFreeSpace || brain.ForceBrainEnabled) && ScanScheduler.CanStartScan(out int ticket))
            {
                finder.BeginSearch(ticket);
                BackgroundScans++;
            }

            active = false;
            LayerKeptOff++;
        }

        /// <summary>LootingBots has a loot target for the bot (its scan, RoomSweep, a body job): QuestingBots stands aside for it now.</summary>
        public static void OnLootSet(LootingBrain brain)
        {
            if (!On || brain?.BotOwner == null || !brain.HasActiveLootable)
            {
                return;
            }

            if (QbStandAside(brain.BotOwner))
            {
                StoodAside++;
            }
        }

        /// <summary>QuestingBots' forced loot window (what its own "force a loot scan" opens, 7 s): its objective layer gives the bot to LootingBots.</summary>
        public static bool QbStandAside(BotOwner bot)
        {
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    _forcedTimer = AccessTools.Field(typeof(BotLootingMonitor), "forcedLootSearchTimer");
                }

                BotLootingMonitor monitor = QuestingBots.Controllers.BotObjectiveManagerFactory.GetObjectiveManager(bot)?.BotMonitor?.GetMonitor<BotLootingMonitor>();
                if (monitor == null || _forcedTimer == null || !(_forcedTimer.GetValue(monitor) is Stopwatch timer))
                {
                    return false;
                }

                timer.Restart();
                return true;
            }
            catch (System.Exception e)
            {
                Diag.Report("Harmonizer.QbStandAside", e);
                return false;
            }
        }

        /// <summary>
        /// QuestingBots wants a loot break. True: something near was handed to LootingBots - the bot turns to it. False with <paramref name="skip"/>: nothing
        /// worth stopping for (or its breaks rest after flipping) - the bot walks on. False without: LiveRaid stays out of it.
        /// </summary>
        public static bool LootBreak(BotOwner bot, out string skip)
        {
            skip = null;
            if (!On || bot == null)
            {
                return false;
            }

            if (_churn.TryGetValue(bot.ProfileId, out Churn c) && Time.time < c.NoBreaksUntil)
            {
                skip = "its loot breaks rest after flipping";
                return false;
            }

            // A body the bot means to search comes before any drawer on the way (it was handed the nearest thing instead and walked off)
            if (LootJobs.AskNow(bot))
            {
                BreaksHanded++;
                return true;
            }

            // On its way to a quest step (QuestDrive): only what lies right on the way
            bool driving = QuestDrive.Driving(bot);
            float reach = driving ? 12f : 40f;
            if (RoomSweep.HandNearest(bot, reach, out string what))
            {
                BreaksHanded++;
                Log.Detail("loot", () => $"{bot.Profile?.Nickname}: loot break - goes for {what}");
                return true;
            }

            BreaksSkipped++;
            if (driving)
            {
                QuestDrive.CutBreak();
                skip = "it is on its way to its quest step and nothing lies within 12 m";
                return false;
            }

            skip = "nothing worth taking within 40 m";
            return false;
        }

        /// <summary>BrainWatch saw the bot go A -> B -> A quickly.</summary>
        public static void OnPingPong(BotOwner bot, string a, string b, float lasted)
        {
            if (!On || bot == null || bot.IsDead)
            {
                return;
            }

            float now = Time.time;
            string pair = string.CompareOrdinal(a, b) <= 0 ? a + "/" + b : b + "/" + a;

            // QuestingBots between two quest steps (a frame or two in its idle layer) is not a flip
            if (lasted < 1f && pair.Contains("BotObjectiveLayer"))
            {
                return;
            }

            if (!_churn.TryGetValue(bot.ProfileId, out Churn c))
            {
                _churn[bot.ProfileId] = c = new Churn();
            }

            if (c.Pair != pair)
            {
                c.Pair = pair;
                c.At.Clear();
            }

            c.At.RemoveAll(t => now - t > 60f);
            c.At.Add(now);
            if (c.At.Count < 4)
            {
                return;
            }

            c.At.Clear();
            string name = bot.Profile?.Nickname;
            if (pair.Contains("Looting"))
            {
                LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
                if (brain != null && !brain.IsBotLooting && !RoomSweep.InVisit(bot))
                {
                    LootingBots.External.PreventBotFromLooting(bot, 30f);
                    Calmed++;
                    Log.Info($"LR Calm: {name} flips between {a} and {b} - looting rests 30 s");
                }

                return;
            }

            if (pair.Contains("BotObjectiveLayer") && pair.Contains("BotRecoveryLayer"))
            {
                c.NoBreaksUntil = now + 60f;
                Calmed++;
                Log.Detail("brain", () => $"{name} flips between {a} and {b} - QuestingBots' loot breaks rest a minute");
            }
        }
    }
}
