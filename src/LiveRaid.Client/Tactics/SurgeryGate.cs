using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// SAIN starts "surgery" (a surgical kit on a wound) as soon as the bot is not bleeding and holds a kit, whoever is about: the decision to
    /// start asks nothing about enemies, the decision to go on asks "is the area clear" and says no a tick later. The next check, two seconds on,
    /// starts it again. Each start of its action pauses the bot's own path for 3 s, so a wounded bot with an enemy known (seen a minute ago,
    /// 100 m away, anywhere under 80 m of path) stood on one spot for minutes while the SAIN decision flickered "SeekCover/Surgery" and
    /// "Search" every 1.5-2 s (raid 26.10 12:00: three ЧВК frozen at one place to the centimetre for 20-50 s, one shot dead where it stood,
    /// turning on the spot). Here the start is allowed only when the same "area clear" test that lets it continue passes. F12 -> 9 "Surgery only when safe".
    /// </summary>
    public class SurgeryGatePatch : ModulePatch
    {
        public static int Blocked { get; private set; }

        private static MethodInfo _areaClear;

        public static void Clear() => Blocked = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotSurgery), nameof(BotSurgery.CheckCanStartUsingKit));

        [PatchPostfix]
        protected static void PatchPostfix(BotSurgery __instance, ref bool __result)
        {
            try
            {
                if (!__result || !Settings.SurgeryWhenSafe.Value)
                {
                    return;
                }

                if (_areaClear == null)
                {
                    _areaClear = AccessTools.Method(typeof(BotSurgery), "CheckEnemies");
                }

                if (_areaClear != null && !(bool)_areaClear.Invoke(__instance, null))
                {
                    __result = false;
                    Blocked++;
                }
            }
            catch (Exception e)
            {
                Diag.Report("SurgeryGatePatch", e);
            }
        }
    }

    /// <summary>
    /// A LiveRaid layer above SAIN's (priority over 22) takes the bot from a SAIN layer without SAIN being told: its layers are not asked again while
    /// ours is in charge, so SAIN keeps "layers active" (and its path paused), and its BotMover patch keeps the stock mover switched off - orders given
    /// with BotOwner.GoToPoint then lead nowhere (raid 26.10 12:00: bots in "LiveRaid Back Off" stood 14 s on the same spot, 10 of 11 samples).
    /// The hand-over SAIN would do itself: layer off, mover free.
    /// </summary>
    public static class SainHandover
    {
        public static int Freed { get; private set; }

        public static void Clear() => Freed = 0;

        public static void Take(BotOwner bot)
        {
            try
            {
                if (bot == null)
                {
                    return;
                }

                SAIN.Components.BotComponent sain = Bots.SainBot.Of(bot);
                if (sain != null && sain.ActiveLayer != ESAINLayer.None)
                {
                    sain.ActiveLayer = ESAINLayer.None;
                    Freed++;
                    Log.Detail("task", $"{bot.Profile?.Nickname} takes the brain over from SAIN's layer - SAIN's layer and path let go");
                }

                if (bot.Mover != null && bot.Mover.Pause)
                {
                    bot.Mover.Pause = false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("SainHandover", e);
            }
        }
    }
}
