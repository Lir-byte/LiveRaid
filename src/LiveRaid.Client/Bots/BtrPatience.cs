using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Bots keep out of the BTR's way for a moment, not for the rest of the raid (F12 -> 3 "Wait out the BTR only briefly").
    ///
    /// EFT's danger layer (AvoidDanger, priority 80 - above everything of SAIN, QuestingBots and LiveRaid) takes any bot within the "avoid BTR" radius of
    /// the armoured car and sits it in the nearest cover until the car is farther off (BotBewareBTR.ShallRunAway: distance only). The BTR stops at its
    /// halts for minutes: raid 2026-10-26 20:28 on Customs, Матвей Бубелэдыс crouched in one spot by the road for 16 minutes, Олег Богемный 4 and
    /// Серега Сейсмолог 3 - "AFK" for anyone who watched. A player steps out of its path and gets on with his business.
    /// Here a bot that has kept away from the car for 20-35 s (by character: rats and campers longer) stops doing so for 3 minutes unless the car is
    /// within 12 m (getting out from under its wheels still counts).
    /// </summary>
    public static class BtrPatience
    {
        private class State
        {
            public float Since = -1f;
            public float Patience;
            public float IgnoreUntil;
        }

        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        public static int GaveUp { get; private set; }

        public static string Summary => $"bots that stopped hiding from the BTR after a short wait {GaveUp}";

        public static void Clear()
        {
            _states.Clear();
            GaveUp = 0;
        }

        public static bool Allow(BotOwner bot, BotBewareBTR beware)
        {
            if (!Settings.BtrPatience.Value || bot == null || bot.IsDead)
            {
                return true;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return true;
            }

            float now = Time.time;
            if (!_states.TryGetValue(bot.Id, out State s))
            {
                _states[bot.Id] = s = new State();
            }

            Transform car = beware.BTRVehicle?.transform;
            float dist = car != null ? Vector3.Distance(car.position, bot.Position) : 0f;
            if (dist < 12f)
            {
                return true;
            }

            if (now < s.IgnoreUntil)
            {
                return false;
            }

            if (s.Since < 0f || now - s.Since > 60f)
            {
                s.Since = now;
                s.Patience = Random.Range(20f, 35f) * (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper ? 1.4f : 1f);
                return true;
            }

            if (now - s.Since < s.Patience)
            {
                return true;
            }

            s.Since = -1f;
            s.IgnoreUntil = now + 180f;
            GaveUp++;
            Log.Detail("route", () => $"{p.Name} has kept out of the BTR's way for {s.Patience:0} s ({dist:0} m off) - gets on with its business");
            return false;
        }
    }

    public class BtrPatiencePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotBewareBTR), nameof(BotBewareBTR.ShallRunAway));

        [PatchPostfix]
        protected static void PatchPostfix(BotBewareBTR __instance, BotOwner ____owner, ref bool __result)
        {
            if (__result && !Diag.Allow(() => BtrPatience.Allow(____owner, __instance), "BtrPatiencePatch"))
            {
                __result = false;
            }
        }
    }
}
