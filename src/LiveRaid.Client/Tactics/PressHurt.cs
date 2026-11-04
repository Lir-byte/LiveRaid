using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Pressing a hurt enemy before he patches up (F12 -> 9 "Press a hurt enemy"). A player's tester (2026-11-02): "PMC bots should push an injured
    /// player at once - alone or with teammates - and finish the fight before he has time to heal". Squads had a 12-18 s "push the wounded" card
    /// (SquadPlaybook); a lone bot's morale (CombatSense) never looked at the enemy's state at all.
    /// What a player can know: he or his squad has just hit the enemy (a bot enemy in the last 8 s), he sees the man is badly hurt (a person under 60 %),
    /// or he hears him heal within 25 m (meds in the hands). Then, by character - hunters and rushers nearly always, looters and questers sometimes,
    /// rats, campers and snipers seldom, a hurt bot or one out-numbered two to one never - it presses: Push with a lower bar of nerve, for 15-25 s.
    /// One roll per enemy and hurt moment, so a bot that let it pass does not change its mind every second.
    /// </summary>
    public static class PressHurt
    {
        private class Roll
        {
            public string Enemy;
            public float Until;
            public bool Press;
        }

        private static readonly Dictionary<string, Roll> _rolls = new Dictionary<string, Roll>();
        private static readonly System.Random _rng = new System.Random();

        public static int Presses { get; private set; }
        public static int LetGo { get; private set; }

        public static string Summary => $"hurt enemies pressed by lone bots {Presses}, let go {LetGo}";

        public static void Clear()
        {
            _rolls.Clear();
            Presses = LetGo = 0;
        }

        /// <summary>Why <paramref name="enemy"/> looks hurt to the bot (or its squad), or null.</summary>
        public static string HurtWhy(BotOwner bot, Player enemy, float dist)
        {
            if (enemy == null || enemy.HealthController?.IsAlive != true)
            {
                return null;
            }

            BotOwner enemyBot = enemy.AIData?.BotOwner;
            if (dist <= 25f && (enemyBot != null ? enemyBot.Medecine?.Using == true : enemy.HandsController is Player.MedsController))
            {
                return "heard him patching up";
            }

            if (enemyBot != null)
            {
                if (HitReaction.TryGetHit(enemyBot, out HitReaction.Hit hit) && Time.time - hit.At < 8f && hit.By != null
                    && (hit.By.ProfileId == bot.ProfileId || SquadTies.AreMates(bot.ProfileId, hit.By.ProfileId)))
                {
                    return hit.By.ProfileId == bot.ProfileId ? "hit him" : "the squad hit him";
                }

                return null;
            }

            return enemy.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.6f ? "he is badly hurt" : null;
        }

        /// <summary>The bot presses its closest enemy now (rolled once per enemy and hurt moment, held 15-25 s).</summary>
        public static bool Wants(BotOwner bot, BotProfile p, Enemy closest, float health, int enemies, int allies, out string why)
        {
            why = null;
            if (!Settings.PressHurt.Value || p.Packrat || closest?.EnemyPlayer == null || closest.RealDistance > 90f)
            {
                return false;
            }

            float now = Time.time;
            string enemyId = closest.EnemyPlayer.ProfileId;
            if (_rolls.TryGetValue(bot.ProfileId, out Roll r) && r.Enemy == enemyId && now < r.Until)
            {
                why = r.Press ? "presses the hurt enemy" : null;
                return r.Press;
            }

            why = HurtWhy(bot, closest.EnemyPlayer, closest.RealDistance);
            if (why == null)
            {
                return false;
            }

            float chance = 0.15f + 0.6f * p.Aggression;
            switch (p.Style)
            {
                case PlayStyle.Hunter:
                case PlayStyle.Rusher:
                    chance += 0.2f;
                    break;
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                case PlayStyle.Sniper:
                    chance -= 0.25f;
                    break;
            }

            if (health < 0.5f || enemies >= allies * 2 + 1)
            {
                chance = 0f;
            }

            bool press = _rng.NextDouble() < Mathf.Clamp(chance, 0f, 0.95f);
            _rolls[bot.ProfileId] = new Roll { Enemy = enemyId, Until = now + 15f + (float)_rng.NextDouble() * 10f, Press = press };
            string w = why;
            if (press)
            {
                Presses++;
                Log.Info($"LR Combat: {p.Name} ({p.Style}, {p.Skill}) presses {closest.EnemyPlayer.Profile?.Nickname} before he patches up - {w} ({chance:P0})");
            }
            else
            {
                LetGo++;
                Log.Detail("combat", () => $"{p.Name} lets {closest.EnemyPlayer.Profile?.Nickname} be although {w} ({chance:P0})");
            }

            return press;
        }
    }
}
