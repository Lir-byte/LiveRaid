using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Boss hunts (F12 -> 8 "Boss hunts"). Online some squads come for Reshala, Glukhar, Sanitar, Tagilla - for the boss's gear and loot.
    /// Bots never did: bosses only met who happened to walk by. Here:
    ///  - the squads of the doctrine "boss hunters" always, about a third of the storm troopers and some PvP hunters make the map's boss their
    ///    goal, 2-8 minutes into the raid (the first loot first);
    ///  - the leader takes the squad there in stages (to some 60 m, then 30 m short of where the boss is), the rest follows; contact is SAIN's fight;
    ///  - the hunt is called off when the squad is badly hurt or down to half, after 14 minutes, or when the boss is dead;
    ///  - a dead boss: hunting squads within 110 m (a bot goes as far back for its own kill) go through his body and his guards' (body jobs).
    /// Bosses, guards and their behaviour are not touched - only who comes for them.
    /// </summary>
    public static class BossHunt
    {
        private class Hunt
        {
            public bool Decided;
            public bool Wants;
            public float StartAt;
            public float Began = -1f;
            public int Stage;
            public bool Over;
        }

        private static readonly Dictionary<int, Hunt> _hunts = new Dictionary<int, Hunt>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Started { get; private set; }
        public static int Arrived { get; private set; }
        public static int CalledOff { get; private set; }
        public static int BossBodies { get; private set; }

        public static string Summary => $"boss hunts: {_hunts.Values.Count(h => h.Wants)} squads meant to hunt, {Started} set off, {Arrived} got close, {CalledOff} called off, " +
                                        $"boss bodies gone through {BossBodies}";

        public static bool On => Settings.Enabled.Value && Settings.BossHunts.Value;

        public static void Clear()
        {
            _hunts.Clear();
            _next = 0f;
            Started = Arrived = CalledOff = BossBodies = 0;
        }

        public static bool IsBoss(WildSpawnType role) => role.ToString().StartsWith("boss", System.StringComparison.OrdinalIgnoreCase);

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<Player> bosses = world.AllAlivePlayersList.Where(x => x?.AIData?.BotOwner != null && !x.AIData.BotOwner.IsDead && IsBoss(x.Profile.Info.Settings.Role)).ToList();
            if (bosses.Count == 0)
            {
                return;
            }

            float elapsed = RaidHelpers.GetRaidElapsedSeconds();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind != BotKind.Pmc || Spectator.TakeOver.IsTaken(bot) || TacticalTasks.SquadLeaderOf(bot) != null)
                {
                    continue;
                }

                Hunt h = HuntOf(bot, p);
                if (!h.Wants || h.Over || elapsed < h.StartAt)
                {
                    continue;
                }

                string why = CallOff(bot, h);
                if (why != null)
                {
                    h.Over = true;
                    CalledOff++;
                    Log.Info($"LR Boss: {p.Name}'s squad calls off its boss hunt - {why}");
                    continue;
                }

                if (TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || ExtractPlanner.IsLeaving(bot))
                {
                    continue;
                }

                Player boss = bosses.OrderBy(b => (b.Position - bot.Position).sqrMagnitude).First();
                float dist = Vector3.Distance(bot.Position, boss.Position);
                if (dist < 35f || h.Stage >= 3)
                {
                    // Close enough: it is the fight now (or nothing happened at three approaches - the hunt is done)
                    if (h.Stage >= 3 || dist < 35f)
                    {
                        h.Over = true;
                        Arrived++;
                        Log.Info($"LR Boss: {p.Name}'s squad is at {boss.Profile?.Nickname}'s place ({dist:0} m)");
                    }

                    continue;
                }

                float shortBy = dist > 120f ? 60f + (float)_rng.NextDouble() * 20f : 25f + (float)_rng.NextDouble() * 10f;
                Vector3? stand = ScavPatrol.Short(bot, boss.Position, shortBy);
                if (stand == null)
                {
                    h.Stage++;
                    continue;
                }

                TacticalTask task = new TacticalTask
                {
                    Kind = TaskKind.Investigate, StandAt = stand, LookAt = boss.Position, Speed = 1f, Sprint = true, SprintBeyond = 60f, MovePose = 1f,
                    Until = Time.time + dist / 3f + 30f,
                };
                if (TacticalTasks.Set(bot, task))
                {
                    if (h.Began < 0f)
                    {
                        h.Began = Time.time;
                        Started++;
                        SquadBrain.Say(bot, EPhraseTrigger.FollowMe, 0.6f);
                    }

                    h.Stage++;
                    Log.Info($"LR Boss: {p.Name}'s squad goes for {boss.Profile?.Nickname} ({boss.Profile?.Info?.Settings?.Role}), {dist:0} m away - stage {h.Stage}");
                }
            }
        }

        private static Hunt HuntOf(BotOwner bot, BotProfile p)
        {
            int squad = SquadTies.SquadOf(bot.ProfileId);
            if (!_hunts.TryGetValue(squad, out Hunt h))
            {
                _hunts[squad] = h = new Hunt();
            }

            if (!h.Decided)
            {
                DoctrineDeck.Squad d = SquadDoctrines.SquadOf(bot);
                if (Settings.SquadDoctrines.Value && d == null)
                {
                    return h;
                }

                h.Decided = true;
                double chance = d?.Doctrine == null ? (p.Style == PlayStyle.Hunter || p.Style == PlayStyle.Rusher ? 0.15 : 0.03)
                    : d.Doctrine.BossHunt ? 1.0
                    : d.Doctrine.Kind == DoctrineKind.Chads ? 0.35
                    : d.Doctrine.Kind == DoctrineKind.PvpHunters || d.Doctrine.Kind == DoctrineKind.Tanks ? 0.15
                    : d.Doctrine.Kind == DoctrineKind.Clan ? 0.1
                    : 0.0;
                h.Wants = _rng.NextDouble() < chance;
                h.StartAt = 120f + (float)_rng.NextDouble() * 360f;
                if (h.Wants)
                {
                    Log.Info($"LR Boss: {p.Name}'s squad{(d?.Doctrine != null ? " (\"" + d.Doctrine.Title + "\")" : "")} means to hunt the boss, from minute {h.StartAt / 60f:0}");
                }
            }

            return h;
        }

        private static string CallOff(BotOwner bot, Hunt h)
        {
            if (h.Began > 0f && Time.time - h.Began > 840f)
            {
                return "14 minutes on it";
            }

            float hp = bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f;
            if (hp < 0.55f)
            {
                return $"the leader is hurt ({hp:P0})";
            }

            List<string> mates = SquadTies.MatesOf(bot.ProfileId).ToList();
            if (mates.Count >= 2)
            {
                int alive = Singleton<GameWorld>.Instance.AllAlivePlayersList.Count(x => mates.Contains(x.ProfileId));
                if (alive * 2 <= mates.Count)
                {
                    return $"down to {alive} of {mates.Count}";
                }
            }

            return null;
        }

        /// <summary>A boss is dead: hunting squads within 110 m go through his body and his guards' around it.</summary>
        public static void OnKill(KillEvent kill)
        {
            if (!On || kill == null || !IsBoss(kill.VictimRole))
            {
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            List<KillEvent> bodies = WorldEvents.Kills.Where(k => !k.VictimPmc && !k.VictimHuman && (k.Position - kill.Position).sqrMagnitude < 40f * 40f
                                                                  && Time.time - k.Time < 300f).ToList();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind != BotKind.Pmc || (bot.Position - kill.Position).sqrMagnitude > 110f * 110f
                    || !_hunts.TryGetValue(SquadTies.SquadOf(bot.ProfileId), out Hunt h) || !h.Wants)
                {
                    continue;
                }

                h.Over = true;
                foreach (KillEvent body in bodies)
                {
                    LootJobs.Add(bot, body, LootJobs.Kind.OwnKill);
                }

                BossBodies++;
                Log.Info($"LR Boss: {kill.VictimName} is dead - {p.Name} will go through his body and his guards' ({bodies.Count} bodies)");
            }
        }
    }
}
