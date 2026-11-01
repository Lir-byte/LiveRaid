using System.Collections.Generic;
using EFT;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A bot that "loots" and gets nowhere. Raid 20:11: Лёма Матчмейкер stood 190+ s at one spot in LootingBots' layer, turning 900 degrees per 5 s, taking nothing:
    /// LootingBots starts a loot transaction, it fails (nothing fits, the item cannot be taken), the next 0.2 s it starts again - until its 300 s timeout.
    /// Two guards: the same target started over and over (5 times in 40 s), and one loot job that has gone on 75 s without taking anything while the bot barely moved.
    /// 0.9.9: the 75 s were counted from the start of the job - a full body takes longer (LootingBots waits 3 s after every thing plus the time to examine it):
    /// raid 2026-10-31 00:34, tyvas was taking his fallen squadmate's things one by one and was cut off after 74 s with ~339k still on him. Now a thing taken
    /// (the bot's worth or free cells change) restarts the clock; one job lasts at most 240 s (LootingBots' own timeout is 300 s).
    /// Either way the target is dropped for good (ignored by this bot) and the bot does not scan for a while, so it carries on with its day.
    /// </summary>
    public static class LootWatch
    {
        private class Job
        {
            public string LootId;
            public float Since;
            public Vector3 Pos;
            public float Started;
            public float Worth;
            public int Free;
        }

        private static readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private static readonly Dictionary<string, (string id, int count, float first)> _starts = new Dictionary<string, (string, int, float)>();

        public static int Dropped { get; private set; }

        public static void Clear()
        {
            _jobs.Clear();
            _starts.Clear();
            Dropped = 0;
        }

        public static string Summary => $"loot jobs given up as hopeless {Dropped}";

        /// <summary>LootingBots is about to start looting its current target. False = do not (hopeless: tried again and again).</summary>
        public static bool AllowStart(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            string id = brain?.ActiveLootId;
            if (bot == null || string.IsNullOrEmpty(id))
            {
                return true;
            }

            float now = Time.time;
            if (!_starts.TryGetValue(bot.ProfileId, out var s) || s.id != id || now - s.first > 40f)
            {
                _starts[bot.ProfileId] = (id, 1, now);
                return true;
            }

            _starts[bot.ProfileId] = (id, s.count + 1, s.first);
            if (s.count + 1 < 5)
            {
                return true;
            }

            GiveUp(brain, bot, $"started it {s.count + 1} times in {now - s.first:0} s");
            return false;
        }

        /// <summary>Every half second (from MoveWatch): one loot job that lasts too long while the bot stays where it is.</summary>
        public static void Tick(BotOwner bot, LootingBrain brain)
        {
            if (bot == null || brain == null)
            {
                return;
            }

            float now = Time.time;
            if (!brain.IsBotLooting || string.IsNullOrEmpty(brain.ActiveLootId))
            {
                _jobs.Remove(bot.ProfileId);
                return;
            }

            if (!_jobs.TryGetValue(bot.ProfileId, out Job j) || j.LootId != brain.ActiveLootId)
            {
                _jobs[bot.ProfileId] = new Job { LootId = brain.ActiveLootId, Since = now, Started = now, Pos = bot.Position, Worth = brain.Stats?.NetWorth ?? 0f, Free = brain.Stats?.AvailableGridSpaces ?? 0 };
                return;
            }

            // Something was taken: the job is getting somewhere
            float worth = brain.Stats?.NetWorth ?? 0f;
            int free = brain.Stats?.AvailableGridSpaces ?? 0;
            if (Mathf.Abs(worth - j.Worth) > 1f || free != j.Free)
            {
                j.Worth = worth;
                j.Free = free;
                j.Since = now;
            }

            // Walking to the target is not the job being stuck: the clock restarts whenever the bot has moved
            if ((bot.Position - j.Pos).sqrMagnitude > 3f * 3f)
            {
                j.Pos = bot.Position;
                j.Since = j.Started = now;
                return;
            }

            if (now - j.Since > 75f)
            {
                GiveUp(brain, bot, $"{now - j.Since:0} s at one spot without taking anything");
            }
            else if (now - j.Started > 240f)
            {
                GiveUp(brain, bot, $"{now - j.Started:0} s at one spot without finishing");
            }
        }

        private static void GiveUp(LootingBrain brain, BotOwner bot, string why)
        {
            Dropped++;
            string name = brain.ActiveLoot != null ? brain.ActiveLoot.name : "-";
            Log.Info($"LR Loot: {bot.Profile.Nickname} gives up on {name} ({why}) - it is ignored, the bot goes on");
            _jobs.Remove(bot.ProfileId);
            _starts.Remove(bot.ProfileId);
            try
            {
                brain.StopLooting();
                brain.CleanupLoot(true);
                LootFinder finder = bot.GetPlayer?.GetComponent<LootFinder>();
                finder?.OverrideNextScanTime(15f);
            }
            catch (System.Exception e)
            {
                Diag.Report("LootWatch.GiveUp", e);
            }
        }
    }
}
