using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using LiveRaid.Client.Bots;
using QuestingBots.Components;
using QuestingBots.Controllers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Hearing more than gunfire (F12 -> 9 "Respond to noise"). A player hears someone run on the floor above, a door, someone going through a
    /// body or reloading behind a wall - and goes to look, goes round, stops to listen, or slips away. Bots only went to shooting (GunfireResponse);
    /// SAIN hears steps, but only in its own fight. Here, twice a second, every calm leader or lone bot listens for enemies making noise near it:
    ///   running        38 m (walking upright 14 m; crouching, prone and standing make none)
    ///   a door         30 m (opened or shut by someone within 3 m of it)
    ///   looting        14 m (a bot going through a body or a container)
    ///   reloading      16 m
    /// A wall between halves the range, another floor (over 3.5 m up or down) cuts it to 40 %, a skilled ear hears a little further.
    /// The reaction depends on who hears (doctrine, style, skill, kit): go and look (to a few metres short of the noise), go round it, stop and
    /// listen (seconds, facing it), slip away (rats, looters with a full bag) or let it be. The human player makes noise like anyone - he gets looked for.
    /// Safety: a bot reacts once per 60-90 s, at most two bots go to one noise maker, at most max(3, living/4) at once, never while busy
    /// (looting, a task, leaving the raid), never AI scavs outside their patrol area.
    /// </summary>
    public static class NoiseResponse
    {
        public enum Noise
        {
            None,
            Steps,
            Running,
            Door,
            Looting,
            Reloading,
        }

        private enum Reaction
        {
            Check,
            GoRound,
            Listen,
            SlipAway,
            Ignore,
        }

        private class Source
        {
            public Vector3 LastPos;
            public float LastAt;
            public float Speed;
            public Noise Door;
            public float DoorAt;
            public Vector3 DoorPos;
        }

        private static readonly Dictionary<string, Source> _sources = new Dictionary<string, Source>();
        private static readonly Dictionary<Door, EDoorState> _doorStates = new Dictionary<Door, EDoorState>();
        private static readonly Dictionary<string, float> _cooldown = new Dictionary<string, float>();
        private static readonly Dictionary<string, (Vector3 at, float time)> _lastHeard = new Dictionary<string, (Vector3, float)>();

        /// <summary>Where the bot last heard an enemy's noise, if within <paramref name="seconds"/> (Swivel looks there).</summary>
        public static bool LastHeard(BotOwner bot, float seconds, out Vector3 at)
        {
            at = default;
            if (bot == null || !_lastHeard.TryGetValue(bot.ProfileId, out var h) || Time.time - h.time > seconds)
            {
                return false;
            }

            at = h.at;
            return true;
        }
        private static readonly Dictionary<string, HashSet<string>> _goingTo = new Dictionary<string, HashSet<string>>();
        private static readonly HashSet<string> _active = new HashSet<string>();
        private static readonly Dictionary<Noise, int> _heard = new Dictionary<Noise, int>();
        private static readonly Dictionary<Reaction, int> _reactions = new Dictionary<Reaction, int>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;
        private static float _nextDoors;

        public static int OfYou { get; private set; }

        public static string Summary =>
            _heard.Count == 0
                ? "noise heard: none"
                : $"noise heard: {string.Join(", ", _heard.Select(kv => $"{kv.Key} {kv.Value}"))} -> {string.Join(", ", _reactions.Select(kv => $"{kv.Key} {kv.Value}"))} ({OfYou} of them yours)";

        public static void Clear()
        {
            _sources.Clear();
            _doorStates.Clear();
            _cooldown.Clear();
            _lastHeard.Clear();
            _goingTo.Clear();
            _active.Clear();
            _heard.Clear();
            _reactions.Clear();
            _next = _nextDoors = 0f;
            OfYou = 0;
        }

        public static void Tick()
        {
            if (!Settings.NoiseResponse.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<Player> alive = world.AllAlivePlayersList.Where(x => x != null && x.HealthController != null && x.HealthController.IsAlive).ToList();
            Track(alive);
            Doors(alive);

            int cap = Mathf.Max(3, alive.Count / 4);
            _active.RemoveWhere(id => !TacticalTasks.Has(alive.FirstOrDefault(x => x.ProfileId == id)?.AIData?.BotOwner, TaskKind.Investigate)
                                      && !TacticalTasks.Has(alive.FirstOrDefault(x => x.ProfileId == id)?.AIData?.BotOwner, TaskKind.Listen));
            foreach (Player listener in alive)
            {
                if (_active.Count >= cap)
                {
                    break;
                }

                BotOwner bot = listener.AIData?.BotOwner;
                if (!CanReact(bot, out BotProfile p))
                {
                    continue;
                }

                Player maker = null;
                Noise noise = Noise.None;
                Vector3 where = default;
                float best = 0f;
                foreach (Player other in alive)
                {
                    if (other == listener || !IsEnemy(bot, other))
                    {
                        continue;
                    }

                    Noise n = NoiseOf(other, out Vector3 at, out float range);
                    if (n == Noise.None)
                    {
                        continue;
                    }

                    float heard = Heard(bot, p, at, range);
                    if (heard > best)
                    {
                        best = heard;
                        maker = other;
                        noise = n;
                        where = at;
                    }
                }

                if (maker != null)
                {
                    React(bot, p, maker, noise, where, best);
                }
            }
        }

        private static bool CanReact(BotOwner bot, out BotProfile p)
        {
            p = null;
            if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
            {
                return false;
            }

            p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return false;
            }

            if (_cooldown.TryGetValue(bot.ProfileId, out float until) && Time.time < until)
            {
                return false;
            }

            if (TacticalTasks.SquadLeaderOf(bot) != null || TacticalTasks.TryGet(bot, out _) || ContainerUnlockLayer.Busy.Contains(bot.ProfileId)
                || !TacticalTasks.BotIsCalm(bot) || ExtractPlanner.IsLeaving(bot))
            {
                return false;
            }

            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            return brain == null || !brain.LootTaskRunning;
        }

        private static bool IsEnemy(BotOwner bot, Player other)
        {
            if (SquadTies.AreMates(bot.ProfileId, other.ProfileId))
            {
                return false;
            }

            try
            {
                return bot.BotsGroup != null && bot.BotsGroup.IsPlayerEnemy(other);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Speed of everyone from where they were half a second ago.</summary>
        private static void Track(List<Player> alive)
        {
            float now = Time.time;
            foreach (Player x in alive)
            {
                if (!_sources.TryGetValue(x.ProfileId, out Source s))
                {
                    _sources[x.ProfileId] = new Source { LastPos = x.Position, LastAt = now };
                    continue;
                }

                float dt = now - s.LastAt;
                if (dt > 0.05f)
                {
                    Vector3 d = x.Position - s.LastPos;
                    d.y = 0f;
                    s.Speed = Mathf.Lerp(s.Speed, d.magnitude / dt, 0.6f);
                    s.LastPos = x.Position;
                    s.LastAt = now;
                }
            }
        }

        /// <summary>Twice a second the doors are looked at: one that changed state was moved by whoever stands within 3 m of it.</summary>
        private static void Doors(List<Player> alive)
        {
            if (Time.time < _nextDoors)
            {
                return;
            }

            _nextDoors = Time.time + 0.5f;
            foreach (Door door in DoorAssistLayer.Doors)
            {
                if (door == null)
                {
                    continue;
                }

                EDoorState state = door.DoorState;
                bool known = _doorStates.TryGetValue(door, out EDoorState was);
                _doorStates[door] = state;
                // Someone took hold of it (shut/open -> interacting) or it banged open/shut in one go; the end of the same swing is not a second noise
                if (!known || was == state || was == EDoorState.Interacting)
                {
                    continue;
                }

                Vector3 pos = door.transform.position;
                Player by = alive.Where(x => (x.Position - pos).sqrMagnitude < 9f).OrderBy(x => (x.Position - pos).sqrMagnitude).FirstOrDefault();
                if (by != null && _sources.TryGetValue(by.ProfileId, out Source s))
                {
                    s.Door = Noise.Door;
                    s.DoorAt = Time.time;
                    s.DoorPos = pos;
                }
            }
        }

        /// <summary>The loudest thing the person does right now, where, and how far it carries.</summary>
        private static Noise NoiseOf(Player x, out Vector3 at, out float range)
        {
            at = x.Position;
            range = 0f;
            if (!_sources.TryGetValue(x.ProfileId, out Source s))
            {
                return Noise.None;
            }

            if (s.Door == Noise.Door && Time.time - s.DoorAt < 1.2f)
            {
                at = s.DoorPos;
                range = 30f;
                return Noise.Door;
            }

            float pose = x.MovementContext != null ? x.MovementContext.PoseLevel : 1f;
            bool prone = x.MovementContext != null && x.MovementContext.IsInPronePose;
            if (!prone && (x.MovementContext?.IsSprintEnabled == true || s.Speed > 4.6f))
            {
                range = 38f;
                return Noise.Running;
            }

            if (x.HandsController is Player.FirearmController fc && fc.IsInReloadOperation())
            {
                range = 16f;
                return Noise.Reloading;
            }

            BotOwner bot = x.AIData?.BotOwner;
            LootingBots.Components.LootingBrain brain = bot != null ? x.GetComponent<LootingBots.Components.LootingBrain>() : null;
            if (brain != null && brain.LootTaskRunning)
            {
                range = 14f;
                return Noise.Looting;
            }

            if (!prone && pose > 0.7f && s.Speed > 2.2f)
            {
                range = 14f;
                return Noise.Steps;
            }

            return Noise.None;
        }

        /// <summary>How well the bot hears it: 0 - not at all, up to 1 - right next to it.</summary>
        private static float Heard(BotOwner bot, BotProfile p, Vector3 at, float range)
        {
            Vector3 ear = bot.Position + Vector3.up * 1.5f;
            float dy = Mathf.Abs(at.y - bot.Position.y);
            float dist = Vector3.Distance(bot.Position, at);
            float reach = range * (0.85f + 0.08f * (int)p.Skill);
            if (dy > 3.5f)
            {
                reach *= 0.4f;
            }

            if (dist > reach)
            {
                return 0f;
            }

            if (Physics.Linecast(ear, at + Vector3.up * 1.2f, LayersMaskController.HighPolyWithTerrainMask))
            {
                reach *= 0.5f;
                if (dist > reach)
                {
                    return 0f;
                }
            }

            return 1f - dist / reach;
        }

        private static void React(BotOwner bot, BotProfile p, Player maker, Noise noise, Vector3 where, float loudness)
        {
            float now = Time.time;
            _lastHeard[bot.ProfileId] = (where, now);
            _heard[noise] = _heard.TryGetValue(noise, out int h) ? h + 1 : 1;
            bool human = Net.IsHuman(maker);
            if (human)
            {
                OfYou++;
            }

            // An AI scav on patrol does not leave its area for a noise (it would for a fight - ScavPatrol calls)
            if (ScavPatrol.Applies(bot, out _) && !ScavPatrol.Inside(ScavPatrol.AreaOf(bot, p), where))
            {
                _cooldown[bot.ProfileId] = now + 20f;
                return;
            }

            if (!_goingTo.TryGetValue(maker.ProfileId, out HashSet<string> going))
            {
                _goingTo[maker.ProfileId] = going = new HashSet<string>();
            }

            going.RemoveWhere(id => !_active.Contains(id));
            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            float aggr = d != null ? d[DoctrineDeck.Aggression] : p.Aggression;
            float precision = d != null ? d[DoctrineDeck.Precision] : 0.3f + 0.15f * (int)p.Skill;
            float bold = aggr + (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter ? 0.2f : 0f) - (p.Style == PlayStyle.Rat ? 0.25f : 0f);
            LootingBots.Components.LootingBrain bag = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            bool fullBag = bag != null && !bag.HasFreeSpace;
            Dictionary<Reaction, float> w = new Dictionary<Reaction, float>
            {
                [Reaction.Check] = going.Count >= 2 ? 0f : Mathf.Max(0f, bold) * (0.6f + loudness),
                [Reaction.GoRound] = going.Count >= 2 ? 0f : Mathf.Max(0f, bold) * precision * 0.9f,
                [Reaction.Listen] = 0.45f,
                [Reaction.SlipAway] = (p.Style == PlayStyle.Rat ? 0.6f : 0.08f) + (fullBag ? 0.4f : 0f) + (1f - aggr) * 0.2f,
                [Reaction.Ignore] = 0.25f + (noise == Noise.Steps ? 0.3f : 0f) + (p.Kind == BotKind.Scav ? 0.2f : 0f),
            };

            Reaction r = Pick(w);
            _reactions[r] = _reactions.TryGetValue(r, out int c) ? c + 1 : 1;
            _cooldown[bot.ProfileId] = now + 60f + (float)_rng.NextDouble() * 30f;
            float dist = Vector3.Distance(bot.Position, where);
            Log.Info($"LR Noise: {p.Name} ({p.Kind} {p.Style}, {p.Skill}) hears {(human ? "you" : maker.Profile?.Nickname)} {Words(noise)} {dist:0} m away -> {r}");
            if (r == Reaction.Ignore)
            {
                return;
            }

            TacticalTask task = null;
            switch (r)
            {
                case Reaction.Check:
                {
                    Vector3? stand = ScavPatrol.Short(bot, where, 6f + (float)_rng.NextDouble() * 6f);
                    if (stand != null)
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand, LookAt = where, Speed = 1f, Sprint = dist > 25f, SprintBeyond = 20f, MovePose = 1f,
                            Until = now + dist / 2.5f + 12f,
                        };
                    }

                    break;
                }

                case Reaction.GoRound:
                {
                    Vector3 dir = bot.Position - where;
                    dir.y = 0f;
                    Vector3 side = Quaternion.Euler(0f, (_rng.NextDouble() < 0.5 ? 1f : -1f) * (65f + (float)_rng.NextDouble() * 30f), 0f) * dir.normalized;
                    if (NavMesh.SamplePosition(where + side * (10f + (float)_rng.NextDouble() * 8f), out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = hit.position, LookAt = where, Speed = 1f, Sprint = true, SprintBeyond = 20f, MovePose = 1f,
                            Until = now + dist / 2.5f + 18f,
                        };
                    }

                    break;
                }

                case Reaction.Listen:
                    task = new TacticalTask { Kind = TaskKind.Listen, LookAt = where, Until = now + 3f + (float)_rng.NextDouble() * 3f };
                    break;

                case Reaction.SlipAway:
                {
                    Vector3 away = bot.Position - where;
                    away.y = 0f;
                    if (NavMesh.SamplePosition(bot.Position + away.normalized * (30f + (float)_rng.NextDouble() * 20f), out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = hit.position, LookAt = where, Speed = 1f, Sprint = true, SprintBeyond = 0f, MovePose = 1f,
                            Until = now + 25f,
                        };
                    }

                    break;
                }
            }

            if (task != null && TacticalTasks.Set(bot, task))
            {
                _active.Add(bot.ProfileId);
                if (r == Reaction.Check || r == Reaction.GoRound)
                {
                    going.Add(bot.ProfileId);
                }

                if (r != Reaction.SlipAway)
                {
                    SquadBrain.Say(bot, r == Reaction.Listen ? EPhraseTrigger.Stop : EPhraseTrigger.Going, 0.35f);
                }
            }
        }

        private static string Words(Noise n)
        {
            switch (n)
            {
                case Noise.Running: return "running";
                case Noise.Steps: return "walking";
                case Noise.Door: return "at a door";
                case Noise.Looting: return "looting";
                default: return "reloading";
            }
        }

        private static Reaction Pick(Dictionary<Reaction, float> w)
        {
            float total = w.Values.Sum();
            float roll = (float)_rng.NextDouble() * total;
            foreach (KeyValuePair<Reaction, float> kv in w)
            {
                roll -= kv.Value;
                if (roll <= 0f)
                {
                    return kv.Key;
                }
            }

            return Reaction.Ignore;
        }
    }
}
