using System;
using EFT;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveRaid.Client.Studio;
using Newtonsoft.Json;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// After a raid, from the player's trail, into studio/edits/&lt;map&gt;.json (the file LiveRaid Studio edits; applies from the next raid):
    ///  - LINKS ("auto-..."): the player walked from one piece of the bots' navmesh to another that is not joined to it (a neck of zero width, a ledge, a
    ///    doorstep the mesh was baked across - Reserve: the hermetic lever and the D-2 shaft were islands of 20 and 96 triangles);
    ///  - NAVMESH PATCHES ("auto-mesh-..."): the player walked where there is no navmesh at all (stairs, a ramp, a plank, a passage the bake missed) and came
    ///    out on the mesh again within 40 m: a strip 2.4 m wide along his steps, from the mesh to the mesh, built into real navmesh in the game on loading
    ///    (as a patch drawn in Studio is). Jumps and falls (an airborne step), steep (over 50 degrees) and long drops are left to the shortcuts (routes).
    /// Nothing is removed or changed there: only new items, a few a raid, never twice for the same place. tools/sync_map_edits.py takes them from the
    /// game into the project (and the release).
    /// </summary>
    public static class AutoLinks
    {
        private const int MinIsland = 8;
        private const float MaxStep = 15f;
        private const int MaxPerRaid = 8;
        private const int MaxPerMap = 80;
        private const int MaxPatchesPerRaid = 3;
        private const int MaxPatchesPerMap = 20;
        private const float StripHalfWidth = 1.2f;

        public static int Written { get; private set; }
        public static int Patches { get; private set; }
        public static int Walled { get; private set; }

        public static string Summary => $"from the player's trail: {Written} navmesh links and {Patches} navmesh patches written, {Walled} link(s) refused (a wall between the ends)";

        public static void Clear() => Written = Patches = Walled = 0;

        public static void Run(string location, List<List<(Vector3 pos, bool air, bool onNav)>> trails)
        {
            try
            {
                if (string.IsNullOrEmpty(location) || trails == null || trails.Count == 0 || !LongPath.EnsureBuilt())
                {
                    return;
                }

                string path = MapEdits.FilePath(location);
                EditsFile file = File.Exists(path) ? JsonConvert.DeserializeObject<EditsFile>(File.ReadAllText(path)) ?? new EditsFile() : new EditsFile();
                file.Map = file.Map ?? location;
                int links = FindLinks(file, trails);
                int patches = FindPatches(file, trails);
                if (links + patches == 0)
                {
                    return;
                }

                file.Revision++;
                file.Saved = DateTime.UtcNow.ToString("o");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path))
                {
                    string backup = Path.Combine(Path.GetDirectoryName(path), "backup");
                    Directory.CreateDirectory(backup);
                    File.Copy(path, Path.Combine(backup, $"{location}.auto-{DateTime.Now:yyyyMMdd_HHmmss}.json"), true);
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
                Written += links;
                Patches += patches;
                Log.Info($"LR AutoLinks: {links} new link(s) and {patches} new navmesh patch(es) written to studio/edits/{location}.json from your trail " +
                         "(they apply from the next raid; tools/sync_map_edits.py carries them into the project)");
            }
            catch (Exception e)
            {
                Diag.Report("AutoLinks", e);
            }
        }

        private static int FindLinks(EditsFile file, List<List<(Vector3 pos, bool air, bool onNav)>> trails)
        {
            List<(Vector3 a, Vector3 b, int sizeA, int sizeB, float walked, int idA, int idB)> found = new List<(Vector3, Vector3, int, int, float, int, int)>();
            foreach (List<(Vector3 pos, bool air, bool onNav)> trail in trails)
            {
                bool have = false;
                int prevId = 0, prevSize = 0;
                Vector3 prevPos = default, prevCentre = default;
                foreach ((Vector3 p, bool _, bool _) in trail)
                {
                    if (!LongPath.Probe(p, 1.5f, out int id, out int size, out Vector3 centre))
                    {
                        continue;
                    }

                    float walked = have ? Vector3.Distance(prevPos, p) : 0f;
                    if (have && id != prevId && walked <= MaxStep && Math.Min(size, prevSize) >= MinIsland)
                    {
                        found.Add((prevCentre, centre, prevSize, size, walked, prevId, id));
                    }

                    have = true;
                    prevId = id;
                    prevSize = size;
                    prevPos = p;
                    prevCentre = centre;
                }
            }

            int fresh = 0;
            foreach (var f0 in found.OrderBy(x => x.walked))
            {
                if (fresh >= MaxPerRaid || file.Links.Count >= MaxPerMap)
                {
                    break;
                }

                // The two centres the walk touched may lie on either side of a wall (a link drawn diagonally across the corner of a building: bots ran at the
                // wall and stood there). The ends are chosen among the centres of both pieces around the place where they meet: the nearest pair with
                // nothing solid between them (at knee and waist height) - through a doorway, along its axis
                var f = f0;
                if (!OpenPair(f0.a, f0.b, f0.idA, f0.idB, out Vector3 pa, out Vector3 pb))
                {
                    Walled++;
                    continue;
                }

                f.a = pa;
                f.b = pb;

                // Not twice for one place: a link (any, drawn by hand too) with both ends within 1.5 m of these
                if (file.Links.Any(l => l.A != null && l.B != null && ((Near(l.A, f.a) && Near(l.B, f.b)) || (Near(l.A, f.b) && Near(l.B, f.a)))))
                {
                    continue;
                }

                file.Links.Add(new EditLink
                {
                    Id = $"auto-{Mathf.RoundToInt(f.a.x)}_{Mathf.RoundToInt(f.a.y)}_{Mathf.RoundToInt(f.a.z)}",
                    Name = $"auto: your walk joined two pieces of mesh ({f.sizeA} and {f.sizeB} triangles) near ({f.a.x:0}, {f.a.y:0}, {f.a.z:0})",
                    A = R(f.a),
                    B = R(f.b),
                    Cost = 1f,
                    TwoWay = true,
                });
                fresh++;
            }

            return fresh;
        }

        private static bool Clear2(Vector3 a, Vector3 b) =>
            !Physics.Linecast(a + Vector3.up * 0.45f, b + Vector3.up * 0.45f, LayersMaskController.HighPolyWithTerrainMask)
            && !Physics.Linecast(a + Vector3.up * 1.1f, b + Vector3.up * 1.1f, LayersMaskController.HighPolyWithTerrainMask);

        private static bool OpenPair(Vector3 a0, Vector3 b0, int idA, int idB, out Vector3 a, out Vector3 b)
        {
            a = a0;
            b = b0;
            Vector3 mid = (a0 + b0) * 0.5f;
            List<Vector3> la = LongPath.CentresOf(idA, mid, 5f), lb = LongPath.CentresOf(idB, mid, 5f);
            la.Add(a0);
            lb.Add(b0);
            float best = float.MaxValue;
            bool any = false;
            foreach (Vector3 x in la)
            {
                foreach (Vector3 y in lb)
                {
                    float d = Vector3.Distance(x, y);
                    if (d < 0.9f || d > 6f || d >= best || Mathf.Abs(x.y - y.y) > 1.2f || !Clear2(x, y))
                    {
                        continue;
                    }

                    best = d;
                    a = x;
                    b = y;
                    any = true;
                }
            }

            return any;
        }

        /// <summary>Runs of the trail with no mesh under it, from the mesh to the mesh: a strip of mesh along each.</summary>
        private static int FindPatches(EditsFile file, List<List<(Vector3 pos, bool air, bool onNav)>> trails)
        {
            List<List<Vector3>> runs = new List<List<Vector3>>();
            foreach (List<(Vector3 pos, bool air, bool onNav)> trail in trails)
            {
                Vector3? lastValid = null;
                List<Vector3> run = null;
                bool airborne = false;
                foreach ((Vector3 p, bool air, bool onNav) in trail)
                {
                    // The trail's own flag: a navmesh point within 1 m of the step (the nearest triangle CENTRE can be far on a big floor triangle)
                    if (onNav)
                    {
                        if (run != null && run.Count > 0 && lastValid.HasValue && !airborne)
                        {
                            List<Vector3> line = new List<Vector3> { lastValid.Value };
                            line.AddRange(run);
                            line.Add(p);
                            runs.Add(line);
                        }

                        run = null;
                        airborne = false;
                        lastValid = p;
                        continue;
                    }

                    if (!lastValid.HasValue)
                    {
                        continue;
                    }

                    run = run ?? new List<Vector3>();
                    run.Add(p);
                    airborne |= air;
                    if (run.Count > 40)
                    {
                        run = null;
                        lastValid = null;
                    }
                }
            }

            int fresh = 0;
            foreach (List<Vector3> line in runs)
            {
                if (fresh >= MaxPatchesPerRaid || file.Patches.Count >= MaxPatchesPerMap)
                {
                    break;
                }

                float length = 0f;
                bool steep = false;
                for (int i = 1; i < line.Count; i++)
                {
                    Vector3 d = line[i] - line[i - 1];
                    float flat = new Vector2(d.x, d.z).magnitude;
                    length += d.magnitude;
                    if (Mathf.Abs(d.y) > Mathf.Max(0.3f, flat * 1.2f))
                    {
                        steep = true;
                    }
                }

                if (steep || length < 2.5f || length > 40f || Mathf.Abs(line[line.Count - 1].y - line[0].y) > 6f || line.Any(q => TrainArea.Contains(q, 15f)))
                {
                    continue;
                }

                // Not twice for one place: a patch (any, drawn by hand too) with vertices near both ends of this walk
                Vector3 s = line[0], e = line[line.Count - 1];
                if (file.Patches.Any(pt => pt.Verts.Any(v => Near(v, s, 1.8f)) && pt.Verts.Any(v => Near(v, e, 1.8f))))
                {
                    continue;
                }

                file.Patches.Add(Strip(line, length));
                fresh++;
            }

            return fresh;
        }

        private static NavPatch Strip(List<Vector3> line, float length)
        {
            NavPatch patch = new NavPatch
            {
                Id = $"auto-mesh-{Mathf.RoundToInt(line[0].x)}_{Mathf.RoundToInt(line[0].y)}_{Mathf.RoundToInt(line[0].z)}",
                Name = $"auto: you walked {length:0} m where the bots' mesh had none, near ({line[0].x:0}, {line[0].y:0}, {line[0].z:0})",
            };
            for (int i = 0; i < line.Count; i++)
            {
                Vector3 dir = Vector3.zero;
                if (i > 0)
                {
                    dir += Flat(line[i] - line[i - 1]);
                }

                if (i < line.Count - 1)
                {
                    dir += Flat(line[i + 1] - line[i]);
                }

                dir = dir.sqrMagnitude < 1e-6f ? Vector3.forward : dir.normalized;
                Vector3 side = new Vector3(-dir.z, 0f, dir.x) * StripHalfWidth;
                patch.Verts.Add(R(line[i] + side));
                patch.Verts.Add(R(line[i] - side));
                if (i > 0)
                {
                    int a = 2 * (i - 1), b = a + 1, c = 2 * i, d = c + 1;
                    patch.Tris.AddRange(new[] { a, b, c, b, d, c });
                }
            }

            // The strip starts and ends on the old mesh: gates join it there (a link only when no short way joins them already)
            patch.Gates.Add(new PatchGate { O = R(line[0]), I = R(Along(line, Mathf.Min(2.5f, length * 0.4f), false)) });
            patch.Gates.Add(new PatchGate { O = R(line[line.Count - 1]), I = R(Along(line, Mathf.Min(2.5f, length * 0.4f), true)) });
            return patch;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.normalized;
        }

        /// <summary>A point of the walk this far from its start (or from its end).</summary>
        private static Vector3 Along(List<Vector3> line, float distance, bool fromEnd)
        {
            List<Vector3> l = fromEnd ? Enumerable.Reverse(line).ToList() : line;
            float left = distance;
            for (int i = 1; i < l.Count; i++)
            {
                float seg = Vector3.Distance(l[i - 1], l[i]);
                if (seg >= left)
                {
                    return Vector3.Lerp(l[i - 1], l[i], seg < 1e-4f ? 0f : left / seg);
                }

                left -= seg;
            }

            return l[l.Count - 1];
        }

        private static float[] R(Vector3 v) => new[] { (float)Math.Round(v.x, 2), (float)Math.Round(v.y, 2), (float)Math.Round(v.z, 2) };

        private static bool Near(float[] a, Vector3 b, float within = 1.5f) => a.Length >= 3 && (new Vector3(a[0], a[1], a[2]) - b).sqrMagnitude < within * within;
    }
}
