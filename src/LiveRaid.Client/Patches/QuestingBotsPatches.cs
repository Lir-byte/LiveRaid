using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using UnityEngine;
using QuestingBots.BotLogic.Objective;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots normally rolls a chance and conjures the key out of thin air.
    /// With real keys, a bot may unlock only what its inventory can open (quests that force an unlock, e.g. Labyrinth, keep working).
    /// </summary>
    public class QbAllowUnlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AbstractWorldInteractiveObjectInteractionAction), "IsWorldInteractiveObjectAllowedToBeUnlocked");

        [PatchPrefix]
        protected static bool PatchPrefix(AbstractWorldInteractiveObjectInteractionAction __instance, ref bool __result)
        {
            try
            {
                return RunPrefix(__instance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbAllowUnlockPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(AbstractWorldInteractiveObjectInteractionAction __instance, ref bool __result)
        {
            if (!Settings.RealKeys.Value)
            {
                return true;
            }

            BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
            BotObjectiveManager objectives = bot?.GetObjectiveManager();
            if (objectives != null && objectives.ForceUnlock)
            {
                return true;
            }

            WorldInteractiveObject target = Traverse.Create(__instance).Property("DesiredWorldInteractiveObject").GetValue<WorldInteractiveObject>();
            __result = bot != null && target != null && BotSetup.HasKeyFor(bot, target.KeyId);
            if (!__result)
            {
                Log.Debug($"{bot?.Profile?.Nickname} has no key for {target?.Id}, not trying");
            }

            return false;
        }
    }

    /// <summary>
    /// When the path to an objective is blocked, QuestingBots picks the nearest locked door and tries it.
    /// Here the bot only considers doors whose key it actually carries.
    /// </summary>
    public class QbFindLockedDoorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.Objective.GoToObjectiveAction"), "tryFindLockedDoorToOpen");

        [PatchPrefix]
        protected static bool PatchPrefix(object __instance, float searchDistance, ref bool __result)
        {
            try
            {
                return RunPrefix(__instance, searchDistance, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbFindLockedDoorPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(object __instance, float searchDistance, ref bool __result)
        {
            if (!Settings.RealKeys.Value)
            {
                return true;
            }

            BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
            BotObjectiveManager objectives = bot?.GetObjectiveManager();
            LocationData location = Singleton<GameWorld>.Instance?.GetComponent<LocationData>();
            __result = false;
            if (objectives?.Position == null || location == null)
            {
                return false;
            }

            HashSet<string> keys = new HashSet<string>(BotSetup.OwnedKeyIds(bot));
            if (keys.Count == 0)
            {
                return false;
            }

            List<WorldInteractiveObject> openable = location
                .FindLockedDoorsNearPosition(objectives.Position.Value, searchDistance)
                .Where(d => keys.Contains(d.KeyId))
                .ToList();
            if (openable.Count == 0)
            {
                return false;
            }

            WorldInteractiveObject door = location.FindFirstAccessibleDoor(openable, bot.Position);
            if (door == null)
            {
                return false;
            }

            Log.Debug($"{bot.Profile.Nickname} will open {door.Id} with its own key");
            objectives.UnlockDoor(door);
            __result = true;
            return false;
        }
    }

    /// <summary>Adds LiveRaid's generated exploration and locked-room quests to the map's custom quests.</summary>
    public class QbCustomQuestsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Utils.ConfigUtil"), "GetCustomQuests");

        [PatchPostfix]
        protected static void PatchPostfix(ref IEnumerable<BotQuest> __result)
        {
            try
            {
                RunPostfix(ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbCustomQuestsPatch", e);
            }
        }

        private static void RunPostfix(ref IEnumerable<BotQuest> __result)
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null)
            {
                return;
            }

            try
            {
                intel.CompletePois();
                List<BotQuest> extra = QuestFactory.Build(intel);
                __result = (__result ?? Enumerable.Empty<BotQuest>()).Concat(extra).ToList();
                Log.Info($"added {extra.Count(q => q.GetName().StartsWith(QuestFactory.ExplorePrefix))} exploration and " +
                         $"{extra.Count(q => q.GetName().StartsWith(QuestFactory.LockedPrefix))} locked-room quests on {intel.LocationId}");
            }
            catch (Exception e)
            {
                Log.Error("quest generation failed: " + e);
            }
        }
    }

    /// <summary>Who may take which quest: locked rooms need the key, scavs don't do PMC tasks.</summary>
    public class QbCanAssignBotPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuest), nameof(BotQuest.CanAssignBot));

        [PatchPostfix]
        protected static void PatchPostfix(BotQuest __instance, BotOwner bot, ref bool __result)
        {
            try
            {
                RunPostfix(__instance, bot, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbCanAssignBotPatch", e);
            }
        }

        private static void RunPostfix(BotQuest __instance, BotOwner bot, ref bool __result)
        {
            if (!__result || bot == null)
            {
                return;
            }

            string name = __instance.GetName() ?? "";
            if (name.StartsWith(QuestFactory.LockedPrefix))
            {
                string doorId = name.Substring(QuestFactory.LockedPrefix.Length);
                MapIntel intel = MapIntel.Current;
                __result = intel != null
                           && intel.SpotsById.TryGetValue(doorId, out LockedSpot spot)
                           && spot.StillLocked
                           && BotSetup.HasKeyFor(bot, spot.KeyId);
                return;
            }

            BotProfile profile = BotProfiles.Get(bot);

            // Bosses, followers, marksmen, raiders stay vanilla: none of LiveRaid's quests (a marksman was handed "LR Explore")
            if ((profile == null || profile.Kind == BotKind.Other) && (name.StartsWith(QuestFactory.ExplorePrefix) || KeyPlaces.IsKeyPlaceQuest(name)))
            {
                __result = false;
                return;
            }

            // No camping or sniping spots (QuestingBots holds the bot there for minutes): players move
            if (profile != null && profile.Kind != BotKind.Other && (__instance.IsCamping || __instance.IsSniping))
            {
                __result = false;
                return;
            }

            if (profile?.Kind == BotKind.Scav)
            {
                bool lrQuest = name.StartsWith(QuestFactory.ExplorePrefix) || KeyPlaces.IsKeyPlaceQuest(name);
                if (ScavPatrol.On)
                {
                    // Patrols of their area (ScavPatrol): any quest with something to do in the area, none of the PMC things
                    __result = !__instance.IsEFTQuest && !__instance.IsSniping && !name.StartsWith("Spawn Rush") && !name.StartsWith("Boss Hunter")
                               && !KeyPlaces.IsKeyPlaceQuest(name) && ScavPatrol.HasWork(bot, __instance);
                }
                else if (!Settings.ScavsQuest.Value && lrQuest)
                {
                    __result = false;
                }
                else if (__instance.IsEFTQuest || __instance.IsSniping || name.StartsWith("Spawn Rush") || name.StartsWith("Boss Hunter"))
                {
                    __result = false;
                }
            }
        }
    }

    /// <summary>Each bot weighs quests by its character: campers love camping spots, looters love loot, questers love tasks.</summary>
    public class QbDesirabilityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotJobAssignmentCreationJob), "DesirabilityMultiplier");

        [PatchPostfix]
        protected static void PatchPostfix(BotQuest quest, BotOwner ____botOwner, ref float __result)
        {
            try
            {
                RunPostfix(quest, ____botOwner, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbDesirabilityPatch", e);
            }
        }

        private static void RunPostfix(BotQuest quest, BotOwner ____botOwner, ref float __result)
        {
            if (!Settings.Personalities.Value || ____botOwner == null)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(____botOwner);
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            // A squad leader picks quests the whole squad likes, so followers' tasks get done too
            HashSet<string> squad = LootBrain.SquadIds(____botOwner);
            float mult = Multiplier(p, quest, squad);
            string qn = quest.GetName() ?? "";

            // A key found in the raid: the room it opens is wanted (FoundKeys)
            if (qn.StartsWith(QuestFactory.LockedPrefix) && MapIntel.Current != null
                && MapIntel.Current.SpotsById.TryGetValue(qn.Substring(QuestFactory.LockedPrefix.Length), out LockedSpot locked))
            {
                mult *= FoundKeys.Multiplier(____botOwner, locked.KeyId);
                // Its own key, its own errand (KeyErrand): the carrier's door weighs more than the other quests
                if (Bots.BotSetup.HasKeyFor(____botOwner, locked.KeyId))
                {
                    mult *= Bots.KeyErrand.Multiplier(____botOwner, locked);
                }
            }

            // An airdrop: storm squads and greedy ones run for it, the quiet ones mostly let it be (the doctrine)
            DoctrineDeck.Member doctrine = SquadDoctrines.Of(____botOwner);
            if (qn.IndexOf("Airdrop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (Settings.AirdropEvent.Value)
                {
                    // An event, not a wish: the near squads that can make it and want it go, the rest leave it (AirdropEvent)
                    mult *= AirdropEvent.Multiplier(____botOwner, quest);
                }
                else if (doctrine != null)
                {
                    mult *= 0.4f + 0.7f * doctrine[DoctrineDeck.Greed] + 0.6f * doctrine[DoctrineDeck.ThirdParty];
                }
            }
            List<BotOwner> followers = ____botOwner.Boss?.Followers;
            if (Settings.SquadTactics.Value && followers != null && followers.Count > 0 && SquadBrain.DisciplineOf(____botOwner) >= 0.35f)
            {
                int n = 1;
                foreach (BotOwner f in followers)
                {
                    BotProfile fp = f != null && !f.IsDead ? BotProfiles.Get(f) : null;
                    if (fp != null && fp.Kind != BotKind.Other)
                    {
                        mult += Multiplier(fp, quest, squad);
                        n++;
                    }
                }

                mult /= n;
            }

            // Restless after loitering in one place: the rest of the map calls (RoamPlanner)
            mult *= RoamPlanner.Multiplier(____botOwner, quest);

            // A hot-dropper (the leader's role decides for the squad) runs for the richest place first
            mult *= HotDrop.Multiplier(p, quest);

            // A third of the PMC squads make a key place (the dorms, a bunker...) their goal (KeyPlaces)
            mult *= KeyPlaces.Multiplier(____botOwner, p, quest);

            // A PMC with a prize key in its secure container runs for that place at its moment (PrizeKeys)
            mult *= PrizeKeys.Multiplier(____botOwner, quest);

            // The bots spread over the map: a quest in a crowded part is wanted less, in an empty one more (MapBalance)
            mult *= MapBalance.Multiplier(____botOwner, p, quest);

            // A region the people's corridors run through is wanted a little more (Corridors)
            mult *= Corridors.Multiplier(p, quest.GetName());

            // Squads spread over the map: a quest other PMC squads hold right now is wanted less (QuestDrive)
            string qname = quest.GetName() ?? "";
            mult *= QuestDrive.SpreadMultiplier(____botOwner, qname, KeyPlaces.TryGet(qname, out _));

            // Ground nobody has visited yet is wanted more, ground just visited less (CoverageLedger)
            mult *= CoverageLedger.Multiplier(p, quest);
            // The aims of the bot's raid (RaidAgenda) and the pull of the upper floors (VerticalLedger)
            mult *= RaidAgenda.Multiplier(____botOwner, p, quest);
            mult *= VerticalLedger.Multiplier(____botOwner, p, quest);
            // What lies under the ground (Depths) and where people have just died (DangerMemory), each bot to its own character (1.4.0)
            mult *= Depths.Multiplier(____botOwner, p, quest);
            mult *= DangerMemory.Multiplier(____botOwner, p, quest);
            // The packrat: the near and rich quests first (1.3.0)
            mult *= PackratVacuum.Multiplier(____botOwner, p, quest);
            __result *= Math.Max(0.02f, mult);
        }

        private static float Multiplier(BotProfile p, BotQuest quest, HashSet<string> squad)
        {
            string name = quest.GetName() ?? "";
            if (name.StartsWith(QuestFactory.LockedPrefix))
            {
                return 0.8f + p.LootGreed * 0.6f;
            }

            if (KeyPlaces.TryGet(name, out KeyPlaces.Place place))
            {
                // A key place is a loot place: looters want it, and a place other squads have just emptied is worth less
                int px = Mathf.FloorToInt(place.Center.x / Settings.RoamRegionSize.Value);
                int pz = Mathf.FloorToInt(place.Center.z / Settings.RoamRegionSize.Value);
                return (0.35f * p.Explore + 0.65f * p.LootGreed) * RegionMemory.Freshness(p.ProfileId, px, pz, squad) * (0.75f + 0.1f * (int)p.Skill);
            }

            if (name.StartsWith(QuestFactory.ExplorePrefix))
            {
                float baseWish = 0.5f * p.Explore + 0.5f * p.LootGreed;
                if (!QuestFactory.TryParseRegion(name, out int rx, out int rz))
                {
                    return baseWish;
                }

                // Places other squads have just walked through are probably looted; places I just cleaned out I skip
                float fresh = RegionMemory.Freshness(p.ProfileId, rx, rz, squad);

                // Hot zones pull looters; explorers care less about what is in them
                QuestFactory.Heat.TryGetValue(name, out float heat);
                float heatWeight = Mathf.Clamp(0.25f + 0.5f * p.LootGreed, 0.2f, 1.1f);
                float pull = Settings.HotZones.Value ? Mathf.Max(0.2f, 1f + heatWeight * (heat - 0.4f) * 2f) : 1f;
                // Newcomers shy away from the richest (most contested) places, veterans go for them
                pull *= 0.75f + 0.1f * (int)p.Skill;

                // In the first minutes the aggressive and the greedy run straight for the rich places, like online rushes to the dorms
                float rush = 1f;
                if (Settings.HotZones.Value && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav) && heat >= 0.75f)
                {
                    float elapsed = QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds();
                    if (elapsed < 420f)
                    {
                        rush += 1.2f * (0.5f * p.LootGreed + 0.5f * p.Aggression) * (1f - elapsed / 420f);
                    }
                }

                return baseWish * fresh * pull * rush;
            }

            if (quest.IsSniping)
            {
                return p.Snipe;
            }

            if (quest.IsCamping)
            {
                return p.Camp;
            }

            if (quest.IsEFTQuest)
            {
                return p.QuestFocus;
            }

            if (name.StartsWith("Spawn Rush") || name.StartsWith("Boss Hunter"))
            {
                return 0.4f + p.Aggression * 1.6f;
            }

            if (name.IndexOf("Airdrop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0.5f * p.LootGreed + 0.7f * p.Aggression;
            }

            return 1f;
        }
    }

    /// <summary>
    /// A bot on its way to a "stand and look round" step of a LiveRaid quest may take a break like on any other step (built in, a fix).
    ///
    /// The open-ground steps of the exploration, sweep, locked-room and Studio-path quests are HoldAtPosition steps (the bot stops and looks about for a few
    /// seconds). QuestingBots' BotObjectiveManager.IsAllowedToTakeABreak answers "no" for that kind of step - not only during the hold, for the whole walk to
    /// it - and without a break the bot may loot only inside the 7-second window of a forced scan (BotQuestingDecisionMonitor: "IsForcedToSearchForLoot",
    /// everything else is behind allowedToTakeABreak). Raid 2026-11-10 11:43 (Customs): 144 "Looting -> BotObjectiveLayer (0.1 s) -> Looting" flips, every
    /// one of a bot with an "LR Explore S.." sweep quest (they are nearly all open ground); each flip stopped LootingBots' job ("cut short from outside" after
    /// 3-6 s). Радик Подзаборный lay down at its squadmate's body and got up again every 7 s for eight minutes; rooms were left after one thing. The same
    /// "no" kept QuestingBots from noticing that such a bot wants to extract, from regrouping it with its squad and from waiting for a squad in a fight.
    /// The hold itself is QuestingBots' action and is not touched: a bot standing at the spot that goes for a thing it sees comes back to the step after it.
    /// </summary>
    public class QbBreakOnTheWayPatch : ModulePatch
    {
        public static int Allowed { get; private set; }

        public static void ClearCounters() => Allowed = 0;

        public static string Summary => $"breaks (loot, squad, exit) allowed on the way to a stand-and-look step {Allowed} ticks";

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.IsAllowedToTakeABreak));

        [PatchPostfix]
        protected static void PatchPostfix(BotObjectiveManager __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            try
            {
                if (!Settings.Enabled.Value || __instance.CurrentQuestAction != QuestAction.HoldAtPosition)
                {
                    return;
                }

                string name = __instance.CurrentAssignment?.QuestAssignment?.GetName();
                if (name != null && name.StartsWith("LR "))
                {
                    __result = true;
                    Allowed++;
                }
            }
            catch (Exception e)
            {
                Diag.Report("QbBreakOnTheWayPatch", e);
            }
        }
    }

    /// <summary>
    /// QuestingBots sends a bot that heard something to look ("Investigate"). The packrat keeps out of fights - raid 2026-11-10 17:22 (Reserve):
    /// Chad_Thundercock walked to the shots in QuestingBots' investigation layer and was killed there ten seconds later. Built in, with the packrat itself.
    /// </summary>
    public class QbNoInvestigatePatch : ModulePatch
    {
        private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(BotObjectiveManager), "botOwner");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.IsAllowedToInvestigate));

        [PatchPostfix]
        protected static void PatchPostfix(BotObjectiveManager __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            try
            {
                BotOwner bot = OwnerField?.GetValue(__instance) as BotOwner;
                BotProfile p = bot == null ? null : BotProfiles.Get(bot);
                if (p != null && p.Packrat && Settings.Enabled.Value)
                {
                    __result = false;
                    PackratVacuum.NoiseWalksRefused++;
                }
            }
            catch (Exception e)
            {
                Diag.Report("QbNoInvestigatePatch", e);
            }
        }
    }
}
