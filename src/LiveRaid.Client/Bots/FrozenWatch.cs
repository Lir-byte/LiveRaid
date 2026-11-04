using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using LiveRaid.Client.Tactics;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Bots that cannot move (F12 -> 2 "Free frozen bots"). A player's tester (2026-11-02): "Reider123 stood on an embankment on one spot for a long time
    /// - he wanted to move but his movement was frozen - until they began to shoot at him".
    ///
    /// 1. CANNOT WALK, WITH NO REASON. EFT's MovementContext.CanWalk is false while the physical condition has HealingLegs (a treatment that must be done
    ///    standing: set when a MedEffect with NoMove starts, cleared when the last MedEffect ends) or while the obstacle facade says no. A treatment cut off in
    ///    the middle - a hit, LiveRaid's break-off of a heal ("breaks off its Анальгин after 0.3 s"), the hands taken back by HandsGuard after "using meds for 3 s
    ///    with a gun in its hands", 16 hung meds and 18 treatments broken off by shots in the raid of 2026-11-02 - can leave UsingMeds/HealingLegs up while no
    ///    meds are in the hands: the bot "wants" to move (SAIN, QuestingBots, the exit layer all give it a path) and does not take a step. A hit ends the MedEffect
    ///    (the damage event), which is why it started to move when shot at. Here: not able to walk, a meds flag up, nothing being used and no meds in the hands
    ///    for 2 s -> the MedEffects are removed and the flags put down (the leg flags put back as EFT itself does after a MedEffect).
    ///
    /// 2. WANTS TO MOVE AND DOES NOT. A SAIN movement decision (take cover, fall back, shift cover, rush, search) with an enemy about, no reload, no treatment,
    ///    no task, and not 1.2 m of progress in 10 s: the state is written to the log (can it walk, the conditions, the speed limits, the weight, the pose, the
    ///    path) so that the cause is seen, and the bot is led out of the enemy's sight to a spot up to 30 m away (SightCover.Escape) - the usual step of 3 m
    ///    had led it back to the very same open place.
    /// </summary>
    public static class FrozenWatch
    {
        private class State
        {
            public Vector3 At;
            public float Since;
            public float NextTry;
            public float MedsFlagSince = -1f;
            public float NextNote;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;

        public static int Freed { get; private set; }
        public static int Frozen { get; private set; }
        public static int Escaped { get; private set; }
        public static int CannotWalk { get; private set; }

        public static string Summary => $"frozen bots: walking given back after a cut-off treatment {Freed}, could not walk for another reason {CannotWalk}, " +
                                        $"wanted to move and did not {Frozen} (led out of sight {Escaped})";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Freed = Frozen = Escaped = CannotWalk = 0;
        }

        public static void Tick()
        {
            if (!Settings.FrozenWatch.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot) || player.MovementContext == null)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                try
                {
                    Look(bot, player, p);
                }
                catch (System.Exception e)
                {
                    Diag.Report("FrozenWatch", e);
                }
            }
        }

        private static State Of(BotOwner bot, Vector3 pos)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { At = pos, Since = Time.time };
            }

            return s;
        }

        private static void Look(BotOwner bot, Player player, BotProfile p)
        {
            MovementContext mc = player.MovementContext;
            float now = Time.time;
            Vector3 pos = bot.Position;
            State s = Of(bot, pos);

            // 1. Cannot walk
            if (!mc.CanWalk)
            {
                bool medsFlag = mc.PhysicalConditionContainsAny(EPhysicalCondition.UsingMeds | EPhysicalCondition.HealingLegs);
                object hands = player.HandsController;
                bool medsReal = bot.Medecine?.Using == true || hands is IMedsController || (hands != null && hands.GetType().Name.Contains("QuickUse"));
                if (medsFlag && !medsReal)
                {
                    if (s.MedsFlagSince < 0f)
                    {
                        s.MedsFlagSince = now;
                    }
                    else if (now - s.MedsFlagSince >= 2f)
                    {
                        Free(bot, player, p, mc, now - s.MedsFlagSince);
                        s.MedsFlagSince = -1f;
                    }
                }
                else if (!medsFlag && now >= s.NextNote)
                {
                    s.NextNote = now + 30f;
                    CannotWalk++;
                    Log.Info($"LR Frozen: {p.Name} cannot walk and no treatment is to blame (conditions {mc.PhysicalCondition}) - {Describe(bot, player, mc)}");
                }

                return;
            }

            s.MedsFlagSince = -1f;

            // 2. Wants to move and does not
            if ((pos - s.At).sqrMagnitude > 1.2f * 1.2f)
            {
                s.At = pos;
                s.Since = now;
                return;
            }

            BotComponent sain = SainBot.Of(bot);
            ECombatDecision d = sain?.Decision?.CurrentCombatDecision ?? ECombatDecision.None;
            bool wants = d == ECombatDecision.SeekCover || d == ECombatDecision.Retreat || d == ECombatDecision.ShiftCover || d == ECombatDecision.RunAway
                         || d == ECombatDecision.MoveToEngage || d == ECombatDecision.RushEnemy || d == ECombatDecision.Search;
            SAIN.SAINComponent.Classes.EnemyClasses.Enemy e = sain?.GoalEnemy;
            if (!wants || e == null || bot.WeaponManager?.Reload?.Reloading == true || bot.Medecine?.Using == true || TacticalTasks.TryGet(bot, out _)
                || LootBrainBusy(player))
            {
                s.Since = now;
                return;
            }

            if (now - s.Since < 10f || now < s.NextTry)
            {
                return;
            }

            s.NextTry = now + 30f;
            Frozen++;
            string who = e.EnemyPlayer?.Profile?.Nickname ?? "an enemy";
            Log.Info($"LR Frozen: {p.Name} ({d}) has not moved {Vector3.Distance(pos, s.At):0.0} m in {now - s.Since:0} s although it wants to - enemy {who} {e.RealDistance:0} m" +
                     $"{(e.IsVisible ? " in sight" : "")}; {Describe(bot, player, mc)}");
            if (SightCover.Escape(bot, e, $"stood {now - s.Since:0} s in the same place"))
            {
                Escaped++;
            }
        }

        private static bool LootBrainBusy(Player player)
        {
            LootingBots.Components.LootingBrain brain = player.GetComponent<LootingBots.Components.LootingBrain>();
            return brain != null && (brain.IsBotLooting || brain.LootTaskRunning);
        }

        private static void Free(BotOwner bot, Player player, BotProfile p, MovementContext mc, float seconds)
        {
            try
            {
                (player.HealthController as ActiveHealthController)?.RemoveMedEffect();
            }
            catch (System.Exception e)
            {
                Diag.Report("FrozenWatch.RemoveMedEffect", e);
            }

            string flags = mc.PhysicalCondition.ToString();
            mc.SetPhysicalCondition(EPhysicalCondition.UsingMeds, false);
            mc.SetPhysicalCondition(EPhysicalCondition.HealingLegs, false);
            IHealthController hc = player.HealthController;
            if (hc != null)
            {
                mc.SetPhysicalCondition(EPhysicalCondition.LeftLegDamaged, hc.IsBodyPartBroken(EBodyPart.LeftLeg) || hc.IsBodyPartDestroyed(EBodyPart.LeftLeg));
                mc.SetPhysicalCondition(EPhysicalCondition.RightLegDamaged, hc.IsBodyPartBroken(EBodyPart.RightLeg) || hc.IsBodyPartDestroyed(EBodyPart.RightLeg));
            }

            Freed++;
            Log.Info($"LR Frozen: {p.Name} could not walk for {seconds:0} s - a treatment cut off in the middle had left it 'healing' with no meds in its hands " +
                     $"(conditions were {flags}) - the treatment is cleared, it can walk");
        }

        /// <summary>The state that matters when a bot does not move.</summary>
        private static string Describe(BotOwner bot, Player player, MovementContext mc)
        {
            PhysicalBase phys = player.Physical;
            string weight = "";
            try
            {
                weight = $", {Load.Kg(bot):0} kg (overweight {phys?.Overweight ?? 0f:0.00}, walk limit {phys?.WalkSpeedLimit ?? 1f:0.00})";
            }
            catch
            {
                // no physical data
            }

            return $"can walk {mc.CanWalk}, conditions {mc.PhysicalCondition}, hands {Tactics.HandsGuard.HandsNote(bot) ?? "gun"}, legs {LegCare.Describe(player) ?? "ok"}, " +
                   $"state speed limit {mc.StateSpeedLimit:0.00}, pose {mc.PoseLevel:0.0}, sprint {mc.IsSprintEnabled}, grounded {mc.IsGrounded}, " +
                   $"EFT path {bot.Mover?.HasPathAndNoComplete}{weight}";
        }
    }
}
