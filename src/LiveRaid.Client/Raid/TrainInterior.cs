using System.Collections.Generic;
using System.Linq;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Inside the armoured train (1.4.14). A bot that comes to board used to stop 9 m from the zone, beside the carriage (raid 2026-11-11 21:42: Clarity stood on the stairs
    /// until the train left). The train is a moving platform with its own navmesh cutting, so its corridor is off the mesh and EFT's mover cannot walk it; the owner's
    /// own walk through the carriages (learned shortcuts of 05:01, taken out of the bots' routes in 1.4.7) is kept here as a line of footholds. A boarding bot is moved
    /// along it by hand (like a route link), takes its own place on it - each at least <see cref="SlotStep"/> m from the others, a squad close together - and waits there.
    /// </summary>
    public static class TrainInterior
    {
        public const float SlotStep = 2.4f;

        // The owner's walk along the train, Reserve (the station is always at the same place): from the west end to the east end, floor y about -5
        private static readonly Dictionary<string, Vector3[]> Lines = new Dictionary<string, Vector3[]>
        {
            ["RezervBase"] = new[]
            {
                new Vector3(121.2f, -5.0f, -142.9f), new Vector3(125.0f, -5.0f, -141.4f), new Vector3(128.8f, -5.0f, -142.9f),
                new Vector3(132.5f, -5.0f, -143.0f), new Vector3(135.9f, -5.0f, -144.5f), new Vector3(139.4f, -5.0f, -145.5f),
                new Vector3(143.1f, -5.0f, -146.7f), new Vector3(146.8f, -5.0f, -147.9f), new Vector3(150.5f, -5.0f, -149.1f),
                new Vector3(154.3f, -5.0f, -150.1f), new Vector3(158.5f, -5.0f, -150.6f), new Vector3(161.4f, -5.0f, -150.7f),
            },
        };

        private static string _location;
        private static Vector3[] _slots;
        private static readonly Dictionary<int, BotOwner> _taken = new Dictionary<int, BotOwner>();

        public static void Begin(string location)
        {
            _location = location;
            _taken.Clear();
            _slots = null;
            if (location != null && Lines.TryGetValue(location, out Vector3[] line))
            {
                _slots = Resample(line, SlotStep);
            }
        }

        public static void Clear()
        {
            _location = null;
            _slots = null;
            _taken.Clear();
        }

        /// <summary>The line of footholds is known for this map and passes the train's zone.</summary>
        public static bool Available(Vector3 zoneCenter)
        {
            return _slots != null && _slots.Length > 0 && _slots.Min(s => Vector3.Distance(s, zoneCenter)) < 12f;
        }

        public static int Count => _slots?.Length ?? 0;

        public static Vector3 SlotPos(int i) => _slots[i];

        /// <summary>Index of the slot nearest to <paramref name="p"/> - where this bot steps in.</summary>
        public static int Nearest(Vector3 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < _slots.Length; i++)
            {
                float d = (_slots[i] - p).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Takes a free place: near a squadmate already aboard, else any, a little to the side of the way in.</summary>
        public static int Claim(BotOwner bot, int entry)
        {
            foreach (int k in _taken.Where(kv => kv.Value == null || kv.Value.IsDead || kv.Value == bot).Select(kv => kv.Key).ToList())
            {
                _taken.Remove(k);
            }

            List<int> free = Enumerable.Range(0, _slots.Length).Where(i => !_taken.ContainsKey(i)).ToList();
            if (free.Count == 0)
            {
                return -1;
            }

            HashSet<string> mates = new HashSet<string>(SquadTies.MatesOf(bot.ProfileId) ?? Enumerable.Empty<string>());
            List<int> mateSlots = _taken.Where(kv => kv.Value != null && mates.Contains(kv.Value.ProfileId)).Select(kv => kv.Key).ToList();

            int pick;
            if (mateSlots.Count > 0)
            {
                // Next to the squad: the nearest free place to any mate (and to the way in on a tie)
                pick = free.OrderBy(i => mateSlots.Min(m => Mathf.Abs(m - i)) + Random.value * 0.9f).First();
            }
            else
            {
                // Anywhere, the farther ends as likely as the near ones, but not the very doorway
                pick = free.OrderByDescending(i => Random.value + (Mathf.Abs(i - entry) >= 2 ? 0.35f : 0f)).First();
            }

            _taken[pick] = bot;
            return pick;
        }

        public static void Release(BotOwner bot)
        {
            foreach (int k in _taken.Where(kv => kv.Value == bot).Select(kv => kv.Key).ToList())
            {
                _taken.Remove(k);
            }
        }

        private static Vector3[] Resample(Vector3[] line, float step)
        {
            List<Vector3> r = new List<Vector3> { line[0] };
            float carry = 0f;
            for (int i = 1; i < line.Length; i++)
            {
                Vector3 a = line[i - 1];
                Vector3 b = line[i];
                float len = Vector3.Distance(a, b);
                float at = step - carry;
                while (at <= len)
                {
                    r.Add(Vector3.Lerp(a, b, at / len));
                    at += step;
                }

                carry = len - (at - step);
            }

            return r.ToArray();
        }
    }
}
