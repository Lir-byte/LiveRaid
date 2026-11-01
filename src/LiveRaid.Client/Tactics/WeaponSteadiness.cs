using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Bots that keep switching weapons (F12 -> 9 "Steady weapon handling").
    ///
    /// Two causes:
    ///  - LootingBots ends EVERY loot job (each item, container and body) with UpdateActiveWeapon: the weapon list is rebuilt and the main weapon is taken into
    ///    the hands again (WeaponSelector.SetSlotItem), with the full draw animation; when the hands are busy it retries every 0.5 s up to 20 times. With a room
    ///    being searched thing after thing the bot drew its rifle again after every drawer - the endless "weapon swap" seen in the raids. Here the re-draw runs only
    ///    when it is needed: the weapons really changed (one was looted or swapped), or the hands hold no firearm, or not the main one; otherwise the magazines
    ///    are just refilled from the loot.
    ///  - EFT, SAIN and LootingBots each decide on their own when to change weapons (no ammo, enemy distance, LootingBots raises EFT's chance to change
    ///    weapon instead of reloading to 80 %): with a looted second gun - often without ammo for it - a bot went rifle -> pistol -> rifle -> second gun in a loop.
    ///    Here a bot that has changed firearms 3 times within 15 s keeps the one in its hands for 12 s (a gun without a round may still look for one with ammo a
    ///    little longer). Taking a gun back after meds, a grenade or the knife is never held up. EFT's chance to change weapon instead of reloading is
    ///    lowered to 35 % (20 % with a helmet).
    /// </summary>
    public static class WeaponSteadiness
    {
        private const float Window = 15f;
        private const int MaxChanges = 3;
        private const float HoldSeconds = 12f;

        private class State
        {
            public bool Known;
            public string Primary;
            public string Secondary;
            public string Holster;
            public EquipmentSlot LastFirearmSlot = (EquipmentSlot)(-1);
            public readonly List<float> Changes = new List<float>();
            public float HoldUntil;
            public float ReloadNoteAt;
            public float ReloadAskedSince = -1f;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly HashSet<string> _heldBots = new HashSet<string>();
        private static float _next;

        public static int SkippedRedraws { get; private set; }
        public static int Held { get; private set; }

        public static string Summary => $"weapon re-draws after looting skipped {SkippedRedraws}, weapon back-and-forth stopped {Held} times ({_heldBots.Count} bots), empty gun reloaded instead of swapped {ReloadsInstead}, swaps to an empty gun refused {ToEmptyRefused}" +
                                        $", out of rounds for every gun {SafeReload.OutOfRoundsBots} bots (swapped to a gun they had rounds for {SafeReload.SwappedToLoaded})";

        public static void Clear()
        {
            _states.Clear();
            _heldBots.Clear();
            _next = 0f;
            SkippedRedraws = Held = ReloadsInstead = ToEmptyRefused = 0;
        }

        private static State Get(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            return s;
        }

        private static bool IsFirearmSlot(EquipmentSlot slot) =>
            slot == EquipmentSlot.FirstPrimaryWeapon || slot == EquipmentSlot.SecondPrimaryWeapon || slot == EquipmentSlot.Holster;

        /// <summary>LootingBots wants to take the main weapon into the hands again after a loot job: true = let it.</summary>
        public static bool AllowRedraw(BotOwner bot)
        {
            if (!Settings.SteadyWeapons.Value || bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
            {
                return true;
            }

            Player player = bot.GetPlayer;
            InventoryEquipment eq = player?.Equipment;
            if (eq == null)
            {
                return true;
            }

            string primary = eq.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem?.Id;
            string secondary = eq.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem?.Id;
            string holster = eq.GetSlot(EquipmentSlot.Holster)?.ContainedItem?.Id;
            State s = Get(bot);
            bool same = s.Known && s.Primary == primary && s.Secondary == secondary && s.Holster == holster;
            s.Known = true;
            s.Primary = primary;
            s.Secondary = secondary;
            s.Holster = holster;
            if (!same)
            {
                return true;
            }

            // A treatment going on is not interrupted for a re-draw
            if (HealBreak.Running(bot))
            {
                SkippedRedraws++;
                return false;
            }

            // Meds, a knife, a grenade or nothing in the hands: the weapon has to come back
            Item inHands = player.HandsController?.Item;
            if (!(inHands is Weapon) || bot.WeaponManager?.IsMelee == true)
            {
                return true;
            }

            // Holding the pistol or the second gun after looting: back to the main one
            string main = primary ?? secondary;
            if (main != null && inHands.Id != main)
            {
                return true;
            }

            // Same guns, the main one already in the hands: just top up the magazines from what was looted
            try
            {
                SafeReload.Run(() => bot.WeaponManager?.Reload?.TryFillMagazines());
                if (bot.WeaponManager != null && !bot.WeaponManager.HaveBullets)
                {
                    SafeReload.Request(bot);
                }
            }
            catch (System.Exception e)
            {
                Log.Debug("refill after looting failed: " + e.Message);
            }

            SkippedRedraws++;
            return false;
        }

        /// <summary>EFT / SAIN / LootingBots asks for a weapon change: false = keep the one in the hands.</summary>
        private static int _depth;

        public static bool AllowChange(BotOwner bot, string what, EquipmentSlot? target = null)
        {
            // A weapon call made from inside another one (EFT's reload asking for a change, ...) or by LiveRaid's own reload: EFT decides alone
            if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || SafeReload.Busy || _depth > 0)
            {
                return true;
            }

            // Meds in the hands in the middle of a treatment: nobody takes them out for a gun (HealBreak ends a treatment when it has to)
            if (!Diag.Allow(() => !HealBreak.RefusesChange(bot), "HealBreak.RefusesChange"))
            {
                return false;
            }

            // The gun taken for this distance stays while the distance does (RangeSense: EFT's "back to the main weapon" put the shotgun back at 80 m)
            if (!Diag.Allow(() => !RangeSense.RefusesChange(bot, target, what), "RangeSense.RefusesChange"))
            {
                return false;
            }

            // The empty pistol is reloaded, not swapped for the long gun: drawn to reload in a lull, or run dry up close (SidearmCare)
            if (!Diag.Allow(() => !SidearmCare.RefusesChange(bot, target, what), "SidearmCare.RefusesChange"))
            {
                return false;
            }

            if (!Settings.SteadyWeapons.Value)
            {
                return true;
            }

            _depth++;
            try
            {
                return Decide(bot, what, target);
            }
            finally
            {
                _depth--;
            }
        }

        private static bool Decide(BotOwner bot, string what, EquipmentSlot? target)
        {

            // A gun back after meds, a grenade or the knife is always welcome
            if (!(bot.GetPlayer?.HandsController?.Item is Weapon) || bot.WeaponManager?.IsMelee == true)
            {
                return true;
            }

            State s = Get(bot);
            float now = Time.time;
            s.Changes.RemoveAll(t => now - t > Window);
            bool dry = bot.WeaponManager != null && !bot.WeaponManager.HaveBullets;
            if (!dry)
            {
                s.ReloadAskedSince = -1f;
            }

            // A gun that can fire is never swapped for one that cannot (SAIN's "best gun for the distance" picked the empty AK every 2 s and the loaded
            // second gun right after: raid 2026-10-22 15:57, Hauzman 6 swaps in 15 s, over and over, no shot fired)
            if (!dry && target.HasValue && target.Value != bot.WeaponManager.Selector.EquipmentSlot && bot.GetPlayer?.Equipment?.GetSlot(target.Value)?.ContainedItem is Weapon
                && !MeleeSwitch.CanFire(bot, target.Value))
            {
                ToEmptyRefused++;
                return false;
            }

            if (dry && bot.WeaponManager.Reload?.Reloading != true)
            {
                bool? empty = EmptyGunChange(bot, s, target, what, now);
                if (empty.HasValue)
                {
                    return empty.Value;
                }
            }
            if (now >= s.HoldUntil && s.Changes.Count < MaxChanges)
            {
                return true;
            }

            // A gun without a round in it may still go on looking for one that has ammo - a little
            if (dry && s.Changes.Count < MaxChanges + 2 && now >= s.HoldUntil)
            {
                return true;
            }

            if (now >= s.HoldUntil)
            {
                s.HoldUntil = now + HoldSeconds;
                Held++;
                _heldBots.Add(bot.ProfileId);
                Item inHands = bot.GetPlayer?.HandsController?.Item;
                Log.Info($"LR Weapon: {bot.Profile?.Nickname} keeps its {inHands?.ShortName?.Localized() ?? "weapon"} for {HoldSeconds:0} s - " +
                         $"{s.Changes.Count} weapon changes in {Window:0} s (asked: {what}{(dry ? ", the gun is empty" : "")})");
            }

            return false;
        }

        /// <summary>
        /// The gun in the hands is empty. SAIN swaps to the "best gun for the distance" whenever the gun is dry and not reloading - with both guns empty the bot
        /// swapped them back and forth and never reloaded (raid 2026-10-22 13:52: MadDutch MP-153 / UZI PRO 15 times in a minute of fighting). A player:
        ///  - takes the other gun if it has rounds in it (true);
        ///  - otherwise reloads the gun in the hands when it carries rounds for it (false, the reload is started);
        ///  - and never swaps one empty gun for another it cannot load either (false).
        /// Null: no say, the usual limiter decides.
        /// </summary>
        private static bool? EmptyGunChange(BotOwner bot, State s, EquipmentSlot? target, string what, float now)
        {
            EquipmentSlot current = bot.WeaponManager.Selector.EquipmentSlot;
            if (target.HasValue && target.Value != current && MeleeSwitch.CanFire(bot, target.Value))
            {
                return true;
            }

            // No round for any gun (SafeReload checked): swapping one empty gun for another only makes noise (Badger, raid 2026-10-26 15:48)
            if (SafeReload.OutOfRounds(bot) && (!target.HasValue || !MeleeSwitch.CanLoad(bot, target.Value)))
            {
                ToEmptyRefused++;
                return false;
            }

            // The other gun is empty too: reload the one in the hands (EFT's bots refill from their secure container) - unless that has not worked for
            // 8 s, then the swap may go ahead
            if (s.ReloadAskedSince < 0f)
            {
                s.ReloadAskedSince = now;
            }

            if (now - s.ReloadAskedSince < 8f)
            {
                SafeReload.Request(bot);
                if (now >= s.ReloadNoteAt)
                {
                    s.ReloadNoteAt = now + 10f;
                    ReloadsInstead++;
                    Log.Detail("weapon", $"{bot.Profile?.Nickname} reloads its empty {current} instead of swapping ({what})");
                }

                return false;
            }

            return null;
        }

        public static int ReloadsInstead { get; private set; }

        /// <summary>
        /// The slot EFT calls "main": the first primary, or the second one when LootingBots ranked a looted gun higher. LiveRaid took "main" for the first
        /// primary always: with the main gun in the second slot it refused EFT's "back to the main gun" as a pointless swap of the empty gun in the hands
        /// for itself and asked for a reload instead - which EFT then did not make, because its reload first asks for the main gun (raid 2026-10-26 20:28:
        /// NastyInc with an empty rifle for 95 s, twice more later, and into his last fight).
        /// </summary>
        public static EquipmentSlot MainSlot(BotOwner bot) => bot?.WeaponManager?.Selector?._mainWeapon ?? EquipmentSlot.FirstPrimaryWeapon;

        public static int ToEmptyRefused { get; private set; }

        /// <summary>
        /// EFT's own "change weapon for this enemy distance" check (a marksman rifle far, a carbine near): with the enemy moving about the threshold it
        /// switched every two seconds (raid 2026-10-22: lualeet SR-25 / AKMN, 5 changes in 15 s). At most one such change per 8 s, none while held.
        /// </summary>
        public static bool DistanceChangeAllowed(BotOwner bot)
        {
            if (RangeSense.Holds(bot) && bot.WeaponManager?.HaveBullets == true)
            {
                return false;
            }

            if (!Settings.SteadyWeapons.Value || bot == null || bot.IsDead || bot.WeaponManager?.IsMelee == true || SafeReload.Busy || _depth > 0)
            {
                return true;
            }

            State s = Get(bot);
            float now = Time.time;
            return now >= s.HoldUntil && (s.Changes.Count == 0 || now - s.Changes[s.Changes.Count - 1] > 8f);
        }

        /// <summary>EFT's chance to change weapon instead of reloading: LootingBots' 80 % / 40 % (with a helmet) down to 35 % / 20 %.</summary>
        public static void LowerChangeChance(BotSettings settings)
        {
            var shoot = settings?.FileSettings?.Shoot;
            if (!Settings.SteadyWeapons.Value || shoot == null)
            {
                return;
            }

            shoot.CHANCE_TO_CHANGE_WEAPON = Mathf.Min(shoot.CHANCE_TO_CHANGE_WEAPON, 35f);
            shoot.CHANCE_TO_CHANGE_WEAPON_WITH_HELMET = Mathf.Min(shoot.CHANCE_TO_CHANGE_WEAPON_WITH_HELMET, 20f);
        }

        /// <summary>Counts the real changes of firearm in the hands (whoever made them), a few times a second.</summary>
        public static void Tick()
        {
            if (!Settings.SteadyWeapons.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.3f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotWeaponSelector selector = bot?.WeaponManager?.Selector;
                if (selector == null || bot.IsDead)
                {
                    continue;
                }

                EquipmentSlot slot = selector.EquipmentSlot;
                if (!IsFirearmSlot(slot))
                {
                    continue;
                }

                State s = Get(bot);
                if (IsFirearmSlot(s.LastFirearmSlot) && s.LastFirearmSlot != slot)
                {
                    s.Changes.Add(now);
                    Log.Detail("weapon", () => $"{bot.Profile?.Nickname} changes {s.LastFirearmSlot} -> {slot} ({s.Changes.Count} in {Window:0} s)");
                }

                s.LastFirearmSlot = slot;
            }
        }
    }

    /// <summary>LootingBots' re-draw of the main weapon after every loot job: only when needed (WeaponSteadiness).</summary>
    public class LbUpdateActiveWeaponPatch : ModulePatch
    {
        private static readonly FieldInfo BotOwnerField = AccessTools.Field(typeof(LootingInventoryController), "_botOwner");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.UpdateActiveWeapon));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingInventoryController __instance)
        {
            try
            {
                return WeaponSteadiness.AllowRedraw(BotOwnerField?.GetValue(__instance) as BotOwner);
            }
            catch (System.Exception e)
            {
                Diag.Report("LbUpdateActiveWeaponPatch", e);
                return true;
            }
        }
    }

    /// <summary>LootingBots sets EFT's chance to change weapon instead of reloading to 80 % (40 % with a helmet) for every looting bot: lowered.</summary>
    public class WeaponChangeChancePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotSettings), nameof(BotSettings.ApplyPresetLocation));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(BotSettings __instance)
        {
            try
            {
                WeaponSteadiness.LowerChangeChance(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("WeaponChangeChancePatch", e);
            }
        }
    }

    public class WeaponTryChangePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TryChangeWeapon));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, ref bool __result)
        {
            if (Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "change weapon"), "WeaponTryChangePatch"))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class WeaponTryChangeToMainPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TryChangeToMain));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, ref bool __result)
        {
            if (Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "main weapon", WeaponSteadiness.MainSlot(____owner)), "WeaponTryChangeToMainPatch"))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class WeaponChangeToMainPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.ChangeToMain));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, ref bool __result)
        {
            if (Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "main weapon", WeaponSteadiness.MainSlot(____owner)), "WeaponChangeToMainPatch"))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class WeaponChangeToSecondPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.ChangeToSecond));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, ref bool __result)
        {
            if (Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "second weapon", ____owner?.WeaponManager?.Selector?._secondWeapon ?? EquipmentSlot.SecondPrimaryWeapon), "WeaponChangeToSecondPatch"))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class WeaponChangeToSlotPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TryChangeToSlot));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner, EquipmentSlot slot, ref bool __result)
        {
            if (Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "weapon in " + slot, slot), "WeaponChangeToSlotPatch"))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class WeaponDistanceChangePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.CanChangeWeaponCauseEnemyDistance));

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner ____owner, ref bool __result)
        {
            if (__result && !Diag.Allow(() => WeaponSteadiness.DistanceChangeAllowed(____owner), "WeaponDistanceChangePatch"))
            {
                __result = false;
            }
        }
    }

    public class WeaponNoAmmoChangePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TryChangeWeaponCauseNoAmmo));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner) =>
            Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "out of ammo"), "WeaponNoAmmoChangePatch");
    }

    public class WeaponTakeMainPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TakeMainWeapon));

        [PatchPrefix]
        protected static bool PatchPrefix(BotOwner ____owner) =>
            Diag.Allow(() => WeaponSteadiness.AllowChange(____owner, "take main weapon", WeaponSteadiness.MainSlot(____owner)), "WeaponTakeMainPatch");
    }
}
