using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using Newtonsoft.Json.Linq;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Key places (F12 -> 3 "Key places draw a third of the PMCs"): the dorms on Customs, the bunkers on Reserve, the resort on Shoreline - the few
    /// spots online players run for. Before, they were only "hot" regions of 140 m among the others, and a region quest took bots to a few points
    /// somewhere in the region: PMCs went into the dorms no more often than vanilla bots.
    ///
    ///  - Each raid the richest places of the map are found from its loot (40 m cells over all floors, neighbours merged): 3-6 places by map size.
    ///    Each gets its own quest "LR Hot x_z" whose objectives are the rich spots INSIDE it, on every floor.
    ///  - About a third of the PMC squads (looters and rushers more often, campers and snipers less) make one of them their goal, spread evenly
    ///    over the places. An EFT quest of the squad near a place makes it far more likely (x2.5) and picks that place.
    ///  - The goal pulls hard (x5) until the leader has spent 40 s inside or 15 minutes passed; then the squad roams as usual.
    ///    Squads without the goal go there a little less (x0.6) than to other places, so the spots are wanted but not crowded.
    /// </summary>
    public static class KeyPlaces
    {
        public const string Prefix = "LR Hot ";
        private const float Cell = 40f;
        private const float PlaceRadius = 35f;
        private const float GoalSeconds = 900f;
        private const float ArriveSeconds = 40f;

        public class Place
        {
            public int Index;
            public string Name;
            public Vector3 Center;
            public float Weight;
            public int Squads;
            public int Reached;
        }

        private class Goal
        {
            public int Place;
            public float Until;
            public float InsideSince = -1f;
            public bool ByQuest;
        }

        private static readonly List<Place> _places = new List<Place>();
        private static readonly Dictionary<string, Place> _byName = new Dictionary<string, Place>();
        private static readonly Dictionary<string, Goal> _goals = new Dictionary<string, Goal>();
        private static readonly HashSet<string> _decided = new HashSet<string>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static IReadOnlyList<Place> Places => _places;

        public static string Summary =>
            _places.Count == 0 ? "key places: none" :
                "key places: " + string.Join(", ", _places.Select(p => $"{p.Name} ({p.Squads} squads aimed, {p.Reached} got in)")) +
                $" - {_decided.Count(id => _goals.ContainsKey(id) || _reachedOrExpired.Contains(id))} of {_decided.Count} squads/solos chose one";

        private static readonly HashSet<string> _reachedOrExpired = new HashSet<string>();

        /// <summary>Raid start: the squads' goals go (the places themselves are rebuilt with the quests, which may happen before or after this).</summary>
        public static void Clear()
        {
            _goals.Clear();
            _decided.Clear();
            _reachedOrExpired.Clear();
            _next = 0f;
        }

        /// <summary>Raid end: everything.</summary>
        public static void ClearAll()
        {
            Clear();
            _places.Clear();
            _byName.Clear();
        }

        /// <summary>Finds the places from the map's points of interest and returns their quests (QuestFactory).</summary>
        public static IEnumerable<JObject> BuildQuests(MapIntel intel, Func<Poi, float> lootWeight, Func<Poi, bool> isLoot, Func<Vector3, JObject> step)
        {
            _places.Clear();
            _byName.Clear();
            _goals.Clear();
            if (!Settings.KeyPlaces.Value)
            {
                yield break;
            }

            List<Poi> loot = intel.Pois.Where(p => lootWeight(p) > 0f).ToList();
            if (loot.Count < 10)
            {
                yield break;
            }

            // Locked rooms count too: their loot is not in the raid's loot list until opened, yet players run there for them (raid 2026-10-31 05:46:
            // the dorms of Customs, full of locked rooms, were only the third place, and nobody got there)
            Dictionary<(int, int), float> rooms = new Dictionary<(int, int), float>();
            foreach (LockedSpot spot in intel.SpotsById.Values)
            {
                Vector3? inside = spot.InteriorPos ?? spot.OutsidePos;
                if (inside != null && spot.RoomValue > 0)
                {
                    var key = (Mathf.FloorToInt(inside.Value.x / Cell), Mathf.FloorToInt(inside.Value.z / Cell));
                    rooms[key] = (rooms.TryGetValue(key, out float v) ? v : 0f) + 0.5f * spot.RoomValue;
                }
            }

            var cells = loot.GroupBy(p => (Mathf.FloorToInt(p.X / Cell), Mathf.FloorToInt(p.Z / Cell)))
                .Select(g => (key: g.Key, weight: g.Sum(lootWeight) + (rooms.TryGetValue(g.Key, out float extra) ? extra : 0f), center: Weighted(g.ToList(), lootWeight)))
                .OrderByDescending(c => c.weight)
                .ToList();
            int want = Mathf.Clamp(Mathf.RoundToInt(cells.Count / 18f), 3, 6);
            float median = cells[cells.Count / 2].weight;

            // The richest cells, a neighbour of an already chosen one joins it (one building over two cells is one place)
            List<(Vector3 center, float weight)> chosen = new List<(Vector3, float)>();
            foreach (var c in cells)
            {
                if (chosen.Count >= want || c.weight < median * 2.5f)
                {
                    break;
                }

                int near = chosen.FindIndex(x => FlatDistance(x.center, c.center) < Cell * 1.6f);
                if (near >= 0)
                {
                    continue;
                }

                chosen.Add((c.center, c.weight));
            }

            for (int i = 0; i < chosen.Count; i++)
            {
                Vector3 center = chosen[i].center;
                List<Poi> inside = loot.Where(p => FlatDistance(p.Pos, center) <= PlaceRadius && Mathf.Abs(p.Y - center.y) < 14f)
                    .OrderByDescending(p => lootWeight(p) + _rng.NextDouble() * 0.5)
                    .ToList();
                List<Poi> spots = Spread(inside, 14);
                if (spots.Count < 3)
                {
                    continue;
                }

                Place place = new Place
                {
                    Index = _places.Count,
                    Name = $"{Prefix}{Mathf.RoundToInt(center.x)}_{Mathf.RoundToInt(center.z)}",
                    Center = center,
                    Weight = chosen[i].weight,
                };
                _places.Add(place);
                _byName[place.Name] = place;

                JArray objectives = new JArray();
                foreach (Poi poi in spots)
                {
                    JObject s = step(poi.Pos);
                    s["waitTimeAfterCompleting"] = 2 + Math.Min(12f, poi.W * 2f);
                    objectives.Add(new JObject
                    {
                        ["repeatable"] = false,
                        ["minDistanceFromBot"] = 4,
                        ["maxDistanceFromBot"] = 9999,
                        ["maxRunDistance"] = 6,
                        ["lootAfterCompleting"] = isLoot(poi) ? "Force" : "Default",
                        ["steps"] = new JArray { s },
                    });
                }

                yield return new JObject
                {
                    ["name"] = place.Name,
                    ["repeatable"] = true,
                    ["pmcsOnly"] = false,
                    ["isCamping"] = false,
                    ["isSniping"] = false,
                    ["minLevel"] = 0,
                    ["maxLevel"] = 99,
                    ["maxBots"] = 6,
                    ["maxBotsInGroup"] = 5,
                    ["desirability"] = Math.Min(60f, Settings.RoamDesirability.Value * 1.4f),
                    ["canRunBetweenObjectives"] = true,
                    ["maxTimeOnQuest"] = 480,
                    ["objectives"] = objectives,
                };
            }

            if (_places.Count > 0)
            {
                Log.Info($"LR Route: key places on {intel.LocationId}: " +
                         string.Join(", ", _places.Select(p => $"{p.Name} at ({p.Center.x:0}, {p.Center.y:0}, {p.Center.z:0}) w{p.Weight:0}")));
            }
        }

        public static bool IsKeyPlaceQuest(string name) => name != null && name.StartsWith(Prefix);

        public static bool TryGet(string questName, out Place place)
        {
            place = null;
            return questName != null && _byName.TryGetValue(questName, out place);
        }

        /// <summary>Desirability factor of a quest for a squad leader / solo PMC (QbDesirabilityPatch).</summary>
        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!Settings.KeyPlaces.Value || _places.Count == 0 || bot == null || p == null || p.Kind != BotKind.Pmc)
            {
                return 1f;
            }

            // Followers go where the leader goes
            if (Tactics.TacticalTasks.SquadLeaderOf(bot) != null)
            {
                return 1f;
            }

            Goal goal = GoalOf(bot, p);
            string name = quest.GetName() ?? "";
            if (TryGet(name, out Place place))
            {
                if (goal == null)
                {
                    return 0.6f;
                }

                return place.Index == goal.Place ? 5f : 0.5f;
            }

            if (goal == null)
            {
                return 1f;
            }

            // On its way to the goal: an EFT quest right there is welcome, the rest of the map can wait a little
            Vector3? at = quest.GetValidObjectives().FirstOrDefault()?.GetFirstStepPosition();
            if (quest.IsEFTQuest && at.HasValue && FlatDistance(at.Value, _places[goal.Place].Center) < 70f)
            {
                return 2f;
            }

            return 0.7f;
        }

        private static Goal GoalOf(BotOwner bot, BotProfile p)
        {
            string id = bot.ProfileId;
            if (_goals.TryGetValue(id, out Goal g))
            {
                return Time.time < g.Until ? g : null;
            }

            if (!_decided.Add(id))
            {
                return null;
            }

            // Decided once per squad leader / solo, in the first 25 minutes of its life on the map
            if (QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds() > 1500f)
            {
                return null;
            }

            List<BotOwner> squad = new List<BotOwner> { bot };
            if (bot.Boss?.Followers != null)
            {
                squad.AddRange(bot.Boss.Followers.Where(f => f != null && !f.IsDead));
            }

            int questPlace = QuestPlace(squad);
            float chance = BaseChance(p);
            if (p.HotDrop)
            {
                chance *= 1.6f;
            }

            if (questPlace >= 0)
            {
                chance = Mathf.Min(0.9f, chance * 2.5f);
            }

            if (_rng.NextDouble() > chance)
            {
                Log.Detail("route", $"{p.Name} ({p.Style}) has no key place in mind ({chance:P0})");
                return null;
            }

            int placeIndex = questPlace >= 0
                ? questPlace
                : _places.OrderBy(pl => pl.Squads).ThenBy(_ => _rng.Next()).First().Index;
            Place place = _places[placeIndex];
            place.Squads++;
            g = new Goal { Place = placeIndex, Until = Time.time + GoalSeconds, ByQuest = questPlace >= 0 };
            _goals[id] = g;
            Log.Info($"LR Route: {p.Name} ({p.Style}{(squad.Count > 1 ? $", squad of {squad.Count}" : "")}) heads for key place {place.Name}" +
                     $"{(g.ByQuest ? " - a quest of the squad is there" : "")} ({chance:P0})");
            return g;
        }

        /// <summary>About one squad in three overall.</summary>
        private static float BaseChance(BotProfile p)
        {
            switch (p.Style)
            {
                case PlayStyle.Looter: return 0.5f;
                case PlayStyle.Rusher: return 0.45f;
                case PlayStyle.Hunter: return 0.4f;
                case PlayStyle.Explorer: return 0.32f;
                case PlayStyle.Quester: return 0.28f;
                case PlayStyle.Rat: return 0.2f;
                case PlayStyle.Sniper: return 0.12f;
                default: return 0.15f;
            }
        }

        /// <summary>The key place an EFT quest of the squad points to (an objective within 70 m), or -1.</summary>
        private static int QuestPlace(List<BotOwner> squad)
        {
            List<BotQuest> quests;
            try
            {
                quests = AccessTools.Field(typeof(BotJobAssignmentController), "allQuests")?.GetValue(null) as List<BotQuest>;
            }
            catch
            {
                return -1;
            }

            if (quests == null)
            {
                return -1;
            }

            foreach (BotQuest quest in quests)
            {
                if (quest == null || !quest.IsEFTQuest)
                {
                    continue;
                }

                bool forSquad = false;
                foreach (BotOwner m in squad)
                {
                    try
                    {
                        if (quest.CanAssignBot(m))
                        {
                            forSquad = true;
                            break;
                        }
                    }
                    catch
                    {
                        // a quest QuestingBots cannot judge for this bot
                    }
                }

                if (!forSquad)
                {
                    continue;
                }

                foreach (BotQuestObjective objective in quest.AllObjectives)
                {
                    Vector3? at = objective.StepCount > 0 ? objective.GetFirstStepPosition() : null;
                    if (!at.HasValue)
                    {
                        continue;
                    }

                    foreach (Place place in _places)
                    {
                        if (FlatDistance(at.Value, place.Center) < 70f)
                        {
                            return place.Index;
                        }
                    }
                }
            }

            return -1;
        }

        /// <summary>Every 2 s: a leader that has been inside its key place for a while (or ran out of time) is done with it.</summary>
        public static void Tick()
        {
            if (_goals.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            List<string> done = null;
            foreach (KeyValuePair<string, Goal> kv in _goals)
            {
                Goal g = kv.Value;
                Player player = world?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == kv.Key);
                if (player == null || Time.time > g.Until)
                {
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    continue;
                }

                Place place = _places[g.Place];
                bool inside = FlatDistance(player.Position, place.Center) < PlaceRadius && Mathf.Abs(player.Position.y - place.Center.y) < 14f;
                if (!inside)
                {
                    g.InsideSince = -1f;
                    continue;
                }

                if (g.InsideSince < 0f)
                {
                    g.InsideSince = Time.time;
                }
                else if (Time.time - g.InsideSince > ArriveSeconds)
                {
                    place.Reached++;
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    Log.Info($"LR Route: {player.Profile.Nickname} got into key place {place.Name}");
                }
            }

            if (done != null)
            {
                foreach (string id in done)
                {
                    _goals.Remove(id);
                    _reachedOrExpired.Add(id);
                }
            }
        }

        private static Vector3 Weighted(List<Poi> pois, Func<Poi, float> weight)
        {
            Vector3 sum = Vector3.zero;
            float total = 0f;
            foreach (Poi p in pois)
            {
                float w = Mathf.Max(0.01f, weight(p));
                sum += p.Pos * w;
                total += w;
            }

            return total > 0f ? sum / total : pois[0].Pos;
        }

        /// <summary>Up to <paramref name="max"/> spots spread over the place and its floors, richest first.</summary>
        private static List<Poi> Spread(List<Poi> pois, int max)
        {
            List<Poi> result = new List<Poi>();
            foreach (Poi p in pois)
            {
                if (result.Count >= max)
                {
                    break;
                }

                bool crowded = result.Any(r => FlatDistance(r.Pos, p.Pos) < 4f && Mathf.Abs(r.Y - p.Y) < 2.2f);
                if (!crowded)
                {
                    result.Add(p);
                }
            }

            return result;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
