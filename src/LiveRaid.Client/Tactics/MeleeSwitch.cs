using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Knife or gun by what works (F12 -> 9 "Knife only at arm's length").
    /// SAIN decides "melee attack" every tick for as long as a knife is in the hands, and takes the gun back only when its knife run ends - a PMC that drew
    /// its knife at point-blank kept charging with it at 15, 30 m (raid 2026-10-22). Here:
    ///  - the knife comes out only for an enemy within arm's reach (3 m), or when no gun has a round and none can be loaded;
    ///  - with a knife in the hands and the enemy beyond 3 m (or gone), a gun with ammo is taken back at once - the main one if it can fire,
    ///    else the second gun or the pistol - and reloaded if it is empty.
    /// </summary>
    public static class MeleeSwitch
    {
        private const float ArmsReach = 3f;

        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static float _tick;

        public static int GunsBack { get; private set; }
        public static int KnivesRefused { get; private set; }

        public static string Summary => $"gun taken back from the knife {GunsBack}, knife refused beyond arm's reach {KnivesRefused}";

        public static void Clear()
        {
            _next.Clear();
            _tick = 0f;
            GunsBack = KnivesRefused = 0;
        }

        /// <summary>Distance to the bot's enemy (SAIN), or a large number when it has none.</summary>
        private static float EnemyDistance(BotOwner bot)
        {
            var enemy = SainBot.Of(bot)?.GoalEnemy;
            return enemy != null ? enemy.RealDistance : 999f;
        }

        /// <summary>The slot of a gun that can fire now or be loaded from what the bot carries; null when none.</summary>
        /// <param name="loadedOnly">
        /// Only a gun that can fire right now. The knife is refused only for such a gun: with an empty gun and rounds "somewhere" the bot was refused the
        /// knife at 4-6 m and stood there with an empty AK (raid 2026-10-22 15:57: Hauzman, 64 refusals).
        /// </param>
        public static EquipmentSlot? UsableGun(BotOwner bot, bool loadedOnly = false)
        {
            InventoryEquipment eq = bot?.GetPlayer?.Equipment;
            if (eq == null)
            {
                return null;
            }

            EquipmentSlot[] slots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
            foreach (EquipmentSlot slot in slots)
            {
                if (eq.GetSlot(slot)?.ContainedItem is Weapon w && (w.ChamberAmmoCount > 0 || Loaded(w)))
                {
                    return slot;
                }
            }

            if (loadedOnly)
            {
                return null;
            }

            HashSet<string> calibers = null;
            foreach (EquipmentSlot slot in slots)
            {
                if (!(eq.GetSlot(slot)?.ContainedItem is Weapon w))
                {
                    continue;
                }

                if (calibers == null)
                {
                    calibers = new HashSet<string>();
                    foreach (Item it in eq.GetAllItems())
                    {
                        if (it is Ammo a && !string.IsNullOrEmpty(a.Caliber))
                        {
                            calibers.Add(a.Caliber);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(w.AmmoCaliber) && calibers.Contains(w.AmmoCaliber))
                {
                    return slot;
                }
            }

            return null;
        }

        private static Weapon GunIn(BotOwner bot, EquipmentSlot slot) => bot?.GetPlayer?.Equipment?.GetSlot(slot)?.ContainedItem as Weapon;

        /// <summary>The gun in that slot has a round in the chamber or in its magazine.</summary>
        public static bool CanFire(BotOwner bot, EquipmentSlot slot)
        {
            Weapon w = GunIn(bot, slot);
            return w != null && (w.ChamberAmmoCount > 0 || Loaded(w));
        }

        /// <summary>The bot carries rounds for the gun in that slot (loose, in a box or in a spare magazine) - it can be reloaded.</summary>
        public static bool CanLoad(BotOwner bot, EquipmentSlot slot)
        {
            Weapon w = GunIn(bot, slot);
            InventoryEquipment eq = bot?.GetPlayer?.Equipment;
            if (w == null || eq == null || string.IsNullOrEmpty(w.AmmoCaliber))
            {
                return false;
            }

            // Summoned ammo: rounds for an empty magazine, a spare magazine for a gun found in the raid (Bottomless) - within its rules
            if (Settings.BottomlessAmmo.Value && w.CurrentAmmoTemplate != null && Bottomless.CanSupply(bot, w))
            {
                return true;
            }

            // A magazine-fed gun is reloaded only with a spare magazine (EFT's bots never refill the one in the gun): rounds with no spare magazine to put
            // them in load nothing - a player in that spot takes his other gun or looks for magazines
            Slot magSlot = w.ReloadMode == Weapon.EReloadMode.ExternalMagazine ? w.GetMagazineSlot() : null;
            bool spareMag = false;
            bool rounds = false;
            foreach (Item it in eq.GetAllItems())
            {
                if (magSlot != null && it is Magazine m && !IsInside(m, w) && magSlot.CanAccept(m))
                {
                    if (m.Count > 0)
                    {
                        return true;
                    }

                    spareMag = true;
                }
                else if (it is Ammo a && a.Caliber == w.AmmoCaliber && !IsInside(a, w) && !(a.Parent?.Container?.ParentItem is Magazine))
                {
                    rounds = true;
                }
            }

            return rounds && (magSlot == null || spareMag);
        }

        /// <summary>
        /// The item sits somewhere inside <paramref name="parent"/>. The walk up is bounded: the root item of an inventory is its own parent in EFT, so an
        /// open-ended walk never ended - 0.4.0 and 0.4.1 froze the game with it (the first empty gun of a fight, then the first bot of the raid).
        /// </summary>
        internal static bool IsInside(Item item, Item parent)
        {
            Item previous = item;
            Item p = item.Parent?.Container?.ParentItem;
            for (int depth = 0; p != null && depth < 16; depth++)
            {
                if (p == parent)
                {
                    return true;
                }

                if (p == previous)
                {
                    return false;
                }

                previous = p;
                p = p.Parent?.Container?.ParentItem;
            }

            return false;
        }

        /// <summary>Rounds in the magazine that sits in the gun.</summary>
        private static bool Loaded(Weapon w)
        {
            foreach (Item it in w.GetAllItems())
            {
                if (it is Ammo && it.Parent?.Container?.ParentItem is Magazine)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>EFT / SAIN wants the knife: only at arm's reach, or when no gun can be made to fire.</summary>
        public static bool AllowKnife(BotOwner bot)
        {
            if (!Settings.KnifeAtArmsLength.Value || bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || SafeReload.Busy)
            {
                return true;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return true;
            }

            float d = EnemyDistance(bot);
            if (d <= ArmsReach || UsableGun(bot, true) == null)
            {
                return true;
            }

            KnivesRefused++;
            Log.Detail("weapon", () => $"{bot.Profile.Nickname} keeps its gun: the enemy is {d:0} m away, the knife is for arm's reach");
            if (bot.WeaponManager != null && !bot.WeaponManager.HaveBullets)
            {
                SafeReload.Request(bot);
            }

            return false;
        }

        /// <summary>Four times a second: a knife in the hands with nobody at arm's reach goes back for a gun.</summary>
        public static void Tick()
        {
            if (!Settings.KnifeAtArmsLength.Value || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || bot.WeaponManager?.Selector == null || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                // A bot on its way out of the raid (or not quite set up) has no hands to ask: IsMelee threw for it at raid end
                try
                {
                    TickBot(bot);
                }
                catch (System.Exception e)
                {
                    Diag.Report("MeleeSwitch.Bot", e);
                }
            }
        }

        private static void TickBot(BotOwner bot)
        {
            // The knife in the hands, whether EFT's weapon manager knows it or not (after a fight scavs went on looting knife in hand for 45 s and more -
            // raid 2026-10-26 20:28, five times: the manager still had the gun as its current weapon)
            if (bot.GetPlayer?.HandsController == null || !(bot.WeaponManager.IsMelee || bot.GetPlayer.HandsController is Player.KnifeController))
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other || (_next.TryGetValue(bot.ProfileId, out float t) && Time.time < t))
            {
                return;
            }

            float d = EnemyDistance(bot);
            if (d <= ArmsReach)
            {
                return;
            }

            // A gun that can fire; with none, and nobody near (12 m+ or gone), one that can be loaded - into the hands to reload it
            EquipmentSlot? gun = UsableGun(bot, true) ?? (d > 12f ? UsableGun(bot) : null);
            if (gun == null)
            {
                return;
            }

            _next[bot.ProfileId] = Time.time + 1.5f;
            BotWeaponSelector selector = bot.WeaponManager.Selector;
            bool ok = selector.TryChangeToSlot(gun.Value, gun.Value == selector._mainWeapon || gun.Value == EquipmentSlot.FirstPrimaryWeapon, null);
            if (!ok)
            {
                ok = selector.TryChangeWeapon(true);
            }

            if (ok)
            {
                GunsBack++;
                Log.Info($"LR Weapon: {p.Name} puts the knife away for its {gun} - the enemy is {(d < 900f ? $"{d:0} m away" : "gone")}");
            }
        }
    }

    /// <summary>The knife comes out only at arm's reach (MeleeSwitch).</summary>
    public class KnifeOnlyAtArmsReachPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.ChangeToMelee));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner) => Diag.Allow(() => MeleeSwitch.AllowKnife(____owner), "KnifeOnlyAtArmsReachPatch");
    }
}
