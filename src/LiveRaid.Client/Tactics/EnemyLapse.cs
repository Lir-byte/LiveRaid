using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A fight that went cold. SAIN keeps an enemy "known" for as long as the bot is searching for him and has not yet been to every place he
    /// might be (up to 400 s after the last news), so a bot that has not seen anyone for minutes crept after a ghost and a PMC and a scav
    /// that could not see each other stood or walked slowly in SAIN's combat layer for 5-20 minutes (raid 2026-10-20 14:21: Парфений РВСН 5 min
    /// on one spot searching for an enemy 100 m away, vargrasen 21 minutes of "search / seek cover" in one block and eight kills lying
    /// unlooted, two bots 80 times each "cannot get on"). An online player loses interest: nobody seen, nobody shooting at him, a minute
    /// of silence - and he goes back to his business, coming back to the fight only if it finds him again.
    ///
    /// Here, per bot and enemy, the time of the last real contact is kept (the enemy in sight, a shot at the bot, a hit, the enemy heard
    /// close by). When it is older than the bot's patience (by character, with luck) the enemy stops being "known" to SAIN for a quiet
    /// stretch - the bot's own plan (quests, loot, exit) takes over. Any new contact ends the quiet at once. When the quiet is over the
    /// bot may take up the hunt again, with a fresh patience.
    /// </summary>
    public static class EnemyLapse
    {
        private class Pair
        {
            public float Last;          // last real contact
            public float Checked;       // when this pair was last asked about
            public float Patience;
            public float QuietUntil;
            public float NearSince;     // start of the present stretch of "the enemy is close but not in sight and nobody shoots" (0 = none)
            public float NearLast;      // last time that was so
            public float NearLimit;     // how long a bot stands such a stretch out before it is a standoff
            public bool Standoff;       // the stretch ran out: closeness alone is no contact any more, until the enemy shows or shoots
        }

        private static readonly Dictionary<Enemy, Pair> _pairs = new Dictionary<Enemy, Pair>();
        private static readonly Dictionary<string, float> _fledUntil = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();

        public static int Lapses { get; private set; }
        public static int Resumed { get; private set; }
        public static int Forced { get; private set; }
        public static int Standoffs { get; private set; }

        public static string Summary => $"lost interest in a cold fight {Lapses} (woken by new contact {Resumed}, dropped after being stuck {Forced}, standoffs through a wall ended {Standoffs})";

        public static void Clear()
        {
            _pairs.Clear();
            _fledUntil.Clear();
            Lapses = Resumed = Forced = Standoffs = 0;
        }

        /// <summary>True while SAIN should not know this enemy any more. Cheap enough for the per-tick check.</summary>
        public static bool IsLapsed(Enemy enemy)
        {
            if (enemy == null || !Settings.ColdFights.Value)
            {
                return false;
            }

            BotOwner bot = enemy.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            // Bosses keep their own way; their guards forget a far ghost too (raid 2026-10-31 17:08: Витёк Заводской bled out in a cover for 6 minutes)
            if (p == null || (p.Kind == BotKind.Other && !BotProfiles.IsCaredGuard(bot, p)))
            {
                return false;
            }

            float now = Time.time;
            if (!_pairs.TryGetValue(enemy, out Pair pair))
            {
                _pairs[enemy] = pair = new Pair { Last = now, Checked = now, Patience = RollPatience(p) };
            }
            else if (now - pair.Checked > 20f && now >= pair.QuietUntil)
            {
                // Not asked about for a while (the enemy was not known): fresh news, a fresh patience
                pair.Last = now;
                pair.Patience = RollPatience(p);
            }

            pair.Checked = now;

            bool contact = Contact(bot, enemy, pair, p);
            if (!contact && _fledUntil.TryGetValue(bot.ProfileId, out float fled) && now < fled)
            {
                return true;
            }

            if (contact)
            {
                pair.Last = now;
                if (pair.QuietUntil > 0f)
                {
                    pair.QuietUntil = 0f;
                    pair.Patience = RollPatience(p);
                    Resumed++;
                }

                return false;
            }

            if (now < pair.QuietUntil)
            {
                return true;
            }

            if (pair.QuietUntil > 0f)
            {
                // The quiet is over: the bot may take the hunt up again
                pair.QuietUntil = 0f;
                pair.Last = now;
                pair.Patience = RollPatience(p);
                return false;
            }

            if (now - pair.Last > pair.Patience)
            {
                pair.QuietUntil = now + Quiet(p);
                Lapses++;
                string why = $"unseen for {now - pair.Last:0} s, {enemy.RealDistance:0} m away";
                if (Lapses <= 15)
                {
                    Log.Info($"LR Combat: {p.Name} ({p.Style}) loses interest in {enemy.EnemyName} - {why} - goes back to what it was doing");
                }
                else
                {
                    Log.Detail("combat", $"{p.Name} loses interest in {enemy.EnemyName} - {why}");
                }

                return true;
            }

            return false;
        }

        /// <summary>The bot has run from a lost fight: until then every enemy that is not right on it (in sight, shooting) is out of its mind.</summary>
        public static void Fled(BotOwner bot, float seconds) => _fledUntil[bot.ProfileId] = Time.time + seconds;

        /// <summary>The bot cannot get to its enemy at all (stuck against a wall, a door, a fence): he is dropped for a good while.</summary>
        public static bool Drop(BotOwner bot, string why)
        {
            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            Enemy enemy = sain?.GoalEnemy;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (enemy == null || p == null || (p.Kind == BotKind.Other && !BotProfiles.IsCaredGuard(bot, p)) || !Settings.ColdFights.Value)
            {
                return false;
            }

            // He is in sight or shooting at the bot: this is a fight, not a wild goose chase
            if (enemy.IsVisible || enemy.Status.ShotAtMeRecently || bot.Memory?.IsUnderFire == true)
            {
                return false;
            }

            if (!_pairs.TryGetValue(enemy, out Pair pair))
            {
                _pairs[enemy] = pair = new Pair();
            }

            float now = Time.time;
            pair.Checked = now;
            pair.Last = now - 999f;
            pair.QuietUntil = now + 100f + (float)_rng.NextDouble() * 80f;
            Forced++;
            Log.Info($"LR Combat: {p.Name} ({p.Style}) cannot get to {enemy.EnemyName} ({why}) - forgets about him for a while");
            return true;
        }

        private static bool Contact(BotOwner bot, Enemy enemy, Pair pair, BotProfile p)
        {
            if (enemy.IsVisible || enemy.Status.ShotAtMeRecently || bot.Memory?.IsUnderFire == true)
            {
                pair.NearSince = 0f;
                pair.Standoff = false;
                return true;
            }

            if (enemy.Seen && enemy.TimeSinceSeen < 4f)
            {
                pair.NearSince = 0f;
                pair.Standoff = false;
                return true;
            }

            // Somebody next to me, heard or just behind a wall: that is not a far fight - for a while.
            // Raid 2026-11-01 16:11: a scav and three PMCs stood 3-12 m from each other behind a wall for 5 minutes (and again and again for 20),
            // nobody in sight, nobody shooting, each side hearing the other every few seconds - the "heard close by" below renewed the contact for ever.
            // A stretch of that longer than the bot's patience is a standoff: the closeness stops counting until the enemy shows himself or shoots.
            float dist = enemy.RealDistance;
            bool near = dist < 6f || (dist < 30f && enemy.TimeSinceLastKnownUpdated < 3f);
            if (!near)
            {
                return false;
            }

            float now = Time.time;
            if (pair.NearSince <= 0f || now - pair.NearLast > 12f)
            {
                pair.NearSince = now;
                pair.NearLimit = RollStandoff(p);
            }

            pair.NearLast = now;
            if (pair.Standoff)
            {
                return false;
            }

            if (now - pair.NearSince > pair.NearLimit)
            {
                pair.Standoff = true;
                Standoffs++;
                string text = $"{p.Name} ({p.Style}) and {enemy.EnemyName} have heard each other {now - pair.NearSince:0} s through a wall or cover, {dist:0} m apart, nobody in sight or shooting - a standoff, the bot goes back to its business";
                if (Standoffs <= 15)
                {
                    Log.Info("LR Combat: " + text);
                }
                else
                {
                    Log.Detail("combat", text);
                }

                return false;
            }

            return true;
        }

        /// <summary>How long a bot stands a close, blind neighbourhood before it counts as a standoff.</summary>
        private static float RollStandoff(BotProfile p)
        {
            float lo = p.Style == PlayStyle.Hunter || p.Style == PlayStyle.Camper || p.Style == PlayStyle.Rat ? 40f : 28f;
            return (lo + 30f * (float)_rng.NextDouble()) * Settings.ColdFightPatience.Value;
        }

        /// <summary>How long a bot keeps looking for someone it has lost: character, with a spread.</summary>
        private static float RollPatience(BotProfile p)
        {
            float lo, hi;
            switch (p.Style)
            {
                case PlayStyle.Hunter: lo = 70f; hi = 125f; break;
                case PlayStyle.Rusher: lo = 55f; hi = 95f; break;
                case PlayStyle.Sniper: lo = 40f; hi = 70f; break;
                case PlayStyle.Rat:
                case PlayStyle.Camper: lo = 22f; hi = 45f; break;
                default: lo = 35f; hi = 65f; break;
            }

            if (p.Kind == BotKind.Scav)
            {
                lo *= 0.8f;
                hi *= 0.8f;
            }

            return (lo + (hi - lo) * (float)_rng.NextDouble()) * Settings.ColdFightPatience.Value;
        }

        private static float Quiet(BotProfile p)
        {
            float quiet = 80f + (float)_rng.NextDouble() * 90f;
            return p.Style == PlayStyle.Hunter ? quiet * 0.7f : quiet;
        }
    }
}
