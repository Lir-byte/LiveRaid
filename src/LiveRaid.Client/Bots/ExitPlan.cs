using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A PMC leaves through an exit of its own spawn side, chosen once like a player chooses it (F12 -> 8 "Exits of the spawn side").
    ///
    /// EFT sends a bot to the NEAREST open exit with no requirements, over all PMC exits of the map, and looks again every 5-6 s on the way
    /// (PatrolExfiltrationPointsData.UpdateFindNextExfiltrationPoint). On Customs, raids of 2026-10-26: 16 PMCs left through three exits only - RUAF
    /// Roadblock 7, ZB-1011 7, Crossroads 2 - and never through Trailer Park, Old Gas Station, ZB-1012 or Smuggler's Boat. Online a PMC has the exits of
    /// the side it spawned on (the map's exits list "entry points": Customs - ZB-1011, ZB-1012, Old Gas Station; Boiler Tanks - Crossroads, Trailer Park,
    /// RUAF Roadblock, Smuggler's Boat), some of them only in some raids, and picks one - mostly a near one, now and then a far one it prefers.
    ///  - The side: the entry point of the player spawn point nearest to where the bot came into the raid.
    ///  - The exits: those of that side open in this raid (EFT rolled their chance at the start), with no requirement (paid, switch-only, co-op and event exits
    ///    are left to their own logic), reachable by the bot's walking area; none of the side - all open ones, as before.
    ///  - The choice: once, at the first moment the bot is sent out, weighted 1 / (0.35 + distance / 250 m)^2 - the nearest is likeliest, a far one possible.
    ///    The squad takes the leader's exit (SquadExtract). The choice holds for the rest of the raid unless the exit closes.
    /// </summary>
    public static class ExitPlan
    {
        private static readonly AccessTools.FieldRef<PatrolExfiltrationPointsData, AIExfiltrationPoint> CachedPoint =
            AccessTools.FieldRefAccess<PatrolExfiltrationPointsData, AIExfiltrationPoint>("_cachedExfiltrationPoint");

        private static readonly Dictionary<string, string> _chosen = new Dictionary<string, string>();
        private static readonly Dictionary<string, int> _used = new Dictionary<string, int>();
        private static readonly HashSet<string> _carNoted = new HashSet<string>();

        // A squadmate farther than this from a car (straight line, about 40 s at a run) will not be at it in time
        private const float CarMateReach = 200f;

        // The exits each spawn squad (or solo) has for this raid, set when its first member comes in - like the exits a player sees at the start
        private static readonly Dictionary<int, HashSet<string>> _cards = new Dictionary<int, HashSet<string>>();

        // Squads whose card could not be made yet, with the reason already written to the trace (raid 15:59: no card for any squad and not a word why)
        private static readonly HashSet<int> _noCardNoted = new HashSet<int>();

        public static string Summary => _used.Count == 0
            ? "exits chosen by spawn side: none"
            : "exits chosen by spawn side: " + string.Join(", ", _used.OrderByDescending(u => u.Value).Select(u => $"{u.Key} {u.Value}"));

        public static void Clear()
        {
            _chosen.Clear();
            _used.Clear();
            _carNoted.Clear();
            _cards.Clear();
            _noCardNoted.Clear();
        }

        /// <summary>
        /// At activation: the squad's (or solo's) exits for this raid - those of its spawn side that are there this raid (paid, conditional and
        /// switch exits among them; whether the bot can use those is weighed when it leaves). Logged once per squad; the choice when leaving is made
        /// among these only. No exit of its side on record: any open one, as before.
        /// </summary>
        public static void Announce(BotOwner bot)
        {
            if (!Settings.ExitsBySide.Value || bot == null || BotProfiles.Get(bot)?.Kind != BotKind.Pmc)
            {
                return;
            }

            int squad = SquadTies.SquadOf(bot.ProfileId);
            if (_cards.ContainsKey(squad))
            {
                return;
            }

            List<AIExfiltrationPoint> points = bot.BotsController?.CoversData?.Patrols?.ExfiltrationPoints;
            string side = SideOf(bot);
            if (points == null || points.Count == 0 || side == null)
            {
                NoCard(bot, squad, points == null || points.Count == 0 ? "EFT has no AI exit points for it yet" : "its spawn side is not known");
                return;
            }

            HashSet<string> card = new HashSet<string>();
            foreach (AIExfiltrationPoint ai in points)
            {
                ExfiltrationPoint exit = ai?.ExfiltrationPoint;
                if (exit?.Settings != null && exit.Status != EExfiltrationStatus.NotPresent
                    && exit.EligibleEntryPoints?.Any(e => string.Equals(e?.Trim(), side, System.StringComparison.OrdinalIgnoreCase)) == true)
                {
                    card.Add(exit.Settings.Name);
                }
            }

            if (card.Count == 0)
            {
                NoCard(bot, squad, $"no exit of the side '{side}' is there this raid ({points.Count} AI exit points)");
                return;
            }

            _cards[squad] = card;
            Log.Info($"LR Extract: {bot.Profile?.Nickname}'s {(SquadTies.MatesOf(bot.ProfileId).Count() > 1 ? "squad" : "solo run")} came in on the side '{side}' - " +
                     $"its exits this raid: {string.Join(", ", card)}");
        }

        private static void NoCard(BotOwner bot, int squad, string why)
        {
            if (_noCardNoted.Add(squad))
            {
                Log.Detail("info", $"LR Extract: no exit card for {bot.Profile?.Nickname}'s squad yet - {why}; it chooses among the open exits of its side when it leaves");
            }
        }

        /// <summary>The bot's spawn side (the entry point of the nearest player spawn point), lower case, or null.</summary>
        public static string SideOf(BotOwner bot)
        {
            BotProfile p = BotProfiles.Get(bot);
            Vector3 at = p != null && p.SpawnPos != Vector3.zero ? p.SpawnPos : bot.Position;
            var points = Singleton<GameWorld>.Instance?.GetComponent<QuestingBots.Components.LocationData>()?.CurrentLocation?.SpawnPointParams;
            if (points == null)
            {
                return null;
            }

            string best = null;
            float bestD = float.MaxValue;
            foreach (var sp in points)
            {
                if (string.IsNullOrEmpty(sp.Infiltration))
                {
                    continue;
                }

                float d = ((Vector3)sp.Position - at).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = sp.Infiltration;
                }
            }

            return best?.Trim().ToLowerInvariant();
        }

        /// <summary>EFT has just picked the nearest exit for a leader or a solo PMC: it goes to its planned one instead.</summary>
        public static void Apply(PatrolExfiltrationPointsData data, BotOwner bot)
        {
            if (!Settings.ExitsBySide.Value || data == null || bot == null || bot.IsDead)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind != BotKind.Pmc)
            {
                return;
            }

            List<AIExfiltrationPoint> points = bot.BotsController?.CoversData?.Patrols?.ExfiltrationPoints;
            if (points == null || points.Count == 0)
            {
                return;
            }

            // Open exits without requirements that the bot's walking area reaches, by name, with the bot's nearest AI point of each
            Dictionary<string, (AIExfiltrationPoint point, float dist, ExfiltrationPoint exit)> open = new Dictionary<string, (AIExfiltrationPoint, float, ExfiltrationPoint)>();
            foreach (AIExfiltrationPoint ai in points)
            {
                ExfiltrationPoint exit = ai?.ExfiltrationPoint;
                if (exit == null || exit.Status == EExfiltrationStatus.NotPresent || exit.Settings == null
                    || (ai.CorePointInGame != null && bot.StartCorePoint != null && ai.CorePointInGame.ConnectionGroupId != bot.StartCorePoint.ConnectionGroupId)
                    || !ExitRules.Allows(bot, exit, out _))
                {
                    continue;
                }

                // A paid car does not wait: its clock runs for everyone from the first payment. A squad whose mate is too far to be at the car in time takes
                // another exit (raid 2026-11-02 15:58: the car was paid for with a mate 136 m off)
                if (CarExits.IsCar(exit) && SquadExtract.MatesTooFar(bot, ai.Position, CarMateReach, out string far))
                {
                    if (_carNoted.Add(bot.ProfileId + exit.Settings.Name))
                    {
                        Log.Info($"LR Extract: {bot.Profile?.Nickname} leaves the car at {exit.Settings.Name.Localized()} alone - its squad is too far from it to be there in time ({far})");
                    }

                    continue;
                }

                float d = Vector3.Distance(bot.Position, ai.Position);
                string name = exit.Settings.Name;
                if (!open.TryGetValue(name, out var have) || d < have.dist)
                {
                    open[name] = (ai, d, exit);
                }
            }

            if (open.Count == 0)
            {
                return;
            }

            if (!_chosen.TryGetValue(bot.ProfileId, out string plan) || !open.ContainsKey(plan))
            {
                // At activation EFT may not have had the exits or the spawn side ready: the card is made now, before the choice
                Announce(bot);
                string side = SideOf(bot);
                bool carded = _cards.TryGetValue(SquadTies.SquadOf(bot.ProfileId), out HashSet<string> card);
                var mine = open.Where(o => carded
                                           ? card.Contains(o.Key)
                                           : side != null && o.Value.exit.EligibleEntryPoints != null
                                             && o.Value.exit.EligibleEntryPoints.Any(e => string.Equals(e?.Trim(), side, System.StringComparison.OrdinalIgnoreCase)))
                               .ToList();
                bool sideKnown = mine.Count > 0;
                if (!sideKnown)
                {
                    mine = open.ToList();
                }

                // 0.9.9: a bot that has just been beaten off does not walk back through that place (raid 2026-10-31 00:34: Crimson broke off a fight
                // with a squad of three, and the only exit of his side, ZB-1011, lay right past them - he walked into them again and was killed).
                // Exits whose way runs within 70 m of where it fell back count for a tenth, and those of the other sides are open to it too
                bool beaten = Tactics.CombatSense.LostFightRecently(bot, 300f, out Vector3 lostAt) && Vector3.Distance(bot.Position, lostAt) > 40f;
                if (beaten)
                {
                    mine = mine.Concat(open.Where(o => !mine.Any(m => m.Key == o.Key))).ToList();
                }

                float total = 0f;
                List<(string name, float w)> weights = new List<(string, float)>();
                List<string> past = new List<string>();
                foreach (var o in mine)
                {
                    float w = 1f / Mathf.Pow(0.35f + o.Value.dist / 250f, 2f);
                    if (beaten)
                    {
                        if (sideKnown && o.Value.exit.EligibleEntryPoints?.Any(e => string.Equals(e?.Trim(), side, System.StringComparison.OrdinalIgnoreCase)) != true)
                        {
                            w *= 0.4f;
                        }

                        if (DistanceToSegment(lostAt, bot.Position, o.Value.point.Position) < 70f)
                        {
                            w *= 0.1f;
                            past.Add(o.Key);
                        }
                    }

                    weights.Add((o.Key, w));
                    total += w;
                }

                float roll = Random.value * total;
                plan = weights[weights.Count - 1].name;
                foreach (var (name, w) in weights)
                {
                    roll -= w;
                    if (roll <= 0f)
                    {
                        plan = name;
                        break;
                    }
                }

                bool replan = _chosen.ContainsKey(bot.ProfileId);
                ExitRules.Allows(bot, open[plan].exit, out string terms);
                ExitRules.Chosen(plan, terms);
                _chosen[bot.ProfileId] = plan;
                _used[plan] = _used.TryGetValue(plan, out int n) ? n + 1 : 1;
                string nearest = mine.OrderBy(o => o.Value.dist).First().Key;
                Log.Info($"LR Extract: {p.Name} {(replan ? "has to change exits and" : "")} heads for {plan} ({open[plan].dist:0} m) - " +
                         (sideKnown ? $"an exit of its spawn side '{side}'" : "no exit of its spawn side is open, any open one") +
                         $" ({string.Join(", ", weights.Select(x => $"{x.name} {x.w / total:P0}"))})" +
                         (plan != nearest ? $", not the nearest ({nearest})" : "") +
                         (past.Count > 0 ? $"; it has just lost a fight on the way to {string.Join(", ", past)}" : "") +
                         (terms != null ? $"; a paid or conditional exit ({terms})" : ""));
            }

            AIExfiltrationPoint target = open[plan].point;
            if (CachedPoint(data) != target)
            {
                CachedPoint(data) = target;
            }
        }

        private static readonly AccessTools.FieldRef<PatrolExfiltrationPointsData, float> NextCheck =
            AccessTools.FieldRefAccess<PatrolExfiltrationPointsData, float>("_nextCheck");

        /// <summary>The bot's exit is gone (the car drove off): it forgets its plan and EFT's cached exit, and chooses again.</summary>
        public static void Release(BotOwner bot)
        {
            if (bot == null)
            {
                return;
            }

            _chosen.Remove(bot.ProfileId);
            PatrolExfiltrationPointsData data = bot.PatrollingData?.ExfiltrationData;
            if (data != null)
            {
                CachedPoint(data) = null;
                NextCheck(data) = 0f;
            }

            bot.Exfiltration?.ResetLeaveTime();
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude < 0.01f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t);
        }
    }
}
