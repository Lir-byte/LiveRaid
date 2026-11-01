using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Raid;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Places only a route leads up to (the cliff on Ground Zero): few players climb there in a raid, so only a few bots may be sent.
    /// An objective is "up the route" when one of its points lies around the upper end of a linked route (25 m, not below the end).
    /// QuestingBots asks <c>BotQuestObjective.CanAssignBot</c> for every candidate objective - once the quota of different bots
    /// is used up, nobody new gets such an objective (EFT quests and LiveRaid exploration alike); a bot that already got one keeps it.
    /// The count is taken from real assignments (<c>BotJobAssignment</c>), not from the checks. Bosses, marksmen and you do not count.
    /// </summary>
    public static class RouteAreaQuota
    {
        private const float AreaRadius = 25f;
        private const float BelowTop = 1.5f;

        private static readonly Dictionary<string, HashSet<string>> _sent = new Dictionary<string, HashSet<string>>();
        private static readonly Dictionary<BotQuestObjective, string> _gate = new Dictionary<BotQuestObjective, string>();

        public static void Clear()
        {
            _sent.Clear();
            _gate.Clear();
        }

        /// <summary>The route whose upper end this point lies at, or null.</summary>
        public static Route RouteAbove(Vector3 point)
        {
            foreach (Route route in Routes.Active)
            {
                if (!route.Linked)
                {
                    continue;
                }

                Vector3 top = route.End.y >= route.Start.y ? route.End : route.Start;
                Vector3 flat = point - top;
                flat.y = 0f;
                if (point.y >= top.y - BelowTop && flat.sqrMagnitude <= AreaRadius * AreaRadius)
                {
                    return route;
                }
            }

            return null;
        }

        private static string GateOf(BotQuestObjective objective)
        {
            if (objective == null)
            {
                return null;
            }

            if (_gate.TryGetValue(objective, out string id))
            {
                return id;
            }

            // Routes are linked when the raid's map intel is ready; an answer from before that would stick for the whole raid
            if (!Routes.Active.Exists(r => r.Linked))
            {
                return null;
            }

            id = null;
            foreach (Vector3? p in objective.GetAllPositions())
            {
                Route route = p.HasValue ? RouteAbove(p.Value) : null;
                if (route != null)
                {
                    id = route.Id;
                    break;
                }
            }

            _gate[objective] = id;
            return id;
        }

        private static bool Counts(BotOwner bot)
        {
            BotKind kind = BotProfiles.Get(bot)?.Kind ?? BotKind.Other;
            return kind != BotKind.Other;
        }

        public static bool Allowed(BotOwner bot, BotQuestObjective objective)
        {
            string id = GateOf(objective);
            if (id == null || bot == null || !Counts(bot))
            {
                return true;
            }

            HashSet<string> sent = _sent.TryGetValue(id, out HashSet<string> s) ? s : null;
            return sent == null || sent.Contains(bot.ProfileId) || sent.Count < Settings.RouteAreaBots.Value;
        }

        public static void OnAssigned(BotOwner bot, BotQuest quest, BotQuestObjective objective)
        {
            string id = GateOf(objective);
            if (id == null || bot == null || !Counts(bot))
            {
                return;
            }

            if (!_sent.TryGetValue(id, out HashSet<string> sent))
            {
                _sent[id] = sent = new HashSet<string>();
            }

            if (sent.Add(bot.ProfileId))
            {
                Log.Info($"LR Route: {bot.Profile.Nickname} is sent up route {id} for \"{quest?.GetName()}\" ({sent.Count}/{Settings.RouteAreaBots.Value} this raid)");
            }
        }
    }

    public class QbRouteAreaCheckPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuestObjective), nameof(BotQuestObjective.CanAssignBot));

        [PatchPostfix]
        protected static void PatchPostfix(BotQuestObjective __instance, BotOwner bot, ref bool __result)
        {
            try
            {
                if (__result && !RouteAreaQuota.Allowed(bot, __instance))
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("QbRouteAreaCheckPatch", e);
            }
        }
    }

    public class QbRouteAreaAssignPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(BotJobAssignment), new[] { typeof(BotOwner), typeof(BotQuest), typeof(BotQuestObjective) });

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner bot, BotQuest quest, BotQuestObjective objective) =>
            Diag.Guard("QbRouteAreaAssignPatch", () => RouteAreaQuota.OnAssigned(bot, quest, objective));
    }

    public class QbRouteAreaAssignStepPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(BotJobAssignment), new[] { typeof(BotOwner), typeof(BotQuest), typeof(BotQuestObjective), typeof(BotQuestObjectiveStep) });

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner bot, BotQuest quest, BotQuestObjective objective) =>
            Diag.Guard("QbRouteAreaAssignPatch", () => RouteAreaQuota.OnAssigned(bot, quest, objective));
    }
}
