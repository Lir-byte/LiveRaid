using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveRaid.Client
{
    /// <summary>
    /// Keeps LiveRaid from ever breaking the game: every per-frame system and every Harmony hook runs guarded. Errors are counted per area
    /// and logged with a stack trace the first few times, then only counted; a system that keeps failing is switched off for the rest of the
    /// raid instead of spamming the log every frame. Also measures how long each system takes per tick.
    /// At raid end one "LR Health" line sums it up.
    /// </summary>
    internal static class Diag
    {
        private class Area
        {
            public string Name;
            public int Errors;
            public int Consecutive;
            public int Runs;
            public bool Disabled;
            public double TotalMs;
            public double MaxMs;
            public string LastError;
        }

        private const int DisableAfterConsecutive = 25;
        private static readonly Dictionary<string, Area> _areas = new Dictionary<string, Area>();
        private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;

        public static void BeginRaid(string location)
        {
            _areas.Clear();
            Trace.Begin(location);
        }

        public static void EndRaid(string location)
        {
            try
            {
                Log.Info(Summary(location));
            }
            catch
            {
                // never fail the raid end over a log line
            }

            Trace.End();
        }

        /// <summary>The game is closing in the middle of a raid: the raid's summary, marked as cut short, and the trace written out.</summary>
        public static void QuitRaid(string location)
        {
            try
            {
                float elapsed = QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds();
                Log.Info(Summary(location).Replace($"LR Health on {location}:", $"LR Health on {location} (the game was closed at {elapsed / 60f:0}:{elapsed % 60f:00} of the raid):"));
            }
            catch
            {
                // never fail the quit over a log line
            }

            Trace.End();
        }

        /// <summary>The summary so far, into the trace only (every 5 minutes).</summary>
        public static void Snapshot(string location)
        {
            if (Trace.Active)
            {
                Trace.Write("health", Summary(location).Replace($"LR Health on {location}:", $"LR Health so far on {location}:"));
            }
        }

        private static Area Get(string name)
        {
            if (!_areas.TryGetValue(name, out Area a))
            {
                _areas[name] = a = new Area { Name = name };
            }

            return a;
        }

        /// <summary>Runs one system's tick: timed, guarded, switched off after many failures in a row.</summary>
        public static void Tick(string name, Action action)
        {
            Area a = Get(name);
            if (a.Disabled)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                action();
                a.Consecutive = 0;
            }
            catch (Exception e)
            {
                a.Consecutive++;
                Report(a, e);
                if (a.Consecutive >= DisableAfterConsecutive)
                {
                    a.Disabled = true;
                    Log.Error($"LR Health: {name} failed {a.Consecutive} times in a row and is switched off until the next raid (last error: {a.LastError})");
                }
            }

            double ms = (Stopwatch.GetTimestamp() - start) * _msPerTick;
            a.Runs++;
            a.TotalMs += ms;
            if (ms > a.MaxMs)
            {
                a.MaxMs = ms;
            }
        }

        /// <summary>An exception caught in a hook or a layer: logged in full the first times, then counted.</summary>
        public static void Report(string area, Exception e) => Report(Get(area), e);

        private static void Report(Area a, Exception e)
        {
            a.Errors++;
            a.LastError = $"{e.GetType().Name}: {e.Message}";
            if (a.Errors <= 3)
            {
                Log.Error($"[{a.Name}] {e}");
            }
            else if (a.Errors % 100 == 0)
            {
                Log.Error($"[{a.Name}] {a.Errors} errors so far (shown in full only the first 3 times), last: {a.LastError}");
            }
            else
            {
                Log.Detail("error", $"{a.Name}: {a.LastError}");
            }
        }

        /// <summary>Runs a hook body; an exception is reported instead of reaching the game or the other mod.</summary>
        public static void Guard(string area, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Report(area, e);
            }
        }

        /// <summary>A hook's yes/no decision, guarded: on an exception the original behaviour is kept (true).</summary>
        public static bool Allow(Func<bool> decide, string area)
        {
            try
            {
                return decide();
            }
            catch (Exception e)
            {
                Report(area, e);
                return true;
            }
        }

        public static int ErrorCount => _areas.Values.Sum(a => a.Errors);

        /// <summary>Slowest systems and error counts, for the periodic health line.</summary>
        public static string TimingLine(int top)
        {
            return string.Join(", ", _areas.Values.Where(a => a.Runs > 0).OrderByDescending(a => a.TotalMs / a.Runs).Take(top)
                .Select(a => $"{a.Name} {a.TotalMs / a.Runs:0.00}/{a.MaxMs:0.0} ms"));
        }

        public static string ErrorLine()
        {
            List<Area> failing = _areas.Values.Where(a => a.Errors > 0).OrderByDescending(a => a.Errors).ToList();
            return failing.Count == 0 ? "none" : string.Join(", ", failing.Select(a => $"{a.Name} {a.Errors}{(a.Disabled ? " (switched off)" : "")}"));
        }

        private static string Summary(string location)
        {
            return $"LR Health on {location}: errors {ErrorLine()}; per tick avg/max: {TimingLine(6)}" +
                   $"; same-side PMC squads made enemies by LiveRaid: {Patches.PmcSameSideHostilityPatch.Flipped}" +
                   $"; unstick: doors opened {Bots.DoorAssistLayer.Opened}, side steps / slips past leaves {Bots.DoorAssistLayer.BackedOff}, unseen nudges {Bots.DoorAssistLayer.Nudged}, gave up {Bots.DoorAssistLayer.GaveUp}, doors cut out of the navmesh {Bots.DoorAssistLayer.CutOut}" +
                   $"; squad requests released: {Bots.RequestWatch.Released}" +
                   $"; bots that left through an exit: {Patches.BotLeftRaidPatch.Left}, through a switch exit: {Bots.SwitchExtract.LeftThrough} (switches pulled {Bots.SwitchExtract.Pulled}); {Bots.NoWayOut.Summary}; {Bots.BackpackExit.Summary}" +
                   $"; {Raid.FreshSteps.Summary}; {Raid.Depths.Summary}; {Tactics.DangerMemory.Summary}; {Tactics.Caution.Summary}" +
                   $"; hit reactions {Tactics.HitReaction.Reactions}, near-miss reactions {Tactics.HitReaction.NearMisses}, squad calls for help {Tactics.SquadAssist.Calls}, QuestingBots idle overruled {Patches.QbDecisionPatch.Overruled}" +
                   $", moved on from a place {Bots.RoamPlanner.MovedOn}, searched loot shared with squads {Patches.LootShareCapturePatch.Shared}" +
                   $"; flanks {Tactics.Maneuvers.Flanks} (ended in contact {Tactics.Maneuvers.FlankContacts}), spread out {Tactics.Maneuvers.Spreads}, went to trade a fallen squadmate {Tactics.Maneuvers.Trades}" +
                   $"; new places learned from your route {Raid.PlayerTrail.Added}" +
                   $"; spawns: {Patches.QbRandomSpawnPointPatch.Summary}" +
                   $"; quests: {Raid.QuestLedger.Summary()}" +
                   $"; combat: left a lost fight {Tactics.CombatSense.Flees}; looting: bodies gone to after a kill {Tactics.LootBrain.KillScans}, faced the loot steadily {Patches.LootFacing.Steadied}, near enough accepted {Patches.LbCloseEnoughPatch.Accepted}, kept from spinning on the way {Patches.LbCrouchWalkPatch.Unspun} ticks, unreachable loot given up {Patches.LbCrouchWalkPatch.GaveUp}" +
                   $"; {Tactics.EnemyLapse.Summary}; SAIN search kept at full pace {Patches.SearchPacePatch.Summary}" +
                   $"; stuck while moving (any layer): {Bots.MoveWatch.Summary}; loot rooms: {Tactics.RoomSweep.Summary}; squad looting: {Tactics.SquadLoot.Summary}; {Bots.Harmonizer.Summary} (looting started again for a handed-over thing {Patches.LbHandoverPatch.Restarted})" +
                   $"; cheap stuff dropped for a better find {Tactics.MakeRoom.Swaps} (refused: {Tactics.MakeRoom.RefusalSummary}; full bag, a find taken into its last cells {Tactics.MakeRoom.ReserveTaken}), full bags sent to a rich body {Tactics.MakeRoom.Scouts}; squad handovers: {Tactics.SquadShare.Summary}" +
                   $"; QuestingBots stalled healing given up {Patches.QbDecisionPatch.HealGiveUps}; {Bots.HealLayer.Summary}; {Bots.BackOffLayer.Summary}; surgery starts held back while an enemy is about {Tactics.SurgeryGatePatch.Blocked}, hand-overs from SAIN layers {Tactics.SainHandover.Freed}; {Tactics.LootWatch.Summary}; {Tactics.ProneLoot.Summary}; {Tactics.LootAudit.Summary}; loot jobs QuestingBots was kept from cutting {Patches.QbLootPreventPatch.Kept}, {Patches.ExfilFacingPatch.Summary}; {Bots.CarExits.Summary}; {Tactics.ShedWeight.Summary}; {Bots.PursuitGuard.Summary}; left the raid early by own decision {Bots.ExtractPlanner.EarlyLeavers}; {Bots.ExtractClock.Summary}; sent away from a long stay {Bots.RoamPlanner.Evictions} (no place found {Bots.RoamPlanner.EvictionsFailed}, of them after an overstay among fights and bodies {Bots.RoamPlanner.Overstays}); smoothing: mind phase changes {Bots.BotMind.Changes}, SAIN manoeuvres carried on {Tactics.DecisionHold.Held}, lesser tasks refused {Tactics.TacticalTasks.Refused}" +
                   $"; {Tactics.WeaponSteadiness.Summary}; {Tactics.SidearmCare.Summary}; SAIN dogfights refused for enemies with no path {Patches.SainDogFightPatch.Refused}; {Tactics.BushSightPatch.Summary}; {Tactics.LootReach.Summary}; {Raid.MapBalance.Summary}; {Raid.CoverageLedger.Summary}; {Raid.Atlas.Summary}; {Bots.FollowerSweep.Summary}; {Patches.QbFollowerWaitPatch.Summary}; {Patches.QbBreakOnTheWayPatch.Summary}; {Raid.QbEmptyQuestListPatch.Summary}; {Tactics.SquadBrain.RolesSummary}; {Tactics.MeleeSwitch.Summary}; {Tactics.Bottomless.Summary}; {Bots.KeyErrand.Summary}; {Bots.RaidAgenda.Summary}; {Raid.VerticalLedger.Summary}; {Bots.SquadKeys.Summary}; {Tactics.RoomSweep.SharedSummary}; {NetWatch.Summary}; {Bots.ShutIn.Summary}; {Bots.IdleBreaker.Summary}; QuestingBots heal stops skipped (bandaging is LiveRaid's, or no meds) {Patches.QbNeedsToHealPatch.Ignored} ticks; LootingBots one-frame flickers with a full bag stopped {Patches.LbWoundedLootPatch.Flickers}; dry guns reloaded by the watcher {Tactics.SafeReload.Dry}; {Tactics.HandsGuard.Summary}; {Tactics.GunState.Summary}; {Tactics.HealBreak.Summary}; {Tactics.CloseSense.Summary}; {Tactics.FireDiscipline.Summary}; {Tactics.BlindFireHold.Summary}; {Bots.FrozenWatch.Summary}; {Bots.LayerCalm.Summary}; loot walks not cut by the 7 s scan window of QuestingBots {Patches.QbLootWalkPatch.Held}; {Tactics.PressHurt.Summary}; {Bots.SquadTies.Summary}; {Bots.QuestDrive.Summary}; {Tactics.MagKeeper.Summary}; {Tactics.ReloadWorthPatch.Summary}; {Patches.SpawnZones.Summary}; {Tactics.AimWatch.Summary}; {Bots.ExitPlan.Summary}; {Bots.BtrPatience.Summary}; {Patches.ExfilFirst.Summary}; {Tactics.ReloadUnderFire.Summary}; {Tactics.FightRoles.Summary}; {Tactics.SquadDoctrines.Summary}; {Tactics.SquadPlaybook.Summary}; {Tactics.SquadComms.Summary}; {Raid.LongPath.Summary}; {Raid.AutoLinks.Summary}; {Patches.RaiderPScavHostilityPatch.Summary}; {Bots.GluharFeud.Summary}; {Bots.ExitCover.Summary}; squads stopped to listen {Tactics.GunfireResponse.Listens}; {Tactics.NoiseResponse.Summary}; {Tactics.Intercept.Summary}; {Bots.ScavPatrol.Summary}; {Bots.PlayerScavLife.Summary}; {Bots.PScavReplacement.Summary}; {Raid.PopulationBalance.Summary}; {Bots.BossHunt.Summary}; {Tactics.PlayerSense.Summary}; {Bots.CoverRoute.Summary}; {Raid.Corridors.Summary}; {Bots.Tempo.Summary}; {Tactics.GrenadeHabit.Summary}; {Bots.DoorManners.Summary}; {Bots.RoomPeek.Summary}; {Bots.Packrat.Summary}; {Bots.ArenaFightFix.Summary}; {Bots.PackratVacuum.Summary}; {Patches.PackratPool.Summary}; {Patches.PerfProbe.Summary}; {Bots.ExitRules.Summary}; {Bots.FoundKeys.Summary}; {Bots.ExitArrival.Summary}; {Tactics.FireTricks.Summary}; glances about while watchful {Bots.Swivel.Glances}, high ground posts of long guns {Bots.Overwatch.Posts}; {Tactics.RaidMood.Summary}; {Tactics.NightDiscipline.Summary}; {Tactics.AirdropEvent.Summary}; stims before a storm {Tactics.SquadPlaybook.Stims}; reloads made for LiveRaid {Tactics.SafeReload.Done}; {Tactics.SightCover.Summary}; {Tactics.RangeSense.Summary}; facing held at the loot {Patches.LootSpinGuardPatch.Frames} frames; loot walks without the slow end {Patches.LbNoSlowEndPatch.Fast}, SAIN patrol stance held off at the loot {Patches.SainLootPatrolPatch.Held} ticks; hurt bots without meds let loot {Patches.LbWoundedLootPatch.Allowed} ticks; {Patches.LegCheck.Summary}; {Bots.LegCare.Summary}; {Bots.LegSprint.Summary}; {Tactics.MedsCheck.Summary}; QuestingBots idle layer gave way to loot {Patches.QbRecoveryYieldsToLootPatch.Yielded} ticks, QuestingBots ambush/hold steps shortened {Patches.QbShortHoldPatch.Shortened}; {Bots.ExtractPlanner.DeferSummary}; return fire instead of moving {Patches.SainCombatDecisionPatch.FireReturned}, held ground after a blocked move {Patches.SainCombatDecisionPatch.HeldGround}; {Tactics.LootDanger.Summary}; {Tactics.LootJobs.Summary}; {Tactics.BodySight.Summary}; {Bots.PrizeKeys.Summary}; wedged bots put back on the navmesh {Raid.RaidMonitor.Rescued}; {Spectator.CullingGuard.Summary}; {Bots.BotSetup.KeySummary}; {Bots.ContainerUnlockLayer.Summary}; {Patches.LootKick.Summary}; {Tactics.MateRecovery.Summary}, guns and armour of fallen squadmates taken into the bag {Tactics.LootRules.GearTaken}; {Tactics.MateKit.Summary}; {Raid.KeyPlaces.Summary}; {Bots.SquadExtract.Summary}" +
                   (Trace.LastPath != null ? $"; trace {Path.GetFileName(Trace.LastPath)}" : "");
        }
    }

    /// <summary>
    /// Per-raid trace file (BepInEx/plugins/LiveRaid/logs/&lt;map&gt;_&lt;time&gt;.log), written only with "Log detail" = Detailed or Full.
    /// Every line carries the raid time, so decisions, bot timelines and health snapshots of one raid can be read in order
    /// without the noise of other mods. The newest 20 files are kept.
    /// </summary>
    internal static class Trace
    {
        private const int KeepFiles = 20;
        private static readonly object _lock = new object();
        private static readonly StringBuilder _buffer = new StringBuilder();
        private static string _path;
        private static DateTime _nextFlush;

        public static string LastPath { get; private set; }

        public static bool Active => _path != null;

        public static void Begin(string location)
        {
            End();
            LastPath = null;
            if (!Log.IsDetailed)
            {
                return;
            }

            try
            {
                string dir = Path.Combine(Plugin.PluginDir, "logs");
                Directory.CreateDirectory(dir);
                foreach (FileInfo old in new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles - 1))
                {
                    old.Delete();
                }

                lock (_lock)
                {
                    _path = Path.Combine(dir, $"{location}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    LastPath = _path;
                    _buffer.Clear();
                    _buffer.Append("# LiveRaid ").Append(Plugin.VersionText).Append(" trace, map ").Append(location).Append(", detail ").Append(Log.Level)
                        .Append(", started ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
                    _buffer.Append("# columns: raid time | category | message\n");
                }
            }
            catch (Exception e)
            {
                _path = null;
                Plugin.Log?.LogWarning("LiveRaid trace file not available: " + e.Message);
            }
        }

        public static void Write(string category, string msg)
        {
            if (_path == null)
            {
                return;
            }

            string stamp = RaidClock();
            lock (_lock)
            {
                _buffer.Append(stamp).Append(" | ").Append(category).Append(" | ").Append(msg).Append('\n');
                if (_buffer.Length > 256 * 1024)
                {
                    FlushLocked();
                }
            }
        }

        /// <summary>Called every frame by the raid worker; writes to disk every few seconds.</summary>
        public static void Tick()
        {
            if (_path == null || DateTime.UtcNow < _nextFlush)
            {
                return;
            }

            _nextFlush = DateTime.UtcNow.AddSeconds(5);
            lock (_lock)
            {
                FlushLocked();
            }
        }

        public static void End()
        {
            lock (_lock)
            {
                FlushLocked();
                _path = null;
            }
        }

        private static void FlushLocked()
        {
            if (_path == null || _buffer.Length == 0)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, _buffer.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("LiveRaid trace write failed, trace stopped: " + e.Message);
                _path = null;
            }

            _buffer.Clear();
        }

        private static string RaidClock()
        {
            try
            {
                if (QuestingBots.Helpers.RaidHelpers.HasRaidStarted())
                {
                    float s = QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds();
                    return $"{(int)(s / 60):00}:{(int)(s % 60):00}";
                }
            }
            catch
            {
                // raid clock not available yet (loading)
            }

            return "--:--";
        }
    }
}
