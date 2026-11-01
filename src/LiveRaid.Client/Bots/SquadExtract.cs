using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A squad leaves the raid together (F12 -> 8 "Leave the raid together"), like players: the leader has the last word.
    ///  - A follower never decides to leave on its own while its leader lives (QuestingBots let any member that had done "enough quests" walk out alone);
    ///    it leaves when the leader does.
    ///  - Every member heads for the leader's exit (EFT picked the nearest exit per bot, so a squad split up over two exits).
    ///  - At the exit nobody starts the exit timer until the squad is there: everyone alive within 15 m of each other (or 90 s have passed, or the
    ///    missing ones are far off and not coming); then all timers start at once and they leave together.
    /// </summary>
    public static class SquadExtract
    {
        private const float Together = 15f;
        private const float MaxWait = 90f;

        private class Waiting
        {
            public BotOwner Bot;
            public float Duration;
            public float Since;
            public bool Logged;
        }

        private static readonly Dictionary<string, Waiting> _waiting = new Dictionary<string, Waiting>();
        private static readonly AccessTools.FieldRef<PatrolExfiltrationPointsData, AIExfiltrationPoint> CachedPoint =
            AccessTools.FieldRefAccess<PatrolExfiltrationPointsData, AIExfiltrationPoint>("_cachedExfiltrationPoint");

        // SetLeaveTime made from here: let it through
        internal static bool Bypass;
        private static float _next;

        public static int Synced { get; private set; }
        public static int WaitedFor { get; private set; }
        public static int FollowersHeld { get; private set; }

        public static string Summary => $"squad exits: same exit as the leader {Synced}, timers held until the squad was there {WaitedFor}, followers kept from leaving alone {FollowersHeld}";

        public static void Clear()
        {
            _waiting.Clear();
            _next = 0f;
            Synced = WaitedFor = FollowersHeld = 0;
        }

        public static BotOwner LeaderOf(BotOwner bot)
        {
            return Tactics.TacticalTasks.SquadLeaderOf(bot);
        }

        private static List<BotOwner> Squad(BotOwner bot)
        {
            BotOwner leader = LeaderOf(bot) ?? bot;
            List<BotOwner> squad = new List<BotOwner> { leader };
            if (leader.Boss?.Followers != null)
            {
                squad.AddRange(leader.Boss.Followers.Where(f => f != null && !f.IsDead && f != leader));
            }

            return squad;
        }

        /// <summary>QuestingBots asks whether the bot is ready to leave: a follower is ready only when its leader is.</summary>
        public static bool FollowerMayLeave(BotOwner bot)
        {
            if (!Settings.ExtractTogether.Value)
            {
                return true;
            }

            BotOwner leader = LeaderOf(bot);
            if (leader == null)
            {
                return true;
            }

            if (leader.Exfiltration?.WannaLeave() == true || SwitchExtract.HasPlan(leader))
            {
                return true;
            }

            FollowersHeld++;
            return false;
        }

        /// <summary>EFT picked an exit for a bot: a squad takes its leader's exit.</summary>
        public static void OnExitChosen(PatrolExfiltrationPointsData data, BotOwner bot)
        {
            if (data == null || bot == null || bot.IsDead)
            {
                return;
            }

            BotOwner leader = LeaderOf(bot);

            // A leader or a solo PMC goes to the exit of its spawn side it chose (ExitPlan), not simply to the nearest
            if (leader == null)
            {
                ExitPlan.Apply(data, bot);
            }

            if (!Settings.ExtractTogether.Value)
            {
                return;
            }

            if (leader != null)
            {
                AIExfiltrationPoint his = leader.PatrollingData?.ExfiltrationData?.CachedExfiltrationPoint;
                AIExfiltrationPoint mine = CachedPoint(data);
                if (his != null && his != mine)
                {
                    CachedPoint(data) = his;
                    Synced++;
                    Log.Detail("extract", $"{bot.Profile?.Nickname} takes its leader's exit {his.Name}");
                }

                return;
            }

            AIExfiltrationPoint ours = CachedPoint(data);
            if (ours == null || bot.Boss?.Followers == null)
            {
                return;
            }

            foreach (BotOwner f in bot.Boss.Followers)
            {
                PatrolExfiltrationPointsData theirs = f?.PatrollingData?.ExfiltrationData;
                if (theirs != null && !f.IsDead && CachedPoint(theirs) != null && CachedPoint(theirs) != ours)
                {
                    CachedPoint(theirs) = ours;
                    Synced++;
                }
            }
        }

        /// <summary>The bot is at its exit and would start the timer: false = wait for the squad (the timer starts later from <see cref="Tick"/>).</summary>
        public static bool AllowTimer(BotOwner bot, float time)
        {
            // Switch exits run their own timer for the whole squad (SwitchExtract)
            // A reset (a time far ahead) is never held up
            if (Bypass || !Settings.ExtractTogether.Value || bot == null || bot.IsDead || SwitchExtract.HasPlan(bot) || time > Time.time + 600f)
            {
                return true;
            }

            List<BotOwner> squad = Squad(bot);
            if (squad.Count <= 1 || Assembled(bot, squad))
            {
                _waiting.Remove(bot.ProfileId);
                return true;
            }

            if (!_waiting.TryGetValue(bot.ProfileId, out Waiting w))
            {
                _waiting[bot.ProfileId] = w = new Waiting { Bot = bot, Since = Time.time };
            }

            w.Duration = Mathf.Clamp(time - Time.time, 1f, 60f);
            if (Time.time - w.Since > MaxWait)
            {
                _waiting.Remove(bot.ProfileId);
                return true;
            }

            if (!w.Logged)
            {
                w.Logged = true;
                WaitedFor++;
                List<string> missing = squad.Where(m => m != bot && Vector3.Distance(m.Position, bot.Position) > Together).Select(m => m.Profile.Nickname).ToList();
                Log.Info($"LR Extract: {bot.Profile.Nickname} waits at the exit for {string.Join(", ", missing)}");

                // A member that is not on its way out yet is told to come
                foreach (BotOwner m in squad)
                {
                    if (m != bot && m.Exfiltration?.WannaLeave() != true && !SwitchExtract.HasPlan(m))
                    {
                        ExtractPlanner.ExtractTheUsualWay(m);
                    }
                }
            }

            return false;
        }

        /// <summary>Everyone alive is within reach of the bot, or the missing ones are far off and not coming to the exit.</summary>
        private static bool Assembled(BotOwner bot, List<BotOwner> squad)
        {
            foreach (BotOwner m in squad)
            {
                if (m == bot || m.IsDead)
                {
                    continue;
                }

                float d = Vector3.Distance(m.Position, bot.Position);
                if (d <= Together)
                {
                    continue;
                }

                // 300 m away and not heading out: it will not come in time
                if (d > 300f && m.Exfiltration?.WannaLeave() != true)
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        /// <summary>Once a second: waiting squads that are complete start their timers together.</summary>
        public static void Tick()
        {
            if (_waiting.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            foreach (Waiting w in _waiting.Values.ToList())
            {
                BotOwner bot = w.Bot;
                if (bot == null || bot.IsDead || bot.Exfiltration == null || !bot.Exfiltration.WannaLeave())
                {
                    _waiting.Remove(w.Bot?.ProfileId ?? "");
                    continue;
                }

                if (!Assembled(bot, Squad(bot)) && Time.time - w.Since < MaxWait)
                {
                    continue;
                }

                _waiting.Remove(bot.ProfileId);
                Bypass = true;
                try
                {
                    bot.Exfiltration.SetLeaveTime(Time.time + w.Duration);
                }
                finally
                {
                    Bypass = false;
                }

                Log.Info($"LR Extract: {bot.Profile.Nickname}'s squad is together at the exit after {Time.time - w.Since:0} s - the timer runs");
            }
        }
    }

    /// <summary>A follower does not leave on its own while its leader lives (SquadExtract).</summary>
    public class QbFollowerExtractPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.BotMonitor.Monitors.BotExtractMonitor"), "isBotReadyToExtract");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance, ref bool __result)
        {
            try
            {
                if (!__result)
                {
                    return;
                }

                BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
                if (bot != null && !SquadExtract.FollowerMayLeave(bot))
                {
                    __result = false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbFollowerExtractPatch", e);
            }
        }
    }

    /// <summary>EFT chose an exit for a bot: the squad uses the leader's (SquadExtract).</summary>
    public class ExitChoicePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PatrolExfiltrationPointsData), nameof(PatrolExfiltrationPointsData.UpdateFindNextExfiltrationPoint));

        [PatchPostfix]
        protected static void PatchPostfix(PatrolExfiltrationPointsData __instance, BotOwner ____owner)
        {
            Diag.Guard("ExitChoicePatch", () => SquadExtract.OnExitChosen(__instance, ____owner));
        }
    }

    /// <summary>The exit timer waits for the squad (SquadExtract).</summary>
    public class ExitTimerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotExfiltrationData), nameof(BotExfiltrationData.SetLeaveTime));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, float time) =>
            Diag.Allow(() => SquadExtract.AllowTimer(____owner, time), "ExitTimerPatch");
    }
}
