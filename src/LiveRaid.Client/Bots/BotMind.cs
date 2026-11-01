using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    public enum MindPhase
    {
        // Nothing going on: quests, loot, errands
        Calm,

        // Knows of an enemy or heard trouble, does not see anyone
        Alert,

        // Sees an enemy, was just hit or is under fire
        Engaged,

        // The fight is over: reload, patch up, listen - not straight back to errands
        Settling,
    }

    /// <summary>
    /// One picture of the bot's situation that every LiveRaid system reads, so they never disagree in the same second.
    /// Before, "calm" was an instant check (does SAIN or EFT hold an enemy right now?), "in a real fight" used time windows of 5-20 s
    /// and "contact" 3 s: an enemy dropping out of SAIN's memory for a second switched all errand layers on and the next second off again.
    /// Here the danger goes up at once and comes down slowly, like a player who reacts to a shot instantly but takes a while to relax:
    /// Engaged lasts 8 s after the last sight / hit / shot at it, Alert turns Calm after 5 quiet seconds, and after a fight the bot
    /// is Settling for 10-25 s by character before errands start again. F12 -> 1 "Smooth decisions" off = the old instant checks.
    /// </summary>
    public static class BotMind
    {
        private const float EngagedHold = 8f;
        private const float AlertHold = 5f;
        private const float RecentFight = 60f;

        private class State
        {
            public MindPhase Phase = MindPhase.Calm;
            public float Since;
            public float LastEngaged = -999f;
            public float LastAlert = -999f;
            public float SettleFor;
            public float Checked = -1f;
            public float SainActive = -999f;
            public string Name;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Changes { get; private set; }

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Changes = 0;
        }

        public static MindPhase PhaseOf(BotOwner bot) => bot == null ? MindPhase.Calm : Update(bot).Phase;

        /// <summary>Calm, and calm long enough (see the class remarks). False while Alert, Engaged or Settling.</summary>
        public static bool IsCalm(BotOwner bot) => bot != null && Update(bot).Phase == MindPhase.Calm;

        /// <summary>Seconds since SAIN last had any decision for the bot (it fights only while it has one).</summary>
        public static float SinceSainActive(BotOwner bot) => bot == null ? 999f : Time.time - Update(bot).SainActive;

        /// <summary>Seconds since the bot last knew of an enemy or was under fire.</summary>
        public static float QuietFor(BotOwner bot) => bot == null ? 0f : Time.time - Update(bot).LastAlert;

        /// <summary>Seconds since the bot last saw an enemy, was hit or shot at (a large number when never).</summary>
        public static float SinceEngaged(BotOwner bot) => bot == null ? 999f : Time.time - Update(bot).LastEngaged;

        /// <summary>Seconds in the current phase.</summary>
        public static float InPhaseFor(BotOwner bot) => bot == null ? 0f : Time.time - Update(bot).Since;

        /// <summary>Four times a second for everyone, so phase changes are noticed (and traced) even when nobody asks.</summary>
        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot != null && !bot.IsDead && bot.BotState == EBotState.Active)
                {
                    Update(bot);
                }
            }
        }

        private static State Update(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Since = Time.time, Name = bot.Profile?.Nickname };
            }

            float now = Time.time;
            if (now - s.Checked < 0.2f)
            {
                return s;
            }

            s.Checked = now;
            var decision = SainBot.Of(bot)?.Decision;
            if (decision != null && (decision.CurrentCombatDecision != SAIN.Preset.Shared.Enums.ECombatDecision.None
                                     || decision.CurrentSquadDecision != SAIN.Preset.Shared.Enums.ESquadDecision.None
                                     || decision.CurrentSelfDecision != SAIN.Preset.Shared.Enums.ESelfActionType.None))
            {
                s.SainActive = now;
            }

            bool engaged = EngagedSignal(bot, out string engagedWhy);
            bool alert = engaged || AlertSignal(bot);
            if (engaged)
            {
                s.LastEngaged = now;
            }

            if (alert)
            {
                s.LastAlert = now;
            }

            MindPhase next = s.Phase;
            string why = null;
            switch (s.Phase)
            {
                case MindPhase.Calm:
                case MindPhase.Settling:
                    if (engaged)
                    {
                        next = MindPhase.Engaged;
                        why = engagedWhy;
                    }
                    else if (alert)
                    {
                        next = MindPhase.Alert;
                        why = "knows of an enemy";
                    }
                    else if (s.Phase == MindPhase.Settling && now - s.Since > s.SettleFor)
                    {
                        next = MindPhase.Calm;
                        why = $"settled after {s.SettleFor:0} s";
                    }

                    break;

                case MindPhase.Alert:
                    if (engaged)
                    {
                        next = MindPhase.Engaged;
                        why = engagedWhy;
                    }
                    else if (now - s.LastAlert > AlertHold)
                    {
                        bool fought = now - s.LastEngaged < RecentFight;
                        next = fought ? MindPhase.Settling : MindPhase.Calm;
                        why = fought ? "quiet again after a fight" : $"quiet for {AlertHold:0} s";
                    }

                    break;

                case MindPhase.Engaged:
                    if (!engaged && now - s.LastEngaged > EngagedHold)
                    {
                        next = alert ? MindPhase.Alert : MindPhase.Settling;
                        why = alert ? "lost sight, still knows of the enemy" : "fight over";
                    }

                    break;
            }

            if (next != s.Phase)
            {
                if (next == MindPhase.Settling)
                {
                    s.SettleFor = SettleSeconds(bot);
                }

                Log.Detail("mind", $"{s.Name} {s.Phase} -> {next} after {now - s.Since:0.0} s: {why}");
                s.Phase = next;
                s.Since = now;
                Changes++;
            }

            return s;
        }

        /// <summary>Sees an enemy (or did a moment ago, close enough to matter), was hit, or is being shot at.</summary>
        private static bool EngagedSignal(BotOwner bot, out string why)
        {
            why = null;
            if (HitReaction.SinceHit(bot) < 4f)
            {
                why = "hit";
                return true;
            }

            if (bot.Memory != null && bot.Memory.UnderFireTime > 0f && Time.time - bot.Memory.UnderFireTime < 3f)
            {
                why = "under fire";
                return true;
            }

            var enemy = SainBot.Of(bot)?.GoalEnemy;
            if (enemy != null && enemy.RealDistance < 150f && (enemy.IsVisible || enemy.TimeSinceSeen < 4f))
            {
                why = "sees an enemy";
                return true;
            }

            if (enemy != null && enemy.RealDistance < 15f && enemy.Heard && enemy.TimeSinceHeard < 3f)
            {
                why = "enemy heard close by";
                return true;
            }

            return false;
        }

        /// <summary>Knows of an enemy at all (SAIN or EFT), or EFT says it is under fire.</summary>
        private static bool AlertSignal(BotOwner bot)
        {
            if (bot.Memory?.GoalEnemy != null || bot.Memory?.IsUnderFire == true)
            {
                return true;
            }

            return SainBot.Of(bot)?.GoalEnemy != null;
        }

        /// <summary>How long after a fight before errands again: rushers soon, rats and campers late (+-20 %).</summary>
        private static float SettleSeconds(BotOwner bot)
        {
            BotProfile p = BotProfiles.Get(bot);
            float s;
            switch (p?.Style)
            {
                case PlayStyle.Rusher: s = 10f; break;
                case PlayStyle.Hunter: s = 14f; break;
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                case PlayStyle.Sniper: s = 25f; break;
                default: s = 18f; break;
            }

            return s * (0.8f + 0.4f * (float)_rng.NextDouble());
        }
    }
}
