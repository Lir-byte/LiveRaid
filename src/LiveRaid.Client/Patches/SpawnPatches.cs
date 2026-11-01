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
        internal static int Picked;
        internal static int ExtraPicked;

        internal static void Clear()
        {
            HumanStarts.Clear();
            Picked = ExtraPicked = 0;
            QbStartLobbyGroupPatch.Cut = 0;
            SpawnSides.Clear();
        }

        internal static string Summary => $"{Picked} squads placed on random points, {ExtraPicked} of them on group / opposite / PMC-bot points; {SpawnSides.Summary}; starting groups cut to fit the starting lobby {QbStartLobbyGroupPatch.Cut}";

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

            // Like online, squads start spread over the map's sides (Customs: half at one end, half at the other), not all on one
            __result =Settings.BalancedSpawnSides.Value && SpawnSides.PlacingPmcs
                ? SpawnSides.Pick(valid, eligible, all, extraMask)
                : eligible[Random.Range(0, eligible.Length)];
            bool extra = (__result.Value.Categories & allowedCategories) == 0 || (__result.Value.Sides & allowedSides) == 0;
            if (extra)
            {
                ExtraPicked++;
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
            __result = InitialCount + reserve;
            Log.Info($"LR Population: {InitialCount} PMCs at raid start{(InitialCount != vanilla ? $" (QuestingBots' {qb} + {extra} bigger lobby = {vanilla}, balanced to the map)" : extra > 0 ? $" (lobby {InitialCount - extra} + {extra})" : "")} + {reserve} in reserve");
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

                SpawnPointParams chosen = there[Random.Range(0, there.Length)];
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
