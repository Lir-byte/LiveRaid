using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using SAIN.Preset.Shared.Models.Preset.Personalities;

namespace LiveRaid.Client.Bots
{
    public enum BotKind
    {
        Pmc,
        Scav,
        PlayerScav,
        Other,
    }

    public enum PlayStyle
    {
        Camper,
        Rat,
        Rusher,
        Looter,
        Quester,
        Explorer,
        Hunter,
        Sniper,
    }

    public enum SkillTier
    {
        Novice,
        Average,
        Experienced,
        Pro,
    }

    /// <summary>
    /// Who this bot "is" for the whole raid: kind, skill, play style and the numbers derived from them.
    /// Deterministic per profile id, so every system asking about the same bot gets the same answer.
    /// </summary>
    public class BotProfile
    {
        public string ProfileId;
        public string Name;
        public BotKind Kind;
        public SkillTier Skill;
        public PlayStyle Style;

        // -1..1 personal talent on top of the requested difficulty
        public float Talent;

        // Objective preferences (1 = neutral)
        public float Aggression;
        public float LootGreed;
        public float Explore;
        public float Camp;
        public float QuestFocus;
        public float Snipe;
        public float Memory;

        // Leave the raid when this fraction of raid time is left
        public float ExtractAtRemaining;

        // Hot-drop: at the raid start runs straight for the richest place of the map (the dorms on Customs, the bunker on Reserve)
        public bool HotDrop;

        public EPersonality SainPersonality;
        public string WeaponClass;

        // The raid's packrat: a lone PMC with a pistol and a big bag, there for the loot only (Packrat)
        public bool Packrat;

        public bool KeysGiven;
        public float ActivatedAt = -1f;

        // Where the bot came into the raid (its spawn side decides its exits - ExitPlan)
        public UnityEngine.Vector3 SpawnPos;
        public readonly List<string> Keys = new List<string>();

        public override string ToString() =>
            $"{Name} [{Kind} {Skill}/{Style} {SainPersonality} talent {Talent:+0.00;-0.00} weapon {WeaponClass ?? "none"} extract at {ExtractAtRemaining:P0} left{(HotDrop ? ", hot-drop" : "")}{(Packrat ? ", packrat" : "")}]";
    }

    public static class BotProfiles
    {
        private static readonly Dictionary<string, BotProfile> _profiles = new Dictionary<string, BotProfile>();

        public static void Clear() => _profiles.Clear();

        public static IEnumerable<BotProfile> All => _profiles.Values;

        public static BotKind KindOf(BotOwner bot)
        {
            WildSpawnType role = bot.Profile.Info.Settings.Role;
            if (role == WildSpawnType.pmcUSEC || role == WildSpawnType.pmcBEAR || QuestingBots.Controllers.BotRegistrationManager.IsBotAPMC(bot))
            {
                return BotKind.Pmc;
            }

            if (role == WildSpawnType.assault || role == WildSpawnType.cursedAssault || role == WildSpawnType.assaultGroup)
            {
                return QuestingBots.Controllers.BotRegistrationManager.GetBotType(bot) == QuestingBots.Helpers.EBotType.PScav
                    ? BotKind.PlayerScav
                    : BotKind.Scav;
            }

            return BotKind.Other;
        }

        public static BotProfile Get(BotOwner bot)
        {
            if (bot?.Profile == null)
            {
                return null;
            }

            if (_profiles.TryGetValue(bot.ProfileId, out BotProfile existing))
            {
                return existing;
            }

            BotProfile profile = Create(bot);
            _profiles[bot.ProfileId] = profile;
            Log.Debug("profile " + profile);
            return profile;
        }

        public static bool TryGet(string profileId, out BotProfile profile) => _profiles.TryGetValue(profileId, out profile);

        /// <summary>A boss's guard (follower*) that LiveRaid looks after too (F12 -> "Boss guards forget cold fights and get unstuck").</summary>
        public static bool IsCaredGuard(BotOwner bot, BotProfile p) =>
            p != null && p.Kind == BotKind.Other && Settings.GuardsCare.Value && bot?.Profile?.Info?.Settings != null
            && bot.Profile.Info.Settings.Role.ToString().StartsWith("follower", System.StringComparison.OrdinalIgnoreCase);

        private static BotProfile Create(BotOwner bot)
        {
            Random rng = new Random(bot.ProfileId.GetHashCode());
            BotProfile p = new BotProfile
            {
                ProfileId = bot.ProfileId,
                Name = bot.Profile.Nickname,
                Kind = KindOf(bot),
                WeaponClass = PrimaryWeaponClass(bot),
            };

            // Skill: requested difficulty plus a personal talent roll, so two "normal" bots still differ
            int tier = DifficultyTier(bot.Profile.Info.Settings.BotDifficulty);
            p.Talent = (float)Math.Max(-1, Math.Min(1, Gaussian(rng) * 0.45));
            if (p.Talent > 0.55f)
            {
                tier++;
            }
            else if (p.Talent < -0.55f)
            {
                tier--;
            }

            if (p.Kind == BotKind.Pmc && bot.Profile.Info.Level < 10 && rng.NextDouble() < 0.35)
            {
                tier--;
            }

            p.Skill = (SkillTier)Math.Max(0, Math.Min(3, tier));
            p.Style = ChooseStyle(p, bot, rng);
            ApplyStyleTraits(p, rng);
            p.SainPersonality = ChoosePersonality(p, rng);
            p.ExtractAtRemaining = ChooseExtractTime(p, rng);
            p.HotDrop = ChooseHotDrop(p, rng);
            if (Packrat.Check(bot, p.Kind))
            {
                Packrat.Shape(p, bot);
            }

            return p;
        }

