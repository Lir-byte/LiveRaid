using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Vertical coverage (F12 -> 3 "Pull of the upper floors"). Raids 2026-11-05/08: on Customs the bots spent 12 % of their time on upper floors where 28 % of the
    /// loot lies (on Ground Zero 23 % against 14 % - the other way round), and the quest steps on roofs and top floors (the Kord on the bank, the AGS on Capital
    /// Insight) were reached only by chance. A floor here is what tools/coverage.py calls one: a point at least 2.5 m above the lowest spot of its 10 m column.
    ///  - The share of the bots' time spent upstairs is kept as a running average, against the share of the map's loot spots that are upstairs.
    ///  - While the bots stay down, quests whose spots lie upstairs weigh more, while they crowd upstairs, less (softly, never more than x2.2 / less than x0.8);
    ///    each bot to its own appetite for heights (a sniper and an explorer climb more, a camper less).
    /// Only the weight of a quest QuestingBots picks anyway: nobody is moved by force, the walk to a floor is QuestingBots' own.
    /// </summary>
    public static class VerticalLedger
    {
        private const float Cell = 10f;
        public const float Floor = 2.5f;

        private static readonly Dictionary<(int, int), float> _low = new Dictionary<(int, int), float>();
        private static readonly Dictionary<string, float> _questUpper = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _appetite = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static bool _built;
        private static float _next;
        private static float _share = -1f;
        private static float _target;
        private static int _samples;

        public static int Asked { get; private set; }
        public static int Favoured { get; private set; }
        public static int Held { get; private set; }

        private static bool On => Settings.Enabled.Value && Settings.VerticalPull.Value && Settings.VerticalStrength.Value > 0;

        public static void Clear()
        {
            _low.Clear();
            _questUpper.Clear();
            _appetite.Clear();
            _built = false;
            _next = 0f;
            _share = -1f;
            _target = 0f;
            _samples = 0;
            Asked = Favoured = Held = 0;
        }

        public static string Summary => !_built || _share < 0f
            ? "upper floors: not used"
            : $"upper floors: the bots spent {_share:P0} of their time upstairs against {_target:P0} of the loot spots (boost x{Boost():0.00}); quests weighed {Asked} (favoured {Favoured}, held back {Held})";

        private static (int, int) Key(float x, float z) => (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        private static bool Build()
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null || !intel.Ready)
            {
                return false;
            }

            List<Poi> pois = intel.Pois.Concat(QuestFactory.QuestPois.Values.SelectMany(v => v)).ToList();
            if (pois.Count == 0)
            {
                return false;
            }

            foreach (Poi p in pois)
            {
                (int, int) k = Key(p.X, p.Z);
                _low[k] = _low.TryGetValue(k, out float low) ? Mathf.Min(low, p.Y) : p.Y;
            }

            float up = 0f, all = 0f;
            foreach (Poi p in pois.Where(p => p.Kind != PoiKind.Open))
            {
                float w = Mathf.Max(0.3f, p.W);
                all += w;
                if (p.Y - _low[Key(p.X, p.Z)] >= Floor)
                {
                    up += w;
                }
            }

            _target = all > 0f ? up / all : 0f;
            _built = true;
            Log.Detail("info", $"LR Vertical: {_low.Count} columns, {_target:P0} of the loot spots are upstairs (2.5 m above the lowest spot of their column)");
            return true;
        }

        /// <summary>How far above the lowest known spot of its 10 m column the point is (0 where nothing is known).</summary>
        public static float Above(Vector3 p) => _built && _low.TryGetValue(Key(p.x, p.z), out float low) ? p.y - low : 0f;

        public static bool IsUpper(Vector3 p) => Above(p) >= Floor;

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || (!_built && !Build()))
            {
                return;
            }

            int bots = 0, up = 0;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                bots++;
                if (IsUpper(bot.Position))
                {
                    up++;
                }
            }

            if (bots >= 3)
            {
                float f = (float)up / bots;
                _share = _share < 0f ? f : 0.97f * _share + 0.03f * f;
                _samples++;
            }
        }

        /// <summary>1 = as is; up to 3 while the bots stay down, down to 0.75 while they crowd upstairs (only after a minute or so of measuring).</summary>
        public static float Boost()
        {
            if (_share < 0f || _samples < 12)
            {
                return 1f;
            }

            // 1.3.0: the gain was 3 (5 % upstairs against 13 % of the spots gave x1.24 - the time upstairs did not grow in the 1.2.9 raids); now 8, up to x3
            return 1f + Mathf.Clamp((_target - _share) * 8f, -0.25f, 2f);
        }

        /// <summary>The weighted share (0-1) of the quest's spots that lie upstairs.</summary>
        public static float QuestUpperShare(BotQuest quest)
        {
            if (!_built || quest == null)
            {
                return 0f;
            }

            string name = quest.GetName() ?? "";
            if (_questUpper.TryGetValue(name, out float cached))
            {
                return cached;
            }

            float share = 0f;
            if (QuestFactory.QuestPois.TryGetValue(name, out List<Poi> pois) && pois.Count > 0)
            {
                float up = 0f, all = 0f;
                foreach (Poi p in pois)
                {
                    float w = Mathf.Max(0.3f, p.W);
                    all += w;
                    if (IsUpper(p.Pos))
                    {
                        up += w;
                    }
                }

                share = all > 0f ? up / all : 0f;
            }
            else
            {
                int n = 0, u = 0;
                try
                {
                    foreach (BotQuestObjective o in quest.AllObjectives)
                    {
                        Vector3? at = o.GetFirstStepPosition();
                        if (!at.HasValue)
                        {
                            continue;
                        }

                        n++;
                        if (IsUpper(at.Value))
                        {
                            u++;
                        }
                    }
                }
                catch (Exception)
                {
                    // quest without positions yet
                }

                share = n > 0 ? (float)u / n : 0f;
            }

            _questUpper[name] = share;
            return share;
        }

        private static float Appetite(BotOwner bot, BotProfile p)
        {
            if (_appetite.TryGetValue(bot.ProfileId, out float a))
            {
                return a;
            }

            float basic;
            switch (p.Style)
            {
                case PlayStyle.Sniper: basic = 1.5f; break;
                case PlayStyle.Explorer: basic = 1.4f; break;
                case PlayStyle.Hunter: basic = 1.2f; break;
                case PlayStyle.Quester: basic = 1.1f; break;
                case PlayStyle.Looter: basic = 1.0f; break;
                case PlayStyle.Rusher: basic = 0.9f; break;
                case PlayStyle.Rat: basic = 0.8f; break;
                default: basic = 0.6f; break;
            }

            a = basic * (0.8f + 0.4f * (float)_rng.NextDouble());
            _appetite[bot.ProfileId] = a;
            return a;
        }

        /// <summary>QuestingBots' wish for a quest with spots upstairs. Only PMCs and player scavs.</summary>
        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On || bot == null || p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
            {
                return 1f;
            }

            float share = QuestUpperShare(quest);
            if (share <= 0.05f)
            {
                return 1f;
            }

            float boost = Boost();
            float s = Settings.VerticalStrength.Value / 100f;
            float m = Mathf.Clamp(1f + (boost - 1f) * share * Appetite(bot, p) * s, 0.8f, 3f);
            Asked++;
            if (m > 1.15f)
            {
                Favoured++;
            }
            else if (m < 0.9f)
            {
                Held++;
            }

            return m;
        }
    }
}
