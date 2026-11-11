using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>One spot of the map in the atlas: in how many raids in a row no bot came near it, and in how many some bot did.</summary>
    public class AtlasSpot
    {
        public float X, Y, Z;

        // Raids in a row in which no bot came within the visit radius (0 = visited in the last raid)
        public int Missed;

        // Raids in which some bot did
        public int Seen;

        [JsonIgnore]
        public Vector3 Pos => new Vector3(X, Y, Z);
    }

    internal class AtlasFile
    {
        public int Version = 1;
        public string LocationId;
        public int Raids;
        public List<AtlasSpot> Spots = new List<AtlasSpot>();
    }

    /// <summary>
    /// The map remembers where the bots did not go (1.3.0, F12 -> 3 "The map remembers cold places"). Raids 2026-11-09 (8 on Customs): 62-64 % of the spots visited at the 20th
    /// minute, a block of 131 spots at (87, -162) that no bot reached in any of 13 raids, upper floors visited by chance. A raid is random by design, so one raid never
    /// covers everything - but the next one can know what the last ones missed. After every raid (and every 5 minutes) <see cref="CoverageLedger"/> writes into
    /// learned/&lt;map&gt;.atlas.json for each spot of the map whether any bot came near it; spots missed in several raids in a row
    /// (<see cref="ColdSpots"/>) are gathered by <see cref="QuestFactory"/> into extra "LR Explore S&lt;k&gt;" quests, building by building, ground floor first, and weigh more in
    /// <see cref="CoverageLedger.Freshness"/>. Over a few raids the whole map is walked, each raid with a different cast of bots.
    /// The report atlas/&lt;map&gt;.csv lists every spot nobody reached in the last raid with the reason it can be seen from the data: no navmesh near it, a bot passed
    /// on another floor, or nobody came within 40 m.
    /// </summary>
    public static class Atlas
    {
        private const float Cell = 4f;
        private const float SameXz = 2.5f;
        private const float SameY = 1.5f;

        private static string _location;
        private static AtlasFile _file;
        private static Dictionary<(int, int), List<AtlasSpot>> _grid = new Dictionary<(int, int), List<AtlasSpot>>();

        public static int ColdCount { get; private set; }
        public static int Swept { get; private set; }

        public static string Summary => _file == null
            ? "atlas: not used"
            : $"atlas: {_file.Spots.Count} spots, {_file.Raids} raids counted, {ColdCount} cold (missed in {MinMissed}+ raids in a row), {Swept} of them in sweep quests";

        public const int MinMissed = 2;

        private static string PathOf(string location) => System.IO.Path.Combine(Plugin.PluginDir, "learned", location + ".atlas.json");

        private static string ReportPath(string location) => System.IO.Path.Combine(Plugin.PluginDir, "atlas", location + ".csv");

        private static (int, int) CellOf(float x, float z) => (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        public static void Clear()
        {
            _location = null;
            _file = null;
            _grid = new Dictionary<(int, int), List<AtlasSpot>>();
            ColdCount = Swept = 0;
        }

        /// <summary>Reads the atlas of the map (once per raid).</summary>
        public static void Load(string location)
        {
            if (_file != null && _location == location)
            {
                return;
            }

            _location = location;
            _grid = new Dictionary<(int, int), List<AtlasSpot>>();
            _file = new AtlasFile { LocationId = location };
            try
            {
                string path = PathOf(location);
                if (File.Exists(path))
                {
                    _file = JsonConvert.DeserializeObject<AtlasFile>(File.ReadAllText(path)) ?? _file;
                    _file.LocationId = location;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Atlas: the atlas of {location} is unreadable, starting a new one: {e.Message}");
            }

            foreach (AtlasSpot s in _file.Spots)
            {
                Index(s);
            }
        }

        private static void Index(AtlasSpot s)
        {
            (int, int) c = CellOf(s.X, s.Z);
            if (!_grid.TryGetValue(c, out List<AtlasSpot> list))
            {
                _grid[c] = list = new List<AtlasSpot>();
            }

            list.Add(s);
        }

        public static AtlasSpot Find(Vector3 p)
        {
            (int cx, int cz) = CellOf(p.x, p.z);
            AtlasSpot best = null;
            float bestD = float.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!_grid.TryGetValue((cx + dx, cz + dz), out List<AtlasSpot> list))
                    {
                        continue;
                    }

                    foreach (AtlasSpot s in list)
                    {
                        float ox = s.X - p.x, oz = s.Z - p.z;
                        float d = ox * ox + oz * oz;
                        if (d <= SameXz * SameXz && Mathf.Abs(s.Y - p.y) <= SameY && d < bestD)
                        {
                            best = s;
                            bestD = d;
                        }
                    }
                }
            }

            return best;
        }

        public static int MissedOf(Vector3 p) => Find(p)?.Missed ?? 0;

        /// <summary>The spots missed in several raids in a row, most neglected first (QuestFactory matches them to the map's points of interest).</summary>
        public static List<AtlasSpot> ColdSpots(string location)
        {
            Load(location);
            List<AtlasSpot> cold = _file.Spots.Where(s => s.Missed >= MinMissed).OrderByDescending(s => s.Missed).ToList();
            ColdCount = cold.Count;
            return cold;
        }

        public static void NoteSwept(int count) => Swept = count;

        /// <summary>
        /// Writes the atlas after (or in the middle of) a raid. <paramref name="visited"/> says for each spot whether a bot came near it in THIS raid; the counters are
        /// computed from the values at the start of the raid, so writing twice in one raid counts it once.
        /// </summary>
        public static void Save(string location, IReadOnlyList<Poi> spots, IReadOnlyList<bool> visited, IReadOnlyList<int> missedAtStart, IReadOnlyList<int> seenAtStart,
            IReadOnlyList<float> nearestHorizontal, IReadOnlyList<float> nearestDy, IReadOnlyList<float> navDistance, IReadOnlyCollection<Poi> inQuests, bool newRaid)
        {
            Load(location);
            for (int i = 0; i < spots.Count; i++)
            {
                AtlasSpot s = Find(spots[i].Pos);
                if (s == null)
                {
                    s = new AtlasSpot { X = spots[i].X, Y = spots[i].Y, Z = spots[i].Z };
                    _file.Spots.Add(s);
                    Index(s);
                }

                s.Missed = visited[i] ? 0 : missedAtStart[i] + 1;
                s.Seen = seenAtStart[i] + (visited[i] ? 1 : 0);
            }

            if (newRaid)
            {
                _file.Raids = _raidsAtStart + 1;
            }

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf(location)));
                File.WriteAllText(PathOf(location), JsonConvert.SerializeObject(_file, Formatting.None));

                StringBuilder sb = new StringBuilder("x,y,z,kind,weight,missed_in_a_row,seen_raids,nearest_bot_horizontal_m,dy_at_nearest_m,navmesh_distance_m,in_a_quest,reason\n");
                int rows = 0;
                for (int i = 0; i < spots.Count; i++)
                {
                    if (visited[i])
                    {
                        continue;
                    }

                    AtlasSpot s = Find(spots[i].Pos);
                    float h = nearestHorizontal[i], dy = nearestDy[i], nav = navDistance[i];
                    bool navKnown = nav > -1.5f;
                    string reason = navKnown && (nav < 0f || nav > 3f)
                        ? $"no navmesh within 3 m ({(nav < 0f ? "none in 4 m" : nav.ToString("0.0", CultureInfo.InvariantCulture) + " m")})"
                        : h < 14f && Mathf.Abs(dy) > 3.5f ? $"a bot passed {h:0} m away on another floor ({dy:+0.0;-0.0} m): the way to this floor is the problem"
                        : h < 40f ? $"a bot came within {h:0} m but not within the visit radius"
                        : "no bot came within 40 m: nobody was sent there";
                    sb.Append(spots[i].X.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(spots[i].Y.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(spots[i].Z.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(spots[i].Kind).Append(',')
                        .Append(spots[i].W.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(s?.Missed ?? 1).Append(',').Append(s?.Seen ?? 0).Append(',')
                        .Append(h > 9000f ? "-" : h.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(h > 9000f ? "-" : dy.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(!navKnown || nav < 0f ? "-" : nav.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(inQuests.Contains(spots[i]) ? 1 : 0).Append(',')
                        .Append('"').Append(reason).Append("\"\n");
                    rows++;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ReportPath(location)));
                File.WriteAllText(ReportPath(location), sb.ToString());
                Log.Detail("info", $"LR Atlas: {location} saved - {_file.Spots.Count} spots, {rows} not reached this raid (atlas/{location}.csv)");
            }
            catch (Exception e)
            {
                Log.Warn("LR Atlas: not saved: " + e.Message);
            }
        }

        private static int _raidsAtStart;

        /// <summary>Called when the raid's ledger is built: the raid count the file had before this raid.</summary>
        public static void RaidStarts() => _raidsAtStart = _file?.Raids ?? 0;
    }
}
