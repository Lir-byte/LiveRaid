using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Raid;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// The ambush (F12 -> 9 "Lie in wait for an enemy you hear"). A player's tester (2026-11-02) wanted bots that, once they hear the player, do not just
    /// camp where they stand or run into him, but go to where he is likely to go - a place with loot, the nearest exit or the switch that opens one -
    /// and wait there. Rules set by the owner of the mod: never beforehand (not without a sound), only by character and by weight (one reaction among the
    /// others of <see cref="NoiseResponse"/>), no long idling, a good position, and the bot decides for itself - no luck, it drops the ambush or goes to
    /// guess again.
    ///
    ///  1. HEARD: NoiseResponse hears an enemy running, walking upright or at a door. The enemy's heading is taken from his steps of the last seconds
    ///     (<see cref="NoiseResponse.Heading"/>), a little blurred.
    ///  2. WHERE HE GOES: what lies ahead of him (within about 50 degrees of the heading): a door 5-45 m off, a place of loot (POIs worth 2.5+), a PMC exit
    ///     (25-160 m) or the switch of a switch exit, else just the corner ahead of him. Each is weighed by how well it lines up with his heading, the
    ///     bot's character (looters like loot places, campers and rats exits...) and a bit of chance; the best few are tried in turn.
    ///  3. WHERE TO LIE: a point round the target (3.5-17 m) that sees the target, is out of the enemy's sight from where he is now, is a little to the
    ///     side of his line and has a wall or a corner next to it; the bot's way there must be complete and it must get there before him (its walking time
    ///     + 2 s under his), and the spot must not be next to him (9+ m). No such point - no ambush: the bot picks another reaction.
    ///  4. WAITING: crouched (snipers and campers sometimes prone) facing the target for 12-25 s (rats and campers a little longer, rushers shorter, a
    ///     novice shorter); it runs there only until 25 m from the target, the last part is a walk. If the enemy's steps come within 35 m of the spot the
    ///     wait is extended once by 8 s. A fight hands the bot to SAIN as always. If the enemy goes far off (120 m) the ambush ends.
    ///  5. IF HE DID NOT COME: if the bot can hear him still, it guesses once more (another target from where he is now), else it drops the ambush
    ///     and goes on with its day. After any ambush the bot makes no new reaction for 2 minutes.
    /// At most 3 bots lie in wait at once, 2 for the same enemy.
    /// </summary>
    public static class Intercept
    {
        private const int MaxAtOnce = 3;
        private const int MaxPerEnemy = 2;
        private const float RestAfter = 120f;

        public class Plan
        {
            public Vector3 Spot;
            public Vector3 Target;
            public string Kind;
            public string Name;
            public float Path;
            public float Eta;
            public float EnemyEta;
        }

        private class State
        {
            public BotOwner Bot;
            public BotProfile P;
            public Player Maker;
            public int Tries = 1;
            public bool Extended;
            public bool Arrived;
            public Plan Plan;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static float _next;

        public static int Planned { get; private set; }
        public static int NoSpot { get; private set; }
        public static int Met { get; private set; }
        public static int MetYou { get; private set; }
        public static int Empty { get; private set; }
        public static int Again { get; private set; }
        public static int Extended { get; private set; }
        public static int Gone { get; private set; }
        private static readonly Dictionary<string, int> _kinds = new Dictionary<string, int>();

        public static string Summary => Planned == 0 && NoSpot == 0
            ? "ambushes: none"
            : $"ambushes: laid {Planned} ({string.Join(", ", _kinds.Select(kv => $"{kv.Key} {kv.Value}"))}), no good spot {NoSpot}; the enemy came {Met} ({MetYou} of them you), " +
              $"nobody came {Empty}, guessed again {Again}, waited longer {Extended}, enemy went off {Gone}";

        public static void Clear()
        {
            _states.Clear();
            _kinds.Clear();
            _next = 0f;
            Planned = NoSpot = Met = MetYou = Empty = Again = Extended = Gone = 0;
        }

        public static void NoteNoSpot() => NoSpot++;

        public static bool Lying(BotOwner bot) => bot != null && _states.ContainsKey(bot.ProfileId);

        /// <summary>The weight of this reaction for the bot (0 - not possible now). Character first: rats, campers, snipers and hunters; rushers hardly ever.</summary>
        public static float Weight(BotOwner bot, BotProfile p, float precision, bool fullBag)
        {
            if (!Settings.Intercept.Value || p.Packrat || fullBag || _states.Count >= MaxAtOnce)
            {
                return 0f;
            }

            if ((bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f) < 0.6f || SafeReload.OutOfRounds(bot) || bot.WeaponManager?.HaveBullets == false)
            {
                return 0f;
            }

            float w;
            switch (p.Style)
            {
                case PlayStyle.Rat: w = 0.5f; break;
                case PlayStyle.Camper:
                case PlayStyle.Sniper: w = 0.4f; break;
                case PlayStyle.Hunter: w = 0.38f; break;
                case PlayStyle.Explorer: w = 0.16f; break;
                case PlayStyle.Looter: w = 0.12f; break;
                case PlayStyle.Quester: w = 0.1f; break;
                default: w = 0.07f; break;
            }

            if (p.Kind == BotKind.Scav)
            {
                w *= 0.6f;
            }
            else if (p.Kind == BotKind.PlayerScav)
            {
                w *= 0.9f;
            }

            return w * (0.7f + 0.1f * (int)p.Skill) * (0.5f + precision) * Settings.InterceptWeight.Value;
        }

        /// <summary>Predicts where the enemy goes and finds a place to lie in wait for him; null when there is no good one.</summary>
        public static Plan MakePlan(BotOwner bot, BotProfile p, Player maker, Vector3 heardAt)
        {
            if (maker == null || maker.HealthController?.IsAlive != true || _states.Values.Count(x => x.Maker == maker) >= MaxPerEnemy)
            {
                return null;
            }

            NoiseResponse.Heading(maker, out Vector3 heading, out float speed);
            if (heading.sqrMagnitude < 0.01f)
            {
                heading = maker.LookDirection;
                heading.y = 0f;
            }

            if (heading.sqrMagnitude < 0.01f)
            {
                return null;
            }

            // What the ear gives is not exact
            heading = Quaternion.Euler(0f, ((float)_rng.NextDouble() - 0.5f) * 36f, 0f) * heading.normalized;
            Vector3 from = heardAt;
            float pace = Mathf.Max(2.2f, speed);
            List<(Vector3 pos, string kind, string name, float score)> targets = Targets(bot, p, from, heading);
            if (targets.Count == 0)
            {
                return null;
            }

            // A bit of chance in the order (the jitter is drawn once per target - a comparer that draws again would be inconsistent)
            var ordered = targets.Select(t => (t, key: t.score * (0.8f + 0.4f * (float)_rng.NextDouble()))).OrderByDescending(x => x.key).Select(x => x.t).ToList();
            for (int i = 0; i < ordered.Count && i < 5; i++)
            {
                Plan plan = Spot(bot, from, pace, ordered[i]);
                if (plan != null)
                {
                    return plan;
                }
            }

            return null;
        }

        private static List<(Vector3 pos, string kind, string name, float score)> Targets(BotOwner bot, BotProfile p, Vector3 from, Vector3 heading)
        {
            var list = new List<(Vector3, string, string, float)>();
            float Align(Vector3 at, float min, float max, float minCos, out float cos)
            {
                Vector3 d = at - from;
                d.y = 0f;
                float dist = d.magnitude;
                cos = dist > 0.1f ? Vector3.Dot(d / dist, heading) : 0f;
                return dist >= min && dist <= max && cos >= minCos ? dist : -1f;
            }

            foreach (Door door in DoorAssistLayer.Doors)
            {
                if (door == null || door.DoorState == EDoorState.Locked || Mathf.Abs(door.transform.position.y - from.y) > 3f)
                {
                    continue;
                }

                if (Align(door.transform.position, 5f, 45f, 0.6f, out float cos) > 0f)
                {
                    list.Add((door.transform.position, "doorway", door.Id, 1.0f * cos));
                }
            }

            bool loot = p.Style == PlayStyle.Looter || p.Style == PlayStyle.Explorer || p.Style == PlayStyle.Rat;
            var best = new List<(Vector3, string, string, float)>();
            MapIntel intel = MapIntel.Current;
            if (intel != null)
            {
                foreach (Poi poi in intel.Pois)
                {
                    if (poi.W < 2.5f || poi.Kind == PoiKind.Open || Mathf.Abs(poi.Y - from.y) > 4f)
                    {
                        continue;
                    }

                    if (Align(poi.Pos, 12f, 90f, 0.7f, out float cos) > 0f)
                    {
                        best.Add((poi.Pos, "loot place", poi.Kind.ToString(), (0.6f + 0.08f * Mathf.Min(poi.W, 8f)) * cos * (loot ? 1.3f : 1f)));
                    }
                }

                list.AddRange(best.OrderByDescending(b => b.Item4).Take(6));
            }

            bool waiter = p.Style == PlayStyle.Camper || p.Style == PlayStyle.Sniper || p.Style == PlayStyle.Rat;
            ExfiltrationPoint[] exits = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (exits != null)
            {
                foreach (ExfiltrationPoint e in exits)
                {
                    if (e == null || e.Status == EExfiltrationStatus.NotPresent || e.Status == EExfiltrationStatus.UncompleteRequirements)
                    {
                        continue;
                    }

                    if (Align(e.transform.position, 25f, 160f, 0.6f, out float cos) > 0f)
                    {
                        list.Add((e.transform.position, "exit", e.Settings?.Name ?? "?", 1.1f * cos * (waiter ? 1.4f : 1f)));
                    }
                }
            }

            try
            {
                foreach (SwitchExtract.SwitchExit sx in SwitchExtract.Exits())
                {
                    if (!sx.Open && Align(sx.Stand, 25f, 160f, 0.6f, out float cos) > 0f)
                    {
                        list.Add((sx.Stand, "exit switch", sx.Name, 1.2f * cos * (waiter ? 1.4f : 1f)));
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("Intercept.switches", e);
            }

            // Else only the way ahead of him
            Vector3 corner = from + heading * (14f + (float)_rng.NextDouble() * 8f);
            if (NavMesh.SamplePosition(corner, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                list.Add((hit.position, "way ahead", "", 0.4f));
            }

            return list;
        }

        /// <summary>The best spot round the target that sees it, is out of the enemy's sight, a little to the side of his line, with a wall near, and that the bot reaches first.</summary>
        private static Plan Spot(BotOwner bot, Vector3 enemy, float pace, (Vector3 pos, string kind, string name, float score) target)
        {
            Vector3 t = target.pos;
            Vector3 line = t - enemy;
            line.y = 0f;
            float enemyDist = line.magnitude;
            line = enemyDist > 0.1f ? line / enemyDist : Vector3.forward;
            int mask = LayersMaskController.HighPolyWithTerrainMask;
            var spots = new List<(Vector3 pos, float score)>();
            foreach (float r in new[] { 6f, 9f, 13f })
            {
                float offset = (float)_rng.NextDouble() * 30f;
                for (int k = 0; k < 12; k++)
                {
                    Vector3 want = t + Quaternion.Euler(0f, offset + k * 30f, 0f) * Vector3.forward * r;
                    if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - t.y) > 2.4f)
                    {
                        continue;
                    }

                    Vector3 s = hit.position;
                    Vector3 toT = t - s;
                    toT.y = 0f;
                    float d = toT.magnitude;
                    if (d < 3.5f || d > 17f || (s - enemy).magnitude < 9f)
                    {
                        continue;
                    }

                    if (Physics.Linecast(s + Vector3.up * 1.0f, t + Vector3.up * 1.2f, mask))
                    {
                        continue;
                    }

                    float score = 0f;
                    bool seen = !Physics.Linecast(enemy + Vector3.up * 1.5f, s + Vector3.up * 0.9f, mask);
                    score += seen ? -3f : 2f;
                    Vector3 sideV = s - t;
                    sideV.y = 0f;
                    float side = Mathf.Abs(line.x * sideV.z - line.z * sideV.x);
                    score += side >= 2f ? 1f : 0f;
                    int walls = 0;
                    foreach (Vector3 dir in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                    {
                        if (Physics.Raycast(s + Vector3.up * 0.7f, dir, 1.3f, mask))
                        {
                            walls++;
                        }
                    }

                    score += 0.4f * Mathf.Min(3, walls);
                    score += 1f - Mathf.Abs(d - 8f) / 10f;
                    score += 0.3f * (float)_rng.NextDouble();
                    spots.Add((s, score));
                }
            }

            foreach ((Vector3 pos, float score) c in spots.OrderByDescending(x => x.score).Take(3))
            {
                if (!NavMesh.SamplePosition(bot.Position, out NavMeshHit from, 1.5f, NavMesh.AllAreas)
                    || !NavMesh.CalculatePath(from.position, c.pos, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0f;
                Vector3[] corners = _path.corners;
                for (int i = 1; i < corners.Length; i++)
                {
                    len += Vector3.Distance(corners[i - 1], corners[i]);
                }

                float straight = Vector3.Distance(bot.Position, c.pos);
                if (len > 85f || len > straight * 2.2f + 25f)
                {
                    continue;
                }

                float eta = len / 4.0f + 1.5f;
                float enemyEta = enemyDist / pace;
                if (eta + 2f > enemyEta)
                {
                    continue;
                }

                return new Plan { Spot = c.pos, Target = t, Kind = target.kind, Name = target.name, Path = len, Eta = eta, EnemyEta = enemyEta };
            }

            return null;
        }

        /// <summary>The task for the bot to carry out the plan; the state and the follow-up (guess again / drop) are wired in.</summary>
        public static TacticalTask Task(BotOwner bot, BotProfile p, Player maker, Plan plan, int tries)
        {
            float styleFactor = p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper || p.Style == PlayStyle.Sniper ? 1.15f
                : p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Looter ? 0.85f : 1f;
            float wait = (12f + (float)_rng.NextDouble() * 13f) * styleFactor * (0.85f + 0.05f * (int)p.Skill);
            string id = bot.ProfileId;
            var state = new State { Bot = bot, P = p, Maker = maker, Tries = tries, Plan = plan };
            _states[id] = state;
            Planned++;
            _kinds[plan.Kind] = _kinds.TryGetValue(plan.Kind, out int n) ? n + 1 : 1;
            string who = Net.IsHuman(maker) ? "you" : maker.Profile?.Nickname;
            Log.Info($"LR Intercept: {p.Name} ({p.Kind} {p.Style}, {p.Skill}) expects {who} at a {plan.Kind}{(string.IsNullOrEmpty(plan.Name) ? "" : " " + plan.Name)} " +
                     $"and lies in wait {Vector3.Distance(plan.Spot, plan.Target):0} m from it for {wait:0} s (there in {plan.Eta:0} s, he in {plan.EnemyEta:0} s)");
            return new TacticalTask
            {
                Kind = TaskKind.Ambush,
                StandAt = plan.Spot,
                LookAt = plan.Target,
                Speed = 1f,
                Sprint = plan.Path > 30f,
                SprintBeyond = 25f,
                MovePose = 1f,
                Dwell = wait,
                Until = Time.time + plan.Path / 3.2f + 4f + wait,
                Prone = (p.Style == PlayStyle.Sniper || p.Style == PlayStyle.Camper) && _rng.NextDouble() < 0.35,
                Cleanup = () => Ended(id),
                OnFinished = b => NobodyCame(b, state),
            };
        }

        private static void Ended(string id)
        {
            if (!_states.TryGetValue(id, out State s))
            {
                return;
            }

            _states.Remove(id);
            BotOwner bot = s.Bot;
            if (bot != null && !bot.IsDead && TacticalTasks.HasContact(bot))
            {
                Met++;
                if (Net.IsHuman(s.Maker))
                {
                    MetYou++;
                }

                Log.Info($"LR Intercept: {s.P.Name} meets {(Net.IsHuman(s.Maker) ? "you" : s.Maker?.Profile?.Nickname)} at the ambush");
            }

            if (bot != null)
            {
                NoiseResponse.Rest(bot, RestAfter);
            }
        }

        private static void NobodyCame(BotOwner bot, State s)
        {
            Empty++;
            Log.Info($"LR Intercept: {s.P.Name} - nobody came to the {s.Plan.Kind} in the time");
            if (s.Tries >= 2 || s.Maker == null || !NoiseResponse.Hear(bot, s.P, s.Maker, out Vector3 at))
            {
                return;
            }

            Plan again = MakePlan(bot, s.P, s.Maker, at);
            if (again == null)
            {
                return;
            }

            Again++;
            if (!TacticalTasks.Set(bot, Task(bot, s.P, s.Maker, again, 2)))
            {
                Ended(bot.ProfileId);
            }
        }

        /// <summary>Twice a second: a wait is extended once when his steps come close; the ambush ends when he is far away.</summary>
        public static void Tick()
        {
            if (_states.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            foreach (State s in _states.Values.ToList())
            {
                BotOwner bot = s.Bot;
                if (bot == null || bot.IsDead || !TacticalTasks.TryGet(bot, out TacticalTask task) || task.Kind != TaskKind.Ambush)
                {
                    continue;
                }

                if (s.Maker == null || s.Maker.HealthController?.IsAlive != true)
                {
                    TacticalTasks.Cancel(bot, "the enemy is gone");
                    continue;
                }

                float away = Vector3.Distance(s.Maker.Position, s.Plan.Spot);
                if (away > 120f)
                {
                    Gone++;
                    Log.Info($"LR Intercept: {s.P.Name} - he went far off, the ambush is over");
                    TacticalTasks.Cancel(bot, "he went elsewhere");
                    continue;
                }

                if (task.Arrived && !s.Extended && away < 35f && Time.time < task.Until)
                {
                    s.Extended = true;
                    task.Until += 8f;
                    Extended++;
                }
            }
        }
    }
}
