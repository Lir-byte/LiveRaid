using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    public class RoutePoint
    {
        public float X, Y, Z;

        // 1 = standing, 0 = crouched
        public float Pose = 1f;

        // The bot jumps when it reaches this point (onto a railing, over a wire...)
        public bool Jump;

        [JsonIgnore]
        public Vector3 Pos => new Vector3(X, Y, Z);
    }

    /// <summary>
    /// A hand-recorded way through a spot the navmesh does not connect: rubble on stairs, a railing to walk along, a window to climb.
    /// The first and the last point stand on the navmesh; the points between are where the feet go.
    /// </summary>
    public class Route
    {
        public string Id;

        // Studio: a name for the editor, and a switch (a disabled climb is kept in the file but not used)
        public string Name;
        public bool Enabled = true;
        public List<RoutePoint> Points = new List<RoutePoint>();

        [JsonIgnore]
        public Vector3 Start;

        [JsonIgnore]
        public Vector3 End;

        [JsonIgnore]
        public bool Linked;

        // The navmesh already joins both ends, but through something a player cannot walk through (rubble, wire on the stairs):
        // the route is made cheaper than the navmesh way, so bots climb it like a player instead of walking through the obstacle
        [JsonIgnore]
        public bool Bypass;
    }

    internal class RouteFile
    {
        public int Version = 1;
        public List<Route> Routes = new List<Route>();
    }

    /// <summary>
    /// LiveRaid routes of the current map (BepInEx/plugins/LiveRaid/routes/&lt;map&gt;.json). Each route becomes a navmesh link, so every
    /// path planner (QuestingBots, SAIN, map intel) sees the place behind it as reachable; RouteTraverseLayer moves the bot along the points.
    /// </summary>
    public static class Routes
    {
        public static readonly List<Route> Active = new List<Route>();
        private static readonly List<NavMeshLinkInstance> _links = new List<NavMeshLinkInstance>();
        private static string _location;

        /// <summary>Identifies the route set; the map cache is rebuilt when it changes (reachability depends on the routes).</summary>
        public static string Key { get; private set; } = "";

        private static string FilePath(string location) => Path.Combine(Plugin.PluginDir, "routes", location + ".json");

        public static void Apply(string location)
        {
            if (_location == location)
            {
                return;
            }

            Remove();
            _location = location;

            // Navmesh patches, blockers and links drawn in LiveRaid Studio go in first: the routes' ends and every reachability check see them
            Diag.Guard("studio edits", () => Studio.MapEdits.Apply(location));
            if (!Settings.Routes.Value)
            {
                return;
            }

            RouteFile file = Load(location);
            if (Settings.UseLearnedShortcuts.Value)
            {
                // Climbs found in the player's trails (PlayerTrail); they change the route key, so the map cache is rebuilt once when one is added
                file.Routes.AddRange(PlayerTrail.ShortcutsFor(location));
            }

            // Climbs drawn in Studio; one imported from routes/<map>.json keeps its Id, so it is not taken twice
            foreach (Route edited in Studio.MapEdits.RoutesFor(location).ToList())
            {
                file.Routes.RemoveAll(r => r.Id == edited.Id);
                file.Routes.Add(edited);
            }

            file.Routes.RemoveAll(r => !r.Enabled);
            Key = KeyFor(file.Routes);
            List<string> report = new List<string>();
            foreach (Route route in file.Routes)
            {
                report.Add(Link(route));
            }

            if (file.Routes.Count > 0)
            {
                Log.Info($"LR Routes on {location}: {file.Routes.Count} route(s), {Active.Count(r => r.Linked)} linked into the navmesh: {string.Join("; ", report)}");
            }
        }

        /// <summary>Applies the routes and the Studio edits again (the edits file changed in a running raid).</summary>
        public static void Reload(string location)
        {
            Remove();
            Apply(location);
        }

        /// <summary>The route set plus what Studio changed in the navmesh: reachability depends on both, so the map cache is rebuilt when either changes.</summary>
        private static string KeyFor(List<Route> routes) =>
            routes.Count == 0 && Studio.MapEdits.NavKey == "" ? "" : Hash(JsonConvert.SerializeObject(routes) + Studio.MapEdits.NavKey);

        public static void Remove()
        {
            Diag.Guard("studio edits removal", Studio.MapEdits.Remove);
            foreach (NavMeshLinkInstance link in _links)
            {
                try
                {
                    link.Remove();
                }
                catch
                {
                    // already gone with the map
                }
            }

            _links.Clear();
            Active.Clear();
            Key = "";
            _location = null;
        }

        private static RouteFile Load(string location)
        {
            try
            {
                string path = FilePath(location);
                return File.Exists(path) ? JsonConvert.DeserializeObject<RouteFile>(File.ReadAllText(path)) ?? new RouteFile() : new RouteFile();
            }
            catch (Exception e)
            {
                Log.Warn($"routes for {location} unreadable: {e.Message}");
                return new RouteFile();
            }
        }

        /// <summary>Connects both ends of the route with a navmesh link. Returns a line for the log.</summary>
        private static string Link(Route route)
        {
            if (route.Points.Count < 2)
            {
                return $"{route.Id}: fewer than 2 points";
            }

            if (!NavMesh.SamplePosition(route.Points[0].Pos, out NavMeshHit a, 1.2f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(route.Points[route.Points.Count - 1].Pos, out NavMeshHit b, 1.2f, NavMesh.AllAreas))
            {
                return $"{route.Id}: an end is not on the navmesh, not linked";
            }

            route.Start = a.position;
            route.End = b.position;
            NavMeshPath path = new NavMeshPath();
            bool before = NavMesh.CalculatePath(route.Start, route.End, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            float navmeshWay = before ? PathLength(path) : 0f;
            route.Bypass = before;

            List<int> agentTypes = new List<int>();
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                agentTypes.Add(NavMesh.GetSettingsByIndex(i).agentTypeID);
            }

            if (agentTypes.Count == 0)
            {
                agentTypes.Add(0);
            }

            int added = 0;
            foreach (int agentType in agentTypes.Distinct())
            {
                NavMeshLinkInstance link = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = route.Start,
                    endPosition = route.End,
                    width = 0f,
                    // A new way: a slow climb, taken only when walking around is a lot longer.
                    // A bypass: cheaper than the navmesh way through the obstacle, so it always wins
                    costModifier = route.Bypass ? 0.2f : 4f,
                    bidirectional = true,
                    area = 0,
                    agentTypeID = agentType,
                });
                if (link.valid)
                {
                    _links.Add(link);
                    added++;
                }
            }

            bool after = NavMesh.CalculatePath(route.Start, route.End, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            float afterWay = after ? PathLength(path) : 0f;
            float straight = Vector3.Distance(route.Start, route.End);

            // Through the link the path is about as long as the link itself. Unity lists both link ends as corners
            // (start, link start, link end, end - 4 corners even when start and link start coincide), so the corner count says nothing.
            bool usesLink = after && afterWay <= straight + 1f && (!route.Bypass || afterWay < navmeshWay - 0.5f);

            // The traverse layer only acts when the bot's own path leads across the far end, so a link that is there is enough
            route.Linked = after && added > 0;
            Active.Add(route);
            string state = route.Bypass
                ? usesLink
                    ? $"bypass: the navmesh walks through here ({navmeshWay:0.0} m), bots now take the route instead ({afterWay:0.0} m)"
                    : $"bypass not taken by the path finder (navmesh way {navmeshWay:0.0} m, with the link {afterWay:0.0} m, {path.corners.Length} corners)"
                : after ? $"linked (new way, {afterWay:0.0} m)" : "link failed (no complete path through it)";
            return $"{route.Id}: {route.Points.Count} points, {Vector3.Distance(route.Start, route.End):0.0} m, {state}";
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

        // ---------------- recording (the player walks the route and presses keys at each foothold) ----------------

        private static readonly List<RoutePoint> _recording = new List<RoutePoint>();

        public static void TickRecorder()
        {
            if (!Settings.RouteRecorder.Value)
            {
                return;
            }

            Player player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null)
            {
                return;
            }

            if (Settings.RouteRecordPoint.Value.IsDown() || Settings.RouteRecordJump.Value.IsDown())
            {
                bool jump = Settings.RouteRecordJump.Value.IsDown();
                Vector3 p = player.Position;
                float pose = player.MovementContext != null ? player.MovementContext.PoseLevel : 1f;
                _recording.Add(new RoutePoint { X = p.x, Y = p.y, Z = p.z, Pose = pose < 0.6f ? 0f : 1f, Jump = jump });
                bool onNavmesh = NavMesh.SamplePosition(p, out _, 0.6f, NavMesh.AllAreas);
                Notify($"Route point {_recording.Count}{(jump ? " (jump)" : "")}: {p.x:0.0}, {p.y:0.0}, {p.z:0.0}{(onNavmesh ? ", on navmesh" : "")}");
            }

            if (Settings.RouteUndo.Value.IsDown() && _recording.Count > 0)
            {
                _recording.RemoveAt(_recording.Count - 1);
                Notify($"Route point removed, {_recording.Count} left");
            }

            if (Settings.RouteSave.Value.IsDown())
            {
                Save(Singleton<GameWorld>.Instance.LocationId);
            }
        }

        private static void Save(string location)
        {
            if (_recording.Count < 2)
            {
                Notify("Route not saved: record at least a start and an end point");
                return;
            }

            bool startOk = NavMesh.SamplePosition(_recording[0].Pos, out _, 1.2f, NavMesh.AllAreas);
            bool endOk = NavMesh.SamplePosition(_recording[_recording.Count - 1].Pos, out _, 1.2f, NavMesh.AllAreas);
            if (!startOk || !endOk)
            {
                Notify($"Route not saved: the {(!startOk ? "first" : "last")} point is not on the navmesh. Start and finish on a floor bots walk on.");
                return;
            }

            RouteFile file = Load(location);
            Route route = new Route { Id = $"{location}_{DateTime.Now:MMdd_HHmmss}", Points = _recording.ToList() };
            file.Routes.Add(route);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath(location)));
                File.WriteAllText(FilePath(location), JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception e)
            {
                Notify("Route not saved: " + e.Message);
                return;
            }

            _recording.Clear();
            string result = Link(route);
            List<Route> all = file.Routes.ToList();
            if (Settings.UseLearnedShortcuts.Value)
            {
                all.AddRange(PlayerTrail.ShortcutsFor(location));
            }

            foreach (Route edited in Studio.MapEdits.RoutesFor(location).ToList())
            {
                all.RemoveAll(r => r.Id == edited.Id);
                all.Add(edited);
            }

            all.RemoveAll(r => !r.Enabled);
            Key = KeyFor(all);
            Log.Info($"LR Routes: saved {result} to routes/{location}.json");
            Notify($"Route saved and active: {result}");
        }

        // Stable across runs (string.GetHashCode is not guaranteed to be)
        private static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h = (h ^ c) * 16777619;
            }

            return h.ToString("x8");
        }

        private static void Notify(string message)
        {
            Log.Info("LR Routes: " + message);
            try
            {
                NotificationManager.DisplayMessageNotification(message, ENotificationDurationType.Default, ENotificationIconType.Default, null);
            }
            catch
            {
                // no UI (headless) - the log line is enough
            }
        }
    }
}
