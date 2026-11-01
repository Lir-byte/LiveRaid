using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// The pistol is kept loaded (F12 -> 9 "Sidearm kept loaded"). EFT reloads only the gun in the hands, LiveRaid's dry-gun watch too: a pistol
    /// emptied in a fight and put away stayed empty for the rest of the raid. Raid 2026-10-31 15:59: Hackerman ran his Glock 18C dry at 04:49, went
    /// back to his TRG and fought AshleyShcaefferBMW at 3-10 m with the bolt-action - the empty pistol was not "a gun that can fire", so the switch
    /// for the distance never took it. A player in a lull draws the pistol, reloads it and puts it away: a calm bot with an empty sidearm it has
    /// rounds for does the same (draw, reload, back to the main gun; given up after 12 s, at most every 30 s).
    /// </summary>
    public static class SidearmCare
    {
        private class Job
        {
            public float Since;
            public EquipmentSlot Back;
            public bool Drawn;
        }

        private static readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private static readonly Dictionary<string, float> _nextTry = new Dictionary<string, float>();
        // When the pistol in the hands ran dry up close (one try to reload it, 8 s)
        private static readonly Dictionary<string, float> _dryUpClose = new Dictionary<string, float>();
        private static float _next;

        public static int Started { get; private set; }
        public static int Loaded { get; private set; }
        public static int Failed { get; private set; }

        public static int ReloadedUpClose { get; private set; }

        public static string Summary => $"empty pistols reloaded in a lull {Loaded} of {Started} (given up {Failed}), reloaded up close instead of the long gun {ReloadedUpClose}";

        /// <summary>
        /// EFT / SAIN wants to put the empty pistol away: refused while LiveRaid has drawn it to reload it in a lull, and when the enemy is close enough
        /// for the pistol to beat the gun asked for. Raid 15:59, 03:30-04:20: Hackerman's Glock 18C ran dry in one burst at 15-20 m and EFT handed him
        /// his TRG M10 back (a gun with rounds always may be taken) - seven swaps in a minute. A player reloads the pistol: a second or two against
        /// working a bolt-action at 15 m. Given up after 8 s without a reload starting, then the swap goes ahead.
        /// </summary>
        public static bool RefusesChange(BotOwner bot, EquipmentSlot? target, string what)
        {
            if (!Settings.Enabled.Value || !Settings.SidearmCare.Value || bot?.WeaponManager?.Selector == null)
            {
                return false;
            }

            BotWeaponManager wm = bot.WeaponManager;
            if (wm.Selector.EquipmentSlot != EquipmentSlot.Holster || (target.HasValue && target.Value == EquipmentSlot.Holster)
                || !(bot.GetPlayer?.HandsController?.Item is Weapon pistol) || MeleeSwitch.CanFire(bot, EquipmentSlot.Holster))
            {
                _dryUpClose.Remove(bot.ProfileId);
                return false;
            }

            if (!MeleeSwitch.CanLoad(bot, EquipmentSlot.Holster) || SafeReload.OutOfRounds(bot))
            {
                return false;
            }

            float now = Time.time;
            if (_jobs.TryGetValue(bot.ProfileId, out Job job) && now - job.Since < 12f)
            {
                SafeReload.Request(bot);
                return true;
            }

            Enemy e = SainBot.Of(bot)?.GoalEnemy;
            EquipmentSlot to = target ?? WeaponSteadiness.MainSlot(bot);
            if (e == null || !(bot.GetPlayer?.Equipment?.GetSlot(to)?.ContainedItem is Weapon other))
            {
                return false;
            }

            float d = e.RealDistance;
            if (RangeSense.Effect(other, d) >= RangeSense.Effect(pistol, d) - 0.1f)
            {
                return false;
            }

            if (!_dryUpClose.TryGetValue(bot.ProfileId, out float since))
            {
                _dryUpClose[bot.ProfileId] = since = now;
                ReloadedUpClose++;
                Log.Detail("weapon", $"{bot.Profile?.Nickname}: its {pistol.ShortName.Localized()} ran dry with {e.EnemyPlayer?.Profile?.Nickname} {d:0} m away - " +
                                     $"reloads it instead of taking its {other.ShortName.Localized()} ({what})");
            }

            if (now - since > 8f)
            {
                return false;
            }

            if (wm.Reload?.Reloading != true)
            {
                SafeReload.Request(bot);
            }

            return true;
        }

        public static void Clear()
        {
            _jobs.Clear();
            _nextTry.Clear();
            _dryUpClose.Clear();
            _next = 0f;
            Started = Loaded = Failed = ReloadedUpClose = 0;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.SidearmCare.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                if (_jobs.TryGetValue(bot.ProfileId, out Job job))
                {
                    Carry(bot, p, job, now);
                }
                else
                {
                    TryStart(bot, p, now);
                }
            }
        }

        private static void TryStart(BotOwner bot, BotProfile p, float now)
        {
            if (_nextTry.TryGetValue(bot.ProfileId, out float at) && now < at)
            {
                return;
            }

            _nextTry[bot.ProfileId] = now + 5f;
            BotWeaponManager wm = bot.WeaponManager;
            BotWeaponSelector sel = wm?.Selector;
            if (sel == null || !wm.IsReady || !wm.IsWeaponReady || wm.IsMelee || wm.Reload?.Reloading == true || sel.EquipmentSlot == EquipmentSlot.Holster
                || !(bot.GetPlayer?.Equipment?.GetSlot(EquipmentSlot.Holster)?.ContainedItem is Weapon pistol)
                || MeleeSwitch.CanFire(bot, EquipmentSlot.Holster) || !MeleeSwitch.CanLoad(bot, EquipmentSlot.Holster))
            {
                return;
            }

            // Only in a lull, hands free, and not while LootingBots has the bot (it takes the main gun back after every looted thing)
            if (!TacticalTasks.BotIsCalm(bot) || bot.Medecine?.Using == true || SafeReload.Busy || SafeReload.OutOfRounds(bot)
                || Raid.RaidMonitor.SafeLayerName(bot) == "Looting" || HandsGuard.RealReload(bot))
            {
                return;
            }

            EquipmentSlot back = sel.EquipmentSlot;
            bool ok = false;
            SafeReload.Run(() => ok = sel.TryChangeToSlot(EquipmentSlot.Holster, false));
            _nextTry[bot.ProfileId] = now + 30f;
            if (!ok)
            {
                return;
            }

            _jobs[bot.ProfileId] = new Job { Since = now, Back = back };
            Started++;
            Log.Detail("weapon", $"{p.Name}: its {pistol.ShortName.Localized()} is empty - draws it in a lull to reload it");
        }

        private static void Carry(BotOwner bot, BotProfile p, Job job, float now)
        {
            BotWeaponManager wm = bot.WeaponManager;
            BotWeaponSelector sel = wm?.Selector;
            if (sel == null)
            {
                _jobs.Remove(bot.ProfileId);
                return;
            }

            bool loaded = MeleeSwitch.CanFire(bot, EquipmentSlot.Holster);
            bool calm = TacticalTasks.NoThreat(bot);
            if (sel.EquipmentSlot == EquipmentSlot.Holster && wm.IsWeaponReady)
            {
                job.Drawn = true;
                if (!loaded && wm.Reload?.Reloading != true)
                {
                    SafeReload.Request(bot);
                }
            }

            // A fight: the pistol stays where it is (loaded or not, SAIN takes it from here)
            if (!calm)
            {
                _jobs.Remove(bot.ProfileId);
                if (!loaded)
                {
                    Failed++;
                }

                return;
            }

            if (loaded && wm.Reload?.Reloading != true && wm.IsWeaponReady)
            {
                Loaded++;
                _jobs.Remove(bot.ProfileId);
                if (sel.EquipmentSlot == EquipmentSlot.Holster && job.Back != EquipmentSlot.Holster)
                {
                    EquipmentSlot back = job.Back;
                    SafeReload.Run(() => sel.TryChangeToSlot(back, false));
                }

                Log.Detail("weapon", $"{p.Name}: its pistol is loaded again - back to its {job.Back}");
                return;
            }

            if (now - job.Since > 12f || (job.Drawn && sel.EquipmentSlot != EquipmentSlot.Holster && !loaded))
            {
                Failed++;
                _jobs.Remove(bot.ProfileId);
                if (sel.EquipmentSlot == EquipmentSlot.Holster && job.Back != EquipmentSlot.Holster && wm.IsWeaponReady)
                {
                    EquipmentSlot back = job.Back;
                    SafeReload.Run(() => sel.TryChangeToSlot(back, false));
                }

                Log.Detail("weapon", $"{p.Name}: its pistol could not be reloaded in 12 s - left as it is");
            }
        }
    }
}
