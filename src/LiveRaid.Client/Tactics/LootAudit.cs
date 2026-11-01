using System.Collections.Generic;
using EFT;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// How every loot job of LootingBots really ended, and a stop to the "go, open, stand up, walk off, come back" loop.
    /// LootingBots ends a job in OnLootTaskEnd(success): true = the body/container is ignored from then on; false (a thing that did not fit, a weapon it
    /// could not strip, a cancelled task) = NOT ignored, and for a body it is even put in the bot's priority queue - so a bot with a full bag, or a body
    /// with something the bag cannot take, was sent back to it again and again (raid 2026-10-30 15:18: PenOkOh went to the same body five times in
    /// two minutes, lying down three of those times; the same 40 % of body trips were repeats, lying or crouching alike).
    ///  - The result of each job is logged (trace "loot": body/container, seconds, complete / incomplete / interrupted, lying or crouching), with counters.
    ///  - A job that ended by itself incomplete is not repeated for 75 s (RoomSweep and the body decision skip it) and the second time the thing is ignored
    ///    by that bot for good. A job cut short from outside (danger, a fight, a treatment, a hand-over: StopLooting) does not count against the thing.
    /// </summary>
    public static class LootAudit
    {
        public class Snapshot
        {
            public string Id;
            public string Name;
            public LootFinder.LootType Type;
            public bool Lying;
            public float Started;
            public bool Cut;
        }

        private class Run
        {
            public float Started;
            public float CutAt = -9f;
            public bool Lying;
        }

        private const float CoolSeconds = 75f;
        private const int MaxIncomplete = 2;

        private static readonly Dictionary<string, Run> _runs = new Dictionary<string, Run>();
        private static readonly Dictionary<string, (int count, float last)> _incomplete = new Dictionary<string, (int, float)>();

        public static int Complete { get; private set; }
        public static int Incomplete { get; private set; }
        public static int Interrupted { get; private set; }
        public static int GivenUp { get; private set; }
        private static int _lyingJobs;
        private static int _lyingIncomplete;
        private static float _lyingSeconds;
        private static int _crouchJobs;
        private static int _crouchIncomplete;
        private static float _crouchSeconds;

        public static string Summary =>
            $"loot jobs: complete {Complete}, incomplete {Incomplete}, cut short from outside {Interrupted}, things given up after {MaxIncomplete} incomplete tries {GivenUp}; " +
            $"bodies lying {_lyingJobs} jobs (incomplete {_lyingIncomplete}, avg {Avg(_lyingSeconds, _lyingJobs):0.0} s), crouching {_crouchJobs} (incomplete {_crouchIncomplete}, avg {Avg(_crouchSeconds, _crouchJobs):0.0} s)";

        private static float Avg(float sum, int n) => n > 0 ? sum / n : 0f;

        public static void Clear()
        {
            _runs.Clear();
            _incomplete.Clear();
            Complete = Incomplete = Interrupted = GivenUp = 0;
            _lyingJobs = _lyingIncomplete = _crouchJobs = _crouchIncomplete = 0;
            _lyingSeconds = _crouchSeconds = 0f;
        }

        private static string Key(BotOwner bot, string id) => bot.ProfileId + "|" + id;

        /// <summary>The job begins (LootingBots' StartLooting went through).</summary>
        public static void OnStart(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            if (bot != null)
            {
                _runs[bot.ProfileId] = new Run { Started = Time.time, Lying = brain.ActiveLootType == LootFinder.LootType.Corpse && ProneLoot.IsLying(bot) };
            }
        }

        /// <summary>StopLooting was called on a bot with a running job: whatever ends it now was cut from outside.</summary>
        public static void OnCut(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            if (bot != null && brain.LootTaskRunning && _runs.TryGetValue(bot.ProfileId, out Run r))
            {
                r.CutAt = Time.time;
            }
        }

        /// <summary>Before OnLootTaskEnd: what the job was (OnLootTaskEnd clears it).</summary>
        public static Snapshot Ending(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            if (bot == null || string.IsNullOrEmpty(brain.ActiveLootId))
            {
                return null;
            }

            _runs.TryGetValue(bot.ProfileId, out Run r);
            return new Snapshot
            {
                Id = brain.ActiveLootId,
                Name = brain.ActiveLoot != null ? brain.ActiveLoot.name : "-",
                Type = brain.ActiveLootType,
                Lying = r?.Lying ?? false,
                Started = r?.Started ?? Time.time,
                Cut = r != null && Time.time - r.CutAt < 3f,
            };
        }

        /// <summary>After OnLootTaskEnd(success).</summary>
        public static void Ended(LootingBrain brain, Snapshot s, bool success)
        {
            BotOwner bot = brain?.BotOwner;
            if (bot == null || s == null)
            {
                return;
            }

            _runs.Remove(bot.ProfileId);
            float seconds = Time.time - s.Started;
            bool body = s.Type == LootFinder.LootType.Corpse;
            string how = success ? "complete" : s.Cut ? "cut short from outside" : "incomplete";
            string nick = bot.Profile?.Nickname;
            Log.Detail("loot", () => $"{nick}: job on {s.Name} ({s.Type}{(body ? (s.Lying ? ", lying" : ", crouching") : "")}) ended after {seconds:0.0} s - {how}");

            if (success)
            {
                Complete++;
                _incomplete.Remove(Key(bot, s.Id));
            }
            else if (s.Cut)
            {
                Interrupted++;
            }
            else
            {
                Incomplete++;
            }

            if (body)
            {
                if (s.Lying)
                {
                    _lyingJobs++;
                    _lyingSeconds += seconds;
                    if (!success && !s.Cut)
                    {
                        _lyingIncomplete++;
                    }
                }
                else
                {
                    _crouchJobs++;
                    _crouchSeconds += seconds;
                    if (!success && !s.Cut)
                    {
                        _crouchIncomplete++;
                    }
                }
            }

            if (success || s.Cut)
            {
                return;
            }

            string key = Key(bot, s.Id);
            _incomplete.TryGetValue(key, out var seen);
            int count = seen.count + 1;
            _incomplete[key] = (count, Time.time);
            if (count >= MaxIncomplete)
            {
                GivenUp++;
                brain.IgnoreLoot(s.Id);
                Log.Info($"LR Loot: {nick} could not finish {s.Name} ({s.Type}) {count} times (nothing more fits, or a thing it cannot take) - leaves it alone");
            }
        }

        /// <summary>This bot ended a job on this thing incomplete a moment ago: not again for a while (RoomSweep, the body decision).</summary>
        public static bool Cooling(BotOwner bot, string id)
        {
            return bot != null && !string.IsNullOrEmpty(id) && _incomplete.TryGetValue(Key(bot, id), out var seen) && Time.time - seen.last < CoolSeconds;
        }
    }
}
