using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Who has been where. Two things players do all the time:
    ///  - they avoid places they have just cleaned out themselves (personal memory);
    ///  - they know a spot that three other squads have just walked through is probably already looted (shared sense of the map).
    /// Regions are the same squares the exploration quests use.
    /// </summary>
    public static class RegionMemory
    {
        private const float OwnMemorySeconds = 900f;
        private const float OthersMemorySeconds = 600f;

        private struct Visit
        {
            public float Time;
            public string BotId;
        }

        private static readonly Dictionary<(int, int), List<Visit>> _visits = new Dictionary<(int, int), List<Visit>>();
        private static readonly Dictionary<string, Dictionary<(int, int), float>> _own = new Dictionary<string, Dictionary<(int, int), float>>();
        private static readonly Dictionary<string, ((int, int) region, float freshness, float at)> _entered = new Dictionary<string, ((int, int), float, float)>();
        private static float _next;

        /// <summary>The region the bot is in now, how fresh it was when the bot came in (1 = untouched) and when that was.</summary>
        public static bool Entered(string botId, out (int, int) region, out float freshness, out float at)
        {
            bool ok = _entered.TryGetValue(botId, out var e);
            region = e.region;
            freshness = ok ? e.freshness : 1f;
            at = e.at;
            return ok;
        }

        public static void Clear()
        {
            _visits.Clear();
            _own.Clear();
            _entered.Clear();
            _next = 0;
        }

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float size = Settings.RoamRegionSize.Value;
            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                Vector3 pos = bot.Position;
                (int, int) region = (Mathf.FloorToInt(pos.x / size), Mathf.FloorToInt(pos.z / size));

                if (!_own.TryGetValue(bot.ProfileId, out Dictionary<(int, int), float> mine))
                {
                    _own[bot.ProfileId] = mine = new Dictionary<(int, int), float>();
                }

                bool newlyEntered = !mine.TryGetValue(region, out float last) || now - last > 120f;
                if (newlyEntered)
                {
                    // How fresh the region was when the bot came in (before this visit counts) - Tempo: a searched region is left sooner
                    _entered[bot.ProfileId] = (region, Freshness(bot.ProfileId, region.Item1, region.Item2), now);
                }

                mine[region] = now;
                if (!newlyEntered)
                {
                    continue;
                }

                if (!_visits.TryGetValue(region, out List<Visit> list))
                {
                    _visits[region] = list = new List<Visit>();
                }

                list.RemoveAll(v => now - v.Time > OthersMemorySeconds);
                list.Add(new Visit { Time = now, BotId = bot.ProfileId });
            }
        }

        /// <summary>1 = untouched region, towards 0 = recently visited (by me: strongly, by others: moderately).</summary>
        public static float Freshness(string botId, int rx, int rz, ICollection<string> squad = null)
        {
            float now = Time.time;
            float f = 1f;

            if (_own.TryGetValue(botId, out Dictionary<(int, int), float> mine) && mine.TryGetValue((rx, rz), out float last))
            {
                float age = now - last;
                if (age < 300f)
                {
                    f *= 0.1f;
                }
                else if (age < OwnMemorySeconds)
                {
                    f *= 0.3f;
                }
            }

            if (_visits.TryGetValue((rx, rz), out List<Visit> list))
            {
                int others = 0;
                foreach (Visit v in list)
                {
                    if (v.BotId != botId && now - v.Time < OthersMemorySeconds && (squad == null || !squad.Contains(v.BotId)))
                    {
                        others++;
                    }
                }

                f *= Mathf.Max(0.35f, 1f / (1f + 0.3f * others));
            }

            return f;
        }
    }
}
