using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// EFT's knowledge of the gun in a bot's hands that goes stale (part of F12 -> 9 "Free stuck hands").
    ///
    /// Raid 2026-10-26 20:28: Faupi "reloaded" his shotgun for 12 minutes (ammo=reloading 732 s, the reload animation really going on), never fired
    /// again and died with it. EFT keeps one BotWeaponInfo per weapon SLOT (BotWeaponManager.info) and makes it only once - the first time a gun from that
    /// slot is in the hands. Its BotReload is built for that first gun: a revolver gets BotReloadRevolver. Faupi started with an MTs-255-12 (a revolver
    /// shotgun) in the first slot and later took an MP-153 off a body into the same slot (LootingBots): EFT went on reloading the MP-153 "as a cylinder"
    /// (ReloadCylinderMagazine on a tube gun) - the cylinder operation found no cylinder, threw a NullReferenceException on every animation event (701
    /// times in Player.log) and never ended. LootingBots' own weapon callback even updates the hands before the slot, so the stale entry was always picked.
    ///  - Here, whenever the hands get a gun (and 4 times a second from HandsGuard), the slot's entry is checked against the gun really in that slot and in
    ///    the hands; a stale one is built anew (EFT's own constructor: the right BotReload for the gun) and is made the current one.
    ///  - A reload operation in the hands that has gone on far longer than any reload takes (a magazine 20 s, rounds one by one 45 s) is ended by hand:
    ///    EFT's own fast-forward, and when that throws (a broken operation), the operation is marked finished and the hands go idle; the reload flag is cleared.
    /// </summary>
    public static class GunState
    {
        public static int Rebuilt { get; private set; }
        public static int Aborted { get; private set; }

        private static readonly Dictionary<string, float> _noteAt = new Dictionary<string, float>();

        public static string Summary => $"stale gun knowledge rebuilt {Rebuilt} (a gun swapped into a slot), hung reload animations ended {Aborted}";

        public static void Clear()
        {
            Rebuilt = Aborted = 0;
            _noteAt.Clear();
        }

        private static readonly EquipmentSlot[] GunSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };

        /// <summary>The slot the gun sits in, or null.</summary>
        public static EquipmentSlot? SlotOf(Player player, Item gun)
        {
            InventoryEquipment eq = player?.Equipment;
            if (eq == null || gun == null)
            {
                return null;
            }

            foreach (EquipmentSlot slot in GunSlots)
            {
                if (eq.GetSlot(slot)?.ContainedItem == gun)
                {
                    return slot;
                }
            }

            return null;
        }

        /// <summary>The weapon manager's entry for the gun in the hands matches that gun: rebuilt when it does not.</summary>
        public static void Check(BotOwner bot)
        {
            BotWeaponManager wm = bot?.WeaponManager;
            Player player = bot?.GetPlayer;
            if (wm?.info == null || player == null || bot.IsDead || !Settings.FreeStuckHands.Value)
            {
                return;
            }

            // Entries of a slot that now holds another gun (or none) are wrong for whoever reads them next
            foreach (EquipmentSlot slot in GunSlots)
            {
                if (wm.info.TryGetValue(slot, out BotWeaponInfo entry) && entry != null && entry.weapon != null
                    && player.Equipment?.GetSlot(slot)?.ContainedItem != entry.weapon && entry != wm._currentWeaponInfo)
                {
                    wm.info.Remove(slot);
                }
            }

            if (!(player.HandsController is Player.FirearmController fc) || !(fc.Item is Weapon gun))
            {
                return;
            }

            EquipmentSlot? held = SlotOf(player, gun);
            if (!held.HasValue)
            {
                return;
            }

            BotWeaponInfo current = wm._currentWeaponInfo;
            bool stale = !wm.info.TryGetValue(held.Value, out BotWeaponInfo info) || info == null || info.weapon != gun;
            if (!stale && current == info)
            {
                return;
            }

            if (stale)
            {
                string was = info?.weapon?.ShortName?.Localized();
                bool reloading = current?.Reload?.Reloading == true;
                info = new BotWeaponInfo(bot, gun, held.Value, wm.ChangeToMode);
                wm.info[held.Value] = info;
                Rebuilt++;
                Bottomless.Poke(bot);
                Log.Detail("weapon", () => $"{bot.Profile?.Nickname}: EFT still had its {held.Value} down as {(was ?? "nothing")} - its {gun.ShortName?.Localized()} " +
                                           $"is known anew{(reloading ? " (the old reload is dropped)" : "")}");
            }

            wm._currentWeaponInfo = info;
        }

        /// <summary>
        /// A reload animation in the hands that has run far longer than any reload takes: ended. True when it was ended.
        /// </summary>
        public static bool EndHungReload(BotOwner bot, Player.FirearmController fc, BotReload reload, string name)
        {
            float took = Time.time - reload._reloadStartTime;
            float limit = reload._reloadType == BotReload.EReloadType.MagReload ? 20f : 45f;
            if (took < limit || fc == null || !fc.IsInReloadOperation())
            {
                return false;
            }

            object op = fc.CurrentOperation;
            string opName = op?.GetType().Name ?? "?";
            bool ended = false;
            for (int i = 0; i < 2 && !ended; i++)
            {
                try
                {
                    fc.CurrentOperation?.FastForward();
                }
                catch (System.Exception)
                {
                    // A broken operation throws in its own end (the cylinder reload of a tube gun): tried once more, then ended by hand
                }

                ended = !fc.IsInReloadOperation();
            }

            if (!ended)
            {
                try
                {
                    Traverse.Create(fc.CurrentOperation).Property("State").SetValue(Player.EOperationState.Finished);
                    fc.InitiateOperation<Player.FirearmController.Idling>().Start();
                }
                catch (System.Exception e)
                {
                    Log.Detail("weapon", () => $"{name}: its hung {opName} could not be ended ({e.GetType().Name}: {e.Message})");
                }

                ended = !fc.IsInReloadOperation();
            }

            reload.Reloading = false;
            reload._reloadFailDebug = false;
            reload._nextReloadTime = Time.time + 1f;
            Aborted++;
            Log.Info($"LR Weapon: {name}'s reload animation ({opName}) had run for {took:0} s - {(ended ? "ended, the gun is free again" : "could not be ended")}");
            Check(bot);
            return true;
        }
    }

    /// <summary>The hands got a gun: EFT's entry for it must be that gun's (GunState).</summary>
    public class GunStateHandsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponManager), nameof(BotWeaponManager.UpdateFirearmsController));

        [PatchPostfix]
        protected static void PatchPostfix(BotWeaponManager __instance, BotOwner ____owner)
        {
            if (____owner != null && !____owner.IsDead)
            {
                Diag.Guard("GunStateHandsPatch", () => GunState.Check(____owner));
            }
        }
    }

    /// <summary>The active slot changed (EFT, LootingBots): its entry must be the gun now in it (GunState).</summary>
    public class GunStateSlotPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotWeaponManager), nameof(BotWeaponManager.CacheActiveEquipmentSlot));

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner ____owner)
        {
            if (____owner != null && !____owner.IsDead)
            {
                Diag.Guard("GunStateSlotPatch", () => GunState.Check(____owner));
            }
        }
    }
}
