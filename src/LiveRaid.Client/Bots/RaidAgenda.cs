using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Raid;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The aims of a bot's raid (F12 -> 3 "Aims of the raid"). A player does not wander at random: he goes in to loot the dorms, to do a quest, to see what is
    /// on the roof, to get to a corner of the map nobody has been to - two to four such aims at a time, and he changes them as the raid shows him things.
    /// Here every PMC and player scav rolls once whether it plays "directed" (F12 chance, 70 %; the rest go as before). A directed bot keeps 2-4 aims:
    ///  - a REGION to loot (a part of the map with loot in it, near, not yet gone through by others - looters like the rich ones);
    ///  - a QUEST of its own (QuestingBots' map quests and the game's, picked from the ones it may take - questers like them);
    ///  - the HEIGHTS (quests whose spots lie upstairs - explorers, snipers, hunters);
    ///  - a COLD corner (a region nobody has set foot in - explorers).
    /// An aim only makes the quests that serve it weigh 1.6-2.4 times more in QuestingBots' choice (each bot to its own degree), it never takes a quest
    /// away from another: the bot still fights, loots on the way, answers the squad and chooses anew every time a quest ends. Aims are reconsidered every
    /// 70-110 s: one that others have since covered (a region nobody needs any more), whose time ran out, or when the bot heads for the exit, is dropped, and
    /// a new one drawn from what the map looks like now.
    /// </summary>
    public static class RaidAgenda
    {
        private enum Kind
        {
            Region,
            Quest,
            Height,
            Cold,

            // 1.4.0: what lies under the ground - bunkers, basements (Depths)
            Depth,
        }

        private class Goal
        {
            public Kind Kind;
            public string Label;
            public int Rx, Rz;
            public string Quest;
            public float Until;
        }

        private class Agenda
        {
            public bool Directed;
            public float Focus = 1f;
            public int Target = 2;
            public float NextEval;
            public readonly List<Goal> Goals = new List<Goal>();
        }

        private class Region
        {
            public int Rx, Rz;
            public float Heat;
            public readonly List<string> Quests = new List<string>();
        }

        private static readonly Dictionary<string, Agenda> _agendas = new Dictionary<string, Agenda>();
        private static readonly Dictionary<string, (Vector3 at, bool ok)> _centroids = new Dictionary<string, (Vector3, bool)>();
        private static readonly System.Random _rng = new System.Random();
        private static List<Region> _regions;
        private static int _cursor;
        private static float _next;
        private static float _nextQuestScan;

        public static int DirectedBots { get; private set; }
        public static int PlainBots { get; private set; }
        public static int Set { get; private set; }
        public static int Dropped { get; private set; }
        public static int Asked { get; private set; }
        public static int Favoured { get; private set; }
        private static readonly int[] _byKind = new int[5];

        private static bool On => Settings.Enabled.Value && Settings.AgendaOn.Value;

        public static string Summary => !On
            ? "aims of the raid: off"
            : $"aims of the raid: {DirectedBots} bots directed, {PlainBots} plain; aims set {Set} (region {_byKind[0]}, quest {_byKind[1]}, heights {_byKind[2]}, cold corner {_byKind[3]}, depths {_byKind[4]}), " +
              $"dropped {Dropped}; quests weighed {Asked} (served an aim {Favoured})";

        public static void Clear()
        {
            _agendas.Clear();
            _centroids.Clear();
            _regions = null;
            _cursor = 0;
            _next = _nextQuestScan = 0f;
            DirectedBots = PlainBots = Set = Dropped = Asked = Favoured = 0;
            Array.Clear(_byKind, 0, _byKind.Length);
        }

        private static bool Applies(BotProfile p) => p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav);

        private static Agenda Of(BotOwner bot, BotProfile p)
        {
            if (_agendas.TryGetValue(bot.ProfileId, out Agenda a))
            {
                return a;
            }

            a = new Agenda
            {
                Directed = _rng.NextDouble() < Mathf.Clamp01(Settings.AgendaChance.Value),
                Focus = 0.75f + 0.5f * (float)_rng.NextDouble(),
                Target = 2 + (_rng.NextDouble() < 0.5 ? 1 : 0) + (p.Explore > 1.3f && _rng.NextDouble() < 0.5 ? 1 : 0),
                NextEval = Time.time + 6f + 10f * (float)_rng.NextDouble(),
            };
            _agendas[bot.ProfileId] = a;
            if (a.Directed)
            {
                DirectedBots++;
            }
            else
            {
                PlainBots++;
            }

            return a;
        }

        private static float RegionSize => Mathf.Max(60f, Settings.RoamRegionSize.Value);

        private static List<Region> Regions()
        {
            if (_regions != null)
            {
                return _regions;
            }

            Dictionary<(int, int), Region> map = new Dictionary<(int, int), Region>();
            foreach (KeyValuePair<string, float> h in QuestFactory.Heat)
            {
                if (!QuestFactory.TryParseRegion(h.Key, out int rx, out int rz))
                {
                    continue;
                }

                if (!map.TryGetValue((rx, rz), out Region r))
                {
                    map[(rx, rz)] = r = new Region { Rx = rx, Rz = rz };
                }

                r.Heat = Mathf.Max(r.Heat, h.Value);
                r.Quests.Add(h.Key);
            }

            _regions = map.Values.ToList();
            return _regions;
        }

        /// <summary>How fresh the ground of the region still is (1 = nobody has been, 0 = all visited just now); -1 when unknown.</summary>
        private static float Fresh(Region r)
        {
            float sum = 0f;
            int n = 0;
            foreach (string q in r.Quests)
            {
                float f = CoverageLedger.Freshness(q);
                if (f >= 0f)
                {
                    sum += f;
                    n++;
                }
            }

            return n > 0 ? sum / n : -1f;
        }

        private static Vector3 Center(Region r) => new Vector3((r.Rx + 0.5f) * RegionSize, 0f, (r.Rz + 0.5f) * RegionSize);

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>QuestingBots' wish for a quest: the quests that serve one of the bot's aims weigh more.</summary>
        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On || bot == null || !Applies(p))
            {
                return 1f;
            }

            Agenda a = Of(bot, p);
            if (!a.Directed || a.Goals.Count == 0)
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            bool region = QuestFactory.TryParseRegion(name, out int rx, out int rz);
            float m = 1f;
            foreach (Goal g in a.Goals)
            {
                switch (g.Kind)
                {
                    case Kind.Region:
                        if (region && g.Rx == rx && g.Rz == rz)
                        {
                            m *= 1.9f;
                        }

                        break;
                    case Kind.Cold:
                        if (region && g.Rx == rx && g.Rz == rz)
                        {
                            m *= 2f;
                        }

                        break;
                    case Kind.Quest:
                        if (g.Quest == name)
                        {
                            m *= 2.4f;
                        }

                        break;
                    case Kind.Height:
                        if (VerticalLedger.QuestUpperShare(quest) >= 0.5f)
                        {
                            m *= 1.6f;
                        }

                        break;
                    case Kind.Depth:
                        if (Depths.QuestDeepShare(name) >= 0.34f)
                        {
                            m *= 2f;
                        }

                        break;
                }
            }

            if (m <= 1f)
            {
                return 1f;
            }

            m = Mathf.Min(3.2f, Mathf.Pow(m, a.Focus));
            Asked++;
            if (m > 1.2f)
            {
                Favoured++;
            }

            return m;
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world.AllAlivePlayersList.Count == 0 || QuestFactory.Heat.Count == 0)
            {
                return;
            }

            // One bot a second: every bot is looked at every minute or two
            _cursor = (_cursor + 1) % world.AllAlivePlayersList.Count;
            BotOwner bot = world.AllAlivePlayersList[_cursor]?.AIData?.BotOwner;
            BotProfile p = bot != null && !bot.IsDead && bot.BotState == EBotState.Active ? BotProfiles.Get(bot) : null;
            if (!Applies(p) || Spectator.TakeOver.IsTaken(bot))
            {
                return;
            }

            Agenda a = Of(bot, p);
            if (!a.Directed || Time.time < a.NextEval)
            {
                return;
            }

            try
            {
                Evaluate(bot, p, a);
            }
            catch (Exception e)
            {
                a.NextEval = Time.time + 120f;
                Diag.Report("RaidAgenda", e);
            }
        }

        private static void Evaluate(BotOwner bot, BotProfile p, Agenda a)
        {
            float now = Time.time;
            // Not in the middle of a fight: the picture is not what the raid looks like
            if (Tactics.HitReaction.InRealFight(bot, 25f))
            {
                a.NextEval = now + 15f;
                return;
            }

            a.NextEval = now + 70f + 40f * (float)_rng.NextDouble();

            // Leaving: nothing to aim at any more
            if (ExtractPlanner.IsLeaving(bot))
            {
                if (a.Goals.Count > 0)
                {
                    Dropped += a.Goals.Count;
                    a.Goals.Clear();
                }

                return;
            }

            List<Region> regions = Regions();
            for (int i = a.Goals.Count - 1; i >= 0; i--)
            {
                Goal g = a.Goals[i];
                string why = null;
                if (now > g.Until)
                {
                    why = "its time is up";
                }
                else if (g.Kind == Kind.Region || g.Kind == Kind.Cold)
                {
                    Region r = regions.FirstOrDefault(x => x.Rx == g.Rx && x.Rz == g.Rz);
                    float f = r != null ? Fresh(r) : -1f;
                    if (f >= 0f && f < (g.Kind == Kind.Cold ? 0.55f : 0.25f))
                    {
                        why = "the ground has been gone through";
                    }
                }

                if (why != null)
                {
                    a.Goals.RemoveAt(i);
                    Dropped++;
                    Log.Detail("quest", () => $"{p.Name} lets go of its aim \"{g.Label}\" - {why}");
                }
            }

            for (int tries = 0; a.Goals.Count < a.Target && tries < 4; tries++)
            {
                Goal g = Pick(bot, p, a, regions);
                if (g == null)
                {
                    continue;
                }

                a.Goals.Add(g);
                Set++;
                _byKind[(int)g.Kind]++;
                Log.Detail("quest", () => $"{p.Name} ({p.Style}) takes an aim: {g.Label}");
            }
        }

        private static Goal Pick(BotOwner bot, BotProfile p, Agenda a, List<Region> regions)
        {
            // Weights of the kinds by the bot's character
            float[] w = KindWeights(p.Style);
            foreach (Kind k in new[] { Kind.Quest, Kind.Height, Kind.Cold, Kind.Depth })
            {
                if (a.Goals.Any(g => g.Kind == k))
                {
                    w[(int)k] = 0f;
                }
            }

            // The depths are an aim only while there is something down there nobody has seen
            if (!Depths.HasFreshDepths())
            {
                w[(int)Kind.Depth] = 0f;
            }

            if (a.Goals.Count(g => g.Kind == Kind.Region) >= 2)
            {
                w[(int)Kind.Region] = 0f;
            }

            float total = w.Sum();
            if (total <= 0f)
            {
                return null;
            }

            float roll = (float)_rng.NextDouble() * total;
            Kind kind = Kind.Region;
            for (int i = 0; i < w.Length; i++)
            {
                if (roll < w[i])
                {
                    kind = (Kind)i;
                    break;
                }

                roll -= w[i];
            }

            Vector3 pos = bot.Position;
            float now = Time.time;
            switch (kind)
            {
                case Kind.Region:
                case Kind.Cold:
                {
                    Region best = null;
                    float bestScore = 0f;
                    float greed = Mathf.Clamp(p.LootGreed, 0.5f, 1.6f);
                    foreach (Region r in regions)
                    {
                        if (a.Goals.Any(g => (g.Kind == Kind.Region || g.Kind == Kind.Cold) && g.Rx == r.Rx && g.Rz == r.Rz))
                        {
                            continue;
                        }

                        float fresh = Fresh(r);
                        if (fresh < 0f || (kind == Kind.Cold && fresh < 0.9f) || (kind == Kind.Region && fresh < 0.35f))
                        {
                            continue;
                        }

                        float d = Flat(pos, Center(r));
                        float score = kind == Kind.Cold
                            ? (0.2f + r.Heat) / (1f + d / 600f)
                            : Mathf.Pow(0.2f + r.Heat, 0.8f + 0.6f * greed) * (0.15f + fresh) / (1f + d / 300f);
                        score *= 0.5f + (float)_rng.NextDouble();
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = r;
                        }
                    }

                    if (best == null)
                    {
                        return null;
                    }

                    return new Goal
                    {
                        Kind = kind,
                        Rx = best.Rx,
                        Rz = best.Rz,
                        Until = now + (kind == Kind.Cold ? 480f : 420f) + 240f * (float)_rng.NextDouble(),
                        Label = kind == Kind.Cold
                            ? $"a corner nobody has been to ({best.Rx}_{best.Rz}, {Flat(pos, Center(best)):0} m)"
                            : $"loot the region {best.Rx}_{best.Rz} ({Flat(pos, Center(best)):0} m, heat {best.Heat:0.0})",
                    };
                }

                case Kind.Height:
                    return new Goal { Kind = Kind.Height, Until = now + 360f + 240f * (float)_rng.NextDouble(), Label = "the heights (quests with spots upstairs)" };

                case Kind.Depth:
                    return new Goal { Kind = Kind.Depth, Until = now + 420f + 240f * (float)_rng.NextDouble(), Label = "the depths (bunkers, basements - quests with spots under the ground)" };

                default:
                    return PickQuest(bot, p, a);
            }
        }

        private static float[] KindWeights(PlayStyle style)
        {
            // Region, Quest, Height, Cold, Depth
            switch (style)
            {
                case PlayStyle.Looter: return new[] { 3f, 1f, 0.8f, 0.8f, 1.2f };
                case PlayStyle.Quester: return new[] { 1f, 3f, 0.8f, 1f, 0.8f };
                case PlayStyle.Explorer: return new[] { 1.2f, 1.2f, 1.5f, 2.5f, 2f };
                case PlayStyle.Hunter:
                case PlayStyle.Rusher: return new[] { 1.5f, 1f, 1.2f, 0.5f, 0.6f };
                case PlayStyle.Sniper: return new[] { 1f, 0.5f, 3f, 0.5f, 0.2f };
                case PlayStyle.Rat: return new[] { 1.5f, 1f, 1f, 0.5f, 1.2f };
                default: return new[] { 1.5f, 1f, 1f, 0.5f, 0.5f };
            }
        }

        private static Goal PickQuest(BotOwner bot, BotProfile p, Agenda a)
        {
            float now = Time.time;
            // The list of quests a bot may take is long to build: one bot in 4 seconds at most
            if (now < _nextQuestScan)
            {
                return null;
            }

            _nextQuestScan = now + 4f;
            Vector3 pos = bot.Position;
            BotQuest best = null;
            float bestScore = 0f;
            int seen = 0;
            foreach (BotQuest q in bot.GetAllPossibleQuests())
            {
                string name = q.GetName() ?? "";
                if (name.StartsWith(QuestFactory.ExplorePrefix) || name.StartsWith(QuestFactory.LockedPrefix) || name.StartsWith(QuestFactory.PathPrefix) || KeyPlaces.IsKeyPlaceQuest(name)
                    || q.IsCamping || q.IsSniping || name.StartsWith("Spawn Rush") || name.StartsWith("Boss Hunter") || name.IndexOf("Airdrop", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (a.Goals.Any(g => g.Quest == name) || ++seen > 80)
                {
                    continue;
                }

                if (!Centroid(q, out Vector3 at))
                {
                    continue;
                }

                float d = Flat(pos, at);
                float score = Mathf.Max(1f, q.Desirability) * (q.IsEFTQuest ? 0.4f + p.QuestFocus : 1f) / (1f + d / 250f) * (0.5f + (float)_rng.NextDouble());
                if (score > bestScore)
                {
                    bestScore = score;
                    best = q;
                }
            }

            if (best == null)
            {
                return null;
            }

            string chosen = best.GetName();
            Centroid(best, out Vector3 where);
            return new Goal
            {
                Kind = Kind.Quest,
                Quest = chosen,
                Until = now + 480f + 360f * (float)_rng.NextDouble(),
                Label = $"the quest \"{chosen}\" ({Flat(pos, where):0} m)",
            };
        }

        private static bool Centroid(BotQuest q, out Vector3 at)
        {
            string name = q.GetName() ?? "";
            if (_centroids.TryGetValue(name, out (Vector3 at, bool ok) c))
            {
                at = c.at;
                return c.ok;
            }

            Vector3 sum = Vector3.zero;
            int n = 0;
            try
            {
                foreach (BotQuestObjective o in q.AllObjectives)
                {
                    Vector3? p = o.GetFirstStepPosition();
                    if (p.HasValue)
                    {
                        sum += p.Value;
                        n++;
                    }
                }
            }
            catch (Exception)
            {
                // no positions yet
            }

            at = n > 0 ? sum / n : Vector3.zero;
            _centroids[name] = (at, n > 0);
            return n > 0;
        }
    }
}
