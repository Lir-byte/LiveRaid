using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Grenades as players use them (F12 -> 9 "Grenades like a player"). SAIN throws at an enemy it cannot see whenever it may - every 5-10 s for
    /// every bot alike. Players differ: a storm squad nades before it goes in, a rat keeps its grenade (the throw gives it away), a novice forgets it has one.
    ///  - every bot has a habit (doctrine, style, skill): after a throw it waits 10-60 s before the next one (the keen ones less);
    ///  - a squad's call to go in (push together, storm a building, spread then storm, bait, revenge) lets each member throw once right away -
    ///    the grenade before the storm;
    ///  - SAIN still decides where and whether a throw is possible (unseen enemy, distance, friends in the way) - LiveRaid only holds it back.
    /// </summary>
    public static class GrenadeHabit
    {
        private static readonly Dictionary<string, float> _holdUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _primedUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _habit = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();

        public static int Thrown { get; private set; }
        public static int BeforeStorm { get; private set; }
        public static int Held { get; private set; }
        public static int HeldFar { get; private set; }

        public static string Summary => $"grenades: thrown {Thrown} (before a storm {BeforeStorm}), throws held back by habit {Held} ticks, at a far or flanked enemy {HeldFar} ticks";

        public static bool On => Settings.Enabled.Value && Settings.GrenadeHabit.Value;

        public static void Clear()
        {
            _holdUntil.Clear();
            _primedUntil.Clear();
            _habit.Clear();
            Thrown = BeforeStorm = Held = HeldFar = 0;
        }

        /// <summary>The squad goes in: each member may throw once in the next 20 s whatever his habit.</summary>
        public static void Prime(IEnumerable<BotOwner> members)
        {
            if (!On)
            {
                return;
            }

            foreach (BotOwner m in members)
            {
                if (m != null && m.WeaponManager?.Grenades?.HaveGrenade == true)
                {
                    _primedUntil[m.ProfileId] = Time.time + 20f;
                }
            }
        }

        /// <summary>0.1 (hardly throws) ... 1 (throws whenever it can).</summary>
        private static float Habit(BotOwner bot)
        {
            if (_habit.TryGetValue(bot.ProfileId, out float h))
            {
                return h;
            }

            BotProfile p = BotProfiles.Get(bot);
            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            DoctrineKind? kind = d?.Squad.Doctrine?.Kind;
            switch (kind)
            {
                case DoctrineKind.Chads:
                case DoctrineKind.Tanks:
                case DoctrineKind.BossHunters: h = 0.9f; break;
                case DoctrineKind.Clan: h = 0.75f; break;
                case DoctrineKind.PvpHunters: h = 0.6f; break;
                case DoctrineKind.Questers:
                case DoctrineKind.Marksmen: h = 0.45f; break;
                case DoctrineKind.Randoms: h = 0.4f; break;
                case DoctrineKind.Looters:
                case DoctrineKind.Speedrunners: h = 0.3f; break;
                case DoctrineKind.Rats: h = 0.2f; break;
                default: h = p?.Kind == BotKind.Scav ? 0.5f : p?.Kind == BotKind.PlayerScav ? 0.4f : 0.5f; break;
            }

            if (p != null)
            {
                if (p.Skill == SkillTier.Novice)
                {
                    h *= 0.6f;
                }

                if (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter)
                {
                    h += 0.1f;
                }
                else if (p.Style == PlayStyle.Rat)
                {
                    h -= 0.1f;
                }
            }

            h = Mathf.Clamp(h + ((float)_rng.NextDouble() - 0.5f) * 0.15f, 0.1f, 1f);
            _habit[bot.ProfileId] = h;
            return h;
        }

        /// <summary>SAIN asks whether to throw: false holds it back (habit cooldown), true lets SAIN decide.</summary>
        internal static bool MayTry(BotOwner bot, Enemy enemy)
        {
            if (!On || bot == null || BotProfiles.Get(bot)?.Kind == BotKind.Other)
            {
                return true;
            }

            // Raid 2026-10-31 05:46: Sheepy threw at Hjal 80+ m away in the middle of a flank. A player going round keeps quiet, and nobody throws at a
            // place he last saw someone 45 m off
            float far = enemy?.KnownPlaces?.LastKnownPlace?.DistanceToBot ?? 0f;
            if (Maneuvers.IsFlanking(bot) || far > 45f)
            {
                HeldFar++;
                return false;
            }

            float now = Time.time;
            if (_primedUntil.TryGetValue(bot.ProfileId, out float primed) && now < primed)
            {
                return true;
            }

            if (_holdUntil.TryGetValue(bot.ProfileId, out float until) && now < until)
            {
                Held++;
                return false;
            }

            return true;
        }

        internal static void Threw(BotOwner bot)
        {
            float now = Time.time;
            bool storm = _primedUntil.TryGetValue(bot.ProfileId, out float primed) && now < primed;
            _primedUntil.Remove(bot.ProfileId);
            float h = Habit(bot);
            _holdUntil[bot.ProfileId] = now + 10f + (1f - h) * 50f * (0.7f + 0.6f * (float)_rng.NextDouble());
            Thrown++;
            if (storm)
            {
                BeforeStorm++;
            }

            Log.Detail("combat", () => $"{bot.Profile?.Nickname} throws a grenade{(storm ? " before going in" : "")} (habit {h:0.00})");
        }
    }

    /// <summary>SAIN's grenade decision: LiveRaid holds a throw back by the bot's habit (GrenadeHabit).</summary>
    public class SainGrenadeHabitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GrenadeThrowDecider), nameof(GrenadeThrowDecider.GetDecision));

        [PatchPrefix]
        protected static bool PatchPrefix(GrenadeThrowDecider __instance, Enemy enemy, ref bool __result, ref string reason)
        {
            try
            {
                BotOwner bot = __instance.BotOwner;
                if (bot?.WeaponManager?.Grenades?.ThrowindNow == true || GrenadeHabit.MayTry(bot, enemy))
                {
                    return true;
                }

                __result = false;
                reason = "LiveRaid: holds its grenade";
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("SainGrenadeHabitPatch", e);
                return true;
            }
        }

        [PatchPostfix]
        protected static void PatchPostfix(GrenadeThrowDecider __instance, bool __result, string reason)
        {
            try
            {
                if (__result && reason == "startThrow")
                {
                    GrenadeHabit.Threw(__instance.BotOwner);
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainGrenadeHabitPatch", e);
            }
        }
    }
}
