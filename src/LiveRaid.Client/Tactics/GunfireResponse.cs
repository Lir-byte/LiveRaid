using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    public enum FightReaction
    {
        Rush,
        Approach,
        Flank,
        Ambush,
        Hold,
        Leave,
        Ignore,
    }

    /// <summary>
    /// Players hear a firefight from far away and decide what to do about it: run in and take it, creep closer and listen, circle round to
    /// catch the winner, stay put and watch, or go the other way. Here every bot that hears a fight makes that decision from its character,
    /// its kit (confidence), how dangerous the fight sounds and who is with it.
    ///
    /// Safeguards: only squad leaders and solos react (followers follow), at most a few responders per fight, a cap on how many bots
    /// are on their way to fights at once, a cooldown per bot, only calm healthy bots, nothing in the last minutes of the raid,
    /// every task has a timeout and ends the moment SAIN sees an enemy.
    /// </summary>
    public static class GunfireResponse
    {
        private class Fight
        {
            public int Id;
            public Vector3 Pos;
            public float Start;
            public float Last;
            public float Range;
            public int Shots;
            public bool Human;
            public readonly HashSet<string> Shooters = new HashSet<string>();
            public readonly List<float> Times = new List<float>();
            public readonly HashSet<string> Considered = new HashSet<string>();
            public readonly HashSet<string> Responders = new HashSet<string>();
        }

        private const int MaxRespondersPerFight = 3;
        private const float BotCooldownSeconds = 120f;
        private const float TaskTimeoutSeconds = 170f;

        private static readonly List<Fight> _fights = new List<Fight>();
        private static readonly Dictionary<string, float> _cooldown = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _active = new Dictionary<string, float>();
        private static readonly HashSet<string> _ambushers = new HashSet<string>();
        private static readonly System.Random _rng = new System.Random();
        private static int _nextId = 1;
        private static float _next;

        public static int HeardFights { get; private set; }
        public static int Responses { get; private set; }
        public static readonly Dictionary<FightReaction, int> ReactionCounts = new Dictionary<FightReaction, int>();

        public static void Clear()
        {
            _fights.Clear();
            _cooldown.Clear();
            _active.Clear();
            _ambushers.Clear();
            ReactionCounts.Clear();
            HeardFights = Responses = Listens = 0;
            _nextId = 1;
            _next = 0;
        }

        public static void OnShot(Vector3 pos, float time, string shooterId, float range, bool human)
        {
            Fight fight = _fights.LastOrDefault(f => time - f.Last < 25f && (f.Pos - pos).sqrMagnitude < 70f * 70f);
            if (fight == null)
            {
                fight = new Fight { Id = _nextId++, Pos = pos, Start = time };
                _fights.Add(fight);
            }

            // The fight's centre drifts with the shooting
            fight.Pos = Vector3.Lerp(fight.Pos, pos, 0.25f);
            fight.Last = time;
            fight.Shots++;
            fight.Range = Mathf.Max(fight.Range, range);
            fight.Human |= human;
            fight.Shooters.Add(shooterId);
            fight.Times.Add(time);
            if (fight.Times.Count > 80)
            {
                fight.Times.RemoveAt(0);
            }
        }

        private static float Intensity(Fight f)
        {
            float now = Time.time;
            int recent = 0;
            for (int i = f.Times.Count - 1; i >= 0 && now - f.Times[i] < 20f; i--)
            {
                recent++;
            }

            return Mathf.Clamp01(recent / 16f);
        }

        public static void Tick()
        {
            if (!Settings.GunfireResponse.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            float now = Time.time;
            _fights.RemoveAll(f => now - f.Last > 180f);
            WatchActive(world);
            if (RaidHelpers.GetRemainingRaidTimeSeconds() < 240f)
            {
                return;
            }

            List<Fight> live = _fights.Where(f => now - f.Last < 15f && f.Shots >= 2 && f.Responders.Count < MaxRespondersPerFight)
                .OrderByDescending(f => f.Last).Take(3).ToList();
            if (live.Count == 0)
            {
                return;
            }

            List<(BotOwner bot, BotProfile p)> candidates = new List<(BotOwner, BotProfile)>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p != null && p.Kind != BotKind.Other)
                {
                    candidates.Add((bot, p));
                }
            }

            int maxActive = Math.Max(3, candidates.Count / 4);
            foreach (Fight fight in live)
            {
                foreach ((BotOwner bot, BotProfile p) in candidates)
                {
                    if (_active.Count >= maxActive || fight.Responders.Count >= MaxRespondersPerFight)
                    {
                        break;
                    }

                    if (fight.Considered.Contains(bot.ProfileId))
                    {
                        continue;
                    }

                    // The bot's own squad is shooting there: that is its fight, not a distant one (SAIN and the squad logic handle it)
                    if (SquadIsInFight(bot, fight))
                    {
                        fight.Considered.Add(bot.ProfileId);
                        continue;
                    }

                    float dist = Vector3.Distance(bot.Position, fight.Pos);
                    float hearing = Mathf.Min(300f, fight.Range * Settings.GunfireRange.Value);
                    if (Settings.FightersOnly.Value)
                    {
                        hearing = Mathf.Min(hearing, ReactionRadius(bot, p));
                    }

                    if (dist > hearing || dist < 20f || !CanRespond(bot, p))
                    {
                        continue;
                    }

                    // Shooting over or under the bot's head (another storey) is none of its business
                    if (Settings.FloorAware.Value && FloorsApart(bot.Position, fight.Pos))
                    {
                        fight.Considered.Add(bot.ProfileId);
                        continue;
                    }

                    // Heard it at all? Closer and louder = more likely; alert, experienced bots catch more
                    fight.Considered.Add(bot.ProfileId);
                    float alert = 0.7f + 0.12f * (int)p.Skill + 0.2f * p.Memory * 0.5f;
                    float pHear = Mathf.Pow(Mathf.Clamp01(1f - dist / hearing), 0.6f) * Mathf.Clamp(alert, 0.5f, 1.2f);
                    if (_rng.NextDouble() > pHear)
                    {
                        continue;
                    }

                    HeardFights++;
                    React(bot, p, fight, dist);
                }
            }
        }

        /// <summary>
        /// How far shooting is a reason to react at all. Far shooting is no threat to a bot that is not out to fight: a looter or a quester does not drop what it is doing
        /// for a fight 200 m away. Rushers and hunters go for any fight within ~170 m, snipers listen furthest (~200 m), scavs and busy bots (looting, mid-quest) less.
        /// </summary>
        internal static float ReactionRadius(BotOwner bot, BotProfile p)
        {
            float r;
            switch (p.Style)
            {
                case PlayStyle.Hunter: r = 175f; break;
                case PlayStyle.Rusher: r = 165f; break;
                case PlayStyle.Sniper: r = 200f; break;
                case PlayStyle.Camper: r = 110f; break;
                case PlayStyle.Explorer: r = 105f; break;
                case PlayStyle.Rat: r = 95f; break;
                case PlayStyle.Looter: r = 80f; break;
                case PlayStyle.Quester: r = 70f; break;
                default: r = 90f; break;
            }

            r *= 0.8f + 0.4f * Mathf.Clamp01(p.Aggression);
            if (p.Kind == BotKind.Scav)
            {
                r *= 0.7f;
            }

            // Busy with its own thing
            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            if (brain != null && (brain.LootTaskRunning || brain.HasActiveLootable))
            {
                r *= 0.6f;
            }

            return r;
        }

        /// <summary>Another storey: 3+ m of height at building scale (45 m), 6+ m up to 100 m.</summary>
        internal static bool FloorsApart(Vector3 a, Vector3 b)
        {
            float dy = Mathf.Abs(a.y - b.y);
            a.y = 0f;
            b.y = 0f;
            float flat = Vector3.Distance(a, b);
            return (dy >= 3.2f && flat < 45f) || (dy >= 6f && flat < 100f);
        }

        private static bool SquadIsInFight(BotOwner bot, Fight fight)
        {
            if (fight.Shooters.Contains(bot.ProfileId))
            {
                return true;
            }

            BotsGroup group = bot.BotsGroup;
            if (group == null)
            {
                return false;
            }

            for (int i = 0; i < group.MembersCount; i++)
            {
                BotOwner member = group.Member(i);
                if (member != null && fight.Shooters.Contains(member.ProfileId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanRespond(BotOwner bot, BotProfile p)
        {
            if (_cooldown.TryGetValue(bot.ProfileId, out float until) && Time.time < until)
            {
                return false;
            }

            if (_active.ContainsKey(bot.ProfileId) || TacticalTasks.TryGet(bot, out _) || Bots.ContainerUnlockLayer.Busy.Contains(bot.ProfileId)
                || !TacticalTasks.BotIsCalm(bot))
            {
                return false;
            }

            // Followers follow their leader; only leaders and solos go to fights
            if (TacticalTasks.SquadLeaderOf(bot) != null)
            {
                return false;
            }

            if (bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.4f || bot.Medecine?.FirstAid?.Have2Do == true)
            {
                return false;
            }

            BotObjectiveManager objectives = bot.GetObjectiveManager();
            BotExtractMonitor extract = objectives?.BotMonitor?.GetMonitor<BotExtractMonitor>();
            if (extract != null && extract.IsTryingToExtract)
            {
                return false;
            }

            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            if (brain != null && brain.LootTaskRunning)
            {
                return false;
            }

            return true;
        }

        private static float BaseInterest(BotProfile p)
        {
            float v;
            switch (p.Style)
            {
                case PlayStyle.Hunter: v = 0.8f; break;
                case PlayStyle.Rusher: v = 0.65f; break;
                case PlayStyle.Explorer: v = 0.4f; break;
                case PlayStyle.Looter: v = 0.4f; break;
                case PlayStyle.Quester: v = 0.3f; break;
                case PlayStyle.Sniper: v = 0.25f; break;
                case PlayStyle.Camper: v = 0.2f; break;
                default: v = 0.15f; break;
            }

            v += (p.Aggression - 0.5f) * 0.3f;
            if (p.Kind == BotKind.Scav)
            {
                v *= 0.5f;
            }

            return Mathf.Clamp01(v);
        }

        /// <summary>How sure of itself the bot feels: its kit, skill, health and company.</summary>
        private static float Confidence(BotOwner bot, BotProfile p)
        {
            float kit = LootBrain.VisibleValue(bot.GetPlayer?.Equipment);
            float conf = Mathf.Clamp01(Mathf.Log10(1f + kit / 100000f) / 1.2f);
            conf += 0.1f * (int)p.Skill;
            int mates = Mathf.Max(0, LootBrain.SquadIds(bot).Count - 1);
            conf += Mathf.Min(0.3f, 0.12f * mates);
            float health = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            conf *= 0.5f + 0.5f * health;
            return Mathf.Clamp01(conf);
        }

        private static void React(BotOwner bot, BotProfile p, Fight fight, float dist)
        {
            // An AI scav on patrol goes only to fights at its area (up to 60 m past its edge): online scavs do not cross the map for shooting
            if (ScavPatrol.Applies(bot, out _))
            {
                ScavPatrol.Area area = ScavPatrol.AreaOf(bot, p);
                Vector3 off = fight.Pos - area.Home;
                off.y = 0f;
                if (off.magnitude > area.Radius + 60f)
                {
                    _cooldown[bot.ProfileId] = Time.time + 40f;
                    return;
                }
            }

            float interest = BaseInterest(p);
            float conf = Confidence(bot, p);
            float danger = Mathf.Clamp01(Intensity(fight) * 0.7f + (fight.Shooters.Count >= 3 ? 0.3f : 0f) + (dist < 120f ? 0.15f : 0f));

            bool brave = p.Aggression >= 0.6f && conf >= 0.45f && (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter || p.Style == PlayStyle.Explorer);
            Dictionary<FightReaction, float> w = new Dictionary<FightReaction, float>
            {
                [FightReaction.Rush] = brave ? interest * conf * (1f - 0.4f * danger) * p.Aggression * 1.4f : 0f,
                [FightReaction.Approach] = interest * (0.6f + 0.4f * conf),
                [FightReaction.Flank] = fight.Shooters.Count >= 2 ? interest * conf * (p.Skill >= SkillTier.Experienced ? 0.8f : 0.35f) : 0f,
                [FightReaction.Ambush] = AmbushWeight(p, fight, conf),
                [FightReaction.Hold] = (1f - interest) * 0.7f * 0.4f,
                [FightReaction.Leave] = danger * (1f - conf) * (p.Style == PlayStyle.Rat ? 1.2f : 0.8f) + (p.Skill == SkillTier.Novice ? 0.15f : 0f),
                [FightReaction.Ignore] = 0.5f * (1f - interest),
            };

            // The squad's doctrine as the leader plays it: storm troopers and hunters go to the sound, the quiet ones stay out or wait to pick the bodies
            DoctrineDeck.Member doctrine = SquadDoctrines.Of(bot);
            if (doctrine != null)
            {
                float third = doctrine[DoctrineDeck.ThirdParty];
                float aggr = doctrine[DoctrineDeck.Aggression];
                w[FightReaction.Rush] *= 0.3f + 1.4f * aggr * third;
                w[FightReaction.Approach] *= 0.4f + 1.2f * third;
                w[FightReaction.Flank] *= 0.5f + doctrine[DoctrineDeck.Precision];
                w[FightReaction.Ambush] *= doctrine.Squad.Doctrine.Kind == DoctrineKind.PvpHunters || doctrine.Squad.Doctrine.Kind == DoctrineKind.Rats ? 2f : 0.8f;
                w[FightReaction.Leave] *= 1.6f - third;
                w[FightReaction.Ignore] *= 1.5f - third;
                if (third > 0.8f && aggr > 0.7f && !brave && conf >= 0.35f)
                {
                    // A storm squad goes even without a rusher's nerve of its own
                    w[FightReaction.Rush] = interest * conf * aggr;
                }
            }

            // A player scav keeps out of PMC fights - it waits for the winner or leaves, and comes for the bodies later (PlayerScavLife);
            // the "chad scav" goes for the fight
            if (p.Kind == BotKind.PlayerScav && PlayerScavLife.On)
            {
                if (PlayerScavLife.IsChad(bot))
                {
                    w[FightReaction.Rush] = Mathf.Max(w[FightReaction.Rush], interest * conf * 0.8f) * 1.6f;
                    w[FightReaction.Approach] *= 1.3f;
                }
                else
                {
                    w[FightReaction.Rush] *= 0.2f;
                    w[FightReaction.Approach] *= 0.5f;
                    w[FightReaction.Flank] *= 0.5f;
                    w[FightReaction.Leave] *= 1.5f;
                    w[FightReaction.Ambush] *= 1.4f;
                }
            }

            // What it stands to lose: a full kit with a bag of loot goes to fewer fights, a bum with nothing to lose to more (PlayerSense)
            float stakes = PlayerSense.Stakes(bot);
            if (stakes > 0f)
            {
                w[FightReaction.Rush] *= 1f - 0.6f * stakes;
                w[FightReaction.Approach] *= 1f - 0.4f * stakes;
                w[FightReaction.Leave] *= 1f + stakes;
            }
            else if (stakes < 0f)
            {
                w[FightReaction.Rush] *= 1.3f;
                w[FightReaction.Approach] *= 1.2f;
            }

            // The raid's moods: a close call, a kill streak, a rich bag (RaidMood)
            RaidMood.Gunfire(bot, w);

            // Never walk into a fight that sounds like a war with a weak kit
            if (danger > 0.7f && conf < 0.4f)
            {
                w[FightReaction.Rush] = 0;
                w[FightReaction.Approach] *= 0.3f;
            }

            FightReaction reaction = Pick(w);
            ReactionCounts[reaction] = ReactionCounts.TryGetValue(reaction, out int n) ? n + 1 : 1;
            Log.Info($"LR Gunfire: {p.Name} ({p.Style}, {p.Skill}, {p.Kind}) hears a fight {dist:0} m away ({fight.Shots} shots, {fight.Shooters.Count} shooters" +
                     $"{(fight.Human ? ", you" : "")}) -> {reaction} [interest {interest:0.00}, confidence {conf:0.00}, danger {danger:0.00}]");

            _cooldown[bot.ProfileId] = Time.time + (reaction == FightReaction.Ignore ? 40f : BotCooldownSeconds);
            if (reaction == FightReaction.Ignore)
            {
                return;
            }

            Responses++;
            fight.Responders.Add(bot.ProfileId);
            if (StopAndListen(bot, p, fight, doctrine, () => Execute(bot, p, fight, reaction, conf, danger, dist)))
            {
                return;
            }

            Execute(bot, p, fight, reaction, conf, danger, dist);
        }

        public static int Listens { get; private set; }

        /// <summary>
        /// "Stop, I hear it" (F12 -> 8 "Stop and listen"): a squad that hears a fight halts for 3-6 s, everyone turning to the sound, then the leader's
        /// reaction goes ahead. A drilled squad does it most times, randoms rarely, a lone bot not at all (it reacts at once as before).
        /// </summary>
        private static bool StopAndListen(BotOwner leader, BotProfile p, Fight fight, DoctrineDeck.Member doctrine, Action then)
        {
            if (!Settings.SquadListen.Value || leader.Boss?.Followers == null || leader.Boss.Followers.Count == 0)
            {
                return false;
            }

            float precision = doctrine != null ? doctrine[DoctrineDeck.Precision] : Mathf.Max(0f, SquadBrain.DisciplineOf(leader));
            float cohesion = doctrine != null ? doctrine[DoctrineDeck.Cohesion] : 0.5f;
            if (_rng.NextDouble() > 0.15f + 0.55f * precision + 0.2f * cohesion)
            {
                return false;
            }

            float seconds = 3f + (float)_rng.NextDouble() * 3f;
            TacticalTask listen = new TacticalTask
            {
                Kind = TaskKind.Listen,
                LookAt = fight.Pos,
                Until = Time.time + seconds,
                OnFinished = b => then(),
            };
            if (!TacticalTasks.Set(leader, listen))
            {
                return false;
            }

            int stopped = 1;
            foreach (BotOwner f in leader.Boss.Followers)
            {
                if (f != null && !f.IsDead && TacticalTasks.BotIsCalm(f)
                    && TacticalTasks.Set(f, new TacticalTask { Kind = TaskKind.Listen, LookAt = fight.Pos, Until = Time.time + seconds + (float)_rng.NextDouble() }))
                {
                    stopped++;
                }
            }

            Listens++;
            SquadBrain.Say(leader, EPhraseTrigger.Stop, 0.5f);
            Log.Info($"LR Squad: {p.Name}'s squad stops and listens to the fight {Vector3.Distance(leader.Position, fight.Pos):0} m away ({stopped} of them, {seconds:0} s)");
            return true;
        }

        /// <summary>
        /// Lying in wait for the winner is one reaction among the others and a rare one: mostly rats and snipers, a little a hunter, hardly ever the rest;
        /// more for a real fight (two shooters or more), never more than two bots waiting at once.
        /// </summary>
        private static float AmbushWeight(BotProfile p, Fight fight, float conf)
        {
            if (_ambushers.Count >= 2)
            {
                return 0f;
            }

            float w;
            switch (p.Style)
            {
                case PlayStyle.Rat: w = 0.3f; break;
                case PlayStyle.Sniper:
                case PlayStyle.Camper: w = 0.22f; break;
                case PlayStyle.Hunter: w = 0.1f; break;
                case PlayStyle.Looter:
                case PlayStyle.Explorer: w = 0.06f; break;
                default: w = 0.03f; break;
            }

            if (fight.Shooters.Count < 2)
            {
                w *= 0.3f;
            }

            if (p.Kind == BotKind.Scav)
            {
                w *= 0.6f;
            }

            return w * (0.6f + 0.4f * conf);
        }

        private static FightReaction Pick(Dictionary<FightReaction, float> w)
        {
            float total = w.Values.Sum();
            if (total <= 0.0001f)
            {
                return FightReaction.Ignore;
            }

            float roll = (float)_rng.NextDouble() * total;
            foreach (KeyValuePair<FightReaction, float> kv in w)
            {
                roll -= kv.Value;
                if (roll <= 0)
                {
                    return kv.Key;
                }
            }

            return FightReaction.Ignore;
        }

        private static void Execute(BotOwner bot, BotProfile p, Fight fight, FightReaction reaction, float conf, float danger, float dist)
        {
            Vector3 fightPos = fight.Pos;
            float now = Time.time;
            TacticalTask task = null;

            switch (reaction)
            {
                case FightReaction.Rush:
                {
                    Vector3? stand = FindPoint(bot, fightPos, 38f, 0f, out float path);
                    if (stand.HasValue)
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand, LookAt = fightPos, Speed = 1f, Sprint = true, SprintBeyond = 110f, MovePose = 1f,
                            Until = now + path / 4.5f + 40f,
                        };
                    }

                    break;
                }

                case FightReaction.Approach:
                {
                    Vector3? stand = FindPoint(bot, fightPos, 55f + (float)_rng.NextDouble() * 35f, 0f, out float path);
                    if (stand.HasValue)
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand, LookAt = fightPos, Speed = 1f, Sprint = true, SprintBeyond = 70f, MovePose = 1f,
                            Until = now + path / 2.8f + 25f + (float)_rng.NextDouble() * 25f,
                        };
                    }

                    break;
                }

                case FightReaction.Flank:
                {
                    float side = _rng.NextDouble() < 0.5 ? 1f : -1f;
                    Vector3? stand = FindPoint(bot, fightPos, 95f + (float)_rng.NextDouble() * 25f, side * (70f + (float)_rng.NextDouble() * 30f), out float path);
                    if (stand.HasValue)
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand, LookAt = fightPos, Speed = 1f, Sprint = true, SprintBeyond = 70f, MovePose = 1f,
                            Until = now + path / 3.2f + 30f,
                        };
                    }

                    break;
                }

                case FightReaction.Leave:
                {
                    Vector3 away = bot.Position - fightPos;
                    away.y = 0;
                    Vector3 target = bot.Position + away.normalized * (70f + (float)_rng.NextDouble() * 40f);
                    if (NavMesh.SamplePosition(target, out NavMeshHit hit, 15f, NavMesh.AllAreas))
                    {
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = hit.position, LookAt = fightPos, Speed = 1f, Sprint = danger > 0.65f, SprintBeyond = 0f, MovePose = 1f,
                            Until = now + 35f,
                        };
                    }

                    break;
                }

                case FightReaction.Ambush:
                {
                    // The third party: wait at the edge of the fight, in sight of it, for whoever is left standing, then go and take what lies there.
                    // A short stand (the fight is still going on, or has just stopped), counted into the day's plan - after it the bot loots and quests on
                    Vector3? stand = FindPoint(bot, fightPos, 48f + (float)_rng.NextDouble() * 30f, (_rng.NextDouble() < 0.5 ? 1f : -1f) * (20f + (float)_rng.NextDouble() * 35f), out float path);
                    if (stand.HasValue)
                    {
                        float wait = 8f + (float)_rng.NextDouble() * 14f;
                        _ambushers.Add(bot.ProfileId);
                        task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand, LookAt = fightPos, Speed = 1f, Sprint = true, SprintBeyond = 60f, MovePose = 1f,
                            Until = now + path / 3f + wait,
                            Prone = false,
                        };
                        Log.Detail("gunfire", $"{p.Name} lies in wait for the winner of the fight {dist:0} m away ({wait:0} s at the spot)");
                    }

                    break;
                }

                case FightReaction.Hold:
                {
                    // A fight next door is worth a long listen; a far one only a short pause before going on
                    // (up to 80 m: 8-30 s, at 300 m: 3-9 s; a 50 s listen on the spot looked like an AFK player)
                    float nearness = 1f - 0.7f * Mathf.InverseLerp(80f, 300f, dist);
                    float holdSeconds = Mathf.Max(2f, (2.5f + (float)_rng.NextDouble() * 5f) * nearness);
                    task = new TacticalTask
                    {
                        Kind = TaskKind.Investigate, StandAt = null, LookAt = fightPos,
                        Until = now + holdSeconds,
                        // Lying down for a short pause looks odd: only when the bot stays a while
                        Prone = (p.Style == PlayStyle.Camper || p.Style == PlayStyle.Sniper) && holdSeconds > 15f && _rng.NextDouble() < 0.5,
                    };
                    Log.Detail("gunfire", $"{p.Name} holds and listens for {holdSeconds:0} s ({dist:0} m away)");
                    break;
                }
            }

            if (task == null)
            {
                Log.Detail("gunfire", $"{p.Name}: no way to {reaction} found, stays on its plan");
                return;
            }

            string id = bot.ProfileId;
            _active[id] = now;
            task.Cleanup = () => { _active.Remove(id); _ambushers.Remove(id); };
            task.OnFinished = b => Followup(b, p, fight, reaction, conf);
            TacticalTasks.Set(bot, task);
        }

        /// <summary>After closing in: if the shooting stopped, go and see what is left lying around; if it goes on and the bot is bold, creep closer.</summary>
        private static void Followup(BotOwner bot, BotProfile p, Fight fight, FightReaction reaction, float conf)
        {
            if (bot.IsDead || reaction == FightReaction.Hold || reaction == FightReaction.Leave || !TacticalTasks.NoThreat(bot))
            {
                return;
            }

            float now = Time.time;
            bool quiet = now - fight.Last > (reaction == FightReaction.Ambush ? 9f : 20f);
            float finishDist = quiet ? 22f : 32f;
            bool bold = conf >= 0.55f && p.Aggression >= 0.55f;
            if (!quiet && !bold)
            {
                return;
            }

            Vector3? stand = FindPoint(bot, fight.Pos, finishDist, 0f, out float path);
            if (!stand.HasValue)
            {
                return;
            }

            string id = bot.ProfileId;
            _active[id] = now;
            Log.Detail("gunfire", $"{p.Name} follows up on the fight: {(quiet ? "it went quiet, checks the bodies" : "still going, creeps closer")} ({path:0} m)");
            TacticalTasks.Set(bot, new TacticalTask
            {
                Kind = TaskKind.Investigate, StandAt = stand, LookAt = fight.Pos, Speed = 1f, Sprint = true, SprintBeyond = 60f, MovePose = 1f,
                Until = now + path / 2.2f + (quiet ? 20f : 25f),
                Cleanup = () => _active.Remove(id),
                OnFinished = b =>
                {
                    // Whatever is left on the ground now is fair game; the loot brain still decides about each body
                    RoomSweep.ForceScan(b);
                },
            });
        }

        /// <summary>A navmesh point `dist` metres from the fight on the bot's side (rotated by angle), reachable without a huge detour.</summary>
        private static Vector3? FindPoint(BotOwner bot, Vector3 fightPos, float dist, float angle, out float pathLength)
        {
            pathLength = 0;
            Vector3 dir = bot.Position - fightPos;
            dir.y = 0;
            if (dir.sqrMagnitude < 1f)
            {
                dir = bot.LookDirection;
            }

            dir.Normalize();
            NavMeshPath path = new NavMeshPath();
            foreach (float extra in new[] { 0f, 15f, -15f, 30f, -30f })
            {
                Vector3 rotated = Quaternion.Euler(0, angle + extra, 0) * dir;
                Vector3 want = fightPos + rotated * dist;
                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                {
                    continue;
                }

                if (!NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0;
                for (int i = 1; i < path.corners.Length; i++)
                {
                    len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                }

                float straight = Vector3.Distance(bot.Position, hit.position);
                if (len > 400f || len > straight * 2.5f + 30f)
                {
                    continue;
                }

                pathLength = len;
                return hit.position;
            }

            return null;
        }

        /// <summary>Watchdog: a response that has run too long, or whose bot died, is cancelled.</summary>
        private static void WatchActive(GameWorld world)
        {
            if (_active.Count == 0)
            {
                return;
            }

            float now = Time.time;
            foreach (string id in _active.Where(kv => now - kv.Value > TaskTimeoutSeconds).Select(kv => kv.Key).ToList())
            {
                BotOwner bot = world.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id)?.AIData?.BotOwner;
                _active.Remove(id);
                if (bot != null && TacticalTasks.TryGet(bot, out TacticalTask t) && t.Kind == TaskKind.Investigate)
                {
                    TacticalTasks.Cancel(bot, "gunfire response timed out");
                }
            }

            foreach (string id in _active.Keys.ToList())
            {
                if (!world.AllAlivePlayersList.Any(pl => pl.ProfileId == id))
                {
                    _active.Remove(id);
                }
            }
        }
    }
}
