using System.Collections.Generic;
using EFT;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// SAIN re-decides ten times a second and keeps nothing it started (only a run to cover is carried on). When the enemy peeks and
    /// hides, the bot went cover -> stand and shoot -> shift cover -> search within a second or two and every switch restarts its movement:
    /// the jerky "can't make up its mind" look. A player commits to a move for a moment. Here a manoeuvre SAIN chose is kept for a short
    /// time (search / rush / approach 2.5 s, cover 1.8 s, stand and shoot 1.2 s, freeze 1 s; +-20 % per bot) before another replaces it.
    /// Never held: anything urgent (grenade, melee, dogfight, retreat - SAIN's out-of-ammo fallback and LiveRaid's morale), and the hold
    /// ends at once when the enemy comes into or goes out of view, the bot is hit, the enemy changes, or something else (a squad decision,
    /// healing, reloading, a run to cover) took over in between. F12 -> 9 "Smooth combat decisions".
    /// </summary>
    public static class DecisionHold
    {
        private class State
        {
            public ECombatDecision Decision;
            public float Since;
            public string EnemyId;
            public bool Visible;
            public float Factor;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();

        public static int Held { get; private set; }

        public static void Clear()
        {
            _states.Clear();
            Held = 0;
        }

        public static void Apply(EnemyDecisionClass decisions, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            BotOwner bot = decisions?.BotOwner;
            SAIN.Components.BotComponent sain = decisions?.Bot;
            if (bot == null || sain?.Decision == null || enemy == null)
            {
                return;
            }

            Bots.BotProfile p = Bots.BotProfiles.Get(bot);
            if (p == null || p.Kind == Bots.BotKind.Other)
            {
                return;
            }

            ECombatDecision chosen = __result ? result : ECombatDecision.None;
            float now = Time.time;
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Factor = 0.8f + 0.4f * (float)_rng.NextDouble() };
                Record(s, chosen, enemy, now);
                return;
            }

            // Something else drove the bot in between (squad decision, healing, reloading, a run to cover, no enemy): start afresh
            if (!Settings.SmoothCombatDecisions.Value || sain.Decision.CurrentCombatDecision != s.Decision)
            {
                Record(s, chosen, enemy, now);
                return;
            }

            if (chosen == s.Decision)
            {
                return;
            }

            float hold = HoldFor(s.Decision, Settings.LongGunHolds.Value && CombatSense.IsLongGun(p)) * s.Factor;
            float held = now - s.Since;
            if (hold <= 0f || Urgent(chosen) || held >= hold || enemy.EnemyProfileId != s.EnemyId || enemy.IsVisible != s.Visible
                || HitReaction.SinceHit(bot) < held)
            {
                Record(s, chosen, enemy, now);
                return;
            }

            // Carry the current manoeuvre on a moment longer
            result = s.Decision;
            __result = true;
            Held++;
        }

        private static void Record(State s, ECombatDecision decision, Enemy enemy, float now)
        {
            s.Decision = decision;
            s.Since = now;
            s.EnemyId = enemy.EnemyProfileId;
            s.Visible = enemy.IsVisible;
        }

        private static bool Urgent(ECombatDecision d)
        {
            switch (d)
            {
                case ECombatDecision.AvoidGrenade:
                case ECombatDecision.MeleeAttack:
                case ECombatDecision.DogFight:
                case ECombatDecision.FightZombies:
                case ECombatDecision.ThrowGrenade:
                case ECombatDecision.Retreat:
                case ECombatDecision.RunAway:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>How long a manoeuvre is carried through (0 = not held).</summary>
        private static float HoldFor(ECombatDecision d, bool longGun)
        {
            // A rifleman with a sniper/marksman rifle commits longer: cover -> stand and shoot -> cover every second was the "runs back and forth" of a marksman
            if (longGun)
            {
                switch (d)
                {
                    case ECombatDecision.StandAndShoot:
                    case ECombatDecision.ShootDistantEnemy: return 2.6f;
                    case ECombatDecision.SeekCover: return 2f;
                    case ECombatDecision.ShiftCover: return 3f;
                }
            }

            switch (d)
            {
                // Only moves that go somewhere are carried through (raid 2026-10-20: holding cover / stand-and-shoot / freeze too
                // made bots look slow to react). SAIN's own run to cover is kept by SAIN itself
                case ECombatDecision.Search:
                case ECombatDecision.RushEnemy:
                case ECombatDecision.MoveToEngage:
                case ECombatDecision.CreepOnEnemy:
                    return 1.8f;
                case ECombatDecision.ShiftCover:
                    return 1.2f;
                default:
                    return 0f;
            }
        }
    }
}
