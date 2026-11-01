using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using QuestingBots.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Do the bots really do their quests, alone and in a squad? QuestingBots logs none of it unless its verbose logging is on, and the trace shows only
    /// the name of the quest a bot holds, which says nothing about steps done. The ledger counts, per bot and per kind (solo / leader / follower),
    /// the objectives a bot was given, the steps it completed, the ones it failed (and why, as far as QuestingBots tells) and the doors unlocked.
    /// Observation only. Lines: trace "quest" category (step done / failed), the totals in "LR Health on" and at the end of a raid "LR Quests done".
    /// </summary>
    public static class QuestLedger
    {
        private class Entry
        {
            public string Name;
            public BotKind Kind;
            public string Role = "solo";
            public int Assigned;
            public int Done;
            public int Failed;
            public int Unlocked;
            public float LastStepAt;
            public float TotalStepSeconds;
            public string LastQuest;
        }

        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private static readonly FieldInfo BotOwnerField = AccessTools.Field(typeof(BotObjectiveManager), "botOwner");

        public static void Clear() => _entries.Clear();

        internal static BotOwner OwnerOf(BotObjectiveManager manager) => BotOwnerField?.GetValue(manager) as BotOwner;

        private static Entry Of(BotObjectiveManager manager, out BotOwner bot)
        {
            bot = BotOwnerField?.GetValue(manager) as BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other)
            {
                return null;
            }

            if (!_entries.TryGetValue(bot.ProfileId, out Entry e))
            {
                _entries[bot.ProfileId] = e = new Entry { Name = p.Name, Kind = p.Kind, LastStepAt = Time.time };
            }

            int members = bot.BotsGroup?.MembersCount ?? 1;
            e.Role = members <= 1 ? "solo" : (bot.BotFollower != null && bot.BotFollower.HaveBoss ? "follower" : "leader");
            return e;
        }

        internal static void Assigned(BotObjectiveManager manager)
        {
            Entry e = Of(manager, out _);
            if (e != null)
            {
                e.Assigned++;
                e.LastStepAt = Time.time;
            }
        }

        internal static void Completed(BotObjectiveManager manager)
        {
            Entry e = Of(manager, out BotOwner bot);
            if (e == null)
            {
                return;
            }

            string quest = manager.CurrentAssignment?.QuestAssignment?.GetName() ?? "?";
            float took = Time.time - e.LastStepAt;
            e.Done++;
            e.TotalStepSeconds += took;
            e.LastStepAt = Time.time;
            e.LastQuest = quest;
            Log.Detail("quest", $"{e.Name} ({e.Kind}, {e.Role}) completed a step of \"{quest}\" after {took:0} s");
        }

        internal static void Failed(BotObjectiveManager manager)
        {
            Entry e = Of(manager, out BotOwner bot);
            if (e == null)
            {
                return;
            }

            string quest = manager.CurrentAssignment?.QuestAssignment?.GetName() ?? "?";
            float away = manager.DistanceToObjective;
            double took = manager.CurrentAssignment?.TimeSinceStarted() ?? 0.0;
            if (took < 15f && !float.IsNaN(away) && away > 5f)
            {
                ShutIn.PathFailed(bot, $"quest step \"{quest}\" {away:0} m away");
            }

            e.Failed++;
            e.LastStepAt = Time.time;
            Log.Info($"LR Quest: {e.Name} ({e.Kind}, {e.Role}) gave up a step of \"{quest}\" after {manager.CurrentAssignment?.TimeSinceStarted() ?? 0:0} s" +
                     $"{(float.IsNaN(away) ? "" : $", {away:0} m from it")}");
        }

        internal static void DoorUnlocked(BotObjectiveManager manager)
        {
            Entry e = Of(manager, out _);
            if (e != null)
            {
                e.Unlocked++;
                Log.Info($"LR Quest: {e.Name} ({e.Kind}, {e.Role}) unlocked a door with its key");
            }
        }

        public static string Summary()
        {
            if (_entries.Count == 0)
            {
                return "no quest data";
            }

            string Part(string role)
            {
                var rows = _entries.Values.Where(x => x.Role == role).ToList();
                return $"{role}: {rows.Count} bots, {rows.Sum(x => x.Assigned)} objectives given, {rows.Sum(x => x.Done)} steps done, {rows.Sum(x => x.Failed)} given up";
            }

            int unlocked = _entries.Values.Sum(x => x.Unlocked);
            int idle = _entries.Values.Count(x => x.Done == 0);
            float seconds = _entries.Values.Sum(x => x.TotalStepSeconds);
            int steps = _entries.Values.Sum(x => x.Done);
            return $"{Part("solo")}; {Part("leader")}; {Part("follower")}; doors unlocked {unlocked}; bots that finished no step at all {idle} of {_entries.Count}; " +
                   $"{(steps > 0 ? $"on average {seconds / steps:0} s per step" : "no steps")}";
        }

        public static void ReportEnd()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            Log.Info("LR Quests done: " + Summary());
            foreach (Entry e in _entries.Values.OrderByDescending(x => x.Done).Take(40))
            {
                Log.Detail("quest", $"{e.Name} ({e.Kind}, {e.Role}): {e.Assigned} given, {e.Done} done, {e.Failed} given up, {e.Unlocked} doors");
            }
        }
    }

    public class QuestLedgerCompletePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.CompleteObjective));

        // After the original: a step the simulation postponed (the bot still acts it out) is not counted yet, the real completion is
        [PatchPostfix]
        protected static void PatchPostfix(BotObjectiveManager __instance)
        {
            try
            {
                if (!Bots.QuestTasks.PendingFor(__instance))
                {
                    QuestLedger.Completed(__instance);
                }
            }
            catch (Exception e)
            {
                Diag.Report("QuestLedgerCompletePatch", e);
            }
        }
    }

    public class QuestLedgerFailPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.FailObjective));

        [PatchPrefix]
        protected static void PatchPrefix(BotObjectiveManager __instance)
        {
            try
            {
                QuestLedger.Failed(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("QuestLedgerFailPatch", e);
            }
        }
    }

    public class QuestLedgerAssignPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.SetObjective));

        [PatchPostfix]
        protected static void PatchPostfix(BotObjectiveManager __instance)
        {
            try
            {
                QuestLedger.Assigned(__instance);
                Bots.QuestDrive.OnAssigned(__instance, QuestLedger.OwnerOf(__instance));
            }
            catch (Exception e)
            {
                Diag.Report("QuestLedgerAssignPatch", e);
            }
        }
    }

    public class QuestLedgerUnlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.DoorIsUnlocked));

        [PatchPostfix]
        protected static void PatchPostfix(BotObjectiveManager __instance)
        {
            try
            {
                QuestLedger.DoorUnlocked(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("QuestLedgerUnlockPatch", e);
            }
        }
    }
}
