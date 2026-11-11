using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Tactics;
using QuestingBots.Components;
using QuestingBots.Controllers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// People's corridors (F12 -> 3 "Walk where people walk"). Online the encounters happen on the way: everybody runs from the spawn to the loot
    /// places and on to the exits along the same few ways, and that is where they cross. QuestingBots walks the shortest way from one loot point to
    /// the next, all over the map - the bots' ways seldom cross the player's (raids of 2026-10-31: the first PMC seen at minute 7-12).
    ///
    /// The corridors are a grid of 16 m cells, weighted by
    ///  - your own trails (learned/trails/&lt;map&gt;_*.csv of PlayerTrail): each trail through a cell counts 2 - the way you really go;
    ///  - the ways people take on any map: navmesh paths from the PMC spawn points to the key places and the hottest regions, and from those to the
    ///    exits (one path every half second after MapIntel is ready, ~40 of them).
    /// The busiest cells (a third of the top weight or more) are corridors. Then, one bot every half second: a calm leader or lone PMC / player scav
    /// on a way of 80 m or more that keeps off the corridors goes by the best corridor cell between it and its goal (at most a third more way),
    /// then on - QuestingBots takes over on arrival. Explorers and looters do it the most, rats the least; a way already on a corridor is left as it
    /// is. The cover rule (CoverRoute) still applies on the stretch to the corridor. And a region a corridor runs through is wanted a little more by
    /// QuestingBots' quests (Multiplier).
    /// </summary>
    public static class Corridors
    {
        private const float Cell = 16f;
        private const float CorridorShare = 0.33f;

        private class Pending
        {
            public Vector3 From, To;
        }

        private static readonly Dictionary<long, float> _weight = new Dictionary<long, float>();
        private static readonly List<(Vector3 pos, float w)> _cells = new List<(Vector3, float)>();
        private static readonly Queue<Pending> _jobs = new Queue<Pending>();
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly Dictionary<string, Vector3> _detouredFor = new Dictionary<string, Vector3>();
        private static readonly Dictionary<(int, int), float> _regionShare = new Dictionary<(int, int), float>();
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static readonly NavMeshPath _probe = new NavMeshPath();
        private static readonly System.Random _rng = new System.Random();
        private static string _location;
        private static bool _queued, _built;
        private static float _tick, _nextSample, _max, _threshold, _readySince = -1f;
        private static int _cursor, _trails, _paths;

        public static int Detours { get; private set; }
        public static int OnCorridor { get; private set; }
        public static int NoCell { get; private set; }
        private static int _samples, _samplesIn;

        public static string Summary => !_built ? "people's corridors: not built" :
            $"people's corridors: {_cells.Count} cells from {_trails} trail(s) of yours and {_paths} spawn-loot-exit ways; {Detours} detours by a corridor, " +
            $"ways already on one {OnCorridor}, no corridor on the way {NoCell}; share of calm PMC time in corridors {(_samples == 0 ? "-" : $"{100 * _samplesIn / _samples} %")}";

        public static bool On => Settings.Enabled.Value && Settings.PeopleCorridors.Value;

        public static void Clear()
        {
            _weight.Clear();
            _cells.Clear();
            _jobs.Clear();
            _next.Clear();
            _detouredFor.Clear();
            _regionShare.Clear();
            _location = null;
            _queued = _built = false;
            _tick = _nextSample = _max = _threshold = 0f;
            _readySince = -1f;
            _cursor = _trails = _paths = 0;
            Detours = OnCorridor = NoCell = 0;
            _samples = _samplesIn = 0;
        }

        private static long Key(float x, float z) => ((long)Mathf.FloorToInt(x / Cell) << 32) ^ (uint)Mathf.FloorToInt(z / Cell);

        private static void Add(Vector3 p, float w)
        {
            long k = Key(p.x, p.z);
            _weight.TryGetValue(k, out float old);
            _weight[k] = old + w;
        }

        public static bool IsCorridor(Vector3 p) => _built && _weight.TryGetValue(Key(p.x, p.z), out float w) && w >= _threshold;

        public static void Tick()
        {
            if (!On)
            {
                return;
            }

            MapIntel intel = MapIntel.Current;
            if (intel == null || !intel.Ready)
            {
                return;
            }

            if (!_queued)
            {
                // The key places and the hot regions come with the quests (QuestFactory, KeyPlaces - cleared at raid start), a little after the map's points: wait up to 90 s
                if (_readySince < 0f)
                {
                    _readySince = Time.time;
                }

                if (KeyPlaces.Places.Count == 0 && Time.time - _readySince < 90f)
                {
                    return;
                }

                _queued = true;
                _location = intel.LocationId;
                Diag.Guard("corridors: trails", () => ReadTrails(_location));
                Diag.Guard("corridors: ways", QueueWays);
            }

            if (Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 0.5f;
            if (_jobs.Count > 0)
            {
                Walk(_jobs.Dequeue());
                if (_jobs.Count == 0)
                {
                    Build();
                }

                return;
            }

            if (!_built)
            {
                Build();
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world.AllAlivePlayersList.Count == 0)
            {
                return;
            }

            if (Time.time >= _nextSample)
            {
                _nextSample = Time.time + 10f;
                Sample(world);
            }

            // One bot per tick, as CoverRoute
            for (int tries = 0; tries < world.AllAlivePlayersList.Count; tries++)
            {
                _cursor = (_cursor + 1) % world.AllAlivePlayersList.Count;
                BotOwner bot = world.AllAlivePlayersList[_cursor]?.AIData?.BotOwner;
                if (bot != null && Check(bot))
                {
                    return;
                }
            }
        }

        /// <summary>Your trails on this map: every trail counts once per cell it went through (2 for each - a human way is the best evidence).</summary>
        private static void ReadTrails(string location)
        {
            string dir = Path.Combine(Plugin.PluginDir, "learned", "trails");
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(dir, location + "_*.csv"))
            {
                HashSet<long> cells = new HashSet<long>();
                List<Vector3> points = new List<Vector3>();
                foreach (string line in File.ReadLines(file).Skip(1))
                {
                    string[] f = line.Split(',');
                    if (f.Length < 4 || !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        continue;
                    }

                    if (cells.Add(Key(x, z)))
                    {
                        points.Add(new Vector3(x, 0f, z));
                    }
                }

                // A trail of a raid spent as an observer is a point or two: no way at all
                if (cells.Count < 12)
                {
                    continue;
                }

                _trails++;
                foreach (Vector3 p in points)
                {
                    Add(p, 2f);
                }
            }
        }

        /// <summary>Spawn -> key places / hot regions -> exits: ~40 navmesh paths, walked one per tick.</summary>
        private static void QueueWays()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            List<Vector3> spawns = new List<Vector3>();
            LocationData data = world?.GetComponent<LocationData>();
            if (data != null)
            {
                spawns = data.GetAllValidSpawnPointParams()
                    .Where(sp => (sp.Categories & ESpawnCategoryMask.Player) != 0 && (sp.Sides & EPlayerSideMask.Pmc) != 0)
                    .Select(sp => (Vector3)sp.Position)
                    .ToList();
            }

            List<Vector3> places = KeyPlaces.Places.OrderByDescending(p => p.Weight).Take(6).Select(p => p.Center).ToList();
            float size = Settings.RoamRegionSize.Value;
            foreach ((int rx, int rz, float heat) h in QuestFactory.HottestRegions(5))
            {
                int rx = h.rx, rz = h.rz;
                if (h.heat >= 0.6f)
                {
                    Vector3 c = new Vector3((rx + 0.5f) * size, 0f, (rz + 0.5f) * size);
                    if (NavMesh.SamplePosition(c + Vector3.up * 30f, out NavMeshHit hit, 60f, NavMesh.AllAreas))
                    {
                        places.Add(hit.position);
                    }
                }
            }

            List<Vector3> exits = new List<Vector3>();
            try
            {
                ExfiltrationPoint[] points = world?.ExfiltrationController?.ExfiltrationPoints;
                if (points != null)
                {
                    exits.AddRange(points.Where(e => e != null).Select(e => e.transform.position));
                }
            }
            catch (Exception e)
            {
                Log.Debug("corridors: no exits - " + e.Message);
            }

            if (places.Count == 0)
            {
                return;
            }

            // Spread the spawns: up to 10, every other one by distance from the first
            foreach (Vector3 s in spawns.OrderBy(_ => _rng.Next()).Take(10))
            {
                foreach (Vector3 p in places.OrderBy(p => Flat(s, p)).Take(2))
                {
                    _jobs.Enqueue(new Pending { From = s, To = p });
                }
            }

            foreach (Vector3 p in places)
            {
                foreach (Vector3 e in exits.OrderBy(e => Flat(p, e)).Take(2))
                {
                    _jobs.Enqueue(new Pending { From = p, To = e });
                }
            }
        }

        private static void Walk(Pending job)
        {
            if (!NavMesh.SamplePosition(job.From, out NavMeshHit a, 8f, NavMesh.AllAreas) || !NavMesh.SamplePosition(job.To, out NavMeshHit b, 15f, NavMesh.AllAreas)
                || !NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
            {
                return;
            }

            _paths++;
            HashSet<long> cells = new HashSet<long>();
            Vector3[] corners = _path.corners;
            for (int i = 1; i < corners.Length; i++)
            {
                float len = Vector3.Distance(corners[i - 1], corners[i]);
                for (float t = 0f; t < len; t += Cell * 0.5f)
                {
                    Vector3 q = Vector3.Lerp(corners[i - 1], corners[i], t / Mathf.Max(0.01f, len));
                    if (cells.Add(Key(q.x, q.z)))
                    {
                        Add(q, 1f);
                    }
                }
            }
        }

        private static void Build()
        {
            _built = true;
            if (_weight.Count == 0)
            {
                Log.Info($"LR Corridors on {_location}: no trails of yours and no ways found - none");
                return;
            }

            // A cell's neighbours share its weight a little: a way is a band, not a line of cells
            Dictionary<long, float> smooth = new Dictionary<long, float>(_weight);
            foreach (KeyValuePair<long, float> kv in _weight)
            {
                int cx = (int)(kv.Key >> 32), cz = (int)(uint)kv.Key;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }

                        long k = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                        smooth.TryGetValue(k, out float w);
                        smooth[k] = w + kv.Value * 0.15f;
                    }
                }
            }

            _weight.Clear();
            foreach (KeyValuePair<long, float> kv in smooth)
            {
                _weight[kv.Key] = kv.Value;
            }

            // Top weight: the 95th percentile, so one spot every path passes (a bridge) does not flatten the rest
            List<float> sorted = _weight.Values.OrderBy(w => w).ToList();
            _max = sorted[Mathf.Clamp((int)(sorted.Count * 0.95f), 0, sorted.Count - 1)];
            _threshold = Mathf.Max(1.5f, _max * CorridorShare);
            float size = Settings.RoamRegionSize.Value;
            foreach (KeyValuePair<long, float> kv in _weight.Where(kv => kv.Value >= _threshold))
            {
                int cx = (int)(kv.Key >> 32), cz = (int)(uint)kv.Key;
                Vector3 c = new Vector3((cx + 0.5f) * Cell, 0f, (cz + 0.5f) * Cell);
                // The cell's ground: the nearest navmesh to its centre, from well above (roofs and floors pick the topmost - fine for ways in the open)
                if (NavMesh.SamplePosition(c + Vector3.up * 40f, out NavMeshHit hit, 50f, NavMesh.AllAreas) && Flat(hit.position, c) < Cell)
                {
                    _cells.Add((hit.position, kv.Value));
                    (int, int) region = (Mathf.FloorToInt(hit.position.x / size), Mathf.FloorToInt(hit.position.z / size));
                    _regionShare.TryGetValue(region, out float r);
                    _regionShare[region] = r + kv.Value;
                }
            }

            float top = _regionShare.Count == 0 ? 1f : _regionShare.Values.Max();
            foreach ((int, int) k in _regionShare.Keys.ToList())
            {
                _regionShare[k] /= top;
            }

            Log.Info($"LR Corridors on {_location}: {_cells.Count} corridor cells of {Cell:0} m from {_trails} trail(s) of yours and {_paths} spawn - loot - exit ways " +
                     $"({_regionShare.Count} regions crossed)");
        }

        /// <summary>A quest in a region corridors run through is wanted a little more (up to +25 % at full strength).</summary>
        public static float Multiplier(BotProfile p, string questName)
        {
            if (!On || !_built || p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || !QuestFactory.TryParseRegion(questName, out int rx, out int rz)
                || !_regionShare.TryGetValue((rx, rz), out float share))
            {
                return 1f;
            }

            return 1f + 0.25f * Settings.CorridorStrength.Value / 50f * share;
        }

        private static void Sample(GameWorld world)
        {
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || BotProfiles.Get(bot)?.Kind != BotKind.Pmc || !BotMind.IsCalm(bot))
                {
                    continue;
                }

                _samples++;
                if (IsCorridor(bot.Position))
                {
                    _samplesIn++;
                }
            }
        }

        /// <summary>True when the bot was looked at (whatever came of it).</summary>
        private static bool Check(BotOwner bot)
        {
            if (bot.IsDead || Spectator.TakeOver.IsTaken(bot) || (_next.TryGetValue(bot.ProfileId, out float next) && Time.time < next))
            {
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || TacticalTasks.SquadLeaderOf(bot) != null
                || TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || bot.Mover == null || !bot.Mover.HasPathAndNoComplete)
            {
                return false;
            }

            // Not on the way out (ExitPlan has its own way), not while looting
            BotObjectiveManager objectives = bot.GetObjectiveManager();
            if (objectives?.BotMonitor?.GetMonitor<QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor>()?.IsTryingToExtract == true)
            {
                return false;
            }

            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            if (brain != null && (brain.LootTaskRunning || brain.HasActiveLootable))
            {
                return false;
            }

            _next[bot.ProfileId] = Time.time + 20f;
            Vector3 dest = MoveGoal.Of(bot);
            if (_detouredFor.TryGetValue(bot.ProfileId, out Vector3 done) && (done - dest).sqrMagnitude < 15f * 15f)
            {
                return true;
            }

            if (Flat(bot.Position, dest) < 80f || _rng.NextDouble() > Chance(p))
            {
                _next[bot.ProfileId] = Time.time + 45f;
                return true;
            }

            if (!NavMesh.CalculatePath(bot.Position, dest, NavMesh.AllAreas, _path) || _path.corners.Length < 2)
            {
                return true;
            }

            float total = Length(_path.corners);
            if (OnCorridorShare(_path.corners, total) >= 0.3f)
            {
                OnCorridor++;
                _detouredFor[bot.ProfileId] = dest;
                return true;
            }

            // Corridor cells inside the ellipse "at most a third more way" between the bot and its goal, the busiest first, a little luck
            float straight = Flat(bot.Position, dest);
            float budget = 1.15f + 0.2f * Settings.CorridorStrength.Value / 50f;
            List<(Vector3 pos, float score)> picks = new List<(Vector3, float)>();
            foreach ((Vector3 pos, float w) c in _cells)
            {
                float d1 = Flat(bot.Position, c.pos);
                if (d1 < 25f || d1 + Flat(c.pos, dest) > straight * budget || Mathf.Abs(c.pos.y - bot.Position.y) > 25f)
                {
                    continue;
                }

                picks.Add((c.pos, c.w / _max * (0.6f + 0.8f * (float)_rng.NextDouble())));
            }

            foreach ((Vector3 pos, float score) c in picks.OrderByDescending(x => x.score).Take(3))
            {
                float a = PathLength(bot.Position, c.pos);
                float b = a > 0f ? PathLength(c.pos, dest) : -1f;
                if (a < 0f || b < 0f || a + b > total * 1.35f)
                {
                    continue;
                }

                Vector3 at = c.pos;
                TacticalTask task = new TacticalTask
                {
                    Kind = TaskKind.Relocate, StandAt = at, LookAt = dest + Vector3.up * 1.2f, Speed = 1f, Sprint = true, SprintBeyond = 10f, MovePose = 1f,
                    Until = Time.time + a / 3f + 12f,
                };
                if (TacticalTasks.Set(bot, task))
                {
                    Detours++;
                    _detouredFor[bot.ProfileId] = dest;
                    _next[bot.ProfileId] = Time.time + 40f;
                    Log.Detail("task", () => $"LR Route: {p.Name} goes by the people's corridor at ({at.x:0}, {at.z:0}) on its way to ({dest.x:0}, {dest.z:0}) " +
                                             $"(+{((a + b) / total - 1f) * 100f:0} % of the way)");
                }

                return true;
            }

            NoCell++;
            _detouredFor[bot.ProfileId] = dest;
            return true;
        }

        /// <summary>How eager the bot is to go where people go: explorers and looters the most, rats and long guns the least; 50 % strength = as listed.</summary>
        private static float Chance(BotProfile p)
        {
            float c;
            switch (p.Style)
            {
                case PlayStyle.Explorer: c = 0.65f; break;
                case PlayStyle.Looter: c = 0.6f; break;
                case PlayStyle.Hunter: c = 0.55f; break;
                case PlayStyle.Quester: c = 0.5f; break;
                case PlayStyle.Rusher: c = 0.45f; break;
                case PlayStyle.Camper: c = 0.3f; break;
                case PlayStyle.Sniper: c = 0.25f; break;
                case PlayStyle.Rat: c = 0.2f; break;
                default: c = 0.45f; break;
            }

            if (p.Kind == BotKind.PlayerScav)
            {
                c *= 0.8f;
            }

            return Mathf.Clamp01(c * Settings.CorridorStrength.Value / 50f);
        }

        private static float OnCorridorShare(Vector3[] corners, float total)
        {
            int n = 0, inside = 0;
            float walked = 0f;
            for (int i = 1; i < corners.Length && walked < total * 0.7f; i++)
            {
                float len = Vector3.Distance(corners[i - 1], corners[i]);
                for (float t = 0f; t < len; t += 8f)
                {
                    n++;
                    if (IsCorridor(Vector3.Lerp(corners[i - 1], corners[i], t / Mathf.Max(0.01f, len))))
                    {
                        inside++;
                    }
                }

                walked += len;
            }

            return n == 0 ? 0f : inside / (float)n;
        }

        private static float PathLength(Vector3 a, Vector3 b)
        {
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, _probe) || _probe.status != NavMeshPathStatus.PathComplete)
            {
                return -1f;
            }

            return Length(_probe.corners);
        }

        private static float Length(Vector3[] corners)
        {
            float len = 0f;
            for (int i = 1; i < corners.Length; i++)
            {
                len += Vector3.Distance(corners[i - 1], corners[i]);
            }

            return len;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
