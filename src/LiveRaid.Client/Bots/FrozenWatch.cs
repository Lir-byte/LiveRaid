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
            public float NoWalkSince = -1f;
            public Vector3 NoWalkAt;
            public float NextNote;
            // 1.2.6: the same place for minutes whatever the bot is busy with (the "wants to move and does not" window above restarts with every task)
            public Vector3 WedgeAt;
            public float WedgeSince;
            public int WedgeHits;
            public float NextRescue;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;

        public static int Freed { get; private set; }
        public static int Frozen { get; private set; }
        public static int Escaped { get; private set; }
        public static int CannotWalk { get; private set; }
        public static int Wedged { get; private set; }
        public static int IdleWedged { get; private set; }

        public static string Summary => $"frozen bots: walking given back after a cut-off treatment {Freed}, could not walk for another reason {CannotWalk}, " +
                                        $"wanted to move and did not {Frozen} (led out of sight {Escaped}, put back on the navmesh after minutes in one place {Wedged}, of those with no enemy about {IdleWedged})";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Freed = Frozen = Escaped = CannotWalk = Wedged = IdleWedged = 0;
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
                // EFT's obstacle check (MovementContext.CanWalk -> ObstacleCollisionFacade.CanMove) says "no" for a moment all the time while a bot runs:
                // raid 2026-11-10 11:43, 1824 of 10136 timeline samples had it, 1365 of them from bots that had just covered 5 m or more. Only a bot that
                // stays where it is counts as one that cannot walk
                if (s.NoWalkSince < 0f || (pos - s.NoWalkAt).sqrMagnitude > 0.5f * 0.5f)
                {
                    s.NoWalkSince = now;
                    s.NoWalkAt = pos;
                }

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
                else if (!medsFlag && now >= s.NextNote && now - s.NoWalkSince >= 5f)
                {
                    s.NextNote = now + 30f;
                    CannotWalk++;
                    Log.Info($"LR Frozen: {p.Name} cannot walk and no treatment is to blame (conditions {mc.PhysicalCondition}) - {Describe(bot, player, mc)}");
                }

                if (medsFlag)
                {
                    // A treatment (or its stale flag, freed above): nothing else to look at until it is over
                    return;
                }

                // Held by an obstacle: the watch below (the same place for minutes, a move that does not happen) is what gets such a bot out - it used to be
                // skipped for exactly the bots pressed into a wall
            }
            else
            {
                s.MedsFlagSince = -1f;
                s.NoWalkSince = -1f;
            }

            if (s.WedgeSince <= 0f || (pos - s.WedgeAt).sqrMagnitude > 1.5f * 1.5f)
            {
                s.WedgeAt = pos;
                s.WedgeSince = now;
                s.WedgeHits = 0;
            }

            // 1.2.8: the same place for minutes with NO enemy about. Raid 2026-11-08 01:29 (Customs): the scav Fedor Tridemaks stood 16 minutes in front of
            // the door of the dorms (an open leaf pressed into him, "stuck at door ... opens it" 90 times, PatrolAssault / Unstick / Back Off in turn) - the
            // wedge count below only grew while a SAIN movement decision against a known enemy was running, and RaidMonitor's rescue only looked at the quest
            // layer; the one rescue he got put him 7 m away in the same trap. Now any bot (not a boss / guard) that has not moved 1.5 m for 3 minutes and is not
            // doing something that stands still by nature is put back on the navmesh, farther each time it happens at the same place
            if (now - s.WedgeSince >= IdleWedgeSeconds && now >= s.NextRescue && !StillByNature(bot, player))
            {
                s.NextRescue = now + 60f;
                if (Raid.RaidMonitor.RescueFrozen(bot, s.WedgeSince, now))
                {
                    Wedged++;
                    IdleWedged++;
                    s.WedgeAt = bot.Position;
                    s.WedgeSince = now;
                    s.WedgeHits = 0;
                    return;
                }
            }

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

            // Raid 2026-11-07 18:57: Bratanoff stood at one point to the centimetre for 3+ minutes (SeekCover / Search / Assist in turn, no path, a door beside
            // him) - the Rescue of RaidMonitor only looked at bots in the quest layer. Three such notes within 2 minutes at the same place: the last step
            // of a player who is stuck - put back on the navmesh a few metres away, out of sight of the camera
            s.WedgeHits++;
            if (s.WedgeHits >= 3 && now - s.WedgeSince >= 120f && now >= s.NextRescue)
            {
                s.NextRescue = now + 120f;
                if (Raid.RaidMonitor.RescueFrozen(bot, s.WedgeSince, now))
                {
                    Wedged++;
                    s.WedgeAt = bot.Position;
                    s.WedgeSince = now;
                    s.WedgeHits = 0;
                }
            }
        }

        private const float IdleWedgeSeconds = 180f;

        /// <summary>Standing in one place is what the bot is meant to do: a loot job, a treatment, a reload, a wait at the exit, a fight it can see, a task with time left.</summary>
        private static bool StillByNature(BotOwner bot, Player player)
        {
            if (LootBrainBusy(player) || bot.Medecine?.Using == true || bot.WeaponManager?.Reload?.Reloading == true)
            {
                return true;
            }

            if (TacticalTasks.TryGet(bot, out TacticalTask task) && task.Until > Time.time)
            {
                return true;
            }

            string layer = Raid.RaidMonitor.SafeLayerName(bot);
            if (layer.IndexOf("Exfil", System.StringComparison.OrdinalIgnoreCase) >= 0 || layer.IndexOf("Extract", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            BotComponent sain = SainBot.Of(bot);
            SAIN.SAINComponent.Classes.EnemyClasses.Enemy e = sain?.GoalEnemy;
            return e != null && (e.IsVisible || e.RealDistance < 60f);
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
