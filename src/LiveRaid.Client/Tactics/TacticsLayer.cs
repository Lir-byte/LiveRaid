using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    public enum TaskKind
    {
        // Freeze for a few seconds after a kill and listen for the victim's friends
        Listen,

        // Hide with a view on a body and wait for someone to take the bait
        Watch,

        // Guard a sector while a squadmate loots
        Cover,

        // React to a firefight heard in the distance (go, creep, flank, hold or leave)
        Investigate,

        // Sit in a well-hidden spot for a while (bush, rubble, a corner nobody checks)
        Hide,

        // Wait near an exit for people on their way out
        ExitCamp,

        // Run to a squadmate who is in a fight (SquadAssist); ends when the helper makes contact itself
        Assist,

        // Lie in wait, crouched and facing a place where a heard enemy is expected, for 12-25 s (Intercept)
        Ambush,

        // Give a squadmate meds or loot, or wait for it (SquadShare): walk up, throw, the other picks it up
        Handover,

        // Walk to a far place of the map (RoamPlanner eviction): a long errand, on arrival it is over
        Relocate,

        // Go to a fallen squadmate's body, or first to a hidden spot near it to leave cheap things there (MateRecovery)
        Recover,
    }

    public class TacticalTask
    {
        public TaskKind Kind;
        public Vector3? StandAt;
        public Vector3 LookAt;
        public float Until;
        public bool Prone;
        public string CorpseId;

        // How long to stay after arriving at StandAt (the wait of an ambush); <0 - by the kind of task (HoldLogic)
        public float Dwell = -1f;

        // Runs when the task ends normally (time up, arrived and done) and the bot is still alive
        public Action<BotOwner> OnFinished;

        // Always runs when the task goes away for any reason (finished, cancelled by a fight, bot dead, replaced): release counters here
        public Action Cleanup;

        // Bot that owns the task (set by TacticalTasks.Set)
        public BotOwner Owner;
        public float StartedAt;

        // Set when the bot reaches StandAt: the wait there is cut to a few seconds (a bot that arrived and then stood for the rest of a minute-long task looked AFK)
        public bool Arrived;

        // Movement style on the way to StandAt (<=0 / <0 = defaults)
        public float Speed = -1f;
        public bool Sprint;
        public float SprintBeyond = 60f;
        public float MovePose = -1f;

        public override string ToString() => $"{Kind} until {Until:0}s";

        /// <summary>Tasks a fight makes pointless: after the shooting the bot decides afresh instead of resuming an old plan.</summary>
        public bool EndsWithCombat => Kind == TaskKind.Listen || Kind == TaskKind.Investigate || Kind == TaskKind.Hide || Kind == TaskKind.Ambush || Kind == TaskKind.Cover
                                      || Kind == TaskKind.Handover || Kind == TaskKind.Relocate || Kind == TaskKind.Recover;
    }

    /// <summary>Short tactical jobs handed to bots by the loot brain, the squad brain, gunfire response, hiding and exit camping.</summary>
    public static class TacticalTasks
    {
        private static readonly Dictionary<string, TacticalTask> _tasks = new Dictionary<string, TacticalTask>();

        // How far a squadmate may fall behind its leader on its own errand before it drops it
        private const float LeashMeters = 35f;

        // "Stay together": a follower drifts at most 25 m off on an errand
        private static float Leash => Settings.TightSquads.Value ? 25f : LeashMeters;
        private static float _nextTick;

        public static void Clear()
        {
            foreach (TacticalTask task in _tasks.Values)
            {
                RunCleanup(task);
            }

            _tasks.Clear();
            _nextTick = 0;
        }

        public static int Count => _tasks.Count;

        /// <summary>How much a task matters: a lesser one does not push out a more important one that started under 20 s ago.</summary>
        public static int Rank(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.Assist:
                case TaskKind.Handover: return 5;
                case TaskKind.Relocate: return 4;
                case TaskKind.Recover: return 4;
                case TaskKind.ExitCamp: return 4;
                case TaskKind.Investigate:
                case TaskKind.Ambush:
                case TaskKind.Watch: return 3;
                case TaskKind.Hide:
                case TaskKind.Cover: return 2;
                default: return 1;
            }
        }

        public static int Refused { get; private set; }

        public static void ClearCounters() => Refused = 0;

        /// <summary>Gives the bot a task. False (and nothing changes) when it is busy with a more important one it started a moment ago.</summary>
        public static bool Set(BotOwner bot, TacticalTask task)
        {
            if (bot == null || task == null)
            {
                return false;
            }

            if (_tasks.TryGetValue(bot.ProfileId, out TacticalTask busy) && busy != task && busy.Kind != task.Kind
                && Rank(busy.Kind) > Rank(task.Kind) && Time.time - busy.StartedAt < 20f && Time.time < busy.Until)
            {
                Refused++;
                Log.Detail("task", () => $"{bot.Profile?.Nickname} keeps {busy.Kind}, {task.Kind} refused");
                RunCleanup(task);
                return false;
            }

            bool renewed = false;
            if (_tasks.TryGetValue(bot.ProfileId, out TacticalTask old) && old != task)
            {
                // Replaced before it ended: its counters still have to be released
                RunCleanup(old);
                renewed = old.Kind == task.Kind;
            }

            task.Owner = bot;
            task.StartedAt = renewed ? old.StartedAt : Time.time;
            _tasks[bot.ProfileId] = task;

            // Guard posts are renewed every second while a squadmate loots: only the first one is worth a line
            if (!renewed)
            {
                Log.Detail("task", () => $"{bot.Profile?.Nickname} starts {task.Kind} for {task.Until - Time.time:0} s" +
                                         (task.StandAt.HasValue ? $", goes {Vector3.Distance(bot.Position, task.StandAt.Value):0} m" : ", stays put"));
            }

            return true;
        }

        public static bool TryGet(BotOwner bot, out TacticalTask task)
        {
            task = null;
            return bot != null && _tasks.TryGetValue(bot.ProfileId, out task);
        }

        public static bool Has(BotOwner bot, TaskKind kind) => bot != null && _tasks.TryGetValue(bot.ProfileId, out TacticalTask t) && t.Kind == kind && Time.time < t.Until;

        /// <summary>Normal end: the follow-up (OnFinished) runs if the bot is alive.</summary>
        public static void Finish(BotOwner bot) => End(bot, true, "done");

        /// <summary>Ends a task without its follow-up (fight, death, watchdog, replaced by a squad order).</summary>
        public static void Cancel(BotOwner bot, string why) => End(bot, false, why);

        private static void End(BotOwner bot, bool followUp, string why)
        {
            if (bot == null || !_tasks.TryGetValue(bot.ProfileId, out TacticalTask task))
            {
                return;
            }

            _tasks.Remove(bot.ProfileId);
            RunCleanup(task);
            Log.Detail("task", () => $"{bot.Profile?.Nickname} ends {task.Kind} after {Time.time - task.StartedAt:0} s: {why}");
            if (!followUp || bot.IsDead)
            {
                return;
            }

            try
            {
                task.OnFinished?.Invoke(bot);
            }
            catch (Exception e)
            {
                Diag.Report("TacticalTask follow-up", e);
            }
        }

        private static void RunCleanup(TacticalTask task)
        {
            Action cleanup = task.Cleanup;
            task.Cleanup = null;
            try
            {
                cleanup?.Invoke();
            }
            catch (Exception e)
            {
                Diag.Report("TacticalTask cleanup", e);
            }
        }

        /// <summary>
        /// Twice a second: tasks of dead or despawned bots are dropped, tasks that are over are ended (the layer is not asked while a higher
        /// layer runs, so it cannot be relied on for that), and a fight cancels the tasks a fight makes pointless.
        /// </summary>
        public static void Tick()
        {
            if (_tasks.Count == 0 || Time.time < _nextTick)
            {
                return;
            }

            _nextTick = Time.time + 0.5f;
            foreach (KeyValuePair<string, TacticalTask> kv in _tasks.ToList())
            {
                TacticalTask task = kv.Value;
                BotOwner bot = task.Owner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || bot.GetPlayer == null || bot.GetPlayer.HealthController?.IsAlive != true)
                {
                    _tasks.Remove(kv.Key);
                    RunCleanup(task);
                    Log.Detail("task", $"{task.Kind} of {bot?.Profile?.Nickname ?? kv.Key} dropped: bot gone");
                    continue;
                }

                if (Time.time >= task.Until)
                {
                    Finish(bot);
                }
                else if (task.EndsWithCombat && !NoThreat(bot))
                {
                    Cancel(bot, "fight");
                }
                else if (task.Kind == TaskKind.Assist && HasContact(bot))
                {
                    Cancel(bot, "contact");
                }
                else if (task.Kind != TaskKind.Assist && task.Kind != TaskKind.Listen && task.Kind != TaskKind.Recover && LeaderOf(bot) is Player leader
                         && (leader.Position - bot.Position).sqrMagnitude > Leash * Leash)
                {
                    // A camper / watcher / listener of a squad left behind: drop it and catch up (QuestingBots follows the leader again)
                    Cancel(bot, "the leader is too far, catches up");
                }
            }
        }

        /// <summary>Tasks never fight: any known enemy hands the bot back to SAIN's combat layers.</summary>
        /// <summary>Sees an enemy right now (or did a moment ago) or was just hit: SAIN must fight.</summary>
        public static bool HasContact(BotOwner bot)
        {
            if (HitReaction.SinceHit(bot) < 4f)
            {
                return true;
            }

            var enemy = LiveRaid.Client.Bots.SainBot.Of(bot)?.GoalEnemy;
            return enemy != null && (enemy.IsVisible || enemy.TimeSinceSeen < 3f);
        }

        /// <summary>
        /// The squad leader of a follower: its boss, or - in the moment after a leader fell, while EFT / QuestingBots hand the lead over and the follower
        /// has no boss to follow yet - the living member of its group that leads others. Null for a leader or a solo.
        /// </summary>
        public static BotOwner SquadLeaderOf(BotOwner bot)
        {
            if (bot == null)
            {
                return null;
            }

            BotOwner boss = LeaderOf(bot)?.AIData?.BotOwner;
            if (boss != null)
            {
                return boss;
            }

            BotsGroup group = bot.BotsGroup;
            if (group == null || group.MembersCount < 2 || bot.Boss?.HaveFollowers() == true)
            {
                return null;
            }

            for (int i = 0; i < group.MembersCount; i++)
            {
                BotOwner m = group.Member(i);
                if (m != null && m != bot && !m.IsDead && m.Boss?.HaveFollowers() == true)
                {
                    return m;
                }
            }

            return null;
        }

        /// <summary>A follower's leader, if it has one alive.</summary>
        public static Player LeaderOf(BotOwner bot)
        {
            IPlayer boss = bot?.BotFollower?.BossToFollow?.Player();
            Player leader = boss as Player;
            return leader != null && leader.HealthController?.IsAlive == true && leader.ProfileId != bot.ProfileId ? leader : null;
        }

        /// <summary>
        /// May the bot START something peaceful (an errand, a detour, a hide, a reaction to a far fight)? Danger is noticed at once;
        /// calm comes back only after the bot's mind has settled (BotMind, "Smooth decisions"), so an enemy flickering in SAIN's memory
        /// no longer switches every errand layer on and off, and nobody walks off to an errand seconds after a fight.
        /// </summary>
        public static bool BotIsCalm(BotOwner bot) =>
            NoThreat(bot) && (!Settings.SmoothDecisions.Value || LiveRaid.Client.Bots.BotMind.IsCalm(bot));

        /// <summary>
        /// May the bot GO ON with what it does (or do an after-fight job: listen after a kill, watch a body)? No enemy known and not under
        /// fire - and, with "Smooth decisions", quiet for 1.5 s, so a one-frame flicker does not stop and restart the job.
        /// </summary>
        public static bool NoThreat(BotOwner bot)
        {
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
            {
                return false;
            }

            if (bot.Memory?.GoalEnemy != null || bot.Memory?.IsUnderFire == true)
            {
                return false;
            }

            SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(bot);
            if (sain != null && sain.GoalEnemy != null)
            {
                return false;
            }

            return !Settings.SmoothDecisions.Value || LiveRaid.Client.Bots.BotMind.QuietFor(bot) > 1.5f;
        }
    }

    /// <summary>
    /// BigBrain layer, priority 21: above QuestingBots questing/following (18/19), below SAIN squad combat (22).
    /// It is only active while the bot is calm, so SAIN solo combat (20) still wins the moment an enemy shows up.
    /// </summary>
    public class TacticsLayer : CustomLayer
    {
        public const int LayerPriority = 21;

        public TacticsLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Tactics";

        public override bool IsActive()
        {
            try
            {
                if (!TacticalTasks.TryGet(BotOwner, out TacticalTask task))
                {
                    return false;
                }

                if (Time.time >= task.Until)
                {
                    TacticalTasks.Finish(BotOwner);
                    return false;
                }

                if (task.Kind == TaskKind.Assist)
                {
                    // On the way to a friend in trouble the enemy is known but not here yet: keep going until contact
                    if (TacticalTasks.HasContact(BotOwner))
                    {
                        TacticalTasks.Cancel(BotOwner, "contact");
                        return false;
                    }

                    return BotOwner.Medecine?.Using != true;
                }

                if (!TacticalTasks.NoThreat(BotOwner))
                {
                    if (task.EndsWithCombat)
                    {
                        TacticalTasks.Cancel(BotOwner, "fight");
                    }

                    return false;
                }

                return BotOwner.Medecine?.FirstAid?.Have2Do != true && BotOwner.Medecine?.SurgicalKit?.HaveWork != true;
            }
            catch (Exception e)
            {
                Diag.Report("TacticsLayer", e);
                return false;
            }
        }

        public override Action GetNextAction() => new Action(typeof(HoldLogic), "hold");

        public override bool IsCurrentActionEnding() => !TacticalTasks.TryGet(BotOwner, out TacticalTask task) || Time.time >= task.Until;

        public override void Stop()
        {
            // Getting up again is left to the next layer; stand so movement looks natural
            BotOwner.BotLay.GetUp(true);
            base.Stop();
        }

        public override void BuildDebugText(StringBuilder sb)
        {
            if (TacticalTasks.TryGet(BotOwner, out TacticalTask task))
            {
                sb.AppendLine("LiveRaid: " + task);
            }
        }
    }

    public class HoldLogic : CustomLogic
    {
        private float _nextMoveOrder;

        private static readonly System.Random _rng = new System.Random();

        /// <summary>How long a bot stays where it was sent before the errand is over. 0 = the task's own time (guard posts, handovers).</summary>
        private static float ArrivalDwell(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.Investigate: return 3.5f + (float)_rng.NextDouble() * 4f;
                case TaskKind.Hide:
                case TaskKind.Watch:
                case TaskKind.ExitCamp: return 4f + (float)_rng.NextDouble() * 5f;
                case TaskKind.Relocate: return 0.5f;
                default: return 0f;
            }
        }

        public HoldLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData.Unpause();
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Hold();
            }
            catch (Exception e)
            {
                Diag.Report("TacticsLayer.Hold", e);
            }
        }

        private void Hold()
        {
            if (!TacticalTasks.TryGet(BotOwner, out TacticalTask task))
            {
                return;
            }

            if (task.StandAt.HasValue && (BotOwner.Position - task.StandAt.Value).sqrMagnitude > 2.25f)
            {
                if (Time.time > _nextMoveOrder)
                {
                    _nextMoveOrder = Time.time + 1f;
                    BotOwner.SetPose(task.MovePose >= 0 ? task.MovePose : 1f);
                    BotOwner.SetTargetMoveSpeed(task.Speed > 0 ? task.Speed : 1f);
                    bool sprint = task.Sprint && Vector3.Distance(BotOwner.Position, task.LookAt) > task.SprintBeyond;
                    BotOwner.Sprint(sprint);
                    BotOwner.GoToPoint(task.StandAt.Value, !sprint, -1f, false, false);
                }

                BotOwner.Steering.LookToMovingDirection();
                return;
            }

            if (task.StandAt.HasValue && !task.Arrived)
            {
                task.Arrived = true;
                float dwell = task.Dwell >= 0f ? task.Dwell : ArrivalDwell(task.Kind);
                if (dwell > 0f && Time.time + dwell < task.Until)
                {
                    Log.Detail("task", $"{BotOwner.Profile?.Nickname} arrived for {task.Kind}, waits {dwell:0} s instead of {task.Until - Time.time:0} s");
                    task.Until = Time.time + dwell;
                }
            }

            BotOwner.StopMove();
            if (task.Prone)
            {
                BotOwner.BotLay.TryLay();
            }
            else
            {
                // A handover is done standing; posts and watches crouched
                BotOwner.SetPose(task.Kind == TaskKind.Handover ? 1f : 0.1f);
            }

            BotOwner.Steering.LookToPoint(task.LookAt + Vector3.up * 0.5f);
        }
    }
}
