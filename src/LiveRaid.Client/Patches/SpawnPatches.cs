using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using JsonType;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots always puts a new PMC / player-scav group on the spawn point furthest from everyone else.
    /// Online spawns are random: any free spawn point can be used, so a squad can appear right next to you.
    /// With "Online-like spawns" on, LiveRaid picks a random eligible point (the minimum distance still applies).
    /// </summary>
    public class QbRandomSpawnPointPatch : ModulePatch
    {
        private const float StartGuardMeters = 25f;
        // Where the human players started (yours; in LiveRaid Online every player of the group)
        internal static readonly System.Collections.Generic.List<Vector3> HumanStarts = new System.Collections.Generic.List<Vector3>();
        // Where the squads of this raid's start were put (one point per squad): the next squad keeps its distance from them (Settings.StartApartMeters)
        internal static readonly System.Collections.Generic.List<Vector3> PlacedStarts = new System.Collections.Generic.List<Vector3>();
        internal static int Picked;
        internal static int ExtraPicked;
        internal static int ApartKept;
        internal static int ApartRelaxed;

        internal static void Clear()
        {
            HumanStarts.Clear();
            PlacedStarts.Clear();
            ApartKept = ApartRelaxed = 0;
            QbReserveDelayPatch.Held = 0;
            QbLateWavesPatch.Clear();
            QbPmcReservePatch.StartedPmcs = 0;
            Picked = ExtraPicked = 0;
            QbStartLobbyGroupPatch.Cut = 0;
            SpawnSides.Clear();
            SpawnZones.Clear();
        }

        internal static string Summary => $"{Picked} squads placed on random points, {ExtraPicked} of them on group / opposite / PMC-bot points; {SpawnSides.Summary}; starting groups cut to fit the starting lobby {QbStartLobbyGroupPatch.Cut}; " +
                                          $"{StartSpreadSummary}; reserve held back until minute {Settings.ReserveDelayMin.Value} {QbReserveDelayPatch.Held} times";

        private static string StartSpreadSummary
        {
            get
            {
                if (PlacedStarts.Count < 2)
                {
                    return "start spread: too few squads placed";
                }

                var nn = PlacedStarts.Select((a, i) => PlacedStarts.Where((b, j) => j != i).Min(b => Vector3.Distance(a, b))).OrderBy(d => d).ToList();
                // 1.2.8: how close the squads started to you - the answer to "did anyone spawn on my side, or does my spawn forbid it" (only the 25 m round your point is kept free)
                string near = "";
                if (HumanStarts.Count > 0)
                {
                    var toYou = PlacedStarts.Select(st => HumanStarts.Min(h => Vector3.Distance(st, h))).OrderBy(d => d).ToList();
                    near = $"; nearest squad start to you {toYou[0]:0} m, squads within 150 m / 300 m of you {toYou.Count(d => d <= 150f)} / {toYou.Count(d => d <= 300f)} (only {StartGuardMeters:0} m round your point is kept free)";
                }

                return $"start spread: {PlacedStarts.Count} squads, nearest neighbour min {nn[0]:0} m / median {nn[nn.Count / 2]:0} m (kept {Settings.StartApartMeters.Value} m apart {ApartKept} times, distance relaxed {ApartRelaxed}){near}";
            }
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationData), nameof(LocationData.TryGetFurthestSpawnPointFromPositions),
                new[] { typeof(Vector3[]), typeof(ESpawnCategoryMask), typeof(EPlayerSideMask), typeof(SpawnPointParams[]), typeof(float) });

        [PatchPrefix]
        protected static bool PatchPrefix(
            LocationData __instance,
            ESpawnCategoryMask allowedCategories,
            EPlayerSideMask allowedSides,
            SpawnPointParams[] excludedSpawnPoints,
            float distanceFromAllPlayers,
            ref SpawnPointParams? __result)
        {
            try
            {
                return RunPrefix(__instance, allowedCategories, allowedSides, excludedSpawnPoints, distanceFromAllPlayers, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbRandomSpawnPointPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(
            LocationData __instance,
            ESpawnCategoryMask allowedCategories,
            EPlayerSideMask allowedSides,
            SpawnPointParams[] excludedSpawnPoints,
            float distanceFromAllPlayers,
            ref SpawnPointParams? __result)
        {
            if (!Settings.OnlineSpawns.Value)
            {
                return true;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            Vector3[] players = world.AllAlivePlayersList.Select(p => p.Position).ToArray();

            // Where you started: remembered when the first squad is placed (you are already in the raid, standing on your spawn point)
            if (HumanStarts.Count == 0)
            {
                HumanStarts.AddRange(Net.Humans().Select(h => h.Position));
            }

            // The points of the map that are neither "Player" nor of the squad's side but still a starting place of a PMC squad: the group / opposite
            // team points of a squad of players and the points the game marks for PMC bots (the latter belong to the scav side in the data)
            bool all = Settings.SpawnAllPoints.Value && (allowedCategories & ESpawnCategoryMask.Player) != 0;
            const ESpawnCategoryMask extraMask = ESpawnCategoryMask.Coop | ESpawnCategoryMask.Group | ESpawnCategoryMask.Opposite | ESpawnCategoryMask.BotPmc;
            bool pmcSide = (allowedSides & EPlayerSideMask.Pmc) != 0;
            bool guard = HumanStarts.Count > 0 && (RaidHelpers.IsBeginningOfRaid() || RaidHelpers.HumanPlayersRecentlySpawned());

            SpawnPointParams[] valid = __instance.GetAllValidSpawnPointParams();
            SpawnPointParams[] eligible = valid
                .Where(sp => !excludedSpawnPoints.Contains(sp))
                .Where(sp => ((sp.Categories & allowedCategories) != 0 && (sp.Sides & allowedSides) != 0)
                             || (all && (sp.Categories & extraMask) != 0
                                 && ((sp.Sides & allowedSides) != 0 || (pmcSide && (sp.Categories & ESpawnCategoryMask.BotPmc) != 0))))
                .Where(sp => players.All(p => Vector3.Distance(sp.Position, p) > distanceFromAllPlayers))
                // A squad never starts on your spawn point nor next to it
                .Where(sp => !guard || HumanStarts.All(h => Vector3.Distance(sp.Position, h) > StartGuardMeters))
                .ToArray();

            if (eligible.Length == 0)
            {
                // Nothing free under the online rule: fall back to QuestingBots' own choice
                return true;
            }

            // The start of the raid: the squads keep apart from each other (a gap that shrinks in steps when there are not enough free points)
            bool beginning = RaidHelpers.IsBeginningOfRaid();
            if (beginning && Settings.StartApartMeters.Value > 0 && PlacedStarts.Count > 0)
            {
                float gap = Settings.StartApartMeters.Value;
                SpawnPointParams[] apart = null;
                for (int step = 0; step < 5 && gap >= 20f; step++, gap *= 0.7f)
                {
                    float g = gap;
                    apart = eligible.Where(sp => PlacedStarts.All(s => Vector3.Distance(sp.Position, s) > g)).ToArray();
                    if (apart.Length > 0)
                    {
                        if (step > 0)
                        {
                            ApartRelaxed++;
                        }

                        break;
                    }
                }

                if (apart != null && apart.Length > 0)
                {
                    eligible = apart;
                    ApartKept++;
                }
            }

            // Player scavs brought in after a loud fight (PScavReplacement) come in on its side: 90-350 m from it, never within 150 m of a human player
            if (Bots.PScavReplacement.FightPoint(out Vector3 fight))
            {
                List<Vector3> humans = Net.Humans().Select(h => h.Position).ToList();
                SpawnPointParams[] byFight = eligible
                    .Where(sp => Vector3.Distance(sp.Position, fight) is float d && d > 90f && d < 350f)
                    .Where(sp => humans.All(h => Vector3.Distance(sp.Position, h) > 150f))
                    .ToArray();
                if (byFight.Length > 0)
                {
                    __result = byFight[Random.Range(0, byFight.Length)];
                    Bots.PScavReplacement.NearFightUsed();
                    Picked++;
                    Log.Debug($"online spawn: player scavs on the side of the fight at ({fight.x:0}, {fight.z:0}) - {__result.Value.Id}, {byFight.Length} points to choose from");
                    return false;
                }
            }

            // 1.4.0: ... or at the part of the map nobody has been to (the nearest free points to it, never within 150 m of a human or 60 m of a living bot)
            if (Bots.PScavReplacement.ColdPoint(out Vector3 cold))
            {
                List<Vector3> humans = Net.Humans().Select(h => h.Position).ToList();
                List<Vector3> living = Comfort.Common.Singleton<GameWorld>.Instance?.AllAlivePlayersList.Where(pl => pl != null).Select(pl => pl.Position).ToList() ?? new List<Vector3>();
                SpawnPointParams[] byCold = eligible
                    .Where(sp => humans.All(h => Vector3.Distance(sp.Position, h) > 150f) && living.All(b => Vector3.Distance(sp.Position, b) > 60f))
                    .OrderBy(sp => Vector3.Distance(sp.Position, cold))
                    .Take(4)
                    .Where(sp => Vector3.Distance(sp.Position, cold) < 260f)
                    .ToArray();
                if (byCold.Length > 0)
                {
                    __result = byCold[Random.Range(0, byCold.Length)];
                    Bots.PScavReplacement.ColdUsed();
                    Picked++;
                    Log.Debug($"online spawn: player scavs at ground nobody has been to round ({cold.x:0}, {cold.z:0}) - {__result.Value.Id}, {Vector3.Distance(__result.Value.Position, cold):0} m from it");
                    return false;
                }
            }

            // Like online, squads start spread over the map's sides (Customs: half at one end, half at the other), not all on one
            __result = Settings.BalancedSpawnSides.Value && SpawnSides.PlacingPmcs
                ? SpawnSides.Pick(valid, eligible, all, extraMask)
                : (Settings.SpawnRotation.Value && SpawnSides.PlacingPmcs ? SpawnZones.Pick(eligible) : eligible[Random.Range(0, eligible.Length)]);
            bool extra = (__result.Value.Categories & allowedCategories) == 0 || (__result.Value.Sides & allowedSides) == 0;
            if (extra)
            {
                ExtraPicked++;
            }

            if (beginning)
            {
                PlacedStarts.Add(__result.Value.Position);
            }

            Picked++;
            Log.Debug($"online spawn: picked {__result.Value.Id} out of {eligible.Length} free points{(extra ? " (a group / opposite / PMC-bot point)" : "")}");
            return false;
        }
    }

    /// <summary>At raid start QuestingBots keeps new groups 30 m away from everyone; online spawns can be much closer.</summary>
    public class QbInitialSpawnDistancePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.BotGenerator"), "GetMinSpawnDistanceFromOtherPlayers");

        [PatchPostfix]
        protected static void PatchPostfix(ref float __result)
        {
            try
            {
                RunPostfix(ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbInitialSpawnDistancePatch", e);
            }
        }

        private static void RunPostfix(ref float __result)
        {
            if (Settings.OnlineSpawns.Value && (RaidHelpers.IsBeginningOfRaid() || RaidHelpers.HumanPlayersRecentlySpawned()))
            {
                __result = Mathf.Min(__result, Settings.OnlineSpawnMinDistance.Value);
            }
        }
    }

    /// <summary>AI scavs live on the map; they never extract (player scavs and PMCs still do).</summary>
    public class QbScavNoExtractPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor"), "isBotReadyToExtract");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref bool __result)
        {
            try
            {
                RunPostfix(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbScavNoExtractPatch", e);
            }
        }

        private static void RunPostfix(object __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
            if (bot != null && BotRegistrationManager.GetBotType(bot) == EBotType.Scav)
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Online the raid starts with the whole lobby and nobody joins when somebody dies. PMC count = lobby (QuestingBots picks it between the map's
    /// min and max players), alive at once = exactly that count, plus a few spare PMCs that come in only after the starting squads lose people.
    /// </summary>
    public class QbPmcReservePatch : ModulePatch
    {
        internal static int InitialCount;
        // PMC bots activated in this raid (the starting lobby first, then the reserve): the reserve phase begins when the lobby is all in
        internal static int StartedPmcs;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PMCGenerator"), "GetMaxGeneratedBots");

        [PatchPostfix]
        protected static void PatchPostfix(ref int __result)
        {
            try
            {
                RunPostfix(ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbPmcReservePatch", e);
            }
        }

        private static void RunPostfix(ref int __result)
        {
            if (__result <= 0)
            {
                return;
            }

            // Co-op: the players of the group are PMCs of the lobby too (QuestingBots already counts one human)
            int players = Net.ScavRaid ? 0 : Net.GroupSize();
            if (Settings.GroupFillsLobby.Value && players > 1)
            {
                int before = __result;
                __result = System.Math.Max(1, __result - (players - 1));
                Log.Info($"LR Population: {players} PMC players in the group - {before - __result} PMC bots fewer");
            }

            // Bigger lobby (F12 -> 6): a few more PMCs from the start, with more solos in the mix (ModControl: squad sizes)
            int extra = Settings.BiggerLobby.Value ? Settings.BiggerLobbyCount.Value : 0;
            if (!Settings.LobbyLikeOnline.Value)
            {
                if (extra > 0)
                {
                    __result += extra;
                    Log.Info($"LR Population: {__result} PMCs this raid ({extra} more than QuestingBots' {__result - extra})");
                }

                return;
            }

            int qb = __result;
            int vanilla = __result + extra;
            // Smart population balance: the start number is pulled toward the online lobby of this very map
            InitialCount = Raid.PopulationBalance.StartCount(vanilla);
            // Lively map: in co-op every player beyond the first brings two more PMCs to come in later
            int reserve = Settings.PmcReserve.Value + (Settings.LivelyMap.Value && players > 1 ? 2 * (players - 1) : 0);
            // Late waves (F12): more PMCs generated, to come in on a schedule (QbLateWavesPatch gives each reserve squad its minute)
            if (Settings.LateWaves.Value)
            {
                reserve += Settings.LateWaveBots.Value;
            }

            __result = InitialCount + reserve;
            Log.Info($"LR Population: {InitialCount} PMCs at raid start{(InitialCount != vanilla ? $" (QuestingBots' {qb} + {extra} bigger lobby = {vanilla}, balanced to the map)" : extra > 0 ? $" (lobby {InitialCount - extra} + {extra})" : "")} + {reserve} in reserve");
        }
    }

    /// <summary>
    /// The reserve PMCs come in "when the starting lobby loses people" - but on a small map the lobby loses them in the first minute (Ground Zero, raid
    /// 2026-11-07 16:56: three dead by 1:21, a reserve squad of three at once, 13 PMCs against a lobby of 10). Online nobody joins; here nobody joins
    /// before the set minute (F12 -> 6 "Reserve PMCs not before").
    /// </summary>
    public class QbReserveDelayPatch : ModulePatch
    {
        internal static int Held;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PMCGenerator"), "GetNumberOfBotsAllowedToSpawn");

        [PatchPostfix]
        protected static void PatchPostfix(ref int __result)
        {
            try
            {
                if (__result <= 0 || Settings.ReserveDelayMin.Value <= 0 || !Settings.LobbyLikeOnline.Value)
                {
                    return;
                }

                int start = QbPmcReservePatch.InitialCount;
                if (start > 0 && QbPmcReservePatch.StartedPmcs >= start && RaidHelpers.GetRaidElapsedSeconds() < Settings.ReserveDelayMin.Value * 60f)
                {
                    __result = 0;
                    Held++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbReserveDelayPatch", e);
            }
        }
    }

    /// <summary>
    /// Late waves (1.4.9, F12 -> 6 "Late PMC waves"): the PMC squads QuestingBots generated beyond the starting lobby are given a minute each - the first at the set minute,
    /// the rest at the set interval - instead of "whenever a place is free after minute 5" (the map was empty: the starting PMCs die in 6-9 minutes, half the lobby by minute 9).
    /// A squad whose minute has come still waits while the map holds the full lobby (QuestingBots' alive limit). The reserve of F12 stays as it was, the late squads are added to it.
    /// </summary>
    public class QbLateWavesPatch : ModulePatch
    {
        private static bool _scheduled;
        public static int Waves { get; private set; }

        public static void Clear()
        {
            _scheduled = false;
            Waves = 0;
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PMCGenerator"), "GetNumberOfBotsAllowedToSpawn");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance)
        {
            try
            {
                if (_scheduled || !Settings.LateWaves.Value || !Settings.LobbyLikeOnline.Value)
                {
                    return;
                }

                int start = QbPmcReservePatch.InitialCount;
                var generator = __instance as QuestingBots.Components.Spawning.BotGenerator;
                if (start <= 0 || generator == null || !generator.HasGeneratedBots)
                {
                    return;
                }

                int total = 0;
                int index = 0;
                foreach (QuestingBots.Models.BotSpawnInfo group in generator.GetBotGroups())
                {
                    int before = total;
                    total += group.GeneratedBotCount;
                    if (before < start || group.HasSpawnStarted)
                    {
                        continue;
                    }

                    // The reserve squads in their generated order: the first at the first minute, the rest each an interval later
                    double at = (Settings.LateWaveFirstMin.Value + index * Settings.LateWaveEveryMin.Value) * 60.0;
                    // A wave later than 4 minutes before the end of the raid would only be fodder: it does not come (raid 2026-11-11 17:27: waves at 41 and 49 minutes of a 40-minute raid)
                    bool tooLate = at > RaidHelpers.OriginalEscapeTimeSeconds - 240.0;
                    group.RaidETRangeToSpawn.Min = tooLate ? RaidHelpers.OriginalEscapeTimeSeconds + 3600.0 : System.Math.Max(group.RaidETRangeToSpawn.Min, at);
                    index++;
                    Log.Info(tooLate ? $"LR Population: late wave {index}: a squad of {group.GeneratedBotCount} PMC(s) at minute {at / 60.0:0.#} is too late for this raid - it does not come"
                        : $"LR Population: late wave {index}: a squad of {group.GeneratedBotCount} PMC(s) comes in at minute {at / 60.0:0.#}");
                }

                Waves = index;
                _scheduled = true;
            }
            catch (System.Exception e)
            {
                _scheduled = true;
                Diag.Report("QbLateWavesPatch", e);
            }
        }
    }

    public class QbPmcAliveCapPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.BotGenerator"), "SetMaxAliveBots");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance)
        {
            try
            {
                RunPostfix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbPmcAliveCapPatch", e);
            }
        }

        private static void RunPostfix(object __instance)
        {
            string name = __instance.GetType().Name;
            // Lively map: player scavs at once - a part of the lobby, not QuestingBots' whole PMC number (7 on Customs on top of the PMCs)
            if (name == "PScavGenerator" && Settings.LivelyMap.Value)
            {
                int lobby = QbPmcReservePatch.InitialCount > 0 ? QbPmcReservePatch.InitialCount : 10;
                int max = Raid.PopulationBalance.On ? Raid.PopulationBalance.PScavAtOnce(lobby) : Mathf.Clamp(Mathf.RoundToInt(lobby * 0.4f), 2, 5);
                Traverse.Create(__instance).Property("MaxAliveBots").SetValue(max);
                Log.Info($"LR Population: player scavs come on their own schedule, up to {max} at a time");
                return;
            }

            if (!Settings.LobbyLikeOnline.Value || QbPmcReservePatch.InitialCount <= 0 || name != "PMCGenerator")
            {
                return;
            }

            Traverse.Create(__instance).Property("MaxAliveBots").SetValue(QbPmcReservePatch.InitialCount);
        }
    }

    /// <summary>
    /// The whole starting lobby comes in at the start (0.11.1). QuestingBots draws the group sizes over the starting PMCs and the reserve as one number,
    /// so a group could straddle the line: raid 2026-10-31 18:29 (Ground Zero, 7 + 2): groups of 2 + 2, then a group of 5 that did not fit the 3 places
    /// left - it waited until 11:56, and the raid ran with 4 PMCs instead of 7. While the starting lobby is being made, a group is cut to the places
    /// left in it; the reserve gets groups of its own.
    /// </summary>
    public class QbStartLobbyGroupPatch : ModulePatch
    {
        internal static int Cut;

        // The group's size is handed to GenerateBotGroup (a real method; MaxBotsToGenerate is a one-line getter the JIT may inline)
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.BotGenerator"), "GenerateBotGroup");

        [PatchPrefix]
        protected static void PatchPrefix(object __instance, ref int bots)
        {
            try
            {
                int start = QbPmcReservePatch.InitialCount;
                if (!Settings.LobbyLikeOnline.Value || start <= 0 || __instance.GetType().Name != "PMCGenerator")
                {
                    return;
                }

                int generated = Traverse.Create(__instance).Property("GeneratedBotCount").GetValue<int>();
                if (generated < start && bots > start - generated)
                {
                    Log.Debug($"a PMC group of {bots} cut to {start - generated} to fit the starting lobby ({generated} of {start} made)");
                    bots = start - generated;
                    Cut++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbStartLobbyGroupPatch", e);
            }
        }
    }

    /// <summary>
    /// Lively map (F12 -> 6): QuestingBots lets no player scav in while any PMC group is still to come and counts every living PMC against the player
    /// scavs' places (PScavGenerator.CanSpawnBots / GetNumberOfBotsAllowedToSpawn). With an online lobby the reserve waits for deaths, so the player
    /// scavs came only when half the PMCs were dead: raid 2026-10-31 17:08 at minutes 20-23 of 40, and none at all in the 18-20 minute co-op raids.
    /// Here they come by their own schedule from 90 s into the raid, with places of their own.
    /// </summary>
    public class QbPScavCanSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PScavGenerator"), "CanSpawnBots");

        [PatchPostfix]
        protected static void PatchPostfix(ref bool __result)
        {
            if (!__result && Settings.LivelyMap.Value && RaidHelpers.GetRaidElapsedSeconds() > 90f)
            {
                __result = true;
            }
        }
    }

    public class QbPScavPlacesPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PScavGenerator"), "GetNumberOfBotsAllowedToSpawn");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref int __result)
        {
            try
            {
                if (Settings.LivelyMap.Value)
                {
                    __result = Traverse.Create(__instance).Method("BotsAllowedToSpawnForGeneratorType").GetValue<int>();
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbPScavPlacesPatch", e);
            }
        }
    }

    /// <summary>
    /// QuestingBots sends a bot home as soon as it has done 4-12 quests. With personal exit times the character decides:
    /// the bot stays until its own moment (or until the raid is about to end, which QuestingBots handles itself).
    /// </summary>
    public class QbPersonalExtractPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor"), "isBotReadyToExtract");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref bool __result)
        {
            try
            {
                RunPostfix(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbPersonalExtractPatch", e);
            }
        }

        private static void RunPostfix(object __instance, ref bool __result)
        {
            if (!__result || !Settings.PersonalExtract.Value)
            {
                return;
            }

            BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
            Bots.BotProfile profile = bot != null ? Bots.BotProfiles.Get(bot) : null;
            if (profile == null || profile.Kind == Bots.BotKind.Other || profile.Kind == Bots.BotKind.Scav)
            {
                return;
            }

            // Raid almost over: let QuestingBots do its thing
            if (RaidHelpers.GetRemainingRaidTimeSeconds() < 300f)
            {
                return;
            }

            if (RaidHelpers.GetRaidTimeRemainingFraction() > profile.ExtractAtRemaining)
            {
                __result = false;
                return;
            }

            // Its hour has come - in the first 20 minutes only by a roll (ExtractClock; one roll per bot, shared with ExtractPlanner)
            if (!Bots.ExtractClock.MayLeave(profile, false, true, out _))
            {
                __result = false;
            }
        }
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// "Spread squads over the map sides" (F12 -> 6). Random points alone left it to luck: on Customs a three and later a five started on the same point
    /// at one end while the other end had a solo (raid 2026-10-21). Online the lobby is split over the map's spawn sides.
    ///
    /// The map's PMC spawn points are split once per raid along their longest spread (principal axis of the points): long maps (Customs, Shoreline,
    /// Lighthouse) into two halves, rounder ones (Woods, Interchange, Reserve, Streets) into four quarters. Each new squad goes to the side with the fewest
    /// PMCs - at the start the PMCs already placed there, later the living ones - on a random free point of that side.
    /// </summary>
    public static class SpawnSides
    {
        private class Side
        {
            public int Placed;
            public int Squads;
        }

        private static bool _built;
        private static Vector2 _center;
        private static Vector2 _axis;
        private static float _split;
        private static int _count;
        private static Side[] _sides = new Side[0];

        // Size of the PMC group QuestingBots is placing right now (PMCGenerator.GetSpawnPositionsForBotGroup), and whether it is placing one
        internal static int PendingGroupSize = 1;
        internal static bool PlacingPmcs;

        public static string Summary =>
            _count == 0 ? "sides not used" : $"{_count} sides: " + string.Join(" / ", _sides.Select((sd, i) => $"#{i + 1} {sd.Placed} PMCs in {sd.Squads} squads"));

        public static void Clear()
        {
            _built = false;
            _count = 0;
            _sides = new Side[0];
            PendingGroupSize = 1;
            PlacingPmcs = false;
        }

        private static void Build(SpawnPointParams[] valid, bool all, ESpawnCategoryMask extraMask)
        {
            _built = true;
            List<Vector2> pts = valid
                .Where(sp => ((sp.Categories & ESpawnCategoryMask.Player) != 0 && (sp.Sides & EPlayerSideMask.Pmc) != 0)
                             || (all && (sp.Categories & extraMask) != 0))
                .Select(sp => new Vector2(sp.Position.x, sp.Position.z))
                .ToList();
            if (pts.Count < 4)
            {
                return;
            }

            Vector2 c = Vector2.zero;
            foreach (Vector2 p in pts)
            {
                c += p;
            }

            c /= pts.Count;
            float sxx = 0f, szz = 0f, sxz = 0f;
            foreach (Vector2 p in pts)
            {
                Vector2 d = p - c;
                sxx += d.x * d.x;
                szz += d.y * d.y;
                sxz += d.x * d.y;
            }

            // Principal axis of the 2x2 covariance
            float tr = sxx + szz;
            float det = sxx * szz - sxz * sxz;
            float disc = Mathf.Sqrt(Mathf.Max(0f, tr * tr / 4f - det));
            float l1 = tr / 2f + disc;
            float l2 = Mathf.Max(1e-3f, tr / 2f - disc);
            Vector2 axis = Mathf.Abs(sxz) > 1e-3f ? new Vector2(l1 - szz, sxz).normalized : (sxx >= szz ? Vector2.right : Vector2.up);
            float aspect = Mathf.Sqrt(l1 / l2);

            _center = c;
            _axis = axis;
            _count = aspect >= 1.45f || pts.Count < 12 ? 2 : 4;
            List<float> along = pts.Select(p => Vector2.Dot(p - c, axis)).OrderBy(t => t).ToList();
            _split = along[along.Count / 2];
            _sides = Enumerable.Range(0, _count).Select(_ => new Side()).ToArray();
            int[] per = new int[_count];
            foreach (Vector2 p in pts)
            {
                per[SideOf(new Vector3(p.x, 0f, p.y))]++;
            }

            Log.Info($"LR Population: {pts.Count} PMC spawn points split into {_count} sides along ({axis.x:0.00}, {axis.y:0.00}), spread {aspect:0.0}:1 - points per side {string.Join(" / ", per)}");
        }

        private static int SideOf(Vector3 pos)
        {
            Vector2 d = new Vector2(pos.x, pos.z) - _center;
            float t = Vector2.Dot(d, _axis) - _split;
            if (_count == 2)
            {
                return t < 0f ? 0 : 1;
            }

            float n = Vector2.Dot(d, new Vector2(-_axis.y, _axis.x));
            return (t < 0f ? 0 : 2) + (n < 0f ? 0 : 1);
        }

        internal static SpawnPointParams Pick(SpawnPointParams[] valid, SpawnPointParams[] eligible, bool all, ESpawnCategoryMask extraMask)
        {
            if (!_built)
            {
                Build(valid, all, extraMask);
            }

            if (_count == 0)
            {
                return eligible[Random.Range(0, eligible.Length)];
            }

            // Load per side: at the start what was placed there, later the PMCs alive there (placed but not yet spawned squads count too)
            int[] load = new int[_count];
            bool start = RaidHelpers.IsBeginningOfRaid();
            for (int i = 0; i < _count; i++)
            {
                load[i] = _sides[i].Placed;
            }

            if (!start)
            {
                int[] alive = new int[_count];
                foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
                {
                    BotOwner bot = p?.AIData?.BotOwner;
                    if (bot != null && !bot.IsDead && Bots.BotProfiles.Get(bot)?.Kind == Bots.BotKind.Pmc)
                    {
                        alive[SideOf(p.Position)]++;
                    }
                }

                load = alive;
            }

            List<int> order = Enumerable.Range(0, _count).OrderBy(i => load[i]).ThenBy(_ => Random.value).ToList();
            foreach (int side in order)
            {
                SpawnPointParams[] there = eligible.Where(sp => SideOf(sp.Position) == side).ToArray();
                if (there.Length == 0)
                {
                    continue;
                }

                SpawnPointParams chosen = Settings.SpawnRotation.Value ? SpawnZones.Pick(there) : there[Random.Range(0, there.Length)];
                int size = Mathf.Max(1, PendingGroupSize);
                _sides[side].Placed += size;
                _sides[side].Squads++;
                Log.Debug($"spawn side #{side + 1} for a squad of {size} (load {string.Join("/", load)}), {there.Length} free points there");
                return chosen;
            }

            return eligible[Random.Range(0, eligible.Length)];
        }
    }

    /// <summary>Remembers how big the PMC group QuestingBots is placing is, for <see cref="SpawnSides"/>.</summary>
    public class QbPmcGroupSizePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.PMCGenerator"), "GetSpawnPositionsForBotGroup");

        [PatchPrefix]
        protected static void PatchPrefix(object botGroup)
        {
            try
            {
                // QuestingBots' own count of the profiles in the group (BotSpawnInfo.GeneratedBotCount)
                int size = Traverse.Create(botGroup).Property("GeneratedBotCount").GetValue<int>();
                SpawnSides.PendingGroupSize = Mathf.Max(1, size);
            }
            catch (System.Exception e)
            {
                SpawnSides.PendingGroupSize = 1;
                Diag.Report("QbPmcGroupSizePatch", e);
            }

            SpawnSides.PlacingPmcs = true;
        }

        [PatchPostfix]
        protected static void PatchPostfix()
        {
            SpawnSides.PlacingPmcs = false;
        }
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Starting zones take turns (F12 -> 6 "Rotate the starting zones", 1.3.3). Inside its side of the map a PMC squad stood on a random free point, so a zone of 9 points (Customs, east: 400, -100) came up three times
    /// as often as one of 3, and whole parts - the terminal with the garages, the middle - went months with a start now and then (10 raids of 2026-11-08 / 09: 34 % of the squads started in the west half, 48 % in
    /// the east end, 14 % in x 60-300). The zones are cells of 90 m; the history of the starts (learned/&lt;map&gt;.spawns.json, faded by 15 % a raid) gives each its weight 1 / (1 + starts)^1.2, zones are equal whatever
    /// the number of their points, and a zone near cold corners of the atlas or near the locked room of an EFT quest (the terminal of Customs, the key of "A package from the past") weighs up to twice as much.
    /// </summary>
    public static class SpawnZones
    {
        private const float Cell = 90f;

        private class History
        {
            public string LocationId;
            public Dictionary<string, float> Uses = new Dictionary<string, float>();
        }

        private static History _h;
        private static string _loc;
        private static List<Vector2> _cold = new List<Vector2>();
        private static List<Vector2> _wanted = new List<Vector2>();

        public static int Picks { get; private set; }
        public static int Steered { get; private set; }

        public static string Summary => _h == null ? "spawn zones not used" : $"spawn zones: {Picks} squads placed by rotation ({Steered} in a zone used less than the average of the map), {_h.Uses.Count} zones in the history";

        public static void Clear()
        {
            _h = null;
            _loc = null;
            _cold = new List<Vector2>();
            _wanted = new List<Vector2>();
            Picks = Steered = 0;
        }

        private static string PathOf(string loc) => System.IO.Path.Combine(Plugin.PluginDir, "learned", loc + ".spawns.json");

        private static string Key(Vector3 p) => Mathf.FloorToInt(p.x / Cell) + "_" + Mathf.FloorToInt(p.z / Cell);

        private static void Ensure()
        {
            string loc = Raid.MapIntel.Current?.LocationId;
            if (string.IsNullOrEmpty(loc) || (_h != null && _loc == loc))
            {
                return;
            }

            _loc = loc;
            _h = new History { LocationId = loc };
            try
            {
                string path = PathOf(loc);
                if (System.IO.File.Exists(path))
                {
                    _h = Newtonsoft.Json.JsonConvert.DeserializeObject<History>(System.IO.File.ReadAllText(path)) ?? _h;
                }

                // The old starts fade: 15 % a raid
                foreach (string k in _h.Uses.Keys.ToList())
                {
                    _h.Uses[k] *= 0.85f;
                }
            }
            catch (System.Exception e)
            {
                Log.Warn($"LR Spawn: the start history of {loc} is unreadable, starting a new one: {e.Message}");
            }

            _cold = Raid.Atlas.ColdSpots(loc).Select(s => new Vector2(s.X, s.Z)).ToList();
            _wanted = new List<Vector2>();
            Raid.MapIntel intel = Raid.MapIntel.Current;
            if (intel != null)
            {
                foreach (List<Raid.LockedSpot> spots in intel.SpotsByKey.Values)
                {
                    foreach (Raid.LockedSpot s in spots)
                    {
                        if (Raid.QuestAudit.QuestDoors.Contains(s.Id) && s.Outside != null && s.Outside.Length >= 3)
                        {
                            _wanted.Add(new Vector2(s.Outside[0], s.Outside[2]));
                        }
                    }
                }
            }
        }

        private static int Near(List<Vector2> list, Vector3 p, float r)
        {
            int n = 0;
            float r2 = r * r;
            foreach (Vector2 v in list)
            {
                float dx = v.x - p.x, dz = v.y - p.z;
                if (dx * dx + dz * dz <= r2)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>One of the free points: a zone by its weight, then a random point of it.</summary>
        internal static SpawnPointParams Pick(SpawnPointParams[] points)
        {
            try
            {
                Ensure();
                if (_h == null || points.Length < 2)
                {
                    return points[Random.Range(0, points.Length)];
                }

                Dictionary<string, int> size = new Dictionary<string, int>();
                foreach (SpawnPointParams sp in points)
                {
                    string k = Key(sp.Position);
                    size[k] = size.TryGetValue(k, out int n) ? n + 1 : 1;
                }

                Dictionary<string, float> weight = new Dictionary<string, float>();
                Dictionary<string, Vector3> sample = new Dictionary<string, Vector3>();
                foreach (KeyValuePair<string, int> z in size)
                {
                    Vector3 at = points.First(sp => Key(sp.Position) == z.Key).Position;
                    sample[z.Key] = at;
                    _h.Uses.TryGetValue(z.Key, out float uses);
                    float cold = 1f + Mathf.Min(1f, Near(_cold, at, 150f) / 10f);
                    float wanted = 1f + Mathf.Min(1f, 0.6f * Near(_wanted, at, 160f));
                    weight[z.Key] = Mathf.Pow(1f / (1f + uses), 1.2f) * cold * wanted;
                }

                float total = weight.Values.Sum();
                float roll = Random.value * total;
                string chosen = weight.Keys.Last();
                foreach (KeyValuePair<string, float> w in weight)
                {
                    roll -= w.Value;
                    if (roll <= 0f)
                    {
                        chosen = w.Key;
                        break;
                    }
                }

                SpawnPointParams[] there = points.Where(sp => Key(sp.Position) == chosen).ToArray();
                SpawnPointParams result = there[Random.Range(0, there.Length)];

                _h.Uses.TryGetValue(chosen, out float before);
                float avg = _h.Uses.Count > 0 ? _h.Uses.Values.Average() : 0f;
                Picks++;
                if (before < avg)
                {
                    Steered++;
                }

                _h.Uses[chosen] = before + 1f;
                Save();
                Log.Debug($"spawn zone {chosen}: {there.Length} of {points.Length} free points, starts so far {before:0.0} (average {avg:0.0}), {weight.Count} zones to choose from");
                return result;
            }
            catch (System.Exception e)
            {
                Diag.Report("SpawnZones", e);
                return points[Random.Range(0, points.Length)];
            }
        }

        private static void Save()
        {
            try
            {
                string path = PathOf(_loc);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(_h));
            }
            catch (System.Exception e)
            {
                Log.Warn($"LR Spawn: the start history could not be saved: {e.Message}");
            }
        }
    }
}
