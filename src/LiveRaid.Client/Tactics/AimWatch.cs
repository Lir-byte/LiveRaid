using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// The last line against "aims and never fires" (F12 -> 9 "Watch for aiming without a shot"), for every bot - bosses and their guards too.
    ///
    /// The complaint has come back since the first versions under different causes: EFT's reload flag left up with no reload (HandsGuard), a gun swapped
    /// into a slot with the old gun's reload (GunState), magazines thrown away on reloads until none was left (MagKeeper). Every time the trace showed
    /// the bot's enemy and nothing about why the gun was silent. Here, twice a second: a bot whose SAIN enemy is in sight, can be shot at and is within
    /// the gun's reach, and whose gun has not lost a round for 6 s, is looked at - the hands, the gun, the magazines, the flags:
    ///  - a reload flag with no reload going on is cleared, a reload animation that has run for long is ended;
    ///  - an empty gun gets its magazines sorted out (MagKeeper) and a reload;
    ///  - a gun with rounds in its magazine but none in the chamber gets a reload with a loaded spare magazine;
    ///  - whatever it is, the state goes to the log once per 20 s per bot: "LR Weapon: X aims at Y (N m) for 6 s without a shot - ...".
    /// </summary>
    public static class AimWatch
    {
        private class State
        {
            public float Since = -1f;
            public int Rounds = -1;
            public string Enemy;
            public float NoteAt;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;
        private static int _lines;

        public static int Seen { get; private set; }
        public static int Fixed { get; private set; }

        public static string Summary => $"aimed 6 s at an enemy in sight without a shot {Seen} times (something fixed {Fixed})";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            _lines = 0;
            Seen = Fixed = 0;
        }

        public static void Tick()
        {
            if (!Settings.AimWithoutShotWatch.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot)
                    || bot.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR)
                {
                    continue;
                }

                try
                {
                    Look(bot, player, now);
                }
                catch (System.Exception e)
                {
                    Diag.Report("AimWatch", e);
                }
            }
        }

        private static int RoundsIn(Weapon gun)
        {
            Magazine mag = gun.GetCurrentMagazine();
            return gun.ChamberAmmoCount + (mag?.Count ?? 0);
        }

        private static void Look(BotOwner bot, Player player, float now)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            Enemy enemy = SainBot.Of(bot)?.GoalEnemy;
            Player.FirearmController fc = player.HandsController as Player.FirearmController;
            Weapon gun = fc?.Item;
            if (enemy == null || !enemy.IsVisible || !enemy.CanShoot || gun == null || enemy.RealDistance > RangeSense.EffectiveRange(gun))
            {
                s.Since = -1f;
                return;
            }

            int rounds = RoundsIn(gun);
            string enemyId = enemy.EnemyPlayer?.ProfileId;
            if (s.Since < 0f || s.Enemy != enemyId || rounds != s.Rounds)
            {
                // A shot (fewer rounds), a reload (more) or a new enemy: the clock starts again
                s.Since = now;
                s.Rounds = rounds;
                s.Enemy = enemyId;
                return;
            }

            if (now - s.Since < 6f)
            {
                return;
            }

            s.Since = now;
            Seen++;
            string fix = Fix(bot, fc, gun);
            if (fix != null)
            {
                Fixed++;
            }

            if (now < s.NoteAt)
            {
                return;
            }

            s.NoteAt = now + 20f;
            string line = $"LR Weapon: {bot.Profile?.Nickname} ({bot.Profile?.Info?.Settings?.Role}) aims at {enemy.EnemyPlayer?.Profile?.Nickname} ({enemy.RealDistance:0} m) for 6 s without a shot - " +
                          Describe(bot, fc, gun) + (fix != null ? " - " + fix : " - nothing wrong found with the gun");
            if (++_lines <= 40)
            {
                Log.Info(line);
            }
            else
            {
                Log.Detail("weapon", () => line);
            }
        }

        /// <summary>What can be put right; a few words on what was done, or null.</summary>
        private static string Fix(BotOwner bot, Player.FirearmController fc, Weapon gun)
        {
            BotWeaponManager wm = bot.WeaponManager;
            BotReload reload = wm?.Reload;
            if (reload == null)
            {
                return null;
            }

            if (reload.Reloading && !fc.IsInReloadOperation())
            {
                reload.Reloading = false;
                reload._reloadFailDebug = false;
                reload._nextReloadTime = 0f;
                SafeReload.Request(bot);
                return "its reload flag hung with no reload going on: cleared";
            }

            if (reload.Reloading && fc.IsInReloadOperation() && Time.time - reload._reloadStartTime > 12f
                && GunState.EndHungReload(bot, fc, reload, bot.Profile?.Nickname))
            {
                return "its reload animation hung: ended";
            }

            if (!wm.HaveBullets || RoundsIn(gun) == 0)
            {
                bool sorted = MagKeeper.Restock(bot, Time.time, false);
                SafeReload.Request(bot);
                return sorted ? "empty gun: a magazine brought within reach, reload asked for" : "empty gun: reload asked for";
            }

            Magazine inGun = gun.GetCurrentMagazine();
            if (gun.HasChambers && gun.ChamberAmmoCount == 0 && inGun != null && inGun.Count > 0 && gun.ReloadMode == Weapon.EReloadMode.ExternalMagazine
                && !fc.IsInReloadOperation())
            {
                Magazine spare = LoadedSpare(bot, gun, inGun);
                if (spare != null)
                {
                    SafeReload.Run(() =>
                    {
                        reload.Reloading = true;
                        reload.ReloadMagazine(spare);
                    });
                    return "nothing in the chamber: reloads with a loaded spare magazine";
                }

                return null;
            }

            return null;
        }

        private static Magazine LoadedSpare(BotOwner bot, Weapon gun, Magazine inGun)
        {
            Slot magSlot = gun.GetMagazineSlot();
            InventoryEquipment eq = bot.GetPlayer?.InventoryController?.Inventory?.Equipment;
            if (magSlot == null || eq == null)
            {
                return null;
            }

            Magazine best = null;
            foreach (Item it in eq.GetAllItems())
            {
                if (it is Magazine m && m != inGun && m.Count > 0 && magSlot.CanAccept(m) && MagKeeper.InReach(m) && (best == null || m.Count > best.Count))
                {
                    best = m;
                }
            }

            return best;
        }

        internal static string Describe(BotOwner bot, Player.FirearmController fc, Weapon gun)
        {
            BotWeaponManager wm = bot.WeaponManager;
            StringBuilder sb = new StringBuilder();
            Magazine mag = gun.GetCurrentMagazine();
            sb.Append(gun.ShortName.Localized()).Append(": chamber ").Append(gun.ChamberAmmoCount)
              .Append(", magazine ").Append(mag != null ? $"{mag.Count}/{mag.MaxCount}" : "none");

            int reach = 0, reachRounds = 0, away = 0, loose = 0;
            Slot magSlot = gun.GetMagazineSlot();
            InventoryEquipment eq = bot.GetPlayer?.InventoryController?.Inventory?.Equipment;
            if (eq != null)
            {
                foreach (Item it in eq.GetAllItems())
                {
                    if (it is Magazine m && magSlot != null && m != mag && magSlot.CanAccept(m) && !MeleeSwitch.IsInside(m, gun))
                    {
                        if (MagKeeper.InReach(m))
                        {
                            reach++;
                            reachRounds += m.Count;
                        }
                        else
                        {
                            away++;
                        }
                    }
                    else if (it is Ammo a && a.Caliber == gun.AmmoCaliber && !(a.Parent?.Container?.ParentItem is Magazine) && !MeleeSwitch.IsInside(a, gun))
                    {
                        loose += a.StackObjectsCount;
                    }
                }
            }

            sb.Append(", spare magazines in reach ").Append(reach).Append(" (").Append(reachRounds).Append(" rounds), out of reach ").Append(away)
              .Append(", loose rounds ").Append(loose);
            sb.Append(", reload flag ").Append(wm?.Reload?.Reloading == true ? (fc.IsInReloadOperation() ? "up (reloading)" : "up (no reload!)") : "down");
            sb.Append(", operation ").Append(fc.CurrentOperation?.GetType().Name ?? "?");
            if (wm?.IsWeaponReady == false)
            {
                sb.Append(", weapon change unfinished");
            }

            if (wm?.Malfunctions?.HaveMalfunction() == true)
            {
                sb.Append(", malfunction ").Append(wm.Malfunctions.MalfunctionType());
            }

            if (bot.Medecine?.Using == true)
            {
                sb.Append(", using meds");
            }

            var sain = SainBot.Of(bot);
            if (sain != null)
            {
                sb.Append(", SAIN ").Append(sain.Decision?.CurrentCombatDecision.ToString() ?? "?").Append('/').Append(sain.Decision?.CurrentSelfDecision.ToString() ?? "?");
            }

            sb.Append(", layer ").Append(DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) ?? "?");
            return sb.ToString();
        }
    }
}
