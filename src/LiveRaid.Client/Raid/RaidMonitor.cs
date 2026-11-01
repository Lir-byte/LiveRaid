using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Tactics;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Watches the raid for whoever analyses it afterwards.
    ///  - Always: stuck detection. A bot that has an objective to walk to but has not moved 1.5 m in 90 s is reported once ("LR Stuck:").
    ///  - Log detail Detailed/Full: a timeline line per bot every 15 s (5 s on Full) into the raid trace - what it is, which brain layer
    ///    drives it, combat stance, LiveRaid task, health, where it is and how far it moved - and once a minute an "LR Health" line:
    ///    population, active layers, stances, tasks, the slowest LiveRaid systems and the error counters.
    /// </summary>
    public static class RaidMonitor
    {
        private class Track
        {
            public Vector3 Pos;
            public float Since;
            public Vector3 LastSample;
            public float StuckReportedAt = -999f;
        }

        private const float StuckSeconds = 90f;
        private const float StuckMove = 1.5f;
        private static readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        private static float _nextSample;
        private static float _nextHealth;
        private static int _stuckReports;

        public static int StuckReports => _stuckReports;

        private static readonly HashSet<string> _zombies = new HashSet<string>();

        public static void Clear()
        {
            _zombies.Clear();
            _tracks.Clear();
            _nextSample = 0;
            _nextHealth = Time.time + 60f;
            _stuckReports = 0;
        }

        public static void Tick()
        {
            float now = Time.time;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || now < _nextSample || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            bool detailed = Log.IsDetailed;
            _nextSample = now + (Log.Level >= LogDetail.Full ? 5f : 15f);
            int stuckNow = 0;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                // Raid 2026-10-22 22:42: MDZZWOC was reported killed, yet stayed in the alive list with its AI running for ten more minutes ("patching
                // itself up" every 8 s at a fixed 10 % hp) - EFT's death handling did not finish (an exception in some death handler; Player.log tells).
                // Such a body is reported once and left out of everything here
                if (player.HealthController?.IsAlive != true)
                {
                    if (_zombies.Add(bot.ProfileId))
                    {
                        Log.Warn($"LR Health: {bot.Profile?.Nickname} is dead but still in the raid's list of the living with its AI running - EFT's death handling broke off (see Player.log)");
                    }

                    continue;
                }

                if (!_tracks.TryGetValue(bot.ProfileId, out Track track))
                {
                    _tracks[bot.ProfileId] = track = new Track { Pos = bot.Position, Since = now, LastSample = bot.Position };
                }

                string layer = SafeLayerName(bot);
                if (CheckStuck(bot, track, layer, now))
                {
                    stuckNow++;
                }

                if (detailed)
                {
                    Log.Detail("timeline", Describe(bot, layer, Vector3.Distance(track.LastSample, bot.Position)));
                }

                track.LastSample = bot.Position;
            }

            if (detailed && now >= _nextHealth)
            {
                _nextHealth = now + 60f;
                Log.Info(HealthLine(world, stuckNow));
            }
        }

        /// <summary>The bot's active BigBrain layer, "-" for none and "?" while its brain is not set up yet (EFT's Brain is null before activation).</summary>
        internal static string SafeLayerName(BotOwner bot)
        {
            try
            {
                return DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) ?? "-";
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>Not moved for long while QuestingBots wants it to walk somewhere: something blocks it (door, rubble, a broken path).</summary>
        private static bool CheckStuck(BotOwner bot, Track track, string layer, float now)
        {
            if ((bot.Position - track.Pos).sqrMagnitude > StuckMove * StuckMove)
            {
                track.Pos = bot.Position;
                track.Since = now;
                return false;
            }

            if (now - track.Since < StuckSeconds || layer.IndexOf("Objective", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            BotObjectiveManager objectives = bot.GetObjectiveManager();
            if (objectives == null || !objectives.IsJobAssignmentActive || objectives.CurrentQuestAction != QuestAction.MoveToPosition
                || objectives.IsCloseToObjective(3f))
            {
                return false;
            }

            if (now - track.StuckReportedAt > 180f)
            {
                track.StuckReportedAt = now;
                _stuckReports++;
                Vector3? target = objectives.Position;
                BotProfile p = BotProfiles.Get(bot);
                Log.Info($"LR Stuck: {bot.Profile.Nickname} ({p?.Kind}/{p?.Style}) has not moved for {now - track.Since:0} s at {Fmt(bot.Position)} " +
                         $"while walking to \"{objectives.CurrentAssignment?.QuestAssignment?.GetName()}\" at {(target.HasValue ? Fmt(target.Value) : "?")} " +
                         $"({(target.HasValue ? Vector3.Distance(bot.Position, target.Value).ToString("0") : "?")} m)");
            }

            return true;
        }

        private static string Describe(BotOwner bot, string layer, float moved)
        {
            BotProfile p = BotProfiles.Get(bot);
            StringBuilder sb = new StringBuilder(160);
            sb.Append(bot.Profile.Nickname).Append(" [").Append(p?.Kind.ToString() ?? "?");
            if (p != null && p.Kind != BotKind.Other)
            {
                sb.Append('/').Append(p.Style).Append('/').Append(p.Skill);
            }
            else
            {
                sb.Append('/').Append(bot.Profile.Info.Settings.Role);
            }

            sb.Append("] layer=").Append(layer);
            if (layer == "Looting")
            {
                // Which of LootingBots' actions: Looting (at / on the way to the loot), Loot Scan, Peaceful (EFT's patrol idle between the two)
                try
                {
                    string act = DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLogic(bot)?.GetType().Name;
                    if (act != null)
                    {
                        sb.Append('/').Append(act);
                    }
                }
                catch
                {
                    // the action name is only for the trace
                }
            }

            sb.Append(" mind=").Append(BotMind.PhaseOf(bot));
            if (CombatSense.TryGetState(bot, out CombatSense.State s) && (s.Stance != CombatStance.Normal || bot.Memory?.GoalEnemy != null))
            {
                sb.Append(" stance=").Append(s.Stance).Append(" morale=").Append(s.Morale.ToString("0.00"));
            }

            if (TacticalTasks.TryGet(bot, out TacticalTask task))
            {
                sb.Append(" task=").Append(task.Kind).Append(" (").Append(Mathf.Max(0f, task.Until - Time.time).ToString("0")).Append(" s left)");
            }

            SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(bot);
            if (sain?.GoalEnemy != null)
            {
                sb.Append(" enemy=").Append(sain.GoalEnemy.EnemyPlayer?.Profile?.Nickname ?? "?").Append('@').Append(sain.GoalEnemy.RealDistance.ToString("0")).Append('m')
                    .Append(sain.GoalEnemy.IsVisible ? " visible" : "")
                    .Append(" sain=").Append(sain.Decision?.CurrentCombatDecision.ToString() ?? "?").Append('/').Append(sain.Decision?.CurrentSquadDecision.ToString() ?? "?");
            }

            BotObjectiveManager objectives = bot.GetObjectiveManager();
            string quest = objectives?.CurrentAssignment?.QuestAssignment?.GetName();
            if (!string.IsNullOrEmpty(quest))
            {
                sb.Append(" quest=\"").Append(quest).Append('"');
            }

            // QuestingBots' own decision (why it quests or not: Fight, WaitForGroup, CheckForLoot, GetLost ...)
            try
            {
                sb.Append(" qb=").Append(QuestingBots.Helpers.BotBrainHelpers.GetCurrentQuestingDecision(bot));
            }
            catch
            {
                // no QuestingBots data for this bot
            }

            float hp = bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 0f;
            sb.Append(" hp=").Append((hp * 100f).ToString("0")).Append('%');
            if (bot.WeaponManager != null && bot.WeaponManager.IsReady)
            {
                if (bot.WeaponManager.Reload?.Reloading == true)
                {
                    // How long, and whether the hands really reload (a flag without the animation is the hung reload HandsGuard clears)
                    sb.Append(" ammo=reloading").Append((Time.time - bot.WeaponManager.Reload._reloadStartTime).ToString("0")).Append('s')
                        .Append(Tactics.HandsGuard.RealReload(bot) ? "" : "(flag)");
                }
                else if (!bot.WeaponManager.HaveBullets)
                {
                    sb.Append(" ammo=empty");
                }
            }
            string hands = Tactics.HandsGuard.HandsNote(bot);
            if (hands != null)
            {
                sb.Append(" hands=").Append(hands);
            }

            if (bot.Medecine?.Using == true)
            {
                sb.Append(" meds=using");
            }

            // A broken or blacked-out leg takes the sprint away (no painkiller): seen at once why a bot only walks
            string legs = Bots.LegCare.Describe(bot.GetPlayer);
            if (legs != null)
            {
                sb.Append(" legs=").Append(legs.Replace(' ', '_'));
            }

            sb.Append(" pos=").Append(Fmt(bot.Position)).Append(" moved=").Append(moved.ToString("0")).Append('m');
            float turned = MoveWatch.TakeTurned(bot);
            if (turned >= 150f)
            {
                sb.Append(" turned=").Append(turned.ToString("0")).Append('d');
            }

            return sb.ToString();
        }

        private static string HealthLine(GameWorld world, int stuckNow)
        {
            Dictionary<string, int> layers = new Dictionary<string, int>();
            Dictionary<BotKind, int> kinds = new Dictionary<BotKind, int>();
            Dictionary<string, int> stances = new Dictionary<string, int>();
            Dictionary<string, int> tasks = new Dictionary<string, int>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                BotKind kind = BotProfiles.Get(bot)?.Kind ?? BotKind.Other;
                kinds[kind] = (kinds.TryGetValue(kind, out int k) ? k : 0) + 1;
                string layer = bot.BotState == EBotState.Active ? SafeLayerName(bot) : bot.BotState.ToString();
                layers[layer] = (layers.TryGetValue(layer, out int n) ? n : 0) + 1;
                CombatStance stance = CombatSense.StanceOf(bot);
                if (stance != CombatStance.Normal)
                {
                    stances[stance.ToString()] = (stances.TryGetValue(stance.ToString(), out int c) ? c : 0) + 1;
                }

                if (TacticalTasks.TryGet(bot, out TacticalTask task))
                {
                    tasks[task.Kind.ToString()] = (tasks.TryGetValue(task.Kind.ToString(), out int t) ? t : 0) + 1;
                }
            }

            string Join<T>(Dictionary<T, int> d) => d.Count == 0 ? "none" : string.Join(", ", d.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            return $"LR Health: bots {Join(kinds)}; layers {Join(layers)}; stances {Join(stances)}; tasks {Join(tasks)}; stuck {stuckNow}; " +
                   $"slowest {Diag.TimingLine(4)}; errors {Diag.ErrorLine()}";
        }

        private static string Fmt(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
    }
}
