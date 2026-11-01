using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// PMCs get their quest steps done (F12 -> 10 "Head for the task", "Squads spread over quests", "Give up a quest step after (min)").
    ///
    /// Three raids of 2026-10-26/27 (Customs, 17-36 min), QuestingBots steps done per bot-minute: scavs 0.8-1.2, PMCs 0.07-0.13 - and not a single step of an
    /// EFT quest. A solo PMC did fine (Myksa: 6 steps in 4 min); squad leaders did 0-3 in 15-25 min. MeowletteBlackpaw held "LR Hot 58_-60" for 13 min
    /// within 100 m: quest step -> "Regroup" (its followers 30-60 m away, looting) -> a loot break of its own -> the quest step again, round and round; the
    /// squad loot wait made it stand up to 2-3 min for each looting follower. 37 % of PMC time went into QuestingBots' "CheckForLoot", 25 % into the
    /// quest. Each PMC held one quest the whole raid, and six PMCs of different squads held the same area.
    ///
    /// Here, for PMC squad leaders and solo PMCs (player scavs too):
    ///  - "on the way to the task": a new step more than 40 m away (55-90 % of the time, by quest focus), or a step hung about for 3-6 min, starts a
    ///    drive of 40-150 s (by distance and greed): loot breaks stop only for what lies within 12 m and for bodies; the squad's followers loot only
    ///    within 12 m too; a leader waits for a looting follower at most 30-45 s;
    ///  - a step held for 75-130 % of "Give up a quest step after" (10 min) without a fight in the last 30 s is dropped (QuestingBots' FailObjective) -
    ///    the bot takes another step or, after 5 min on the quest, another quest;
    ///  - "spread": a quest held right now by N other PMC squads is wanted 0.5^N as much (key places 0.75^N) - QbDesirabilityPatch.
    /// </summary>
    public static class QuestDrive
    {
        private class State
        {
            public object Assignment;
            public float HeldSince;
            public float DriveUntil;
            public float StaleAt;
            public float GiveUpAt;
            public bool StaleDriven;
            public string Quest;
            public float BestAway;      // closest the bot has been to the step
            public float BestAt;        // when it last got 20 m closer than before
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly Dictionary<string, HashSet<int>> _holders = new Dictionary<string, HashSet<int>>();
        private static float _next;
        private static float _nextHolders;

        public static int Drives { get; private set; }
        public static int StaleDrives { get; private set; }
        public static int GivenUp { get; private set; }
        public static int NoProgress { get; private set; }
        public static int BreaksCut { get; private set; }
        public static int WaitsCut { get; private set; }

        public static string Summary =>
            $"quest drive: on the way to a step {Drives} times (after hanging about one {StaleDrives}), loot breaks kept to the way {BreaksCut}, squad loot waits cut short {WaitsCut}, steps given up after long {GivenUp}, steps dropped for no progress {NoProgress}";

        public static void Clear()
        {
            _states.Clear();
            _holders.Clear();
            _next = _nextHolders = 0f;
            Drives = StaleDrives = GivenUp = NoProgress = BreaksCut = WaitsCut = 0;
        }

        private static bool Applies(BotOwner bot, out BotProfile p)
        {
            p = BotProfiles.Get(bot);
            return p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav);
        }

        private static bool IsFollower(BotOwner bot) => bot.BotFollower != null && bot.BotFollower.HaveBoss;

        private static State Get(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            return s;
        }

        /// <summary>The bot (or, for a follower, its leader) is on its way to a quest step: loot only what lies on the way.</summary>
        public static bool Driving(BotOwner bot)
        {
            if (!Settings.QuestDriveOn.Value || bot == null)
            {
                return false;
            }

            BotOwner lead = IsFollower(bot) ? TacticalTasks.SquadLeaderOf(bot) : bot;
            return lead != null && _states.TryGetValue(lead.ProfileId, out State s) && Time.time < s.DriveUntil;
        }

        internal static void CutBreak() => BreaksCut++;

        /// <summary>How long a leader on its way waits for a looting squadmate (null = the usual wait).</summary>
        public static float? LeaderWaitCap(BotOwner leader)
        {
            if (!Driving(leader))
            {
                return null;
            }

            WaitsCut++;
            return Random.Range(30f, 45f);
        }

        /// <summary>QuestingBots gave the bot a new step.</summary>
        public static void OnAssigned(BotObjectiveManager manager, BotOwner bot)
        {
            if (!Settings.QuestDriveOn.Value || bot == null || IsFollower(bot) || !Applies(bot, out BotProfile p))
            {
                return;
            }

            State s = Get(bot);
            float away = manager.DistanceToObjective;
            if (float.IsNaN(away) || away < 40f)
            {
                return;
            }

            float chance = Mathf.Clamp01(0.55f + 0.35f * p.QuestFocus);
            if (Random.value > chance)
            {
                return;
            }

            StartDrive(bot, s, p, away, manager.CurrentAssignment?.QuestAssignment?.GetName(), "a new step");
        }

        private static void StartDrive(BotOwner bot, State s, BotProfile p, float away, string quest, string why)
        {
            float greed = Mathf.Clamp(p.LootGreed, 0.3f, 1.6f);
            float length = Mathf.Clamp(away / 4.5f + 20f, 40f, 150f) * (1.35f - 0.35f * greed);
            s.DriveUntil = Time.time + length;
            Drives++;
            Log.Detail("quest", () => $"{p.Name} heads for its quest step of \"{quest}\" ({away:0} m, {why}) - loots only on the way for {length:0} s");
        }

        /// <summary>Once a second: steps hung about get a drive, steps held far too long are dropped; the quest holders are counted.</summary>
        public static void Tick()
        {
            float now = Time.time;
            if (now >= _nextHolders)
            {
                _nextHolders = now + 2f;
                CountHolders();
            }

            if (!Settings.QuestDriveOn.Value || now < _next)
            {
                return;
            }

            _next = now + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || IsFollower(bot) || !Applies(bot, out BotProfile p))
                {
                    continue;
                }

                try
                {
                    Watch(bot, p, now);
                }
                catch (System.Exception e)
                {
                    Diag.Report("QuestDrive", e);
                }
            }
        }

        private static void Watch(BotOwner bot, BotProfile p, float now)
        {
            BotObjectiveManager m = bot.GetObjectiveManager();
            BotJobAssignment a = m?.CurrentAssignment;
            State s = Get(bot);
            if (a == null || !m.IsJobAssignmentActive)
            {
                s.Assignment = null;
                return;
            }

            if (!ReferenceEquals(s.Assignment, a))
            {
                s.Assignment = a;
                s.HeldSince = now;
                s.StaleDriven = false;
                s.Quest = a.QuestAssignment?.GetName();
                s.StaleAt = now + Random.Range(180f, 360f);
                s.BestAway = float.MaxValue;
                s.BestAt = now;
                float minutes = Settings.QuestStepGiveUpMin.Value;
                s.GiveUpAt = minutes > 0f ? now + minutes * 60f * Random.Range(0.75f, 1.3f) : float.MaxValue;
            }

            // Fights do not count against the step
            if (BotMind.SinceEngaged(bot) < 5f)
            {
                s.StaleAt += 1f;
                s.GiveUpAt += 1f;
                return;
            }

            float away = m.DistanceToObjective;
            if (!s.StaleDriven && now > s.StaleAt && !float.IsNaN(away) && away > 15f)
            {
                s.StaleDriven = true;
                StaleDrives++;
                StartDrive(bot, s, p, away, s.Quest, $"hung about it for {(now - s.HeldSince) / 60f:0.0} min");
            }

            // Not getting any closer: a step 140 m away held 8 minutes while the leader and his follower waited for each other (raid 2026-11-01 16:11, "Regroup"
            // against "FollowBoss"). Fights, loot and a visit to a room are no reason; a bot with no way to the step is.
            if (!float.IsNaN(away))
            {
                if (away < s.BestAway - 20f || s.BestAway == float.MaxValue)
                {
                    s.BestAway = away;
                    s.BestAt = now;
                }
                else if (away > 30f && now - s.BestAt > 150f && BotMind.SinceEngaged(bot) > 20f && !RoomSweep.InVisit(bot) && !LootBusy(bot))
                {
                    NoProgress++;
                    s.BestAt = now;
                    s.GiveUpAt = float.MaxValue;
                    Log.Info($"LR Quest: {p.Name} ({p.Kind}) is no closer than {s.BestAway:0} m to a step of \"{s.Quest}\" after 2.5 min ({away:0} m now, held {(now - s.HeldSince) / 60f:0.0} min) - drops it for another");
                    m.FailObjective();
                    return;
                }
            }

            if (now > s.GiveUpAt && BotMind.SinceEngaged(bot) > 30f && !RoomSweep.InVisit(bot))
            {
                float held = now - s.HeldSince;
                s.GiveUpAt = float.MaxValue;
                GivenUp++;
                Log.Info($"LR Quest: {p.Name} ({p.Kind}) has held a step of \"{s.Quest}\" for {held / 60f:0.0} min without getting it done" +
                         $"{(float.IsNaN(away) ? "" : $" ({away:0} m from it)")} - drops it for another");
                m.FailObjective();
            }
        }

        private static bool LootBusy(BotOwner bot)
        {
            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            return brain != null && (brain.LootTaskRunning || brain.HasActiveLootable);
        }

        private static void CountHolders()
        {
            _holders.Clear();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !Settings.QuestSpread.Value)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || IsFollower(bot) || BotProfiles.Get(bot)?.Kind != BotKind.Pmc)
                {
                    continue;
                }

                string quest = bot.GetObjectiveManager()?.CurrentAssignment?.QuestAssignment?.GetName();
                if (string.IsNullOrEmpty(quest))
                {
                    continue;
                }

                if (!_holders.TryGetValue(quest, out HashSet<int> squads))
                {
                    _holders[quest] = squads = new HashSet<int>();
                }

                squads.Add(SquadTies.SquadOf(bot.ProfileId));
            }
        }

        /// <summary>Wish multiplier for a quest held by other PMC squads right now (1 = nobody else).</summary>
        public static float SpreadMultiplier(BotOwner bot, string quest, bool keyPlace)
        {
            if (!Settings.QuestSpread.Value || bot == null || string.IsNullOrEmpty(quest) || BotProfiles.Get(bot)?.Kind != BotKind.Pmc
                || !_holders.TryGetValue(quest, out HashSet<int> squads))
            {
                return 1f;
            }

            int mine = SquadTies.SquadOf(bot.ProfileId);
            int others = 0;
            foreach (int sq in squads)
            {
                if (sq != mine)
                {
                    others++;
                }
            }

            return others == 0 ? 1f : Mathf.Pow(keyPlace ? 0.75f : 0.5f, others);
        }
    }
}
