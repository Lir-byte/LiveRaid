using System;
using System.Collections.Generic;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    public enum QuestTaskKind
    {
        TakeItem,
        Plant,
        Visit,
    }

    /// <summary>What a PMC does on the spot of an EFT quest objective before QuestingBots counts it as done.</summary>
    public class QuestTask
    {
        public BotJobAssignment Assignment;
        public QuestTaskKind Kind;
        public Vector3 Stand;
        public Vector3 Look;
        public float Duration;
        public string Quest;
        public string What;

        // Set when the bot is on the spot and starts acting
        public float ActingSince = -1;
        public float GiveUpAt;
        public float NextLook;
        public bool AnimationPlayed;
    }

    /// <summary>
    /// Online, a player doing a quest walks up, picks the item up, plants the marker or looks around the place, then leaves.
    /// QuestingBots only walks bots to the point and waits. This makes the PMC act it out: at a quest item it crouches, looks at it and
    /// plays the pick-up animation (the item itself stays: quest items belong to the player who has the quest); at a plant/marker zone it
    /// crouches and plants for the quest's plant time; in a zone to visit it looks around. Then the objective is completed.
    /// </summary>
    public static class QuestTasks
    {
        private static readonly Dictionary<string, QuestTask> _tasks = new Dictionary<string, QuestTask>();
        private static readonly HashSet<BotJobAssignment> _acted = new HashSet<BotJobAssignment>();

        // True while LiveRaid itself completes the objective, so the patch lets it through
        internal static bool Completing;

        public static void Clear()
        {
            _tasks.Clear();
            _acted.Clear();
        }

        public static bool TryGet(BotOwner bot, out QuestTask task) => _tasks.TryGetValue(bot.ProfileId, out task);

        /// <summary>True while the bot is acting out the step it just "completed" (QuestingBots' completion was held back): not a finished step yet.</summary>
        internal static bool PendingFor(BotObjectiveManager manager)
        {
            if (Completing || manager?.CurrentAssignment == null)
            {
                return false;
            }

            foreach (QuestTask t in _tasks.Values)
            {
                if (t.Assignment == manager.CurrentAssignment)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Called instead of the first completion of an EFT objective; returns true when the bot will act it out first.</summary>
        public static bool TryStart(BotOwner bot, BotObjectiveManager objectives)
        {
            BotJobAssignment a = objectives?.CurrentAssignment;
            if (bot == null || a?.QuestAssignment == null || !a.QuestAssignment.IsEFTQuest || _acted.Contains(a) || !a.Position.HasValue)
            {
                return false;
            }

            if (_tasks.TryGetValue(bot.ProfileId, out QuestTask existing) && existing.Assignment == a)
            {
                return true;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind != BotKind.Pmc)
            {
                return false;
            }

            QuestTask task = Describe(a, objectives);
            if (task == null)
            {
                return false;
            }

            task.GiveUpAt = Time.time + 120f;
            _tasks[bot.ProfileId] = task;
            Log.Info($"LR Quest: {p.Name} {Verb(task.Kind)} {task.What} for \"{task.Quest}\" ({task.Duration:0} s)");
            return true;
        }

        private static QuestTask Describe(BotJobAssignment a, BotObjectiveManager objectives)
        {
            QuestTask task = new QuestTask { Assignment = a, Stand = a.Position.Value, Look = a.Position.Value, Quest = a.QuestAssignment.GetName() };
            BotQuestObjective objective = a.QuestObjectiveAssignment;
            if (a.QuestObjectiveStepAssignment?.ActionType == QuestAction.PlantItem)
            {
                task.Kind = QuestTaskKind.Plant;
                task.Duration = Mathf.Clamp((float)objectives.MinElapsedActionTime, 3f, 40f);
                task.What = "a quest item at " + (objective as BotQuestZoneObjective)?.ZoneID;
                return task;
            }

            if (objective is BotQuestItemObjective item && item.Item != null)
            {
                task.Kind = QuestTaskKind.TakeItem;
                task.Look = item.Item.transform.position;
                task.Duration = UnityEngine.Random.Range(3f, 6f);
                task.What = item.ToString().Replace("Item ", "");
                return task;
            }

            if (objective is BotQuestZoneObjective zone)
            {
                task.Kind = QuestTaskKind.Visit;
                task.Duration = UnityEngine.Random.Range(4f, 9f);
                task.What = zone.ZoneID;
                return task;
            }

            return null;
        }

        internal static void Finish(BotOwner bot, QuestTask task, bool complete)
        {
            _tasks.Remove(bot.ProfileId);
            _acted.Add(task.Assignment);
            if (!complete)
            {
                return;
            }

            BotObjectiveManager objectives = bot.GetObjectiveManager();
            if (objectives?.CurrentAssignment != task.Assignment)
            {
                return;
            }

            try
            {
                Completing = true;
                objectives.CompleteObjective();
            }
            finally
            {
                Completing = false;
            }
        }

        internal static void Drop(BotOwner bot) => _tasks.Remove(bot.ProfileId);

        private static string Verb(QuestTaskKind kind)
        {
            switch (kind)
            {
                case QuestTaskKind.TakeItem: return "picks up";
                case QuestTaskKind.Plant: return "plants";
                default: return "searches the place";
            }
        }
    }

    /// <summary>EFT objectives are completed only after the bot acted them out.</summary>
    public class QbCompleteObjectivePatch : ModulePatch
    {
        private static readonly FieldInfo _botOwner = AccessTools.Field(typeof(BotObjectiveManager), "botOwner");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotObjectiveManager), nameof(BotObjectiveManager.CompleteObjective));

        [PatchPrefix]
        protected static bool PatchPrefix(BotObjectiveManager __instance)
        {
            if (QuestTasks.Completing || !Settings.QuestSimulation.Value)
            {
                return true;
            }

            try
            {
                return !QuestTasks.TryStart(_botOwner?.GetValue(__instance) as BotOwner, __instance);
            }
            catch (Exception e)
            {
                Log.Warn("quest task failed to start: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// BigBrain layer, priority 19: above QuestingBots questing (18), below SAIN solo combat (20). Active while a quest task is pending;
    /// a fight interrupts it and the bot finishes afterwards. A plant objective starts as soon as the bot reaches the zone.
    /// </summary>
    public class QuestTaskLayer : CustomLayer
    {
        public const int LayerPriority = 19;
        private float _nextCheck;

        public QuestTaskLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Quest Task";

        public override bool IsActive()
        {
            try
            {
                return CheckActive();
            }
            catch (Exception e)
            {
                Diag.Report("QuestTaskLayer", e);
                QuestTasks.Drop(BotOwner);
                return false;
            }
        }

        private bool CheckActive()
        {
            if (!Settings.QuestSimulation.Value || BotOwner.BotState != EBotState.Active)
            {
                return false;
            }

            BotObjectiveManager objectives = BotOwner.GetObjectiveManager();
            if (QuestTasks.TryGet(BotOwner, out QuestTask task))
            {
                if (objectives?.CurrentAssignment != task.Assignment)
                {
                    QuestTasks.Drop(BotOwner);
                    return false;
                }

                if (Time.time > task.GiveUpAt)
                {
                    QuestTasks.Finish(BotOwner, task, true);
                    return false;
                }

                return TacticalTasks.NoThreat(BotOwner);
            }

            // Plant objectives: take over on arrival so the planting itself is acted out (QuestingBots would just stand there)
            if (Time.time > _nextCheck && objectives != null && objectives.CurrentQuestAction == QuestAction.PlantItem && objectives.IsCloseToObjective(1.5f))
            {
                _nextCheck = Time.time + 1f;
                return TacticalTasks.NoThreat(BotOwner) && QuestTasks.TryStart(BotOwner, objectives);
            }

            return false;
        }

        public override Action GetNextAction() => new Action(typeof(QuestTaskLogic), "quest task");

        public override bool IsCurrentActionEnding() => !QuestTasks.TryGet(BotOwner, out _);

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData.Unpause();
            BotOwner.SetPose(1f);
            base.Stop();
        }
    }

    public class QuestTaskLogic : CustomLogic
    {
        private float _nextOrder;

        public QuestTaskLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Act();
            }
            catch (Exception e)
            {
                Diag.Report("QuestTaskLogic", e);
                if (QuestTasks.TryGet(BotOwner, out QuestTask task))
                {
                    // Count it as done rather than leave the bot frozen on the spot
                    QuestTasks.Finish(BotOwner, task, true);
                }
            }
        }

        private void Act()
        {
            if (!QuestTasks.TryGet(BotOwner, out QuestTask task))
            {
                return;
            }

            float dist = Vector3.Distance(BotOwner.Position, task.Stand);
            if (task.ActingSince < 0 && dist > 1.2f)
            {
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 1f;
                    BotOwner.SetPose(1f);
                    BotOwner.SetTargetMoveSpeed(0.6f);
                    BotOwner.Sprint(false);
                    BotOwner.GoToPoint(task.Stand, true, 0.5f, false, false);
                }

                BotOwner.Steering.LookToMovingDirection();
                return;
            }

            BotOwner.StopMove();
            if (task.ActingSince < 0)
            {
                task.ActingSince = Time.time;
            }

            float elapsed = Time.time - task.ActingSince;
            switch (task.Kind)
            {
                case QuestTaskKind.TakeItem:
                    BotOwner.SetPose(0.1f);
                    BotOwner.Steering.LookToPoint(task.Look);
                    if (!task.AnimationPlayed && elapsed > 0.8f)
                    {
                        task.AnimationPlayed = true;
                        Animate(EInteraction.TakeItem);
                    }

                    break;

                case QuestTaskKind.Plant:
                    BotOwner.SetPose(0f);
                    BotOwner.Steering.LookToPoint(task.Stand + BotOwner.LookDirection.normalized - Vector3.up * 0.4f);
                    if (!task.AnimationPlayed && elapsed > 0.8f)
                    {
                        task.AnimationPlayed = true;
                        Animate(EInteraction.DropItem);
                    }

                    break;

                default:
                    // Clear the place: look around in random directions
                    BotOwner.SetPose(0.8f);
                    if (Time.time > task.NextLook)
                    {
                        task.NextLook = Time.time + UnityEngine.Random.Range(1.2f, 2.4f);
                        float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                        BotOwner.Steering.LookToPoint(BotOwner.Position + new Vector3(Mathf.Cos(a) * 8f, 1.4f, Mathf.Sin(a) * 8f));
                    }

                    break;
            }

            if (elapsed >= task.Duration)
            {
                BotOwner.SetPose(1f);
                QuestTasks.Finish(BotOwner, task, true);
            }
        }

        private void Animate(EInteraction interaction)
        {
            if (!Settings.QuestAnimations.Value)
            {
                return;
            }

            try
            {
                BotOwner.GetPlayer?.SetInteractInHands(interaction);
            }
            catch (Exception e)
            {
                Log.Debug($"{BotOwner.Profile.Nickname}: {interaction} animation failed: {e.Message}");
            }
        }
    }
}
