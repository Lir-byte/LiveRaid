using System.Collections.Generic;
using System.Linq;
using LiveRaid.Client.Raid;
using UnityEngine;

namespace LiveRaid.Client.Studio
{
    /// <summary>
    /// Shows the map edits in the world (F12 → "Show the edits in the world", or the hot key): patches green, blockers red, links yellow, climbs orange,
    /// bot paths cyan, extra places magenta. Meant for the free camera, to see what Studio's file did to the map.
    /// </summary>
    public static class EditsOverlay
    {
        private static readonly List<GameObject> _objects = new List<GameObject>();
        private const float Lift = 0.06f;

        public static void Clear()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _objects.Clear();
        }

        public static void Rebuild()
        {
            Clear();
            EditsFile e = MapEdits.Current;
            if (e == null || !Settings.MapEditsOverlay.Value)
            {
                return;
            }

            try
            {
                // Patches: filled
                List<Vector3> fill = new List<Vector3>();
                List<int> fillIdx = new List<int>();
                List<Vector3> lines = new List<Vector3>();
                foreach (NavPatch p in e.Patches.Where(x => x.Enabled))
                {
                    int baseIndex = fill.Count;
                    foreach (float[] v in p.Verts)
                    {
                        fill.Add(MapEdits.V(v) + Vector3.up * Lift);
                    }

                    foreach (int i in p.Tris)
                    {
                        fillIdx.Add(baseIndex + i);
                    }

                    for (int i = 0; i + 2 < p.Tris.Count; i += 3)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            lines.Add(fill[baseIndex + p.Tris[i + k]]);
                            lines.Add(fill[baseIndex + p.Tris[i + (k + 1) % 3]]);
                        }
                    }
                }

                if (fill.Count > 0)
                {
                    Add("LR edits: patches", Mesh(fill, fillIdx, MeshTopology.Triangles), new Color(0.1f, 0.9f, 0.3f, 0.35f));
                    Add("LR edits: patch edges", Mesh(lines, Seq(lines.Count), MeshTopology.Lines), new Color(0.2f, 1f, 0.4f, 1f));
                }

                // Blockers: box edges
                lines = new List<Vector3>();
                foreach (Blocker b in e.Blockers.Where(x => x.Enabled && x.C != null && x.Size != null))
                {
                    Quaternion rot = Quaternion.Euler(0f, b.Yaw, 0f);
                    Vector3 c = MapEdits.V(b.C), h = MapEdits.V(b.Size) * 0.5f;
                    Vector3[] corners = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                    {
                        corners[i] = c + rot * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
                    }

                    int[] edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
                    foreach (int i in edges)
                    {
                        lines.Add(corners[i]);
                    }
                }

                Lines("LR edits: blockers", lines, new Color(1f, 0.2f, 0.2f, 1f));

                lines = new List<Vector3>();
                foreach (EditLink l in e.Links.Where(x => x.Enabled && x.A != null && x.B != null))
                {
                    lines.Add(MapEdits.V(l.A) + Vector3.up * 0.3f);
                    lines.Add(MapEdits.V(l.B) + Vector3.up * 0.3f);
                }

                Lines("LR edits: links", lines, new Color(1f, 0.9f, 0.1f, 1f));

                lines = new List<Vector3>();
                foreach (Route r in e.Routes)
                {
                    for (int i = 1; i < r.Points.Count; i++)
                    {
                        lines.Add(r.Points[i - 1].Pos + Vector3.up * 0.2f);
                        lines.Add(r.Points[i].Pos + Vector3.up * 0.2f);
                    }
                }

                Lines("LR edits: climbs", lines, new Color(1f, 0.55f, 0.1f, 1f));

                lines = new List<Vector3>();
                foreach (BotPath p in e.Paths.Where(x => x.Enabled && x.Points != null))
                {
                    for (int i = 1; i < p.Points.Count; i++)
                    {
                        lines.Add(MapEdits.V(p.Points[i - 1].P) + Vector3.up * 0.4f);
                        lines.Add(MapEdits.V(p.Points[i].P) + Vector3.up * 0.4f);
                    }
                }

                Lines("LR edits: bot paths", lines, new Color(0.1f, 0.9f, 1f, 1f));

                lines = new List<Vector3>();
                foreach (EditSpot s in e.Spots.Where(x => x.Enabled && x.P != null))
                {
                    Vector3 c = MapEdits.V(s.P) + Vector3.up * 0.5f;
                    lines.Add(c + Vector3.left * 0.5f);
                    lines.Add(c + Vector3.right * 0.5f);
                    lines.Add(c + Vector3.forward * 0.5f);
                    lines.Add(c + Vector3.back * 0.5f);
                    lines.Add(c);
                    lines.Add(c + Vector3.up * 2f);
                }

                Lines("LR edits: places", lines, new Color(1f, 0.2f, 0.9f, 1f));
            }
            catch (System.Exception ex)
            {
                Log.Warn("LR Studio: overlay failed: " + ex.Message);
                Clear();
            }
        }

        private static List<int> Seq(int n) => Enumerable.Range(0, n).ToList();

        private static Mesh Mesh(List<Vector3> verts, List<int> indices, MeshTopology topology)
        {
            Mesh m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetIndices(indices, topology, 0);
            m.RecalculateBounds();
            return m;
        }

        private static void Lines(string name, List<Vector3> lines, Color color)
        {
            if (lines.Count >= 2)
            {
                Add(name, Mesh(lines, Seq(lines.Count), MeshTopology.Lines), color);
            }
        }

        private static void Add(string name, Mesh mesh, Color color)
        {
            GameObject go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();

            // First shader the build still has; alpha for the fill
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Standard");
            if (shader != null)
            {
                renderer.sharedMaterial = new Material(shader);
            }

            renderer.material.color = color;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _objects.Add(go);
        }
    }
}