        private static int DifficultyTier(BotDifficulty difficulty)
        {
            switch (difficulty)
            {
                case BotDifficulty.easy: return 0;
                case BotDifficulty.hard: return 2;
                case BotDifficulty.impossible: return 3;
                default: return 1;
            }
        }

        private static string PrimaryWeaponClass(BotOwner bot)
        {
            try
            {
                Item item = bot.GetPlayer?.Equipment?.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem;
                return (item as Weapon)?.WeapClass;
            }
            catch
            {
                return null;
            }
        }

        private static PlayStyle ChooseStyle(BotProfile p, BotOwner bot, Random rng)
        {
            Dictionary<PlayStyle, double> w;
            if (p.Kind == BotKind.Scav)
            {
                w = new Dictionary<PlayStyle, double>
                {
                    // No campers (session 10): a player does not stand in one spot doing nothing; rats move from cover to cover
                    [PlayStyle.Camper] = 0, [PlayStyle.Rat] = 22, [PlayStyle.Rusher] = 8, [PlayStyle.Looter] = 34,
                    [PlayStyle.Explorer] = 22, [PlayStyle.Hunter] = 4, [PlayStyle.Quester] = 0, [PlayStyle.Sniper] = 2,
                };
            }
            else
            {
                w = new Dictionary<PlayStyle, double>
                {
                    [PlayStyle.Camper] = 0, [PlayStyle.Rat] = 12, [PlayStyle.Rusher] = 14, [PlayStyle.Looter] = 15,
                    [PlayStyle.Quester] = 17, [PlayStyle.Explorer] = 15, [PlayStyle.Hunter] = 10, [PlayStyle.Sniper] = 5,
                };
            }

            // What the bot carries says a lot about how it plays
            switch (p.WeaponClass)
            {
                case "sniperRifle":
                case "marksmanRifle":
                    w[PlayStyle.Sniper] *= 5;
                    w[PlayStyle.Camper] *= 1.5;
                    w[PlayStyle.Rusher] *= 0.3;
                    break;
                case "shotgun":
                case "smg":
                    w[PlayStyle.Rusher] *= 2.5;
                    w[PlayStyle.Sniper] *= 0.1;
                    break;
                case "machinegun":
                    w[PlayStyle.Hunter] *= 2;
                    w[PlayStyle.Camper] *= 1.5;
                    break;
                case null:
                    w[PlayStyle.Rat] *= 2.5;
                    w[PlayStyle.Looter] *= 1.5;
                    w[PlayStyle.Sniper] = 0;
                    break;
            }

            switch (p.Skill)
            {
                case SkillTier.Novice:
                    w[PlayStyle.Rat] *= 1.6;
                    w[PlayStyle.Camper] *= 1.3;
                    w[PlayStyle.Hunter] *= 0.4;
                    w[PlayStyle.Rusher] *= 0.7;
                    break;
                case SkillTier.Pro:
                    w[PlayStyle.Hunter] *= 2.2;
                    w[PlayStyle.Rusher] *= 1.5;
                    w[PlayStyle.Rat] *= 0.5;
                    break;
            }

            double total = 0;
            foreach (double v in w.Values)
            {
                total += v;
            }

            double roll = rng.NextDouble() * total;
            foreach (KeyValuePair<PlayStyle, double> kv in w)
            {
                roll -= kv.Value;
                if (roll <= 0)
                {
                    return kv.Key;
                }
            }

            return PlayStyle.Explorer;
        }

