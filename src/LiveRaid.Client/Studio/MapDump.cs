using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Studio
{
    /// <summary>
    /// Writes the navmesh of the current map for LiveRaid Studio: BepInEx/plugins/LiveRaid/studio/dump/&lt;map&gt;.mesh.bin (triangles) and
    /// &lt;map&gt;.meta.json (doors, exits, spawn points, bounds). Done once per map, before any edit is applied - the dump is the map as the game made it.
    /// </summary>
    public static class MapDump
    {
        public static string Dir => Path.Combine(Plugin.PluginDir, "studio", "dump");

        public static string MeshPath(string location) => Path.Combine(Dir, location + ".mesh.bin");

        public static string MetaPath(string location) => Path.Combine(Dir, location + ".meta.json");

        public static bool Exists(string location) => File.Exists(MeshPath(location)) && File.Exists(MetaPath(location));

        private class Meta
        {
            public string Format = "liveraid-dump/1";
            public string Map;
            public string Saved;
            public string Mod;
            public int Verts, Tris;
            public float[] Min, Max;
            public List<object> Doors = new List<object>();
            public List<object> Exfils = new List<object>();
            public List<object> Spawns = new List<object>();
        }

        /// <summary>Dump when the map has none yet, or the player asked for a fresh one (Redump). Safe to call every raid.</summary>
        public static void Auto(string location)
        {
            bool fresh = Settings.MapEditsRedump.Value;
            if (!Settings.MapEditsDump.Value && !fresh)
            {
                return;
            }

            if (!fresh && Exists(location))
            {
                return;
            }

            try
            {
                Write(location);
                if (fresh)
                {
                    Settings.MapEditsRedump.Value = false;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Studio: could not dump the navmesh of {location}: {e.Message}");
            }
        }

        private static void Write(string location)
        {
            NavMeshTriangulation t = NavMesh.CalculateTriangulation();
            if (t.vertices == null || t.vertices.Length == 0 || t.indices == null || t.indices.Length < 3)
            {
                Log.Warn($"LR Studio: the navmesh of {location} is empty, nothing dumped");
                return;
            }

            // Weld vertices at 1 cm: tiles of the navmesh list their shared corners separately
            Dictionary<long, int> welded = new Dictionary<long, int>();
            List<Vector3> verts = new List<Vector3>();
            int[] remap = new int[t.vertices.Length];
            for (int i = 0; i < t.vertices.Length; i++)
            {
                Vector3 v = t.vertices[i];
                long key = ((long)Mathf.RoundToInt(v.x * 100f) & 0x1FFFFF) << 42 | ((long)Mathf.RoundToInt(v.y * 100f) & 0x1FFFFF) << 21 | ((long)Mathf.RoundToInt(v.z * 100f) & 0x1FFFFF);
                if (!welded.TryGetValue(key, out int index))
                {
                    index = verts.Count;
                    welded[key] = index;
                    verts.Add(v);
                }

                remap[i] = index;
            }

            List<int> indices = new List<int>();
            List<byte> areas = new List<byte>();
            for (int i = 0; i + 2 < t.indices.Length; i += 3)
            {
                int a = remap[t.indices[i]], b = remap[t.indices[i + 1]], c = remap[t.indices[i + 2]];
                if (a == b || b == c || a == c)
                {
                    continue;
                }

                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
                int area = t.areas != null && i / 3 < t.areas.Length ? t.areas[i / 3] : 0;
                areas.Add((byte)Mathf.Clamp(area, 0, 255));
            }

            Directory.CreateDirectory(Dir);
            using (BinaryWriter w = new BinaryWriter(File.Create(MeshPath(location))))
            {
                w.Write(new[] { (byte)'L', (byte)'R', (byte)'M', (byte)'E', (byte)'S', (byte)'H', (byte)'1', (byte)0 });
                w.Write((uint)verts.Count);
                w.Write((uint)indices.Count);
                foreach (Vector3 v in verts)
                {
                    w.Write(v.x);
                    w.Write(v.y);
                    w.Write(v.z);
                }

                foreach (int i in indices)
                {
                    w.Write((uint)i);
                }

                w.Write(areas.ToArray());
            }

            Vector3 min = verts[0], max = verts[0];
            foreach (Vector3 v in verts)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            Meta meta = new Meta
            {
                Map = location,
                Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Mod = Plugin.VersionText,
                Verts = verts.Count,
                Tris = indices.Count / 3,
                Min = new[] { min.x, min.y, min.z },
                Max = new[] { max.x, max.y, max.z },
            };
            Collect(meta);
            File.WriteAllText(MetaPath(location), JsonConvert.SerializeObject(meta, Formatting.Indented));
            Log.Info($"LR Studio: the navmesh of {location} is dumped for LiveRaid Studio ({meta.Verts} vertices, {meta.Tris} triangles, {meta.Doors.Count} doors, {meta.Exfils.Count} exits, {meta.Spawns.Count} spawn points)");
        }

        private static void Collect(Meta meta)
        {
            try
            {
                HashSet<string> seen = new HashSet<string>();
                foreach (Door door in UnityEngine.Object.FindObjectsOfType<Door>())
                {
                    if (door == null || string.IsNullOrEmpty(door.Id) || !seen.Add(door.Id))
                    {
                        continue;
                    }

                    Vector3 p = door.transform.position;
                    meta.Doors.Add(new { door.Id, X = p.x, Y = p.y, Z = p.z, State = door.DoorState.ToString(), Key = door.KeyId ?? "" });
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Studio: doors not dumped: {e.Message}");
            }

            try
            {
                foreach (ExfiltrationPoint point in UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>())
                {
                    if (point == null)
                    {
                        continue;
                    }

                    Vector3 p = point.transform.position;
                    string kind = point is SharedExfiltrationPoint ? "shared" : point is ScavExfiltrationPoint ? "scav" : "pmc";
                    meta.Exfils.Add(new { Name = point.Settings?.Name ?? point.name, Kind = kind, X = p.x, Y = p.y, Z = p.z });
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Studio: exits not dumped: {e.Message}");
            }

            try
            {
                var spawnPoints = Singleton<GameWorld>.Instance?.GetComponent<QuestingBots.Components.LocationData>()?.CurrentLocation?.SpawnPointParams;
                if (spawnPoints != null)
                {
                    foreach (var sp in spawnPoints)
                    {
                        Vector3 p = sp.Position;
                        meta.Spawns.Add(new { X = p.x, Y = p.y, Z = p.z });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"LR Studio: spawn points not dumped: {e.Message}");
            }
        }
    }
}
