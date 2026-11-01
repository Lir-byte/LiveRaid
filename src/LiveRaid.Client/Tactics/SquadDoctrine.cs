using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>How a PMC squad plays this raid (F12 -> 8 "Squad doctrines").</summary>
    public enum DoctrineKind
    {
        Chads,
        PvpHunters,
        Looters,
        Questers,
        Carry,
        Randoms,
        Clan,
        BossHunters,
        Rats,
        Marksmen,
        Speedrunners,
        Tanks,
    }

    /// <summary>Formation a squad keeps on open ground (indoors every squad walks in a column).</summary>
    public enum SquadShape
    {
        Wedge,
        Line,
        Pairs,
        Scout,
        Loose,
    }

    /// <summary>
    /// The pure part of the squad doctrines: which doctrine a squad draws, its numbers, its quirks and every member's own take on it.
    /// No Unity and no EFT here, so the rules can be checked outside the game (tools/bindtest runs SelfTest on thousands of made-up raids):
    ///  - a doctrine goes to at most two PMC squads of a raid (solos count as squads of one); with 12 doctrines that is room for 24 squads -
    ///    more than any lobby holds; a 25th squad would play without one rather than make a third;
    ///  - no two squads are alike: each squad's numbers are drawn inside its doctrine's ranges plus two quirks, and a squad that would come out
    ///    too close to one already in the raid (same doctrine: same quirks or numbers within 0.9 in sum; any doctrine: within 0.5) is drawn again;
    ///  - every member plays the squad's doctrine his own way: his style, skill, aggression, greed, talent and gun shift the squad's numbers
    ///    (a rusher pushes harder than the squad, a novice keeps the formation worse, a sniper wants the range), plus a little personal noise.
    /// </summary>
    public static class DoctrineDeck
    {
        public const int Params = 11;

        // System.Math only (no UnityEngine): SelfTest runs outside the game
        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public const int PerRaid = 2;

        public static readonly string[] ParamNames =
        {
            "aggression", "cohesion", "pace", "comms", "precision", "grit", "range", "greed", "third party", "revenge", "leave early",
        };

        public const int Aggression = 0, Cohesion = 1, Pace = 2, Comms = 3, Precision = 4, Grit = 5, Range = 6, Greed = 7, ThirdParty = 8, Revenge = 9, LeaveEarly = 10;

        /// <summary>What the deck needs to know about a squad member (filled from BotProfile in the game, made up in SelfTest).</summary>
        public struct Traits
        {
            public string Id;
            public string Name;
            public PlayStyle Style;
            public SkillTier Skill;
            public float Aggression;
            public float LootGreed;
            public float Talent;
            public bool LongGun;
            public bool ShortGun;
        }

        public class Template
        {
            public DoctrineKind Kind;
            public string Title;
            public float[] Min;
            public float[] Max;
            public SquadShape[] Shapes;
            public bool Solo = true;
            public bool BossHunt;
            public Dictionary<SquadTactic, float> Tactics;
        }

        public class Quirk
        {
            public string Title;
            public int Group;
            public float[] Delta;
        }

        public class Squad
        {
            public int Id;
            public Template Doctrine;
            public int Copy;
            public float[] Values;
            public Quirk[] Quirks;
            public SquadShape Shape;
            public string Leader;
            public int Size;
            public readonly List<Member> Members = new List<Member>();

            public string Fingerprint
            {
                get
                {
                    if (Doctrine == null)
                    {
                        return "none";
                    }

                    StringBuilder b = new StringBuilder(Doctrine.Kind.ToString());
                    foreach (float v in Values)
                    {
                        b.Append(':').Append((int)Math.Round(v * 100f));
                    }

                    foreach (Quirk q in Quirks)
                    {
                        b.Append('|').Append(q.Title);
                    }

                    return b.ToString();
                }
            }

            public string Describe() => Doctrine == null
                ? "no doctrine"
                : $"\"{Doctrine.Title}\"{(Size == 1 ? " solo" : "")} #{Copy} ({string.Join(", ", Quirks.Select(q => q.Title))}; {Numbers(Values)}; formation {Shape})";
        }

        public class Member
        {
            public string Id;
            public string Name;
            public Squad Squad;
            public float[] Values;

            // Chance to go his own way when the squad calls a tactic (randoms often, a drilled clan hardly ever)
            public float Lapse;

            // Seconds before he follows a call (a novice is slow to catch on)
            public float Delay;
            public string Take;

            public float this[int i] => Values[i];
        }

        public class Raid
        {
            public readonly List<Squad> Squads = new List<Squad>();
            public readonly Dictionary<DoctrineKind, int> Count = new Dictionary<DoctrineKind, int>();
            public int WithoutDoctrine;

            public int MaxPerDoctrine => Count.Count == 0 ? 0 : Count.Values.Max();
        }

        private static float[] V(params float[] v) => v;

        private static Dictionary<SquadTactic, float> T(float pushTogether, float hold, float pinFlank, float pincer, float suppressRush, float bounding,
            float pairPush, float pushWounded, float fallBack, float eachOwn) => new Dictionary<SquadTactic, float>
        {
            [SquadTactic.PushTogether] = pushTogether,
            [SquadTactic.Hold] = hold,
            [SquadTactic.PinAndFlank] = pinFlank,
            [SquadTactic.Pincer] = pincer,
            [SquadTactic.SuppressAndRush] = suppressRush,
            [SquadTactic.Bounding] = bounding,
            [SquadTactic.PairPush] = pairPush,
            [SquadTactic.PushTheWounded] = pushWounded,
            [SquadTactic.FallBackAndRegroup] = fallBack,
            [SquadTactic.EachHisOwn] = eachOwn,
        };

        //                                                  aggr  coh   pace  comms prec  grit  range greed third reven leave
        public static readonly Template[] Doctrines =
        {
            new Template
            {
                Kind = DoctrineKind.Chads, Title = "Штурмовики",
                Min = V(0.75f, 0.35f, 0.75f, 0.40f, 0.35f, 0.70f, 0.10f, 0.30f, 0.70f, 0.60f, 0.10f),
                Max = V(0.95f, 0.60f, 0.95f, 0.80f, 0.70f, 0.95f, 0.40f, 0.60f, 0.95f, 0.90f, 0.35f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Pairs }, Tactics = T(2.2f, 0.3f, 1f, 0.9f, 1.6f, 0.8f, 1.8f, 2f, 0.4f, 0.6f),
            },
            new Template
            {
                Kind = DoctrineKind.PvpHunters, Title = "PvP-охотники",
                Min = V(0.60f, 0.45f, 0.60f, 0.50f, 0.55f, 0.55f, 0.35f, 0.35f, 0.75f, 0.50f, 0.20f),
                Max = V(0.85f, 0.70f, 0.85f, 0.85f, 0.85f, 0.80f, 0.65f, 0.60f, 1.00f, 0.80f, 0.40f),
                Shapes = new[] { SquadShape.Line, SquadShape.Scout }, Tactics = T(1f, 0.8f, 2f, 2f, 1.2f, 1.4f, 1f, 1.5f, 0.9f, 0.5f),
            },
            new Template
            {
                Kind = DoctrineKind.Looters, Title = "Лутеры",
                Min = V(0.25f, 0.30f, 0.55f, 0.30f, 0.35f, 0.25f, 0.30f, 0.80f, 0.10f, 0.20f, 0.55f),
                Max = V(0.50f, 0.60f, 0.80f, 0.60f, 0.65f, 0.50f, 0.60f, 1.00f, 0.35f, 0.45f, 0.85f),
                Shapes = new[] { SquadShape.Pairs, SquadShape.Loose }, Tactics = T(0.6f, 1.8f, 0.8f, 0.5f, 0.5f, 0.9f, 0.5f, 0.8f, 2f, 1f),
            },
            new Template
            {
                Kind = DoctrineKind.Questers, Title = "Квестеры",
                Min = V(0.35f, 0.50f, 0.55f, 0.45f, 0.45f, 0.40f, 0.35f, 0.40f, 0.20f, 0.35f, 0.30f),
                Max = V(0.60f, 0.75f, 0.80f, 0.75f, 0.75f, 0.65f, 0.65f, 0.65f, 0.45f, 0.60f, 0.55f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Line }, Tactics = T(0.9f, 1.4f, 1.3f, 0.8f, 0.9f, 1.3f, 0.7f, 1f, 1.3f, 0.7f),
            },
            new Template
            {
                Kind = DoctrineKind.Carry, Title = "Кэрри (сильный ведёт слабых)",
                Min = V(0.55f, 0.55f, 0.60f, 0.55f, 0.30f, 0.45f, 0.30f, 0.40f, 0.40f, 0.40f, 0.30f),
                Max = V(0.85f, 0.85f, 0.85f, 0.85f, 0.55f, 0.75f, 0.60f, 0.70f, 0.70f, 0.70f, 0.55f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Scout }, Tactics = T(0.9f, 1f, 1.6f, 0.6f, 1.8f, 0.7f, 0.6f, 1.3f, 1f, 1.2f),
            },
            new Template
            {
                Kind = DoctrineKind.Randoms, Title = "Рандомы",
                Min = V(0.30f, 0.10f, 0.40f, 0.10f, 0.05f, 0.20f, 0.20f, 0.40f, 0.30f, 0.30f, 0.30f),
                Max = V(0.80f, 0.35f, 0.90f, 0.50f, 0.30f, 0.60f, 0.80f, 0.80f, 0.70f, 0.80f, 0.70f),
                Shapes = new[] { SquadShape.Loose }, Tactics = T(1f, 1f, 0.4f, 0.2f, 0.4f, 0.2f, 0.5f, 0.8f, 1f, 3f),
            },
            new Template
            {
                Kind = DoctrineKind.Clan, Title = "Слаженный клан",
                Min = V(0.55f, 0.70f, 0.60f, 0.70f, 0.80f, 0.60f, 0.35f, 0.40f, 0.50f, 0.50f, 0.25f),
                Max = V(0.80f, 0.95f, 0.85f, 0.95f, 1.00f, 0.85f, 0.65f, 0.65f, 0.75f, 0.80f, 0.50f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Line }, Tactics = T(1f, 0.9f, 1.6f, 2f, 1.8f, 2.2f, 1.2f, 1.4f, 1.4f, 0.1f),
            },
            new Template
            {
                Kind = DoctrineKind.BossHunters, Title = "Охотники на боссов", BossHunt = true,
                Min = V(0.65f, 0.55f, 0.65f, 0.50f, 0.50f, 0.60f, 0.30f, 0.60f, 0.40f, 0.50f, 0.30f),
                Max = V(0.90f, 0.80f, 0.90f, 0.80f, 0.80f, 0.85f, 0.60f, 0.85f, 0.70f, 0.80f, 0.55f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Pairs }, Tactics = T(1.6f, 0.7f, 1.2f, 1.3f, 1.7f, 1.4f, 1.3f, 1.6f, 0.7f, 0.5f),
            },
            new Template
            {
                Kind = DoctrineKind.Rats, Title = "Тихушники",
                Min = V(0.15f, 0.25f, 0.50f, 0.05f, 0.40f, 0.20f, 0.40f, 0.60f, 0.15f, 0.10f, 0.45f),
                Max = V(0.40f, 0.55f, 0.75f, 0.30f, 0.70f, 0.45f, 0.75f, 0.90f, 0.40f, 0.35f, 0.75f),
                Shapes = new[] { SquadShape.Pairs, SquadShape.Loose }, Tactics = T(0.3f, 1.6f, 1.2f, 0.6f, 0.5f, 0.8f, 0.4f, 1.4f, 2.2f, 1f),
            },
            new Template
            {
                Kind = DoctrineKind.Marksmen, Title = "Дальники",
                Min = V(0.40f, 0.35f, 0.55f, 0.45f, 0.55f, 0.45f, 0.75f, 0.35f, 0.35f, 0.35f, 0.30f),
                Max = V(0.65f, 0.65f, 0.80f, 0.75f, 0.85f, 0.70f, 1.00f, 0.60f, 0.60f, 0.60f, 0.55f),
                Shapes = new[] { SquadShape.Line, SquadShape.Scout }, Tactics = T(0.5f, 2f, 1.2f, 0.9f, 1f, 1.4f, 0.5f, 1f, 1.2f, 0.6f),
            },
            new Template
            {
                Kind = DoctrineKind.Speedrunners, Title = "Торопыги",
                Min = V(0.45f, 0.30f, 0.85f, 0.35f, 0.35f, 0.35f, 0.20f, 0.55f, 0.20f, 0.25f, 0.70f),
                Max = V(0.70f, 0.55f, 1.00f, 0.65f, 0.65f, 0.60f, 0.50f, 0.85f, 0.45f, 0.50f, 0.95f),
                Shapes = new[] { SquadShape.Pairs, SquadShape.Scout }, Tactics = T(1.3f, 0.6f, 0.8f, 0.5f, 0.9f, 0.6f, 1.2f, 1f, 1.6f, 1f),
            },
            new Template
            {
                Kind = DoctrineKind.Tanks, Title = "Бронированный кулак",
                Min = V(0.60f, 0.65f, 0.50f, 0.50f, 0.55f, 0.80f, 0.10f, 0.40f, 0.45f, 0.55f, 0.20f),
                Max = V(0.80f, 0.90f, 0.70f, 0.80f, 0.80f, 1.00f, 0.40f, 0.60f, 0.70f, 0.80f, 0.45f),
                Shapes = new[] { SquadShape.Wedge, SquadShape.Line }, Tactics = T(1.6f, 0.6f, 0.9f, 0.9f, 1.5f, 1.6f, 1.1f, 1.4f, 0.3f, 0.4f),
            },
        };

        //                                                       aggr   coh    pace   comms  prec   grit   range  greed  third  reven  leave
        public static readonly Quirk[] Quirks =
        {
            new Quirk { Title = "болтливые", Group = 0, Delta = V(0f, 0f, 0f, 0.30f, 0f, 0f, 0f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "молчуны", Group = 0, Delta = V(0f, 0f, 0f, -0.30f, 0f, 0f, 0f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "торопливые", Group = 1, Delta = V(0.05f, 0f, 0.15f, 0f, -0.10f, 0f, 0f, 0f, 0f, 0f, 0.05f) },
            new Quirk { Title = "педанты", Group = 1, Delta = V(0f, 0.10f, -0.05f, 0f, 0.20f, 0f, 0f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "осторожные", Group = 2, Delta = V(-0.15f, 0.05f, 0f, 0f, 0f, -0.10f, 0.05f, 0f, -0.10f, 0f, 0.10f) },
            new Quirk { Title = "дерзкие", Group = 2, Delta = V(0.15f, 0f, 0.05f, 0f, 0f, 0.15f, -0.05f, 0f, 0.10f, 0f, -0.05f) },
            new Quirk { Title = "разбредаются", Group = 3, Delta = V(0f, -0.25f, 0f, -0.05f, -0.05f, 0f, 0f, 0.05f, 0f, 0f, 0f) },
            new Quirk { Title = "держатся кучей", Group = 3, Delta = V(0f, 0.25f, 0f, 0.05f, 0f, 0f, 0f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "любят дальний бой", Group = 4, Delta = V(-0.05f, 0f, 0f, 0f, 0.05f, 0f, 0.25f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "любят ближний бой", Group = 4, Delta = V(0.10f, 0f, 0.05f, 0f, 0f, 0.05f, -0.25f, 0f, 0f, 0f, 0f) },
            new Quirk { Title = "стервятники", Group = 5, Delta = V(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.15f, 0.25f, 0f, 0f) },
            new Quirk { Title = "сами по себе", Group = 5, Delta = V(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, -0.25f, 0f, 0.05f) },
            new Quirk { Title = "мстительные", Group = 6, Delta = V(0.05f, 0f, 0f, 0f, 0f, 0.05f, 0f, 0f, 0f, 0.35f, -0.05f) },
            new Quirk { Title = "жадные", Group = 7, Delta = V(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.30f, 0.05f, 0f, -0.10f) },
        };

        public static Template TemplateOf(DoctrineKind kind) => Doctrines.First(d => d.Kind == kind);

        /// <summary>
        /// Deals a doctrine to a squad (all members known so far): weighted by who is in it, never a doctrine two squads of the raid already play,
        /// numbers and quirks drawn again until the squad differs from every other one. Null doctrine only when all 24 places are taken.
        /// </summary>
        public static Squad Deal(Raid raid, int id, IList<Traits> members, System.Random rng)
        {
            Squad squad = new Squad { Id = id, Size = members.Count };
            int size = members.Count;
            List<(Template t, float w)> pool = new List<(Template, float)>();
            foreach (Template t in Doctrines)
            {
                raid.Count.TryGetValue(t.Kind, out int used);
                if (used >= PerRaid || (!t.Solo && size < 2))
                {
                    continue;
                }

                float w = Fit(t.Kind, members);

                // A doctrine already played in this raid is less likely to come again: more kinds per raid
                w *= used == 0 ? 1f : 0.55f;
                if (w > 0f)
                {
                    pool.Add((t, w));
                }
            }

            if (pool.Count == 0)
            {
                // Every doctrine taken twice (or none fits a lone man): the squad plays by its characters alone, no third copy
                foreach (Template t in Doctrines)
                {
                    raid.Count.TryGetValue(t.Kind, out int used);
                    if (used < PerRaid && (t.Solo || size >= 2))
                    {
                        pool.Add((t, 1f));
                    }
                }
            }

            if (pool.Count == 0)
            {
                raid.WithoutDoctrine++;
                raid.Squads.Add(squad);
                return squad;
            }

            Template pick = Pick(pool, rng);
            raid.Count.TryGetValue(pick.Kind, out int copies);
            raid.Count[pick.Kind] = copies + 1;
            squad.Doctrine = pick;
            squad.Copy = copies + 1;

            float bestGap = -1f;
            float[] best = null;
            Quirk[] bestQuirks = null;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                Quirk[] quirks = DrawQuirks(rng);
                float[] values = new float[Params];
                for (int i = 0; i < Params; i++)
                {
                    float v = pick.Min[i] + (float)rng.NextDouble() * (pick.Max[i] - pick.Min[i]);
                    foreach (Quirk q in quirks)
                    {
                        v += q.Delta[i];
                    }

                    values[i] = Clamp01(v);
                }

                float gap = Gap(raid, pick, values, quirks);
                if (gap > bestGap)
                {
                    bestGap = gap;
                    best = values;
                    bestQuirks = quirks;
                }

                if (gap >= 1f)
                {
                    break;
                }
            }

            squad.Values = best;
            squad.Quirks = bestQuirks;
            squad.Shape = pick.Shapes[rng.Next(pick.Shapes.Length)];
            if (squad.Values[Cohesion] < 0.3f)
            {
                squad.Shape = SquadShape.Loose;
            }

            // The member who decides: the best player for a carry, otherwise the first one in (the leader QuestingBots made)
            squad.Leader = pick.Kind == DoctrineKind.Carry
                ? members.OrderByDescending(m => (int)m.Skill + m.Talent).First().Id
                : members[0].Id;
            raid.Squads.Add(squad);
            foreach (Traits m in members)
            {
                squad.Members.Add(Mutate(squad, m));
            }

            return squad;
        }

        /// <summary>
        /// 1 when the squad is far enough from all others (and so it counts as a different squad), less when it is near one: a squad of the same
        /// doctrine needs other quirks or numbers 0.9 apart in sum, a squad of another doctrine 0.5.
        /// </summary>
        private static float Gap(Raid raid, Template t, float[] values, Quirk[] quirks)
        {
            float gap = 1f;
            foreach (Squad other in raid.Squads)
            {
                if (other.Doctrine == null)
                {
                    continue;
                }

                float d = Distance(values, other.Values);
                bool same = other.Doctrine.Kind == t.Kind;
                bool sameQuirks = same && SameQuirks(quirks, other.Quirks);
                float need = same ? 0.9f : 0.5f;
                float g = Math.Min(1f, d / need) * (sameQuirks ? 0.5f : 1f);
                gap = Math.Min(gap, g);
            }

            return gap;
        }

        public static float Distance(float[] a, float[] b)
        {
            float d = 0f;
            for (int i = 0; i < Params; i++)
            {
                d += Math.Abs(a[i] - b[i]);
            }

            return d;
        }

        private static bool SameQuirks(Quirk[] a, Quirk[] b) => a.Length == b.Length && a.All(b.Contains);

        private static Quirk[] DrawQuirks(System.Random rng)
        {
            Quirk first = Quirks[rng.Next(Quirks.Length)];
            Quirk second;
            do
            {
                second = Quirks[rng.Next(Quirks.Length)];
            }
            while (second.Group == first.Group);

            return new[] { first, second };
        }

        private static Template Pick(List<(Template t, float w)> pool, System.Random rng)
        {
            float total = pool.Sum(x => x.w);
            float roll = (float)rng.NextDouble() * total;
            foreach ((Template t, float w) in pool)
            {
                roll -= w;
                if (roll <= 0f)
                {
                    return t;
                }
            }

            return pool[pool.Count - 1].t;
        }

        /// <summary>How well a doctrine suits the people in the squad: a squad of rushers plays the storm troopers more often, rats the quiet ones.</summary>
        public static float Fit(DoctrineKind kind, IList<Traits> m)
        {
            int n = Math.Max(1, m.Count);
            float Share(params PlayStyle[] styles) => m.Count(x => styles.Contains(x.Style)) / (float)n;
            float aggr = m.Count > 0 ? m.Average(x => x.Aggression) : 0.5f;
            float skill = m.Count > 0 ? (float)m.Average(x => (int)x.Skill) / 3f : 0.4f;
            int spread = m.Count > 0 ? m.Max(x => (int)x.Skill) - m.Min(x => (int)x.Skill) : 0;
            float longGuns = m.Count(x => x.LongGun) / (float)n;
            switch (kind)
            {
                case DoctrineKind.Chads: return (0.5f + 1.5f * aggr) * (1f + Share(PlayStyle.Rusher, PlayStyle.Hunter));
                case DoctrineKind.PvpHunters: return 0.6f + 1.5f * Share(PlayStyle.Hunter) + 0.6f * skill;
                case DoctrineKind.Looters: return (0.5f + 2f * Share(PlayStyle.Looter, PlayStyle.Explorer)) * (1.3f - aggr);
                case DoctrineKind.Questers: return 0.6f + 2f * Share(PlayStyle.Quester);
                // A lone man "carries" only himself: the last resort for a solo when every other doctrine is taken twice
                case DoctrineKind.Carry: return m.Count < 2 ? 0f : spread >= 2 ? 2.5f : spread == 1 ? 0.8f : 0.15f;
                case DoctrineKind.Randoms: return 1.6f - 1.2f * skill;
                case DoctrineKind.Clan: return (0.3f + 1.7f * skill) * (m.Count >= 3 ? 1.2f : m.Count == 2 ? 0.9f : 0.5f);
                case DoctrineKind.BossHunters: return (0.5f + aggr) * (skill >= 0.5f ? 1.2f : 0.7f);
                case DoctrineKind.Rats: return (0.5f + 2f * Share(PlayStyle.Rat, PlayStyle.Camper)) * (1.3f - aggr);
                case DoctrineKind.Marksmen: return 0.4f + 2.5f * longGuns + Share(PlayStyle.Sniper);
                case DoctrineKind.Speedrunners: return 0.7f + Share(PlayStyle.Explorer, PlayStyle.Looter, PlayStyle.Rusher);
                case DoctrineKind.Tanks: return (0.4f + aggr) * (0.6f + skill);
                default: return 1f;
            }
        }

        /// <summary>A member's own take on the squad's doctrine: his character moves the squad's numbers, a little noise on top.</summary>
        public static Member Mutate(Squad squad, Traits t)
        {
            Member m = new Member { Id = t.Id, Name = t.Name, Squad = squad, Values = new float[Params] };
            if (squad.Doctrine == null)
            {
                return m;
            }

            float[] d = new float[Params];
            List<string> why = new List<string>();
            int skill = (int)t.Skill;
            d[Aggression] += 0.35f * (t.Aggression - 0.5f);
            d[Grit] += 0.3f * (t.Aggression - 0.5f) + 0.05f * t.Talent;
            d[Precision] += 0.08f * (skill - 1.5f) + 0.1f * t.Talent;
            d[Cohesion] += skill == 0 ? -0.12f : skill == 3 ? 0.08f : 0f;
            d[Greed] += 0.25f * (Clamp(t.LootGreed, 0f, 2.5f) - 1f);
            d[Revenge] += 0.2f * (t.Aggression - 0.5f);
            switch (t.Style)
            {
                case PlayStyle.Rusher: d[Aggression] += 0.12f; d[Pace] += 0.1f; d[Range] -= 0.12f; why.Add("rusher"); break;
                case PlayStyle.Hunter: d[Aggression] += 0.08f; d[ThirdParty] += 0.15f; d[Revenge] += 0.1f; why.Add("hunter"); break;
                case PlayStyle.Rat: d[Aggression] -= 0.12f; d[LeaveEarly] += 0.1f; d[Comms] -= 0.1f; why.Add("rat"); break;
                case PlayStyle.Camper: d[Aggression] -= 0.1f; d[Range] += 0.08f; why.Add("camper"); break;
                case PlayStyle.Sniper: d[Range] += 0.3f; d[Pace] -= 0.08f; d[Aggression] -= 0.08f; why.Add("sniper"); break;
                case PlayStyle.Looter: d[Greed] += 0.12f; d[ThirdParty] -= 0.05f; d[LeaveEarly] += 0.05f; why.Add("looter"); break;
                case PlayStyle.Explorer: d[Cohesion] -= 0.08f; d[Pace] += 0.05f; why.Add("explorer"); break;
                case PlayStyle.Quester: d[Cohesion] += 0.04f; d[ThirdParty] -= 0.08f; why.Add("quester"); break;
            }

            if (t.LongGun)
            {
                d[Range] += 0.15f;
                why.Add("long gun");
            }
            else if (t.ShortGun)
            {
                d[Range] -= 0.15f;
                why.Add("short gun");
            }

            if (squad.Doctrine.Kind == DoctrineKind.Carry)
            {
                if (t.Id == squad.Leader)
                {
                    d[Precision] += 0.2f;
                    d[Aggression] += 0.1f;
                    why.Add("carries the squad");
                }
                else
                {
                    d[Precision] -= 0.15f;
                    d[Cohesion] += 0.12f;
                    why.Add("is carried");
                }
            }

            System.Random own = new System.Random((t.Id ?? t.Name ?? "").GetHashCode() ^ squad.Id * 7919);
            for (int i = 0; i < Params; i++)
            {
                d[i] += ((float)own.NextDouble() - 0.5f) * 0.12f;
                m.Values[i] = Clamp01(squad.Values[i] + d[i]);
            }

            m.Lapse = Clamp((1f - m.Values[Precision]) * 0.35f, 0.01f, 0.4f);
            m.Delay = skill == 0 ? 1f + (float)own.NextDouble() * 2f : skill == 1 ? 0.4f + (float)own.NextDouble() * 1f : (float)own.NextDouble() * 0.5f;
            int[] top = Enumerable.Range(0, Params).OrderByDescending(i => Math.Abs(m.Values[i] - squad.Values[i])).Take(3).ToArray();
            m.Take = string.Join(", ", top.Select(i => $"{ParamNames[i]} {m.Values[i] - squad.Values[i]:+0.00;-0.00}")) +
                     (why.Count > 0 ? " (" + string.Join(", ", why) + ")" : "") + $", goes his own way {m.Lapse:P0}, follows a call in {m.Delay:0.0} s";
            return m;
        }

        public static string Numbers(float[] v) =>
            string.Join(" ", Enumerable.Range(0, Params).Select(i => $"{Short[i]} {v[i]:0.00}"));

        private static readonly string[] Short = { "aggr", "coh", "pace", "comms", "prec", "grit", "range", "greed", "3rd", "rev", "leave" };

        /// <summary>
        /// The rules, checked on made-up raids (tools/bindtest): no doctrine more than twice in a raid, no two squads with the same numbers and quirks,
        /// two squads of a doctrine apart, every member of a squad different from the squad and from each other.
        /// </summary>
        public static string SelfTest(int raids)
        {
            System.Random rng = new System.Random(20261031);
            int squads = 0, over = 0, identical = 0, close = 0, without = 0, withoutRoom = 0, members = 0, sameMembers = 0, maxSquads = 0;
            float closest = float.MaxValue, closestAny = float.MaxValue, takeSum = 0f, takeMin = float.MaxValue;
            Dictionary<DoctrineKind, int> dealtKinds = new Dictionary<DoctrineKind, int>();
            for (int r = 0; r < raids; r++)
            {
                Raid raid = new Raid();
                int count = 1 + rng.Next(26);
                maxSquads = Math.Max(maxSquads, count);
                for (int s = 0; s < count; s++)
                {
                    List<Traits> list = new List<Traits>();
                    int size = rng.NextDouble() < 0.3 ? 1 : 2 + rng.Next(4);
                    for (int i = 0; i < size; i++)
                    {
                        list.Add(new Traits
                        {
                            Id = $"r{r}s{s}m{i}",
                            Name = $"bot{i}",
                            Style = (PlayStyle)rng.Next(8),
                            Skill = (SkillTier)rng.Next(4),
                            Aggression = (float)rng.NextDouble(),
                            LootGreed = 0.4f + (float)rng.NextDouble() * 1.8f,
                            Talent = (float)rng.NextDouble() * 2f - 1f,
                            LongGun = rng.NextDouble() < 0.15,
                            ShortGun = rng.NextDouble() < 0.25,
                        });
                    }

                    Squad sq = Deal(raid, s + 1, list, rng);
                    squads++;
                    members += sq.Members.Count;
                    if (sq.Doctrine != null)
                    {
                        dealtKinds[sq.Doctrine.Kind] = dealtKinds.TryGetValue(sq.Doctrine.Kind, out int k) ? k + 1 : 1;
                        foreach (Member mm in sq.Members)
                        {
                            float take = Distance(mm.Values, sq.Values);
                            takeSum += take;
                            takeMin = Math.Min(takeMin, take);
                        }
                    }
                    else if (s < Doctrines.Length * PerRaid)
                    {
                        withoutRoom++;
                    }
                    for (int a = 0; a < sq.Members.Count; a++)
                    {
                        for (int b = a + 1; b < sq.Members.Count; b++)
                        {
                            if (Distance(sq.Members[a].Values, sq.Members[b].Values) < 0.01f)
                            {
                                sameMembers++;
                            }
                        }
                    }
                }

                without += raid.WithoutDoctrine;
                if (raid.MaxPerDoctrine > PerRaid)
                {
                    over++;
                }

                List<Squad> dealt = raid.Squads.Where(x => x.Doctrine != null).ToList();
                for (int a = 0; a < dealt.Count; a++)
                {
                    for (int b = a + 1; b < dealt.Count; b++)
                    {
                        closestAny = Math.Min(closestAny, Distance(dealt[a].Values, dealt[b].Values));
                    }
                }

                if (dealt.Select(x => x.Fingerprint).Distinct().Count() != dealt.Count)
                {
                    identical++;
                }

                foreach (IGrouping<DoctrineKind, Squad> g in dealt.GroupBy(x => x.Doctrine.Kind))
                {
                    List<Squad> two = g.ToList();
                    if (two.Count == 2)
                    {
                        float dist = Distance(two[0].Values, two[1].Values);
                        closest = Math.Min(closest, dist);
                        if (dist < 0.9f && SameQuirks(two[0].Quirks, two[1].Quirks))
                        {
                            close++;
                        }
                    }
                }
            }

            bool ok = over == 0 && identical == 0 && close == 0 && sameMembers == 0 && withoutRoom == 0;
            return $"DOCTRINES {(ok ? "OK" : "FAILED")}: {raids} raids (1-{maxSquads} PMC squads each), {squads} squads, {members} members; " +
                   $"raids with a doctrine more than twice {over}; raids with two identical squads {identical}; same-doctrine pairs alike (same quirks, numbers < 0.9 apart) {close}; " +
                   $"closest same-doctrine pair {closest:0.00} apart, closest pair of any doctrines {closestAny:0.00}; " +
                   $"squads left without a doctrine while places were free {withoutRoom}, past the {Doctrines.Length * PerRaid} places {without - withoutRoom}; " +
                   $"squadmates with identical numbers {sameMembers}; a member's own take differs from his squad by {takeSum / Math.Max(1, members - without):0.00} on average (least {takeMin:0.00}); " +
                   $"dealt: {string.Join(", ", dealtKinds.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value * 100f / Math.Max(1, squads - without):0}%"))}";
        }
    }

    /// <summary>
    /// The doctrines in the raid (F12 -> 8 "Squad doctrines"): a PMC squad gets one when its members have come in (2 s after the first, or the first
    /// time anything asks), later members get their own take on it at once. The squad is the spawn squad of SquadTies (QuestingBots splitting a
    /// squad keeps its doctrine). Logged: "LR Squad: ... play ..." with the squad's numbers and quirks, one line per member with his take on it.
    /// </summary>
    public static class SquadDoctrines
    {
        private class Pending
        {
            public readonly List<BotOwner> Bots = new List<BotOwner>();
            public float FirstAt;
            public DoctrineDeck.Squad Squad;
        }

        private static readonly Dictionary<int, Pending> _squads = new Dictionary<int, Pending>();
        private static readonly Dictionary<string, DoctrineDeck.Member> _members = new Dictionary<string, DoctrineDeck.Member>();
        private static DoctrineDeck.Raid _raid = new DoctrineDeck.Raid();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static void Clear()
        {
            _squads.Clear();
            _members.Clear();
            _raid = new DoctrineDeck.Raid();
            _next = 0f;
        }

        public static string Summary
        {
            get
            {
                List<DoctrineDeck.Squad> dealt = _raid.Squads.Where(s => s.Doctrine != null).ToList();
                if (_raid.Squads.Count == 0)
                {
                    return "squad doctrines: none dealt";
                }

                float closest = float.MaxValue;
                foreach (IGrouping<DoctrineKind, DoctrineDeck.Squad> g in dealt.GroupBy(s => s.Doctrine.Kind))
                {
                    List<DoctrineDeck.Squad> l = g.ToList();
                    if (l.Count == 2)
                    {
                        closest = Mathf.Min(closest, DoctrineDeck.Distance(l[0].Values, l[1].Values));
                    }
                }

                bool unique = dealt.Select(s => s.Fingerprint).Distinct().Count() == dealt.Count;
                return $"squad doctrines: {_raid.Squads.Count} PMC squads - " +
                       string.Join(", ", dealt.GroupBy(s => s.Doctrine.Title).Select(g => $"{g.Key} x{g.Count()}")) +
                       $"; at most twice each: {(_raid.MaxPerDoctrine <= DoctrineDeck.PerRaid ? "yes" : "NO")}; all squads different: {(unique ? "yes" : "NO")}" +
                       (closest < float.MaxValue ? $" (closest same-doctrine pair {closest:0.00} apart)" : "") +
                       (_raid.WithoutDoctrine > 0 ? $"; without a doctrine (all places taken) {_raid.WithoutDoctrine}" : "");
            }
        }

        /// <summary>At activation (after SquadTies): a PMC joins its squad's doctrine, or waits for it to be dealt.</summary>
        public static void Register(BotOwner bot)
        {
            if (!Settings.SquadDoctrines.Value || bot?.ProfileId == null || _members.ContainsKey(bot.ProfileId))
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind != BotKind.Pmc)
            {
                return;
            }

            int id = SquadTies.SquadOf(bot.ProfileId);
            if (!_squads.TryGetValue(id, out Pending squad))
            {
                _squads[id] = squad = new Pending { FirstAt = Time.time };
            }

            if (squad.Bots.Contains(bot))
            {
                return;
            }

            squad.Bots.Add(bot);
            if (squad.Squad != null)
            {
                Join(squad.Squad, bot, p);
            }
        }

        public static void Tick()
        {
            if (!Settings.SquadDoctrines.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            foreach (KeyValuePair<int, Pending> kv in _squads)
            {
                if (kv.Value.Squad == null && Time.time - kv.Value.FirstAt >= 2f)
                {
                    Seal(kv.Key, kv.Value);
                }
            }
        }

        /// <summary>The member's own take on his squad's doctrine (null: not a PMC, feature off, or no doctrine left).</summary>
        public static DoctrineDeck.Member Of(BotOwner bot)
        {
            if (!Settings.SquadDoctrines.Value || bot?.ProfileId == null)
            {
                return null;
            }

            if (_members.TryGetValue(bot.ProfileId, out DoctrineDeck.Member m))
            {
                return m.Squad.Doctrine != null ? m : null;
            }

            int id = SquadTies.SquadOf(bot.ProfileId);
            if (_squads.TryGetValue(id, out Pending squad) && squad.Squad == null && squad.Bots.Contains(bot))
            {
                Seal(id, squad);
                return _members.TryGetValue(bot.ProfileId, out m) && m.Squad.Doctrine != null ? m : null;
            }

            return null;
        }

        public static DoctrineDeck.Squad SquadOf(BotOwner bot) => Of(bot)?.Squad;

        private static DoctrineDeck.Traits TraitsOf(BotOwner bot, BotProfile p) => new DoctrineDeck.Traits
        {
            Id = bot.ProfileId,
            Name = p.Name,
            Style = p.Style,
            Skill = p.Skill,
            Aggression = p.Aggression,
            LootGreed = p.LootGreed,
            Talent = p.Talent,
            LongGun = p.WeaponClass == "sniperRifle" || p.WeaponClass == "marksmanRifle",
            ShortGun = p.WeaponClass == "shotgun" || p.WeaponClass == "smg" || p.WeaponClass == "pistol",
        };

        private static void Seal(int id, Pending pending)
        {
            List<BotOwner> bots = pending.Bots.Where(b => b?.Profile != null).ToList();
            List<DoctrineDeck.Traits> traits = new List<DoctrineDeck.Traits>();
            foreach (BotOwner b in bots)
            {
                BotProfile p = BotProfiles.Get(b);
                if (p != null)
                {
                    traits.Add(TraitsOf(b, p));
                }
            }

            if (traits.Count == 0)
            {
                return;
            }

            pending.Squad = DoctrineDeck.Deal(_raid, id, traits, _rng);
            string names = string.Join(", ", traits.Select(t => t.Name));
            if (pending.Squad.Doctrine == null)
            {
                Log.Info($"LR Squad: {names} - no doctrine left ({_raid.Squads.Count} squads, every doctrine already twice) - they play by their characters alone");
            }
            else
            {
                Log.Info($"LR Squad: {names} play {pending.Squad.Describe()}");
            }

            foreach (DoctrineDeck.Member m in pending.Squad.Members)
            {
                _members[m.Id] = m;
                Note(m, bots.FirstOrDefault(b => b.ProfileId == m.Id));
            }
        }

        private static void Join(DoctrineDeck.Squad squad, BotOwner bot, BotProfile p)
        {
            DoctrineDeck.Member m = DoctrineDeck.Mutate(squad, TraitsOf(bot, p));
            squad.Members.Add(m);
            _members[m.Id] = m;
            Note(m, bot);
        }

        private static void Note(DoctrineDeck.Member m, BotOwner bot)
        {
            if (m.Squad.Doctrine == null)
            {
                return;
            }

            Log.Info($"LR Squad: {m.Name}'s own take on \"{m.Squad.Doctrine.Title}\": {m.Take}");

            // When the squad leaves: a doctrine of quick runs goes earlier, a storm squad stays for more fights (the squad leaves on its leader's time)
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p != null)
            {
                p.ExtractAtRemaining = Mathf.Clamp(p.ExtractAtRemaining + (m[DoctrineDeck.LeaveEarly] - 0.5f) * 0.25f, 0.05f, 0.9f);
            }
        }
    }
}
