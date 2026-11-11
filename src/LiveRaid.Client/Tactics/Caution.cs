using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// "Careful choice" (F12 -> 4 "Careful choice", 1.4.5): the PMCs' and player scavs' own prudence about where to go and which fight to go to, kept to what a
    /// player does and by character. From six raids of Reserve (107 PMCs): 88 % of lone PMCs and 90 % of pairs died, median minute 6-9 (trios: 64 %, minute 9);
    /// a third of the deaths were at the hands of the boss's pack and raiders (the boss and his guards spawn at 00:00 in the middle of the base, where the lobby
    /// goes to loot), half at those of other PMCs; and the hearing of a fight was weighed by character alone, with no regard for being alone or hurt.
    ///
    /// How careful a bot is (<see cref="Level"/>, 0-1) is made of: the squad it is with now (alone 1.0, a pair 0.55, three 0.3, four or more 0.18), its skill (a green one
    /// 1.2, a trained one 0.75, a pro 0.5), its style (rats, campers, looters 1.15, hunters and rushers 0.65), its aggression, what it has to lose (PlayerSense), and its
    /// health (under 70 % it adds up to 0.5 whatever the squad: a squad with a hurt member goes careful too). So a lone rat goes carefully, a trained trio hardly ever,
    /// a trio with a hurt man or a green one - like a loner.
    /// It acts in two places:
    ///  - the reaction to the gunfire it hears (GunfireResponse): going to the fight (rush, approach, flank) weighs less, leaving, holding and ignoring more -
    ///    more so for a fight that sounds like a war;
    ///  - the weight of a quest in a part of the map (DangerMemory): a careful bot is never "drawn" to a place where people died; the boss's spawn place is known
    ///    for the first seven minutes (a player knows where the boss and his guards come from), and a death at the hands of the boss's pack or raiders weighs double
    ///    and is known from further off (a fight like that is heard far and talked about).
    /// </summary>
    public static class Caution
    {
        private const float BossRemember = 420f;

        private class Cached
        {
            public float At;
            public float Level;
        }

        private static readonly Dictionary<string, Cached> _levels = new Dictionary<string, Cached>();
        private static readonly Dictionary<string, (Vector3 pos, float at)> _bossSpots = new Dictionary<string, (Vector3, float)>();
        private static float _next;

        public static int Weighed { get; private set; }
        public static int Careful { get; private set; }
        public static int QuestsMoved { get; private set; }

        public static bool On => Settings.Enabled.Value && Settings.CautiousChoice.Value;

        public static string Summary => !On
            ? "careful choice: off"
            : $"careful choice: fights weighed {Weighed} (of them by a careful bot {Careful}), quests held back or let go of {QuestsMoved}, boss places known {_bossSpots.Count}";

        public static void Clear()
        {
            _levels.Clear();
            _bossSpots.Clear();
            _next = 0f;
            Weighed = Careful = QuestsMoved = 0;
        }

        /// <summary>The boss's (and every boss's) place at the start of the raid - the first time his bot is seen.</summary>
        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 3f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || _bossSpots.ContainsKey(bot.ProfileId))
                {
                    continue;
                }

                WildSpawnType role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (role.ToString().StartsWith("boss") && role != WildSpawnType.bossZryachiy)
                {
                    _bossSpots[bot.ProfileId] = (bot.Position, Time.time);
                    Log.Info($"LR Caution: the boss {bot.Profile.Nickname} ({role}) is at ({bot.Position.x:0}, {bot.Position.z:0}) - careful players keep away from there for a while");
                }
            }
        }

        /// <summary>How hot a part of the map is for the bosses' sake: 0 = nothing, up to 1.5 (a boss spawned there a moment ago), fading over seven minutes.</summary>
        public static float BossDanger(Vector3 centre, float reach)
        {
            float danger = 0f;
            foreach ((Vector3 pos, float at) in _bossSpots.Values)
            {
                float age = Time.time - at;
                if (age > BossRemember)
                {
                    continue;
                }

                float dx = pos.x - centre.x, dz = pos.z - centre.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < reach)
                {
                    danger += 1.5f * (1f - age / BossRemember) * (1f - 0.5f * d / reach);
                }
            }

            return Mathf.Min(1.5f, danger);
        }

        /// <summary>The squad it is with now: itself and the mates alive.</summary>
        private static int AliveSquad(BotOwner bot)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return 1;
            }

            HashSet<string> mates = new HashSet<string>(SquadTies.MatesOf(bot.ProfileId));
            int alive = 0;
            foreach (Player player in world.AllAlivePlayersList)
            {
                if (player != null && player.HealthController?.IsAlive == true && mates.Contains(player.ProfileId))
                {
                    alive++;
                }
            }

            return Mathf.Max(1, alive);
        }

        /// <summary>0 (bold) .. 1 (as careful as it gets) for this bot as it is now.</summary>
        public static float Level(BotOwner bot, BotProfile p)
        {
            if (bot == null || p == null)
            {
                return 0f;
            }

            if (_levels.TryGetValue(bot.ProfileId, out Cached c) && Time.time - c.At < 3f)
            {
                return c.Level;
            }

            int alive = AliveSquad(bot);
            float squad = alive <= 1 ? 1f : alive == 2 ? 0.55f : alive == 3 ? 0.3f : 0.18f;
            float skill = p.Skill == SkillTier.Novice ? 1.2f : p.Skill == SkillTier.Experienced ? 0.75f : p.Skill == SkillTier.Pro ? 0.5f : 1f;
            float style;
            switch (p.Style)
            {
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                case PlayStyle.Looter: style = 1.15f; break;
                case PlayStyle.Hunter:
                case PlayStyle.Rusher: style = 0.65f; break;
                default: style = 1f; break;
            }

            float level = squad * skill * style * Mathf.Clamp(1.25f - 0.35f * p.Aggression, 0.6f, 1.2f);
            float hp = bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f;
            level += Mathf.Clamp01((0.7f - hp) / 0.5f) * 0.5f;
            level += 0.15f * Mathf.Max(0f, PlayerSense.Stakes(bot));
            level = Mathf.Clamp01(level);
            _levels[bot.ProfileId] = new Cached { At = Time.time, Level = level };
            return level;
        }

        private static bool Applies(BotProfile p) => p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav) && !p.Packrat;

        /// <summary>The weights of the reactions to a fight heard (GunfireResponse): a careful bot goes to it less and keeps away more.</summary>
        public static void Gunfire(BotOwner bot, BotProfile p, Dictionary<FightReaction, float> w, float danger)
        {
            if (!On || !Applies(p))
            {
                return;
            }

            float l = Mathf.Clamp01(Level(bot, p) * (0.6f + 0.6f * danger));
            Weighed++;
            if (l >= 0.4f)
            {
                Careful++;
            }

            w[FightReaction.Rush] *= 1f - 0.85f * l;
            w[FightReaction.Approach] *= 1f - 0.65f * l;
            w[FightReaction.Flank] *= 1f - 0.75f * l;
            w[FightReaction.Ambush] *= 1f - 0.15f * l;
            w[FightReaction.Leave] *= 1f + 1.3f * l;
            w[FightReaction.Hold] *= 1f + 0.9f * l;
            w[FightReaction.Ignore] *= 1f + 0.6f * l;
        }

        /// <summary>The lean of a bot towards a place where people died (DangerMemory): -1 keeps well away .. +1 is drawn. A careful bot is not drawn.</summary>
        public static float Lean(BotOwner bot, BotProfile p, float lean)
        {
            if (!On || !Applies(p))
            {
                return lean;
            }

            float capped = Mathf.Min(lean, 1f - 2f * Level(bot, p));
            if (capped < lean)
            {
                QuestsMoved++;
            }

            return capped;
        }
    }
}
