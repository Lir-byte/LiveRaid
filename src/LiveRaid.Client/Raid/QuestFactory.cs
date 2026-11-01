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

        /// <summary>Quest name -> how loot-rich the region is compared with the rest of the map (0 = empty, 1 = the richest).</summary>
        public static readonly Dictionary<string, float> Heat = new Dictionary<string, float>();

        public static List<BotQuest> Build(MapIntel intel)
        {
            JArray quests = new JArray();
            // A new pick every raid: a region has more spots than one quest takes, so over several raids bots get to all of them
            System.Random rng = new System.Random();
            Heat.Clear();

            if (Settings.MapWideQuests.Value)
            {
                foreach (JObject q in ExploreQuests(intel, rng))
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

            string[] parts = questName.Substring(ExplorePrefix.Length).Trim().Split('_');
            return parts.Length == 2 && int.TryParse(parts[0], out rx) && int.TryParse(parts[1], out rz);
        }

        private static IEnumerable<JObject> ExploreQuests(MapIntel intel, System.Random rng)
        {
            float size = Settings.RoamRegionSize.Value;

            // Places the player found that the bots' map did not have (PlayerTrail) join the regions they lie in
            List<Poi> learned = PlayerTrail.PlacesFor(intel.LocationId);
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

                int maxObjectives = 8 + Mathf.RoundToInt(12f * heat);
                List<Poi> chosen = PickSpread(region.ToList(), maxObjectives, rng);
                if (chosen.Count == 0)
                {
                    continue;
                }

                string name = $"{ExplorePrefix} {region.Key.Item1}_{region.Key.Item2}";
                Heat[name] = heat;
                if (heat >= 0.85f)
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
                    ["maxBots"] = heat > 0.8f ? 5 : 3,
                    ["maxBotsInGroup"] = 5,
                    ["desirability"] = Math.Min(70f, Settings.RoamDesirability.Value * (0.6f + 0.9f * heat) + Math.Min(10, lootSpots) * 0.5f),
                    ["canRunBetweenObjectives"] = true,
                    ["maxTimeOnQuest"] = 300 + 300 * heat,
                    ["objectives"] = objectives,
                };
            }

            Log.Info($"hot zones on {intel.LocationId}: {(hottest.Count > 0 ? string.Join(", ", hottest) : "none")} (region = {size:0} m)");
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
