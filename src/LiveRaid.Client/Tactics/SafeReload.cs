using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Reloads asked for by LiveRaid, run later and never from inside EFT's own weapon calls.
    /// 0.4.0 crashed the game ~50 s into the raid (no exception, the process died): "an empty gun reloads instead of swapping" called TryReload from
    /// inside the prefix of a weapon change; when EFT cannot reload (rounds but no magazine to put them in) TryReload asks for a weapon change itself -
    /// back into the prefix, TryReload again... a stack overflow, which takes Unity down without a log line. Now every reload LiveRaid wants is queued
    /// and made on the next tick (at most one per bot per 1.5 s), and while it runs LiveRaid's weapon hooks stand aside (<see cref="Busy"/>).
    /// </summary>
    public static class SafeReload
    {
        private static readonly HashSet<string> _wanted = new HashSet<string>();
        private static readonly Dictionary<string, float> _last = new Dictionary<string, float>();
        private static readonly List<string> _batch = new List<string>();

        /// <summary>LiveRaid itself is reloading or refilling a bot right now: weapon hooks let EFT do as it likes.</summary>
        public static bool Busy { get; private set; }

        public static int Done { get; private set; }

        public static void Clear()
        {
            _wanted.Clear();
            _last.Clear();
            _outUntil.Clear();
            _outNoted.Clear();
            _refusalNoteAt.Clear();
            Busy = false;
            Done = Dry = OutOfRoundsBots = SwappedToLoaded = 0;
            _nextWatch = 0f;
        }

        // Bots with no rounds for any of their guns (until when the check holds)
        private static readonly Dictionary<string, float> _outUntil = new Dictionary<string, float>();
        private static readonly HashSet<string> _outNoted = new HashSet<string>();

        public static int OutOfRoundsBots { get; private set; }
        public static int SwappedToLoaded { get; private set; }

        /// <summary>The bot has no round left for any of its guns (checked within the last 20 s).</summary>
        public static bool OutOfRounds(BotOwner bot) => bot != null && _outUntil.TryGetValue(bot.ProfileId, out float until) && Time.time < until;

        private static bool IsGunSlot(EFT.InventoryLogic.EquipmentSlot slot) =>
            slot == EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon || slot == EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon
                                                                       || slot == EFT.InventoryLogic.EquipmentSlot.Holster;

        /// <summary>
        /// The gun in the hands is empty and the bot carries no round for it. A reload then makes EFT change weapons on its own (LiveRaid's hooks stand
        /// aside while it reloads) - raid 2026-10-26 15:48: Badger, both rifles dry and no rounds left, swapped them every 2 s for minutes (8-10 changes in
        /// 15 s), each swap from this very reload. A player takes a gun that still fires (true, the swap made here); with none, he has nothing to shoot -
        /// he gets out of the fight and looks for ammo on a body or leaves (false: noted, CombatSense / LootJobs / ExtractPlanner take it from there).
        /// </summary>
        private static bool NothingToLoad(BotOwner bot, float now)
        {
            EFT.InventoryLogic.EquipmentSlot slot = bot.WeaponManager.Selector.EquipmentSlot;
            if (!IsGunSlot(slot) || MeleeSwitch.CanLoad(bot, slot))
            {
                _outUntil.Remove(bot.ProfileId);
                return false;
            }

            EFT.InventoryLogic.EquipmentSlot? loaded = MeleeSwitch.UsableGun(bot, true);
            if (loaded.HasValue && loaded.Value != slot)
            {
                Run(() => bot.WeaponManager.Selector.TryChangeToSlot(loaded.Value, false, null));
                SwappedToLoaded++;
                Log.Detail("weapon", () => $"{bot.Profile?.Nickname} has no rounds for its empty {slot} - takes its loaded {loaded.Value}");
                return true;
            }

            // Another gun it carries rounds for: that one is taken and reloaded next time round
            foreach (EFT.InventoryLogic.EquipmentSlot other in new[] { EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon, EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon, EFT.InventoryLogic.EquipmentSlot.Holster })
            {
                if (other != slot && MeleeSwitch.CanLoad(bot, other))
                {
                    Run(() => bot.WeaponManager.Selector.TryChangeToSlot(other, false, null));
                    SwappedToLoaded++;
                    Log.Detail("weapon", () => $"{bot.Profile?.Nickname} has no rounds for its empty {slot} - takes its {other}, which it has rounds for");
                    return true;
                }
            }

            _outUntil[bot.ProfileId] = now + 20f;
            if (_outNoted.Add(bot.ProfileId))
            {
                OutOfRoundsBots++;
                bool other = Bots.BotProfiles.Get(bot)?.Kind == Bots.BotKind.Other;
                bool rearm = !other && LootJobs.AddRearm(bot);
                // What it carries, so a wrong "no rounds" shows in the log (Reshala's guards were "dry" with packs of rounds in their secure container)
                string state = bot.GetPlayer?.HandsController is Player.FirearmController fc && fc.Item != null ? " [" + AimWatch.Describe(bot, fc, fc.Item) + "]" : "";
                Log.Info($"LR Weapon: {bot.Profile?.Nickname} has no rounds left for any of its guns - " +
                         (other ? "a boss or guard, LiveRaid sends it nowhere" : rearm ? "looks for ammo on the nearest body, keeps out of fights" : "keeps out of fights and heads out") + state);
            }

            return true;
        }

        public static void Request(BotOwner bot)
        {
            if (bot != null && !bot.IsDead)
            {
                _wanted.Add(bot.ProfileId);
            }
        }

        /// <summary>Runs an EFT weapon call of LiveRaid's own (a refill after looting) with the hooks standing aside.</summary>
        public static void Run(System.Action call)
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            try
            {
                call();
            }
            finally
            {
                Busy = false;
            }
        }

        private static float _nextWatch;

        /// <summary>
        /// Every 2 s: any bot with an empty gun in its hands that is not reloading gets a reload. SAIN reloads only inside its own combat layers - a bot
        /// that emptied its gun and left the fight walked, looted and went into the next fight with it empty (raid 2026-10-22 15:57: ScottieKnowz
        /// "ammo=empty" for two minutes, then "cannot shoot ... its gun is empty").
        /// </summary>
        private static void WatchDryGuns(GameWorld world)
        {
            if (!Settings.SteadyWeapons.Value || Time.time < _nextWatch)
            {
                return;
            }

            _nextWatch = Time.time + 2f;
            foreach (Player p in world.AllAlivePlayersList.ToArray())
            {
                // One bot whose weapon manager is half set up (EFT's HaveBullets threw for a bot being spawned - raid 2026-10-26 18:34, 21 times) must not
                // stop the watch for all the others
                try
                {
                    WatchOne(p);
                }
                catch (System.NullReferenceException)
                {
                }
            }
        }

        private static void WatchOne(Player p)
        {
            BotOwner bot = p?.AIData?.BotOwner;
            BotWeaponManager wm = bot?.WeaponManager;
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || wm == null || !wm.IsReady || wm.CurrentWeapon == null)
            {
                return;
            }

            bool bullets = wm.HaveBullets;
            if (bullets)
            {
                _outUntil.Remove(bot.ProfileId);
                _outNoted.Remove(bot.ProfileId);
                return;
            }

            if (wm.IsMelee || wm.Reload == null || wm.Reload.Reloading || !(p.HandsController?.Item is EFT.InventoryLogic.Weapon) || Spectator.TakeOver.IsTaken(bot)
                || OutOfRounds(bot))
            {
                return;
            }

            Request(bot);
            Dry++;
        }

        public static int Dry { get; private set; }

        private static readonly Dictionary<string, float> _refusalNoteAt = new Dictionary<string, float>();

        /// <summary>
        /// The reload LiveRaid asked for did not start: why, once in 20 s per bot, in the trace. EFT's reload says nothing when it declines (meds in use, a
        /// weapon change not finished, a jam, the main gun wanted first, no magazine with rounds) - a gun stayed empty for minutes with no hint why.
        /// </summary>
        private static void NoteRefusal(BotOwner bot, float now)
        {
            BotWeaponManager wm = bot.WeaponManager;
            if (wm?.Reload == null || wm.Reload.Reloading || wm.HaveBullets || (_refusalNoteAt.TryGetValue(bot.ProfileId, out float at) && now < at))
            {
                return;
            }

            _refusalNoteAt[bot.ProfileId] = now + 20f;
            string why;
            if (bot.Medecine?.Using == true)
            {
                why = "it is using meds";
            }
            else if (!wm.IsWeaponReady)
            {
                why = "a weapon change has not finished";
            }
            else if (wm.Malfunctions?.HaveMalfunction() == true)
            {
                why = "the gun is jammed (" + wm.Malfunctions.MalfunctionType() + ")";
            }
            else if (wm.Selector != null && wm.Selector.EquipmentSlot != wm.Selector._mainWeapon)
            {
                why = $"EFT wants its main gun ({wm.Selector._mainWeapon}) first";
            }
            else if (!MeleeSwitch.CanLoad(bot, wm.Selector?.EquipmentSlot ?? EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon))
            {
                why = "no magazine or round it can load";
            }
            else
            {
                why = "EFT would not start it (hands busy or not idle)";
            }

            Log.Detail("weapon", () => $"{bot.Profile?.Nickname}: its empty {wm.CurrentWeapon?.ShortName?.Localized()} is not reloaded - {why}");
        }

        public static void Tick()
        {
            GameWorld w = Singleton<GameWorld>.Instance;
            if (w != null && !Busy)
            {
                WatchDryGuns(w);
            }

            if (_wanted.Count == 0 || Busy)
            {
                return;
            }

            _batch.Clear();
            _batch.AddRange(_wanted);
            _wanted.Clear();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (string id in _batch)
            {
                if (_last.TryGetValue(id, out float at) && now - at < 1.5f)
                {
                    continue;
                }

                BotOwner bot = null;
                foreach (Player p in world.AllAlivePlayersList.ToArray())
                {
                    if (p != null && p.ProfileId == id)
                    {
                        bot = p.AIData?.BotOwner;
                        break;
                    }
                }

                BotReload reload = bot?.WeaponManager?.Reload;
                // Nor in the middle of a treatment: a weapon change from here cut RedBeard's splint short eight times (raid 2026-10-30 23:28); the dry-gun
                // watch asks again once the meds are put away
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || reload == null || reload.Reloading || bot.WeaponManager.HaveBullets
                    || bot.Medecine?.Using == true)
                {
                    continue;
                }

                _last[id] = now;
                try
                {
                    // EFT reloads only from magazines in the rig and pockets: one out of reach (or a dropped one) is brought there first, the reload follows
                    // on the next request (MagKeeper)
                    if (Settings.KeepMagazines.Value && MagKeeper.Restock(bot, now, false))
                    {
                        _wanted.Add(id);
                        continue;
                    }

                    if (NothingToLoad(bot, now))
                    {
                        continue;
                    }

                    // Empty spare magazines are filled from loose rounds first (secure container included) - TryReload alone needs a full one
                    Run(() =>
                    {
                        reload.TryFillMagazines();
                        reload.TryReload();
                    });
                    Done++;
                    NoteRefusal(bot, now);
                }
                catch (System.IndexOutOfRangeException e)
                {
                    // EFT's own reload check fails on some guns (raid 2026-10-22 16:49: BotReloadMagazine.CanReload -> IndexOutOfRange for 1nco's
                    // second primary): no retry for this bot for half a minute, one line in the log
                    _last[id] = now + 30f;
                    Log.Detail("weapon", () => $"{bot.Profile?.Nickname}: EFT cannot reload its {bot.WeaponManager?.CurrentWeapon?.ShortName?.Localized()} ({e.Message}) - tried again in 30 s");
                }
                catch (System.Exception e)
                {
                    Diag.Report("SafeReload", e);
                }
            }
        }
    }
}
