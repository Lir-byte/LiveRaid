using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Ways that keep to cover (F12 -> 3 "Keep to cover across open ground"). QuestingBots walks the navmesh's shortest way - straight across a
    /// field, down the middle of a road. A player runs along the tree line, the wall, the cars. Here, every few seconds, a calm leader or lone
    /// PMC/player scav on its way looks at the next 120 m of its path: 40 m or more of it in the open (hardly anything within 6 m on any side),
    /// starting within 35 m ahead, and it first runs to a spot with cover 15-35 m to one side of that stretch, then goes on (QuestingBots takes
    /// over again on arrival) - if that costs at most a third more way. Who does it: careful characters and drilled squads mostly, anyone right
    /// after shooting nearby; a rusher in a quiet raid cuts straight across. At most once per 25 s per bot.
    /// </summary>
    public static class CoverRoute
    {
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly HashSet<string> _looked = new HashSet<string>();

        public static int Overlooks { get; private set; }
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static readonly NavMeshPath _probe = new NavMeshPath();
        private static readonly System.Random _rng = new System.Random();
        private static float _tick;
        private static int _cursor;

        public static int Detours { get; private set; }
        public static int Checked { get; private set; }
        public static int NoOpen { get; private set; }
        public static int NoSide { get; private set; }

        public static string Summary => $"looks over open ground before crossing {Overlooks}; ways kept to cover: {Detours} detours off open ground (paths looked at {Checked}: no open stretch {NoOpen}, no cover to the side {NoSide})";

        public static bool On => Settings.Enabled.Value && Settings.CoverRoutes.Value;

        public static void Clear()
        {
            _next.Clear();
            _looked.Clear();
            Overlooks = 0;
            _tick = 0f;
            _cursor = 0;
            Detours = Checked = NoOpen = NoSide = 0;
        }

        public static void Tick()
        {
            if (!On || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world.AllAlivePlayersList.Count == 0)
            {
                return;
            }

            // One bot per tick: the raycasts are spread over time
            for (int tries = 0; tries < world.AllAlivePlayersList.Count; tries++)
            {
                _cursor = (_cursor + 1) % world.AllAlivePlayersList.Count;
                BotOwner bot = world.AllAlivePlayersList[_cursor]?.AIData?.BotOwner;
                if (bot != null && Check(bot))
                {
                    return;
                }
            }
        }

        /// <summary>True when the bot was looked at (whatever came of it).</summary>
        private static bool Check(BotOwner bot)
        {
            if (bot.IsDead || Spectator.TakeOver.IsTaken(bot) || (_next.TryGetValue(bot.ProfileId, out float next) && Time.time < next))
            {
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || TacticalTasks.SquadLeaderOf(bot) != null
                || TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || bot.Mover == null || !bot.Mover.HasPathAndNoComplete)
            {
                return false;
            }

            _next[bot.ProfileId] = Time.time + 8f;
            if (!Wants(bot, p))
            {
                return true;
            }

            Vector3 dest = MoveGoal.Of(bot);
            if ((dest - bot.Position).sqrMagnitude < 60f * 60f || !NavMesh.CalculatePath(bot.Position, dest, NavMesh.AllAreas, _path) || _path.corners.Length < 2)
            {
                return true;
            }

            Checked++;
            if (!OpenStretch(_path.corners, out Vector3 from, out Vector3 to, out float total))
            {
                NoOpen++;
                return true;
            }

            Vector3 mid = (from + to) * 0.5f;

            // A look first (F12 -> 3 "Look over open ground first"): from where it stands, if that is not out in the open itself, 2-4 s over the field
            string seen = bot.ProfileId + "|" + Mathf.RoundToInt(mid.x / 20f) + "|" + Mathf.RoundToInt(mid.z / 20f);
            if (Settings.Overlook.Value && !_looked.Contains(seen) && LootBrain.Openness(bot.Position) < 0.6f && _rng.NextDouble() < 0.6)
            {
                _looked.Add(seen);
                float look = 2f + (float)_rng.NextDouble() * 2f;
                if (TacticalTasks.Set(bot, new TacticalTask { Kind = TaskKind.Listen, LookAt = mid + Vector3.up * 1.2f, Until = Time.time + look }))
                {
                    Overlooks++;
                    _next[bot.ProfileId] = Time.time + look + 1f;
                    Log.Detail("task", () => $"{p.Name} looks over {Vector3.Distance(from, to):0} m of open ground ahead for {look:0} s before crossing");
                    return true;
                }
            }
            Vector3 along = to - from;
            along.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, along.normalized);
            Vector3? best = null;
            float bestCost = float.MaxValue;
            foreach (float s in _rng.NextDouble() < 0.5 ? new[] { 1f, -1f } : new[] { -1f, 1f })
            {
                foreach (float off in new[] { 18f, 26f, 34f })
                {
                    if (!NavMesh.SamplePosition(mid + side * s * off, out NavMeshHit hit, 6f, NavMesh.AllAreas) || LootBrain.Openness(hit.position) > 0.5f)
                    {
                        continue;
                    }

                    float a = Length(bot.Position, hit.position);
                    float b = a > 0f ? Length(hit.position, dest) : -1f;
                    if (a < 0f || b < 0f || a + b > total * 1.35f)
                    {
                        continue;
                    }

                    if (a + b < bestCost)
                    {
                        bestCost = a + b;
                        best = hit.position;
                    }
                }
            }

            if (best == null)
            {
                NoSide++;
                return true;
            }

            TacticalTask task = new TacticalTask
            {
                Kind = TaskKind.Relocate, StandAt = best, LookAt = to, Speed = 1f, Sprint = true, SprintBeyond = 10f, MovePose = 1f,
                Until = Time.time + Vector3.Distance(bot.Position, best.Value) / 3f + 12f,
            };
            if (TacticalTasks.Set(bot, task))
            {
                Detours++;
                _next[bot.ProfileId] = Time.time + 25f;
                Log.Detail("task", () => $"{p.Name} keeps to cover: {Vector3.Distance(from, to):0} m of open ground ahead, goes by ({best.Value.x:0}, {best.Value.z:0}) " +
                                         $"(+{(bestCost / total - 1f) * 100f:0} % of the way)");
            }

            return true;
        }

        private static bool Wants(BotOwner bot, BotProfile p)
        {
            if (WorldEvents.ShotPressure(bot.Position, 150f, 90f, null) > 0f)
            {
                return true;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            float careful = d != null ? 0.5f * (1f - d[DoctrineDeck.Aggression]) + 0.5f * d[DoctrineDeck.Precision] : 1f - p.Aggression;

            // At night the open is where a light or a silhouette gives one away - for the disciplined ones (NightDiscipline)
            if (NightDiscipline.KeepsDark(bot))
            {
                careful += 0.3f;
            }

            // On a kill streak: cuts across (RaidMood)
            if (RaidMood.Careless(bot))
            {
                careful *= 0.5f;
            }
            if (p.Style == PlayStyle.Rusher)
            {
                careful *= 0.4f;
            }

            careful += 0.1f * ((int)p.Skill - 1);
            return _rng.NextDouble() < Mathf.Clamp(careful, 0.05f, 0.9f);
        }

        /// <summary>A stretch of 40 m+ in the open on the next 120 m of the path, starting within 35 m ahead (sampled every 8 m).</summary>
        private static bool OpenStretch(Vector3[] corners, out Vector3 from, out Vector3 to, out float total)
        {
            from = to = default;
            total = 0f;
            for (int i = 1; i < corners.Length; i++)
            {
                total += Vector3.Distance(corners[i - 1], corners[i]);
            }

            float walked = 0f;
            float openSince = -1f;
            Vector3 openFrom = default;
            for (int i = 1; i < corners.Length && walked < 120f; i++)
            {
                Vector3 a = corners[i - 1], b = corners[i];
                float len = Vector3.Distance(a, b);
                for (float t = 0f; t < len && walked + t < 120f; t += 8f)
                {
                    Vector3 q = Vector3.Lerp(a, b, t / Mathf.Max(0.01f, len));
                    // Open: at most 3 of 8 directions blocked within 6 m (ground slopes and kerbs catch the low rays too)
                    bool open = LootBrain.Openness(q) >= 0.6f && !Tactics.SquadPlaybook.Indoors(q);
                    float at = walked + t;
                    if (open)
                    {
                        if (openSince < 0f)
                        {
                            if (at > 35f)
                            {
                                return false;
                            }

                            openSince = at;
                            openFrom = q;
                        }

                        if (at - openSince >= 40f)
                        {
                            from = openFrom;
                            to = q;
                            return true;
                        }
                    }
                    else
                    {
                        openSince = -1f;
                    }
                }

                walked += len;
            }

            return false;
        }

        private static float Length(Vector3 a, Vector3 b)
        {
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, _probe) || _probe.status != NavMeshPathStatus.PathComplete)
            {
                return -1f;
            }

            float len = 0f;
            for (int i = 1; i < _probe.corners.Length; i++)
            {
                len += Vector3.Distance(_probe.corners[i - 1], _probe.corners[i]);
            }

            return len;
        }
    }
}
