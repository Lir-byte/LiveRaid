using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    public enum PoiKind
    {
        Container,
        LooseLoot,
        Open,

        // A loose-loot spawn point from the SPT database: loot can appear there in any raid, whether or not it did this time
        LootSpot,

        // A place the player went to that the bots' map did not cover (PlayerTrail); never stored in the map cache
        Learned,
    }

    public class Poi
    {
        public float X, Y, Z;
        public PoiKind Kind;

        // How attractive the spot is for a looter (safe 6, weapon crate 4, jacket 1, open ground 0.2...)
        public float W = 1f;

        [JsonIgnore]
        public Vector3 Pos => new Vector3(X, Y, Z);

        // Open ground only: other spots of the same cell and floor, tried in turn when this one cannot be reached
        [JsonIgnore]
        public List<Vector3> Alternates;

        public static Poi At(Vector3 v, PoiKind kind, float w = 1f) => new Poi { X = v.x, Y = v.y, Z = v.z, Kind = kind, W = w };
    }

    /// <summary>A locked door or keyed container the bots may open with a key from their inventory.</summary>
    public class LockedSpot
    {
        public string Id;
        public string KeyId;
        public bool IsContainer;

        // Where a bot stands to use the key, and a point behind the door
        public float[] Outside;
        public float[] Interior;

        // Containers and loose loot near the interior point; more loot = more interesting key
        public int RoomValue;

        [JsonIgnore]
        public WorldInteractiveObject Object;

        [JsonIgnore]
        public Vector3? OutsidePos => Outside == null ? (Vector3?)null : new Vector3(Outside[0], Outside[1], Outside[2]);

        [JsonIgnore]
        public Vector3? InteriorPos => Interior == null ? (Vector3?)null : new Vector3(Interior[0], Interior[1], Interior[2]);

        public bool StillLocked => Object != null && Object.DoorState == EDoorState.Locked;
    }

    internal class MapIntelCache
    {
        public int Version = MapIntel.CacheVersion;
        public string LocationId;

        // Routes make places reachable: POIs are rebuilt when the route set changes
        public string RoutesKey = "";
        public List<LockedSpot> Spots = new List<LockedSpot>();
        public List<Poi> Pois = new List<Poi>();
        public Dictionary<string, float> Density = new Dictionary<string, float>();
    }

    /// <summary>
    /// Everything LiveRaid knows about the current map: which doors/containers are locked and by which key,
    /// what lies behind them, and a set of reachable points of interest covering the whole walkable area.
    /// Expensive path checks run once per map and are cached on disk.
    /// </summary>
    public class MapIntel
    {
        public const int CacheVersion = 6;

        // Two points are on different floors when their heights differ by at least this much
        public const float FloorGap = 2.2f;

        public static MapIntel Current { get; private set; }

        public string LocationId { get; private set; }
        public bool DoorsReady { get; private set; }
        public bool PoisReady { get; private set; }

        // POIs built and every locked door analysed (rooms behind doors known)
        public bool Ready => PoisReady && _poiJob == null;

        public readonly List<LockedSpot> Spots = new List<LockedSpot>();
        public readonly Dictionary<string, LockedSpot> SpotsById = new Dictionary<string, LockedSpot>();
        public readonly Dictionary<string, List<LockedSpot>> SpotsByKey = new Dictionary<string, List<LockedSpot>>();
        public readonly List<Poi> Pois = new List<Poi>();

        // Loot weight per DensityCell x DensityCell square, over every container and loose-loot spot (reachable or not)
        public const float DensityCell = 40f;
        public readonly Dictionary<string, float> Density = new Dictionary<string, float>();

        private readonly NavMeshPath _path = new NavMeshPath();
        private IEnumerator _poiJob;

        // Points known to be on the main walkable area (spawn points, then every POI found reachable).
        // Long NavMesh queries come back partial on big maps, so reachability is always checked from a nearby anchor.
        private const float AnchorCell = 60f;
        private const float AnchorReach = 180f;
        private readonly Dictionary<long, List<Vector3>> _anchors = new Dictionary<long, List<Vector3>>();
        private readonly List<LockedSpot> _pendingAnalysis = new List<LockedSpot>();
        private readonly List<Poi> _unreachable = new List<Poi>();

        public static void BeginRaid(string locationId)
        {
            Current = new MapIntel { LocationId = locationId };
        }

        public static void EndRaid()
        {
            Current = null;
        }

        private string CachePath => Path.Combine(Plugin.PluginDir, "cache", LocationId + ".json");

        /// <summary>Finds every locked door/container. Cheap scan; interior points come from cache or path checks.</summary>
        public void EnsureDoors()
        {
            if (DoorsReady)
            {
                return;
            }

            DoorsReady = true;
            Stopwatch sw = Stopwatch.StartNew();
            Routes.Apply(LocationId);
            AddInitialAnchors();

            List<WorldInteractiveObject> locked = new List<WorldInteractiveObject>();
            HashSet<string> seen = new HashSet<string>();
            int doorsTotal = 0, doorsLockedWithKey = 0, doorsBreachable = 0, doorsSealed = 0;
            foreach (WorldInteractiveObject wio in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>())
            {
                if (wio == null || string.IsNullOrEmpty(wio.Id) || !seen.Add(wio.Id))
                {
                    continue;
                }

                if (wio is Door census && census.Operatable)
                {
                    doorsTotal++;
                    if (census.DoorState == EDoorState.Locked)
                    {
                        if (!string.IsNullOrEmpty(census.KeyId))
                        {
                            doorsLockedWithKey++;
                        }
                        else if (census.CanBeBreached)
                        {
                            doorsBreachable++;
                        }
                        else
                        {
                            doorsSealed++;
                        }
                    }
                }

                if (wio.DoorState != EDoorState.Locked || string.IsNullOrEmpty(wio.KeyId) || !wio.Operatable)
                {
                    continue;
                }

                if (wio is Door || wio is Trunk || wio is LootableContainer)
                {
                    locked.Add(wio);
                }
            }

            // Door analysis is cached per door id across raids (door lock states can differ from raid to raid)
            MapIntelCache cache = LoadCache() ?? new MapIntelCache { LocationId = LocationId };
            Dictionary<string, LockedSpot> cached = cache.Spots.GroupBy(sp => sp.Id).ToDictionary(g => g.Key, g => g.First());
            int analyzed = 0;

            foreach (WorldInteractiveObject wio in locked)
            {
                if (!cached.TryGetValue(wio.Id, out LockedSpot spot))
                {
                    // Keys can be handed out right away; the room behind the door is found later by the background job
                    spot = new LockedSpot { Id = wio.Id, KeyId = wio.KeyId, IsContainer = wio is LootableContainer || wio is Trunk };
                    cached[wio.Id] = spot;
                    _pendingAnalysis.Add(spot);
                    analyzed++;
                }
                else if (wio is Trunk && !spot.IsContainer)
                {
                    // A car trunk is opened like a safe, not walked into: cached as a door before 0.8.1 (it never got a bot - no room behind it)
                    spot.IsContainer = true;
                    spot.Interior = null;
                    if (spot.RoomValue < 3)
                    {
                        spot.RoomValue = 3;
                    }
                }

                spot.Object = wio;
                spot.KeyId = wio.KeyId;
                AddSpot(spot);
            }

            _allSpots = cached.Values.ToList();
            if (cache.Pois.Count > 0 && (cache.RoutesKey ?? "") != Routes.Key)
            {
                Log.Info($"map intel {LocationId}: routes changed, rebuilding points of interest");
            }
            else if (cache.Pois.Count > 0)
            {
                Pois.AddRange(cache.Pois);
                foreach (KeyValuePair<string, float> kv in cache.Density)
                {
                    Density[kv.Key] = kv.Value;
                }

                foreach (Poi poi in cache.Pois)
                {
                    AddAnchor(poi.Pos);
                }

                PoisReady = true;
            }

            Log.Info($"doors on {LocationId}: {doorsTotal} operable, {doorsLockedWithKey} locked with a key (bots need the key), " +
                     $"{doorsBreachable} locked without a key but breachable (bots can kick them in), {doorsSealed} sealed (nobody can open them)");
            Log.Info($"map intel {LocationId}: {Spots.Count} locked spots ({Spots.Count(sp => sp.Interior != null)} with a room behind, {analyzed} to analyze), " +
                     $"{SpotsByKey.Count} distinct keys, {(PoisReady ? Pois.Count + " cached POIs" : "POIs pending")} [{sw.ElapsedMilliseconds} ms]");

            if (!PoisReady || _pendingAnalysis.Count > 0)
            {
                _poiJob = BuildAll();
            }
        }

        private List<LockedSpot> _allSpots = new List<LockedSpot>();

        /// <summary>Advances POI generation by a slice of work; call every frame from the raid component.</summary>
        public void StepPoiJob(int budgetMs)
        {
            if (_poiJob == null)
            {
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < budgetMs)
            {
                if (!_poiJob.MoveNext())
                {
                    _poiJob = null;
                    return;
                }
            }
        }

        /// <summary>Finishes POI generation right now (QuestingBots asks for quests synchronously).</summary>
        public void CompletePois()
        {
            EnsureDoors();
            while (_poiJob != null && _poiJob.MoveNext())
            {
            }

            _poiJob = null;
        }

        private void AddSpot(LockedSpot spot)
        {
            Spots.Add(spot);
            SpotsById[spot.Id] = spot;
            if (!SpotsByKey.TryGetValue(spot.KeyId, out List<LockedSpot> list))
            {
                SpotsByKey[spot.KeyId] = list = new List<LockedSpot>();
            }

            list.Add(spot);
        }

        private void AddInitialAnchors()
        {
            // The human player and all map spawn points stand on the main walkable area
            List<Vector3> starts = new List<Vector3>();
            foreach (Player player in Net.Humans())
            {
                starts.Add(player.Position);
            }

            var spawnPoints = Singleton<GameWorld>.Instance?.GetComponent<QuestingBots.Components.LocationData>()?.CurrentLocation?.SpawnPointParams;
            if (spawnPoints != null)
            {
                starts.AddRange(spawnPoints.Select(sp => (Vector3)sp.Position));
            }

            int added = 0;
            foreach (Vector3 start in starts)
            {
                if (NavMesh.SamplePosition(start, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                {
                    AddAnchor(hit.position);
                    added++;
                }
            }

            if (added == 0)
            {
                Log.Warn($"map intel {LocationId}: no navmesh anchor points found, reachability checks will fail");
            }
        }

        private void AddAnchor(Vector3 p)
        {
            long key = CellKey(p, AnchorCell);
            if (!_anchors.TryGetValue(key, out List<Vector3> list))
            {
                _anchors[key] = list = new List<Vector3>();
            }

            list.Add(p);
        }

        private List<Vector3> NearestAnchors(Vector3 p, float maxDist, int count)
        {
            int cx = Mathf.FloorToInt(p.x / AnchorCell);
            int cz = Mathf.FloorToInt(p.z / AnchorCell);
            int ring = Mathf.CeilToInt(maxDist / AnchorCell);
            float sq = maxDist * maxDist;
            List<Vector3> found = new List<Vector3>();
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                {
                    long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (_anchors.TryGetValue(key, out List<Vector3> list))
                    {
                        found.AddRange(list.Where(a => (a - p).sqrMagnitude < sq));
                    }
                }
            }

            return found.OrderBy(a => (a - p).sqrMagnitude).Take(count).ToList();
        }

        private float NearestAnchorDistance(Vector3 p)
        {
            List<Vector3> nearest = NearestAnchors(p, AnchorReach, 1);
            return nearest.Count > 0 ? Vector3.Distance(nearest[0], p) : float.MaxValue;
        }

        /// <summary>Can a bot walk from the main walkable area to this point (through LiveRaid routes too)?</summary>
        public bool IsReachable(Vector3 target) => Reachable(target);

        /// <summary>True when a complete path exists from one of the nearest known-reachable points.</summary>
        private bool Reachable(Vector3 target)
        {
            foreach (Vector3 anchor in NearestAnchors(target, AnchorReach, 4))
            {
                if (NavMesh.CalculatePath(anchor, target, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerator BuildAll()
        {
            if (!PoisReady)
            {
                IEnumerator pois = BuildPois();
                while (pois.MoveNext())
                {
                    yield return null;
                }
            }

            int rooms = 0;
            int analyzed = _pendingAnalysis.Count;
            foreach (LockedSpot spot in _pendingAnalysis)
            {
                if (spot.Object != null)
                {
                    Analyze(spot.Object, spot);
                    if (spot.Interior != null)
                    {
                        rooms++;
                    }
                }

                yield return null;
            }

            _pendingAnalysis.Clear();
            SaveCache();
            ReachabilityReport();
            if (analyzed > 0)
            {
                Log.Info($"map intel {LocationId}: analyzed {analyzed} locked spots, {rooms} have a room behind them; cached");
            }
        }

        /// <summary>
        /// Samples navmesh points around a locked object. A locked door carves the navmesh, so the side that
        /// cannot be reached from the main walkable area is the room behind it.
        /// </summary>
        private void Analyze(WorldInteractiveObject wio, LockedSpot spot)
        {
            Vector3 center = wio.transform.position;

            if (spot.IsContainer)
            {
                if (NavMesh.SamplePosition(center, out NavMeshHit hit, 2f, NavMesh.AllAreas) && Reachable(hit.position))
                {
                    spot.Outside = ToArray(hit.position);
                    spot.RoomValue = 3;
                }

                return;
            }

            Vector3? reachable = null;
            Vector3? unreachable = null;
            float bestReach = float.MaxValue;
            float bestUnreach = float.MaxValue;
            for (int i = 0; i < 12; i++)
            {
                float angle = i * 30f * Mathf.Deg2Rad;
                float radius = i % 2 == 0 ? 1.4f : 2.4f;
                Vector3 probe = center + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 1.2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - center.y) > 2.2f)
                {
                    continue;
                }

                float dist = Vector3.Distance(hit.position, center);
                if (Reachable(hit.position))
                {
                    if (dist < bestReach)
                    {
                        bestReach = dist;
                        reachable = hit.position;
                    }
                }
                else if (dist < bestUnreach)
                {
                    bestUnreach = dist;
                    unreachable = hit.position;
                }
            }

            if (reachable.HasValue)
            {
                spot.Outside = ToArray(reachable.Value);
            }

            if (reachable.HasValue && unreachable.HasValue)
            {
                // Step a little deeper into the room so the objective is clearly inside
                Vector3 inward = (unreachable.Value - reachable.Value).normalized;
                Vector3 deeper = unreachable.Value + inward * 2f;
                Vector3 interior = NavMesh.SamplePosition(deeper, out NavMeshHit deepHit, 1.5f, NavMesh.AllAreas) && !Reachable(deepHit.position)
                    ? deepHit.position
                    : unreachable.Value;
                spot.Interior = ToArray(interior);
                spot.RoomValue = CountLootNear(interior, 7f);
            }
        }

        private static int CountLootNear(Vector3 pos, float radius)
        {
            int count = 0;
            float sq = radius * radius;
            foreach (LootableContainer c in UnityEngine.Object.FindObjectsOfType<LootableContainer>())
            {
                if (c != null && (c.transform.position - pos).sqrMagnitude < sq)
                {
                    count += 2;
                }
            }

            foreach (LootItem item in LooseLoot())
            {
                if ((item.transform.position - pos).sqrMagnitude < sq)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Candidate points: every loot container, the loose loot of this raid, every loose-loot spawn point of the map from the SPT
        /// database, plus open ground: one point per ~40 m cell and per floor of walkable navmesh.
        /// Only points reachable from the main walkable area are kept (no roofs, no locked rooms).
        /// </summary>
        private IEnumerator BuildPois()
        {
            Stopwatch total = Stopwatch.StartNew();
            List<Poi> candidates = new List<Poi>();
            Dictionary<PoiKind, int> candidatesByKind = new Dictionary<PoiKind, int>();

            foreach (LootableContainer c in UnityEngine.Object.FindObjectsOfType<LootableContainer>())
            {
                if (c == null)
                {
                    continue;
                }

                float w = ContainerWeight(c);
                AddDensity(c.transform.position, w);
                if (c.DoorState != EDoorState.Locked)
                {
                    candidates.Add(Poi.At(c.transform.position, PoiKind.Container, w));
                }
            }

            foreach (LootItem item in LooseLoot())
            {
                float w = 1f + Math.Min(3f, Tactics.LootBrain.Price(item.Item) / 30000f);
                AddDensity(item.transform.position, w);
                candidates.Add(Poi.At(item.transform.position, PoiKind.LooseLoot, w));
            }

            // Loose loot of one raid is random; the database knows every spot where loot can spawn, on every floor and in every room
            foreach ((Vector3 p, float probability) in DatabaseLootPoints())
            {
                candidates.Add(Poi.At(p, PoiKind.LootSpot, 0.5f + 0.5f * Mathf.Clamp01(probability * 4f)));
            }

            yield return null;

            // Open ground: per cell and per floor (3 m height bands) keep the largest navmesh triangles; the biggest is the
            // point, the next ones replace it when it turns out to be unreachable (a sealed room, a roof)
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            const float cell = 40f;
            const float band = 3f;
            const int keep = 4;
            Dictionary<(int, int, int), List<(Vector3 pos, float area)>> cells = new Dictionary<(int, int, int), List<(Vector3, float)>>();
            for (int i = 0; i + 2 < tri.indices.Length; i += 3)
            {
                Vector3 a = tri.vertices[tri.indices[i]];
                Vector3 b = tri.vertices[tri.indices[i + 1]];
                Vector3 c = tri.vertices[tri.indices[i + 2]];
                float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                if (area < 2f)
                {
                    continue;
                }

                Vector3 centroid = (a + b + c) / 3f;
                var key = (Mathf.FloorToInt(centroid.x / cell), Mathf.FloorToInt(centroid.z / cell), Mathf.FloorToInt(centroid.y / band));
                if (!cells.TryGetValue(key, out var best))
                {
                    cells[key] = best = new List<(Vector3, float)>(keep + 1);
                }

                if (best.Count < keep || area > best[best.Count - 1].area)
                {
                    best.Add((centroid, area));
                    best.Sort((x, y) => y.area.CompareTo(x.area));
                    if (best.Count > keep)
                    {
                        best.RemoveAt(keep);
                    }
                }

                if (i % 3000 == 0)
                {
                    yield return null;
                }
            }

            foreach (var entry in cells.Values)
            {
                Poi open = Poi.At(entry[0].pos, PoiKind.Open, 0.2f);
                open.Alternates = entry.Skip(1).Select(e => e.pos).ToList();
                candidates.Add(open);
            }

            foreach (Poi candidate in candidates)
            {
                candidatesByKind[candidate.Kind] = (candidatesByKind.TryGetValue(candidate.Kind, out int n) ? n : 0) + 1;
            }

            // Snap and dedupe: real containers and this raid's loot win over database spots, loot wins over open ground.
            // Points on another floor never hide each other.
            candidates.Sort((x, y) => Priority(x.Kind).CompareTo(Priority(y.Kind)));
            Dictionary<long, List<Vector3>> taken = new Dictionary<long, List<Vector3>>();
            List<Poi> pending = new List<Poi>();
            foreach (Poi candidate in candidates)
            {
                if (!NavMesh.SamplePosition(candidate.Pos, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                {
                    continue;
                }

                Vector3 p = hit.position;
                if (TooClose(taken, p, candidate.Kind == PoiKind.Open ? 15f : 6f))
                {
                    continue;
                }

                Take(taken, p);
                Poi poi = Poi.At(p, candidate.Kind, candidate.W);
                poi.Alternates = candidate.Alternates;
                pending.Add(poi);
            }

            yield return null;

            // Grow the reachable set outward from the spawn points: each point is checked against nearby
            // already-reachable points, so every path query stays short. Points with no anchor nearby wait for a later pass.
            int checkedCount = 0, replaced = 0, openLost = 0;
            for (int pass = 0; pass < 8 && pending.Count > 0; pass++)
            {
                List<Poi> deferred = new List<Poi>();
                int found = 0;
                foreach (Poi poi in pending.OrderBy(pt => NearestAnchorDistance(pt.Pos)).ToList())
                {
                    if (NearestAnchorDistance(poi.Pos) > AnchorReach)
                    {
                        deferred.Add(poi);
                        continue;
                    }

                    checkedCount++;
                    Poi accepted = Reachable(poi.Pos) ? poi : null;

                    // Open ground that cannot be reached: try the other big triangles of the same cell and floor
                    for (int i = 0; accepted == null && poi.Alternates != null && i < poi.Alternates.Count; i++)
                    {
                        if (!NavMesh.SamplePosition(poi.Alternates[i], out NavMeshHit hit, 2f, NavMesh.AllAreas) || TooClose(Pois, hit.position, 15f))
                        {
                            continue;
                        }

                        checkedCount++;
                        if (Reachable(hit.position))
                        {
                            accepted = Poi.At(hit.position, PoiKind.Open, poi.W);
                            replaced++;
                        }
                    }

                    if (accepted != null)
                    {
                        Pois.Add(accepted);
                        AddAnchor(accepted.Pos);
                        found++;
                    }
                    else if (poi.Kind != PoiKind.Open)
                    {
                        _unreachable.Add(poi);
                    }
                    else
                    {
                        openLost++;
                    }

                    yield return null;
                }

                pending = deferred;
                if (found == 0)
                {
                    break;
                }
            }

            _unreachable.AddRange(pending.Where(pt => pt.Kind != PoiKind.Open));
            openLost += pending.Count(pt => pt.Kind == PoiKind.Open);
            PoisReady = true;
            string Kinds(Func<PoiKind, int> count) => string.Join(", ", ((PoiKind[])Enum.GetValues(typeof(PoiKind))).Select(k => $"{k} {count(k)}"));
            Log.Info($"map intel {LocationId}: {Pois.Count} reachable POIs ({Kinds(k => Pois.Count(pt => pt.Kind == k))}) from {candidates.Count} candidates " +
                     $"({Kinds(k => candidatesByKind.TryGetValue(k, out int n) ? n : 0)}); {checkedCount} path checks, " +
                     $"{replaced} open points replaced by another spot of their cell, {openLost} open points unreachable; " +
                     $"{Pois.Select(pt => Mathf.FloorToInt(pt.Y / 3f)).Distinct().Count()} height levels; {total.ElapsedMilliseconds} ms, cached");
        }

        /// <summary>Dedupe order: what is surely there first, open ground last.</summary>
        private static int Priority(PoiKind kind)
        {
            switch (kind)
            {
                case PoiKind.Container: return 0;
                case PoiKind.LooseLoot: return 1;
                case PoiKind.LootSpot: return 2;
                default: return 3;
            }
        }

        /// <summary>Every loose-loot spawn point of this map, from the LiveRaid server (it reads the SPT database). Empty when the server has no answer.</summary>
        private List<(Vector3, float)> DatabaseLootPoints()
        {
            List<(Vector3, float)> points = new List<(Vector3, float)>();
            if (!Settings.DatabaseLootSpots.Value)
            {
                return points;
            }

            try
            {
                string json = SPT.Common.Http.RequestHandler.GetJson("/liveraid/map/lootpoints/" + LocationId);
                JArray array = string.IsNullOrEmpty(json) ? null : JObject.Parse(json)["points"] as JArray;
                foreach (JToken t in array ?? new JArray())
                {
                    if (t is JArray v && v.Count >= 3)
                    {
                        points.Add((new Vector3((float)v[0], (float)v[1], (float)v[2]), v.Count > 3 ? (float)v[3] : 1f));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"map intel {LocationId}: no loot spawn points from the server ({e.Message}); only this raid's loot is used");
            }

            return points;
        }

        private static void Take(Dictionary<long, List<Vector3>> taken, Vector3 p)
        {
            long key = CellKey(p, 20f);
            if (!taken.TryGetValue(key, out List<Vector3> list))
            {
                taken[key] = list = new List<Vector3>();
            }

            list.Add(p);
        }

        /// <summary>Another point within minDist on the same floor (points a floor above or below do not count).</summary>
        private static bool TooClose(Dictionary<long, List<Vector3>> taken, Vector3 p, float minDist)
        {
            float sq = minDist * minDist;
            int cx = Mathf.FloorToInt(p.x / 20f);
            int cz = Mathf.FloorToInt(p.z / 20f);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (taken.TryGetValue(key, out List<Vector3> list) && list.Any(q => SameFloorClose(q, p, sq)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TooClose(List<Poi> pois, Vector3 p, float minDist)
        {
            float sq = minDist * minDist;
            return pois.Any(q => q.Kind == PoiKind.Open && SameFloorClose(q.Pos, p, sq));
        }

        private static bool SameFloorClose(Vector3 q, Vector3 p, float sq) =>
            Mathf.Abs(q.y - p.y) < FloorGap && (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z) < sq;

        /// <summary>
        /// Loot spots bots cannot walk to. Those next to the room behind a locked door are fine (they need a key); the rest are worth a look:
        /// closed-off rooms, roofs, spots inside geometry. Written to unreachable/&lt;map&gt;.csv so they can be checked in the editor/game.
        /// </summary>
        private void ReachabilityReport()
        {
            if (_unreachable.Count == 0)
            {
                Log.Info($"access on {LocationId}: every loot spot is reachable on foot");
                return;
            }

            int behindLock = 0;
            List<string> rows = new List<string> { "kind,weight,x,y,z,reason" };
            foreach (Poi poi in _unreachable)
            {
                bool nearLockedRoom = _allSpots.Any(sp => sp.InteriorPos.HasValue && (sp.InteriorPos.Value - poi.Pos).sqrMagnitude < 9f * 9f
                                                          || (sp.IsContainer && sp.Object != null && (sp.Object.transform.position - poi.Pos).sqrMagnitude < 2.5f * 2.5f));
                if (nearLockedRoom)
                {
                    behindLock++;
                }

                rows.Add($"{poi.Kind},{poi.W:0.0},{poi.X:0.0},{poi.Y:0.0},{poi.Z:0.0},{(nearLockedRoom ? "locked room (needs a key)" : "no path found")}");
            }

            try
            {
                string dir = Path.Combine(Plugin.PluginDir, "unreachable");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, LocationId + ".csv"), rows);
            }
            catch (Exception e)
            {
                Log.Warn("could not write the unreachable list: " + e.Message);
            }

            Log.Info($"access on {LocationId}: {Pois.Count(pt => pt.Kind != PoiKind.Open)} loot spots reachable, {_unreachable.Count} not: " +
                     $"{behindLock} behind locked doors/containers, {_unreachable.Count - behindLock} with no path found (see unreachable/{LocationId}.csv)");
        }

        private static IEnumerable<LootItem> LooseLoot()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world?.LootItems == null)
            {
                yield break;
            }

            foreach (LootItem item in world.LootItems.GetValuesEnumerator())
            {
                if (item != null && !(item is Corpse) && item.Item != null)
                {
                    yield return item;
                }
            }
        }

        public static string DensityKey(int cx, int cz) => cx + "," + cz;

        private void AddDensity(Vector3 p, float w)
        {
            string key = DensityKey(Mathf.FloorToInt(p.x / DensityCell), Mathf.FloorToInt(p.z / DensityCell));
            Density[key] = (Density.TryGetValue(key, out float v) ? v : 0f) + w;
        }

        /// <summary>Loot weight in the square around a point (3x3 density cells).</summary>
        public float LootDensityAround(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / DensityCell);
            int cz = Mathf.FloorToInt(p.z / DensityCell);
            float sum = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (Density.TryGetValue(DensityKey(cx + dx, cz + dz), out float v))
                    {
                        sum += v;
                    }
                }
            }

            return sum;
        }

        /// <summary>What a player expects to find in a container type: safes and weapon crates draw people, jackets less so.</summary>
        private static float ContainerWeight(LootableContainer c)
        {
            string name;
            try
            {
                name = (c.ItemOwner?.RootItem?.Template?._name ?? c.name ?? "").ToLowerInvariant();
            }
            catch
            {
                name = (c.name ?? "").ToLowerInvariant();
            }

            if (name.Contains("safe")) return 6f;
            if (name.Contains("weaponcrate") || name.Contains("wbox") || name.Contains("crate_weapon") || name.Contains("boss_container")) return 4f;
            if (name.Contains("cache")) return 3f;
            if (name.Contains("pcblock")) return 3f;
            if (name.Contains("med")) return 2.5f;
            if (name.Contains("grenade")) return 2.5f;
            if (name.Contains("ammo")) return 2f;
            if (name.Contains("tool")) return 2f;
            if (name.Contains("cashregister")) return 2f;
            if (name.Contains("sportbag") || name.Contains("suitcase")) return 2f;
            if (name.Contains("drawer")) return 1.5f;
            if (name.Contains("dead")) return 1.5f;
            return 1f;
        }

        private static long CellKey(Vector3 p, float size)
        {
            int cx = Mathf.FloorToInt(p.x / size);
            int cz = Mathf.FloorToInt(p.z / size);
            return ((long)cx << 32) ^ (uint)cz;
        }

        private static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

        private MapIntelCache LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                {
                    return null;
                }

                MapIntelCache cache = JsonConvert.DeserializeObject<MapIntelCache>(File.ReadAllText(CachePath));
                return cache != null && cache.Version == CacheVersion && cache.LocationId == LocationId ? cache : null;
            }
            catch (Exception e)
            {
                Log.Warn($"map cache for {LocationId} unreadable, rebuilding: {e.Message}");
                return null;
            }
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                MapIntelCache cache = new MapIntelCache
                {
                    LocationId = LocationId,
                    Spots = _allSpots,
                    Pois = Pois,
                    Density = Density,
                    RoutesKey = Routes.Key,
                };
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(cache, Formatting.None));
            }
            catch (Exception e)
            {
                Log.Warn($"could not save map cache: {e.Message}");
            }
        }
    }
}
