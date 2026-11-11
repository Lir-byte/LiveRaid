using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    public enum FightRole
    {
        None,
        Hold,
        Assault,
        Flank,
    }

    /// <summary>
    /// A role for every fight (F12 -> 9 "Fight roles"). Before, only rushers and hunters (or the most aggressive) could ever push: a looter, a rat or a quester
    /// three against one still sat in cover - raid 2026-10-22 22:42 on Customs, 37 of 38 pushes were rushers'. Players mix it up: the side with the numbers
    /// goes in, a lone man against two holds, someone goes round. Here each bot draws a role when its fight starts and again when the fight changes (the
    /// numbers swing, it gets badly hurt, or after 25-45 s):
    ///   assault - push the enemy (CombatSense Push: rush within 80 m, close in up to 150 m);
    ///   flank   - go round the enemy once he is out of sight (Maneuvers);
    ///   hold    - SAIN's own fight from cover.
    /// Weights: the character (rusher .55 ... sniper .05 for assault), x2 with two against one, x1.6 at 3:2, x0.5 outnumbered, x0.3 badly hurt, x0.25 a long
    /// gun against a far enemy. A squad first draws a plan for the fight - push together (likely with the edge), hold (likely without it), one goes round
    /// while the rest keep the enemy busy, or everyone for himself - and its members draw within it. Morale still rules over it: a bot losing the fight
    /// falls back or breaks contact whatever its role.
    /// </summary>
    public static class FightRoles
    {
        private class BotRole
        {
            public FightRole Role;
            public float Until;
            public int RatioBand;
            public int HpBand;
            public int FlankTries;

            // The squad's call this role was drawn under (SquadPlaybook.Refresh): a new call or the next bound draws again
            public int Stamp;
        }

        private class SquadPlan
        {
            public string Name;
            public float Until;
            public int RatioBand;
            public string FlankerId;
        }

        private static readonly Dictionary<string, BotRole> _roles = new Dictionary<string, BotRole>();
        private static readonly Dictionary<BotsGroup, SquadPlan> _plans = new Dictionary<BotsGroup, SquadPlan>();
        private static readonly System.Random _rng = new System.Random();

        public static int Assaults { get; private set; }
        public static int Flanks { get; private set; }
        public static int Holds { get; private set; }
        public static int SquadPushes { get; private set; }

        public static string Summary => $"fight roles drawn: assault {Assaults}, flank {Flanks}, hold {Holds} (squads pushing together {SquadPushes})";

        public static void Clear()
        {
            _roles.Clear();
            _plans.Clear();
            Assaults = Flanks = Holds = SquadPushes = 0;
        }

        public static FightRole RoleOf(BotOwner bot) =>
            bot != null && _roles.TryGetValue(bot.ProfileId, out BotRole r) ? r.Role : FightRole.None;

        /// <summary>The fight is over for this bot: the next one starts with a fresh draw.</summary>
        public static void Forget(BotOwner bot)
        {
            if (bot != null)
            {
                _roles.Remove(bot.ProfileId);
            }
        }

        /// <summary>The flank could not be started (no way round, enemy in view): after a few tries the role is drawn again without it.</summary>
        public static void FlankFailed(BotOwner bot)
        {
            if (bot != null && _roles.TryGetValue(bot.ProfileId, out BotRole r) && r.Role == FightRole.Flank && ++r.FlankTries >= 4)
            {
                r.Until = 0f;
                r.FlankTries = -100;
            }
        }

        private static int RatioBand(int allies, int enemies)
        {
            float r = allies / (float)Mathf.Max(1, enemies);
            return r >= 2f ? 3 : r >= 1.5f ? 2 : r > 1f ? 1 : r >= 1f ? 0 : r >= 0.67f ? -1 : -2;
        }

        private static int HpBand(float health) => health < 0.45f ? 0 : health < 0.7f ? 1 : 2;

        /// <summary>The bot's role in its current fight, drawn when needed. None when the feature is off.</summary>
        public static FightRole Current(BotOwner bot, BotProfile p, int enemies, int allies, float health, Enemy closest, bool holdLong)
        {
            if (!Settings.FightRoles.Value || bot == null || p == null)
            {
                return FightRole.None;
            }

            // The packrat has no part in a fight: it was drawn Assault and Flank like anyone (raids 2026-11-10)
            if (p.Packrat)
            {
                return FightRole.None;
            }

            float now = Time.time;
            int ratio = RatioBand(allies, enemies);
            int hp = HpBand(health);
            int stamp = SquadPlaybook.Refresh(bot, ratio, closest, health);
            bool known = _roles.TryGetValue(bot.ProfileId, out BotRole r);
            if (known && now < r.Until && r.RatioBand == ratio && hp >= r.HpBand && r.Stamp == stamp)
            {
                return r.Role;
            }

            bool noFlank = known && r.FlankTries < 0;
            if (!known)
            {
                _roles[bot.ProfileId] = r = new BotRole();
            }

            FightRole old = r.Role;
            string plan = null;
            r.Role = Draw(bot, p, ratio, health, closest, holdLong, noFlank, out plan);
            r.Until = Mathf.Min(now + 25f + (float)_rng.NextDouble() * 20f, SquadPlaybook.RoleUntil(bot));
            r.Stamp = stamp;
            r.RatioBand = ratio;
            r.HpBand = hp;
            if (r.Role != FightRole.Flank)
            {
                r.FlankTries = noFlank ? -100 : 0;
            }

            switch (r.Role)
            {
                case FightRole.Assault: Assaults++; break;
                case FightRole.Flank: Flanks++; break;
                default: Holds++; break;
            }

            if (r.Role != old)
            {
                Log.Detail("combat", () => $"{p.Name} ({p.Style}) takes the role {r.Role}: {allies} vs {enemies}, hp {health:P0}{(plan != null ? ", squad plan " + plan : "")}");
            }

            return r.Role;
        }

        private static FightRole Draw(BotOwner bot, BotProfile p, int ratio, float health, Enemy closest, bool holdLong, bool noFlank, out string planName)
        {
            planName = null;
            float assault = AssaultWeight(bot, p, ratio, health, closest, holdLong);
            float flank = noFlank || p.Style == PlayStyle.Sniper || p.Style == PlayStyle.Camper || health < 0.6f || closest == null
                          || closest.RealDistance < 18f || closest.RealDistance > 120f
                ? 0f
                : 0.12f + (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter || p.Style == PlayStyle.Explorer ? 0.08f : 0f);

            if (Settings.SquadPlaybook.Value && p.Kind == BotKind.Pmc)
            {
                // The squad's call from its playbook (SquadPlaybook): a fixed part, or the bot's own draw with changed odds
                FightRole? part = SquadPlaybook.RoleFor(bot, p, closest, holdLong, health, ref assault, ref flank, out planName);
                if (part.HasValue)
                {
                    // A flanker who found no way round several times fights from where he is
                    return part.Value == FightRole.Flank && noFlank ? FightRole.Hold : part.Value;
                }

                double own = _rng.NextDouble();
                return own < assault ? FightRole.Assault : own < assault + flank ? FightRole.Flank : FightRole.Hold;
            }

            SquadPlan plan = PlanOf(bot, ratio);
            if (plan != null)
            {
                planName = plan.Name;
                switch (plan.Name)
                {
                    case "push together":
                        // Everyone goes in, but a long gun or a badly hurt man covers them
                        return holdLong || health < 0.4f || p.Style == PlayStyle.Sniper ? FightRole.Hold : FightRole.Assault;

                    case "hold":
                        assault *= 0.35f;
                        flank *= 0.5f;
                        break;

                    case "pin and flank":
                        if (plan.FlankerId == null && flank > 0f && _rng.NextDouble() < 0.7)
                        {
                            plan.FlankerId = bot.ProfileId;
                            return FightRole.Flank;
                        }

                        assault *= 0.6f;
                        flank = 0f;
                        break;
                }
            }

            double roll = _rng.NextDouble();
            if (roll < assault)
            {
                return FightRole.Assault;
            }

            return roll < assault + flank ? FightRole.Flank : FightRole.Hold;
        }

        private static float AssaultWeight(BotOwner bot, BotProfile p, int ratio, float health, Enemy closest, bool holdLong)
        {
            float w;
            switch (p.Style)
            {
                case PlayStyle.Rusher: w = 0.55f; break;
                case PlayStyle.Hunter: w = 0.45f; break;
                case PlayStyle.Explorer: w = 0.3f; break;
                case PlayStyle.Quester: w = 0.25f; break;
                case PlayStyle.Looter: w = 0.22f; break;
                case PlayStyle.Rat:
                case PlayStyle.Camper: w = 0.1f; break;
                default: w = 0.05f; break;
            }

            w += 0.25f * (p.Aggression - 0.5f) + 0.03f * (int)p.Skill;

            // The squad's doctrine as this member plays it: storm troopers go in, the quiet ones hold
            DoctrineDeck.Member doctrine = SquadDoctrines.Of(bot);
            if (doctrine != null)
            {
                w *= 0.5f + doctrine[DoctrineDeck.Aggression];
            }
            switch (ratio)
            {
                case 3: w *= 2f; break;
                case 2: w *= 1.6f; break;
                case 1: w *= 1.3f; break;
                case 0: break;
                case -1: w *= 0.5f; break;
                default: w *= 0.25f; break;
            }

            if (health < 0.45f)
            {
                w *= 0.3f;
            }
            else if (health < 0.7f)
            {
                w *= 0.7f;
            }

            if (holdLong)
            {
                w *= 0.25f;
            }

            // Close quarters: going in is often the safest move
            if (closest != null && closest.IsVisible && closest.RealDistance < 15f)
            {
                w *= 1.3f;
            }

            return Mathf.Clamp(w, 0.03f, 0.85f);
        }

        /// <summary>The squad's plan for this fight (null for a lone bot), drawn by the first member that weighs the fight.</summary>
        private static SquadPlan PlanOf(BotOwner bot, int ratio)
        {
            BotsGroup group = bot.BotsGroup;
            if (group == null || group.MembersCount < 2)
            {
                return null;
            }

            float now = Time.time;
            if (_plans.TryGetValue(group, out SquadPlan plan) && now < plan.Until && plan.RatioBand == ratio)
            {
                return plan;
            }

            float aggression = 0f;
            int n = 0;
            for (int i = 0; i < group.MembersCount; i++)
            {
                BotProfile mp = BotProfiles.Get(group.Member(i));
                if (mp != null)
                {
                    aggression += mp.Aggression;
                    n++;
                }
            }

            aggression = n > 0 ? aggression / n : 0.5f;
            float push = 0.08f + (ratio >= 2 ? 0.45f : ratio == 1 ? 0.25f : ratio == 0 ? 0.08f : 0f) + 0.2f * aggression;
            float hold = 0.2f + (ratio < 0 ? 0.4f : 0f);
            float flank = ratio >= 0 ? 0.15f : 0.05f;
            float split = 0.4f;
            double roll = _rng.NextDouble() * (push + hold + flank + split);
            string name = roll < push ? "push together" : roll < push + hold ? "hold" : roll < push + hold + flank ? "pin and flank" : "each his own";
            _plans[group] = plan = new SquadPlan { Name = name, Until = now + 30f + (float)_rng.NextDouble() * 20f, RatioBand = ratio };
            if (name == "push together")
            {
                SquadPushes++;
                Log.Info($"LR Squad: {bot.Profile.Nickname}'s squad pushes together ({(ratio >= 2 ? "clear edge" : ratio >= 1 ? "the edge" : "even")})");
            }

            return plan;
        }
    }
}
