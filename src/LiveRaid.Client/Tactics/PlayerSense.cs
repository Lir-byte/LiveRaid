using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A lone fighter's habits that SAIN does not have (F12 -> 9 "Fight like a player"). SAIN peeks again and again from the same cover and only
    /// shifts when the enemy has been out of sight a while; LiveRaid's "return fire" kept a bot hit in the open standing to trade shots. Players:
    ///  - change the angle: after a few shots from one spot (a pro after 4-9 rounds, a novice after 18-30; a long gun after 2-4 - shoot and move),
    ///    once the enemy has ducked out of sight, they go to another spot instead of peeking where he waits for them (SAIN's ShiftCover);
    ///  - hit in the open (a field, a road) by an enemy 25 m or more away: run for cover, do not stand and trade;
    ///  - weigh what they carry: a full kit with a bag of loot (the stakes) is careful - goes to fewer fights and has less nerve to stay in one;
    ///    a bum with nothing to lose is bolder (GunfireResponse, CombatSense);
    ///  - leave a scav be: a looter, a rat, a quiet squad does not start a gunfight with an AI scav 30 m or more away that is not shooting at it -
    ///    the noise would bring the PMCs; closer than that, or once the scav shoots, it is a fight (SainDistantEnemyPatch).
    /// SAIN already strafes and jumps in close fights (DogFight) - that is left to it.
    /// </summary>
    public static class PlayerSense
    {
        private class Spot
        {
            public Vector3 Pos;
            public int Shots;
            public int Limit;
            public float WantUntil;
        }

        private static readonly Dictionary<string, Spot> _spots = new Dictionary<string, Spot>();
        private static readonly Dictionary<string, (float at, float open)> _open = new Dictionary<string, (float, float)>();
        private static readonly Dictionary<string, float> _dashAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, bool> _tolerant = new Dictionary<string, bool>();
        private static readonly Dictionary<string, (bool leave, float since)> _scavLatch = new Dictionary<string, (bool, float)>();
        private static readonly System.Random _rng = new System.Random();

        public static int NewAngles { get; private set; }
        public static int Dashes { get; private set; }
        public static int ScavsLeftAlone { get; private set; }

        public static string Summary => $"fighting like a player: new angles after a few shots {NewAngles}, runs for cover when hit in the open {Dashes}, AI scavs left alone {ScavsLeftAlone}";

        public static bool On => Settings.Enabled.Value && Settings.FightLikePlayer.Value;

        public static void Clear()
        {
            _spots.Clear();
            _open.Clear();
            _dashAt.Clear();
            _tolerant.Clear();
            _scavLatch.Clear();
            NewAngles = Dashes = ScavsLeftAlone = 0;
        }

        private static bool PlaysLikeAPlayer(BotProfile p) => p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav);

        /// <summary>Every shot (WorldEvents): counted per spot; enough of them from one spot and the bot wants another angle.</summary>
        public static void OnShot(Player shooter)
        {
            BotOwner bot = shooter?.AIData?.BotOwner;
            if (!On || bot == null)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (!PlaysLikeAPlayer(p))
            {
                return;
            }

            if (!_spots.TryGetValue(bot.ProfileId, out Spot s) || (bot.Position - s.Pos).sqrMagnitude > 1.5f * 1.5f)
            {
                _spots[bot.ProfileId] = s = new Spot { Pos = bot.Position, Limit = Limit(bot, p) };
            }

            s.Shots++;
            if (s.Shots >= s.Limit && s.WantUntil < Time.time)
            {
                s.WantUntil = Time.time + 6f;
            }
        }

        private static int Limit(BotOwner bot, BotProfile p)
        {
            if (CombatSense.IsLongGun(p))
            {
                return 2 + _rng.Next(3);
            }

            int min, max;
            switch (p.Skill)
            {
                case SkillTier.Novice: min = 18; max = 30; break;
                case SkillTier.Average: min = 10; max = 18; break;
                case SkillTier.Experienced: min = 6; max = 12; break;
                default: min = 4; max = 9; break;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            float k = d != null ? 1.3f - 0.6f * d[DoctrineDeck.Precision] : 1f;

            // On a kill streak it gets sloppy: stays longer on one spot (RaidMood)
            if (RaidMood.Careless(bot))
            {
                k *= 1.6f;
            }
            return Mathf.Max(2, Mathf.RoundToInt((min + (float)_rng.NextDouble() * (max - min)) * k));
        }

        private static float OpennessOf(BotOwner bot)
        {
            if (_open.TryGetValue(bot.ProfileId, out var o) && Time.time - o.at < 1f)
            {
                return o.open;
            }

            float open = LootBrain.Openness(bot.Position);
            _open[bot.ProfileId] = (Time.time, open);
            return open;
        }

        /// <summary>The decision of SAIN after morale and "return fire": a new angle, or a run for cover when hit in the open.</summary>
        public static void Apply(BotOwner bot, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            if (!On || bot == null || enemy == null || bot.WeaponManager == null || !bot.WeaponManager.HaveBullets || HandsGuard.RealReload(bot))
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (!PlaysLikeAPlayer(p) || CombatSense.StanceOf(bot) == CombatStance.Push || CombatSense.HoldingGround(bot))
            {
                return;
            }

            bool holding = result == ECombatDecision.StandAndShoot || result == ECombatDecision.Freeze || result == ECombatDecision.ShootDistantEnemy
                           || result == ECombatDecision.SeekCover;
            if (!holding)
            {
                return;
            }

            float now = Time.time;

            // Hit in the open by someone 25 m+ away: run for cover
            if (result != ECombatDecision.SeekCover && HitReaction.SinceHit(bot) < 2.5f && enemy.RealDistance > 25f
                && (!_dashAt.TryGetValue(bot.ProfileId, out float dashed) || now - dashed > 8f) && OpennessOf(bot) > 0.75f)
            {
                _dashAt[bot.ProfileId] = now;
                result = ECombatDecision.SeekCover;
                __result = true;
                Dashes++;
                Log.Detail("combat", () => $"{p.Name} is hit in the open by {enemy.EnemyPlayer?.Profile?.Nickname} ({enemy.RealDistance:0} m) - runs for cover");
                return;
            }

            // A few shots from one spot and the enemy ducked: another angle (a long gun moves on even while he is in sight)
            if (_spots.TryGetValue(bot.ProfileId, out Spot s) && now < s.WantUntil && (!enemy.IsVisible || CombatSense.IsLongGun(p)))
            {
                s.WantUntil = 0f;
                s.Shots = 0;
                result = ECombatDecision.ShiftCover;
                __result = true;
                NewAngles++;
                Log.Detail("combat", () => $"{p.Name} has fired {s.Limit}+ rounds from one spot - takes another angle on {enemy.EnemyPlayer?.Profile?.Nickname}");
            }
        }

        /// <summary>
        /// What the bot stands to lose: its gear (a quarter of its worth) and the loot it carries. Over ~300k it is careful, under ~60k a bum with
        /// nothing to lose. 0..1 careful, below 0 bold.
        /// </summary>
        public static float Stakes(BotOwner bot)
        {
            if (!On || bot?.GetPlayer == null)
            {
                return 0f;
            }

            float kit = LootBrain.VisibleValue(bot.GetPlayer.Equipment, "stakes" + bot.ProfileId);
            LootingBots.Components.LootingBrain brain = bot.GetPlayer.GetComponent<LootingBots.Components.LootingBrain>();
            float loot = brain?.Stats?.NetWorth ?? 0f;
            float stakes = loot + kit * 0.25f;
            if (stakes >= 300000f)
            {
                // Not everybody minds what he carries: a hoarder a lot, the others little (RaidMood)
                return Mathf.Clamp01(Mathf.Clamp01((stakes - 300000f) / 500000f + 0.4f) * RaidMood.StakesWeight(bot));
            }

            return kit + loot < 60000f ? -0.4f : 0f;
        }

        /// <summary>
        /// An AI scav 30 m or more away that is not shooting at a looter / rat / quiet squad member: not its fight (the noise would bring PMCs).
        /// Decided once per bot (doctrine and style) and held 20 s per scav.
        /// </summary>
        public static bool LeavesScavAlone(BotOwner bot, Enemy enemy)
        {
            if (!On || bot == null || enemy?.EnemyPlayer == null)
            {
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind != BotKind.Pmc)
            {
                return false;
            }

            BotOwner other = enemy.EnemyPlayer.AIData?.BotOwner;
            if (other == null || BotProfiles.Get(other)?.Kind != BotKind.Scav)
            {
                return false;
            }

            if (!_tolerant.TryGetValue(bot.ProfileId, out bool tolerant))
            {
                DoctrineDeck.Member d = SquadDoctrines.Of(bot);
                DoctrineKind? kind = d?.Squad.Doctrine?.Kind;
                double chance = kind == DoctrineKind.Looters || kind == DoctrineKind.Rats || kind == DoctrineKind.Speedrunners ? 0.55
                    : kind == DoctrineKind.Questers ? 0.3
                    : kind == DoctrineKind.Marksmen ? 0.2
                    : 0.05;
                if (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Looter)
                {
                    chance += 0.2;
                }

                _tolerant[bot.ProfileId] = tolerant = new System.Random(bot.ProfileId.GetHashCode() ^ 0x5ca7).NextDouble() < chance;
            }

            string key = bot.ProfileId + enemy.EnemyProfileId;
            bool threat = enemy.Status.ShotAtMeRecently || bot.Memory?.IsUnderFire == true || HitReaction.SinceHit(bot) < 10f || enemy.RealDistance < 30f;
            if (!tolerant || threat)
            {
                _scavLatch[key] = (false, Time.time);
                return false;
            }

            if (_scavLatch.TryGetValue(key, out var l) && Time.time - l.since < 20f)
            {
                return l.leave;
            }

            _scavLatch[key] = (true, Time.time);
            ScavsLeftAlone++;
            Log.Detail("combat", () => $"{p.Name} leaves the scav {enemy.EnemyPlayer.Profile?.Nickname} ({enemy.RealDistance:0} m) alone - not worth the noise");
            return true;
        }
    }
}
