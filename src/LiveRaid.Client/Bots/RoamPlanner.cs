using System.Collections.Generic;
using System.Linq;

using Comfort.Common;
using EFT;
using QuestingBots.Components;
using QuestingBots.Controllers;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using QuestingBots.Models.Questing;
using UnityEngine.AI;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Players travel: loot a building, do a quest point, move on across the map. Bots kept circling one spot for many minutes
    /// (a cover, an area quest, "holding" a point). Every squad leader and solo bot has an anchor: the place it has been around (70 m)
    /// since it last moved on. After its character's patience (explorers ~3 min ... campers ~8) it gets restless for a few minutes:
    /// objectives near the anchor lose their appeal, far ones gain (QuestingBots' desirability, <see cref="Multiplier"/>), and a current
    /// objective in the same place is dropped so QuestingBots picks the next one now. Followers go where the leader goes.
    /// </summary>
    public static class RoamPlanner
    {
        private const float AnchorRadius = 70f;
        private const float NearRadius = 110f;
        private const float FarRadius = 160f;
        private const float RestlessSeconds = 240f;

        private class Anchor
        {
            public Vector3 Pos;
            public float Since;
            public float RestlessUntil;
            public Vector3 RestlessFrom;
            public bool Evicted;
        }

        // Where each bot has been over the last minutes (a point every 5 s): a bot that never leaves a 110 m circle for the set time is sent away
        private class Dwell
        {
            public readonly Queue<(Vector3 pos, float at)> Samples = new Queue<(Vector3, float)>();
            public float NextSample;
            public float NotBefore;
            public float FastNotBefore;
            public Vector3 Area;
            public float AreaSince = -1f;
        }

        // Raid 2026-11-10 17:22 (Reserve): the squad of Bnuy spent minutes 8-32 in the barracks yard, 120 x 50 m. Every check was skipped: there was always a
        // body of the last fight to go through within 60 m ("not loitering"), a fight less than a minute ago, or a loot job - the yard is where every scav of
        // the base comes, and each one killed was the next reason to stay. A stay 1.75 times over the limit (10.5 min by default) ends whatever the reason:
        // the bodies there are let go and the squad goes to another part of the map.
        private const float OverstayFactor = 1.75f;
        public static int Overstays { get; private set; }

        // Quick tier of the stay check: a calm bot that has not left a circle this small for this long is stuck, not busy (raid 2026-11-01 16:11:
        // FiveF and his partner shuffled 7.5 minutes inside 25 m of one spot, "Regroup" against "FollowBoss", before the 5-minute check sent them away)
        private const float FastRadius = 45f;
        private const float FastSeconds = 150f;

        private const float EvictRadius = 110f;
        private static readonly Dictionary<string, Dwell> _dwell = new Dictionary<string, Dwell>();
        private static readonly System.Random _rng = new System.Random();
        public static int Evictions { get; private set; }
        public static int EvictionsFailed { get; private set; }

        private static readonly Dictionary<string, Anchor> _anchors = new Dictionary<string, Anchor>();
        private static float _next;

        public static int MovedOn { get; private set; }

        public static void Clear()
        {
            _anchors.Clear();
            _dwell.Clear();
            MovedOn = 0;
            Evictions = EvictionsFailed = Overstays = 0;
        }

        public static void Tick()
        {
            if (!Settings.MoveOn.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || Tactics.TacticalTasks.SquadLeaderOf(bot) != null)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other || (p.Kind == BotKind.Scav && ScavPatrol.On))
                {
                    // An AI scav on patrol stays in its area: that is no loitering (ScavPatrol)
                    continue;
                }

                // Still going through the bodies of a fight here: not loitering - up to a point (the overstay)
                if (Tactics.LootJobs.HasWorkNear(bot) || Tactics.LootJobs.LeaderShouldWait(bot))
                {
                    if (Settings.Eviction.Value)
                    {
                        if (!_anchors.TryGetValue(bot.ProfileId, out Anchor held))
                        {
                            _anchors[bot.ProfileId] = held = new Anchor { Pos = bot.Position, Since = Time.time };
                        }

                        Evict(bot, p, held, bot.Position, busyWithBodies: true);
                    }

                    continue;
                }

                Vector3 pos = bot.Position;
                if (!_anchors.TryGetValue(bot.ProfileId, out Anchor a))
                {
                    _anchors[bot.ProfileId] = a = new Anchor { Pos = pos, Since = Time.time };
                }

                if (Settings.Eviction.Value)
                {
                    Evict(bot, p, a, pos);
                }

                Vector3 flat = pos - a.Pos;
                flat.y = 0f;
                if (flat.sqrMagnitude > AnchorRadius * AnchorRadius)
                {
                    a.Pos = pos;
                    a.Since = Time.time;
                    continue;
                }

                // A fight here is not loitering; the clock starts again after it
                if (Tactics.HitReaction.InRealFight(bot))
                {
                    a.Since = Mathf.Max(a.Since, Time.time - Patience(p) * 0.5f);
                    continue;
                }

                // A fast squad (its doctrine's pace) has less patience (Tempo)
                if (Time.time - a.Since < Patience(p) * Tempo.PaceFactor(bot) || Time.time < a.RestlessUntil)
                {
                    continue;
                }

                a.RestlessFrom = a.Pos;
                a.RestlessUntil = Time.time + RestlessSeconds;
                MovedOn++;
                string objective = bot.GetObjectiveManager()?.CurrentAssignment?.QuestAssignment?.GetName() ?? "-";
                Vector3? target = bot.GetObjectiveManager()?.Position;
                bool dropped = false;
                if (target == null || FlatDistance(target.Value, a.Pos) < NearRadius)
                {
                    BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
                    dropped = true;
                }

                Log.Info($"LR Route: {p.Name} ({p.Style}) spent {(Time.time - a.Since) / 60f:0.0} min around ({a.Pos.x:0}, {a.Pos.z:0}) " +
                         $"- moves on to another part of the map{(dropped ? $" (drops \"{objective}\")" : "")}");
                a.Since = Time.time;
            }
        }

        /// <summary>
        /// The bot leaves a region (Tempo: searched before it came, nothing found): objectives near it lose their appeal for a few minutes
        /// (<see cref="Multiplier"/>), and its current one is dropped if it lies there, so QuestingBots picks one elsewhere now.
        /// </summary>
        public static void LeaveRegion(BotOwner bot, BotProfile p, Vector3 centre, string why)
        {
            if (!_anchors.TryGetValue(bot.ProfileId, out Anchor a))
            {
                _anchors[bot.ProfileId] = a = new Anchor { Pos = bot.Position, Since = Time.time };
            }

            a.RestlessFrom = centre;
            a.RestlessUntil = Time.time + RestlessSeconds;
            a.Since = Time.time;
            string objective = bot.GetObjectiveManager()?.CurrentAssignment?.QuestAssignment?.GetName() ?? "-";
            Vector3? target = bot.GetObjectiveManager()?.Position;
            bool dropped = false;
            if (target == null || FlatDistance(target.Value, centre) < NearRadius)
            {
                BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
                dropped = true;
            }

            Log.Info($"LR Route: {p.Name} ({p.Style}) leaves the region at ({centre.x:0}, {centre.z:0}) - {why}{(dropped ? $" (drops \"{objective}\")" : "")}");
        }

        /// <summary>
        /// "Eviction": the bot has spent the whole stay limit (F12, 6 min; explorers/rushers a little less, rats/snipers more) inside one 110 m circle without leaving.
        /// The ordinary move-on (above) did not help such a bot: its anchor resets whenever it steps 70 m away, and QuestingBots hands it the same region's quest again
        /// (raid 19:08: a scav stayed 10 minutes around one block although "moves on" fired twice).
        /// Now: the current objective is dropped, everything near is shut out for 8 minutes (desirability) and a far place of the map is chosen for it and it is walked there
        /// as a tactical task (sprinting the long stretches, called off by any fight). Squad followers follow their leader.
        /// </summary>
        private static void Evict(BotOwner bot, BotProfile p, Anchor a, Vector3 pos, bool busyWithBodies = false)
        {
            if (!_dwell.TryGetValue(bot.ProfileId, out Dwell d))
            {
                _dwell[bot.ProfileId] = d = new Dwell { NotBefore = Time.time + StayLimit(p) };
            }

            if (Time.time < d.NextSample)
            {
                return;
            }

            d.NextSample = Time.time + 5f;
            d.Samples.Enqueue((pos, Time.time));
            float limit = StayLimit(p);

            // How long the bot has not left one circle, whatever it was doing
            if (d.AreaSince < 0f || FlatDistance(pos, d.Area) > EvictRadius)
            {
                d.Area = pos;
                d.AreaSince = Time.time;
            }

            float stayed = Time.time - d.AreaSince;
            if (stayed >= limit * OverstayFactor && Time.time >= d.NotBefore)
            {
                Overstay(bot, p, a, d, pos, stayed);
                return;
            }

            if (busyWithBodies)
            {
                return;
            }
            while (d.Samples.Count > 0 && Time.time - d.Samples.Peek().at > limit + 10f)
            {
                d.Samples.Dequeue();
            }

            Vector3 centre = Vector3.zero;
            float used = limit;
            bool fast = Time.time >= d.FastNotBefore && FastStall(d, FastSeconds * Tempo.PaceFactor(bot), out centre);
            if (fast)
            {
                used = FastSeconds * Tempo.PaceFactor(bot);
            }
            else
            {
                if (Time.time < d.NotBefore || d.Samples.Count < 8 || Time.time - d.Samples.Peek().at < limit)
                {
                    return;
                }

                // Everything the bot did within the limit stays inside one circle?
                foreach ((Vector3 sp, float _) in d.Samples)
                {
                    centre += sp;
                }

                centre /= d.Samples.Count;
                foreach ((Vector3 sp, float _) in d.Samples)
                {
                    if (FlatDistance(sp, centre) > EvictRadius)
                    {
                        // It does travel: the window starts over from here
                        d.Samples.Clear();
                        return;
                    }
                }
            }

            // Not in the middle of something that matters
            BotObjectiveManager objectives = bot.GetObjectiveManager();
            if (HitReaction.InRealFight(bot, 60f) || bot.Medecine?.Using == true || TacticalTasks.TryGet(bot, out _)
                || objectives?.BotMonitor?.GetMonitor<QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor>()?.IsTryingToExtract == true
                || RemainingFraction() < 0.12f)
            {
                return;
            }

            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            if (brain != null && (brain.LootTaskRunning || brain.HasActiveLootable))
            {
                return;
            }

            Vector3? far = PickFarPoint(bot, centre, out float pathLength);
            d.Samples.Clear();
            d.NotBefore = Time.time + 180f;
            d.FastNotBefore = Time.time + 150f;
            a.RestlessFrom = centre;
            a.RestlessUntil = Time.time + 480f;
            a.Evicted = true;
            a.Pos = pos;
            a.Since = Time.time;
            BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
            if (far == null)
            {
                EvictionsFailed++;
                Log.Info($"LR Route: {p.Name} ({p.Style}) has stayed {used / 60f:0.0} min around ({centre.x:0}, {centre.z:0}) - the objectives there are dropped and shut out, no far place found to send it to");
                return;
            }

            Evictions++;
            float seconds = pathLength / 3.2f + 45f;
            TacticalTask task = new TacticalTask
            {
                Kind = TaskKind.Relocate,
                StandAt = far,
                LookAt = far.Value,
                Until = Time.time + seconds,
                Speed = 1f,
                MovePose = 1f,
                Sprint = true,
                SprintBeyond = 30f,
            };
            TacticalTasks.Set(bot, task);
            Log.Info($"LR Route: {p.Name} ({p.Style}) has stayed {used / 60f:0.0} min within {(fast ? FastRadius : EvictRadius):0} m of ({centre.x:0}, {centre.z:0}) - sent to ({far.Value.x:0}, {far.Value.z:0}), {pathLength:0} m away");
        }

        /// <summary>A stay far over the limit: only a fight right now, a treatment or a loot job in the hands put it off (a few seconds).</summary>
        private static void Overstay(BotOwner bot, BotProfile p, Anchor a, Dwell d, Vector3 pos, float stayed)
        {
            BotObjectiveManager objectives = bot.GetObjectiveManager();
            LootingBots.Components.LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
            if (HitReaction.InRealFight(bot, 12f) || bot.Medecine?.Using == true || (brain != null && brain.LootTaskRunning)
                || objectives?.BotMonitor?.GetMonitor<QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor>()?.IsTryingToExtract == true
                || SwitchExtract.HasPlan(bot) || RemainingFraction() < 0.12f)
            {
                return;
            }

            Vector3 centre = d.Area;
            Vector3? far = PickFarPoint(bot, centre, out float pathLength);
            d.Samples.Clear();
            d.AreaSince = -1f;
            d.NotBefore = Time.time + 180f;
            d.FastNotBefore = Time.time + 150f;
            a.RestlessFrom = centre;
            a.RestlessUntil = Time.time + 480f;
            a.Evicted = true;
            a.Pos = pos;
            a.Since = Time.time;
            int bodies = LootJobs.DropNear(bot, centre, EvictRadius + 40f);
            List<BotOwner> followers = bot.Boss?.Followers;
            if (followers != null)
            {
                foreach (BotOwner f in followers)
                {
                    if (f != null && !f.IsDead)
                    {
                        bodies += LootJobs.DropNear(f, centre, EvictRadius + 40f);
                    }
                }
            }

            BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
            TacticalTasks.Cancel(bot, "overstay");
            if (far == null)
            {
                EvictionsFailed++;
                Log.Info($"LR Route: {p.Name} ({p.Style}) has not left {EvictRadius:0} m of ({centre.x:0}, {centre.z:0}) for {stayed / 60f:0.0} min - the objectives there are dropped and shut out, " +
                         "no far place found to send it to");
                return;
            }

            Evictions++;
            Overstays++;
            TacticalTasks.Set(bot, new TacticalTask
            {
                Kind = TaskKind.Relocate,
                StandAt = far,
                LookAt = far.Value,
                Until = Time.time + pathLength / 3.2f + 45f,
                Speed = 1f,
                MovePose = 1f,
                Sprint = true,
                SprintBeyond = 30f,
            });
            Log.Info($"LR Route: {p.Name} ({p.Style}) has not left {EvictRadius:0} m of ({centre.x:0}, {centre.z:0}) for {stayed / 60f:0.0} min (fights, bodies and loot kept it there) - " +
                     $"{bodies} bodies there are let go, sent to ({far.Value.x:0}, {far.Value.z:0}), {pathLength:0} m away");
        }

        /// <summary>The samples of the last <paramref name="seconds"/> all lie within <see cref="FastRadius"/> of their centre (and the window is really covered).</summary>
        private static bool FastStall(Dwell d, float seconds, out Vector3 centre)
        {
            centre = Vector3.zero;
            float from = Time.time - seconds;
            int count = 0;
            float oldest = float.MaxValue;
            foreach ((Vector3 sp, float at) in d.Samples)
            {
                if (at >= from)
                {
                    centre += sp;
                    count++;
                    oldest = Mathf.Min(oldest, at);
                }
            }

            if (count < seconds / 5f - 4f || oldest > from + 12f)
            {
                return false;
            }

            centre /= count;
            foreach ((Vector3 sp, float at) in d.Samples)
            {
                if (at >= from && FlatDistance(sp, centre) > FastRadius)
                {
                    return false;
                }
            }

            return true;
        }

        private static float StayLimit(BotProfile p)
        {
            float minutes = Settings.EvictionMinutes.Value;
            switch (p.Style)
            {
                case PlayStyle.Explorer:
                case PlayStyle.Rusher:
                case PlayStyle.Hunter: minutes *= 0.85f; break;
                case PlayStyle.Rat:
                case PlayStyle.Sniper:
                case PlayStyle.Camper: minutes *= 1.2f; break;
            }

            return minutes * 60f;
        }

        private static float RemainingFraction()
        {
            try
            {
                float total = QuestingBots.Helpers.RaidHelpers.OriginalEscapeTimeSeconds;
                return total > 1f ? QuestingBots.Helpers.RaidHelpers.GetRemainingRaidTimeSeconds() / total : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>A place 220-450 m from where the bot has been, that it can walk to: a point of the map's own list (containers, loot, learned places), several tries.</summary>
        internal static Vector3? PickFarPoint(BotOwner bot, Vector3 centre, out float pathLength)
        {
            pathLength = 0f;
            MapIntel intel = MapIntel.Current;
            if (intel == null || intel.Pois.Count == 0)
            {
                return null;
            }

            NavMeshPath path = new NavMeshPath();
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Poi poi = intel.Pois[_rng.Next(intel.Pois.Count)];
                Vector3 at = poi.Pos;
                float flat = FlatDistance(at, centre);
                // The first tries ask for a proper trip (220-450 m); when the map has nothing like that from here (an edge, a corner), anything 140+ m away will do
                bool wide = attempt >= 20;
                if (flat < (wide ? 140f : 220f) || flat > (wide ? 700f : 450f) || poi.W < 0.2f)
                {
                    continue;
                }

                // Not into a crowd while there is room elsewhere (the first tries only; MapBalance)
                if (!wide && !Raid.MapBalance.Accept(bot, at))
                {
                    continue;
                }

                if (!NavMesh.SamplePosition(at, out NavMeshHit hit, 2f, NavMesh.AllAreas)
                    || !NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0f;
                for (int c = 1; c < path.corners.Length; c++)
                {
                    len += Vector3.Distance(path.corners[c - 1], path.corners[c]);
                }

                if (len > 1500f)
                {
                    continue;
                }

                pathLength = len;
                return hit.position;
            }

            return null;
        }

        /// <summary>Extra desirability factor of a quest for a restless bot: near the place it has been loitering - low, far - high.</summary>
        public static float Multiplier(BotOwner bot, BotQuest quest)
        {
            if (bot == null || !_anchors.TryGetValue(bot.ProfileId, out Anchor a) || Time.time > a.RestlessUntil)
            {
                return 1f;
            }

            Vector3? at = quest.GetValidObjectives().FirstOrDefault()?.GetFirstStepPosition();
            if (at == null)
            {
                return 1f;
            }

            float d = FlatDistance(at.Value, a.RestlessFrom);
            if (a.Evicted)
            {
                // Sent away: nothing near the place it was kept out of, the far side of the map is welcome
                return d < 150f ? 0.02f : d > 220f ? 2.5f : 0.8f;
            }

            return d < NearRadius ? 0.1f : d > FarRadius ? 1.5f : 1f;
        }

        private static float Patience(BotProfile p)
        {
            switch (p.Style)
            {
                // About a third shorter than at first (raid 2026-10-20: 4 move-ons in 10 min, bots circled one place)
                case PlayStyle.Explorer: return 120f;
                case PlayStyle.Rusher:
                case PlayStyle.Hunter: return 140f;
                case PlayStyle.Quester: return 160f;
                case PlayStyle.Looter: return 200f;
                case PlayStyle.Rat: return 240f;
                case PlayStyle.Sniper: return 280f;
                case PlayStyle.Camper: return 320f;
                default: return 180f;
            }
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            d.y = 0f;
            return d.magnitude;
        }
    }
}
