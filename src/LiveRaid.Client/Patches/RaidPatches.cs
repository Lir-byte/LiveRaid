using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Raid;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>Raid start: reset per-raid state and attach the per-frame worker.</summary>
    public class RaidStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BotsController).GetMethod(nameof(BotsController.Init), BindingFlags.Public | BindingFlags.Instance);

        [PatchPostfix]
        protected static void PatchPostfix()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            // LiveRaid Online: the raid runs with the host's settings as they are now
            SettingsSync.PullNow();
            if (!Net.RunsBots)
            {
                // Fika client: the bots of this raid live on the host, LiveRaid runs there
                Log.Info($"raid on {world.LocationId}: {Plugin.Display} {Plugin.VersionText} - this machine is a Fika client, the host runs the bots and LiveRaid");
                return;
            }

            try
            {
                Diag.BeginRaid(world.LocationId);
                BotProfiles.Clear();
                BotSetup.Clear();
                Tactics.MateKit.Clear();
                ExtractPlanner.Clear();
                Tactics.TacticalTasks.Clear();
                Tactics.LootBrain.Clear();
                Tactics.SquadBrain.Clear();
                Formation.Clear();
                Tactics.CombatSense.Clear();
                PmcSameSideHostilityPatch.Clear();
                RegionMemory.Clear();
                Tactics.GunfireResponse.Clear();
                ContainerUnlockLayer.Clear();
                QuestTasks.Clear();
                RouteTraverseLayer.Clear();
                RouteAreaQuota.Clear();
                DoorAssistLayer.Clear();
                RequestWatch.Clear();
                Tactics.HitReaction.Clear();
                Tactics.SquadAssist.Clear();
                Tactics.Maneuvers.Clear();
                Tactics.RoomSweep.Clear();
                Tactics.SquadLoot.Clear();
                Bots.Harmonizer.Clear();
                Tactics.MakeRoom.Clear();
                Tactics.SquadShare.Clear();
                Tactics.RangeSense.Clear();
                MoveWatch.Clear();
                BotMind.Clear();
                BrainWatch.Clear();
                Tactics.DecisionHold.Clear();
                SainDistantEnemyPatch.Clear();
                SainDogFightPatch.Clear();
                Tactics.BushSightPatch.Clear();
                Tactics.EnemyLapse.Clear();
                SearchPacePatch.Clear();
                LootFacing.Clear();
                Raid.QuestLedger.Clear();
                QbRandomSpawnPointPatch.Clear();
                LbCloseEnoughPatch.Accepted = 0;
                LbCrouchWalkPatch.Clear();
                Tactics.LootReach.Clear();
                SainSquadDecisionPatch.Clear();
                Tactics.TacticalTasks.ClearCounters();
                Bots.HealLayer.ClearCounters();
                Bots.BackOffLayer.Clear();
                Tactics.SurgeryGatePatch.Clear();
                Tactics.SainHandover.Clear();
                Tactics.LootWatch.Clear();
                Tactics.ProneLoot.Clear();
                Tactics.LootAudit.Clear();
                ExfilFacingPatch.Clear();
                QbLootPreventPatch.Clear();
                Bots.PursuitGuard.Clear();
                Tactics.WeaponSteadiness.Clear();
                Tactics.LootDanger.Clear();
                Tactics.MateRecovery.Clear();
                Tactics.MeleeSwitch.Clear();
                ShutIn.Clear();
                IdleBreaker.Clear();
                QbNeedsToHealPatch.ClearCounters();
                Tactics.SafeReload.Clear();
                Tactics.SidearmCare.Clear();
                Raid.MapBalance.Clear();
                Tactics.HandsGuard.Clear();
                Tactics.GunState.Clear();
                Tactics.HealBreak.Clear();
                Tactics.CloseSense.Clear();
                Tactics.FireDiscipline.Clear();
                Tactics.BlindFireHold.Clear();
                FrozenWatch.Clear();
                LayerCalm.Clear();
                QbLootWalkPatch.Clear();
                Tactics.Intercept.Clear();
                Tactics.PressHurt.Clear();
                Tactics.MagKeeper.Clear();
                Tactics.Bottomless.Clear();
                NetWatch.Clear();
                Tactics.AimWatch.Clear();
                SquadTies.Clear();
                QuestDrive.Clear();
                ExitPlan.Clear();
                BtrPatience.Clear();
                ExfilFirst.Clear();
                Tactics.ReloadUnderFire.Clear();
                Tactics.FightRoles.Clear();
                Tactics.SquadDoctrines.Clear();
                Tactics.SquadPlaybook.Clear();
                Tactics.SquadComms.Clear();
                Tactics.PlayerSense.Clear();
                Tactics.GrenadeHabit.Clear();
                PScavReplacement.Clear();
                Raid.PopulationBalance.Clear();
                Raid.Corridors.Clear();
                Tempo.Clear();
                DoorManners.Clear();
                RoomPeek.Clear();
                Packrat.Clear();
                PerfProbe.Clear();
                ExitRules.Clear();
                Load.Clear();
                FoundKeys.Clear();
                ExitArrival.Clear();
                CarExits.Clear();
                Tactics.ShedWeight.Clear();
                Tactics.FireTricks.Clear();
                Swivel.Clear();
                Overwatch.Clear();
                Tactics.RaidMood.Clear();
                Tactics.NightDiscipline.Clear();
                Tactics.AirdropEvent.Clear();
                CoverRoute.Clear();
                Tactics.NoiseResponse.Clear();
                ScavPatrol.Clear();
                PlayerScavLife.Clear();
                BossHunt.Clear();
                Tactics.SightCover.Clear();
                LootSpinGuardPatch.Clear();
                LbHandoverPatch.Clear();
                LbWoundedLootPatch.ClearCounters();
                LegCheck.ClearCounters();
                LegCare.ClearCounters();
                LegSprint.ClearCounters();
                Tactics.MedsCheck.ClearCounters();
                QbRecoveryYieldsToLootPatch.ClearCounters();
                QbShortHoldPatch.ClearCounters();
                Bots.ExtractPlanner.ClearDeferrals();
                Tactics.LootRules.ClearCounters();
                Tactics.LootJobs.Clear();
                Tactics.BodySight.Clear();
                PrizeKeys.Clear();
                LootKick.Clear();
                SainCombatDecisionPatch.ClearCounters();
                KeyPlaces.Clear();
                SquadExtract.Clear();
                RoamPlanner.Clear();
                SwitchExtract.Clear();
                QbDecisionPatch.Clear();
                QbDecisionPatch.ClearHeal();
                BotLeftRaidPatch.Clear();
                LootShareCapturePatch.Clear();
                QuestAudit.Clear();
                RaidMonitor.Clear();
                Spectator.SpectatorSession.Clear();
                Tactics.WorldEvents.Begin();
                ModControl.Apply();
                MapIntel.BeginRaid(world.LocationId);
                CoverageLog.Begin(world.LocationId);
                PlayerTrail.Begin(world.LocationId);
                EncounterLog.Begin(world.LocationId);
                world.gameObject.GetOrAddComponent<RaidWorker>();
                Log.Info($"raid on {world.LocationId}: {Plugin.Display} {Plugin.VersionText} active ({Net.Role}), log detail {Log.Level}" +
                         (Trace.LastPath != null ? $", trace logs/{System.IO.Path.GetFileName(Trace.LastPath)}" : ""));
            }
            catch (System.Exception e)
            {
                Log.Error("LiveRaid raid start failed, some features may not work this raid: " + e);
            }
        }
    }

    public class RaidEndPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(GameWorld).GetMethod(nameof(GameWorld.Dispose), BindingFlags.Public | BindingFlags.Instance);

        [PatchPostfix]
        protected static void PatchPostfix()
        {
            if (MapIntel.Current == null && !Net.RunsBots)
            {
                // A Fika client never started LiveRaid in this raid
                return;
            }

            string location = MapIntel.Current?.LocationId ?? "raid";
            Diag.Guard("raid end: encounters", EncounterLog.End);

            // Before map intel goes: the last trail points are checked against the bots' map
            Diag.Guard("raid end: player trail", PlayerTrail.End);
            Diag.Guard("raid end: map intel", MapIntel.EndRaid);
            Diag.Guard("raid end: routes", Routes.Remove);

            // The raid summary reads per-raid counters (doors, hostility): before they are cleared
            Diag.Guard("raid end: brain watch", () =>
            {
                string brain = BrainWatch.Summary(location);
                if (brain != null)
                {
                    Log.Info(brain);
                }
            });
            Diag.Guard("quest ledger", Raid.QuestLedger.ReportEnd);
            Diag.EndRaid(location);
            Diag.Guard("raid end: state", () =>
            {
                QuestTasks.Clear();
                RouteTraverseLayer.Clear();
                DoorAssistLayer.Clear();
                Tactics.WorldEvents.End();
                Tactics.TacticalTasks.Clear();
                Tactics.LootBrain.Clear();
                Tactics.SquadBrain.Clear();
                Tactics.CombatSense.Clear();
                Tactics.Maneuvers.Clear();
                Tactics.RoomSweep.Clear();
                Tactics.SquadLoot.Clear();
                Bots.Harmonizer.Clear();
                Tactics.MakeRoom.Clear();
                Tactics.SquadShare.Clear();
                Tactics.RangeSense.Clear();
                Tactics.WeaponSteadiness.Clear();
                Tactics.LootDanger.Clear();
                Tactics.MateRecovery.Clear();
                Tactics.MeleeSwitch.Clear();
                ShutIn.Clear();
                IdleBreaker.Clear();
                QbNeedsToHealPatch.ClearCounters();
                Tactics.SafeReload.Clear();
                Tactics.SidearmCare.Clear();
                Raid.MapBalance.Clear();
                Tactics.HandsGuard.Clear();
                Tactics.GunState.Clear();
                Tactics.HealBreak.Clear();
                Tactics.CloseSense.Clear();
                Tactics.FireDiscipline.Clear();
                Tactics.BlindFireHold.Clear();
                FrozenWatch.Clear();
                LayerCalm.Clear();
                QbLootWalkPatch.Clear();
                Tactics.Intercept.Clear();
                Tactics.PressHurt.Clear();
                Tactics.MagKeeper.Clear();
                Tactics.Bottomless.Clear();
                NetWatch.Clear();
                Tactics.AimWatch.Clear();
                SquadTies.Clear();
                QuestDrive.Clear();
                ExitPlan.Clear();
                BtrPatience.Clear();
                ExfilFirst.Clear();
                Tactics.ReloadUnderFire.Clear();
                Tactics.FightRoles.Clear();
                Tactics.SquadDoctrines.Clear();
                Tactics.SquadPlaybook.Clear();
                Tactics.SquadComms.Clear();
                Tactics.PlayerSense.Clear();
                Tactics.GrenadeHabit.Clear();
                DoorManners.Clear();
                RoomPeek.Clear();
                Packrat.Clear();
                PerfProbe.Clear();
                ExitRules.Clear();
                Load.Clear();
                FoundKeys.Clear();
                ExitArrival.Clear();
                CarExits.Clear();
                Tactics.ShedWeight.Clear();
                Tactics.FireTricks.Clear();
                Swivel.Clear();
                Overwatch.Clear();
                Tactics.RaidMood.Clear();
                Tactics.NightDiscipline.Clear();
                Tactics.AirdropEvent.Clear();
                CoverRoute.Clear();
                Tactics.NoiseResponse.Clear();
                ScavPatrol.Clear();
                PlayerScavLife.Clear();
                BossHunt.Clear();
                Tactics.SightCover.Clear();
                LootSpinGuardPatch.Clear();
                LbHandoverPatch.Clear();
                LbWoundedLootPatch.ClearCounters();
                LegCheck.ClearCounters();
                LegCare.ClearCounters();
                LegSprint.ClearCounters();
                Tactics.MedsCheck.ClearCounters();
                QbRecoveryYieldsToLootPatch.ClearCounters();
                QbShortHoldPatch.ClearCounters();
                Bots.ExtractPlanner.ClearDeferrals();
                Tactics.LootRules.ClearCounters();
                Tactics.LootJobs.Clear();
                Tactics.BodySight.Clear();
                PrizeKeys.Clear();
                KeyPlaces.ClearAll();
                SquadExtract.Clear();
                MoveWatch.Clear();
                BotMind.Clear();
                BrainWatch.Clear();
                Tactics.DecisionHold.Clear();
                SainDistantEnemyPatch.Clear();
                SainDogFightPatch.Clear();
                Tactics.BushSightPatch.Clear();
                Tactics.EnemyLapse.Clear();
                SainSquadDecisionPatch.Clear();
                PmcSameSideHostilityPatch.Clear();
                RegionMemory.Clear();
                Tactics.GunfireResponse.Clear();
                ContainerUnlockLayer.Clear();
                BotProfiles.Clear();
                BotSetup.Clear();
                Tactics.MateKit.Clear();
                Spectator.SpectatorSession.Clear();
                QbPmcReservePatch.InitialCount = 0;
            });
        }
    }

    /// <summary>BotOwner.method_10 is the brain activation (same hook QuestingBots uses to attach its objective manager).</summary>
    public class BotActivatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BotOwner).GetMethod("method_10", BindingFlags.Public | BindingFlags.Instance);

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner __instance)
        {
            try
            {
                SquadTies.Register(__instance);
                Tactics.SquadDoctrines.Register(__instance);
                BotSetup.OnActivated(__instance);
                PlayerScavLife.OnActivated(__instance);
                ExitPlan.Announce(__instance);
                Tactics.MateKit.Remember(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("BotSetup", e);
            }
        }
    }

    /// <summary>Spreads map analysis over frames while the raid loads, so the first quest build rarely has to wait.</summary>
    public class RaidWorker : MonoBehaviour
    {
        private float _startAt;
        private float _nextSnapshot;

        private void Start()
        {
            _startAt = Time.time + 1f;
            _nextSnapshot = Time.time + 300f;
        }

        /// <summary>
        /// The game closed in the middle of a raid (F12 -> 1 "Raid summary when the game is closed"): the raid's summary and the encounter log are written
        /// all the same - the raids of 2026-10-30/31 had none, the game was always closed before the raid ended.
        /// </summary>
        private void OnApplicationQuit()
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null || !Settings.SummaryOnQuit.Value)
            {
                return;
            }

            Diag.Guard("quit: encounters", EncounterLog.End);
            Diag.QuitRaid(intel.LocationId);
        }

        private void Update()
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null || Time.time < _startAt)
            {
                return;
            }

            // Every 5 minutes the summary so far goes into the trace (a raid cut short still has its numbers)
            if (Settings.SummaryOnQuit.Value && Time.time >= _nextSnapshot)
            {
                _nextSnapshot = Time.time + 300f;
                Diag.Guard("summary so far", () => Diag.Snapshot(intel.LocationId));
            }

            // Each system runs guarded and timed: one failing system never stops the others (see Diag, "LR Health" lines)
            Diag.Tick("MapIntel.Doors", intel.EnsureDoors);
            Diag.Tick("MapIntel.Pois", () => intel.StepPoiJob(4));
            Diag.Tick("Observer.Start", Spectator.ObserverMode.TryStart);
            Diag.Tick("Observer", Spectator.ObserverMode.Tick);
            Diag.Tick("BotMind", BotMind.Tick);
            Diag.Tick("BrainWatch", BrainWatch.Tick);
            Diag.Tick("TacticalTasks", Tactics.TacticalTasks.Tick);
            Diag.Tick("ExtractPlanner", ExtractPlanner.Tick);
            Diag.Tick("CoverageLog", CoverageLog.Tick);
            Diag.Tick("SquadDoctrines", Tactics.SquadDoctrines.Tick);
            Diag.Tick("SquadBrain", Tactics.SquadBrain.Tick);
            Diag.Tick("SquadComms", Tactics.SquadComms.Tick);
            Diag.Tick("ProneLoot", Tactics.ProneLoot.Tick);
            Diag.Tick("LootBrain", Tactics.LootBrain.Tick);
            Diag.Tick("CombatSense", Tactics.CombatSense.Tick);
            Diag.Tick("RegionMemory", RegionMemory.Tick);
            Diag.Tick("GunfireResponse", Tactics.GunfireResponse.Tick);
            Diag.Tick("NoiseResponse", Tactics.NoiseResponse.Tick);
            Diag.Tick("ScavPatrol", ScavPatrol.Tick);
            Diag.Tick("PlayerScavLife", PlayerScavLife.Tick);
            Diag.Tick("PScavReplacement", PScavReplacement.Tick);
            Diag.Tick("PopulationBalance", Raid.PopulationBalance.Tick);
            Diag.Tick("BossHunt", BossHunt.Tick);
            Diag.Tick("CoverRoute", CoverRoute.Tick);
            Diag.Tick("Corridors", Raid.Corridors.Tick);
            Diag.Tick("DoorManners", DoorManners.Tick);
            Diag.Tick("RoomPeek", RoomPeek.Tick);
            Diag.Tick("FoundKeys", FoundKeys.Tick);
            Diag.Tick("ExitArrival", ExitArrival.Tick);
            Diag.Tick("CarExits", CarExits.Tick);
            Diag.Tick("ShedWeight", Tactics.ShedWeight.Tick);
            Diag.Tick("MapBalance", Raid.MapBalance.Tick);
            Diag.Tick("SidearmCare", Tactics.SidearmCare.Tick);
            Diag.Tick("Overwatch", Overwatch.Tick);
            Diag.Tick("RaidMood", Tactics.RaidMood.Tick);
            Diag.Tick("NightDiscipline", Tactics.NightDiscipline.Tick);
            Diag.Tick("FireTricks", () => Tactics.FireTricks.Tick(Singleton<GameWorld>.Instance?.AllAlivePlayersList.Select(x => x?.AIData?.BotOwner) ?? Enumerable.Empty<BotOwner>()));
            Diag.Tick("EncounterLog", EncounterLog.Tick);
            Diag.Tick("QuestAudit", QuestAudit.Tick);
            Diag.Tick("RouteRecorder", Routes.TickRecorder);
            Diag.Tick("MapEdits", Studio.MapEdits.Tick);
            Diag.Tick("RaidMonitor", RaidMonitor.Tick);
            Diag.Tick("DoorCuts", DoorAssistLayer.Tick);
            Diag.Tick("RequestWatch", RequestWatch.Tick);
            Diag.Tick("HitReaction", Tactics.HitReaction.Tick);
            Diag.Tick("SquadAssist", Tactics.SquadAssist.Tick);
            Diag.Tick("RoamPlanner", RoamPlanner.Tick);
            Diag.Tick("Tempo", Tempo.Tick);
            Diag.Tick("Maneuvers", Tactics.Maneuvers.Tick);
            Diag.Tick("MoveWatch", MoveWatch.Tick);
            Diag.Tick("RoomSweep", Tactics.RoomSweep.Tick);
            Diag.Tick("MakeRoom", Tactics.MakeRoom.Tick);
            Diag.Tick("SquadShare", Tactics.SquadShare.Tick);
            Diag.Tick("RangeSense", Tactics.RangeSense.Tick);
            Diag.Tick("WeaponSteadiness", Tactics.WeaponSteadiness.Tick);
            Diag.Tick("LootDanger", Tactics.LootDanger.Tick);
            Diag.Tick("MateRecovery", Tactics.MateRecovery.Tick);
            Diag.Tick("LootJobs", Tactics.LootJobs.Tick);
            Diag.Tick("BodySight", Tactics.BodySight.Tick);
            Diag.Tick("PrizeKeys", PrizeKeys.Tick);
            Diag.Tick("MeleeSwitch", Tactics.MeleeSwitch.Tick);
            Diag.Tick("ShutIn", ShutIn.Tick);
            Diag.Tick("IdleBreaker", IdleBreaker.Tick);
            Diag.Tick("PursuitGuard", Bots.PursuitGuard.Tick);
            Diag.Tick("HandsGuard", Tactics.HandsGuard.Tick);
            Diag.Tick("HealBreak", Tactics.HealBreak.Tick);
            Diag.Tick("CloseSense", Tactics.CloseSense.Tick);
            Diag.Tick("SquadTies", SquadTies.Tick);
            Diag.Tick("QuestDrive", QuestDrive.Tick);
            Diag.Tick("NetWatch", NetWatch.Tick);
            Diag.Tick("MagKeeper", Tactics.MagKeeper.Tick);
            Diag.Tick("Bottomless", Tactics.Bottomless.Tick);
            Diag.Tick("AimWatch", Tactics.AimWatch.Tick);
            Diag.Tick("ReloadUnderFire", Tactics.ReloadUnderFire.Tick);
            Diag.Tick("Intercept", Tactics.Intercept.Tick);
            Diag.Tick("FrozenWatch", FrozenWatch.Tick);
            Diag.Tick("Packrat", Packrat.Tick);
            Diag.Tick("SafeReload", Tactics.SafeReload.Tick);
            Diag.Tick("KeyPlaces", KeyPlaces.Tick);
            Diag.Tick("SquadExtract", SquadExtract.Tick);
            Diag.Tick("PlayerTrail", PlayerTrail.Tick);
            Trace.Tick();
        }
    }
}
