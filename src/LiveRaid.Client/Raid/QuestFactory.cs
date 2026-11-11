using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Turns map intel into QuestingBots quests:
    ///  - "LR Explore": one quest per region of the map, its objectives are loot spots and open ground in that region.
    ///    Loot-rich regions ("hot zones": industrial areas, dorms, malls) get more objectives, more time per spot and a higher pull;
    ///  - "LR Locked": one quest per locked room, available only to bots that carry its key.
    /// QuestingBots then snaps, validates and schedules them like its own quests.
    /// </summary>
    public static class QuestFactory
    {
        public const string ExplorePrefix = "LR Explore";
        public const string LockedPrefix = "LR Locked ";
        public const string PathPrefix = "LR Path ";

        /// <summary>The atlas' sweep quests ("LR Explore S3"): the places the last raids missed, gathered building by building (<see cref="Atlas"/>).</summary>
        public const string SweepPrefix = "LR Explore S";

        /// <summary>Quest name -> how loot-rich the region is compared with the rest of the map (0 = empty, 1 = the richest).</summary>
        public static readonly Dictionary<string, float> Heat = new Dictionary<string, float>();

        /// <summary>Quest name -> the spots its objectives send a bot to (CoverageLedger weighs the quest by how many of them nobody has visited).</summary>
        public static readonly Dictionary<string, List<Poi>> QuestPois = new Dictionary<string, List<Poi>>();

        public static List<BotQuest> Build(MapIntel intel)
        {
            JArray quests = new JArray();
            // A new pick every raid: a region has more spots than one quest takes, so over several raids bots get to all of them
            System.Random rng = new System.Random();
            Heat.Clear();
            QuestPois.Clear();

            if (Settings.MapWideQuests.Value)
            {
                foreach (JObject q in ExploreQuests(intel, rng))
                {
                    quests.Add(q);
                }
            }

            if (Settings.MapWideQuests.Value && Settings.AtlasMemory.Value)
            {
                foreach (JObject q in SweepQuests(intel))
                {
                    quests.Add(q);
                }
            }

            if (Settings.MapWideQuests.Value)
            {
                foreach (JObject q in KeyPlaces.BuildQuests(intel, LootWeight, IsLoot, Step))
                {
                    quests.Add(q);
                }
            }

            if (Settings.RealKeys.Value)
            {
                foreach (JObject q in LockedRoomQuests(intel))
                {
                    quests.Add(q);
                }
            }

            // Walks drawn in LiveRaid Studio
            if (Settings.MapEdits.Value)
            {
                foreach (JObject q in EditedPathQuests(intel))
                {
                    quests.Add(q);
                }
            }

            BotQuest[] result = JsonConvert.DeserializeObject<BotQuest[]>(quests.ToString(Formatting.None), EFT.EftJsonConverters.SerializerSettings);
            return result?.ToList() ?? new List<BotQuest>();
        }

        public static bool TryParseRegion(string questName, out int rx, out int rz)
        {
            rx = rz = 0;
            if (questName == null || !questName.StartsWith(ExplorePrefix))
            {
                return false;
            }

            // "LR Explore 3_-1" or, for the second part of a large region, "LR Explore 3_-1#2"
            string tail = questName.Substring(ExplorePrefix.Length).Trim();
            int hash = tail.IndexOf('#');
            if (hash >= 0)
            {
                tail = tail.Substring(0, hash);
            }

            string[] parts = tail.Split('_');
            return parts.Length == 2 && int.TryParse(parts[0], out rx) && int.TryParse(parts[1], out rz);
        }

        /// <summary>The hottest distinct regions (a large region is several quests with the same heat).</summary>
        public static List<(int rx, int rz, float heat)> HottestRegions(int count)
        {
            var best = new Dictionary<(int, int), float>();
            foreach (KeyValuePair<string, float> h in Heat)
            {
                if (TryParseRegion(h.Key, out int rx, out int rz) && (!best.TryGetValue((rx, rz), out float old) || h.Value > old))
                {
                    best[(rx, rz)] = h.Value;
                }
            }

            return best.OrderByDescending(kv => kv.Value).Take(count).Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
        }

        private static IEnumerable<JObject> ExploreQuests(MapIntel intel, System.Random rng)
        {
            float size = Settings.RoamRegionSize.Value;

            // Places the player found that the bots' map did not have (PlayerTrail) join the regions they lie in
            List<Poi> learned = PlayerTrail.PlacesFor(intel.LocationId);
            if (Settings.MapEdits.Value)
            {
                // Places drawn in LiveRaid Studio join the regions they lie in, like the ones learned from the player's trail
                learned = learned.Concat(Studio.MapEdits.SpotsFor(intel.LocationId)).ToList();
            }

            if (learned.Count > 0)
            {
                Log.Info($"LR Trail: {learned.Count} learned places on {intel.LocationId} ({learned.Count(l => l.W >= 1.2f)} where you stopped) join the exploration quests");
            }

            var regions = intel.Pois.Concat(learned).GroupBy(p => (Mathf.FloorToInt(p.X / size), Mathf.FloorToInt(p.Z / size))).ToList();
            if (regions.Count == 0)
            {
                yield break;
            }

            // Heat = rank of the region's loot weight among all regions: robust against one huge outlier
            List<float> weights = regions.Select(r => r.Sum(LootWeight)).OrderBy(w => w).ToList();
            float RankOf(float w) => weights.Count <= 1 ? 0.5f : weights.FindLastIndex(x => x <= w) / (float)(weights.Count - 1);

            List<string> hottest = new List<string>();
            foreach (var region in regions)
            {
                float regionWeight = region.Sum(LootWeight);
                float heat = RankOf(regionWeight);

                // 1.2.6: with the coverage balance on, a region is split into several quests that between them cover ALL its spots (a quest used to take
                // 8-20 of them, a region of 85 spots only 37 %); the parts are told apart by "#k" in the name
                int maxObjectives = 8 + Mathf.RoundToInt(12f * heat);
                List<List<Poi>> parts = Settings.CoverageBalance.Value
                    ? SplitRegion(region.ToList(), 12 + Mathf.RoundToInt(8f * heat), rng)
                    : new List<List<Poi>> { PickSpread(region.ToList(), maxObjectives, rng) };
                int partNo = 0;
                foreach (List<Poi> chosen in parts)
                {
                    partNo++;
                    if (chosen.Count == 0)
                    {
                        continue;
                    }

                    string name = parts.Count > 1
                        ? $"{ExplorePrefix} {region.Key.Item1}_{region.Key.Item2}#{partNo}"
                        : $"{ExplorePrefix} {region.Key.Item1}_{region.Key.Item2}";
                    Heat[name] = heat;
                    QuestPois[name] = chosen;
                    if (heat >= 0.85f && partNo == 1)
                    {
                        hottest.Add($"{region.Key.Item1}_{region.Key.Item2}(w{regionWeight:0})");
                    }

                    int lootSpots = chosen.Count(IsLoot);
                    JArray objectives = new JArray();
                    foreach (Poi poi in chosen)
                    {
                        bool loot = IsLoot(poi);
                        JObject step = Step(poi.Pos);

                        // Rich spots (safes, weapon crates, dense loot) deserve a longer look
                        step["waitTimeAfterCompleting"] = loot ? 2 + Math.Min(14f, poi.W * 2.2f) : 2;
                        if (!loot)
                        {
                            // Open ground: stop for a moment and look around like a player clearing an area
                            step["stepType"] = "HoldAtPosition";
                            step["minElapsedTime"] = new JObject { ["min"] = 4, ["max"] = 14 };
                        }

                        objectives.Add(new JObject
                        {
                            ["repeatable"] = false,
                            ["minDistanceFromBot"] = 8,
                            ["maxDistanceFromBot"] = 9999,
                            ["maxRunDistance"] = 8,
                            ["lootAfterCompleting"] = loot ? "Force" : "Default",
                            ["steps"] = new JArray { step },
                        });
                    }

                    yield return new JObject
                    {
                        ["name"] = name,
                        ["repeatable"] = true,
                        ["pmcsOnly"] = false,
                        ["isCamping"] = false,
                        ["isSniping"] = false,
                        ["minLevel"] = 0,
                        ["maxLevel"] = 99,
                        ["maxBots"] = parts.Count > 1 ? (heat > 0.8f ? 3 : 2) : (heat > 0.8f ? 5 : 3),
                        ["maxBotsInGroup"] = 5,
                        ["desirability"] = Math.Min(70f, Settings.RoamDesirability.Value * (0.6f + 0.9f * heat) + Math.Min(10, lootSpots) * 0.5f),
                        ["canRunBetweenObjectives"] = true,
                        ["maxTimeOnQuest"] = 300 + 300 * heat,
                        ["objectives"] = objectives,
                    };
                }
            }

            Log.Info($"hot zones on {intel.LocationId}: {(hottest.Count > 0 ? string.Join(", ", hottest) : "none")} (region = {size:0} m)");
        }

        /// <summary>
        /// Extra quests from the atlas (1.3.0): every spot of the map missed in 2+ raids in a row goes into a cluster of up to 14 spots within 40 m of the most neglected one;
        /// the steps run floor by floor, ground first, nearest next - a "building sweep". At most 24 clusters, so a map whose atlas is still young is not flooded.
        /// </summary>
        private static IEnumerable<JObject> SweepQuests(MapIntel intel)
        {
            List<AtlasSpot> cold = Atlas.ColdSpots(intel.LocationId);
            if (cold.Count == 0)
            {
                yield break;
            }

            List<(Poi poi, int missed)> left = new List<(Poi, int)>();
            foreach (AtlasSpot c in cold)
            {
                Poi match = intel.Pois.FirstOrDefault(p => Mathf.Abs(p.Y - c.Y) <= 1.5f && Mathf.Abs(p.X - c.X) <= 2.5f && Mathf.Abs(p.Z - c.Z) <= 2.5f);
                if (match != null)
                {
                    left.Add((match, c.Missed));
                }
            }

            int clusters = 0, spots = 0;
            while (left.Count > 0 && clusters < 24)
            {
                Poi seed = left[0].poi;
                List<(Poi poi, int missed)> cluster = left.Where(x => HorizontalDistance(x.poi.Pos, seed.Pos) <= 40f).Take(14).ToList();
                foreach (var c in cluster)
                {
                    left.Remove(c);
                }

                // Floor by floor from the lowest, the nearest spot first within a floor
                List<Poi> ordered = new List<Poi>();
                Vector3 at = cluster.OrderBy(x => x.poi.Y).First().poi.Pos;
                foreach (var floor in cluster.GroupBy(x => Mathf.RoundToInt(x.poi.Y / 3f)).OrderBy(g => g.Key))
                {
                    List<Poi> rest = floor.Select(x => x.poi).ToList();
                    while (rest.Count > 0)
                    {
                        Poi next = rest.OrderBy(p => HorizontalDistance(p.Pos, at)).First();
                        rest.Remove(next);
                        ordered.Add(next);
                        at = next.Pos;
                    }
                }

                clusters++;
                spots += ordered.Count;
                string name = SweepPrefix + clusters;
                Heat[name] = 0.7f;
                QuestPois[name] = ordered;
                JArray objectives = new JArray();
                foreach (Poi poi in ordered)
                {
                    bool loot = IsLoot(poi);
                    JObject step = Step(poi.Pos);
                    step["waitTimeAfterCompleting"] = loot ? 2 + Math.Min(10f, poi.W * 2f) : 2;
                    if (!loot)
                    {
                        step["stepType"] = "HoldAtPosition";
                        step["minElapsedTime"] = new JObject { ["min"] = 3, ["max"] = 8 };
                    }

                    objectives.Add(new JObject
                    {
                        ["repeatable"] = false,
                        ["minDistanceFromBot"] = 4,
                        ["maxDistanceFromBot"] = 9999,
                        ["maxRunDistance"] = 8,
                        ["lootAfterCompleting"] = loot ? "Force" : "Default",
                        ["steps"] = new JArray { step },
                    });
                }

                yield return new JObject
                {
                    ["name"] = name,
                    ["repeatable"] = true,
                    ["pmcsOnly"] = false,
                    ["isCamping"] = false,
                    ["isSniping"] = false,
                    ["minLevel"] = 0,
                    ["maxLevel"] = 99,
                    ["maxBots"] = 2,
                    ["maxBotsInGroup"] = 5,
                    ["desirability"] = Math.Min(70f, Settings.RoamDesirability.Value * 1.2f + 10f),
                    ["canRunBetweenObjectives"] = true,
                    ["maxTimeOnQuest"] = 420,
                    ["objectives"] = objectives,
                };
            }

            Atlas.NoteSwept(spots);
            Log.Info($"LR Atlas: {clusters} sweep quests for {spots} spots that the last raids missed ({cold.Count} cold in the atlas)");
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static IEnumerable<JObject> LockedRoomQuests(MapIntel intel)
        {
            foreach (LockedSpot spot in intel.Spots)
            {
                if (spot.IsContainer || spot.InteriorPos == null || spot.OutsidePos == null || !spot.StillLocked)
                {
                    continue;
                }

                Vector3 inside = spot.InteriorPos.Value;
                JObject look = Step(inside);
                look["stepType"] = "HoldAtPosition";
                look["minElapsedTime"] = new JObject { ["min"] = 3, ["max"] = 8 };

                yield return new JObject
                {
                    ["name"] = LockedPrefix + spot.Id,
                    ["repeatable"] = false,
                    ["pmcsOnly"] = false,
                    ["minLevel"] = 0,
                    ["maxLevel"] = 99,
                    ["maxBots"] = 2,
                    ["maxBotsInGroup"] = 5,
                    ["desirability"] = 45 + Math.Min(25, spot.RoomValue * 2),
                    ["canRunBetweenObjectives"] = true,
                    ["maxTimeOnQuest"] = 300,
                    ["objectives"] = new JArray
                    {
                        new JObject
                        {
                            ["repeatable"] = false,
                            ["minDistanceFromBot"] = 3,
                            ["maxDistanceFromBot"] = 9999,
                            ["maxRunDistance"] = 6,
                            ["lootAfterCompleting"] = "Force",
                            ["doorIDToUnlock"] = spot.Id,
                            ["fixedPositionToUnlockDoor"] = Vec(spot.OutsidePos.Value),
                            ["steps"] = new JArray { Step(inside), look },
                        },
                    },
                };
            }
        }

        /// <summary>
        /// "LR Path &lt;name&gt;": a walk drawn in Studio. One objective whose steps are the points in order (QuestingBots walks the steps one after another);
        /// "pingpong" adds the way back, "loop" returns to the first point, "once" ends at the last.
        /// </summary>
        private static IEnumerable<JObject> EditedPathQuests(MapIntel intel)
        {
            foreach (Studio.BotPath path in Studio.MapEdits.PathsFor(intel.LocationId))
            {
                List<Studio.PathPoint> points = path.Points.Where(p => p.P != null && p.P.Length >= 3).ToList();
                if (points.Count < 2)
                {
                    continue;
                }

                List<Studio.PathPoint> order = new List<Studio.PathPoint>(points);
                if (path.Mode == "pingpong")
                {
                    order.AddRange(Enumerable.Reverse(points).Skip(1));
                }
                else if (path.Mode == "loop")
                {
                    order.Add(points[0]);
                }

                JArray steps = new JArray();
                foreach (Studio.PathPoint point in order)
                {
                    JObject step = Step(new Vector3(point.P[0], point.P[1], point.P[2]));
                    step["waitTimeAfterCompleting"] = Math.Max(0f, point.Wait);
                    if (point.Hold)
                    {
                        step["stepType"] = "HoldAtPosition";
                        step["minElapsedTime"] = new JObject { ["min"] = Math.Max(2f, point.Wait), ["max"] = Math.Max(4f, point.Wait * 1.5f) };
                    }

                    steps.Add(step);
                }

                yield return new JObject
                {
                    ["name"] = PathPrefix + (path.Name ?? path.Id),
                    ["repeatable"] = true,
                    ["pmcsOnly"] = path.Who == "pmc",
                    ["botRoleFilter"] = path.Who == "scav" ? "assault,cursedAssault,marksman" : "",
                    ["minLevel"] = 0,
                    ["maxLevel"] = 99,
                    ["maxBots"] = Math.Max(1, path.MaxBots),
                    ["maxBotsInGroup"] = 5,
                    ["desirability"] = Math.Max(0f, Math.Min(100f, path.Desirability)),
                    ["canRunBetweenObjectives"] = true,
                    ["maxTimeOnQuest"] = Math.Max(60f, path.MaxTime),
                    ["objectives"] = new JArray
                    {
                        new JObject
                        {
                            ["repeatable"] = false,
                            ["minDistanceFromBot"] = 0,
                            ["maxDistanceFromBot"] = 9999,
                            ["maxRunDistance"] = 12,
                            ["lootAfterCompleting"] = "Default",
                            ["steps"] = steps,
                        },
                    },
                };
            }
        }

        /// <summary>
        /// All the spots of a region with something in them, split into compact parts of about <paramref name="chunk"/> spots (bisection along the longest
        /// side, a floor counts as distance); open ground only as a sample - stopping to look round there costs time and adds little.
        /// </summary>
        private static List<List<Poi>> SplitRegion(List<Poi> pois, int chunk, System.Random rng)
        {
            List<Poi> pool = pois.Where(p => p.Kind != PoiKind.Open).ToList();
            List<Poi> open = pois.Where(p => p.Kind == PoiKind.Open).ToList();
            if (open.Count > 0)
            {
                pool.AddRange(PickSpread(open, Math.Max(2, pool.Count / 3), rng));
            }

            List<List<Poi>> result = new List<List<Poi>>();
            if (pool.Count > 0)
            {
                Bisect(pool, Math.Max(1, (int)Math.Ceiling(pool.Count / (float)Math.Max(6, chunk))), result);
            }

            return result;
        }

        private static void Bisect(List<Poi> pool, int parts, List<List<Poi>> result)
        {
            if (parts <= 1 || pool.Count <= 1)
            {
                result.Add(pool);
                return;
            }

            float spreadX = pool.Max(p => p.X) - pool.Min(p => p.X);
            float spreadZ = pool.Max(p => p.Z) - pool.Min(p => p.Z);
            float spreadY = 4f * (pool.Max(p => p.Y) - pool.Min(p => p.Y));
            List<Poi> sorted = spreadY > spreadX && spreadY > spreadZ ? pool.OrderBy(p => p.Y).ToList()
                : spreadX >= spreadZ ? pool.OrderBy(p => p.X).ToList() : pool.OrderBy(p => p.Z).ToList();
            int left = parts / 2;
            int cut = Math.Max(1, Math.Min(sorted.Count - 1, (int)Math.Round(sorted.Count * left / (float)parts)));
            Bisect(sorted.Take(cut).ToList(), left, result);
            Bisect(sorted.Skip(cut).ToList(), parts - left, result);
        }

        /// <summary>Squared distance where a floor of height counts like 12 m of walking, so floors of one building are picked as separate places.</summary>
        private static float FloorAwareSqr(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            return d.x * d.x + d.z * d.z + 16f * d.y * d.y;
        }

        /// <summary>Loot spots and places where the player stopped (likely looted) count as loot; open ground and walked-through places do not.</summary>
        private static bool IsLoot(Poi p) => p.Kind == PoiKind.Learned ? p.W >= 1.2f : p.Kind != PoiKind.Open;

        /// <summary>A region's loot weight: player stops count half (where he stood is a hint of loot, not a known spawn).</summary>
        private static float LootWeight(Poi p) => p.Kind == PoiKind.Open ? 0f : p.Kind == PoiKind.Learned ? (p.W >= 1.2f ? p.W * 0.5f : 0f) : p.W;

        /// <summary>
        /// Farthest-point sampling that prefers loot spots (and rich ones), so a region quest covers the whole region, upper floors included.
        /// Places learned from the player's trail are seeded first (up to a third of the objectives), so bots really get to them.
        /// </summary>
        private static List<Poi> PickSpread(List<Poi> pois, int max, System.Random rng)
        {
            if (pois.Count <= max)
            {
                return pois;
            }

            List<Poi> result = new List<Poi>();
            List<Poi> remaining = pois.OrderBy(_ => rng.Next()).ToList();
            // Places where the player stopped first, then the ones he walked through
            foreach (Poi l in remaining.Where(p => p.Kind == PoiKind.Learned).OrderByDescending(p => p.W + rng.NextDouble() * 0.8).Take(Math.Max(1, max / 3)).ToList())
            {
                result.Add(l);
                remaining.Remove(l);
            }

            if (result.Count == 0)
            {
                result.Add(remaining[0]);
                remaining.RemoveAt(0);
            }
            while (result.Count < max && remaining.Count > 0)
            {
                int best = 0;
                float bestScore = float.MinValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    float nearest = result.Min(r => FloorAwareSqr(r.Pos, remaining[i].Pos));
                    float score = Mathf.Sqrt(nearest) * (remaining[i].Kind == PoiKind.Open ? 0.7f : remaining[i].Kind == PoiKind.Learned ? 1.25f : 1f + 0.15f * Math.Min(6f, remaining[i].W));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }

                result.Add(remaining[best]);
                remaining.RemoveAt(best);
            }

            return result;
        }

        private static JObject Step(Vector3 pos) => new JObject
        {
            ["position"] = Vec(pos),
            ["waitTimeAfterCompleting"] = 2,
            ["maxDistance"] = 3,
        };

        private static JObject Vec(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
    }
}
