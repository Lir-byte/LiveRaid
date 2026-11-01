using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Hands that hang (F12 -> 9 "Free stuck hands"). One EFT flaw sat under three complaints that came back raid after raid:
    ///  - "endless reload": BotReload.Reloading is set when a reload starts and cleared only by the reload animation's callback. When anything takes the
    ///    hands in the middle (meds, a weapon change, LootingBots, a grenade) the callback never comes and the flag stays. EFT's own safety
    ///    (CheckReloadLongTime, 16 s for a magazine) clears it only once per gun per raid (_reloadFailDebug is never reset) and only from TryReload;
    ///    SAIN's "cancel reload" is an empty method. With the flag up SAIN does not shoot, EFT refuses every weapon change (TryChangeToSlot checks it)
    ///    and first aid does not start (TryApplyToCurrentPart checks it). Raid 2026-10-22 23:36: 3 TO 8 BATTLE CRABS "reloading" for 50 s until shot dead,
    ///    raid 22:42: MDZZWOC 10 min.
    ///  - "stuck between meds and the gun": after meds EFT takes the gun back with TakePrevWeapon - refused while the reload flag hangs; a heal cancelled by
    ///    a hit (CancelCurrent) leaves BotMedecine.Using up until EFT's 30 s timeout; a heal whose SetInHands failed waits for the same timeout.
    ///  - "froze while bleeding": LiveRaid's heal layer waited for the reload to end before using a bandage - it never ended - and stood the bot still.
    /// Here, 4 times a second for PMCs and scavs: a reload flag with no reload going on in the hands is cleared; "using meds" with no meds in the hands is
    /// ended; meds or empty hands with nothing being used get the gun back; a weapon change that never finished is released.
    /// </summary>
    public static class HandsGuard
    {
        private class State
        {
            public float ReloadBadSince = -1f;
            public float MedsBadSince = -1f;
            public float IdleHandsSince = -1f;
            public float NotReadySince = -1f;
            public float NextTakeAt;
            public int Takes;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;

        public static int ReloadsCleared { get; private set; }
        public static int MedsEnded { get; private set; }
        public static int GunsBack { get; private set; }
        public static int ChangesReleased { get; private set; }

        public static string Summary =>
            $"stuck hands freed: hung reloads cleared {ReloadsCleared}, hung meds ended {MedsEnded}, guns taken back from idle hands {GunsBack}, unfinished weapon changes released {ChangesReleased}";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            ReloadsCleared = MedsEnded = GunsBack = ChangesReleased = 0;
        }

        /// <summary>The reload in the hands is real (the animation is going on), not just EFT's flag.</summary>
        public static bool RealReload(BotOwner bot) =>
            bot?.WeaponManager?.Reload?.Reloading == true && bot.GetPlayer?.HandsController is Player.FirearmController fc && fc.IsInReloadOperation();

        public static void Tick()
        {
            if (!Settings.FreeStuckHands.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || player.HealthController?.IsAlive != true || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                // Bosses and their guards too (since 0.8.5): a hung reload flag is EFT's flaw, not a behaviour - Reshala's guards aimed and never fired.
                // For them only the flags are put right (reload, meds, an unfinished change); the gun is not pulled back into their hands
                BotProfile p = BotProfiles.Get(bot);
                if (p == null || bot.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR)
                {
                    continue;
                }

                try
                {
                    TickBot(bot, player, p);
                }
                catch (System.Exception e)
                {
                    Diag.Report("HandsGuard", e);
                }
            }
        }

        private static State Get(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            return s;
        }

        private static void TickBot(BotOwner bot, Player player, BotProfile p)
        {
            BotWeaponManager wm = bot.WeaponManager;
            if (wm == null || wm.Selector == null)
            {
                return;
            }

            State s = Get(bot);
            float now = Time.time;
            object hands = player.HandsController;
            Player.FirearmController firearm = hands as Player.FirearmController;
            // Stimulators go through the quick-use hands, not the meds controller
            bool medsInHands = hands is IMedsController || (hands != null && hands.GetType().Name.Contains("QuickUse"));

            // 0. EFT's entry for the gun in the hands is that gun's (a gun swapped into a slot kept the old gun's reload - GunState)
            GunState.Check(bot);

            // 1. The reload flag without a reload going on
            BotReload reload = wm.Reload;
            if (reload != null && reload.Reloading)
            {
                bool real = firearm != null && firearm.IsInReloadOperation();
                if (real && GunState.EndHungReload(bot, firearm, reload, p.Name))
                {
                    s.ReloadBadSince = -1f;
                    if (!wm.HaveBullets)
                    {
                        SafeReload.Request(bot);
                    }
                }
                else if (!real && now - reload._reloadStartTime > 1.5f)
                {
                    if (s.ReloadBadSince < 0f)
                    {
                        s.ReloadBadSince = now;
                    }
                    else if (now - s.ReloadBadSince >= 1f)
                    {
                        float hung = now - reload._reloadStartTime;
                        ClearReload(reload);
                        s.ReloadBadSince = -1f;
                        ReloadsCleared++;
                        Log.Detail("weapon", () => $"{p.Name}: its reload had hung for {hung:0} s (no reload in the hands, {Describe(hands)}) - cleared");
                        if (!wm.HaveBullets && firearm != null)
                        {
                            SafeReload.Request(bot);
                        }
                    }
                }
                else
                {
                    s.ReloadBadSince = -1f;
                }
            }
            else
            {
                s.ReloadBadSince = -1f;
            }

            // 2. "Using meds" with no meds in the hands (SetInHands failed, or the heal was cancelled by a hit and the flag stayed)
            BotMedecine med = bot.Medecine;
            bool using_ = med != null && (med.Using || med.FirstAid?._using == true || med.SurgicalKit?._using == true || med.Stimulators?._using == true);
            if (using_ && !medsInHands)
            {
                if (s.MedsBadSince < 0f)
                {
                    s.MedsBadSince = now;
                }
                else if (now - s.MedsBadSince >= 3f)
                {
                    EndMeds(med);
                    s.MedsBadSince = -1f;
                    MedsEnded++;
                    Log.Detail("weapon", () => $"{p.Name}: was 'using meds' for 3 s with {Describe(hands)} in its hands - ended, gun back");
                    if (p.Kind != BotKind.Other)
                    {
                        TakeGunBack(bot, s, now, force: true);
                    }
                }
            }
            else
            {
                s.MedsBadSince = -1f;
            }

            // 3. Meds (or nothing) in the hands while nothing is being used: the gun has to come back
            bool idleHands = p.Kind != BotKind.Other && hands != null && !using_ && (medsInHands || (IsEmpty(hands) && !player.HandsController.IsInInteractionStrictCheck()));
            if (idleHands)
            {
                if (s.IdleHandsSince < 0f)
                {
                    s.IdleHandsSince = now;
                }
                else if (now - s.IdleHandsSince >= (medsInHands ? 3f : 5f) && now >= s.NextTakeAt)
                {
                    if (reload != null && reload.Reloading)
                    {
                        // EFT refuses any weapon change while the flag is up
                        ClearReload(reload);
                        ReloadsCleared++;
                    }

                    bool first = s.Takes == 0;
                    TakeGunBack(bot, s, now, force: s.Takes >= 2);
                    if (first)
                    {
                        GunsBack++;
                        Log.Detail("weapon", () => $"{p.Name}: {Describe(hands)} in the hands with nothing being used - takes its gun back");
                    }
                }
            }
            else
            {
                s.IdleHandsSince = -1f;
                s.Takes = 0;
            }

            // 4. A weapon change that never finished (the callback did not come): EFT refuses the next one until IsWeaponReady
            if (!wm.Selector.IsWeaponReady)
            {
                if (s.NotReadySince < 0f)
                {
                    s.NotReadySince = now;
                }
                else if (now - s.NotReadySince >= 6f)
                {
                    wm.Selector.IsWeaponReady = true;
                    s.NotReadySince = -1f;
                    ChangesReleased++;
                    Log.Detail("weapon", () => $"{p.Name}: a weapon change had not finished for 6 s - released");
                    if (p.Kind != BotKind.Other && !(hands is Player.FirearmController) && !wm.IsMelee)
                    {
                        TakeGunBack(bot, s, now, force: true);
                    }
                }
            }
            else
            {
                s.NotReadySince = -1f;
            }
        }

        private static void ClearReload(BotReload reload)
        {
            reload.Reloading = false;
            reload._reloadFailDebug = false;
            reload._nextReloadTime = 0f;
        }

        private static void EndMeds(BotMedecine med)
        {
            if (med.FirstAid != null && med.FirstAid._using)
            {
                // Same as EFT's own 30 s timeout: flag down, gun back, next use a little later
                med.FirstAid.StopUse();
            }

            if (med.SurgicalKit != null && med.SurgicalKit._using)
            {
                med.SurgicalKit.Using = false;
            }

            if (med.Stimulators != null && med.Stimulators._using)
            {
                med.Stimulators.Using = false;
            }

            med.Using = false;
        }

        /// <summary>
        /// Back to the gun: EFT's own way first (the previous weapon - refused while a change is under way or too soon after one), every second;
        /// on the third try the main weapon is drawn directly.
        /// </summary>
        private static void TakeGunBack(BotOwner bot, State s, float now, bool force)
        {
            s.NextTakeAt = now + 1f;
            s.Takes++;
            BotWeaponSelector sel = bot.WeaponManager.Selector;
            SafeReload.Run(() =>
            {
                if (force || !sel.TakePrevWeapon())
                {
                    if (force || s.Takes >= 3)
                    {
                        sel.TakeMainWeapon();
                    }
                }
            });
        }

        private static bool IsEmpty(object hands) => hands == null || hands.GetType().Name.Contains("EmptyHands");

        private static string Describe(object hands)
        {
            if (hands == null)
            {
                return "nothing";
            }

            if (hands is IMedsController)
            {
                return "meds";
            }

            if (hands is Player.FirearmController)
            {
                return "a gun";
            }

            string n = hands.GetType().Name;
            return n.Contains("EmptyHands") ? "empty hands" : n.Contains("Knife") ? "a knife" : n.Contains("Grenade") ? "a grenade" : n;
        }

        /// <summary>What the hands hold, for the trace timeline (null when it is the gun).</summary>
        public static string HandsNote(BotOwner bot)
        {
            object hands = bot?.GetPlayer?.HandsController;
            if (hands is Player.FirearmController || hands == null)
            {
                return null;
            }

            return Describe(hands).Replace(' ', '_');
        }
    }
}
