using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Patches;
using LiveRaid.Client.Tactics;
using QuestingBots.Components.Spawning;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Player scavs come in to replace the PMCs that are gone (F12 -> 6 "Player scavs as replacements", with "Lively map"). Measured raids of 2026-10-31:
    /// on Customs the PMCs went 11 -> 4 by minute 25 of 40, the first player scav came at minute 20 - from the middle of the raid there were hardly any
    /// people on the map. Online the players who die are replaced by scavs coming in all raid long.
    ///
    /// Nobody new is made: QuestingBots draws every player scav group of the raid at its start, each with its own hour (the game's scav-run times,
    /// mostly late). When "people" (PMC bots, player scavs and the human players of the lobby) fall under the share of the lobby, the earliest group
    /// still to come is brought forward to now - until the last 10 minutes. Player scavs at once stay under "Lively map"'s cap, the map's bot cap
    /// is QuestingBots'. After a loud fight (two people dead within 3 minutes and 120 m) the wait is shorter and the group comes in on the side of that
    /// fight (QbRandomSpawnPointPatch: a free point 90-350 m from it, never within 150 m of a human player) - a scav run to fresh bodies.
    /// </summary>
    public static class PScavReplacement
    {
        private const float TickSeconds = 20f;
        private const float LastMinutes = 600f;
        private const float FirstSeconds = 150f;

        private static readonly HashSet<object> _pulled = new HashSet<object>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;
        private static float _shortSince = -1f;
        private static float _wait;
        private static Vector3? _fight;
        private static float _fightUntil;
        private static Vector3? _cold;
        private static float _coldUntil;

        public static int Groups { get; private set; }
        public static int Scavs { get; private set; }
        public static int NearFights { get; private set; }
        public static int ToColdGround { get; private set; }
        public static int ColdPlaced { get; private set; }
        public static int Short { get; private set; }

        public static string Summary => $"player scavs as replacements: {Groups} group(s), {Scavs} scav(s) brought forward ({NearFights} placed on the side of a loud fight, {ToColdGround} group(s) sent to ground nobody had been to - {ColdPlaced} placed there), ticks short of people with none left to bring {Short}";

        public static bool On => Settings.Enabled.Value && Settings.PScavReplace.Value && Settings.LivelyMap.Value && Settings.LobbyLikeOnline.Value;

        // Placing a brought-forward group right now (PScavGenerator.GetSpawnPositionsForBotGroup) - QbRandomSpawnPointPatch puts it by the fight
        internal static bool PlacingNearFight;

        public static void Clear()
        {
            _pulled.Clear();
            _next = 0f;
            _shortSince = -1f;
            _fight = null;
            _fightUntil = 0f;
            _cold = null;
            _coldUntil = 0f;
            PlacingNearFight = false;
            Groups = Scavs = NearFights = Short = ToColdGround = ColdPlaced = 0;
        }

        /// <summary>The lobby in people: the PMC bots of the start and the human PMCs of the group (QuestingBots counts one human on its own).</summary>
        public static int Lobby()
        {
            int bots = QbPmcReservePatch.InitialCount;
            if (bots <= 0)
            {
                return 0;
            }

            return bots + (Net.ScavRaid ? 0 : HumanPeople());
        }

        /// <summary>People on the map now: living PMC bots, player scavs and human PMCs (an observer's hidden body is nobody).</summary>
        public static int People(GameWorld world)
        {
            int n = 0;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                BotKind kind = BotProfiles.Get(bot)?.Kind ?? BotKind.Other;
                if (kind == BotKind.Pmc || kind == BotKind.PlayerScav)
                {
                    n++;
                }
            }

            return n + (Net.ScavRaid ? 0 : HumanPeople());
        }

        private static int HumanPeople() => Net.Humans().Count(h => !Spectator.ObserverMode.Is(h));

        /// <summary>The fresh bodies a brought-forward group goes to (valid while that group is being placed).</summary>
        internal static bool FightPoint(out Vector3 point)
        {
            point = _fight ?? default;
            return PlacingNearFight && _fight.HasValue && Time.time < _fightUntil;
        }

        /// <summary>The untouched part of the map a brought-forward group comes in at (valid while that group is being placed).</summary>
        internal static bool ColdPoint(out Vector3 point)
        {
            point = _cold ?? default;
            return PlacingNearFight && _cold.HasValue && Time.time < _coldUntil;
        }

        internal static void ColdUsed() => ColdPlaced++;

        internal static bool IsPulled(object group) => group != null && _pulled.Contains(group);

        internal static void NearFightUsed() => NearFights++;

        public static void Tick()
        {
            if (!On || !Net.RunsBots || Time.time < _next)
            {
                return;
            }

            _next = Time.time + TickSeconds;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            float elapsed = RaidHelpers.GetRaidElapsedSeconds();
            float remaining = RaidHelpers.GetRemainingRaidTimeSeconds();
            bool smart = Raid.PopulationBalance.On;
            if (elapsed < FirstSeconds || remaining < (smart ? Raid.PopulationBalance.LastSeconds(elapsed, remaining) : LastMinutes))
            {
                return;
            }

            int lobby = Lobby();
            if (lobby <= 0)
            {
                return;
            }

            int target = smart ? Raid.PopulationBalance.PeopleTarget(lobby, elapsed, remaining) : Mathf.CeilToInt(lobby * Settings.PeopleShare.Value / 100f);
            int people = People(world);
            int missing = target - people;
            if (missing <= 0)
            {
                _shortSince = -1f;
                return;
            }

            // A crowd round a human (or the free camera): nobody new comes in meanwhile (smart population balance)
            if (smart && Raid.PopulationBalance.HoldArrivals)
            {
                return;
            }

            // A loud fight lately: the scavs come sooner, and to it
            bool loud = LoudFight(out Vector3 fight);
            if (_shortSince < 0f)
            {
                _shortSince = Time.time;
                _wait = loud ? Random.Range(10f, 30f) : Random.Range(40f, 120f);
            }

            if (Time.time - _shortSince < _wait)
            {
                return;
            }

            if (!world.TryGetComponent(out PScavGenerator generator) || generator == null)
            {
                return;
            }

            // The earliest group that has not come yet and is not due yet; one that fits the gap if there is one
            List<QuestingBots.Models.BotSpawnInfo> waiting = generator.GetBotGroups()
                .Where(g => g != null && !g.HasSpawnStarted && !g.HaveAllBotsSpawned && g.GeneratedBotCount > 0 && g.RaidETRangeToSpawn.Min > elapsed + 5)
                .OrderBy(g => g.RaidETRangeToSpawn.Min)
                .ToList();
            QuestingBots.Models.BotSpawnInfo pick = waiting.FirstOrDefault(g => g.GeneratedBotCount <= missing + 1) ?? waiting.OrderBy(g => g.GeneratedBotCount).FirstOrDefault();
            if (pick == null)
            {
                Short++;
                return;
            }

            double was = pick.RaidETRangeToSpawn.Min;
            pick.RaidETRangeToSpawn.Min = elapsed;
            _pulled.Add(pick);
            Groups++;
            Scavs += pick.GeneratedBotCount;
            _shortSince = -1f;

            // 1.4.0: not every scav run goes to the fresh bodies (F12 -> 6 "Replacements to the fight, %"). Raid 2026-11-10 12:37 (Reserve): seventeen player scavs
            // in a row came in "to the fight" at the same two squares and died there - the people on the map stayed at 3-5 of 8 wanted all raid. The rest come in
            // at the part of the map nobody has been to yet, away from the fights: they live, and the map gets walked
            _fight = null;
            _cold = null;
            string where = "";
            bool toFight = loud && _rng.NextDouble() * 100.0 < Settings.PScavToFight.Value;
            if (toFight)
            {
                _fight = fight;
                _fightUntil = Time.time + 90f;
                where = $", to the fight at ({fight.x:0}, {fight.z:0})";
            }
            else
            {
                List<Vector3> fights = WorldEvents.Kills.Where(k => Time.time - k.Time < 300f).Select(k => k.Position).ToList();
                if (Raid.CoverageLedger.TryColdArea(fights, _rng, out Vector3 cold, out int spots))
                {
                    _cold = cold;
                    _coldUntil = Time.time + 90f;
                    ToColdGround++;
                    where = $", to ground nobody has been to round ({cold.x:0}, {cold.z:0}) - {spots} spots{(loud ? ", not to the fight" : "")}";
                }
            }

            Log.Info($"LR PScav: {pick.GeneratedBotCount} come(s) in to replace the people gone (people {people} of {target} wanted, lobby {lobby}); " +
                     $"they were due at {Clock(was)}{where}");
        }

        /// <summary>Two people (PMCs, player scavs, humans) killed within 3 minutes and 120 m of each other in the last 4 minutes.</summary>
        private static bool LoudFight(out Vector3 point)
        {
            point = default;
            float now = Time.time;
            List<KillEvent> recent = WorldEvents.Kills
                .Where(k => now - k.Time < 240f && (k.VictimPmc || k.VictimHuman || k.VictimKind == BotKind.PlayerScav))
                .OrderByDescending(k => k.Time)
                .ToList();
            foreach (KillEvent a in recent)
            {
                foreach (KillEvent b in recent)
                {
                    if (a != b && Mathf.Abs(a.Time - b.Time) < 180f && Vector3.Distance(a.Position, b.Position) < 120f)
                    {
                        point = (a.Position + b.Position) * 0.5f;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
    }

    /// <summary>Marks the placing of a group that PScavReplacement brought forward, for QbRandomSpawnPointPatch.</summary>
    public class QbPScavPlacingPatch : SPT.Reflection.Patching.ModulePatch
    {
        protected override System.Reflection.MethodBase GetTargetMethod() =>
            HarmonyLib.AccessTools.Method(typeof(PScavGenerator), "GetSpawnPositionsForBotGroup");

        [SPT.Reflection.Patching.PatchPrefix]
        protected static void PatchPrefix(object botGroup)
        {
            PScavReplacement.PlacingNearFight = PScavReplacement.IsPulled(botGroup);
        }

        [SPT.Reflection.Patching.PatchPostfix]
        protected static void PatchPostfix()
        {
            PScavReplacement.PlacingNearFight = false;
        }
    }
}
