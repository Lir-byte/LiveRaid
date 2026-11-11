using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// The map's "who has been where" at the level of single spots (1.2.6). Raids 2026-11-05/07: over many raids the bots reach 92-96 % of the loot spots, but
    /// in one raid a PMC squad got to 11-38 % of them and 3-16 of 24 regions of Customs were never entered. Two reasons: a region quest used to take only 8-20 of
    /// its spots (a region of 85 spots was cut to 37 % of them), and nothing told a bot that the other side of the map had not been visited yet.
    /// Here every bot that stands within 12 m of a spot (same floor) marks it visited; a spot visited long ago counts as half fresh again after 20 minutes.
    /// The "freshness" of a quest is the weighted share of its spots that are still fresh; <see cref="Multiplier"/> makes a quest with fresh ground more
    /// wanted and a quest nobody needs to visit again less wanted (softly, each bot to its own degree) - the walk of the whole map evens out without
    /// moving anyone by force. Also the measure: the share of the spots visited and the regions never entered, in the raid summary and every 5 minutes.
    /// </summary>
    public static class CoverageLedger
    {
        private const float VisitRadius = 12f;
        private const float VisitHeight = 3.5f;
        private const float ReFreshSeconds = 1200f;
        private const float CellSize = 24f;

        private static readonly Dictionary<Poi, int> _index = new Dictionary<Poi, int>();
        private static readonly Dictionary<(int, int), List<int>> _cells = new Dictionary<(int, int), List<int>>();
        private static readonly List<Poi> _pois = new List<Poi>();
        private static float[] _last = new float[0];

        // Spot -> the quests (as lists of spot numbers) it is a step of: FreshSteps refuses a step on seen ground only while its quest has unseen ground left
        private static readonly Dictionary<int, List<int[]>> _questsOf = new Dictionary<int, List<int[]>>();

        // Atlas (1.3.0): the state of each spot at the start of the raid, and how near a bot came to it this raid (for the report of the places nobody reached)
        private static int[] _missed0 = new int[0];
        private static int[] _seen0 = new int[0];
        private static float[] _minH = new float[0];
        private static float[] _dyAtMin = new float[0];
        private static float[] _nav = new float[0];
        private static float _builtAt;
        private static bool _countedRaid;
        private static bool _built;
        private static float _next;
        private static float _nextLog;
        private static int _questCount;

        public static int Asked { get; private set; }
        public static int Favoured { get; private set; }
        public static int Held { get; private set; }
        public static int Cold { get; private set; }

        private static bool On => Settings.Enabled.Value && Settings.CoverageBalance.Value && Settings.CoverageStrength.Value > 0;

        /// <summary>
        /// The raid is over: the atlas gets the raid's last minutes. Called before MapIntel forgets the map - from Clear() the save never ran (the map's name was
        /// already gone at a raid's end, and at the next raid's start the ledger was already empty), so a spot reached after the last 5-minute save counted as missed.
        /// </summary>
        public static void EndRaid() => SaveAtlas(true);

        public static void Clear()
        {
            _index.Clear();
            _cells.Clear();
            _pois.Clear();
            _questsOf.Clear();
            _last = new float[0];
            _missed0 = _seen0 = new int[0];
            _minH = _dyAtMin = _nav = new float[0];
            _countedRaid = false;
            _built = false;
            _next = _nextLog = 0f;
            _questCount = 0;
            Asked = Favoured = Held = Cold = 0;
        }

        public static string Summary
        {
            get
            {
                if (!_built || _pois.Count == 0)
                {
                    return "coverage ledger not used";
                }

                Totals(out int visited, out int regions, out int untouched);
                // The share of TIME upstairs (VerticalLedger) says little: a floor is run through in a minute, the yard between the houses takes five.
                // What matters is whether the spots up there get visited as often as the ones below
                int up = 0, upSeen = 0;
                for (int i = 0; i < _pois.Count; i++)
                {
                    if (VerticalLedger.IsUpper(_pois[i].Pos))
                    {
                        up++;
                        if (_last[i] >= 0f)
                        {
                            upSeen++;
                        }
                    }
                }

                int down = _pois.Count - up;
                string floors = up == 0 ? "no spots upstairs" : $"upstairs {upSeen} of {up} ({100 * upSeen / up} %), below {visited - upSeen} of {down} ({(down > 0 ? 100 * (visited - upSeen) / down : 0)} %)";
                return $"coverage ledger: {visited} of {_pois.Count} spots visited by a bot ({100 * visited / _pois.Count} %; {floors}), regions never entered {untouched} of {regions}; " +
                       $"quests weighed by fresh ground {Asked} (favoured {Favoured}, held back {Held}, cold regions pulled harder {Cold})";
            }
        }

        private static bool Build()
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null || !intel.Ready || QuestFactory.QuestPois.Count == 0)
            {
                return false;
            }

            // Every spot a quest sends a bot to, plus the map's own spots
            foreach (Poi poi in intel.Pois.Concat(QuestFactory.QuestPois.Values.SelectMany(v => v)))
            {
                if (poi == null || _index.ContainsKey(poi))
                {
                    continue;
                }

                _index[poi] = _pois.Count;
                _pois.Add(poi);
                (int, int) c = CellOf(poi.X, poi.Z);
                if (!_cells.TryGetValue(c, out List<int> list))
                {
                    _cells[c] = list = new List<int>();
                }

                list.Add(_pois.Count - 1);
            }

            if (_pois.Count < 20)
            {
                _index.Clear();
                _cells.Clear();
                _pois.Clear();
                return false;
            }

            foreach (List<Poi> steps in QuestFactory.QuestPois.Values)
            {
                int[] numbers = steps.Where(s => s != null && _index.ContainsKey(s)).Select(s => _index[s]).ToArray();
                foreach (int n in numbers)
                {
                    if (!_questsOf.TryGetValue(n, out List<int[]> of))
                    {
                        _questsOf[n] = of = new List<int[]>();
                    }

                    of.Add(numbers);
                }
            }

            _last = Enumerable.Repeat(-1f, _pois.Count).ToArray();
            _minH = Enumerable.Repeat(99999f, _pois.Count).ToArray();
            _dyAtMin = new float[_pois.Count];
            _nav = Enumerable.Repeat(-2f, _pois.Count).ToArray();
            _missed0 = new int[_pois.Count];
            _seen0 = new int[_pois.Count];
            _builtAt = Time.time;
            _countedRaid = false;
            if (Settings.AtlasMemory.Value && !string.IsNullOrEmpty(intel.LocationId))
            {
                Atlas.Load(intel.LocationId);
                Atlas.RaidStarts();
                for (int k = 0; k < _pois.Count; k++)
                {
                    AtlasSpot known = Atlas.Find(_pois[k].Pos);
                    if (known != null)
                    {
                        _missed0[k] = known.Missed;
                        _seen0[k] = known.Seen;
                    }
                }
            }

            _questCount = QuestFactory.QuestPois.Count;
            _built = true;
            Log.Detail("info", $"LR Coverage: {_pois.Count} spots under watch, {_questCount} exploration quests");
            return true;
        }

        private static (int, int) CellOf(float x, float z) => (Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));

        /// <summary>Writes the atlas (see <see cref="Atlas"/>): in the middle of a raid every 5 minutes, and when it is over - but only a raid that lasted 8 minutes counts.</summary>
        private static void SaveAtlas(bool end)
        {
            if (!_built || _pois.Count == 0 || !Settings.AtlasMemory.Value || Time.time - _builtAt < 480f)
            {
                return;
            }

            string location = MapIntel.Current?.LocationId;
            if (string.IsNullOrEmpty(location))
            {
                return;
            }

            try
            {
                if (!end)
                {
                    FillNavDistances();
                }

                List<bool> visited = _last.Select(t => t >= 0f).ToList();
                HashSet<Poi> inQuests = new HashSet<Poi>(QuestFactory.QuestPois.Values.SelectMany(v => v));
                Atlas.Save(location, _pois, visited, _missed0, _seen0, _minH, _dyAtMin, _nav, inQuests, !_countedRaid);
                _countedRaid = true;
            }
            catch (Exception e)
            {
                Diag.Report("CoverageLedger.SaveAtlas", e);
            }
        }

        /// <summary>The distance from each spot nobody reached to the navmesh (a few hundred spots, once, mid-raid while the navmesh exists).</summary>
        private static void FillNavDistances()
        {
            for (int i = 0; i < _pois.Count; i++)
            {
                if (_last[i] >= 0f || _nav[i] > -1.5f)
                {
                    continue;
                }

                _nav[i] = UnityEngine.AI.NavMesh.SamplePosition(_pois[i].Pos, out UnityEngine.AI.NavMeshHit hit, 4f, UnityEngine.AI.NavMesh.AllAreas)
                    ? Vector3.Distance(_pois[i].Pos, hit.position)
                    : -1f;
            }
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 3f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || (!_built && !Build()))
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                Vector3 pos = bot.Position;
                (int cx, int cz) = CellOf(pos.x, pos.z);
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!_cells.TryGetValue((cx + dx, cz + dz), out List<int> list))
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            Poi poi = _pois[i];
                            float ox = poi.X - pos.x, oz = poi.Z - pos.z;
                            float h2 = ox * ox + oz * oz;
                            if (h2 <= VisitRadius * VisitRadius && Mathf.Abs(poi.Y - pos.y) <= VisitHeight)
                            {
                                _last[i] = now;
                            }

                            // For the atlas report: the nearest a bot came horizontally, and at what height difference
                            if (h2 < _minH[i] * _minH[i])
                            {
                                _minH[i] = Mathf.Sqrt(h2);
                                _dyAtMin[i] = pos.y - poi.Y;
                            }
                        }
                    }
                }
            }

            if (now >= _nextLog)
            {
                _nextLog = now + 300f;
                Totals(out int visited, out int regions, out int untouched);
                SaveAtlas(false);
                Log.Detail("info", $"LR Coverage: {visited} of {_pois.Count} spots visited by a bot ({100 * visited / Math.Max(1, _pois.Count)} %), regions never entered {untouched} of {regions}");
            }
        }

        private static void Totals(out int visited, out int regions, out int untouched)
        {
            visited = 0;
            float size = Mathf.Max(60f, Settings.RoamRegionSize.Value);
            Dictionary<(int, int), bool> reg = new Dictionary<(int, int), bool>();
            for (int i = 0; i < _pois.Count; i++)
            {
                bool seen = _last[i] >= 0f;
                if (seen)
                {
                    visited++;
                }

                (int, int) k = (Mathf.FloorToInt(_pois[i].X / size), Mathf.FloorToInt(_pois[i].Z / size));
                reg[k] = reg.TryGetValue(k, out bool was) && was || seen;
            }

            regions = reg.Count;
            untouched = reg.Count(kv => !kv.Value);
        }

        /// <summary>
        /// A spot no bot has been to in this raid, between <paramref name="minR"/> and <paramref name="maxR"/> metres (horizontally) from <paramref name="centre"/> and not within
        /// 8 m of any of <paramref name="avoid"/>: for a squad member's short look round (FollowerSweep). Spots on another floor than the centre and spots the atlas missed
        /// in earlier raids are likelier. False when there is none.
        /// </summary>
        public static bool TryColdSpotNear(Vector3 centre, float minR, float maxR, IList<Vector3> avoid, System.Random rng, out Poi found)
        {
            found = null;
            if (!_built)
            {
                return false;
            }

            float total = 0f;
            Poi pick = null;
            (int cx, int cz) = CellOf(centre.x, centre.z);
            int ring = Mathf.CeilToInt(maxR / CellSize);
            for (int dz = -ring; dz <= ring; dz++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (!_cells.TryGetValue((cx + dx, cz + dz), out List<int> list))
                    {
                        continue;
                    }

                    foreach (int i in list)
                    {
                        if (_last[i] >= 0f)
                        {
                            continue;
                        }

                        Poi poi = _pois[i];
                        float ox = poi.X - centre.x, oz = poi.Z - centre.z;
                        float h = Mathf.Sqrt(ox * ox + oz * oz);
                        if (h < minR || h > maxR || Mathf.Abs(poi.Y - centre.y) > 12f)
                        {
                            continue;
                        }

                        bool near = false;
                        for (int a = 0; a < avoid.Count && !near; a++)
                        {
                            float ax = poi.X - avoid[a].x, az = poi.Z - avoid[a].z;
                            near = ax * ax + az * az < 64f && Mathf.Abs(poi.Y - avoid[a].y) < 3f;
                        }

                        if (near)
                        {
                            continue;
                        }

                        float w = Mathf.Max(0.3f, poi.W) * (Mathf.Abs(poi.Y - centre.y) > 2.5f ? 2f : 1f) * (1f + 0.5f * Mathf.Min(3, _missed0.Length > i ? _missed0[i] : 0));
                        total += w;
                        if (rng.NextDouble() * total < w)
                        {
                            pick = poi;
                        }
                    }
                }
            }

            found = pick;
            return pick != null;
        }

        /// <summary>
        /// The spot at this place (a quest step's position: within 2.5 m, same floor) was visited by a bot in the last <paramref name="seconds"/> (FreshSteps).
        /// </summary>
        public static bool VisitedLately(Vector3 pos, float seconds, out bool loot, out int index, out float age)
        {
            loot = false;
            index = -1;
            age = 0f;
            if (!_built)
            {
                return false;
            }

            (int cx, int cz) = CellOf(pos.x, pos.z);
            float best = 2.5f * 2.5f;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!_cells.TryGetValue((cx + dx, cz + dz), out List<int> list))
                    {
                        continue;
                    }

                    foreach (int i in list)
                    {
                        Poi poi = _pois[i];
                        float ox = poi.X - pos.x, oz = poi.Z - pos.z;
                        float h2 = ox * ox + oz * oz;
                        if (h2 < best && Mathf.Abs(poi.Y - pos.y) <= 2f)
                        {
                            best = h2;
                            index = i;
                        }
                    }
                }
            }

            if (index < 0 || _last[index] < 0f)
            {
                return false;
            }

            age = Time.time - _last[index];
            loot = _pois[index].Kind != PoiKind.Open;
            return age < seconds;
        }

        /// <summary>A quest this spot is a step of still has another spot nobody has been to in the last <paramref name="seconds"/>.</summary>
        public static bool QuestHasOtherFresh(int index, float seconds)
        {
            if (!_built || !_questsOf.TryGetValue(index, out List<int[]> quests))
            {
                return false;
            }

            float now = Time.time;
            foreach (int[] steps in quests)
            {
                foreach (int j in steps)
                {
                    if (j != index && (_last[j] < 0f || now - _last[j] >= seconds))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// A part of the map that most needs somebody: the centre of the spots nobody has been to in the region with the most of them (by weight; one of the best
        /// three by chance), the farther from <paramref name="avoid"/> (fights) the better. For people coming into the raid late (PScavReplacement).
        /// </summary>
        public static bool TryColdArea(IList<Vector3> avoid, System.Random rng, out Vector3 centre, out int spots)
        {
            centre = default;
            spots = 0;
            if (!_built || _pois.Count == 0)
            {
                return false;
            }

            float size = Mathf.Max(60f, Settings.RoamRegionSize.Value);
            var regions = new Dictionary<(int, int), (Vector3 sum, int n, float w)>();
            for (int i = 0; i < _pois.Count; i++)
            {
                if (_last[i] >= 0f)
                {
                    continue;
                }

                Poi poi = _pois[i];
                (int, int) k = (Mathf.FloorToInt(poi.X / size), Mathf.FloorToInt(poi.Z / size));
                regions.TryGetValue(k, out (Vector3 sum, int n, float w) r);
                regions[k] = (r.sum + poi.Pos, r.n + 1, r.w + Mathf.Max(0.3f, poi.W));
            }

            List<(Vector3 at, int n, float score)> best = new List<(Vector3, int, float)>();
            foreach (var r in regions.Values)
            {
                if (r.n < 5)
                {
                    continue;
                }

                Vector3 at = r.sum / r.n;
                float far = 1f;
                if (avoid != null)
                {
                    foreach (Vector3 a in avoid)
                    {
                        float dx = a.x - at.x, dz = a.z - at.z;
                        far = Mathf.Min(far, Mathf.Clamp(Mathf.Sqrt(dx * dx + dz * dz) / 220f, 0.15f, 1f));
                    }
                }

                best.Add((at, r.n, r.w * far));
            }

            if (best.Count == 0)
            {
                return false;
            }

            best = best.OrderByDescending(b => b.score).Take(3).ToList();
            float total = best.Sum(b => b.score);
            float roll = (float)rng.NextDouble() * total;
            (Vector3 at, int n, float score) pick = best[0];
            foreach (var b in best)
            {
                roll -= b.score;
                if (roll <= 0f)
                {
                    pick = b;
                    break;
                }
            }

            centre = pick.at;
            spots = pick.n;
            return true;
        }

        /// <summary>The weighted share of a quest's spots that are still fresh: 1 = nobody has been to any of them, 0 = all were visited just now.</summary>
        public static float Freshness(string questName)
        {
            if (!_built || !QuestFactory.QuestPois.TryGetValue(questName, out List<Poi> list) || list.Count == 0)
            {
                return -1f;
            }

            float now = Time.time;
            float sum = 0f, weight = 0f;
            foreach (Poi poi in list)
            {
                if (!_index.TryGetValue(poi, out int i))
                {
                    continue;
                }

                float w = Mathf.Max(0.3f, poi.W);
                float score = _last[i] < 0f ? 1f : 0.5f * Mathf.Clamp01((now - _last[i]) / ReFreshSeconds);
                // Atlas: a spot missed in several raids in a row is worth more than fresh ground (up to +75 %)
                if (_last[i] < 0f && _missed0.Length > i && _missed0[i] >= Atlas.MinMissed)
                {
                    score += 0.25f * Mathf.Min(3, _missed0[i] - Atlas.MinMissed + 1);
                }

                sum += w * score;
                weight += w;
            }

            return weight > 0f ? sum / weight : -1f;
        }

        /// <summary>A quest with fresh ground is wanted more, one whose spots were all visited lately less. Only the exploration quests, only PMCs and player scavs.</summary>
        public static float Multiplier(BotProfile p, BotQuest quest)
        {
            if (!On || p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (!name.StartsWith(QuestFactory.ExplorePrefix))
            {
                return 1f;
            }

            float u = Freshness(name);
            if (u < 0f)
            {
                return 1f;
            }

            float s = Settings.CoverageStrength.Value / 100f;
            // The sweep quests of the atlas (the places the last raids missed) are wanted outright (1.3.0)
            float sweepBoost = Settings.AtlasMemory.Value && name.StartsWith(QuestFactory.SweepPrefix) ? 1.5f : 1f;
            float raw = Mathf.Clamp(0.7f + (0.3f + 1.1f * s) * Mathf.Pow(u, 0.8f), 0.5f, 2.5f);
            // Each bot obeys to its own degree: explorers more, the rest a little less
            float obey = Mathf.Clamp(0.6f + 0.3f * p.Explore, 0.5f, 1.2f);
            float m = Mathf.Pow(raw, obey) * sweepBoost;
            // 1.2.9: ground nobody has set foot on at all pulls harder as the raid goes on (F12 "Pull of the cold corners"): from minute 4 up to +45 % at minute 12
            if (Settings.ColdPull.Value && u >= 0.95f)
            {
                float t = QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds();
                float cold = 1f + 0.45f * Mathf.Clamp01((t - 240f) / 480f) * obey;
                if (cold > 1.02f)
                {
                    m *= cold;
                    Cold++;
                }
            }
            Asked++;
            if (m > 1.15f)
            {
                Favoured++;
            }
            else if (m < 0.85f)
            {
                Held++;
            }

            return m;
        }
    }
}
