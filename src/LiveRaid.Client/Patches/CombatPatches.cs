using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Lets combat morale steer SAIN's per-enemy decision: fall back to cover when the fight is going badly,
    /// change position when pinned or bleeding, push when the squad has the upper hand.
    /// Grenade dodging, melee, close-range dogfights and ammo-out retreats are never overridden.
    /// </summary>
    public class SainCombatDecisionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EnemyDecisionClass), nameof(EnemyDecisionClass.GetDecision));

        [PatchPostfix]
        protected static void PatchPostfix(EnemyDecisionClass __instance, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            try
            {
                // An enemy of someone else's distant fight that SAIN just heard of: no decision at all. SAIN marks such an enemy known
                // for a moment before LiveRaid's check forgets it again, and that moment was enough for one SeekCover tick - the bot
                // dropped its quest, entered SAIN's combat layer and left it a tenth of a second later (raid 2026-10-20: 709 times)
                if (SainDistantEnemyPatch.IsSomeoneElsesFight(enemy))
                {
                    result = ECombatDecision.None;
                    __result = false;
                    return;
                }

                RunPostfix(__instance, enemy, ref result, ref __result);
                KeepRunningToCover(__instance, ref result);
                ReturnFire(__instance, enemy, ref result, ref __result);

                // A player's habits: another angle after a few shots from one spot, a run for cover when hit in the open (PlayerSense)
                PlayerSense.Apply(__instance.BotOwner, enemy, ref result, ref __result);

                // The squad's peek to draw fire (FireTricks)
                FireTricks.ApplyPeek(__instance.BotOwner, enemy, ref result, ref __result);

                // Last word: whatever SAIN and the morale chose, a manoeuvre is carried through for a moment before another one replaces it
                DecisionHold.Apply(__instance, enemy, ref result, ref __result);

                // ...except that a bot caught in a long reload goes on reloading behind cover (ReloadUnderFire)
                CoverWhileReloading(__instance, ref result, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainCombatDecisionPatch", e);
            }
        }

        /// <summary>
        /// SAIN turns a fall-back (Retreat) into "keep running to that cover" (SeekCover) on its next tick; asking for Retreat again then
        /// restarted the run every other tick (646 times in one raid). A bot already running to cover keeps running.
        /// </summary>
        private static void KeepRunningToCover(EnemyDecisionClass decisions, ref ECombatDecision result)
        {
            SAIN.Components.BotComponent sain = decisions.Bot;
            if (result == ECombatDecision.Retreat && sain?.Decision?.CurrentCombatDecision == ECombatDecision.SeekCover && sain.Mover?.Moving == true)
            {
                result = ECombatDecision.SeekCover;
            }
        }

        private static void CoverWhileReloading(EnemyDecisionClass decisions, ref ECombatDecision result, ref bool __result)
        {
            if (!ReloadUnderFire.WantsCover(decisions.BotOwner))
            {
                return;
            }

            switch (result)
            {
                case ECombatDecision.StandAndShoot:
                case ECombatDecision.RushEnemy:
                case ECombatDecision.MoveToEngage:
                case ECombatDecision.Search:
                case ECombatDecision.Freeze:
                case ECombatDecision.ShootDistantEnemy:
                case ECombatDecision.CreepOnEnemy:
                case ECombatDecision.None:
                    result = ECombatDecision.SeekCover;
                    __result = true;
                    break;
            }
        }

        /// <summary>The hands are busy reloading (or the gun is dry): LiveRaid never turns such a bot's decision into a push or a stand-and-shoot.</summary>
        private static bool CannotShootNow(BotOwner bot) =>
            bot?.WeaponManager == null || !bot.WeaponManager.HaveBullets || HandsGuard.RealReload(bot);

        private static float _dryNoteAt;
        private static readonly System.Collections.Generic.Dictionary<string, float> _drySince = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> _firingUntil = new System.Collections.Generic.Dictionary<string, float>();

        public static int FireReturned { get; private set; }

        public static int HeldGround { get; private set; }

        public static void ClearCounters()
        {
            FireReturned = HeldGround = 0;
            _firingUntil.Clear();
            _drySince.Clear();
        }

        /// <summary>
        /// "Return fire" (F12 -> 9). Raid 2026-10-22: TetrisGG stood two minutes with jiblet in plain view 50-60 m away while SAIN sent it again and again
        /// to a cover it could not reach (Retreat/SeekCover; LiveRaid's stuck watch gave the run up every 8 s, SAIN chose it again) - it never fired.
        ///  - A bot that could not carry out its last manoeuvre holds its ground for 12 s: any move decision becomes "stand and shoot" while it sees
        ///    the enemy (or "freeze" behind what it has while it does not).
        ///  - A bot that has seen a shootable enemy for 2.5 s, within 80 m, and has not fired for 5 s does not walk or run about: it shoots.
        /// Grenades, melee, dogfights and running out of ammo are never overridden.
        /// </summary>
        private static void ReturnFire(EnemyDecisionClass decisions, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            if (!Settings.ReturnFire.Value || enemy == null)
            {
                return;
            }

            switch (result)
            {
                case ECombatDecision.Retreat:
                case ECombatDecision.SeekCover:
                case ECombatDecision.ShiftCover:
                case ECombatDecision.RunAway:
                case ECombatDecision.MoveToEngage:
                case ECombatDecision.Search:
                case ECombatDecision.CreepOnEnemy:
                case ECombatDecision.RushEnemy:
                    break;
                default:
                    return;
            }

            BotOwner bot = decisions.BotOwner;
            if (bot == null || bot.WeaponManager == null || bot.WeaponManager.IsMelee)
            {
                return;
            }

            if (!bot.WeaponManager.HaveBullets)
            {
                // Seen in the trace as ammo=empty: a gun that stays dry in a fight (no reload starts) is a bug to chase, not a choice. The first 2 s after
                // the last round EFT is still about to start its reload - not worth a note (the trace of 2026-10-26 had such false alarms)
                float nowDry = UnityEngine.Time.time;
                if (!_drySince.TryGetValue(bot.ProfileId, out float dryAt) || nowDry - dryAt > 30f)
                {
                    _drySince[bot.ProfileId] = dryAt = nowDry;
                }

                if (enemy.IsVisible && enemy.RealDistance < 40f && bot.WeaponManager.Reload?.Reloading != true && nowDry >= _dryNoteAt && nowDry - dryAt > 2f)
                {
                    _dryNoteAt = UnityEngine.Time.time + 5f;
                    Log.Detail("combat", () => $"{bot.Profile.Nickname} cannot shoot {enemy.EnemyPlayer?.Profile?.Nickname} ({enemy.RealDistance:0} m): its gun is empty and no reload is going on");
                    SafeReload.Request(bot);
                }

                return;
            }

            _drySince.Remove(bot.ProfileId);

            // Reloading: SAIN's own move (usually to cover) stands; an assault is not turned into standing still
            if (HandsGuard.RealReload(bot) || CombatSense.StanceOf(bot) == CombatStance.Push)
            {
                return;
            }

            bool shootable = enemy.IsVisible && enemy.CanShoot;
            if (CombatSense.HoldingGround(bot))
            {
                result = shootable ? ECombatDecision.StandAndShoot : ECombatDecision.Freeze;
                __result = true;
                HeldGround++;
                return;
            }

            float now = UnityEngine.Time.time;
            if (shootable && result != ECombatDecision.RushEnemy && _firingUntil.TryGetValue(bot.ProfileId, out float until) && now < until)
            {
                // Once it shoots back it keeps shooting while it sees him - not "shoot, then run for cover, then shoot" every few seconds
                // (raid 2026-10-22 13:52: SeekCover <-> StandAndShoot 68 times)
                if (WorldEvents.ShotBy(bot.ProfileId, 2f))
                {
                    _firingUntil[bot.ProfileId] = Mathf.Max(until, now + 3f);
                }

                result = ECombatDecision.StandAndShoot;
                __result = true;
                return;
            }

            // Pushing in on a close enemy is a move players make: let a rush be, the rest shoots back
            // Within the reach of its gun only: a shotgun or a pistol standing up to trade shots with a rifle at 60-80 m only gets the bot killed
            if (!shootable || result == ECombatDecision.RushEnemy || enemy.RealDistance > Mathf.Min(80f, CombatSense.Reach(bot) + 10f)
                || now - enemy.Vision.LastChangeVisionTime < 2.5f
                || WorldEvents.ShotBy(bot.ProfileId, 5f))
            {
                return;
            }

            _firingUntil[bot.ProfileId] = now + 5f;
            result = ECombatDecision.StandAndShoot;
            __result = true;
            FireReturned++;
            Log.Detail("combat", () => $"{bot.Profile.Nickname} returns fire at {enemy.EnemyPlayer?.Profile?.Nickname} ({enemy.RealDistance:0} m) instead of moving about");
        }

        private static void RunPostfix(EnemyDecisionClass __instance, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            if (!Settings.CombatMorale.Value || enemy == null)
            {
                return;
            }

            switch (result)
            {
                case ECombatDecision.AvoidGrenade:
                case ECombatDecision.MeleeAttack:
                case ECombatDecision.DogFight:
                case ECombatDecision.Retreat:
                case ECombatDecision.FightZombies:
                case ECombatDecision.ThrowGrenade:
                    return;
            }

            switch (CombatSense.StanceOf(__instance.BotOwner))
            {
                case CombatStance.Retreat:
                case CombatStance.BreakContact:
                    result = ECombatDecision.Retreat;
                    __result = true;
                    break;

                case CombatStance.Reposition:
                    if (result == ECombatDecision.StandAndShoot || result == ECombatDecision.ShootDistantEnemy
                        || result == ECombatDecision.Freeze || result == ECombatDecision.SeekCover)
                    {
                        result = ECombatDecision.ShiftCover;
                        __result = true;
                    }

                    break;

                case CombatStance.Probe:
                    // Looking for an enemy nobody sees: SAIN's search walks to where it was last heard or seen
                    if (!enemy.IsVisible && (result == ECombatDecision.SeekCover || result == ECombatDecision.StandAndShoot || result == ECombatDecision.Freeze
                                             || result == ECombatDecision.ShootDistantEnemy || result == ECombatDecision.ShiftCover))
                    {
                        result = ECombatDecision.Search;
                        __result = true;
                    }

                    break;

                case CombatStance.Push:
                    // Not while the hands reload or the gun is dry: SAIN's own choice (cover, reload) stands
                    if (CannotShootNow(__instance.BotOwner))
                    {
                        break;
                    }

                    if (result == ECombatDecision.StandAndShoot || result == ECombatDecision.SeekCover || result == ECombatDecision.ShootDistantEnemy
                        || result == ECombatDecision.Freeze || result == ECombatDecision.Search)
                    {
                        if (enemy.RealDistance < 80f)
                        {
                            result = ECombatDecision.RushEnemy;
                            __result = true;
                        }
                        else if (enemy.RealDistance < 150f && result != ECombatDecision.Search)
                        {
                            // Too far to rush: close in
                            result = ECombatDecision.MoveToEngage;
                            __result = true;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>When most of a squad is falling back or running, the squad regroups instead of trickling in one by one.</summary>
    public class SainSquadDecisionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SquadDecisionClass), nameof(SquadDecisionClass.GetDecision));

        [PatchPostfix]
        protected static void PatchPostfix(SquadDecisionClass __instance, ref ESquadDecision Decision, ref bool __result)
        {
            try
            {
                RunPostfix(__instance, ref Decision, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainSquadDecisionPatch", e);
            }
        }

        private static void RunPostfix(SquadDecisionClass __instance, ref ESquadDecision Decision, ref bool __result)
        {
            // Same for the squad layer: no squad decision over an enemy of someone else's distant fight
            if (SainDistantEnemyPatch.IsSomeoneElsesFight(__instance.Bot?.GoalEnemy))
            {
                Decision = ESquadDecision.None;
                __result = false;
                return;
            }

            if (!Settings.CombatMorale.Value)
            {
                return;
            }

            BotOwner bot = __instance.BotOwner;
            if (bot == null)
            {
                return;
            }

            BotsGroup group = bot.BotsGroup;
            if (group == null || group.MembersCount < 2)
            {
                return;
            }

            int shaken = 0;
            int total = 0;
            for (int i = 0; i < group.MembersCount; i++)
            {
                BotOwner member = group.Member(i);
                if (member == null || member.IsDead)
                {
                    continue;
                }

                total++;
                CombatStance stance = CombatSense.StanceOf(member);
                if (stance == CombatStance.Retreat || stance == CombatStance.BreakContact)
                {
                    shaken++;
                }
            }

            // Starts when half the squad is shaken, holds at least 6 s and until fewer than a third are: the squad no longer flips between
            // SAIN's squad layer (regroup) and its solo layer every decision
            bool regrouping = _regroupSince.TryGetValue(group, out float since);
            bool want = total >= 2 && (shaken * 2 >= total || (regrouping && (shaken * 3 >= total || UnityEngine.Time.time - since < 6f)));
            if (want)
            {
                if (!regrouping)
                {
                    _regroupSince[group] = UnityEngine.Time.time;
                }

                Decision = ESquadDecision.Regroup;
                __result = true;
            }
            else if (regrouping)
            {
                _regroupSince.Remove(group);
            }
        }

        private static readonly System.Collections.Generic.Dictionary<BotsGroup, float> _regroupSince = new System.Collections.Generic.Dictionary<BotsGroup, float>();

        public static void Clear() => _regroupSince.Clear();
    }
}
