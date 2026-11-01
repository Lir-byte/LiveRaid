using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using LiveRaid.Client.Patches;
using LiveRaid.Client.Tactics;
using QuestingBots.Helpers;
using SPT.Reflection.Patching;

namespace LiveRaid.Client
{
    [BepInPlugin(Guid, Name, Version)]
#if LIVERAID_ONLINE
    [BepInDependency("com.fika.core", "2.4.3")]
#endif
    [BepInDependency("xyz.drakia.bigbrain")]
    [BepInDependency("xyz.drakia.waypoints")]
    [BepInDependency("me.sol.sain")]
    [BepInDependency("com.danw.questingbots")]
    [BepInDependency("me.skwizzy.lootingbots")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.lir.liveraid";
#if LIVERAID_ONLINE
        public const string Name = "Lir-LiveRaidOnline";
        public const string Display = "LiveRaid Online";
#else
        public const string Name = "Lir-LiveRaid";
        public const string Display = "LiveRaid";
#endif
        public const string Version = "1.0.5";

        /// <summary>Branch of the mod: "" - the stable one (main), "test" - the test branch (experimental), switched by LiveRaid Launcher.</summary>
        public const string Channel = "";

        /// <summary>The version as the logs show it: "0.10.12 (test)" on the test branch.</summary>
        public const string VersionText = Version + (Channel == "" ? "" : " (" + Channel + ")");

        internal static ManualLogSource Log;
        internal static string PluginDir;

        // Features whose patches failed to apply; the rest of the mod keeps working
        internal static readonly HashSet<string> DisabledFeatures = new HashSet<string>();

        private void Awake()
        {
            Log = Logger;
            PluginDir = System.IO.Path.GetDirectoryName(Info.Location);
            try
            {
                Settings.Bind(Config);
            }
            catch (Exception e)
            {
                // Without its settings nothing can run; say so where the user looks (the exception alone went only to Unity's Player.log)
                Log.LogError("LiveRaid could not read its F12 settings and is OFF this session: " + e);
                return;
            }

            // LiveRaid Online: the host's settings for everybody (before the master switch - the host may have turned it on or off)
            try
            {
                SettingsSync.Init(Config);
            }
            catch (Exception e)
            {
                Log.LogError("LiveRaid Online: the shared settings could not be set up: " + e);
            }

            // A wider F12 window that can be resized by its corner (ConfigurationManager's is fixed at 650 px)
            ConfigWindow.Bind(Config);
            Enable("f12 window", new CmWindowRectPatch(), new CmOnGuiPatch(), new CmSettingsWindowPatch());

            if (!Settings.Enabled.Value)
            {
                Log.LogWarning("LiveRaid is disabled in its settings");
                return;
            }

#if !LIVERAID_ONLINE
            // With Project Fika every raid is a Fika raid (the bots live on the host, keys must be sent to the other players, Fika has its own
            // camera after death): the single-player edition would break things there
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
            {
                Log.LogError("Project Fika is installed: this is LiveRaid for single player and it stays OFF. Install LiveRaid Online (LiveRaid-Online-<version>.zip) instead.");
                return;
            }
#else
            OnlineSetup.Init();
#endif

            Enable("raid", new RaidStartPatch(), new RaidEndPatch(), new BotActivatePatch());
            Enable("keys", new QbAllowUnlockPatch(), new QbFindLockedDoorPatch());
            Enable("quests", new QbCustomQuestsPatch(), new QbCanAssignBotPatch(), new QbDesirabilityPatch(), new QbLootAfterObjectivePatch());
            Enable("scav patrols", new Bots.QbScavObjectivePatch());
            Enable("grenade habit", new Tactics.SainGrenadeHabitPatch());
            Enable("third party turn", new Tactics.SainTurnLookPatch());
            Enable("head on a swivel", new Bots.SwivelLookPatch());
            Enable("airdrop event", new Tactics.QbAirdropLandedPatch());
            Enable("night lights", new Tactics.SainLightPatch(), new Tactics.SainLightSearchPatch());
            Enable("personality", new SainPersonalityPatch(), new SainSearchTimePatch());
            Enable("lod", new SainLodPatch());
            Enable("spawns", new QbRandomSpawnPointPatch(), new QbInitialSpawnDistancePatch(), new QbPmcReservePatch(), new QbPmcAliveCapPatch(), new QbPmcGroupSizePatch(), new QbStartLobbyGroupPatch(), new QbPScavCanSpawnPatch(), new QbPScavPlacesPatch(), new Bots.QbPScavPlacingPatch());
            Enable("personal extract", new QbPersonalExtractPatch());
            Enable("extract", new QbScavNoExtractPatch());
            Enable("loot thinking", new ShotHeardPatch(), new LbCorpseDecisionPatch(), new LbStartLootingPatch(), new LbStopLootingPatch(), new LbLootTaskEndPatch(), new BotLayGetUpPatch(), new LbRoomRangePatch());
            Enable("loot pose", new LbTryLootPatch(), new LootSteadyLookPatch(), new LbCloseEnoughPatch(), new LbScanHoldPatch(), new LbCrouchWalkPatch(), new LbWoundedLootPatch(), new LootSpinGuardPatch(), new LbHandoverPatch());
            Enable("steady weapons", new LbUpdateActiveWeaponPatch(), new WeaponChangeChancePatch(), new WeaponTryChangePatch(), new WeaponTryChangeToMainPatch(),
                new WeaponChangeToMainPatch(), new WeaponChangeToSecondPatch(), new WeaponChangeToSlotPatch(), new WeaponTakeMainPatch(),
                new WeaponDistanceChangePatch(), new WeaponNoAmmoChangePatch());
            Enable("knife or gun", new Tactics.KnifeOnlyAtArmsReachPatch());
            Enable("pursuit guard", new Bots.PursuitLayerPatch());
            Enable("no jumping on broken legs", new NoJumpPatch(), new NoJumpAnimatorPatch());
            Enable("sprint on a hurt leg", new Bots.LegSprintPatch());
            Enable("gun state", new Tactics.GunStateHandsPatch(), new Tactics.GunStateSlotPatch());
            Enable("keep magazines", new Tactics.MagKeeperReloadPatch());
            Enable("bottomless ammo", new Tactics.BottomlessMagazinePatch(), new Tactics.SummonedGridStatsPatch());
            Enable("facing the way out", new ExfilFacingPatch());
            Enable("loot not cut by QuestingBots' hearing", new QbLootPreventPatch());
            Enable("squad ties", new Bots.SquadTiesAddEnemyPatch(), new Bots.SquadTiesMemoryPatch(), new Bots.SquadTiesIsEnemyPatch());
            Enable("btr patience", new Bots.BtrPatiencePatch());
            Enable("usable medkits", new Tactics.FirstAidRefreshPatch(), new Tactics.FirstAidDamagedPartPatch(), new Tactics.SurgeryKitRefreshPatch());
            Enable("loot given first", new QbRecoveryYieldsToLootPatch());
            Enable("short QuestingBots holds", new QbShortHoldPatch());
            Enable("tempo like players", new Bots.QbWaitAfterStepPatch(), new Bots.QbLootScanPatch());
            Enable("wounded without meds go on", new QbNeedsToHealPatch());
            Enable("fallen squadmate loot", new MatePickupPatch(), new MateValuePatch());
            Enable("fallen squadmate whole kit", new MateKitOrderPatch(), new MateKitWholeBagPatch());
            Enable("squad extract", new Bots.QbFollowerExtractPatch(), new Bots.ExitChoicePatch(), new Bots.ExitTimerPatch());
            Enable("squads", new QbFormationPatch());
            Enable("combat morale", new SainCombatDecisionPatch(), new SainSquadDecisionPatch());
            Enable("distant fights", new SainDistantEnemyPatch());
            Enable("no dogfight with far enemies", new SainDogFightPatch(), new SainDogFightClearPatch());
            Enable("bushes between bots", new Tactics.BushSightPatch());
            Enable("loot from the right side of the wall", new Tactics.LbLootSpotPatch());
            Enable("surgery when safe", new Tactics.SurgeryGatePatch());
            Enable("search pace", new SearchPacePatch(), new SearchWaitPatch());
            Enable("pmc hostility", new PmcSameSideHostilityPatch());
            Enable("quest tasks", new Bots.QbCompleteObjectivePatch());
            Enable("quest ledger", new Raid.QuestLedgerCompletePatch(), new Raid.QuestLedgerFailPatch(), new Raid.QuestLedgerAssignPatch(), new Raid.QuestLedgerUnlockPatch());
            Enable("routes", new Bots.BotMoverTraversePatch(), new Bots.QbRouteAreaCheckPatch(), new Bots.QbRouteAreaAssignPatch(), new Bots.QbRouteAreaAssignStepPatch());
            Enable("no sleeping bots", new QbSleepingPatch());
            Enable("reactions", new ExfilUnderFirePatch(), new QbDecisionPatch());
            Enable("extract log", new BotLeftRaidPatch());
            Enable("squad loot", new LootShareCapturePatch());
            Enable("loot speed", new LbLootSpeedPatch());
            Enable("make room", new Tactics.LbMakeRoomPickupPatch(), new Tactics.LbMakeRoomAllowPatch());
#if !LIVERAID_ONLINE
            // After death (free camera, taking over a bot) and observer raids: single player only. In Fika the raid goes on for the others and
            // Fika has its own spectating; a bot taken over on one machine would desync on the rest
            Enable("spectator", new SpectatorDeathPatch(), new SpectatorStopPatch(), new SpectatorDeathEffectsPatch(), new SpectatorListenerPatch());
            Enable("observer", new ObserverAddEnemyPatch(), new ObserverEnemiesAddPatch(), new ObserverCheckAndAddEnemyPatch(), new ObserverIsPlayerEnemyPatch(),
                new ObserverIsEnemyPatch(), new ObserverDamagePatch(), new ObserverKillPatch(), new ObserverDestroyBodyPartPatch(), new ObserverWarnPatch());
            Enable("take over", new TakenBotUpdatePatch(), new TakenBotFixedUpdatePatch(), new TakenBotSainPatch(), new TakenBotInventoryPatch());
#endif
            RegisterLayers();

            // LiveRaid sets the QuestingBots / SAIN values its features depend on (in memory, their files untouched)
            ModControl.Hook();
            ModControl.Apply();

            Log.LogInfo($"{Display} {VersionText} loaded" + (DisabledFeatures.Count > 0 ? "; disabled: " + string.Join(", ", DisabledFeatures) : ""));
        }

        private void Update()
        {
            try
            {
                Loc.Poll();
                SettingsMenu.Tick();
                SettingsSync.Tick();
            }
            catch (Exception e)
            {
                Diag.Report("F12 menu", e);
            }
        }

        /// <summary>
        /// Brains LiveRaid's characters can have. SPT gives PMCs the PmcUsec/PmcBear brain, scavs Assault, and player scavs Assault,
        /// PmcUsec/PmcBear or Killa (SPT bot.json playerScavBrainType). Bosses with the Killa brain are not touched: every layer checks
        /// the bot's LiveRaid kind first and stays off for bosses, followers and other special roles.
        /// </summary>
        private static List<string> CharacterBrains() => new List<string> { "PMC", "PmcUsec", "PmcBear", "Assault", "CursAssault", "Killa" };

        private static void RegisterLayers()
        {
            try
            {
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(TacticsLayer), CharacterBrains(), TacticsLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.ContainerUnlockLayer), CharacterBrains(), Bots.ContainerUnlockLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(DisengageLayer), CharacterBrains(), DisengageLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.HealLayer), CharacterBrains(), Bots.HealLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.BackOffLayer), CharacterBrains(), Bots.BackOffLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.DoorAssistLayer), CharacterBrains(), Bots.DoorAssistLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.SwitchExtractLayer), CharacterBrains(), Bots.SwitchExtractLayer.LayerPriority);
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.QuestTaskLayer),
                    new List<string> { "PMC", "PmcUsec", "PmcBear" }, Bots.QuestTaskLayer.LayerPriority);

                // Every bot that walks through a route needs the climb, bosses included (their paths can cross a route too)
                List<string> allBrains = QuestingBots.Helpers.BotBrainHelpers.GetAllNonSniperBrains().AddAllSniperBrains().ToStringList();
                DrakiaXYZ.BigBrain.Brains.BrainManager.AddCustomLayer(typeof(Bots.RouteTraverseLayer), allBrains, Bots.RouteTraverseLayer.LayerPriority);
            }
            catch (Exception e)
            {
                DisabledFeatures.Add("brain layers");
                Log.LogError("could not register the LiveRaid brain layers: " + e);
            }
        }

        private static void Enable(string feature, params ModulePatch[] patches)
        {
            foreach (ModulePatch patch in patches)
            {
                try
                {
                    patch.Enable();
                }
                catch (Exception e)
                {
                    DisabledFeatures.Add(feature);
                    Log.LogError($"[{feature}] {patch.GetType().Name} failed to apply, feature '{feature}' may be incomplete: {e.Message}");
                }
            }
        }
    }
}