        private static void ApplyStyleTraits(BotProfile p, Random rng)
        {
            // aggression, greed, explore, camp, quest, snipe, memory
            float[] t;
            switch (p.Style)
            {
                case PlayStyle.Camper: t = new[] { 0.35f, 0.5f, 0.5f, 2.6f, 0.8f, 1.3f, 1.3f }; break;
                case PlayStyle.Rat: t = new[] { 0.15f, 1.4f, 1.2f, 1.3f, 0.5f, 0.6f, 0.8f }; break;
                case PlayStyle.Rusher: t = new[] { 0.95f, 0.6f, 1.3f, 0.2f, 0.8f, 0.2f, 1.0f }; break;
                case PlayStyle.Looter: t = new[] { 0.4f, 2.0f, 1.6f, 0.5f, 0.5f, 0.4f, 0.9f }; break;
                case PlayStyle.Quester: t = new[] { 0.5f, 0.8f, 0.9f, 0.9f, 2.6f, 1.0f, 1.0f }; break;
                case PlayStyle.Explorer: t = new[] { 0.6f, 1.1f, 2.2f, 0.4f, 0.8f, 0.6f, 1.0f }; break;
                case PlayStyle.Hunter: t = new[] { 0.9f, 0.5f, 1.4f, 0.5f, 0.7f, 0.8f, 1.7f }; break;
                default: t = new[] { 0.4f, 0.6f, 0.6f, 1.7f, 0.8f, 3.0f, 1.2f }; break;
            }

            float J(float v) => v * (float)(0.8 + rng.NextDouble() * 0.4);
            p.Aggression = Math.Min(1f, J(t[0]));
            p.LootGreed = J(t[1]);
            p.Explore = J(t[2]);
            p.Camp = J(t[3]);
            p.QuestFocus = p.Kind == BotKind.Scav ? 0 : J(t[4]);
            p.Snipe = J(t[5]);
            p.Memory = J(t[6]) * (1 + 0.15f * ((int)p.Skill - 1));
        }

        private static EPersonality ChoosePersonality(BotProfile p, Random rng)
        {
            double r = rng.NextDouble();

            // Newcomers panic: some beg, some run, regardless of the style they meant to play.
            // Kept to 1 in 4: passive SAIN personalities (Timmy, Coward, Rat, turtle) hold still for minutes, and they were 46 % of all bots
            if (p.Skill == SkillTier.Novice && p.Kind == BotKind.Pmc)
            {
                if (r < 0.17) return EPersonality.Timmy;
                if (r < 0.25) return EPersonality.Coward;
                r = rng.NextDouble();
            }

            if (p.Kind == BotKind.Scav)
            {
                switch (p.Style)
                {
                    case PlayStyle.Rat: return r < 0.5 ? EPersonality.Rat : r < 0.7 ? EPersonality.Coward : EPersonality.Normal;
                    case PlayStyle.Rusher: return r < 0.15 ? EPersonality.Wreckless : EPersonality.Chad;
                    case PlayStyle.Hunter: return EPersonality.Chad;
                    case PlayStyle.Camper: return EPersonality.Normal;
                    default: return r < 0.08 ? EPersonality.Coward : EPersonality.Normal;
                }
            }

            switch (p.Style)
            {
                case PlayStyle.Camper: return EPersonality.Normal;
                case PlayStyle.Rat: return r < 0.8 ? EPersonality.Rat : EPersonality.Coward;
                case PlayStyle.Rusher:
                    if (r < 0.04) return EPersonality.Wreckless;
                    return p.Skill == SkillTier.Pro && r < 0.6 ? EPersonality.GigaChad : EPersonality.Chad;
                case PlayStyle.Looter: return r < 0.2 ? EPersonality.Rat : EPersonality.Normal;
                case PlayStyle.Quester: return r < 0.3 ? EPersonality.Chad : EPersonality.Normal;
                case PlayStyle.Explorer: return r < 0.4 ? EPersonality.Chad : EPersonality.Normal;
                case PlayStyle.Hunter: return p.Skill >= SkillTier.Experienced && r < 0.5 ? EPersonality.GigaChad : EPersonality.Chad;
                // Snipers: no snapping turtle (it holds one spot for minutes)
                default: return r < 0.6 ? EPersonality.Normal : EPersonality.Rat;
            }
        }

        /// <summary>Like online, some players spawn and sprint for the loot-rich place before others get there: mostly looters and rushers.</summary>
        private static bool ChooseHotDrop(BotProfile p, Random rng)
        {
            if (p.Kind == BotKind.Scav || p.Kind == BotKind.Other)
            {
                return false;
            }

            double chance;
            switch (p.Style)
            {
                case PlayStyle.Looter: chance = 0.4; break;
                case PlayStyle.Rusher: chance = 0.35; break;
                case PlayStyle.Hunter: chance = 0.3; break;
                case PlayStyle.Explorer: chance = 0.25; break;
                case PlayStyle.Quester: chance = 0.12; break;
                case PlayStyle.Rat: chance = 0.08; break;
                default: chance = 0.05; break;
            }

            if (p.Kind == BotKind.PlayerScav)
            {
                chance *= 0.6;
            }

            return rng.NextDouble() < chance;
        }

        private static float ChooseExtractTime(BotProfile p, Random rng)
        {
            float U(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            if (rng.NextDouble() < 0.1)
            {
                return U(0.03f, 0.12f);
            }

            switch (p.Style)
            {
                case PlayStyle.Rusher:
                case PlayStyle.Hunter: return U(0.4f, 0.75f);
                case PlayStyle.Looter:
                case PlayStyle.Rat: return rng.NextDouble() < 0.3 ? U(0.55f, 0.8f) : U(0.2f, 0.5f);
                case PlayStyle.Camper:
                case PlayStyle.Sniper: return U(0.05f, 0.3f);
                default: return U(0.15f, 0.55f);
            }
        }

        private static double Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
