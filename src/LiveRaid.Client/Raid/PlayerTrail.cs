using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    /// <summary>A place on the map the player went to that the bots' map (map intel POIs) did not have.</summary>
    public class LearnedPlace
    {
        public float X, Y, Z;

        // How much a bot should want to go there: a stop (the player stood there, likely looting) more than a place walked through
        public float W;

        // Raids in which the player came by, how many of them he stopped there, longest stop (s)
        public int Raids;
        public int Stops;
        public float Dwell;
        public string Last;

        [JsonIgnore]
        public Vector3 Pos => new Vector3(X, Y, Z);
    }

    internal class LearnedFile
    {
        public int Version = 1;
        public string LocationId;
        public List<LearnedPlace> Places = new List<LearnedPlace>();
    }

    /// <summary>
    /// Learns the map from the player. In the background, every raid records where the player goes (a point every few metres, a mark where
    /// he stood a while, the stretches where he left the navmesh: climbs, rooftops, rubble). While the raid goes on the trail is analysed:
    /// every place the bots' map does not cover (no point of interest on the same floor nearby), that a bot can walk to, becomes a learned
    /// place in BepInEx/plugins/LiveRaid/learned/&lt;map&gt;.json. From the next raid the exploration quests send bots there too.
    /// Stretches off the navmesh that join two places the navmesh does not (or only by a long detour) are written as route candidates
    /// (learned/&lt;map&gt;.shortcuts.json, the routes/ format); bots use them only with F12 -> 10 "Use learned shortcuts".
    /// </summary>
    public static class PlayerTrail
    {
        private class Sample
        {
            public Vector3 Pos;
            public float T;
            public bool OnNav;
            public float Pose;
            public bool Air;
        }

        private class Candidate
        {
            public Vector3 Pos;
            public bool Stop;
            public float Dwell;
        }

        private const int MaxPlaces = 600;
        private const float WalkSpacing = 25f;
        private const float StopSeconds = 8f;
        private const float StopRadius = 3f;

        private static string _location;
        private static LearnedFile _file;
        private static readonly Queue<Candidate> _pending = new Queue<Candidate>();
        private static readonly HashSet<LearnedPlace> _touchedThisRaid = new HashSet<LearnedPlace>();

        /// <summary>One human's trail in this raid (in LiveRaid Online the host follows every player of the group).</summary>
        private class Walker
        {
            public readonly List<Sample> Trail = new List<Sample>();
            public readonly List<Sample> OffNav = new List<Sample>();
            public Sample LastOnNav;
            public Vector3? LastWalkCandidate;
            public Vector3 Anchor;
            public float AnchorSince;
            public bool HasAnchor;
        }

        private static readonly Dictionary<string, Walker> _walkers = new Dictionary<string, Walker>();

        private static int TrailPoints => _walkers.Values.Sum(w => w.Trail.Count);
        private static float _next, _nextSave;
        private static int _added, _merged, _covered, _unreachable, _shortcuts;
        private static string _trailCsv;
        private static readonly StringBuilder _csv = new StringBuilder();
        private static bool _dirty;

        public static int Added => _added;

        public static void Begin(string location)
        {
            _location = location;
            _file = null;
            _walkers.Clear();
            _pending.Clear();
            _touchedThisRaid.Clear();
            _next = 0f;
            _nextSave = Time.time + 120f;
            _added = _merged = _covered = _unreachable = _shortcuts = 0;
            _csv.Clear();
            _trailCsv = null;
            _dirty = false;
            if (Settings.LearnFromMyRoute.Value)
            {
                try
                {
                    string dir = Path.Combine(Plugin.PluginDir, "learned", "trails");
                    Directory.CreateDirectory(dir);
                    _trailCsv = Path.Combine(dir, $"{location}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                    // The newest 30 trails are kept
                    foreach (FileInfo old in new DirectoryInfo(dir).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTimeUtc).Skip(29))
                    {
                        old.Delete();
                    }

                    File.WriteAllText(_trailCsv, "time,x,y,z,on_navmesh,mark\n");
                }
                catch (Exception e)
                {
                    Log.Warn("LR Trail: cannot write the trail file: " + e.Message);
                    _trailCsv = null;
                }
            }
        }

        public static void End()
        {
            if (_location == null)
            {
                return;
            }

            if (Settings.LearnFromMyRoute.Value)
            {
                foreach (Walker w in _walkers.Values)
                {
                    FlushStop(w, Time.time);
                }

                // Whatever is still waiting is checked now (map intel is still there: called before it is cleared)
                int budget = 200;
                while (_pending.Count > 0 && budget-- > 0)
                {
                    Analyze(_pending.Dequeue());
                }

                FlushCsv();
                Save();
                if (TrailPoints > 0)
                {
                    Log.Info($"LR Trail on {_location}: {TrailPoints} trail points{(_walkers.Count > 1 ? $" of {_walkers.Count} players" : "")}, {_added} new places for the bots, {_merged} known learned places visited again, " +
                             $"{_covered} already covered by the bots' map, {_unreachable} not walkable for bots, {_shortcuts} shortcut candidates; " +
                             $"{Load().Places.Count} learned places on the map in total");
                }
            }

            _location = null;
            _file = null;
        }

        private static string FilePath(string location) => Path.Combine(Plugin.PluginDir, "learned", location + ".json");

        private static string ShortcutPath(string location) => Path.Combine(Plugin.PluginDir, "learned", location + ".shortcuts.json");

        private static LearnedFile Load()
        {
            if (_file != null)
            {
                return _file;
            }

            _file = LoadFile(_location);
            return _file;
        }

        private static LearnedFile LoadFile(string location)
        {
            try
            {
                string path = FilePath(location);
                if (File.Exists(path))
                {
                    LearnedFile f = JsonConvert.DeserializeObject<LearnedFile>(File.ReadAllText(path));
                    if (f?.Places != null)
                    {
                        return f;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Trail: learned places of {location} unreadable, starting afresh: {e.Message}");
            }

            return new LearnedFile { LocationId = location };
        }

        private static void Save()
        {
            if (!_dirty || _file == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath(_location)));
                File.WriteAllText(FilePath(_location), JsonConvert.SerializeObject(_file, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception e)
            {
                Log.Warn("LR Trail: learned places not saved: " + e.Message);
            }
        }

        /// <summary>Learned places of the map as points of interest for the exploration quests (empty when the feature is off).</summary>
        public static List<Poi> PlacesFor(string location)
        {
            if (!Settings.BotsVisitLearned.Value)
            {
                return new List<Poi>();
            }

            return LoadFile(location).Places.Select(p => Poi.At(p.Pos, PoiKind.Learned, p.W)).ToList();
        }

        /// <summary>Route candidates found in the player's trails, for Routes when "Use learned shortcuts" is on.</summary>
        public static List<Route> ShortcutsFor(string location)
        {
            try
            {
                string path = ShortcutPath(location);
                return File.Exists(path) ? JsonConvert.DeserializeObject<RouteFile>(File.ReadAllText(path))?.Routes ?? new List<Route>() : new List<Route>();
            }
            catch (Exception e)
            {
                Log.Warn($"LR Trail: learned shortcuts of {location} unreadable: {e.Message}");
                return new List<Route>();
            }
        }

        public static void Tick()
        {
            if (_location == null || !Settings.LearnFromMyRoute.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            if (!Spectator.SpectatorSession.Active)
            {
                foreach (Player human in Net.Humans())
                {
                    if (!_walkers.TryGetValue(human.ProfileId, out Walker w))
                    {
                        _walkers[human.ProfileId] = w = new Walker();
                    }

                    Record(w, human);
                }
            }

            MapIntel intel = MapIntel.Current;
            if (intel != null && intel.Ready)
            {
                // A few path checks per tick at most: the analysis never shows in the frame time
                for (int i = 0; i < 3 && _pending.Count > 0; i++)
                {
                    Analyze(_pending.Dequeue());
                }
            }

            if (Time.time > _nextSave)
            {
                _nextSave = Time.time + 120f;
                FlushCsv();
                Save();
            }
        }

        private static void Record(Walker w, Player me)
        {
            Vector3 pos = me.Position;
            float now = Time.time;
            bool onNav = NavMesh.SamplePosition(pos, out NavMeshHit hit, 1f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - pos.y) < 0.8f;
            Sample s = new Sample
            {
                Pos = pos,
                T = now,
                OnNav = onNav,
                Pose = me.MovementContext != null ? me.MovementContext.PoseLevel : 1f,
                Air = me.MovementContext != null && !me.MovementContext.IsGrounded,
            };

            // Stops: the player stood within a few metres for a while (looting, holding an angle, a quest spot)
            if (!w.HasAnchor)
            {
                w.Anchor = pos;
                w.AnchorSince = now;
                w.HasAnchor = true;
            }
            else if ((pos - w.Anchor).sqrMagnitude > StopRadius * StopRadius)
            {
                FlushStop(w, now);
                w.Anchor = pos;
                w.AnchorSince = now;
            }

            Sample last = w.Trail.Count > 0 ? w.Trail[w.Trail.Count - 1] : null;
            if (last == null || (pos - last.Pos).sqrMagnitude > 3f * 3f || (!onNav && (pos - last.Pos).sqrMagnitude > 0.7f * 0.7f))
            {
                w.Trail.Add(s);
                AppendCsv(s, onNav ? "walk" : "off_navmesh");
                if (onNav && (w.LastWalkCandidate == null || FloorAwareDistance(pos, w.LastWalkCandidate.Value) > WalkSpacing))
                {
                    w.LastWalkCandidate = pos;
                    _pending.Enqueue(new Candidate { Pos = pos });
                }
            }

            // Stretches off the navmesh: where the player climbed, walked a railing or a roof
            if (!onNav)
            {
                w.OffNav.Add(s);
            }
            else
            {
                if (w.OffNav.Count > 0)
                {
                    CloseOffNav(w, s);
                }

                w.LastOnNav = s;
            }
        }

        private static void FlushStop(Walker w, float now)
        {
            if (!w.HasAnchor)
            {
                return;
            }

            float dwell = now - w.AnchorSince;
            if (dwell >= StopSeconds)
            {
                _pending.Enqueue(new Candidate { Pos = w.Anchor, Stop = true, Dwell = dwell });
                AppendCsv(new Sample { Pos = w.Anchor, T = now, OnNav = true }, $"stop_{dwell:0}s");
            }

            w.HasAnchor = false;
        }

        private static void Analyze(Candidate c)
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null)
            {
                return;
            }

            if (!NavMesh.SamplePosition(c.Pos, out NavMeshHit hit, 2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - c.Pos.y) > 1.5f
                || !intel.IsReachable(hit.position))
            {
                _unreachable++;
                return;
            }

            Vector3 p = hit.position;
            LearnedFile file = Load();

            // Learned before: the place counts one more raid (and a stop now if it was only walked through)
            LearnedPlace known = file.Places.FirstOrDefault(l => SameFloorNear(l.Pos, p, c.Stop ? 6f : 9f));
            if (known != null)
            {
                if (_touchedThisRaid.Add(known))
                {
                    known.Raids++;
                    _merged++;
                }

                if (c.Stop)
                {
                    known.Stops++;
                    known.Dwell = Mathf.Max(known.Dwell, c.Dwell);
                }

                known.W = Weight(known);
                known.Last = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _dirty = true;
                return;
            }

            // On the bots' map already: a point of interest on the same floor close by
            float near = c.Stop ? 10f : 20f;
            if (intel.Pois.Any(poi => SameFloorNear(poi.Pos, p, near)))
            {
                _covered++;
                return;
            }

            LearnedPlace place = new LearnedPlace
            {
                X = p.x,
                Y = p.y,
                Z = p.z,
                Raids = 1,
                Stops = c.Stop ? 1 : 0,
                Dwell = c.Stop ? c.Dwell : 0f,
                Last = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            place.W = Weight(place);
            file.Places.Add(place);
            _touchedThisRaid.Add(place);
            _added++;
            _dirty = true;
            Log.Detail("trail", $"new place for the bots at {p.x:0.0}, {p.y:0.0}, {p.z:0.0} ({(c.Stop ? $"you stood {c.Dwell:0} s" : "you walked through")})");

            if (file.Places.Count > MaxPlaces)
            {
                // Keep the places that matter: often visited, stopped at, recent
                LearnedPlace weakest = file.Places.Where(l => !_touchedThisRaid.Contains(l))
                    .OrderBy(l => l.Raids * 2 + l.Stops * 3)
                    .ThenBy(l => l.Last, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (weakest != null)
                {
                    file.Places.Remove(weakest);
                }
            }
        }

        /// <summary>Stood there: likely loot or a good spot (1.2-3.7). Walked through: open ground worth a look (0.5), more with repeat visits.</summary>
        private static float Weight(LearnedPlace l)
        {
            float w = l.Stops > 0 ? 1.2f + Mathf.Min(2.5f, l.Dwell / 25f) : 0.5f;
            return w + Mathf.Min(1f, 0.15f * (l.Raids - 1) + 0.2f * Mathf.Max(0, l.Stops - 1));
        }

        /// <summary>
        /// The player is back on the navmesh after a stretch off it. If it joined two points the navmesh cannot (or only by a long detour),
        /// it is written as a route candidate. Drops (a fall or jump down) are one-way and are not taken: routes work both ways.
        /// </summary>
        private static void CloseOffNav(Walker w, Sample back)
        {
            List<Sample> stretch = w.OffNav.ToList();
            w.OffNav.Clear();
            Sample start = w.LastOnNav;
            if (start == null || stretch.Count < 2 || back.T - start.T > 40f)
            {
                return;
            }

            float walked = 0f;
            Vector3 prev = start.Pos;
            Sample prevSample = start;
            foreach (Sample s in stretch.Concat(new[] { back }))
            {
                walked += Vector3.Distance(prev, s.Pos);
                if (prevSample.Pos.y - s.Pos.y > 1.5f && s.T - prevSample.T < 1.1f)
                {
                    // fell or jumped down: a bot could not come back up this way
                    return;
                }

                prev = s.Pos;
                prevSample = s;
            }

            if (walked < 3f || walked > 60f)
            {
                return;
            }

            NavMeshPath path = new NavMeshPath();
            bool connected = NavMesh.CalculatePath(start.Pos, back.Pos, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            float navWay = connected ? PathLength(path) : 0f;
            if (connected && navWay < Mathf.Max(walked * 3f, walked + 25f))
            {
                return;
            }

            List<RoutePoint> points = new List<RoutePoint> { Point(start) };
            Vector3 lastKept = start.Pos;
            foreach (Sample s in stretch)
            {
                if ((s.Pos - lastKept).sqrMagnitude > 1f || s.Air)
                {
                    points.Add(Point(s));
                    lastKept = s.Pos;
                }
            }

            points.Add(Point(back));
            Route route = new Route { Id = $"{_location}_learned_{DateTime.Now:MMdd_HHmmss}", Points = points };
            try
            {
                RouteFile file = File.Exists(ShortcutPath(_location))
                    ? JsonConvert.DeserializeObject<RouteFile>(File.ReadAllText(ShortcutPath(_location))) ?? new RouteFile()
                    : new RouteFile();

                // The same climb again: keep the first recording
                if (file.Routes.Any(r => r.Points.Count >= 2
                                         && ((Vector3.Distance(r.Points[0].Pos, start.Pos) < 3f && Vector3.Distance(r.Points[r.Points.Count - 1].Pos, back.Pos) < 3f)
                                             || (Vector3.Distance(r.Points[0].Pos, back.Pos) < 3f && Vector3.Distance(r.Points[r.Points.Count - 1].Pos, start.Pos) < 3f))))
                {
                    return;
                }

                file.Routes.Add(route);
                Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath(_location)));
                File.WriteAllText(ShortcutPath(_location), JsonConvert.SerializeObject(file, Formatting.Indented));
                _shortcuts++;
                Log.Info($"LR Trail: shortcut candidate {route.Id}: {points.Count} points, you went {walked:0} m off the navmesh, " +
                         (connected ? $"bots would walk {navWay:0} m around" : "bots have no way there at all") +
                         $" (learned/{_location}.shortcuts.json; bots take it with F12 -> 10 'Use learned shortcuts')");
            }
            catch (Exception e)
            {
                Log.Warn("LR Trail: shortcut not saved: " + e.Message);
            }
        }

        private static RoutePoint Point(Sample s) => new RoutePoint { X = s.Pos.x, Y = s.Pos.y, Z = s.Pos.z, Pose = s.Pose < 0.6f ? 0f : 1f, Jump = s.Air };

        private static bool SameFloorNear(Vector3 a, Vector3 b, float dist)
        {
            if (Mathf.Abs(a.y - b.y) >= MapIntel.FloorGap)
            {
                return false;
            }

            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz < dist * dist;
        }

        private static float FloorAwareDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            return Mathf.Sqrt(d.x * d.x + d.z * d.z + 16f * d.y * d.y);
        }

        private static float PathLength(NavMeshPath path)
        {
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            return length;
        }

        private static void AppendCsv(Sample s, string mark)
        {
            if (_trailCsv == null)
            {
                return;
            }

            _csv.Append(((int)s.T).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Pos.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Pos.y.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Pos.z.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.OnNav ? '1' : '0').Append(',').Append(mark).Append('\n');
            if (_csv.Length > 8000)
            {
                FlushCsv();
            }
        }

        private static void FlushCsv()
        {
            if (_trailCsv == null || _csv.Length == 0)
            {
                return;
            }

            try
            {
                File.AppendAllText(_trailCsv, _csv.ToString());
            }
            catch (Exception e)
            {
                Log.Warn("LR Trail: trail file write failed: " + e.Message);
                _trailCsv = null;
            }

            _csv.Clear();
        }
    }
}
