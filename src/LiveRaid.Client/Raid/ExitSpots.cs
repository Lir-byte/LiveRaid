using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Where in the exit zone a bot stands out its timer (1.4.15). Every bot used to take the same stand point (or stopped at the zone's edge), in plain view of whoever
    /// shoots at the exit. Now each bot picks its own point of the zone on the navmesh: random, apart from the places already taken, and the more hidden from the
    /// outside the better (rays from the point at head and chest height to a ring 18 m round: a blocked ray is cover).
    /// </summary>
    public static class ExitSpots
    {
        private const float Apart = 1.3f;
        private const int Tries = 16;

        private struct Taken
        {
            public Vector3 Pos;
            public float At;
            public int Zone;
        }

        private static readonly List<Taken> _taken = new List<Taken>();
        private static readonly NavMeshPath _path = new NavMeshPath();

        public static void Clear() => _taken.Clear();

        /// <summary>A point of the zone for this bot, or null (no mesh inside the zone: the old stand point stays).</summary>
        public static Vector3? Pick(ExfiltrationPoint exit, Vector3 from, Vector3 stand)
        {
            BoxCollider zone = exit != null ? exit.GetComponent<BoxCollider>() : null;
            if (zone == null)
            {
                return null;
            }

            int id = exit.GetInstanceID();
            _taken.RemoveAll(t => Time.time - t.At > 150f);
            Vector3? best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < Tries; i++)
            {
                Vector3 local = zone.center + new Vector3(
                    (Random.value - 0.5f) * zone.size.x, (Random.value - 0.5f) * zone.size.y, (Random.value - 0.5f) * zone.size.z);
                Vector3 world = zone.transform.TransformPoint(local);
                if (!NavMesh.SamplePosition(world, out NavMeshHit hit, 1.6f, NavMesh.AllAreas))
                {
                    continue;
                }

                Vector3 p = hit.position;
                Vector3 probe = p + Vector3.up * 0.5f;
                if ((zone.ClosestPoint(probe) - probe).sqrMagnitude > 0.5f * 0.5f)
                {
                    continue;
                }

                bool crowded = false;
                foreach (Taken t in _taken)
                {
                    if (t.Zone == id && (t.Pos - p).sqrMagnitude < Apart * Apart)
                    {
                        crowded = true;
                        break;
                    }
                }

                if (crowded)
                {
                    continue;
                }

                // Reachable on foot from where the bot is (not a patch of mesh the other side of a wall)
                float direct = Vector3.Distance(from, p);
                if (!NavMesh.CalculatePath(from, p, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete || PathLength(_path) > direct * 2.5f + 8f)
                {
                    continue;
                }

                float score = Cover(p) + Random.value * 1.2f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            if (best.HasValue)
            {
                _taken.Add(new Taken { Pos = best.Value, At = Time.time, Zone = id });
            }

            return best;
        }

        /// <summary>How many of 16 rays from the point (two heights) to a ring 18 m round are stopped by something: the more, the less of the point is seen from outside.</summary>
        private static float Cover(Vector3 p)
        {
            int blocked = 0;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f + 0.2f;
                Vector3 far = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 18f;
                if (Physics.Linecast(p + Vector3.up * 1.5f, far + Vector3.up * 1.5f, LayersMaskController.HighPolyWithTerrainMask))
                {
                    blocked++;
                }

                if (Physics.Linecast(p + Vector3.up * 0.9f, far + Vector3.up * 0.9f, LayersMaskController.HighPolyWithTerrainMask))
                {
                    blocked++;
                }
            }

            return blocked / 4f;
        }

        private static float PathLength(NavMeshPath path)
        {
            float len = 0f;
            Vector3[] c = path.corners;
            for (int i = 1; i < c.Length; i++)
            {
                len += Vector3.Distance(c[i - 1], c[i]);
            }

            return len;
        }
    }
}
