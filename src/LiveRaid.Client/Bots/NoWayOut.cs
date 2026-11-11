using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A bot told to leave on a map where no exit is open to bots does not turn into a bystander (built in, always on - a fix, not a choice).
    ///
    /// Raid 2026-11-10 12:37, Reserve: rasty_airsoft was sent out at minute 8 (its whole squad dead). The game's bot exfil goes only to an exit WITHOUT requirements
    /// (PatrolExfiltrationPointsData.UpdateFindNextExfiltrationPoint), and Reserve has none for a PMC: D-2 and the bunker door need a switch, the manhole an empty
    /// backpack slot, the cliff a paracord and an ice axe, the train is a train. The "Exfiltration" layer never came on (0 samples in the whole raid), while
    /// QuestingBots had already stopped the bot's questing for good ("wants to extract and will no longer quest") and LiveRaid's own systems skipped it as "leaving".
    /// For the remaining 19 minutes it went round one building in a 30 m square - looting, EFT's patrol in place, SAIN's cover-seeking - pressing into walls and doors.
    ///
    ///  - Before the order is given (QuestingBots' TryInstructBotToExtract - ours and its own end-of-raid one): no exit this bot could use -> a PMC takes the switch
    ///    exit whatever the odds (SwitchExtract: pulls the switches, stands the timer); none of those either, or not a PMC -> the order is refused, the bot goes on
    ///    with its raid and is asked again later (exits open: someone pulls D-2, a car comes).
    ///  - A bot that already wants to leave and has lost its way out (the switch plan failed, the car left): its wish is taken back and its questing given back.
    /// </summary>
    public static class NoWayOut
    {
        private const float AskAgain = 75f;

        private static readonly Dictionary<string, (float at, bool any)> _ordinary = new Dictionary<string, (float, bool)>();
        private static readonly Dictionary<string, float> _refusedUntil = new Dictionary<string, float>();
        private static readonly HashSet<string> _switchFailed = new HashSet<string>();
        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static readonly MethodInfo QuestingAllowedSetter = AccessTools.PropertySetter(typeof(BotObjectiveManager), nameof(BotObjectiveManager.IsQuestingAllowed));
        private static float _nextSweep;
        private static int _forced, _refused, _restored;

        public static string Summary => $"no exit open to bots: {_forced} sent through a switch exit instead, {_refused} stayed in the raid, {_restored} given their raid back";

        public static void Clear()
        {
            _ordinary.Clear();
            _refusedUntil.Clear();
            _switchFailed.Clear();
            _noted.Clear();
            _nextSweep = 0f;
            _forced = _refused = _restored = 0;
        }

        /// <summary>An exit the game's own bot exfil (with ExitPlan / ExitRules) can take this bot to: there this raid, in its walking area, every requirement met.</summary>
        public static bool HasOrdinaryExit(BotOwner bot)
        {
            if (bot == null)
            {
                return false;
            }

            if (_ordinary.TryGetValue(bot.ProfileId, out var known) && Time.time - known.at < 10f)
            {
                return known.any;
            }

            bool any = false;
            List<AIExfiltrationPoint> points = bot.BotsController?.CoversData?.Patrols?.ExfiltrationPoints;
            ExfiltrationPoint[] all = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (points != null)
            {
                foreach (AIExfiltrationPoint ai in points)
                {
                    if (ai == null || (ai.CorePointInGame != null && bot.StartCorePoint != null && ai.CorePointInGame.ConnectionGroupId != bot.StartCorePoint.ConnectionGroupId))
                    {
                        continue;
                    }

                    // The game links a point to its exit only when a bot first looks for one
                    ExfiltrationPoint exit = ai.ExfiltrationPoint ?? all?.FirstOrDefault(e => e?.Settings != null && e.Settings.Name == ai.Name);
                    if (exit == null || exit.Status == EExfiltrationStatus.NotPresent || !ExitRules.Allows(bot, exit, out _))
                    {
                        continue;
                    }

                    any = true;
                    break;
                }
            }

            _ordinary[bot.ProfileId] = (Time.time, any);
            return any;
        }

        /// <summary>The switch plan of this bot came to nothing: it is not tried again this raid.</summary>
        public static void SwitchFailed(BotOwner bot)
        {
            if (bot != null)
            {
                _switchFailed.Add(bot.ProfileId);
            }
        }

        /// <summary>
        /// QuestingBots is about to send the bot (and its followers) to the game's exfil. False: the order is not given - the answer is in <paramref name="result"/>.
        /// </summary>
        public static bool BeforeOrder(BotOwner bot, out bool result)
        {
            result = false;
            BotProfile p = bot == null ? null : BotProfiles.Get(bot);
            if (!Settings.Enabled.Value || p == null || p.Kind == BotKind.Other || p.Kind == BotKind.Scav || bot.IsDead)
            {
                return true;
            }

            if (SwitchExtract.HasPlan(bot))
            {
                result = true;
                return false;
            }

            if (HasOrdinaryExit(bot))
            {
                return true;
            }

            if (_refusedUntil.TryGetValue(bot.ProfileId, out float until) && Time.time < until)
            {
                return false;
            }

            if (TryForceSwitch(bot, p))
            {
                result = true;
                return false;
            }

            _refusedUntil[bot.ProfileId] = Time.time + AskAgain;
            if (_noted.Add(bot.ProfileId))
            {
                _refused++;
                Log.Info($"LR Extract: {p.Name} ({p.Kind}) would leave, but no exit of this map is open to it (every one needs a switch, an item or an empty slot it " +
                         "cannot meet) - stays in the raid and goes on with its business; asked again when an exit opens");
            }

            return false;
        }

        private static bool TryForceSwitch(BotOwner bot, BotProfile p)
        {
            if (!Settings.SwitchExits.Value || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || _switchFailed.Contains(bot.ProfileId))
            {
                return false;
            }

            SwitchExtract.SwitchExit exit = SwitchExtract.Choose(bot, out float way);
            if (exit == null)
            {
                return false;
            }

            string why = $"the only way out, {way:0} m";
            SwitchExtract.Assign(bot, exit, why);
            int squad = 0;
            List<BotOwner> followers = bot.Boss?.Followers;
            if (followers != null)
            {
                foreach (BotOwner f in followers)
                {
                    if (f != null && !f.IsDead && !SwitchExtract.HasPlan(f) && (!exit.Direct || ExitRules.Allows(f, exit.Exit, out _)))
                    {
                        SwitchExtract.Assign(f, exit, why);
                        squad++;
                    }
                }
            }

            _forced++;
            Log.Info($"LR Extract: {p.Name} ({p.Style}){(squad > 0 ? $" and {squad} squadmates" : "")} leave through switch exit {exit.Name.Localized()} - no other exit of this " +
                     $"map is open to bots ({(exit.Open ? "already open" : exit.Chain.Count + " switch(es) to pull")}, {way:0} m)");
            return true;
        }

        /// <summary>Every 5 s: bots that want to leave and have no way out any more get their raid back.</summary>
        public static void Tick()
        {
            if (!Settings.Enabled.Value || Time.time < _nextSweep)
            {
                return;
            }

            _nextSweep = Time.time + 5f;
            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot) || bot.Exfiltration == null || !bot.Exfiltration.WannaLeave())
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other || p.Kind == BotKind.Scav || SwitchExtract.HasPlan(bot) || HasOrdinaryExit(bot)
                    || bot.Exfiltration.BotInExfiltrationArea())
                {
                    continue;
                }

                // A follower whose leader is on a switch plan gets the plan from it; otherwise the bot itself looks for the switch exit once
                if (TryForceSwitch(bot, p))
                {
                    continue;
                }

                GiveRaidBack(bot, p, "no exit of this map is open to it any more");
            }
        }

        /// <summary>The bot does not leave after all: EFT's wish to leave is taken back, QuestingBots quests with it again, LiveRaid's systems count it in again.</summary>
        public static void GiveRaidBack(BotOwner bot, BotProfile p, string why)
        {
            if (bot?.Exfiltration == null)
            {
                return;
            }

            bot.Exfiltration._timeToExfiltration = float.MaxValue;
            bot.Exfiltration.ResetLeaveTime();
            ExtractPlanner.NotSent(bot);
            _refusedUntil[bot.ProfileId] = Time.time + AskAgain;
            try
            {
                BotObjectiveManager objectives = bot.GetObjectiveManager();
                if (objectives != null && !objectives.IsQuestingAllowed)
                {
                    QuestingAllowedSetter?.Invoke(objectives, new object[] { true });
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("NoWayOut", e);
            }

            _restored++;
            Log.Info($"LR Extract: {p?.Name ?? bot.Profile?.Nickname} was on its way out, but {why} - it goes on with its raid (questing and looting again)");
        }
    }

    /// <summary>QuestingBots sends a bot to the game's exfil: not when no exit is open to it (NoWayOut).</summary>
    public class QbInstructExtractPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotExtractMonitor), nameof(BotExtractMonitor.TryInstructBotToExtract));

        [PatchPrefix]
        protected static bool PatchPrefix(BotExtractMonitor __instance, ref bool __result)
        {
            try
            {
                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                if (bot == null || NoWayOut.BeforeOrder(bot, out bool result))
                {
                    return true;
                }

                __result = result;
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("QbInstructExtractPatch", e);
                return true;
            }
        }
    }
}
