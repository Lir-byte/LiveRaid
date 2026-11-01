using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using QuestingBots.Configuration;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// LiveRaid is the one control panel over the mods it builds on. Every value of QuestingBots / SAIN that a LiveRaid feature
    /// depends on is set here, in memory, from LiveRaid's own F12 switches - their config files stay as their authors ship them.
    /// The mod's own value is remembered on first touch and put back when the LiveRaid switch that owns it is turned off,
    /// so turning a LiveRaid feature off really gives the other mod's behaviour back.
    /// Applied at plugin start, at every raid start and whenever an owning switch changes.
    /// </summary>
    public static class ModControl
    {
        private static readonly Dictionary<string, object> Original = new Dictionary<string, object>();
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>The PMC squad sizes of an online lobby: half solos and duos, a few full squads.</summary>
        private static readonly double[][] OnlineGroupSizes = { new[] { 1d, 30 }, new[] { 2d, 30 }, new[] { 3d, 25 }, new[] { 4d, 10 }, new[] { 5d, 5 } };

        /// <summary>
        /// With "Bigger lobby" (+5 PMCs): more solos without fewer squads. Old mix, 10 PMCs: ~4.3 groups, ~1.3 of them solos (13 % of the PMCs alone).
        /// This mix, 15 PMCs: ~7.6 groups - ~3.4 solos and ~4.2 squads holding ~11.6 PMCs, so both solos and squads grow.
        /// </summary>
        private static readonly double[][] BiggerLobbyGroupSizes = { new[] { 1d, 45 }, new[] { 2d, 26 }, new[] { 3d, 17 }, new[] { 4d, 8 }, new[] { 5d, 4 } };

        /// <summary>QuestingBots' client config (its ConfigUtil is internal, the config classes are public).</summary>
        public static ModConfig QuestingBots()
        {
            Type configUtil = AccessTools.TypeByName("QuestingBots.Utils.ConfigUtil");
            object util = configUtil == null ? null : typeof(Singleton<>).MakeGenericType(configUtil).GetProperty("Instance")?.GetValue(null);
            return util == null ? null : Traverse.Create(util).Property("CurrentConfig").GetValue<ModConfig>();
        }

        public static void Hook()
        {
            foreach (BepInEx.Configuration.ConfigEntryBase entry in new BepInEx.Configuration.ConfigEntryBase[]
                     {
                         Settings.LobbyLikeOnline, Settings.ScavsQuest, Settings.ScavPatrol, Settings.RealKeys, Settings.ThoroughLooting,
                         Settings.PersonalExtract, Settings.FarBotsFullSenses, Settings.Enabled, Settings.SquadTactics, Settings.TightSquads, Settings.SquadLootWait, Settings.SquadLootSpread,
                         Settings.BiggerLobby,
                     })
            {
                entry.ConfigFile.SettingChanged += (_, e) =>
                {
                    if (e.ChangedSetting == entry)
                    {
                        Apply();
                    }
                };
            }
        }

        public static void Apply()
        {
            List<string> changed = new List<string>();
            try
            {
                ApplyQuestingBots(changed);
            }
            catch (Exception e)
            {
                Diag.Report("ModControl.QuestingBots", e);
            }

            try
            {
                LodSettings.ApplyToSain();
            }
            catch (Exception e)
            {
                Diag.Report("ModControl.SAIN", e);
            }

            if (changed.Count > 0)
            {
                Log.Info("LR Control: " + string.Join("; ", changed));
            }
        }

        private static void ApplyQuestingBots(List<string> changed)
        {
            ModConfig qb = QuestingBots();
            if (qb == null)
            {
                return;
            }

            bool on = Settings.Enabled.Value;
            QuestingConfig q = qb.Questing;
            BotSpawnTypeConfig pmcs = qb.BotSpawns.PMCs;

            // Online-sized lobby: always the full lobby of the map, online squad sizes (the reserve and the cap are LiveRaid patches)
            bool lobby = on && Settings.LobbyLikeOnline.Value;
            Own("QB lobby: fraction of max players", lobby, () => pmcs.FractionOfMaxPlayers, v => pmcs.FractionOfMaxPlayers = v, 1f, changed);
            Own("QB lobby: always full", lobby, () => pmcs.LobbySizeReduction.AlwaysUseFullLobbies, v => pmcs.LobbySizeReduction.AlwaysUseFullLobbies = v, true, changed);
            Own("QB lobby: squad sizes", lobby, () => pmcs.BotsPerGroupDistribution, v => pmcs.BotsPerGroupDistribution = v,
                Settings.BiggerLobby.Value ? BiggerLobbyGroupSizes : OnlineGroupSizes, changed);

            // Scavs roam the map: QuestingBots must let scavs quest at all (which quests they get is LiveRaid's choice)
            bool roam = on && (Settings.ScavsQuest.Value || Settings.ScavPatrol.Value);
            Own("QB questing: scavs", roam, () => q.AllowedBotTypesForQuesting.Scav, v => q.AllowedBotTypesForQuesting.Scav = v, true, changed);

            // Real keys: any bot that carries the key opens the door, scavs included, and looks for doors a bit further away
            bool keys = on && Settings.RealKeys.Value;
            Own("QB doors: scavs unlock", keys, () => q.UnlockingDoors.Enabled.Scav, v => q.UnlockingDoors.Enabled.Scav = v, true, changed);
            Own("QB doors: player scavs unlock", keys, () => q.UnlockingDoors.Enabled.PScav, v => q.UnlockingDoors.Enabled.PScav = v, true, changed);
            Own("QB doors: search radius", keys, () => q.UnlockingDoors.SearchRadius, v => q.UnlockingDoors.SearchRadius = v, 40f, changed);

            // Thorough looting: bots stop for loot on the way much more often, like players do
            bool loot = on && Settings.ThoroughLooting.Value;
            BreakForLootingConfig b = q.BotQuestingRequirements.BreakForLooting;
            Own("QB looting: checks every (s)", loot, () => b.MinTimeBetweenLootingChecks, v => b.MinTimeBetweenLootingChecks = v, 22f, changed);
            Own("QB looting: follower checks every (s)", loot, () => b.MinTimeBetweenFollowerLootingChecks, v => b.MinTimeBetweenFollowerLootingChecks = v, 18f, changed);
            Own("QB looting: events every (s)", loot, () => b.MinTimeBetweenLootingEvents, v => b.MinTimeBetweenLootingEvents = v, 28f, changed);
            Own("QB looting: scan time (s)", loot, () => b.MaxLootScanTime, v => b.MaxLootScanTime = v, 7f, changed);
            // How far a follower may loot from its leader: the squad loot spread (F12 -> 8), or 30 m for a tight squad without it
            bool tight = on && Settings.SquadTactics.Value && Settings.TightSquads.Value;
            Own("QB looting: max distance from leader (m)", loot || tight, () => b.MaxDistanceFromBoss, v => b.MaxDistanceFromBoss = v,
                Settings.SquadLootWait.Value ? UnityEngine.Mathf.Clamp(Settings.SquadLootSpread.Value, 20f, 150f) : tight ? 30f : 60f, changed);

            // Unsticking has one owner: LiveRaid's stuck watch (doors, side steps, other way, back off) acts within 2-8 s in any layer; QuestingBots' own
            // jump (6 s) and vault (8 s) on a questing bot came in the middle of it - a bot jumping at a door LiveRaid was opening. QuestingBots' remedies
            // stay as the last resort, later
            bool unstick = on && Settings.DoorAssist.Value;
            StuckBotRemediesConfig rem = q.StuckBotDetection.StuckBotRemedies;
            Own("QB unstick: jump after (s)", unstick, () => rem.MinTimeBeforeJumping, v => rem.MinTimeBeforeJumping = v, 14f, changed);
            Own("QB unstick: vault after (s)", unstick, () => rem.MinTimeBeforeVaulting, v => rem.MinTimeBeforeVaulting = v, 16f, changed);

            // Stay together: followers keep close to the leader (QuestingBots: 7-20 m while questing, 15-35 m in a fight, the leader waits only past 15 / 25 m)
            MaxFollowerDistanceConfig f = q.BotQuestingRequirements.MaxFollowerDistance;
            Own("QB squad: follow range questing (m)", tight, () => f.TargetRangeQuesting, v => f.TargetRangeQuesting = v, new MinMaxConfig(4, 12), changed);
            Own("QB squad: follow range in a fight (m)", tight, () => f.TargetRangeCombat, v => f.TargetRangeCombat = v, new MinMaxConfig(8, 22), changed);
            Own("QB squad: leader waits past (m)", tight, () => f.Nearest, v => f.Nearest = v, 10f, changed);
            Own("QB squad: leader regroups past (m)", tight, () => f.Furthest, v => f.Furthest = v, 18f, changed);

            // LootingBots' own limits (its F12/config values are only read, never edited on disk). "Maximum looting bots" 20: a forced scan (LiveRaid, QuestingBots) adds a bot to
            // the active cache past the limit, and LootingBots then switches the others off one by one - in the middle of looting (StopLooting) - so with 40+ bots on the map
            // half of them looted a body for half a second, went back to their quest and tried again (raid 20:11: "LootNow on a body" ten times in a row, nothing taken).
            // 0 = no limit. After two empty scans LootingBots shuts a bot's scanning for 3 minutes; here 30 s.
            Own("LootingBots: maximum looting bots", loot, () => global::LootingBots.LootingBots.MaxActiveLootingBots.Value, v => global::LootingBots.LootingBots.MaxActiveLootingBots.Value = v, 0, changed);
            Own("LootingBots: empty scans before a pause", loot, () => global::LootingBots.LootingBots.MaxEmptyAttempts.Value, v => global::LootingBots.LootingBots.MaxEmptyAttempts.Value = v, 3, changed);
            Own("LootingBots: pause after empty scans (s)", loot, () => global::LootingBots.LootingBots.EmptyAttemptsCooldown.Value, v => global::LootingBots.LootingBots.EmptyAttemptsCooldown.Value = v, 30, changed);

            // Every bot that is not a boss, a follower or a cultist may loot bodies, containers and loose things (whatever LootingBots' own F12 says)
            const global::LootingBots.Utilities.BotType need = global::LootingBots.Utilities.BotType.Scav | global::LootingBots.Utilities.BotType.Pmc
                | global::LootingBots.Utilities.BotType.PlayerScav | global::LootingBots.Utilities.BotType.Raider;
            foreach ((string name, BepInEx.Configuration.ConfigEntry<global::LootingBots.Utilities.BotType> entry) in new[]
                     {
                         ("LootingBots: who loots bodies", global::LootingBots.LootingBots.CorpseLootingEnabled),
                         ("LootingBots: who loots containers", global::LootingBots.LootingBots.ContainerLootingEnabled),
                         ("LootingBots: who loots loose things", global::LootingBots.LootingBots.LooseItemLootingEnabled),
                     })
            {
                var own = Original.TryGetValue(name, out object o) ? (global::LootingBots.Utilities.BotType)o : entry.Value;
                Own(name, loot, () => entry.Value, v => entry.Value = v, own | need, changed);
            }

            // What is worth a slot in the bag: LootingBots' 12 000 roubles per slot for a PMC (5 000 for others) left nearly everything lying on the floors -
            // a player takes a 7 000 rouble bolt box. Lower values of the user's own LootingBots config stay
            float pmcMin = Original.TryGetValue("LootingBots: PMC min value per slot", out object pm) ? (float)pm : global::LootingBots.LootingBots.PMCMinLootThreshold.Value;
            float scavMin = Original.TryGetValue("LootingBots: Scav min value per slot", out object sm) ? (float)sm : global::LootingBots.LootingBots.ScavMinLootThreshold.Value;
            Own("LootingBots: PMC min value per slot", loot, () => global::LootingBots.LootingBots.PMCMinLootThreshold.Value, v => global::LootingBots.LootingBots.PMCMinLootThreshold.Value = v,
                Math.Min(pmcMin, 7000f), changed);
            Own("LootingBots: Scav min value per slot", loot, () => global::LootingBots.LootingBots.ScavMinLootThreshold.Value, v => global::LootingBots.LootingBots.ScavMinLootThreshold.Value = v,
                Math.Min(scavMin, 3000f), changed);

            // Personal extract time: the moment comes from the character; the quest count only stops bots that would leave far too early
            bool extract = on && Settings.PersonalExtract.Value;
            ExtractionRequirementsConfig x = q.ExtractionRequirements;
            Own("QB extract: min quests", extract, () => x.TotalQuests.Min, v => x.TotalQuests.Min = v, 4d, changed);
            Own("QB extract: max quests", extract, () => x.TotalQuests.Max, v => x.TotalQuests.Max = v, 12d, changed);

            // QuestingBots hands extract orders to SAIN, whose extract layer is off unless "SAIN Extract Behavior" is on:
            // the order went nowhere and no PMC ever left. Then the game's own exfiltration (kept by SAIN in that case) is used
            bool sainExtracts = SAIN.Preset.Shared.GlobalSettings.GlobalSettingsClass.Instance?.General?.Extract?.SAIN_EXTRACT_TOGGLE == true;
            Own("QB extract: through SAIN", on && !sainExtracts, () => x.UseSAINForExtracting, v => x.UseSAINForExtracting = v, false, changed);
        }

        /// <summary>LiveRaid's value while <paramref name="active"/>, the mod's own value otherwise.</summary>
        private static void Own<T>(string key, bool active, Func<T> get, Action<T> set, T ours, List<string> changed)
        {
            if (!Original.ContainsKey(key))
            {
                Original[key] = get();
            }

            T want = active ? ours : (T)Original[key];
            if (Show(get()) == Show(want))
            {
                return;
            }

            set(want);
            changed.Add($"{key} = {Show(want)}{(active ? "" : " (the mod's own value)")}");
        }

        private static string Show(object value)
        {
            switch (value)
            {
                case null: return "null";
                case double[][] rows: return "[" + string.Join(",", rows.Select(r => "[" + string.Join(",", r) + "]")) + "]";
                case MinMaxConfig mm: return $"{Show(mm.Min)}-{Show(mm.Max)}";
                case float f: return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                case double d: return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }
    }

    /// <summary>
    /// QuestingBots' AI limiter puts far bots to sleep (they stop moving and thinking). LiveRaid's rule is that nobody sleeps -
    /// far bots only think less often ("Simplify distant bots"). The limiter is kept off here without touching QuestingBots' own setting.
    /// </summary>
    public class QbSleepingPatch : ModulePatch
    {
        private static readonly Type LayerType = AccessTools.TypeByName("QuestingBots.BotLogic.Sleep.SleepingLayer");
        private static readonly MethodInfo UpdatePreviousState = LayerType == null ? null : AccessTools.Method(LayerType, "updatePreviousState");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(LayerType, "IsActive");

        [PatchPrefix]
        protected static bool PatchPrefix(object __instance, ref bool __result)
        {
            if (Settings.QbSleeping.Value || !Settings.Enabled.Value)
            {
                return true;
            }

            try
            {
                // Through QuestingBots' own bookkeeping, so a bot that was asleep is woken up and unregistered properly
                __result = UpdatePreviousState != null ? (bool)UpdatePreviousState.Invoke(__instance, new object[] { false }) : false;
                return false;
            }
            catch (Exception e)
            {
                Diag.Report("QbSleepingPatch", e);
                return true;
            }
        }
    }
}
