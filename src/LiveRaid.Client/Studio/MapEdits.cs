using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using Newtonsoft.Json;
using LiveRaid.Client.Raid;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Studio
{
    // ---------------- the file LiveRaid Studio writes (BepInEx/plugins/LiveRaid/studio/edits/<map>.json) ----------------

    /// <summary>A hand-drawn piece of navmesh: a mesh of triangles the navmesh is built over. Ramps, planks, a floor the map forgot.</summary>
    public class NavPatch
    {
        public string Id, Name;
        public bool Enabled = true;
        public List<float[]> Verts = new List<float[]>();
        public List<int> Tris = new List<int>();

        // Where the patch meets the old navmesh: O stands on the old mesh, I inside the patch. A link is added only when no short way joins them
        public List<PatchGate> Gates = new List<PatchGate>();
    }

    public class PatchGate
    {
        public float[] O, I;
    }

    /// <summary>A box that cuts the navmesh out (carving): a fence the navmesh walks through, a pit, a place bots must keep out of.</summary>
    public class Blocker
    {
        public string Id, Name;
        public bool Enabled = true;
        public float[] C;
        public float[] Size;
        public float Yaw;
    }

    public class EditLink
    {
        public string Id, Name;
        public bool Enabled = true;
        public float[] A, B;
        public float Cost = 1f;
        public bool TwoWay = true;
    }

    public class PathPoint
    {
        public float[] P;
        public float Wait = 2f;
        public bool Hold;
    }

    /// <summary>A walk for bots: QuestingBots quest "LR Path &lt;name&gt;" whose steps are these points in order.</summary>
    public class BotPath
    {
        public string Id, Name;
        public bool Enabled = true;
        public List<PathPoint> Points = new List<PathPoint>();

        // once: walk it once; loop: from the last point back to the first; pingpong: there and back
        public string Mode = "pingpong";

        // all / pmc / scav
        public string Who = "all";
        public float Desirability = 40f;
        public int MaxBots = 2;
        public float MaxTime = 300f;
    }

    public class EditSpot
    {
        public string Id, Name;
        public bool Enabled = true;
        public float[] P;

        // loot / hold / camp
        public string Kind = "loot";
        public float W = 2f;
    }

    public class EditsFile
    {
        public string Format = "liveraid-edits/1";
        public string Map;
        public int Revision;
        public string Saved;
        public List<NavPatch> Patches = new List<NavPatch>();
        public List<Blocker> Blockers = new List<Blocker>();
        public List<EditLink> Links = new List<EditLink>();

        // Climbs: the same shape as routes/<map>.json (Points with X, Y, Z, Pose, Jump)
        public List<Route> Routes = new List<Route>();
        public List<BotPath> Paths = new List<BotPath>();
        public List<EditSpot> Spots = new List<EditSpot>();
    }

    /// <summary>
    /// Applies what LiveRaid Studio drew: navmesh patches (built at run time and added to the map's navmesh), blockers (navmesh carving), links,
    /// climbs (as Routes), walks for bots (QuestingBots quests) and extra places (exploration goals). Reloads by itself when the file changes.
    /// </summary>
    public static class MapEdits
    {
        public static EditsFile Current { get; private set; }

        /// <summary>Identifies what changes reachability (patches, blockers, links): the map cache is rebuilt when it changes.</summary>
        public static string NavKey { get; private set; } = "";

        public static string Dir => Path.Combine(Plugin.PluginDir, "studio", "edits");

        public static string FilePath(string location) => Path.Combine(Dir, location + ".json");

        private static string _location;
        private static DateTime _fileTime;
        private static float _nextWatch;
        private static bool _overlayShown;
        private static readonly List<NavMeshDataInstance> _datas = new List<NavMeshDataInstance>();
        private static readonly List<NavMeshLinkInstance> _links = new List<NavMeshLinkInstance>();
        private static readonly List<GameObject> _blockers = new List<GameObject>();

        public static void Apply(string location)
        {
            Remove();
            _location = location;
            Current = null;
            NavKey = "";

            // The navmesh as the game made it, before the first edit touches it
            MapDump.Auto(location);

            if (!Settings.MapEdits.Value)
            {
                return;
            }

            string path = FilePath(location);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                _fileTime = File.GetLastWriteTimeUtc(path);
                Current = JsonConvert.DeserializeObject<EditsFile>(File.ReadAllText(path)) ?? new EditsFile();
            }
            catch (Exception e)
            {
                Log.Warn($"LR Studio: the edits of {location} are unreadable: {e.Message}");
                return;
            }

            List<string> report = new List<string>();
            List<int> agentTypes = AgentTypes();
            int patches = 0, gates = 0;
            foreach (NavPatch patch in Current.Patches.Where(p => p.Enabled))
            {
                try
                {
                    string line = BuildPatch(patch, agentTypes, ref gates);
                    if (line != null)
                    {
                        patches++;
                    }
                    else
                    {
                        report.Add($"patch {patch.Name ?? patch.Id}: not built");
                    }
                }
                catch (Exception e)
                {
                    report.Add($"patch {patch.Name ?? patch.Id}: {e.Message}");
                }
            }

            int blockers = 0;
            foreach (Blocker blocker in Current.Blockers.Where(b => b.Enabled && b.C != null && b.Size != null))
            {
                GameObject go = new GameObject("LR Blocker " + (blocker.Name ?? blocker.Id));
                go.transform.SetPositionAndRotation(V(blocker.C), Quaternion.Euler(0f, blocker.Yaw, 0f));
                NavMeshObstacle obstacle = go.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.center = Vector3.zero;
                obstacle.size = V(blocker.Size);
                obstacle.carveOnlyStationary = true;
                obstacle.carving = true;
                _blockers.Add(go);
                blockers++;
            }

            int links = 0;
            foreach (EditLink link in Current.Links.Where(l => l.Enabled && l.A != null && l.B != null))
            {
                if (AddLinks(V(link.A), V(link.B), link.Cost, link.TwoWay, agentTypes))
                {
                    links++;
                }
                else
                {
                    report.Add($"link {link.Name ?? link.Id}: an end is not on the navmesh");
                }
            }

            NavKey = Hash(JsonConvert.SerializeObject(new object[]
            {
                Current.Patches.Where(p => p.Enabled), Current.Blockers.Where(b => b.Enabled), Current.Links.Where(l => l.Enabled),
            }));
            if (patches + blockers + links == 0)
            {
                NavKey = "";
            }

            Log.Info($"LR Studio: edits of {location} (revision {Current.Revision}): {patches} navmesh patch(es) ({gates} gate link(s)), {blockers} blocker(s), {links} link(s), " +
                $"{Current.Routes.Count(r => r.Points.Count >= 2)} climb(s), {Current.Paths.Count(p => p.Enabled)} bot path(s), {Current.Spots.Count(s => s.Enabled)} place(s)" +
                (report.Count > 0 ? "; problems: " + string.Join("; ", report) : ""));
            EditsOverlay.Rebuild();
        }

        public static void Remove()
        {
            foreach (NavMeshDataInstance data in _datas)
            {
                try
                {
                    data.Remove();
                }
                catch
                {
                    // gone with the map
                }
            }

            foreach (NavMeshLinkInstance link in _links)
            {
                try
                {
                    link.Remove();
                }
                catch
                {
                    // gone with the map
                }
            }

            foreach (GameObject go in _blockers)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }

            _datas.Clear();
            _links.Clear();
            _blockers.Clear();
            EditsOverlay.Clear();
            _location = null;
            Current = null;
            NavKey = "";
        }

        /// <summary>Climbs drawn in Studio, joined to the recorded ones.</summary>
        public static IEnumerable<Route> RoutesFor(string location) =>
            Current != null && _location == location ? Current.Routes.Where(r => r.Points != null && r.Points.Count >= 2) : Enumerable.Empty<Route>();

        /// <summary>Extra places for the exploration quests (they join the regions they lie in, like places learned from the player's trail).</summary>
        public static List<Poi> SpotsFor(string location)
        {
            List<Poi> result = new List<Poi>();
            if (Current == null || _location != location)
            {
                return result;
            }

            foreach (EditSpot s in Current.Spots.Where(x => x.Enabled && x.P != null && x.P.Length >= 3))
            {
                // Learned places count as loot from weight 1.2; a "hold" place is only walked to and looked around
                float w = s.Kind == "hold" || s.Kind == "camp" ? 0.6f : Mathf.Max(1.2f, s.W);
                result.Add(new Poi { X = s.P[0], Y = s.P[1], Z = s.P[2], Kind = PoiKind.Learned, W = w });
            }

            return result;
        }

        public static IEnumerable<BotPath> PathsFor(string location) =>
            Current != null && _location == location ? Current.Paths.Where(p => p.Enabled && p.Points != null && p.Points.Count >= 2) : Enumerable.Empty<BotPath>();

        // ---------------- run time: reload when the file changes (Studio saved), hot keys ----------------

        public static void Tick()
        {
            if (_location == null || !Singleton<GameWorld>.Instantiated)
            {
                return;
            }

            if (Settings.MapEditsOverlayKey.Value.IsDown())
            {
                Settings.MapEditsOverlay.Value = !Settings.MapEditsOverlay.Value;
                Notify("Overlay of the map edits: " + (Settings.MapEditsOverlay.Value ? "on" : "off"));
            }

            if (Settings.MapEditsOverlay.Value != _overlayShown)
            {
                _overlayShown = Settings.MapEditsOverlay.Value;
                EditsOverlay.Rebuild();
            }

            bool force = Settings.MapEditsReloadKey.Value.IsDown();
            if (!force && (!Settings.MapEditsAutoReload.Value || Time.realtimeSinceStartup < _nextWatch))
            {
                return;
            }

            _nextWatch = Time.realtimeSinceStartup + 2f;
            string path = FilePath(_location);
            bool changed = File.Exists(path) ? File.GetLastWriteTimeUtc(path) != _fileTime : Current != null;
            if (!force && !changed)
            {
                return;
            }

            // The edits are applied by Routes (which also puts the climbs back); only reachability and the overlay are redone here
            Routes.Reload(_location);
            Notify(Current == null ? "Map edits: none for this map" : $"Map edits reloaded (revision {Current.Revision})");
        }

        // ---------------- building ----------------

        private static List<int> AgentTypes()
        {
            List<int> agentTypes = new List<int>();
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                agentTypes.Add(NavMesh.GetSettingsByIndex(i).agentTypeID);
            }

            if (agentTypes.Count == 0)
            {
                agentTypes.Add(0);
            }

            return agentTypes.Distinct().ToList();
        }

        private static string BuildPatch(NavPatch patch, List<int> agentTypes, ref int gateLinks)
        {
            if (patch.Verts.Count < 3 || patch.Tris.Count < 3 || patch.Tris.Count % 3 != 0)
            {
                return null;
            }

            Vector3[] verts = patch.Verts.Select(V).ToArray();
            int[] tris = patch.Tris.ToArray();
            for (int i = 0; i < tris.Length; i += 3)
            {
                // The navmesh is built over the faces that look up (Unity: clockwise seen from the front)
                Vector3 n = Vector3.Cross(verts[tris[i + 1]] - verts[tris[i]], verts[tris[i + 2]] - verts[tris[i]]);
                if (n.y < 0f)
                {
                    int t = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = t;
                }
            }

            Mesh mesh = new Mesh { vertices = verts, triangles = tris };
            mesh.RecalculateBounds();
            Bounds bounds = mesh.bounds;
            bounds.Expand(new Vector3(6f, 6f, 6f));

            int built = 0;
            foreach (int agentType in agentTypes)
            {
                NavMeshBuildSettings settings = NavMesh.GetSettingsByID(agentType);
                settings.minRegionArea = 0f;
                List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>
                {
                    new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = mesh, transform = Matrix4x4.identity, area = 0 },
                };
                NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (data == null)
                {
                    continue;
                }

                NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);
                if (instance.valid)
                {
                    _datas.Add(instance);
                    built++;
                }
            }

            if (built == 0)
            {
                return null;
            }

            // Where the patch meets the old navmesh a link is added only if the two do not already join up
            NavMeshPath path = new NavMeshPath();
            foreach (PatchGate gate in patch.Gates ?? new List<PatchGate>())
            {
                if (gate.O == null || gate.I == null)
                {
                    continue;
                }

                if (!NavMesh.SamplePosition(V(gate.O), out NavMeshHit o, 1.0f, NavMesh.AllAreas) || !NavMesh.SamplePosition(V(gate.I), out NavMeshHit i, 1.0f, NavMesh.AllAreas))
                {
                    continue;
                }

                float straight = Vector3.Distance(o.position, i.position);
                bool joined = NavMesh.CalculatePath(o.position, i.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete && Length(path) <= straight * 2f + 2f;
                if (!joined && AddLinks(o.position, i.position, 1f, true, agentTypes))
                {
                    gateLinks++;
                }
            }

            return patch.Name ?? patch.Id ?? "patch";
        }

        private static bool AddLinks(Vector3 a, Vector3 b, float cost, bool twoWay, List<int> agentTypes)
        {
            if (!NavMesh.SamplePosition(a, out NavMeshHit ha, 1.2f, NavMesh.AllAreas) || !NavMesh.SamplePosition(b, out NavMeshHit hb, 1.2f, NavMesh.AllAreas))
            {
                return false;
            }

            bool any = false;
            foreach (int agentType in agentTypes)
            {
                NavMeshLinkInstance link = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = ha.position,
                    endPosition = hb.position,
                    width = 0f,
                    costModifier = Mathf.Max(0f, cost),
                    bidirectional = twoWay,
                    area = 0,
                    agentTypeID = agentType,
                });
                if (link.valid)
                {
                    _links.Add(link);
                    any = true;
                }
            }

            return any;
        }

        private static float Length(NavMeshPath path)
        {
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            return length;
        }

        internal static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);

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
            Log.Info("LR Studio: " + message);
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
