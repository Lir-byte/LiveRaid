using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    public enum SquadTactic
    {
        None,
        PushTogether,
        Hold,
        PinAndFlank,
        Pincer,
        SuppressAndRush,
        Bounding,
        PairPush,
        PushTheWounded,
        FallBackAndRegroup,
        EachHisOwn,
        WideFlank,
        HighGround,
        Bait,
        SpreadThenStorm,
        StormBuilding,
        HoldBuilding,
        RunDown,
        Scatter,
        Revenge,
        PeekBait,
        ThirdPartyTurn,
        SmokeScreen,
    }

    /// <summary>
    /// The squad's playbook in a fight (F12 -> 8 "Squad playbook"). Before, a squad drew one of four plans (push together, hold, one goes round,
    /// each his own), so squads fought alike. Here the squad's leader calls a tactic from a deck of ten, weighed by the squad's doctrine
    /// (<see cref="SquadDoctrines"/>), the fight (numbers, range, health, a wounded enemy) and what the squad already did this raid (a tactic
    /// used before is less likely again - a squad tries different things). The tactic deals the fight roles of <see cref="FightRoles"/>:
    ///   push together      - everyone goes in, long guns and the badly hurt cover;
    ///   hold               - fight from cover, pushes and flanks rarer;
    ///   pin and flank      - one goes round, the rest keep the enemy busy;
    ///   pincer             - two go round, one each side, the rest pin;
    ///   suppress and rush  - the boldest runs in while the others shoot from cover;
    ///   bounding           - two teams take turns: one moves up, the other covers, every 6-9 s (a drilled squad keeps time, randoms do not);
    ///   pair push          - the two boldest go in together from where they are;
    ///   push the wounded   - a squadmate hit the enemy: everyone presses him for a few seconds before he patches up;
    ///   fall back, regroup - the whole squad breaks off 45-90 m and comes back at the enemy, likely round him;
    ///   each his own       - no call, every member by his own character;
    ///   wide flank         - one goes far round, to the enemy's side and back (110-140 degrees);
    ///   high ground        - one goes round by a spot above the enemy (a floor, a roof, a hill);
    ///   bait               - one shows himself and draws the enemy's eye while another goes round, the rest pin;
    ///   spread, then storm - everyone takes his own spot for 10-20 s, then all go in at once;
    ///   storm the building - the enemy is inside: two bounds up to it, then all go in;
    ///   hold the building  - the squad is inside and the enemy outside: hold the ways in 10-20 s, then go at him or out by another way;
    ///   run him down       - the enemy went out of sight while the squad has the edge: the two boldest chase him;
    ///   scatter            - the fight is lost: everyone breaks off his own way (and the squad soon leaves the raid);
    ///   revenge            - a squadmate fell and the squad is a vengeful one: all go for the killer (called on the death, SquadComms).
    /// Members play it their way: a member may go his own way (randoms often, a clan hardly ever) and a novice catches on a second or two late.
    /// Morale still rules over the call: a bot losing its nerve falls back whatever it was told.
    /// </summary>
    public static class SquadPlaybook
    {
        public class Plan
        {
            public int Serial;
            public int Squad;
            public SquadTactic Tactic;
            public float StartedAt;
            public float Until;
            public int RatioBand;
            public string Caller;
            public float Phase;

            // Hold the building / smoke screen: after holding, go at the enemy (true) or out by another way (false)
            public bool Variant;

            // Turn to a third party: where the new shots came from
            public Vector3 Target;
            public string Why;
            public readonly List<string> Order = new List<string>();
            public readonly List<string> Picked = new List<string>();
            public readonly HashSet<string> Lapsed = new HashSet<string>();
            public readonly HashSet<string> Weighed = new HashSet<string>();
            public readonly HashSet<string> FellBack = new HashSet<string>();
        }

        private static readonly Dictionary<int, Plan> _plans = new Dictionary<int, Plan>();
        private static readonly Dictionary<int, Dictionary<SquadTactic, int>> _used = new Dictionary<int, Dictionary<SquadTactic, int>>();
        private static readonly Dictionary<int, float> _regrouped = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _woundedCheck = new Dictionary<int, float>();
        private static readonly Dictionary<SquadTactic, int> _calls = new Dictionary<SquadTactic, int>();
        private static readonly System.Random _rng = new System.Random();
        private static int _serial;

        public static int OwnWays { get; private set; }

        public static readonly Dictionary<SquadTactic, string> Titles = new Dictionary<SquadTactic, string>
        {
            [SquadTactic.PushTogether] = "общий штурм",
            [SquadTactic.Hold] = "держать позиции",
            [SquadTactic.PinAndFlank] = "огневая база и обход",
            [SquadTactic.Pincer] = "клещи",
            [SquadTactic.SuppressAndRush] = "подавление и рывок",
            [SquadTactic.Bounding] = "перебежки с прикрытием",
            [SquadTactic.PairPush] = "двойка вперёд",
            [SquadTactic.PushTheWounded] = "дожать раненого",
            [SquadTactic.FallBackAndRegroup] = "откат и перегруппировка",
            [SquadTactic.EachHisOwn] = "каждый сам",
            [SquadTactic.WideFlank] = "широкий обход",
            [SquadTactic.HighGround] = "обход по высоте",
            [SquadTactic.Bait] = "приманка",
            [SquadTactic.SpreadThenStorm] = "растяжка и штурм",
            [SquadTactic.StormBuilding] = "штурм здания",
            [SquadTactic.HoldBuilding] = "оборона здания",
            [SquadTactic.RunDown] = "преследование",
            [SquadTactic.Scatter] = "рассыпаться и уходить",
            [SquadTactic.Revenge] = "месть за своего",
            [SquadTactic.PeekBait] = "выглядывание-провокация",
            [SquadTactic.ThirdPartyTurn] = "разворот на третью сторону",
            [SquadTactic.SmokeScreen] = "дымовая завеса",
        };

        /// <summary>Tactics whose roles change in phases (bounds, hold-then-go): the role is drawn again at each phase.</summary>
        private static bool Phased(SquadTactic t) =>
            t == SquadTactic.Bounding || t == SquadTactic.SpreadThenStorm || t == SquadTactic.StormBuilding || t == SquadTactic.HoldBuilding || t == SquadTactic.SmokeScreen;

        private static int PhaseOf(Plan plan, float now)
        {
            int phase = Mathf.FloorToInt((now - plan.StartedAt) / plan.Phase);
            return plan.Tactic == SquadTactic.Bounding ? phase : plan.Tactic == SquadTactic.StormBuilding ? Mathf.Min(phase, 2) : Mathf.Min(phase, 1);
        }

        /// <summary>How the doctrines like the later tactics of the deck (the first ten are in the doctrine itself).</summary>
        private static float DoctrineExtra(DoctrineKind kind, SquadTactic t)
        {
            switch (t)
            {
                case SquadTactic.WideFlank:
                    return kind == DoctrineKind.PvpHunters || kind == DoctrineKind.Clan ? 1.8f : kind == DoctrineKind.Rats ? 1.4f : kind == DoctrineKind.Randoms ? 0.3f : 1f;
                case SquadTactic.HighGround:
                    return kind == DoctrineKind.Marksmen ? 2.2f : kind == DoctrineKind.Clan || kind == DoctrineKind.PvpHunters ? 1.4f : kind == DoctrineKind.Chads ? 0.6f : 1f;
                case SquadTactic.Bait:
                    return kind == DoctrineKind.PvpHunters ? 2f : kind == DoctrineKind.Rats ? 1.6f : kind == DoctrineKind.Clan ? 1.3f : kind == DoctrineKind.Randoms ? 0.3f : 0.8f;
                case SquadTactic.SpreadThenStorm:
                    return kind == DoctrineKind.Clan || kind == DoctrineKind.Tanks ? 2f : kind == DoctrineKind.BossHunters ? 1.5f : kind == DoctrineKind.Randoms ? 0.2f : 1f;
                case SquadTactic.StormBuilding:
                    return kind == DoctrineKind.Chads || kind == DoctrineKind.Tanks || kind == DoctrineKind.BossHunters ? 2f : kind == DoctrineKind.Looters || kind == DoctrineKind.Rats ? 0.4f : 1f;
                case SquadTactic.HoldBuilding:
                    return kind == DoctrineKind.Marksmen || kind == DoctrineKind.Looters || kind == DoctrineKind.Questers ? 1.6f : kind == DoctrineKind.Chads ? 0.5f : 1f;
                case SquadTactic.RunDown:
                    return kind == DoctrineKind.Chads ? 2.2f : kind == DoctrineKind.PvpHunters || kind == DoctrineKind.Speedrunners ? 1.5f : kind == DoctrineKind.Rats || kind == DoctrineKind.Looters ? 0.4f : 1f;
                case SquadTactic.Scatter:
                    return kind == DoctrineKind.Randoms || kind == DoctrineKind.Speedrunners ? 2f : kind == DoctrineKind.Rats ? 1.6f : kind == DoctrineKind.Clan || kind == DoctrineKind.Tanks ? 0.3f : 1f;
                case SquadTactic.PeekBait:
                    return kind == DoctrineKind.PvpHunters || kind == DoctrineKind.Clan ? 1.8f : kind == DoctrineKind.Rats || kind == DoctrineKind.Marksmen ? 1.4f : kind == DoctrineKind.Randoms ? 0.3f : 1f;
                case SquadTactic.SmokeScreen:
                    return kind == DoctrineKind.Clan || kind == DoctrineKind.Tanks || kind == DoctrineKind.BossHunters ? 1.8f : kind == DoctrineKind.Looters ? 1.3f : kind == DoctrineKind.Randoms ? 0.4f : 1f;
                default:
                    return 1f;
            }
        }

        public static void Clear()
        {
            _plans.Clear();
            _used.Clear();
            _regrouped.Clear();
            _woundedCheck.Clear();
            _calls.Clear();
            _serial = 0;
            OwnWays = 0;
        }

        public static string Summary
        {
            get
            {
                int calls = _calls.Values.Sum();
                if (calls == 0)
                {
                    return "squad tactics: none called";
                }

                string bySquad = string.Join(", ", _used.Where(kv => kv.Value.Count > 0).OrderByDescending(kv => kv.Value.Count)
                    .Select(kv => $"#{kv.Key} {kv.Value.Count} different"));
                return $"squad tactics: {calls} calls, {_calls.Count} different ({string.Join(", ", _calls.OrderByDescending(kv => kv.Value).Select(kv => $"{Titles[kv.Key]} {kv.Value}"))})" +
                       $"; by squad: {bySquad}; members who went their own way {OwnWays}";
            }
        }

        public static string TitleOf(BotOwner bot) =>
            bot != null && _plans.TryGetValue(SquadTies.SquadOf(bot.ProfileId), out Plan plan) && Time.time < plan.Until ? Titles[plan.Tactic] : null;

        /// <summary>
        /// Keeps the squad's call fresh (a new one when the last ran out, the numbers swung or a squadmate just hit the enemy) and returns a stamp
        /// that changes whenever the bot's role may: a new call, the next bound, the end of a novice's delay. 0 - no squad call for this bot.
        /// </summary>
        public static int Refresh(BotOwner bot, int ratio, Enemy closest, float health)
        {
            // PMC squads only: scavs keep the old plans until their own package (patrols of their area)
            if (!Settings.SquadPlaybook.Value || bot == null || BotProfiles.Get(bot)?.Kind != BotKind.Pmc)
            {
                return 0;
            }

            List<BotOwner> mates = AliveMates(bot);
            if (mates.Count < 2)
            {
                return 0;
            }

            int squad = SquadTies.SquadOf(bot.ProfileId);
            float now = Time.time;
            bool have = _plans.TryGetValue(squad, out Plan plan) && now < plan.Until;
            bool wounded = false;
            if (have && plan.Tactic != SquadTactic.PushTheWounded && plan.Tactic != SquadTactic.FallBackAndRegroup && plan.Tactic != SquadTactic.Scatter
                && plan.Tactic != SquadTactic.Revenge && now - plan.StartedAt > 5f
                && (!_woundedCheck.TryGetValue(squad, out float next) || now >= next))
            {
                _woundedCheck[squad] = now + 3f;
                wounded = Wounded(closest, mates);
            }

            // The numbers are weighed by the member who made the call (each member counts allies and enemies his own way), and not in the first seconds
            bool swung = have && plan.Caller == bot.ProfileId && plan.RatioBand != ratio && plan.Tactic != SquadTactic.FallBackAndRegroup
                         && plan.Tactic != SquadTactic.Scatter && plan.Tactic != SquadTactic.Revenge && now - plan.StartedAt > 8f;
            // Shot from a new side: half the squad turns there (a card of its own, called on the event)
            if (have && plan.Tactic != SquadTactic.ThirdPartyTurn && Settings.ThirdPartyTurn.Value && mates.Count >= 2 && now - plan.StartedAt > 2f
                && FireTricks.ThirdParty(bot, closest, out Vector3 from))
            {
                plan = Start(bot, squad, mates, SquadTactic.ThirdPartyTurn, ratio, $"{bot.Profile?.Nickname} hit from a new side",
                    SquadDoctrines.SquadOf(bot)?.Values[DoctrineDeck.Precision] ?? 0.5f, SquadDoctrines.SquadOf(bot), from);
                _calls[SquadTactic.ThirdPartyTurn] = _calls.TryGetValue(SquadTactic.ThirdPartyTurn, out int tp) ? tp + 1 : 1;
            }
            else if (!have || swung || wounded)
            {
                plan = Call(bot, squad, mates, ratio, closest, health);
            }

            if (plan == null)
            {
                return 0;
            }

            int stamp = plan.Serial * 1000;
            if (Phased(plan.Tactic))
            {
                stamp += PhaseOf(plan, now) % 400;
            }

            DoctrineDeck.Member me = SquadDoctrines.Of(bot);
            if (me != null && now < plan.StartedAt + me.Delay)
            {
                stamp += 500;
            }

            return stamp;
        }

        /// <summary>How long the bot's role from the call holds (the call's end, the next bound, a novice's delay).</summary>
        public static float RoleUntil(BotOwner bot)
        {
            if (bot == null || !_plans.TryGetValue(SquadTies.SquadOf(bot.ProfileId), out Plan plan) || Time.time >= plan.Until)
            {
                return float.MaxValue;
            }

            float until = plan.Until;
            float now = Time.time;
            if (Phased(plan.Tactic))
            {
                int phase = PhaseOf(plan, now);
                int last = plan.Tactic == SquadTactic.Bounding ? int.MaxValue : plan.Tactic == SquadTactic.StormBuilding ? 2 : 1;
                if (phase < last)
                {
                    until = Mathf.Min(until, plan.StartedAt + (phase + 1) * plan.Phase);
                }
            }

            DoctrineDeck.Member me = SquadDoctrines.Of(bot);
            if (me != null && now < plan.StartedAt + me.Delay)
            {
                until = Mathf.Min(until, plan.StartedAt + me.Delay);
            }

            return until;
        }

        /// <summary>
        /// The bot's part in the squad's call: a fixed role, or null with the weights of its own draw changed (hold: pushes rarer) - or untouched
        /// when there is no call or the bot goes its own way.
        /// </summary>
        public static FightRole? RoleFor(BotOwner bot, BotProfile p, Enemy closest, bool holdLong, float health, ref float assault, ref float flank, out string planName)
        {
            planName = null;
            if (!Settings.SquadPlaybook.Value || bot == null || p.Kind != BotKind.Pmc || !_plans.TryGetValue(SquadTies.SquadOf(bot.ProfileId), out Plan plan)
                || Time.time >= plan.Until)
            {
                return null;
            }

            planName = Titles[plan.Tactic];
            DoctrineDeck.Member me = SquadDoctrines.Of(bot);
            if (!plan.Weighed.Contains(bot.ProfileId))
            {
                plan.Weighed.Add(bot.ProfileId);
                float lapse = me?.Lapse ?? 0.15f;
                if (plan.Tactic != SquadTactic.EachHisOwn && plan.Tactic != SquadTactic.FallBackAndRegroup && plan.Tactic != SquadTactic.Scatter
                    && plan.Tactic != SquadTactic.Revenge && plan.Tactic != SquadTactic.ThirdPartyTurn && _rng.NextDouble() < lapse)
                {
                    plan.Lapsed.Add(bot.ProfileId);
                    OwnWays++;
                    Log.Info($"LR Squad: {p.Name} ({p.Style}, {p.Skill}) goes his own way instead of \"{planName}\"");
                }
            }

            if (plan.Lapsed.Contains(bot.ProfileId))
            {
                planName += " (ignored)";
                return null;
            }

            bool cannotGoIn = holdLong || health < 0.4f || p.Style == PlayStyle.Sniper;
            bool late = me != null && Time.time < plan.StartedAt + me.Delay;
            switch (plan.Tactic)
            {
                case SquadTactic.PushTogether:
                    return cannotGoIn || late ? FightRole.Hold : FightRole.Assault;

                case SquadTactic.Hold:
                    assault *= 0.35f;
                    flank *= 0.5f;
                    return null;

                case SquadTactic.PinAndFlank:
                case SquadTactic.Pincer:
                    if (plan.Picked.Contains(bot.ProfileId))
                    {
                        return late ? FightRole.Hold : FightRole.Flank;
                    }

                    assault *= 0.5f;
                    flank = 0f;
                    return null;

                case SquadTactic.SuppressAndRush:
                case SquadTactic.PairPush:
                    if (plan.Picked.Contains(bot.ProfileId) && !cannotGoIn)
                    {
                        return late ? FightRole.Hold : FightRole.Assault;
                    }

                    return FightRole.Hold;

                case SquadTactic.Bounding:
                {
                    int index = plan.Order.IndexOf(bot.ProfileId);
                    int phase = Mathf.FloorToInt((Time.time - plan.StartedAt) / plan.Phase);
                    bool moving = index >= 0 && index % 2 == phase % 2 && !cannotGoIn && !late;
                    if (moving)
                    {
                        CombatSense.AllowPush(bot);
                        return FightRole.Assault;
                    }

                    return FightRole.Hold;
                }

                case SquadTactic.PushTheWounded:
                    if (!cannotGoIn && !late)
                    {
                        CombatSense.AllowPush(bot);
                        return FightRole.Assault;
                    }

                    return FightRole.Hold;

                case SquadTactic.FallBackAndRegroup:
                    if (!plan.FellBack.Contains(bot.ProfileId))
                    {
                        plan.FellBack.Add(bot.ProfileId);
                        CombatSense.FallBack(bot, closest, plan.Until - Time.time + 10f, "falls back with its squad to regroup");
                    }

                    return FightRole.Hold;

                case SquadTactic.WideFlank:
                case SquadTactic.HighGround:
                    if (plan.Picked.Contains(bot.ProfileId))
                    {
                        Maneuvers.SetStyle(bot, plan.Tactic == SquadTactic.WideFlank ? Maneuvers.FlankStyle.Wide : Maneuvers.FlankStyle.High);
                        return late ? FightRole.Hold : FightRole.Flank;
                    }

                    assault *= 0.5f;
                    flank = 0f;
                    return null;

                case SquadTactic.Bait:
                {
                    int at = plan.Picked.IndexOf(bot.ProfileId);
                    if (at == 0 && !plan.FellBack.Contains(bot.ProfileId))
                    {
                        // The bait shows itself for a few seconds (SAIN's search at the enemy), then fights from cover
                        plan.FellBack.Add(bot.ProfileId);
                        CombatSense.Nudge(bot, CombatStance.Probe, "shows itself to draw the enemy's eye (bait)", 6f + (float)_rng.NextDouble() * 3f);
                        return FightRole.Hold;
                    }

                    if (at == 1)
                    {
                        return late ? FightRole.Hold : FightRole.Flank;
                    }

                    return FightRole.Hold;
                }

                case SquadTactic.SpreadThenStorm:
                    if (PhaseOf(plan, Time.time) >= 1 && !cannotGoIn)
                    {
                        CombatSense.AllowPush(bot);
                        return FightRole.Assault;
                    }

                    return FightRole.Hold;

                case SquadTactic.StormBuilding:
                {
                    int phase = PhaseOf(plan, Time.time);
                    int index = plan.Order.IndexOf(bot.ProfileId);
                    bool moving = !cannotGoIn && !late && (phase >= 2 || (index >= 0 && index % 2 == phase % 2));
                    if (moving)
                    {
                        CombatSense.AllowPush(bot);
                        return FightRole.Assault;
                    }

                    return FightRole.Hold;
                }

                case SquadTactic.HoldBuilding:
                    if (PhaseOf(plan, Time.time) < 1)
                    {
                        assault = 0f;
                        flank = 0f;
                        return FightRole.Hold;
                    }

                    if (plan.Variant)
                    {
                        if (!cannotGoIn)
                        {
                            CombatSense.AllowPush(bot);
                            return FightRole.Assault;
                        }

                        return FightRole.Hold;
                    }

                    if (!plan.FellBack.Contains(bot.ProfileId))
                    {
                        plan.FellBack.Add(bot.ProfileId);
                        CombatSense.FallBack(bot, closest, plan.Until - Time.time + 10f, "leaves the building by another way with its squad");
                    }

                    return FightRole.Hold;

                case SquadTactic.RunDown:
                    if (plan.Picked.Contains(bot.ProfileId) && !cannotGoIn)
                    {
                        CombatSense.AllowPush(bot);
                        return late ? FightRole.Hold : FightRole.Assault;
                    }

                    return FightRole.Hold;

                case SquadTactic.Scatter:
                    if (!plan.FellBack.Contains(bot.ProfileId))
                    {
                        plan.FellBack.Add(bot.ProfileId);
                        CombatSense.BreakOff(bot, closest, "scattering with its beaten squad");
                    }

                    return FightRole.Hold;

                case SquadTactic.Revenge:
                    if (!cannotGoIn)
                    {
                        CombatSense.AllowPush(bot);
                        return FightRole.Assault;
                    }

                    return FightRole.Hold;

                case SquadTactic.PeekBait:
                    if (plan.Picked.Contains(bot.ProfileId) && !plan.FellBack.Contains(bot.ProfileId) && !late)
                    {
                        // The one who draws the fire (FireTricks), the rest watch where the shot comes from
                        plan.FellBack.Add(bot.ProfileId);
                        FireTricks.StartPeek(bot);
                    }

                    assault = 0f;
                    flank = 0f;
                    return FightRole.Hold;

                case SquadTactic.ThirdPartyTurn:
                {
                    int index = plan.Order.IndexOf(bot.ProfileId);
                    if (index >= 0 && index % 2 == 1 && !plan.FellBack.Contains(bot.ProfileId))
                    {
                        plan.FellBack.Add(bot.ProfileId);
                        FireTricks.Turn(bot, plan.Target, 6f + (float)_rng.NextDouble() * 3f);
                    }

                    return FightRole.Hold;
                }

                case SquadTactic.SmokeScreen:
                {
                    if (plan.Picked.Contains(bot.ProfileId) && !plan.FellBack.Contains(bot.ProfileId) && closest != null)
                    {
                        plan.FellBack.Add(bot.ProfileId);
                        FireTricks.Smoke(bot, closest.LastKnownPosition ?? closest.EnemyPosition, "the squad's smoke screen");
                    }

                    if (PhaseOf(plan, Time.time) < 1)
                    {
                        return FightRole.Hold;
                    }

                    if (plan.Variant)
                    {
                        if (!cannotGoIn)
                        {
                            CombatSense.AllowPush(bot);
                            return FightRole.Assault;
                        }

                        return FightRole.Hold;
                    }

                    if (!plan.Weighed.Contains("fallback|" + bot.ProfileId))
                    {
                        plan.Weighed.Add("fallback|" + bot.ProfileId);
                        CombatSense.FallBack(bot, closest, plan.Until - Time.time + 10f, "falls back under the smoke with its squad");
                    }

                    return FightRole.Hold;
                }

                default:
                    return null;
            }
        }

        private static Plan Call(BotOwner bot, int squad, List<BotOwner> mates, int ratio, Enemy closest, float health)
        {
            float now = Time.time;
            DoctrineDeck.Squad doctrine = SquadDoctrines.SquadOf(bot);
            float dist = closest?.RealDistance ?? 60f;
            float avgHealth = mates.Average(m => m.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f);
            int size = mates.Count;
            bool wounded = Wounded(closest, mates);
            bool regrouped = _regrouped.TryGetValue(squad, out float until) && now < until;
            float precision = doctrine?.Values[DoctrineDeck.Precision] ?? 0.5f;
            float range = doctrine?.Values[DoctrineDeck.Range] ?? 0.5f;
            bool enemyInside = closest != null && Indoors((closest.LastKnownPosition ?? closest.EnemyPosition));
            bool weInside = Indoors(bot.Position);
            bool slipped = closest != null && !closest.IsVisible && closest.TimeSinceSeen > 3f && closest.TimeSinceSeen < 15f && dist < 90f;
            int lost = Mathf.Max(0, SquadTies.MatesOf(bot.ProfileId).Count() - size);
            if (!_used.TryGetValue(squad, out Dictionary<SquadTactic, int> used))
            {
                _used[squad] = used = new Dictionary<SquadTactic, int>();
            }

            Dictionary<SquadTactic, float> w = new Dictionary<SquadTactic, float>();
            foreach (SquadTactic t in Titles.Keys)
            {
                float v = doctrine?.Doctrine.Tactics.TryGetValue(t, out float d) == true ? d : doctrine?.Doctrine != null ? DoctrineExtra(doctrine.Doctrine.Kind, t) : 1f;
                switch (t)
                {
                    case SquadTactic.PushTogether:
                        v *= (ratio >= 2 ? 2.2f : ratio == 1 ? 1.5f : ratio == 0 ? 0.8f : 0.25f) * (dist < 90f ? 1f : 0.4f);
                        break;
                    case SquadTactic.Hold:
                        v *= (ratio < 0 ? 2f : 1f) * (0.6f + range);
                        break;
                    case SquadTactic.PinAndFlank:
                        v *= dist >= 18f && dist <= 120f ? (ratio >= 0 ? 1f : 0.6f) : 0f;
                        break;
                    case SquadTactic.Pincer:
                        v *= size >= 3 && ratio >= 0 && dist >= 25f && dist <= 120f ? 1f : 0f;
                        break;
                    case SquadTactic.SuppressAndRush:
                        v *= dist >= 12f && dist <= 90f ? 1f : 0.2f;
                        break;
                    case SquadTactic.Bounding:
                        v *= dist >= 30f && dist <= 160f ? 1f : 0.3f;
                        break;
                    case SquadTactic.PairPush:
                        v *= size >= 3 && dist < 100f ? (ratio >= 0 ? 1f : 0.4f) : 0f;
                        break;
                    case SquadTactic.PushTheWounded:
                        v *= wounded ? 3f : 0f;
                        break;
                    case SquadTactic.FallBackAndRegroup:
                        v *= ratio < 0 || avgHealth < 0.55f ? 2.5f : 0.15f;
                        break;
                    case SquadTactic.EachHisOwn:
                        v *= 0.4f + 1.6f * (1f - precision);
                        break;
                    case SquadTactic.WideFlank:
                        v *= ratio >= 0 && dist >= 30f && dist <= 140f ? 0.8f * (0.5f + precision) : 0f;
                        break;
                    case SquadTactic.HighGround:
                        v *= dist >= 20f && dist <= 120f ? 0.6f * (0.5f + range) : 0f;
                        break;
                    case SquadTactic.Bait:
                        v *= size >= 3 && ratio >= 0 && dist >= 20f && dist <= 100f ? 0.8f * (0.4f + precision) : 0f;
                        break;
                    case SquadTactic.SpreadThenStorm:
                        v *= size >= 3 && ratio >= 1 && dist >= 25f && dist <= 100f ? 1f : 0f;
                        break;
                    case SquadTactic.StormBuilding:
                        v *= enemyInside && !weInside && ratio >= 0 && dist < 90f ? 2f : 0f;
                        break;
                    case SquadTactic.HoldBuilding:
                        v *= weInside && !enemyInside && dist < 120f ? 2f : 0f;
                        break;
                    case SquadTactic.RunDown:
                        v *= slipped && ratio >= 1 ? 1.5f : 0f;
                        break;
                    case SquadTactic.Scatter:
                        v *= ratio <= -1 && (avgHealth < 0.5f || lost > 0) ? 2f : 0f;
                        break;
                    case SquadTactic.Revenge:
                    case SquadTactic.ThirdPartyTurn:
                        v = 0f;
                        break;
                    case SquadTactic.PeekBait:
                        v *= Settings.PeekBait.Value && closest != null && !closest.IsVisible && dist >= 12f && dist <= 80f ? 0.9f : 0f;
                        break;
                    case SquadTactic.SmokeScreen:
                        v *= Settings.SmokeScreens.Value && dist >= 15f && dist <= 120f && mates.Any(FireTricks.HasSmoke) ? (ratio < 0 || avgHealth < 0.6f ? 1.6f : 0.8f) : 0f;
                        break;
                }

                if (regrouped)
                {
                    v *= t == SquadTactic.PinAndFlank || t == SquadTactic.Pincer ? 2.5f : t == SquadTactic.FallBackAndRegroup ? 0.2f : 1f;
                }

                // A squad tries different things: each use of a tactic in this raid makes it less likely, a new one a little more
                v *= used.TryGetValue(t, out int n) ? Mathf.Pow(0.45f, n) : 1.25f;
                w[t] = Mathf.Max(0f, v);
            }

            SquadTactic tactic = Pick(w);
            if (tactic == SquadTactic.None)
            {
                return null;
            }

            used[tactic] = used.TryGetValue(tactic, out int u) ? u + 1 : 1;
            _calls[tactic] = _calls.TryGetValue(tactic, out int c) ? c + 1 : 1;
            return Start(bot, squad, mates, tactic, ratio, $"{size} of the squad, {dist:0} m, hp {avgHealth:P0}{(wounded ? ", the enemy is hit" : "")}" +
                                                         $"{(regrouped ? ", after regrouping" : "")}{(enemyInside ? ", he is inside" : weInside ? ", we are inside" : "")}" +
                                                         $"{(lost > 0 ? $", {lost} lost" : "")}", precision, doctrine);
        }

        /// <summary>
        /// A call on an event (SquadComms): a squadmate fell - a vengeful squad goes for the killer, a shaken one falls back. Replaces the current call.
        /// </summary>
        public static bool Force(BotOwner member, SquadTactic tactic, string why)
        {
            if (!Settings.SquadPlaybook.Value || member == null || member.IsDead)
            {
                return false;
            }

            List<BotOwner> mates = AliveMates(member);
            if (mates.Count == 0)
            {
                return false;
            }

            int squad = SquadTies.SquadOf(member.ProfileId);
            if (!_used.TryGetValue(squad, out Dictionary<SquadTactic, int> used))
            {
                _used[squad] = used = new Dictionary<SquadTactic, int>();
            }

            used[tactic] = used.TryGetValue(tactic, out int u) ? u + 1 : 1;
            _calls[tactic] = _calls.TryGetValue(tactic, out int c) ? c + 1 : 1;
            DoctrineDeck.Squad doctrine = SquadDoctrines.SquadOf(member);
            return Start(member, squad, mates, tactic, 0, why, doctrine?.Values[DoctrineDeck.Precision] ?? 0.5f, doctrine) != null;
        }

        private static Plan Start(BotOwner bot, int squad, List<BotOwner> mates, SquadTactic tactic, int ratio, string why, float precision, DoctrineDeck.Squad doctrine,
            Vector3 target = default)
        {
            float now = Time.time;
            float length = tactic == SquadTactic.PushTheWounded || tactic == SquadTactic.RunDown ? 12f + (float)_rng.NextDouble() * 6f
                : tactic == SquadTactic.Revenge ? 20f + (float)_rng.NextDouble() * 10f
                : tactic == SquadTactic.FallBackAndRegroup || tactic == SquadTactic.Scatter ? 30f + (float)_rng.NextDouble() * 10f
                : 30f + (float)_rng.NextDouble() * 20f;
            Plan plan = new Plan
            {
                Serial = ++_serial,
                Squad = squad,
                Tactic = tactic,
                StartedAt = now,
                Until = now + length,
                RatioBand = ratio,
                Caller = bot.ProfileId,
                Target = target,
                Phase = tactic == SquadTactic.SmokeScreen ? 3f + (float)_rng.NextDouble() * 1.5f
                    : tactic == SquadTactic.SpreadThenStorm || tactic == SquadTactic.HoldBuilding
                    ? 10f + (float)_rng.NextDouble() * 10f
                    : 6f + 3f * (1f - precision) + ((float)_rng.NextDouble() - 0.5f) * 2f * (1f - precision),
                Variant = (doctrine?.Values[DoctrineDeck.Aggression] ?? 0.5f) + ((float)_rng.NextDouble() - 0.5f) * 0.4f > 0.55f,
                Why = why,
            };

            // Boldest first: who rushes, who goes round, who moves in the first bound
            List<(BotOwner b, BotProfile p)> ranked = mates.Select(m => (m, BotProfiles.Get(m))).Where(x => x.Item2 != null)
                .OrderByDescending(x => Boldness(x.m, x.Item2)).ToList();
            plan.Order.AddRange(ranked.Select(x => x.b.ProfileId));
            switch (tactic)
            {
                case SquadTactic.PinAndFlank:
                case SquadTactic.Pincer:
                    plan.Picked.AddRange(ranked.Where(x => CanFlank(x.b, x.Item2)).Take(tactic == SquadTactic.Pincer ? 2 : 1).Select(x => x.b.ProfileId));
                    break;
                case SquadTactic.SuppressAndRush:
                    plan.Picked.AddRange(ranked.Where(x => !CombatSense.IsLongGun(x.Item2)).Take(1).Select(x => x.b.ProfileId));
                    break;
                case SquadTactic.PairPush:
                    plan.Picked.AddRange(ranked.Where(x => !CombatSense.IsLongGun(x.Item2)).Take(2).Select(x => x.b.ProfileId));
                    break;
                case SquadTactic.FallBackAndRegroup:
                    _regrouped[squad] = plan.Until + 60f;
                    break;
                case SquadTactic.WideFlank:
                case SquadTactic.HighGround:
                    plan.Picked.AddRange(ranked.Where(x => CanFlank(x.b, x.Item2)).Take(1).Select(x => x.b.ProfileId));
                    break;
                case SquadTactic.Bait:
                    // The bait: the boldest who is not the best flanker; the flanker: the best of the others
                    BotOwner flanker = ranked.Where(x => CanFlank(x.b, x.Item2)).Select(x => x.b).FirstOrDefault();
                    BotOwner bait = ranked.Select(x => x.b).FirstOrDefault(b => b != flanker);
                    if (bait != null)
                    {
                        plan.Picked.Add(bait.ProfileId);
                    }

                    if (flanker != null)
                    {
                        plan.Picked.Add(flanker.ProfileId);
                    }

                    break;
                case SquadTactic.RunDown:
                    plan.Picked.AddRange(ranked.Where(x => !CombatSense.IsLongGun(x.Item2)).Take(2).Select(x => x.b.ProfileId));
                    break;
                case SquadTactic.PeekBait:
                    // Not the boldest - the one who can take a hit and hide again: a mid one of the squad
                    plan.Picked.Add(ranked[ranked.Count / 2].b.ProfileId);
                    break;
                case SquadTactic.SmokeScreen:
                    plan.Picked.AddRange(ranked.Where(x => FireTricks.HasSmoke(x.b)).Take(1).Select(x => x.b.ProfileId));
                    break;
            }

            _plans[squad] = plan;
            if (tactic == SquadTactic.PushTogether || tactic == SquadTactic.StormBuilding || tactic == SquadTactic.SpreadThenStorm || tactic == SquadTactic.Bait
                || tactic == SquadTactic.Revenge)
            {
                // Going in: a grenade first, whatever the member's habit (GrenadeHabit), and a stim for the bold with something to spend it on
                GrenadeHabit.Prime(mates);
                StimBeforeStorm(mates);
            }

            BotOwner leader = mates.FirstOrDefault(m => m.ProfileId == doctrine?.Leader) ?? TacticalTasks.SquadLeaderOf(bot) ?? bot;
            string picked = plan.Picked.Count > 0
                ? " - " + string.Join(", ", plan.Picked.Select(id => BotProfiles.TryGet(id, out BotProfile bp) ? bp.Name : id)) +
                  (tactic == SquadTactic.PinAndFlank || tactic == SquadTactic.Pincer || tactic == SquadTactic.WideFlank || tactic == SquadTactic.HighGround ? " goes round"
                      : tactic == SquadTactic.Bait ? " (bait, then the one going round)" : " goes in")
                : "";
            Log.Info($"LR Squad: {leader.Profile?.Nickname} calls \"{Titles[tactic]}\" ({plan.Why}{(doctrine?.Doctrine != null ? ", doctrine \"" + doctrine.Doctrine.Title + "\"" : "")})" +
                     $"{picked}{(tactic == SquadTactic.Bounding || tactic == SquadTactic.StormBuilding ? $", bounds of {plan.Phase:0.0} s" : "")}" +
                     $"{(tactic == SquadTactic.SpreadThenStorm || tactic == SquadTactic.HoldBuilding ? $", holds {plan.Phase:0} s" : "")}" +
                     $"{(tactic == SquadTactic.HoldBuilding || tactic == SquadTactic.SmokeScreen ? (plan.Variant ? ", then goes at him" : ", then out by another way") : "")}");
            float comms = doctrine?.Values[DoctrineDeck.Comms] ?? 0.5f;
            SquadBrain.Say(leader, Phrase(tactic), 0.35f + 0.5f * comms);
            return plan;
        }

        public static int Stims { get; private set; }

        /// <summary>A storm squad's bold members with a stim in the bag use it before they go in (a third of the time).</summary>
        private static void StimBeforeStorm(List<BotOwner> mates)
        {
            if (!Settings.StormStims.Value)
            {
                return;
            }

            foreach (BotOwner m in mates)
            {
                DoctrineDeck.Member d = SquadDoctrines.Of(m);
                var stims = m.Medecine?.Stimulators;
                if (d == null || stims == null || !stims.HaveSmt || d[DoctrineDeck.Aggression] < 0.6f || _rng.NextDouble() > 0.35 || !stims.CanUseNow())
                {
                    continue;
                }

                try
                {
                    stims.TryApply();
                    Stims++;
                    Log.Detail("combat", () => $"{m.Profile?.Nickname} uses a stim before going in");
                }
                catch
                {
                    // EFT refused (hands busy): no stim this time
                }
            }
        }

        private static EPhraseTrigger Phrase(SquadTactic t)
        {
            switch (t)
            {
                case SquadTactic.PushTogether:
                case SquadTactic.PushTheWounded: return EPhraseTrigger.Gogogo;
                case SquadTactic.Hold: return EPhraseTrigger.HoldPosition;
                case SquadTactic.PinAndFlank: return _rng.NextDouble() < 0.5 ? EPhraseTrigger.LeftFlank : EPhraseTrigger.RightFlank;
                case SquadTactic.Pincer: return EPhraseTrigger.RightFlank;
                case SquadTactic.SuppressAndRush: return EPhraseTrigger.Suppress;
                case SquadTactic.Bounding: return EPhraseTrigger.CoverMe;
                case SquadTactic.PairPush: return EPhraseTrigger.FollowMe;
                case SquadTactic.FallBackAndRegroup:
                case SquadTactic.Scatter: return EPhraseTrigger.Regroup;
                case SquadTactic.WideFlank:
                case SquadTactic.HighGround: return _rng.NextDouble() < 0.5 ? EPhraseTrigger.LeftFlank : EPhraseTrigger.RightFlank;
                case SquadTactic.Bait:
                case SquadTactic.PeekBait:
                case SquadTactic.SmokeScreen: return EPhraseTrigger.Covering;
                case SquadTactic.ThirdPartyTurn: return EPhraseTrigger.RightFlank;
                case SquadTactic.SpreadThenStorm:
                case SquadTactic.HoldBuilding: return EPhraseTrigger.HoldPosition;
                case SquadTactic.StormBuilding:
                case SquadTactic.RunDown:
                case SquadTactic.Revenge: return EPhraseTrigger.Gogogo;
                default: return EPhraseTrigger.OnYourOwn;
            }
        }

        private static float Boldness(BotOwner bot, BotProfile p)
        {
            DoctrineDeck.Member m = SquadDoctrines.Of(bot);
            float aggr = m != null ? m[DoctrineDeck.Aggression] : p.Aggression;
            float hp = bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f;
            return aggr + (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter ? 0.2f : 0f) + 0.3f * hp + (float)_rng.NextDouble() * 0.15f;
        }

        /// <summary>A roof over the spot (the same test the formation uses for "indoors").</summary>
        public static bool Indoors(Vector3 pos) =>
            Physics.Raycast(pos + Vector3.up * 1.6f, Vector3.up, 6f, LayersMaskController.HighPolyWithTerrainMask);

        private static bool CanFlank(BotOwner bot, BotProfile p) =>
            !CombatSense.IsLongGun(p) && p.Style != PlayStyle.Camper && (bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f) >= 0.6f
            && !LegCare.Limps(bot.GetPlayer)
            && !(bot.GetPlayer?.MovementContext?.PhysicalConditionContainsAny(EPhysicalCondition.LeftLegDamaged | EPhysicalCondition.RightLegDamaged) ?? false);

        /// <summary>The enemy has just been hit by one of the squad (a bot enemy in the last 8 s) or is visibly hurt (a person under 60 %).</summary>
        private static bool Wounded(Enemy closest, List<BotOwner> mates)
        {
            Player enemy = closest?.EnemyPlayer;
            if (enemy == null || closest.RealDistance > 90f)
            {
                return false;
            }

            BotOwner enemyBot = enemy.AIData?.BotOwner;
            if (enemyBot != null)
            {
                return HitReaction.TryGetHit(enemyBot, out HitReaction.Hit hit) && Time.time - hit.At < 8f && hit.By != null
                       && mates.Any(m => m.ProfileId == hit.By.ProfileId);
            }

            return enemy.HealthController != null && enemy.HealthController.IsAlive && enemy.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.6f;
        }

        private static SquadTactic Pick(Dictionary<SquadTactic, float> w)
        {
            float total = w.Values.Sum();
            if (total <= 0.0001f)
            {
                return SquadTactic.None;
            }

            float roll = (float)_rng.NextDouble() * total;
            foreach (KeyValuePair<SquadTactic, float> kv in w)
            {
                roll -= kv.Value;
                if (roll <= 0f)
                {
                    return kv.Key;
                }
            }

            return w.Keys.Last();
        }

        /// <summary>The bot and its living spawn squadmates in the raid (QuestingBots' split groups included), none taken over by the player.</summary>
        private static List<BotOwner> AliveMates(BotOwner bot)
        {
            List<BotOwner> list = new List<BotOwner>();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return list;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner b = player?.AIData?.BotOwner;
                if (b != null && !b.IsDead && (b == bot || SquadTies.AreMates(bot.ProfileId, b.ProfileId)) && !Spectator.TakeOver.IsTaken(b))
                {
                    list.Add(b);
                }
            }

            return list;
        }
    }
}
