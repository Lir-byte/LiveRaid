using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Fire discipline (F12 -> 9 "Fire discipline"). A player's tester (2026-11-02): "bots pre-fire suppression and empty their whole mag, then reload
    /// out in the open - I wait behind a wall and take them mid-reload". SAIN suppresses an enemy who has just ducked out of sight from almost every
    /// one of its actions (the base steering of BotAction, cover, search, regroup) every quarter of a second, until the magazine is down to a third;
    /// traces of 31.10-02.11: 21 of 225 bots died reloading. A player shoots at a wall for a reason - to cover a squadmate on the move - and otherwise
    /// waits for the peek with a full magazine.
    ///  - covering fire is free: a squadmate with the role Assault or Flank within 100 m, or the bot itself pushing (CombatSense) - SAIN's suppression as is;
    ///  - otherwise in bursts: a burst of 0.6-3 s, then a pause of 1.5-9 s, and it stops while more of the magazine is left (a third for a novice,
    ///    two thirds for a pro) - by discipline: skill, the squad's precision, a rusher sprays more, a rat or a sniper less, plus a little of each bot's own.
    /// Novices and rushers still waste rounds now and then - that is what they do online too.
    /// </summary>
    public static class FireDiscipline
    {
        private class State
        {
            public float BurstEnd;
            public float CoolEnd;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();

        public static int Bursts { get; private set; }
        public static int Held { get; private set; }
        public static int Covering { get; private set; }

        public static string Summary => $"fire discipline: suppression bursts {Bursts}, held fire between bursts {Held} ticks, covering fire for a squadmate on the move {Covering} ticks, ammo checks on a gun with no magazine guarded {SainAmmoRatioPatch.Guarded}";

        public static void Clear()
        {
            _states.Clear();
            Bursts = Held = Covering = 0;
            SainAmmoRatioPatch.Clear();
        }

        public static float DisciplineOf(BotOwner bot, BotProfile p)
        {
            float d = p.Skill switch
            {
                SkillTier.Pro => 0.88f,
                SkillTier.Experienced => 0.72f,
                SkillTier.Average => 0.5f,
                _ => 0.25f,
            };
            DoctrineDeck.Member m = SquadDoctrines.Of(bot);
            if (m != null)
            {
                d += 0.25f * (m[DoctrineDeck.Precision] - 0.5f);
            }

            if (p.Style == PlayStyle.Rusher)
            {
                d -= 0.15f;
            }
            else if (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Sniper)
            {
                d += 0.1f;
            }

            d += (((bot.ProfileId.GetHashCode() >> 8) & 0xFF) / 255f - 0.5f) * 0.2f;
            return Mathf.Clamp(d, 0.05f, 0.95f);
        }

        /// <summary>A squadmate is going in or round the enemy now (fire from here covers him), or the bot itself pushes.</summary>
        private static bool Covers(BotOwner bot)
        {
            if (CombatSense.StanceOf(bot) == CombatStance.Push)
            {
                return true;
            }

            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return false;
            }

            foreach (Player pl in world.AllAlivePlayersList)
            {
                BotOwner mate = pl?.AIData?.BotOwner;
                if (mate == null || mate == bot || mate.IsDead || (mate.Position - bot.Position).sqrMagnitude > 100f * 100f
                    || !SquadTies.AreMates(bot.ProfileId, mate.ProfileId))
                {
                    continue;
                }

                FightRole role = FightRoles.RoleOf(mate);
                if (role == FightRole.Assault || role == FightRole.Flank)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>SAIN asks whether to suppress now. False = hold fire this tick (SAIN's suppression is reset).</summary>
        internal static bool Allow(SAINBotSuppressClass supp, ref float minimumAmmoRatio)
        {
            if (!Settings.FireDiscipline.Value)
            {
                return true;
            }

            BotOwner bot = supp.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other)
            {
                return true;
            }

            if (Covers(bot))
            {
                Covering++;
                return true;
            }

            float d = DisciplineOf(bot, p);
            minimumAmmoRatio = Mathf.Max(minimumAmmoRatio, 0.33f + 0.35f * d);
            float now = Time.time;
            if (_states.TryGetValue(bot.ProfileId, out State s) && now >= s.BurstEnd && now < s.CoolEnd)
            {
                Held++;
                if (supp.SuppressingTarget)
                {
                    supp.ResetSuppressing();
                }

                return false;
            }

            return true;
        }

        /// <summary>SAIN did suppress: the first shot of a burst sets how long the burst runs and the pause after it.</summary>
        internal static void Fired(SAINBotSuppressClass supp)
        {
            BotOwner bot = supp.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (!Settings.FireDiscipline.Value || p == null || p.Kind == BotKind.Other || Covers(bot))
            {
                return;
            }

            float now = Time.time;
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            if (now < s.BurstEnd)
            {
                return;
            }

            float d = DisciplineOf(bot, p);
            float r = (float)_rng.NextDouble();
            s.BurstEnd = now + Mathf.Lerp(3f, 0.6f, d) * (0.8f + 0.4f * r);
            s.CoolEnd = s.BurstEnd + Mathf.Lerp(1.5f, 9f, d) * (0.7f + 0.6f * (float)_rng.NextDouble());
            Bursts++;
            Log.Detail("combat", () => $"{p.Name} suppresses in a burst of {s.BurstEnd - now:0.0} s, then holds fire {s.CoolEnd - s.BurstEnd:0} s (discipline {d:0.00})");
        }
    }

    public class SainSuppressDisciplinePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAINBotSuppressClass), nameof(SAINBotSuppressClass.TrySuppressAnyEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(SAINBotSuppressClass __instance, ref float minimumAmmoRatio, ref bool __result)
        {
            try
            {
                if (!FireDiscipline.Allow(__instance, ref minimumAmmoRatio))
                {
                    __result = false;
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainSuppressDisciplinePatch", e);
            }

            return true;
        }

        [PatchPostfix]
        protected static void PatchPostfix(SAINBotSuppressClass __instance, bool __result)
        {
            if (!__result)
            {
                return;
            }

            try
            {
                FireDiscipline.Fired(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainSuppressDisciplinePatch", e);
            }
        }
    }

    /// <summary>
    /// SAIN's <c>CalcAmmoRatio</c> (the suppression check, called from the steering tick of almost every combat action) guards a missing weapon but not a
    /// weapon without a magazine: EFT's <c>BotReload.MaxBulletCount</c> reads <c>GetCurrentMagazine().MaxCount</c>. Raid 2026-11-02 15:58: 880 NullReferenceExceptions
    /// from bots whose gun stood without a magazine, each one cutting short SAIN's <c>PathSteeringTicked</c> of that bot (SeekCover bots "wanting to move and not
    /// moving"). A gun without a magazine has no ammo to suppress with: ratio 0.
    /// </summary>
    public class SainAmmoRatioPatch : ModulePatch
    {
        public static int Guarded { get; private set; }

        public static void Clear() => Guarded = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAINBotSuppressClass), nameof(SAINBotSuppressClass.CalcAmmoRatio));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner botowner, ref int currentAmmoCount, ref float __result)
        {
            try
            {
                EFT.InventoryLogic.Weapon gun = botowner?.WeaponManager?.ShootController?.Item;
                if (gun != null && gun.ReloadMode != EFT.InventoryLogic.Weapon.EReloadMode.OnlyBarrel && gun.GetCurrentMagazine() == null)
                {
                    currentAmmoCount = 0;
                    __result = 0f;
                    Guarded++;
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainAmmoRatioPatch", e);
            }

            return true;
        }
    }
}
