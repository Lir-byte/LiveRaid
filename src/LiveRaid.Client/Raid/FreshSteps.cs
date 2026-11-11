using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// A place somebody has just been to is not walked to again (F12 -> 3 "Skip places just checked", 1.4.0).
    ///
    /// Raids 2026-11-10: on Reserve all the bots together finished 93 quest steps in 28 minutes against 714 spots under watch, on Customs 470 in 40 minutes against
    /// 767 - and QuestingBots hands a bot the NEAREST step of its quest whether or not another bot (or the bot itself, in passing) stood there a minute ago. A step
    /// spent on ground already seen is a step not spent on the 40 % of the map nobody reached. A player who watched his mate clear a room does not clear it again.
    ///
    /// When QuestingBots asks whether a step may go to a bot (BotQuestObjective.CanAssignBot), a step whose spot the coverage ledger saw visited in the last
    /// 10 minutes is refused - by chance, not always: open ground 90 %, a loot spot 55 % (passing by is not searching), less for an old visit, more for explorers,
    /// scaled by the F12 strength; the roll of a bot for a spot holds for 3 minutes, so the answer does not flicker. The quest then gives its next nearest step, a quest
    /// with nothing fresh left gives none and QuestingBots takes another quest - with CoverageLedger's weight, one with fresh ground. AI scavs run out of steps in
    /// their patrol area sooner, which grows it (ScavPatrol) - they get to the corners of their part of the map too.
    /// </summary>
    public static class FreshSteps
    {
        private const float Window = 600f;

        public static int Skipped { get; private set; }
        public static int Kept { get; private set; }

        public static bool On => Settings.Enabled.Value && Settings.FreshSteps.Value && Settings.FreshStepsStrength.Value > 0;

        public static string Summary => !On
            ? "skip places just checked: off"
            : $"skip places just checked: {Skipped} steps on ground already seen refused, {Kept} taken all the same, lifted for a bot left without a quest {Amnesties}";

        private static readonly Dictionary<string, float> _amnesty = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _refusedAt = new Dictionary<string, float>();

        public static int Amnesties { get; private set; }

        public static void Clear()
        {
            Skipped = Kept = Amnesties = 0;
            _amnesty.Clear();
            _refusedAt.Clear();
        }

        /// <summary>
        /// QuestingBots found no quest at all for the bot and is about to switch its questing off for good. If this rule refused it a step in the last minute, the
        /// rule is lifted for the bot for 10 minutes and QuestingBots looks again instead. True: let QuestingBots give up as it meant to.
        /// </summary>
        public static bool LetGiveUp(BotOwner bot)
        {
            if (bot == null || !_refusedAt.TryGetValue(bot.ProfileId, out float at) || Time.time - at > 60f)
            {
                return true;
            }

            _amnesty[bot.ProfileId] = Time.time + 600f;
            _refusedAt.Remove(bot.ProfileId);
            Amnesties++;
            Log.Info($"LR Coverage: {bot.Profile?.Nickname} found no quest with the places just checked left out - the rule is lifted for it for 10 minutes");
            return false;
        }

        public static bool Allows(BotOwner bot, Vector3? position)
        {
            if (!On || bot == null || position == null)
            {
                return true;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other || (_amnesty.TryGetValue(bot.ProfileId, out float until) && Time.time < until))
            {
                return true;
            }

            // An AI scav of a patrol area has few steps to choose from as it is (ScavPatrol): only the wanderers skip
            if (p.Kind == BotKind.Scav && !ScavPatrol.Wanders(bot))
            {
                return true;
            }

            Vector3 at = position.Value;
            if (!CoverageLedger.VisitedLately(at, Window, out bool loot, out int index, out float age))
            {
                return true;
            }

            // Only while the quest has ground left that nobody has seen: a quest never ends up with no step to give because of this rule
            if (!CoverageLedger.QuestHasOtherFresh(index, Window))
            {
                return true;
            }

            // Standing next to it: whether it looks in once more makes no difference to the map
            if ((bot.Position - at).sqrMagnitude < 12f * 12f)
            {
                return true;
            }

            float chance = Settings.FreshStepsStrength.Value / 100f * (loot ? 0.55f : 0.9f) * Mathf.Clamp(0.75f + 0.25f * p.Explore, 0.6f, 1.15f)
                           * Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(age / Window));
            // The packrat goes through everything itself
            if (p.Packrat && loot)
            {
                chance *= 0.4f;
            }

            if (Roll(bot.ProfileId, index, (int)(Time.time / 180f)) < chance)
            {
                Skipped++;
                _refusedAt[bot.ProfileId] = Time.time;
                return false;
            }

            Kept++;
            return true;
        }

        /// <summary>A number 0..1 that stays the same for the same bot, spot and 3-minute window.</summary>
        internal static float Roll(string id, int a, int b)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in id ?? "")
                {
                    h = (h ^ c) * 16777619u;
                }

                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }

    /// <summary>QuestingBots asks whether a quest step may go to a bot: not one on ground just seen (FreshSteps).</summary>
    public class QbFreshStepPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuestObjective), nameof(BotQuestObjective.CanAssignBot));

        [PatchPostfix]
        protected static void PatchPostfix(BotQuestObjective __instance, BotOwner bot, ref bool __result)
        {
            try
            {
                if (__result && bot != null && FreshSteps.On)
                {
                    __result = FreshSteps.Allows(bot, __instance.GetFirstStepPosition());
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbFreshStepPatch", e);
            }
        }
    }

    /// <summary>
    /// QuestingBots' quest choice dies on an empty list (built in, a fix). BotJobAssignmentCreationJob drops a quest from its list when no step of it can be
    /// given to the bot, and asks ChooseNextRandomQuest again; with the list empty that one calls Max() on nothing - "InvalidOperationException: Sequence
    /// contains no elements" in Player.log (3 times in the raids of 2026-11-10), the coroutine is gone and the bot is left without an assignment instead of
    /// reaching QuestingBots' own way out (repeatable quests again, or the give-up FreshSteps watches). With an empty list the choice is skipped for a frame,
    /// with no quest chosen - the job then runs into its timeout as its author meant.
    /// </summary>
    public class QbEmptyQuestListPatch : ModulePatch
    {
        private static readonly FieldInfo Available = AccessTools.Field(typeof(BotJobAssignmentCreationJob), "availableQuests");
        private static readonly FieldInfo Next = AccessTools.Field(typeof(BotJobAssignmentCreationJob), "_nextRandomQuest");

        public static int Skipped { get; private set; }

        public static void ClearCounters() => Skipped = 0;

        public static string Summary => $"QuestingBots quest choices on an empty list skipped {Skipped}";

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotJobAssignmentCreationJob), "ChooseNextRandomQuest");

        [PatchPostfix]
        protected static void PatchPostfix(BotJobAssignmentCreationJob __instance, ref System.Collections.IEnumerator __result)
        {
            try
            {
                if (Available?.GetValue(__instance) is System.Collections.ICollection list && list.Count == 0)
                {
                    Next?.SetValue(__instance, null);
                    __result = OneFrame();
                    Skipped++;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbEmptyQuestListPatch", e);
            }
        }

        private static System.Collections.IEnumerator OneFrame()
        {
            yield return null;
        }
    }

    /// <summary>QuestingBots gives up finding a quest for a bot and stops its questing: not because of FreshSteps.</summary>
    public class QbGiveUpQuestingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotJobAssignmentCreationJob), "StopQuestingAndExtract");

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____botOwner)
        {
            try
            {
                return FreshSteps.LetGiveUp(____botOwner);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbGiveUpQuestingPatch", e);
                return true;
            }
        }
    }

    /// <summary>
    /// The pull of the depths (F12 -> 3 "Pull of the depths", 1.4.0): bunkers, basements, tunnels. Raid 2026-11-10 12:37 (Reserve): of 286 spots nobody reached,
    /// 77 lay 7-17 m under the ground (the bunkers under the base: 36 + 32 + 33 spots in three squares), and 63 times a bot passed right over a spot it never went
    /// down to. The upper floors have their pull (VerticalLedger); what lies under the ground had none - and its way in is one stair or one door a bot seldom takes
    /// by chance.
    ///
    /// The ground level is taken per 50 m square: the median height of the open-ground spots of the square and its neighbours (all spots where it has none). A spot
    /// 3.5 m or more under it is "deep". A quest with a third or more of its spots deep weighs more for a bot, the more of them are still fresh: up to x2.3 at full
    /// strength, to each bot's own appetite (explorers, looters and rats go down, snipers and campers seldom) - and never once the depths are gone through.
    /// </summary>
    public static class Depths
    {
        private const float Cell = 50f;
        private const float Under = 3.5f;

        private static readonly Dictionary<(int, int), float> _ground = new Dictionary<(int, int), float>();
        private static readonly Dictionary<string, float> _share = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _appetite = new Dictionary<string, float>();
        private static bool _built;
        private static int _deepSpots;

        public static int Asked { get; private set; }
        public static int Favoured { get; private set; }

        public static bool On => Settings.Enabled.Value && Settings.DepthPull.Value && Settings.DepthStrength.Value > 0;

        public static string Summary => !_built
            ? "depths: not used"
            : $"depths: {_deepSpots} spots under the ground, {_share.Count(kv => kv.Value >= 0.34f)} quests go down; quests weighed {Asked} (favoured {Favoured})";

        public static void Clear()
        {
            _ground.Clear();
            _share.Clear();
            _appetite.Clear();
            _built = false;
            _deepSpots = 0;
            _freshCheckedAt = -100f;
            _freshDeep = false;
            Asked = Favoured = 0;
        }

        private static (int, int) Key(float x, float z) => (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        private static bool Build()
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null || !intel.Ready || intel.Pois.Count < 20)
            {
                return false;
            }

            var open = new Dictionary<(int, int), List<float>>();
            var all = new Dictionary<(int, int), List<float>>();
            foreach (Poi poi in intel.Pois)
            {
                (int, int) k = Key(poi.X, poi.Z);
                Add(all, k, poi.Y);
                if (poi.Kind == PoiKind.Open)
                {
                    Add(open, k, poi.Y);
                }
            }

            foreach ((int, int) k in all.Keys)
            {
                List<float> ys = Around(open, k);
                if (ys.Count < 3)
                {
                    ys = Around(all, k);
                }

                ys.Sort();
                _ground[k] = ys[ys.Count / 2];
            }

            _deepSpots = intel.Pois.Count(p => IsDeep(p.Pos));
            _built = true;
            Log.Detail("info", $"LR Depths: {_deepSpots} of {intel.Pois.Count} spots lie {Under:0.#} m or more under the ground of their part of the map");
            return true;
        }

        private static void Add(Dictionary<(int, int), List<float>> map, (int, int) k, float y)
        {
            if (!map.TryGetValue(k, out List<float> list))
            {
                map[k] = list = new List<float>();
            }

            list.Add(y);
        }

        private static List<float> Around(Dictionary<(int, int), List<float>> map, (int, int) k)
        {
            List<float> ys = new List<float>();
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (map.TryGetValue((k.Item1 + dx, k.Item2 + dz), out List<float> list))
                    {
                        ys.AddRange(list);
                    }
                }
            }

            return ys;
        }

        private static float _freshCheckedAt = -100f;
        private static bool _freshDeep;

        /// <summary>There is a quest that goes down whose ground is still mostly unseen (looked at every 20 s): an aim for RaidAgenda.</summary>
        public static bool HasFreshDepths()
        {
            if (!On || (!_built && !Build()) || _deepSpots == 0)
            {
                return false;
            }

            if (Time.time - _freshCheckedAt < 20f)
            {
                return _freshDeep;
            }

            _freshCheckedAt = Time.time;
            _freshDeep = false;
            foreach (string name in QuestFactory.QuestPois.Keys)
            {
                if (QuestDeepShare(name) >= 0.34f && CoverageLedger.Freshness(name) >= 0.4f)
                {
                    _freshDeep = true;
                    break;
                }
            }

            return _freshDeep;
        }

        public static bool IsDeep(Vector3 p) => _ground.TryGetValue(Key(p.x, p.z), out float ground) && p.y <= ground - Under;

        /// <summary>The share of the quest's spots that lie under the ground (0 when the quest is not an exploration quest of LiveRaid).</summary>
        public static float QuestDeepShare(string name)
        {
            if (name == null || (!_built && !Build()))
            {
                return 0f;
            }

            if (_share.TryGetValue(name, out float known))
            {
                return known;
            }

            float share = 0f;
            if (QuestFactory.QuestPois.TryGetValue(name, out List<Poi> pois) && pois.Count > 0)
            {
                share = pois.Count(p => IsDeep(p.Pos)) / (float)pois.Count;
            }

            _share[name] = share;
            return share;
        }

        private static float Appetite(BotOwner bot, BotProfile p)
        {
            if (_appetite.TryGetValue(bot.ProfileId, out float a))
            {
                return a;
            }

            switch (p.Style)
            {
                case PlayStyle.Explorer: a = 1.35f; break;
                case PlayStyle.Looter: a = 1.2f; break;
                case PlayStyle.Rat: a = 1.15f; break;
                case PlayStyle.Quester: a = 1f; break;
                case PlayStyle.Hunter:
                case PlayStyle.Rusher: a = 0.85f; break;
                case PlayStyle.Camper: a = 0.6f; break;
                default: a = 0.5f; break;
            }

            // Nobody is the same: some never go down, some love it
            a *= 0.5f + FreshSteps.Roll(bot.ProfileId, 77, 0);
            if (p.Packrat)
            {
                a = Mathf.Max(a, 1.1f);
            }

            _appetite[bot.ProfileId] = a;
            return a;
        }

        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On || bot == null || p == null || p.Kind == BotKind.Other)
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (!name.StartsWith(QuestFactory.ExplorePrefix))
            {
                return 1f;
            }

            float share = QuestDeepShare(name);
            if (share < 0.34f)
            {
                return 1f;
            }

            float fresh = CoverageLedger.Freshness(name);
            if (fresh < 0f)
            {
                fresh = 0.5f;
            }

            float m = 1f + Settings.DepthStrength.Value / 100f * Mathf.Clamp01(share + 0.2f) * Appetite(bot, p) * (0.15f + 1.1f * fresh);
            m = Mathf.Clamp(m, 1f, 2.3f);
            Asked++;
            if (m > 1.2f)
            {
                Favoured++;
            }

            return m;
        }
    }
}
