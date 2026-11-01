using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// The plan of the raid's population, no Unity and no EFT in it (tools/bindtest runs <see cref="SelfTest"/>): how many people (PMCs and player scavs),
    /// how many AI scavs, how fast the people are made up for, per map. The numbers of each map come from SPT's own locations (min/max players, bot cap,
    /// raid time) and two traits of the map: its size (Customs = 1) and how indoor it is (0 = open field, 1 = corridors).
    /// </summary>
    public static class PopulationPlan
    {
        public struct Profile
        {
            public string Id;
            public int Min, Max, BotMax, Minutes;
            public float Size, Indoor;
        }

        private static Profile P(string id, int min, int max, int botMax, int minutes, float size, float indoor) =>
            new Profile { Id = id, Min = min, Max = max, BotMax = botMax, Minutes = minutes, Size = size, Indoor = indoor };

        private static readonly Profile[] Table =
        {
            P("bigmap", 10, 12, 19, 40, 1.0f, 0.35f),
            P("factory4_day", 7, 8, 0, 20, 0.12f, 0.95f),
            P("factory4_night", 5, 6, 22, 25, 0.12f, 0.95f),
            P("Interchange", 10, 14, 30, 40, 1.25f, 0.8f),
            P("laboratory", 8, 10, 25, 35, 0.5f, 1.0f),
            P("Labyrinth", 5, 5, 30, 40, 0.7f, 1.0f),
            P("Lighthouse", 10, 12, 29, 40, 1.6f, 0.3f),
            P("RezervBase", 9, 11, 28, 40, 1.1f, 0.65f),
            P("Sandbox", 9, 10, 20, 35, 0.5f, 0.45f),
            P("Sandbox_high", 9, 12, 24, 35, 0.5f, 0.45f),
            P("Shoreline", 10, 14, 31, 45, 1.8f, 0.3f),
            P("TarkovStreets", 12, 16, 48, 50, 1.6f, 0.6f),
            P("Woods", 10, 14, 30, 40, 2.2f, 0.1f),
        };

        public static readonly Profile Unknown = P("?", 8, 12, 20, 40, 1f, 0.4f);

        public static IReadOnlyList<Profile> All => Table;

        public static Profile For(string locationId)
        {
            if (!string.IsNullOrEmpty(locationId))
            {
                foreach (Profile p in Table)
                {
                    if (string.Equals(p.Id, locationId, StringComparison.OrdinalIgnoreCase))
                    {
                        return p;
                    }
                }
            }

            return Unknown;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Max(0f, Math.Min(1f, t));

        private static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));

        /// <summary>
        /// PMCs (people) at the start: SPT's online lobby of the map (the middle of min..max players), a little more on the big open maps,
        /// scaled by the raid's density and mood; the vanilla number (QuestingBots' draw plus the bigger lobby) is pulled toward it by <paramref name="strength"/>.
        /// Never fewer than 2, never beyond the map's players + 3.
        /// </summary>
        public static int StartPeople(Profile p, int vanilla, float density, float mood, float strength)
        {
            float model = (p.Min + p.Max) / 2f * density * mood + (p.Size >= 1.5f && p.Indoor < 0.4f ? 1f : 0f);
            float v = vanilla + (model - vanilla) * Clamp(strength, 0f, 1f);
            int lo = Math.Max(2, p.Min - 2);
            int hi = Math.Max(lo, p.Max + 3);
            return Math.Max(lo, Math.Min(hi, (int)Math.Round(v)));
        }

        /// <summary>
        /// The share of the lobby that should still be people at this point of the raid (<paramref name="f"/> = elapsed / whole raid). Online nobody comes
        /// in for the first deaths, then the scavs make the map up to a level that sinks as the raid ages; the open maps need more people to feel alive
        /// than the corridor ones. <paramref name="quiet"/> (0-0.1) is added while the whole map has been quiet.
        /// </summary>
        public static float ShareAt(Profile p, float f, float density, float mood, float quiet)
        {
            float s;
            if (f < 0.15f)
            {
                s = 0.85f;
            }
            else if (f < 0.5f)
            {
                s = Lerp(0.72f, 0.60f, (f - 0.15f) / 0.35f);
            }
            else if (f < 0.8f)
            {
                s = Lerp(0.60f, 0.46f, (f - 0.5f) / 0.3f);
            }
            else
            {
                s = Lerp(0.46f, 0.40f, (f - 0.8f) / 0.2f);
            }

            s *= 1f + 0.12f * (1f - p.Indoor) - 0.10f * p.Indoor;
            s *= Clamp(density * mood, 0.6f, 1.25f);
            return Clamp(s + quiet, 0.2f, 1f);
        }

        /// <summary>How many AI scavs the map carries comfortably (0 = the map's bot cap is not ours to manage). Less late in the raid.</summary>
        public static int ScavTarget(Profile p, float f, float density, float mood)
        {
            if (p.BotMax <= 0)
            {
                return 0;
            }

            float v = p.BotMax * 0.7f * density * mood * (f > 0.75f ? 0.85f : 1f);
            return Math.Min(p.BotMax, Math.Max(3, (int)Math.Round(v)));
        }

        /// <summary>Player scavs on the map at once: a part of the lobby, a little bigger on open maps.</summary>
        public static int PScavAtOnce(Profile p, int lobby) =>
            Math.Max(1, Math.Min(5, (int)Math.Round(lobby * (0.28f + 0.10f * (1f - p.Indoor)))));

        /// <summary>The last stretch of the raid (seconds) in which nobody new is brought in: a fifth of the raid, 5-10 minutes.</summary>
        public static float LastSeconds(float totalSeconds) => Clamp(totalSeconds * 0.2f, 300f, 600f);

        /// <summary>The rules checked on every map: numbers stay in range and move the right way. "POPULATION OK" or what failed.</summary>
        public static string SelfTest()
        {
            int checks = 0;
            foreach (Profile p in Table)
            {
                foreach (float density in new[] { 0.5f, 1f, 1.5f })
                {
                    foreach (float mood in new[] { 0.88f, 1f, 1.12f })
                    {
                        float last = 2f;
                        for (float f = 0f; f <= 1f; f += 0.05f)
                        {
                            float s = ShareAt(p, f, density, mood, 0f);
                            checks++;
                            if (s < 0.2f || s > 1f || s > last + 1e-4f)
                            {
                                return $"POPULATION FAILED: share {s:0.00} at {f:0.00} on {p.Id} (density {density}, mood {mood}) is out of range or rises";
                            }

                            last = s;
                        }

                        for (int vanilla = 2; vanilla <= 24; vanilla++)
                        {
                            foreach (float strength in new[] { 0f, 0.5f, 1f })
                            {
                                int n = StartPeople(p, vanilla, density, mood, strength);
                                checks++;
                                if (n < 2 || n > p.Max + 3 || n < p.Min - 2)
                                {
                                    return $"POPULATION FAILED: {n} people at start on {p.Id} (vanilla {vanilla}, density {density}, strength {strength})";
                                }
                            }
                        }

                        int scavs = ScavTarget(p, 0.5f, density, mood);
                        checks++;
                        if (p.BotMax > 0 ? scavs < Math.Min(3, p.BotMax) || scavs > p.BotMax : scavs != 0)
                        {
                            return $"POPULATION FAILED: {scavs} scavs on {p.Id} (bot cap {p.BotMax})";
                        }
                    }
                }

                int at = PScavAtOnce(p, 12);
                checks++;
                if (at < 1 || at > 5)
                {
                    return $"POPULATION FAILED: {at} player scavs at once on {p.Id}";
                }
            }

            if (StartPeople(For("bigmap"), 13, 1f, 1f, 0f) != 13 || StartPeople(For("bigmap"), 13, 1f, 1f, 1f) != 11)
            {
                return "POPULATION FAILED: Customs start numbers (13 at strength 0, 11 at strength 1)";
            }

            if (For("nowhere").Id != "?" || For("WOODS").Id != "Woods")
            {
                return "POPULATION FAILED: map lookup";
            }

            return $"POPULATION OK: {checks} checks on {Table.Length} maps";
        }
    }

    /// <summary>
    /// Smart head count (F12 -> 6 "Smart population balance"): how many PMCs, player scavs and AI scavs the raid carries, so that the map is neither a
    /// crowd nor a desert. Only NUMBERS are set here - no bot is sent anywhere and nothing is steered toward a player (where the bots walk is the job of
    /// the map-wide spread, <see cref="MapBalance"/>).
    ///  - Start: the lobby of the map (PopulationPlan.StartPeople) pulled from QuestingBots' draw plus the bigger lobby toward the map's online number;
    ///    each raid has a mood (quiet / usual / busy) - a few more or fewer of everything, so raids differ.
    ///  - Making up: the people share to keep (PScavReplacement) follows the age of the raid and the map (open maps need more people) instead of a flat 60 %;
    ///    nobody comes in the first fifth of the raid's deaths, nobody in its last stretch. While the whole map has been quiet for minutes the share
    ///    rises a little (+2 points per 20 s up to +10) - more people, not "events brought to somebody".
    ///  - Comfort: if any human (and the free camera) has too many bots within 120 m, the arrivals wait and the scav waves are held back
    ///    (BotSpawner.MaxBots lowered to what is alive; QuestingBots spawns its PMCs and player scavs on its own, so only the vanilla waves are held).
    ///  - Scav ceiling: the map's AI scavs stay under a comfortable number (map's bot cap x 0.7, less late), the cap given back as soon as they are under it.
    /// </summary>
    public static class PopulationBalance
    {
        private const float TickSeconds = 20f;
        private const float CrowdRadius = 120f;

        private static readonly System.Random _rng = new System.Random();
        private static readonly List<float> _shots = new List<float>();
        private static float _next;
        private static bool _hasProfile;
        private static PopulationPlan.Profile _profile;
        private static float _mood;
        private static string _moodName = "usual";
        private static int _natural = -1;
        private static bool _braked;
        private static bool _crowded;
        private static float _quiet;
        private static float _lastReport;
        private static int _people, _scavs, _pscavs, _crowd, _lastTarget;

        public static int BrakeTicks { get; private set; }
        public static int Crowds { get; private set; }
        public static int HeldTicks { get; private set; }
        public static int QuietTicks { get; private set; }
        public static int MaxCrowd { get; private set; }
        public static int StartFrom { get; private set; }
        public static int StartTo { get; private set; }

        public static bool On => Settings.Enabled.Value && Settings.PopulationBalance.Value;

        public static string Summary =>
            On ? $"population balance: map {(_hasProfile ? _profile.Id : "-")}, mood {_moodName}, PMC start {StartFrom} -> {StartTo}, scav waves held back {BrakeTicks} ticks, " +
                 $"arrivals held for a crowd {HeldTicks} ticks ({Crowds} crowds, most bots within {CrowdRadius:0} m of a human {MaxCrowd}), quiet map ticks {QuietTicks}"
               : "population balance off";

        public static void Clear()
        {
            _hasProfile = false;
            _mood = 0f;
            _moodName = "usual";
            _natural = -1;
            _braked = false;
            _crowded = false;
            _quiet = 0f;
            _next = 0f;
            _lastReport = 0f;
            _shots.Clear();
            _people = _scavs = _pscavs = _crowd = _lastTarget = 0;
            BrakeTicks = Crowds = HeldTicks = QuietTicks = MaxCrowd = StartFrom = StartTo = 0;
        }

        private static float Density => Settings.PopulationDensity.Value / 100f;

        private static PopulationPlan.Profile Profile()
        {
            if (!_hasProfile)
            {
                _profile = PopulationPlan.For(Singleton<GameWorld>.Instance?.LocationId);
                _hasProfile = true;
            }

            return _profile;
        }

        /// <summary>This raid's mood: a quiet one (a quarter of the raids), a usual one, a busy one (a quarter).</summary>
        private static float Mood()
        {
            if (_mood <= 0f)
            {
                double r = _rng.NextDouble();
                _mood = r < 0.25 ? 0.88f : r < 0.75 ? 1f : 1.12f;
                _moodName = _mood < 1f ? "quiet" : _mood > 1f ? "busy" : "usual";
            }

            return _mood;
        }

        /// <summary>Every shot of the raid, for the quiet-map measure (called by WorldEvents).</summary>
        public static void OnShot()
        {
            if (!On)
            {
                return;
            }

            float now = Time.time;
            _shots.Add(now);
            if (_shots.Count > 600 && now - _shots[0] > 300f)
            {
                _shots.RemoveAll(t => now - t > 300f);
            }
        }

        /// <summary>The PMC count at the start: QuestingBots' number (plus the bigger lobby) pulled toward the map's online number.</summary>
        public static int StartCount(int vanilla)
        {
            if (!On)
            {
                return vanilla;
            }

            PopulationPlan.Profile p = Profile();
            int result = PopulationPlan.StartPeople(p, vanilla, Density, Mood(), Settings.PopulationStrength.Value / 100f);
            StartFrom = vanilla;
            StartTo = result;
            Log.Info($"LR Population: balance on {p.Id} (size {p.Size:0.0}, indoor {p.Indoor:0.0}), this raid is {_moodName}: PMCs at start {vanilla} -> {result} " +
                     $"(the map's online lobby {p.Min}-{p.Max}), AI scavs about {PopulationPlan.ScavTarget(p, 0f, Density, Mood())}");
            return result;
        }

        /// <summary>People to keep on the map now (PScavReplacement): the share for this age of the raid, of the lobby.</summary>
        public static int PeopleTarget(int lobby, float elapsed, float remaining)
        {
            float total = Mathf.Max(60f, elapsed + remaining);
            float share = PopulationPlan.ShareAt(Profile(), elapsed / total, Density, Mood(), _quiet);
            return Mathf.CeilToInt(lobby * share);
        }

        public static float LastSeconds(float elapsed, float remaining) => PopulationPlan.LastSeconds(Mathf.Max(60f, elapsed + remaining));

        public static int PScavAtOnce(int lobby) => PopulationPlan.PScavAtOnce(Profile(), lobby);

        /// <summary>Arrivals wait: some human (or the free camera) has a crowd around.</summary>
        public static bool HoldArrivals
        {
            get
            {
                if (On && _crowded)
                {
                    HeldTicks++;
                    return true;
                }

                return false;
            }
        }

        public static void Tick()
        {
            if (!On || !Net.RunsBots || Time.time < _next)
            {
                return;
            }

            float now = Time.time;
            _next = now + TickSeconds;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            float elapsed = RaidHelpers.GetRaidElapsedSeconds();
            float remaining = RaidHelpers.GetRemainingRaidTimeSeconds();
            float f = Mathf.Clamp01(elapsed / Mathf.Max(60f, elapsed + remaining));
            PopulationPlan.Profile p = Profile();

            // Census: who is on the map, and where the people (and the free camera) look from
            List<Vector3> bots = new List<Vector3>();
            int pmc = 0, pscav = 0, scav = 0;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                BotKind kind = BotProfiles.Get(bot)?.Kind ?? BotKind.Other;
                if (kind == BotKind.Pmc)
                {
                    pmc++;
                }
                else if (kind == BotKind.PlayerScav)
                {
                    pscav++;
                }
                else if (kind == BotKind.Scav)
                {
                    scav++;
                }

                bots.Add(player.Position);
            }

            _people = pmc + pscav;
            _scavs = scav;
            _pscavs = pscav;

            List<Vector3> viewers = new List<Vector3>();
            foreach (Player human in Net.Humans())
            {
                if (!Spectator.ObserverMode.Is(human))
                {
                    viewers.Add(human.Position);
                }
            }

            if (Spectator.SpectatorCamera.ListenerOverride(out Vector3 camera))
            {
                viewers.Add(camera);
            }

            int crowd = 0;
            foreach (Vector3 v in viewers)
            {
                int n = 0;
                foreach (Vector3 b in bots)
                {
                    if ((b - v).sqrMagnitude < CrowdRadius * CrowdRadius)
                    {
                        n++;
                    }
                }

                crowd = Mathf.Max(crowd, n);
            }

            _crowd = crowd;
            MaxCrowd = Mathf.Max(MaxCrowd, crowd);
            int cap = Settings.CrowdCap.Value;
            if (!_crowded && crowd >= cap)
            {
                _crowded = true;
                Crowds++;
                Log.Info($"LR Population: {crowd} bots within {CrowdRadius:0} m of a human or the free camera (comfort cap {cap}) - arrivals wait, scav waves held back");
            }
            else if (_crowded && crowd <= cap - 2)
            {
                _crowded = false;
                Log.Info($"LR Population: the crowd round the human is down to {crowd} - arrivals and scav waves go on");
            }

            // The whole map quiet (not the player's corner): the share of people rises a little, up to +10 points
            float perMinute = 0f;
            float from = now - 240f;
            foreach (float t in _shots)
            {
                if (t >= from)
                {
                    perMinute++;
                }
            }

            perMinute /= 4f;
            if (f < 0.75f && elapsed > 240f && perMinute < 4f + 4f * p.Size)
            {
                _quiet = Mathf.Min(0.10f, _quiet + 0.02f);
                QuietTicks++;
            }
            else
            {
                _quiet = Mathf.Max(0f, _quiet - 0.03f);
            }

            // Scav waves: held back while the AI scavs are over the comfortable number or somebody has a crowd round him
            int scavTarget = PopulationPlan.ScavTarget(p, f, Density, Mood());
            _lastTarget = scavTarget;
            BotSpawner spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
            if (spawner != null)
            {
                if (_natural < 0)
                {
                    _natural = spawner.MaxBots;
                }

                if (_natural > 0)
                {
                    bool brake = _crowded || (scavTarget > 0 && scav >= scavTarget);
                    int want = brake ? Mathf.Max(1, spawner.AllBotsWithLoaded) : _natural;
                    if (spawner.MaxBots != want)
                    {
                        spawner.MaxBots = want;
                    }

                    if (brake)
                    {
                        BrakeTicks++;
                    }

                    if (brake != _braked)
                    {
                        _braked = brake;
                        Log.Info(brake
                            ? $"LR Population: scav waves held back ({scav} AI scavs, comfortable {scavTarget}{(_crowded ? ", a crowd round a human" : "")})"
                            : $"LR Population: scav waves go on ({scav} AI scavs, comfortable {scavTarget}, the map's cap {_natural})");
                    }
                }
            }

            if (now - _lastReport > 300f)
            {
                _lastReport = now;
                int lobby = PScavReplacement.Lobby();
                Log.Info($"LR Population: {p.Id} at {elapsed / 60f:0} min ({f * 100f:0} %) - people {_people} of {PeopleTarget(Mathf.Max(lobby, 1), elapsed, remaining)} wanted (lobby {lobby}, player scavs {_pscavs}), " +
                         $"AI scavs {_scavs} of {scavTarget} comfortable, most bots round a human {_crowd}, map shots {perMinute:0}/min{(_quiet > 0f ? $" (quiet: +{_quiet * 100f:0} points of people)" : "")}");
            }
        }
    }
}
