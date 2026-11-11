using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using LiveRaid.Client.Studio;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// A way across the map that Unity's own path search gives up on. NavMesh.CalculatePath keeps a pool of about two thousand nodes: a long way with
    /// a detour (Reserve: the D-2 power lever lies 7 m under the floor a bot stands on, and the only way down is a ramp 100 m off - 300 m round, some
    /// ten thousand polygons searched) ends as a "partial" path that stops right above the target. Checked on the map's dump: the search from the roof
    /// above the lever and from the lobby both need ~10 000 polygons, from the foot of the ramp 230.
    ///
    /// Here the search is our own over the map's triangles (NavMesh.CalculateTriangulation) plus the links of the map edits. One search from the TARGET
    /// (Dijkstra) makes a "field" that gives every bot's way to it; a way is read off it as via-points about 25 m apart, and a bot walks them one after
    /// another - every leg is short enough for Unity.
    ///
    /// Everything heavy runs on a worker thread (1.4.4): in the raid of 2026-11-11 the first call built the graph on the main thread and stood the game
    /// still for 9.1 s (ExtractPlanner tick, 06:51). Until the graph or the field is ready the answer is "not yet" (false, deferred) and callers ask again.
    /// </summary>
    public static class LongPath
    {
        private static Vector3[] _cent;
        private static List<int>[] _adj;
        private static Dictionary<long, List<int>> _grid;
        private static volatile bool _built;
        private static volatile bool _building;
        private static volatile bool _failed;
        private static int _builtRevision = -1;
        private static int[] _comp;
        private static int[] _compSize;

        private class Field
        {
            public float[] Dist;
            public int[] Next;
        }

        private static readonly ConcurrentDictionary<int, Field> _fields = new ConcurrentDictionary<int, Field>();
        private static readonly ConcurrentDictionary<int, bool> _fieldTasks = new ConcurrentDictionary<int, bool>();
        private static readonly Dictionary<string, (float at, Vector3[] via, float length)> _cache = new Dictionary<string, (float, Vector3[], float)>();
        private static int _generation;

        private class Heap
        {
            private readonly List<(float f, int id)> _a = new List<(float, int)>();

            public int Count => _a.Count;

            public void Push(float f, int id)
            {
                _a.Add((f, id));
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].f <= _a[i].f)
                    {
                        break;
                    }

                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            public (float f, int id) Pop()
            {
                (float f, int id) top = _a[0];
                int last = _a.Count - 1;
                _a[0] = _a[last];
                _a.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].f < _a[m].f)
                    {
                        m = l;
                    }

                    if (r < _a.Count && _a[r].f < _a[m].f)
                    {
                        m = r;
                    }

                    if (m == i)
                    {
                        break;
                    }

                    (_a[m], _a[i]) = (_a[i], _a[m]);
                    i = m;
                }

                return top;
            }
        }

        public static int Searches { get; private set; }
        public static int Found { get; private set; }

        /// <summary>The last TryFind that found nothing was "not yet" (the graph or the field is being made), not "no way".</summary>
        public static bool LastDeferred { get; private set; }

        public static string Summary => $"own long-way search: {Searches} fields made, {Found} ways found" + (_built ? $" ({_cent.Length} triangles)" : _failed ? " (could not be built)" : _building ? " (still building)" : "");

        public static void Clear()
        {
            _generation++;
            _cent = null;
            _adj = null;
            _grid = null;
            _built = _failed = _building = false;
            _builtRevision = -1;
            _comp = null;
            _compSize = null;
            _cache.Clear();
            _fields.Clear();
            _fieldTasks.Clear();
            Searches = Found = 0;
        }

        private static long Cell(float x, float z) => ((long)(int)Mathf.Floor(x / 4f) << 32) ^ (uint)(int)Mathf.Floor(z / 4f);

        private static long Q(Vector3 v) => ((long)Mathf.RoundToInt(v.x * 100f) * 73856093L) ^ ((long)Mathf.RoundToInt(v.y * 100f) * 19349663L) ^ ((long)Mathf.RoundToInt(v.z * 100f) * 83492791L);

        /// <summary>
        /// The graph is ready (false: not yet - it is being built on a worker thread, or the mesh is not there). Call it early in a raid and it is ready when
        /// first needed; the map edits' revision changing starts a new build.
        /// </summary>
        public static bool EnsureBuilt()
        {
            int revision = MapEdits.Current?.Revision ?? 0;
            if (_built && _builtRevision == revision)
            {
                return true;
            }

            if (_building || (_failed && _builtRevision == revision))
            {
                return false;
            }

            StartBuild(revision);
            return false;
        }

        private static void StartBuild(int revision)
        {
            _building = true;
            _failed = false;
            try
            {
                // The mesh itself only the main thread may read (a copy); the rest is arrays and runs elsewhere
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                Vector3[] verts = tri.vertices;
                int[] idx = tri.indices;
                List<(Vector3 a, Vector3 b, bool two)> links = new List<(Vector3, Vector3, bool)>();
                EditsFile edits = MapEdits.Current;
                if (edits != null)
                {
                    foreach (EditLink l in edits.Links)
                    {
                        if (l.Enabled && l.A != null && l.B != null && l.A.Length >= 3 && l.B.Length >= 3)
                        {
                            links.Add((new Vector3(l.A[0], l.A[1], l.A[2]), new Vector3(l.B[0], l.B[1], l.B[2]), l.TwoWay));
                        }
                    }

                    // A patch's gates join it to the old mesh in the game (a link only when no short way does): in the graph its triangles had no shared edge
                    // with anything, and the way field to the D-2 button (inside a patch) reached 11 triangles of 96 942 (raid 2026-11-11 05:01)
                    foreach (NavPatch patch in edits.Patches)
                    {
                        if (!patch.Enabled || patch.Gates == null)
                        {
                            continue;
                        }

                        foreach (PatchGate g in patch.Gates)
                        {
                            if (g?.O != null && g.I != null && g.O.Length >= 3 && g.I.Length >= 3)
                            {
                                links.Add((new Vector3(g.O[0], g.O[1], g.O[2]), new Vector3(g.I[0], g.I[1], g.I[2]), true));
                            }
                        }
                    }
                }

                int generation = _generation;
                Task.Run(() => BuildGraph(verts, idx, links, revision, generation));
            }
            catch (Exception e)
            {
                _failed = true;
                _building = false;
                _builtRevision = revision;
                Diag.Report("LongPath.StartBuild", e);
            }
        }

        private static void BuildGraph(Vector3[] vertices, int[] indices, List<(Vector3 a, Vector3 b, bool two)> links, int revision, int generation)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int n = indices.Length / 3;
                if (n == 0)
                {
                    throw new InvalidOperationException("the navmesh is empty");
                }

                // Weld the vertices (1 cm): the triangulation repeats a vertex for each polygon that uses it
                Dictionary<long, int> weld = new Dictionary<long, int>(vertices.Length);
                int[] map = new int[vertices.Length];
                int next = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    long key = Q(vertices[i]);
                    if (!weld.TryGetValue(key, out int id))
                    {
                        id = next++;
                        weld[key] = id;
                    }

                    map[i] = id;
                }

                Vector3[] cent = new Vector3[n];
                List<int>[] adj = new List<int>[n];
                Dictionary<long, int> edges = new Dictionary<long, int>(n * 2);
                for (int t = 0; t < n; t++)
                {
                    int a = map[indices[3 * t]], b = map[indices[3 * t + 1]], c = map[indices[3 * t + 2]];
                    cent[t] = (vertices[indices[3 * t]] + vertices[indices[3 * t + 1]] + vertices[indices[3 * t + 2]]) / 3f;
                    adj[t] = new List<int>(3);
                    int[] vs = { a, b, c };
                    for (int k = 0; k < 3; k++)
                    {
                        int p = vs[k], q = vs[(k + 1) % 3];
                        long ek = ((long)Math.Min(p, q) << 32) | (uint)Math.Max(p, q);
                        if (edges.TryGetValue(ek, out int other))
                        {
                            adj[t].Add(other);
                            adj[other].Add(t);
                        }
                        else
                        {
                            edges[ek] = t;
                        }
                    }
                }

                Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
                for (int t = 0; t < n; t++)
                {
                    long cell = Cell(cent[t].x, cent[t].z);
                    if (!grid.TryGetValue(cell, out List<int> list))
                    {
                        grid[cell] = list = new List<int>();
                    }

                    list.Add(t);
                }

                // Triangles that lie one on another (a patch over the old mesh, the doubled floor of a narrow place) are one place
                int stitched = 0;
                foreach (KeyValuePair<long, List<int>> cell in grid)
                {
                    int cx = (int)(cell.Key >> 32), cz = (int)(cell.Key & 0xffffffffL);
                    foreach (int t in cell.Value)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out List<int> others))
                                {
                                    continue;
                                }

                                foreach (int o in others)
                                {
                                    if (o <= t)
                                    {
                                        continue;
                                    }

                                    Vector3 dv = cent[o] - cent[t];
                                    if (Mathf.Abs(dv.y) < 0.35f && dv.x * dv.x + dv.z * dv.z < 0.55f * 0.55f && !adj[t].Contains(o))
                                    {
                                        adj[t].Add(o);
                                        adj[o].Add(t);
                                        stitched++;
                                    }
                                }
                            }
                        }
                    }
                }

                // The links of the map edits join what the mesh does not (a ledge, a narrow neck)
                int joined = 0;
                foreach ((Vector3 la, Vector3 lb, bool two) in links)
                {
                    int ta = NearestIn(grid, cent, la, 3f), tb = NearestIn(grid, cent, lb, 3f);
                    if (ta >= 0 && tb >= 0 && ta != tb)
                    {
                        adj[ta].Add(tb);
                        if (two)
                        {
                            adj[tb].Add(ta);
                        }

                        joined++;
                    }
                }

                if (generation != _generation)
                {
                    return;
                }

                _cent = cent;
                _adj = adj;
                _grid = grid;
                _comp = null;
                _compSize = null;
                _fields.Clear();
                _fieldTasks.Clear();
                _builtRevision = revision;
                _built = true;
                _building = false;
                Log.Info($"LR LongPath: {n} navmesh triangles, {joined} edit link(s) and {stitched} overlapping pair(s) in the way search, built in {watch.ElapsedMilliseconds} ms (worker thread)");
            }
            catch (Exception e)
            {
                if (generation == _generation)
                {
                    _failed = true;
                    _building = false;
                    _builtRevision = revision;
                }

                Diag.Report("LongPath.BuildGraph", e);
            }
        }

        private static int NearestIn(Dictionary<long, List<int>> grid, Vector3[] cent, Vector3 p, float within)
        {
            int best = -1;
            float bestD = within * within;
            int cx = (int)Mathf.Floor(p.x / 4f), cz = (int)Mathf.Floor(p.z / 4f);
            int r = Mathf.CeilToInt(within / 4f);
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out List<int> list))
                    {
                        continue;
                    }

                    foreach (int t in list)
                    {
                        float d = (cent[t] - p).sqrMagnitude;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = t;
                        }
                    }
                }
            }

            return best;
        }

        private static int Nearest(Vector3 p, float within) => NearestIn(_grid, _cent, p, within);

        private static void EnsureComps()
        {
            if (_comp != null)
            {
                return;
            }

            int[] comp = new int[_cent.Length];
            int[] size = new int[_cent.Length];
            for (int i = 0; i < comp.Length; i++)
            {
                comp[i] = -1;
            }

            Stack<int> stack = new Stack<int>();
            for (int s0 = 0; s0 < comp.Length; s0++)
            {
                if (comp[s0] >= 0)
                {
                    continue;
                }

                int count = 0;
                comp[s0] = s0;
                stack.Push(s0);
                while (stack.Count > 0)
                {
                    int u = stack.Pop();
                    count++;
                    foreach (int v in _adj[u])
                    {
                        if (comp[v] < 0)
                        {
                            comp[v] = s0;
                            stack.Push(v);
                        }
                    }
                }

                size[s0] = count;
            }

            _compSize = size;
            _comp = comp;
        }

        /// <summary>
        /// The piece of mesh (the triangles joined by shared edges, and by the links of the map edits) the point stands on: its id and size in triangles,
        /// and the centre of the triangle nearest to the point. False when no triangle is within <paramref name="within"/> m, or the graph is not ready.
        /// </summary>
        public static bool Probe(Vector3 p, float within, out int id, out int size, out Vector3 centre)
        {
            id = -1;
            size = 0;
            centre = p;
            if (!EnsureBuilt())
            {
                return false;
            }

            EnsureComps();
            int t = Nearest(p, within);
            if (t < 0)
            {
                return false;
            }

            id = _comp[t];
            size = _compSize[id];
            centre = _cent[t];
            return true;
        }

        /// <summary>The centres of the triangles of the piece <paramref name="id"/> (from Probe) within <paramref name="radius"/> m of the point.</summary>
        public static List<Vector3> CentresOf(int id, Vector3 p, float radius)
        {
            List<Vector3> result = new List<Vector3>();
            if (!_built || _comp == null)
            {
                return result;
            }

            int cx = (int)Mathf.Floor(p.x / 4f), cz = (int)Mathf.Floor(p.z / 4f);
            int r = Mathf.CeilToInt(radius / 4f);
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (!_grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out List<int> list))
                    {
                        continue;
                    }

                    foreach (int t in list)
                    {
                        if (_comp[t] == id && (_cent[t] - p).sqrMagnitude <= radius * radius)
                        {
                            result.Add(_cent[t]);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// A way from <paramref name="from"/> to <paramref name="to"/> as via-points (the last one is <paramref name="to"/> itself, snapped to the mesh), about
        /// <paramref name="step"/> m apart; <paramref name="length"/> is its length. False: the two are not joined (an island with no link), or not yet known
        /// (the graph or the field of this target is still being made - the caller asks again).
        /// </summary>
        public static bool TryFind(Vector3 from, Vector3 to, out Vector3[] via, out float length, float step = 25f)
        {
            via = null;
            length = 0f;
            string ck = Mathf.RoundToInt(from.x / 8f) + "|" + Mathf.RoundToInt(from.y / 4f) + "|" + Mathf.RoundToInt(from.z / 8f) + ">" + Mathf.RoundToInt(to.x) + "|" + Mathf.RoundToInt(to.y) + "|" + Mathf.RoundToInt(to.z);
            if (_cache.TryGetValue(ck, out (float at, Vector3[] via, float length) hit0) && Time.time - hit0.at < 120f)
            {
                via = hit0.via;
                length = hit0.length;
                return via != null;
            }

            bool ok = Find(from, to, out via, out length, step, out bool deferred);
            LastDeferred = deferred;
            if (!deferred)
            {
                _cache[ck] = (Time.time, via, length);
            }

            return ok;
        }

        private static Field MakeField(int target)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            Vector3[] cent = _cent;
            List<int>[] adj = _adj;
            int n = cent.Length;
            Field field = new Field { Dist = new float[n], Next = new int[n] };
            for (int i = 0; i < n; i++)
            {
                field.Dist[i] = float.MaxValue;
                field.Next[i] = -1;
            }

            bool[] closed = new bool[n];
            Heap heap = new Heap();
            field.Dist[target] = 0f;
            heap.Push(0f, target);
            int reached = 0;
            while (heap.Count > 0)
            {
                int u = heap.Pop().id;
                if (closed[u])
                {
                    continue;
                }

                closed[u] = true;
                reached++;
                foreach (int v in adj[u])
                {
                    float nv = field.Dist[u] + Vector3.Distance(cent[u], cent[v]);
                    if (nv < field.Dist[v])
                    {
                        field.Dist[v] = nv;
                        field.Next[v] = u;
                        heap.Push(nv, v);
                    }
                }
            }

            Log.Info($"LR LongPath: way field to ({cent[target].x:0}, {cent[target].y:0}, {cent[target].z:0}) made in {watch.ElapsedMilliseconds} ms (worker thread), {reached} of {n} triangles reach it");
            return field;
        }

        private static bool Find(Vector3 from, Vector3 to, out Vector3[] via, out float length, float step, out bool deferred)
        {
            via = null;
            length = 0f;
            deferred = false;
            if (!EnsureBuilt())
            {
                deferred = !_failed;
                return false;
            }

            int a = Nearest(from, 6f), b = Nearest(to, 6f);
            if (a < 0 || b < 0)
            {
                return false;
            }

            if (!_fields.TryGetValue(b, out Field field))
            {
                deferred = true;
                if (_fieldTasks.TryAdd(b, true))
                {
                    Searches++;
                    int generation = _generation;
                    Task.Run(() =>
                    {
                        try
                        {
                            Field made = MakeField(b);
                            if (generation == _generation)
                            {
                                _fields[b] = made;
                            }
                        }
                        catch (Exception e)
                        {
                            Diag.Report("LongPath.MakeField", e);
                            _fields[b] = new Field { Dist = new float[0], Next = new int[0] };
                        }
                    });
                }

                return false;
            }

            if (field.Dist.Length != _cent.Length || field.Dist[a] == float.MaxValue)
            {
                return false;
            }

            List<int> chain = new List<int> { a };
            for (int t = a, guard = 0; t != b && guard < 100000; guard++)
            {
                t = field.Next[t];
                if (t < 0)
                {
                    return false;
                }

                chain.Add(t);
            }

            List<Vector3> pts = new List<Vector3>();
            Vector3 last = from;
            for (int i = 1; i < chain.Count; i++)
            {
                if (Vector3.Distance(last, _cent[chain[i]]) >= step && i < chain.Count - 1)
                {
                    last = _cent[chain[i]];
                    pts.Add(last);
                }
            }

            pts.Add(NavMesh.SamplePosition(to, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ? hit.position : _cent[b]);
            length = field.Dist[a];
            via = pts.ToArray();
            Found++;
            return true;
        }
    }
}
