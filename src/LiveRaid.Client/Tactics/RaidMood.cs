using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Moods over the raid (F12 -> 4 "Moods over the raid"). Each PMC and player scav has, by chance (its doctrine and character weigh in), some of
    /// four natures - none is a given, every bot is different:
    ///  - vengeful: remembers who shot it and, once things are quiet, goes looking for him where the shots came from (twice at most, 20 s-5 min after);
    ///  - shaken by a close call: after dropping under 25 % health and living through it, careful for the rest of the raid - fewer fights, less nerve,
    ///    leaves sooner;
    ///  - streaky: after 2+ kills in 10 minutes bolder (more nerve, goes to fights) but sloppier (stays longer on one spot, cuts across open ground);
    ///    it wears off 8 minutes after the last kill;
    ///  - a hoarder: with a rich bag it guards it - avoids fights and heads out sooner (the others care far less about what they carry).
    /// The others are read by CombatSense (nerve), GunfireResponse (going to fights), ExtractPlanner (leaving), PlayerSense (angles, stakes), CoverRoute.
    /// </summary>
    public static class RaidMood
    {
        private class Mood
        {
            public bool Vengeful;
            public bool Shakeable;
            public bool Streaky;
            public bool Hoarder;

            public bool NearDeath;
            public bool Shaken;
            public readonly List<float> Kills = new List<float>();

            public string Grudge;
            public string GrudgeName;
            public Vector3 GrudgeAt;
            public float GrudgeSince;
            public int Hunts;
            public float LastHitSeen;
        }

        private static readonly Dictionary<string, Mood> _moods = new Dictionary<string, Mood>();
        private static float _next;

        public static int Grudges { get; private set; }
        public static int Hunts { get; private set; }
        public static int ShakenCount { get; private set; }
        public static int Streaks { get; private set; }

        public static string Summary
        {
            get
            {
                int v = _moods.Values.Count(m => m.Vengeful), s = _moods.Values.Count(m => m.Shakeable), k = _moods.Values.Count(m => m.Streaky), h = _moods.Values.Count(m => m.Hoarder);
                return $"moods: {_moods.Count} bots (vengeful {v}, shakeable {s}, streaky {k}, hoarders {h}); grudges {Grudges}, hunts for them {Hunts}, " +
                       $"shaken by a close call {ShakenCount}, kill streaks {Streaks}";
            }
        }

        private static bool On => Settings.Enabled.Value && Settings.RaidMoods.Value;

        public static void Clear()
        {
            _moods.Clear();
            _next = 0f;
            Grudges = Hunts = ShakenCount = Streaks = 0;
        }

        private static Mood Get(BotOwner bot)
        {
            if (bot?.ProfileId == null)
            {
                return null;
            }

            if (_moods.TryGetValue(bot.ProfileId, out Mood m))
            {
                return m;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
            {
                return null;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            System.Random rng = new System.Random(bot.ProfileId.GetHashCode() ^ 0x6d00d);
            float revenge = d != null ? d[DoctrineDeck.Revenge] : p.Aggression;
            float grit = d != null ? d[DoctrineDeck.Grit] : 0.3f + 0.4f * p.Aggression;
            float aggr = d != null ? d[DoctrineDeck.Aggression] : p.Aggression;
            float greed = d != null ? d[DoctrineDeck.Greed] : Mathf.Clamp01(p.LootGreed / 2f);
            m = new Mood
            {
                Vengeful = rng.NextDouble() < 0.1 + 0.4 * revenge,
                Shakeable = rng.NextDouble() < 0.65 - 0.4 * grit,
                Streaky = rng.NextDouble() < 0.15 + 0.35 * aggr,
                Hoarder = rng.NextDouble() < 0.2 + 0.4 * greed,
            };
            _moods[bot.ProfileId] = m;
            return m;
        }

        private static Mood Of(BotOwner bot) => On ? Get(bot) : null;

        private static bool OnStreak(Mood m) =>
            m != null && m.Streaky && m.Kills.Count >= 2 && Time.time - m.Kills[m.Kills.Count - 1] < 480f && m.Kills[m.Kills.Count - 1] - m.Kills[m.Kills.Count - 2] < 600f;

        private static bool Rich(BotOwner bot) => PlayerSense.Stakes(bot) > 0.2f;

        // ---------------- what the others read ----------------

        public static float CourageDelta(BotOwner bot)
        {
            Mood m = Of(bot);
            if (m == null)
            {
                return 0f;
            }

            float d = 0f;
            if (m.Shaken)
            {
                d -= 0.35f;
            }

            if (OnStreak(m))
            {
                d += 0.3f;
            }

            if (m.Hoarder && Rich(bot))
            {
                d -= 0.2f;
            }

            return d;
        }

        public static void Gunfire(BotOwner bot, Dictionary<FightReaction, float> w)
        {
            Mood m = Of(bot);
            if (m == null)
            {
                return;
            }

            if (m.Shaken)
            {
                w[FightReaction.Rush] *= 0.4f;
                w[FightReaction.Approach] *= 0.6f;
                w[FightReaction.Leave] *= 1.6f;
            }

            if (OnStreak(m))
            {
                w[FightReaction.Rush] *= 1.5f;
                w[FightReaction.Approach] *= 1.3f;
                w[FightReaction.Leave] *= 0.6f;
            }

            if (m.Hoarder && Rich(bot))
            {
                w[FightReaction.Rush] *= 0.5f;
                w[FightReaction.Approach] *= 0.7f;
                w[FightReaction.Leave] *= 1.5f;
            }
        }

        public static float ExtractUrge(BotOwner bot, List<string> reasons)
        {
            Mood m = Of(bot);
            if (m == null)
            {
                return 0f;
            }

            float u = 0f;
            if (m.Shaken)
            {
                u += 0.3f;
                reasons.Add("shaken by a close call");
            }

            if (m.Hoarder && PlayerSense.Stakes(bot) > 0.4f)
            {
                u += 0.25f;
                reasons.Add("guards a rich bag");
            }

            return u;
        }

        /// <summary>On a kill streak: sloppier (stays longer on one spot, cuts across the open).</summary>
        public static bool Careless(BotOwner bot) => OnStreak(Of(bot));

        /// <summary>How much what it carries weighs on it: a hoarder a lot, the rest a little (PlayerSense.Stakes, the careful side).</summary>
        public static float StakesWeight(BotOwner bot)
        {
            Mood m = Of(bot);
            return m == null ? 1f : m.Hoarder ? 1.2f : 0.35f;
        }

        // ---------------- events ----------------

        public static void OnKill(Player killer)
        {
            Mood m = Of(killer?.AIData?.BotOwner);
            if (m == null)
            {
                return;
            }

            bool was = OnStreak(m);
            m.Kills.Add(Time.time);
            if (!was && OnStreak(m))
            {
                Streaks++;
                Log.Info($"LR Mood: {killer.Profile?.Nickname} is on a kill streak ({m.Kills.Count} kills) - bolder and sloppier for a while");
            }
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                Mood m = bot != null && !bot.IsDead && !Spectator.TakeOver.IsTaken(bot) ? Get(bot) : null;
                if (m == null)
                {
                    continue;
                }

                string name = bot.Profile?.Nickname;
                float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
                if (hp < 0.25f)
                {
                    m.NearDeath = true;
                }
                else if (m.NearDeath && hp > 0.5f && !m.Shaken)
                {
                    m.NearDeath = false;
                    if (m.Shakeable)
                    {
                        m.Shaken = true;
                        ShakenCount++;
                        Log.Info($"LR Mood: {name} lived through a close call - careful for the rest of the raid");
                    }
                }

                // A hit: a vengeful one remembers who it was and where the shots came from
                if (m.Vengeful && HitReaction.TryGetHit(bot, out HitReaction.Hit hit) && hit.At > m.LastHitSeen && hit.By != null
                    && !SquadTies.AreMates(bot.ProfileId, hit.By.ProfileId))
                {
                    m.LastHitSeen = hit.At;
                    if (m.Grudge != hit.By.ProfileId)
                    {
                        m.Grudge = hit.By.ProfileId;
                        m.GrudgeName = hit.By.Profile?.Nickname;
                        m.Hunts = 0;
                        Grudges++;
                        Log.Detail("combat", () => $"{name} will remember {m.GrudgeName} - he shot it");
                    }

                    m.GrudgeAt = hit.From;
                    m.GrudgeSince = now;
                }

                Hunt(bot, m, world, now, hp);
            }
        }

        private static void Hunt(BotOwner bot, Mood m, GameWorld world, float now, float hp)
        {
            if (m.Grudge == null || m.Hunts >= 2 || now - m.GrudgeSince < 20f || now - m.GrudgeSince > 300f || hp < 0.5f
                || !TacticalTasks.BotIsCalm(bot) || TacticalTasks.TryGet(bot, out _) || ExtractPlanner.IsLeaving(bot) || TacticalTasks.SquadLeaderOf(bot) != null)
            {
                return;
            }

            if (!world.AllAlivePlayersList.Any(x => x?.ProfileId == m.Grudge))
            {
                m.Grudge = null;
                return;
            }

            float dist = Vector3.Distance(bot.Position, m.GrudgeAt);
            if (dist > 200f)
            {
                m.Grudge = null;
                return;
            }

            Vector3? stand = ScavPatrol.Short(bot, m.GrudgeAt, 8f + Random.value * 10f);
            if (stand == null)
            {
                m.Hunts++;
                return;
            }

            m.Hunts++;
            if (TacticalTasks.Set(bot, new TacticalTask
                {
                    Kind = TaskKind.Investigate, StandAt = stand, LookAt = m.GrudgeAt, Speed = 1f, Sprint = dist > 30f, SprintBeyond = 25f, MovePose = 1f,
                    Until = now + dist / 2.8f + 20f,
                }))
            {
                Hunts++;
                Log.Info($"LR Mood: {bot.Profile?.Nickname} goes looking for {m.GrudgeName}, who shot it - where the shots came from, {dist:0} m away");
            }
        }
    }
}
