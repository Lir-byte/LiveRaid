using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Raid;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Prize keys (F12 -> 2 "Prize keys in a PMC secure container"). Online somebody in the lobby often has the key to the richest locked place of the map - the
    /// marked room on Customs, KIBA on Interchange, a marked room on Reserve, a Labs keycard - kept in his secure container, and runs there first.
    ///  - Prize places: the locked doors of the map (MapIntel) whose key is worth 140k+ (handbook/flea price through LootingBots' appraiser); doors within 15 m of
    ///    each other are one place (KIBA: the outer door and the inner grate - both keys go together).
    ///  - Carriers: rolled once per raid - 75 % that one PMC has a prize key, then 55 %, 35 %, 15 % for a second, third, fourth (F12: chance and its drop per
    ///    carrier). A carrier is a PMC squad leader or solo, picked as they come into the raid; each takes a place nobody has yet if there is one.
    ///  - The keys go into his secure container: nobody sees them on the body, nobody can loot them (they leave the raid with him or are lost with him).
    ///  - When and how urgently he goes is rolled: 55 % right away (the first minute), 30 % a little later (2-7 min), 15 % late (8-18 min); 1 in 10 carriers
    ///    keeps the key for later and only goes if the room comes up on his way. From his moment on the place's locked-room quest pulls hard and the rest of
    ///    the map waits; QuestingBots drops its current objective once so the run starts at once.
    ///  - The run ends when he has been inside for 20 s, when someone else got the place open first, or after 15 minutes.
    /// Behind the first door a second locked door of the same place (KIBA's grate) is unlocked by ContainerUnlockLayer, which the plan points at.
    /// </summary>
    public static class PrizeKeys
    {
        private const float PrizePrice = 140000f;
        private const float ClusterRadius = 15f;
        private const float RunSeconds = 900f;
        private const float InsideSeconds = 20f;

        public class Place
        {
            public string Name;
            public readonly List<LockedSpot> Spots = new List<LockedSpot>();
            public readonly List<string> Keys = new List<string>();
            public float Price;
            public int Carriers;
            public Vector3 Center;
        }

        private class Plan
        {
            public string BotId;
            public string Name;
            public Place Place;
            public float GoAt;
            public float Pull;
            public bool Goes;
            public bool Kicked;
            public bool Done;
            public float InsideSince = -1f;
        }

        private static readonly List<Place> _places = new List<Place>();
        private static readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>();
        private static readonly System.Random _rng = new System.Random();
        private static bool _built;
        private static int _slots = -1;
        private static float _next;

        public static int Carriers { get; private set; }
        public static int GotIn { get; private set; }
        public static int Beaten { get; private set; }

        public static string Summary => !_built ? "prize keys: -" :
            _places.Count == 0 ? "prize keys: no prize places on this map" :
            $"prize keys: {Carriers} carriers ({_slots} slots left), got into the place {GotIn}, found it opened by someone else {Beaten}";

        public static void Clear()
        {
            _places.Clear();
            _plans.Clear();
            _built = false;
            _slots = -1;
            _next = 0f;
            Carriers = GotIn = Beaten = 0;
        }

        private static bool On => Settings.PrizeKeys.Value && Settings.RealKeys.Value;

        /// <summary>A freshly activated PMC (BotSetup): a squad leader or solo may get a prize key, while the raid's carriers are not all handed out.</summary>
        public static void TryGive(BotOwner bot, BotProfile p)
        {
            if (!On || bot == null || p == null || p.Kind != BotKind.Pmc || (bot.BotFollower != null && bot.BotFollower.HaveBoss))
            {
                return;
            }

            EnsurePlaces();
            if (_places.Count == 0)
            {
                return;
            }

            if (_slots < 0)
            {
                RollSlots();
            }

            if (_slots <= 0)
            {
                return;
            }

            // Which of the raid's PMCs gets it is random: about every second squad leader / solo on its way in, anyone after the first 4 minutes
            float elapsed = QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds();
            if (elapsed < 240f && _rng.NextDouble() < 0.5)
            {
                return;
            }

            Place place = PickPlace();
            List<string> given = new List<string>();
            foreach (string keyId in place.Keys)
            {
                if (BotSetup.HasKeyFor(bot, keyId))
                {
                    given.Add(keyId);
                    continue;
                }

                Item key = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null);
                if (key != null && BotSetup.Place(bot, key, new[] { EquipmentSlot.SecuredContainer }))
                {
                    given.Add(keyId);
                }
            }

            if (given.Count == 0)
            {
                Log.Warn($"LR Key: {p.Name} could not take the prize key for {place.Name} - no room in its secure container");
                return;
            }

            _slots--;
            Carriers++;
            place.Carriers++;
            foreach (string k in given)
            {
                if (!p.Keys.Contains(k))
                {
                    p.Keys.Add(k);
                }
            }

            float now = Time.time;
            Plan plan = new Plan { BotId = bot.ProfileId, Name = p.Name, Place = place, Goes = _rng.NextDouble() < 0.9 };
            string when;
            double r = _rng.NextDouble();
            if (!plan.Goes)
            {
                plan.GoAt = float.MaxValue;
                plan.Pull = 1.5f;
                when = "keeps it for later - goes there if it comes up on the way";
            }
            else if (r < 0.55)
            {
                plan.GoAt = now + (float)_rng.NextDouble() * 40f;
                plan.Pull = 25f;
                when = "runs there right away";
            }
            else if (r < 0.85)
            {
                plan.GoAt = now + 120f + (float)_rng.NextDouble() * 300f;
                plan.Pull = 14f;
                when = $"goes there in ~{(plan.GoAt - now) / 60f:0} min";
            }
            else
            {
                plan.GoAt = now + 480f + (float)_rng.NextDouble() * 600f;
                plan.Pull = 8f;
                when = $"goes there late, in ~{(plan.GoAt - now) / 60f:0} min";
            }

            _plans[bot.ProfileId] = plan;
            Log.Info($"LR Key: {p.Name} (PMC {p.Style}{(bot.Boss?.Followers?.Count > 0 ? $", squad of {bot.Boss.Followers.Count + 1}" : "")}) carries the prize key(s) " +
                     $"{string.Join(" + ", given.Select(BotSetup.KeyName))} in its secure container - {place.Name}, {Vector3.Distance(bot.Position, place.Center):0} m away - {when}");
        }

        /// <summary>Carriers of the raid: 75 % for one, then 20 % less for every further one (F12).</summary>
        private static void RollSlots()
        {
            _slots = 0;
            float chance = Mathf.Clamp01(Settings.PrizeKeyChance.Value);
            float step = Mathf.Max(0.05f, Settings.PrizeKeyStep.Value);
            var rolls = new List<string>();
            while (chance > 0f)
            {
                bool yes = _rng.NextDouble() < chance;
                rolls.Add($"{chance:P0} {(yes ? "yes" : "no")}");
                if (!yes)
                {
                    break;
                }

                _slots++;
                chance -= step;
            }

            Log.Info($"LR Key: prize places on {MapIntel.Current?.LocationId}: {string.Join(", ", _places.Select(pl => $"{pl.Name} ({pl.Price / 1000f:0}k)"))} - " +
                     $"{_slots} PMC(s) of this raid carry a prize key (rolls: {string.Join(", ", rolls)})");
        }

        private static Place PickPlace()
        {
            int least = _places.Min(pl => pl.Carriers);
            List<Place> free = _places.Where(pl => pl.Carriers == least).ToList();
            float total = free.Sum(pl => Mathf.Sqrt(pl.Price));
            float roll = (float)_rng.NextDouble() * total;
            foreach (Place pl in free)
            {
                roll -= Mathf.Sqrt(pl.Price);
                if (roll <= 0f)
                {
                    return pl;
                }
            }

            return free[free.Count - 1];
        }

        private static void EnsurePlaces()
        {
            if (_built)
            {
                return;
            }

            MapIntel intel = MapIntel.Current;
            if (intel == null)
            {
                return;
            }

            intel.EnsureDoors();
            _built = true;
            var prize = intel.Spots.Where(s => s.Object != null && s.StillLocked && !s.IsContainer && BotSetup.KeyPrice(s.KeyId) >= PrizePrice).ToList();
            foreach (LockedSpot spot in prize)
            {
                Vector3 at = spot.Object.transform.position;
                Place place = _places.FirstOrDefault(pl => pl.Spots.Any(s => (s.Object.transform.position - at).sqrMagnitude < ClusterRadius * ClusterRadius));
                if (place == null)
                {
                    _places.Add(place = new Place());
                }

                place.Spots.Add(spot);
                if (!place.Keys.Contains(spot.KeyId))
                {
                    place.Keys.Add(spot.KeyId);
                }
            }

            foreach (Place place in _places)
            {
                place.Price = place.Keys.Max(k => (float)BotSetup.KeyPrice(k));
                string top = place.Keys.OrderByDescending(BotSetup.KeyPrice).First();
                place.Name = $"'{BotSetup.KeyName(top)}' ({place.Spots[0].Id}{(place.Spots.Count > 1 ? $" +{place.Spots.Count - 1}" : "")})";
                place.Center = place.Spots.Aggregate(Vector3.zero, (sum, s) => sum + s.Object.transform.position) / place.Spots.Count;
            }

            // Only places a bot can get to: the first door of the place has a way in (a locked-room quest)
            _places.RemoveAll(pl => !pl.Spots.Any(s => s.InteriorPos.HasValue && s.OutsidePos.HasValue));
        }

        private static Plan PlanOf(BotOwner bot)
        {
            if (bot == null || _plans.Count == 0)
            {
                return null;
            }

            if (_plans.TryGetValue(bot.ProfileId, out Plan plan))
            {
                return plan;
            }

            // A follower goes where its leader goes
            BotOwner leader = Tactics.TacticalTasks.SquadLeaderOf(bot);
            return leader != null && _plans.TryGetValue(leader.ProfileId, out plan) ? plan : null;
        }

        /// <summary>The bot is on its run to its prize place right now.</summary>
        public static bool Running(BotOwner bot)
        {
            Plan plan = PlanOf(bot);
            return plan != null && plan.Goes && !plan.Done && Time.time >= plan.GoAt;
        }

        /// <summary>QuestingBots' desirability (QbDesirabilityPatch): the place's locked-room quests pull hard from the carrier's moment on, the rest waits.</summary>
        public static float Multiplier(BotOwner bot, BotQuest quest)
        {
            if (!On || bot == null || !_plans.TryGetValue(bot.ProfileId, out Plan plan) || plan.Done)
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            bool ours = name.StartsWith(QuestFactory.LockedPrefix) && plan.Place.Spots.Any(s => name.Substring(QuestFactory.LockedPrefix.Length) == s.Id);
            bool now = plan.Goes && Time.time >= plan.GoAt;
            if (ours)
            {
                return now ? plan.Pull : plan.Pull > 2f ? 1.3f : plan.Pull;
            }

            return now ? 0.35f : 1f;
        }

        /// <summary>
        /// The first locked door of the bot's own prize place, once its moment has come and it is within 200 m - ContainerUnlockLayer walks the bot there
        /// and unlocks it, whatever QuestingBots has it doing. Before, only QuestingBots' locked-room quest led there: raid 2026-10-30 23:28, nidhoeggr set
        /// off 32 m from 'Общ Меч', a fight came between, then QuestingBots held the wounded bot ("waited 121 s for it to be able-bodied") and the quest
        /// never came up again - none of the 9 carriers of the last four raids got in.
        /// </summary>
        public static LockedSpot FirstDoor(BotOwner bot)
        {
            if (!On || bot == null || !_plans.TryGetValue(bot.ProfileId, out Plan plan) || !plan.Goes || plan.Done || Time.time < plan.GoAt)
            {
                return null;
            }

            Vector3 pos = bot.Position;
            return plan.Place.Spots
                .Where(s => s.StillLocked && s.Object != null && s.OutsidePos.HasValue && s.InteriorPos.HasValue && (s.OutsidePos.Value - pos).sqrMagnitude < 200f * 200f
                            && BotSetup.HasKeyFor(bot, s.KeyId))
                .OrderBy(s => (s.OutsidePos.Value - pos).sqrMagnitude)
                .FirstOrDefault();
        }

        /// <summary>
        /// A locked door of the bot's prize place it can now walk up to (behind the first one: KIBA's grate) - for ContainerUnlockLayer.
        /// </summary>
        public static LockedSpot InnerDoor(BotOwner bot)
        {
            Plan plan = PlanOf(bot);
            if (plan == null || plan.Done || !plan.Goes || plan.Place.Spots.Count < 2)
            {
                return null;
            }

            // Only once the place is open (the first door unlocked): the rest of its doors are inside
            if (plan.Place.Spots.All(s => s.StillLocked))
            {
                return null;
            }

            return plan.Place.Spots.FirstOrDefault(s => s.StillLocked && s.Object != null && (s.Object.transform.position - bot.Position).sqrMagnitude < 30f * 30f
                                                        && BotSetup.HasKeyFor(bot, s.KeyId));
        }

        /// <summary>Every 2 s: carriers whose moment came drop their current objective once; runs end inside, beaten or out of time.</summary>
        public static void Tick()
        {
            if (_plans.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Plan plan in _plans.Values)
            {
                if (plan.Done)
                {
                    continue;
                }

                Player player = null;
                foreach (Player pl in world.AllAlivePlayersList)
                {
                    if (pl != null && pl.ProfileId == plan.BotId)
                    {
                        player = pl;
                        break;
                    }
                }

                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    plan.Done = true;
                    continue;
                }

                if (!plan.Goes || now < plan.GoAt)
                {
                    continue;
                }

                if (!plan.Kicked)
                {
                    // Its moment: whatever QuestingBots has it doing is dropped once, the next pick is the place
                    plan.Kicked = true;
                    if (Tactics.TacticalTasks.NoThreat(bot))
                    {
                        BotJobAssignmentController.InactivateAllJobAssignmentsForBot(bot.ProfileId);
                    }

                    Log.Info($"LR Key: {plan.Name} heads for its prize place {plan.Place.Name} ({Vector3.Distance(bot.Position, plan.Place.Center):0} m)");
                }

                bool inside = plan.Place.Spots.Any(s => s.InteriorPos.HasValue && (s.InteriorPos.Value - bot.Position).sqrMagnitude < 7f * 7f
                                                        && Mathf.Abs(s.InteriorPos.Value.y - bot.Position.y) < 2.5f);
                if (inside && !plan.Place.Spots.All(s => s.StillLocked))
                {
                    if (plan.InsideSince < 0f)
                    {
                        plan.InsideSince = now;
                    }
                    else if (now - plan.InsideSince > InsideSeconds)
                    {
                        plan.Done = true;
                        GotIn++;
                        Log.Info($"LR Key: {plan.Name} got into its prize place {plan.Place.Name} and goes through it");
                    }

                    continue;
                }

                plan.InsideSince = -1f;
                if (plan.Place.Spots.All(s => !s.StillLocked) && (plan.Place.Center - bot.Position).sqrMagnitude > 25f * 25f)
                {
                    // Somebody else opened it while it was on the way: no locked-room quest any more - the run is over (it may still pass by)
                    plan.Done = true;
                    Beaten++;
                    Log.Info($"LR Key: {plan.Name} - its prize place {plan.Place.Name} has been opened by someone else");
                    continue;
                }

                if (now - plan.GoAt > RunSeconds)
                {
                    plan.Done = true;
                    Log.Info($"LR Key: {plan.Name} did not make it to its prize place {plan.Place.Name} in {RunSeconds / 60f:0} min - goes on with the raid");
                }
            }
        }
    }
}
