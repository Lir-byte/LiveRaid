using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Magazines are kept, not thrown away (F12 -> 9 "Keep emptied magazines"). Every bot, bosses and their guards included: it is EFT's flaw, not a
    /// matter of behaviour.
    ///
    /// The bug that came back since the first versions - "Vitek of Reshala's guard aims at a PMC and never fires" (raid 2026-10-27 00:42: Vitek
    /// Zavodskoy fought iDkGK from 00:15, at 02:07 his rifle was empty, "no rounds left for any of its guns", and he stood aiming until he was shot at
    /// 03:31; Filya Zavodskoy of the same guard was dry 23 s into the raid). EFT, per the code:
    ///  - BotReload.ReloadMagazine finds a place for the magazine it takes out of the gun only in the rig and the pockets
    ///    (GetPrioritizedGridsForUnloadedObject without the bag) - with none free, ReloadExternalMagResult removes it and DropMod throws it on the
    ///    ground. A packed rig (the guards carry mags, grenades and meds in theirs) lost one magazine per reload.
    ///  - a bot reloads only from magazines in its rig and pockets (FastAccessSlots, top level); loose rounds (SPT puts 20 packs into every bot's
    ///    secure container) are put only into such magazines (AddAmmoToMagazines). With every spare magazine on the ground there is nothing to put
    ///    them in: CanReload asks for a weapon change, there is none - the gun stays empty for good, and SAIN keeps aiming with it.
    /// Here:
    ///  - the emptied magazine of a reload goes into the bag, else the secure container, when the rig and pockets are full (MagKeeperReloadPatch);
    ///  - once a second, a bot whose gun takes magazines and that has no loaded spare magazine within reach: a magazine of that gun out of reach (bag,
    ///    secure container, inside a pouch) is moved into the rig or pockets, an empty one there is filled from the loose rounds, and with no magazine
    ///    at all one lying within 4 m (the dropped ones) is picked up.
    /// </summary>
    public static class MagKeeper
    {
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly List<Magazine> _mags = new List<Magazine>();
        // 1.2.6: the same magazine is not carried to the rig / filled again and again (raid 2026-11-07: one 8-round magazine "taken out of the bag" 7 times,
        // and EFT refused a refill with "hands controller can't perform this operation" while the hands were busy). Id of the magazine -> when it may be tried again
        private static readonly Dictionary<string, float> _carryAgain = new Dictionary<string, float>();
        private static readonly Dictionary<string, (string mag, int tries, float at)> _fillTries = new Dictionary<string, (string, int, float)>();
        public static int Backoffs { get; private set; }
        private static float _tick;

        public static int KeptInBag { get; private set; }
        public static int Dropped { get; private set; }
        public static int BroughtToRig { get; private set; }
        public static int Filled { get; private set; }
        public static int PickedUp { get; private set; }
        public static int NoMagazine { get; private set; }

        public static string Summary =>
            $"magazines: emptied ones kept in the bag / secure container {KeptInBag} (thrown away, nowhere to put {Dropped}), brought from out of reach into the rig {BroughtToRig}, " +
            $"filled from loose rounds {Filled}, picked up from the ground {PickedUp}, bots with no magazine for their gun at all {NoMagazine}, repeats of one magazine held back {Backoffs}";

        public static void Clear()
        {
            _next.Clear();
            _carryAgain.Clear();
            _fillTries.Clear();
            _tick = 0f;
            KeptInBag = Dropped = BroughtToRig = Filled = PickedUp = NoMagazine = Backoffs = 0;
        }

        private static string Name(BotOwner bot) => bot?.Profile?.Nickname ?? "?";

        internal static bool IsGunSlot(EquipmentSlot slot) =>
            slot == EquipmentSlot.FirstPrimaryWeapon || slot == EquipmentSlot.SecondPrimaryWeapon || slot == EquipmentSlot.Holster;

        /// <summary>A free spot for the item in the given equipment slots' containers, the way EFT looks (filters and size).</summary>
        internal static GridItemAddress FreeSpot(InventoryEquipment eq, Item item, params EquipmentSlot[] slots)
        {
            foreach (EquipmentSlot s in slots)
            {
                if (!(eq.GetSlot(s)?.ContainedItem is CompoundItem c) || c.Grids == null)
                {
                    continue;
                }

                foreach (Grid g in c.Grids)
                {
                    if (g == null || !g.CanAccept(item))
                    {
                        continue;
                    }

                    GridItemAddress a = g.FindLocationForItem(item);
                    if (a != null)
                    {
                        return a;
                    }
                }
            }

            return null;
        }

        /// <summary>The magazine sits at the top level of the rig or the pockets - where EFT's bots reach for one.</summary>
        internal static bool InReach(Item item)
        {
            Item parent = item?.Parent?.Container?.ParentItem;
            if (parent == null)
            {
                return false;
            }

            return parent is Vest || parent is Pockets;
        }

        /// <summary>Where the magazine EFT takes out of the gun should go: null = EFT's own choice works (or nothing would).</summary>
        internal static GridItemAddress PlaceForEmptied(BotOwner bot, Magazine old)
        {
            InventoryEquipment eq = bot?.GetPlayer?.InventoryController?.Inventory?.Equipment;
            if (eq == null || old == null)
            {
                return null;
            }

            foreach (Grid g in eq.GetPrioritizedGridsForUnloadedObject())
            {
                if (g?.FindLocationForItem(old) != null)
                {
                    return null;
                }
            }

            return FreeSpot(eq, old, EquipmentSlot.Backpack, EquipmentSlot.SecuredContainer);
        }

        internal static void NoteKept(BotOwner bot, Magazine old, GridItemAddress at)
        {
            KeptInBag++;
            string where = at?.Container?.ParentItem is Backpack ? "bag" : "secure container";
            Log.Detail("weapon", () => $"{Name(bot)}: rig and pockets full - its emptied {old.ShortName.Localized()} goes into its {where}, not on the ground");
        }

        internal static void NoteDropped(BotOwner bot, Magazine old)
        {
            Dropped++;
            Log.Detail("weapon", () => $"{Name(bot)}: no room anywhere for its emptied {old.ShortName.Localized()} - EFT throws it on the ground");
        }

        public static void Tick()
        {
            if (!Settings.KeepMagazines.Value || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player p in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = p?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || (_next.TryGetValue(bot.ProfileId, out float at) && now < at)
                    || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                _next[bot.ProfileId] = now + 1f;
                try
                {
                    Restock(bot, now, false);
                }
                catch (System.Exception e)
                {
                    Diag.Report("MagKeeper", e);
                }
            }
        }

        /// <summary>
        /// A loaded spare magazine for the gun in the hands is made reachable, if the bot has what it takes. True when something was done (a reload can
        /// follow), false when the bot needs nothing or has nothing to do it with.
        /// </summary>
        public static bool Restock(BotOwner bot, float now, bool fromReload)
        {
            Player player = bot.GetPlayer;
            if (!(player?.HandsController is Player.FirearmController fc) || fc.IsInReloadOperation() || player.InventoryController == null)
            {
                return false;
            }

            // LootingBots moves items of a looting bot: no second hand in its bag meanwhile
            if (DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) == "Looting")
            {
                return false;
            }

            Weapon gun = fc.Item;
            BotReload reload = bot.WeaponManager?.Reload;
            if (gun == null || reload == null || reload.Reloading || gun.ReloadMode != Weapon.EReloadMode.ExternalMagazine)
            {
                return false;
            }

            Slot magSlot = gun.GetMagazineSlot();
            if (magSlot == null)
            {
                return false;
            }

            Magazine inGun = gun.GetCurrentMagazine();
            // A full gun needs nothing; otherwise the bot wants one loaded spare magazine in reach
            if (inGun != null && inGun.Count >= inGun.MaxCount && !fromReload)
            {
                return false;
            }

            InventoryController ic = player.InventoryController;
            InventoryEquipment eq = ic.Inventory.Equipment;
            Magazine reachEmpty = null;
            Magazine outOfReach = null;
            _mags.Clear();
            foreach (Item it in eq.GetAllItems())
            {
                if (it is Magazine m && m != inGun && magSlot.CanAccept(m) && !MeleeSwitch.IsInside(m, gun))
                {
                    _mags.Add(m);
                }
            }

            foreach (Magazine m in _mags)
            {
                if (InReach(m))
                {
                    if (m.Count > 0)
                    {
                        return false;
                    }

                    reachEmpty ??= m;
                }
                else if (outOfReach == null || m.Count > outOfReach.Count)
                {
                    outOfReach = m;
                }
            }

            // 1. A magazine out of reach (loaded, or empty while there are rounds to fill it) into the rig / pockets
            if (outOfReach != null && (outOfReach.Count > 0 || reachEmpty == null)
                && !(_carryAgain.TryGetValue(outOfReach.Id.ToString(), out float carryAt) && now < carryAt && ++Backoffs > 0))
            {
                GridItemAddress to = FreeSpot(eq, outOfReach, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets);
                if (to != null)
                {
                    Magazine moved = outOfReach;
                    string from = moved.Parent?.Container?.ParentItem?.ShortName?.Localized() ?? "its kit";
                    var move = ItemManipulator.Move(moved, to, ic, true);
                    if (!move.Failed)
                    {
                        ic.TryRunNetworkTransaction(move, null);
                        _carryAgain[moved.Id.ToString()] = now + 120f;
                        BroughtToRig++;
                        Log.Detail("weapon", () => $"{Name(bot)}: no loaded magazine in its rig - takes its {moved.ShortName.Localized()} ({moved.Count} rounds) out of {from}");
                        return true;
                    }
                }
            }

            // 2. An empty magazine in reach and loose rounds for it: filled (EFT's own refill, from rig, pockets, bag and secure container)
            if (reachEmpty != null)
            {
                string emptyId = reachEmpty.Id.ToString();
                if (_fillTries.TryGetValue(bot.ProfileId, out var tried) && tried.mag == emptyId && tried.tries >= 2 && now - tried.at < 90f)
                {
                    // Two refills of this very magazine in a row and it is still empty: EFT is refusing (busy hands) - not again for 90 s
                    Backoffs++;
                    return false;
                }

                if (reload.FindAmmoToReloadMag(gun, reachEmpty) != null)
                {
                    bool again = _fillTries.TryGetValue(bot.ProfileId, out var last) && last.mag == emptyId && now - last.at < 20f;
                    _fillTries[bot.ProfileId] = (emptyId, again ? last.tries + 1 : 1, again && last.tries + 1 >= 2 ? now : (again ? last.at : now));
                    Bottomless.FillMagazine(bot, reload, gun, reachEmpty);
                    Filled++;
                    Log.Detail("weapon", () => $"{Name(bot)}: fills its empty {reachEmpty.ShortName.Localized()} with loose rounds");
                    return true;
                }

                return false;
            }

            if (_mags.Count > 0)
            {
                return false;
            }

            // 3. No magazine for the gun anywhere: one on the ground within 4 m (thrown there by a reload)
            if (!fromReload && (inGun == null || inGun.Count == 0) && PickUpDropped(bot, player, ic, eq, magSlot, gun))
            {
                return true;
            }

            if (inGun == null || inGun.Count == 0)
            {
                NoMagazine++;
            }

            return false;
        }

        private static bool PickUpDropped(BotOwner bot, Player player, InventoryController ic, InventoryEquipment eq, Slot magSlot, Weapon gun)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world?.LootItems == null)
            {
                return false;
            }

            Vector3 pos = player.Position;
            LootItem best = null;
            float bestD = 16f;
            foreach (LootItem li in world.LootItems.GetValuesEnumerator())
            {
                if (li == null || !(li.Item is Magazine m) || !magSlot.CanAccept(m))
                {
                    continue;
                }

                float d = (li.transform.position - pos).sqrMagnitude;
                if (d < bestD && Mathf.Abs(li.transform.position.y - pos.y) < 2f)
                {
                    best = li;
                    bestD = d;
                }
            }

            if (best == null)
            {
                return false;
            }

            Magazine mag = (Magazine)best.Item;
            GridItemAddress to = FreeSpot(eq, mag, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets, EquipmentSlot.Backpack);
            if (to == null)
            {
                return false;
            }

            var move = ItemManipulator.Move(mag, to, ic, true);
            if (move.Failed)
            {
                return false;
            }

            ic.TryRunNetworkTransaction(move, null);
            PickedUp++;
            Log.Info($"LR Weapon: {Name(bot)} has no magazine left for its {gun.ShortName.Localized()} - picks up the {mag.ShortName.Localized()} lying at its feet ({Mathf.Sqrt(bestD):0.0} m)");
            return true;
        }
    }

    /// <summary>
    /// EFT's BotReload.ReloadMagazine with one change: the magazine out of the gun goes into the bag or the secure container when the rig and pockets
    /// are full, instead of on the ground (MagKeeper). Taken over only in that case - otherwise EFT's own method runs untouched.
    /// </summary>
    public class MagKeeperReloadPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotReload), nameof(BotReload.ReloadMagazine));

        [PatchPrefix]
        protected static bool PatchPrefix(BotReload __instance, Magazine foundMag)
        {
            try
            {
                BotOwner bot0 = __instance._owner;
                Magazine old0 = __instance._weapon?.GetCurrentMagazine();
                // 1.3.3: a swap that adds nothing is not made (ReloadWorthPatch), nor the third one within 14 s while the gun is still loaded
                if (bot0 != null && old0 != null && foundMag != null && ReloadWorthPatch.Futile(__instance, bot0, old0, foundMag))
                {
                    __instance.Reloading = false;
                    return false;
                }

                if (!Settings.KeepMagazines.Value)
                {
                    return true;
                }

                BotOwner bot = bot0;
                Magazine old = old0;
                IFirearmHandsController shoot = __instance.ShootController;
                if (bot == null || old == null || shoot == null || foundMag == null)
                {
                    return true;
                }

                GridItemAddress place = MagKeeper.PlaceForEmptied(bot, old);
                if (place == null)
                {
                    InventoryEquipment eq = bot.GetPlayer?.InventoryController?.Inventory?.Equipment;
                    bool rigHasRoom = eq != null && eq.GetPrioritizedGridsForUnloadedObject().Any(g => g?.FindLocationForItem(old) != null);
                    if (!rigHasRoom)
                    {
                        MagKeeper.NoteDropped(bot, old);
                    }

                    return true;
                }

                MagKeeper.NoteKept(bot, old, place);
                __instance._reloadType = BotReload.EReloadType.MagReload;
                bot.BotTalk.Say(EPhraseTrigger.NeedAmmo);
                shoot.ReloadMag(foundMag, place, result =>
                {
                    __instance._nextReloadTime = Time.time + 0.5f;
                    __instance.Reloading = false;
                    __instance.AddAmmoToMagazines();
                });
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("MagKeeperReloadPatch", e);
                return true;
            }
        }
    }

    /// <summary>
    /// "Endless reload" (always on, 1.3.3). Raid 2026-11-09 22:18: 42MadBeast (a PMC, Mk-47 with 20-round PMAGs) reloaded every 3-4 s for three minutes (53 times, "emptied PMAG goes into its bag"),
    /// shot nothing and was beaten up by a player scav. EFT per the IL: SAIN reloads when the magazine in the gun is under 80 % (ammo ratio = rounds / the magazine's capacity); BotReloadMagazine.CanReload refuses only
    /// when the magazine in the gun is FULL, otherwise it takes GetMagazineForReload - the reachable magazine with the MOST rounds, bag included, with no check that it holds more than the one in the gun.
    /// With every spare magazine at 15 of 20 the bot swaps one 15 for another 15 (the old one goes to the bag - with the rig full, since MagKeeper - or the rig, where the next swap finds it again), the ratio stays at
    /// 0.75 and SAIN's self-action "Reload" never ends: the bot does nothing else. Here: a swap after which the gun does not hold more rounds than it does (the found magazine has no more than the one in the gun, and the gun is
    /// not empty) is refused in CanReload (so SAIN does not even start it) and again in ReloadMagazine; and a loaded gun (4+ rounds) that has had 3 swaps in 14 s is left alone for 20 s.
    /// </summary>
    public class ReloadWorthPatch : ModulePatch
    {
        private static readonly Dictionary<string, List<float>> _swaps = new Dictionary<string, List<float>>();
        private static readonly Dictionary<string, float> _said = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _holdUntil = new Dictionary<string, float>();

        public static int Refused { get; private set; }
        public static int Held { get; private set; }

        public static string Summary => $"magazine swaps that would add no rounds refused {Refused}, endless reload loops held {Held}";

        public static void ClearCounters()
        {
            Refused = Held = 0;
            _swaps.Clear();
            _said.Clear();
            _holdUntil.Clear();
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotReloadMagazine), nameof(BotReloadMagazine.CanReload), new[] { typeof(bool), typeof(Magazine).MakeByRefType(), typeof(List<Ammo>).MakeByRefType() });

        [PatchPostfix]
        protected static void PatchPostfix(BotReloadMagazine __instance, ref bool __result, ref Magazine foundMag)
        {
            try
            {
                if (!__result || foundMag == null || !Settings.Enabled.Value)
                {
                    return;
                }

                BotOwner bot = __instance._owner;
                Magazine cur = bot?.WeaponManager?.CurrentWeapon?.GetCurrentMagazine();
                if (cur == null || !Pointless(bot, cur, foundMag, out string why))
                {
                    return;
                }

                __result = false;
                foundMag = null;
                __instance._nextReloadTime = Time.time + 3f;
                Note(bot, why);
            }
            catch (System.Exception e)
            {
                Diag.Report("ReloadWorthPatch", e);
            }
        }

        /// <summary>True when the swap should not be made (and counts the swaps that are made).</summary>
        internal static bool Futile(BotReload reload, BotOwner bot, Magazine old, Magazine found)
        {
            if (!Settings.Enabled.Value)
            {
                return false;
            }

            if (Pointless(bot, old, found, out string why))
            {
                reload._nextReloadTime = Time.time + 3f;
                Note(bot, why);
                return true;
            }

            // Counted here: a swap that is going to be made
            float now = Time.time;
            if (!_swaps.TryGetValue(bot.ProfileId, out List<float> list))
            {
                _swaps[bot.ProfileId] = list = new List<float>();
            }

            list.RemoveAll(t => now - t > 14f);
            list.Add(now);
            return false;
        }

        private static bool Pointless(BotOwner bot, Magazine cur, Magazine found, out string why)
        {
            why = null;
            float now = Time.time;
            if (cur.Count <= 0)
            {
                return false;
            }

            // 1.4.10: a full magazine in a gun with an EMPTY CHAMBER is not "the same rounds": the swap loads the chamber. Raid 2026-11-11 17:27: Zhiguli's Kedr stood at
            // chamber 0, magazine 20/20 and the best spare 20 - "a swap adds nothing" - and he did not fire for 6 s at 17 m
            Weapon curGun = bot.WeaponManager?.CurrentWeapon;
            if (curGun != null && curGun.HasChambers && curGun.ChamberAmmoCount == 0 && curGun.ReloadMode == Weapon.EReloadMode.ExternalMagazine)
            {
                return false;
            }

            if (found.Count <= cur.Count)
            {
                why = $"the best spare magazine holds {found.Count} rounds, the one in the gun {cur.Count} - a swap adds nothing";
                Refused++;
                return true;
            }

            if (cur.Count >= 4 && _swaps.TryGetValue(bot.ProfileId, out List<float> list) && list.Count(t => now - t <= 14f) >= 3)
            {
                _holdUntil[bot.ProfileId] = now + 20f;
            }

            if (cur.Count >= 4 && _holdUntil.TryGetValue(bot.ProfileId, out float until) && now < until)
            {
                why = $"three swaps within 14 s with {cur.Count} rounds in the gun - left alone for a while";
                Held++;
                return true;
            }

            return false;
        }

        private static void Note(BotOwner bot, string why)
        {
            float now = Time.time;
            if (_said.TryGetValue(bot.ProfileId, out float at) && now - at < 30f)
            {
                return;
            }

            _said[bot.ProfileId] = now;
            Log.Info($"LR Weapon: {bot.Profile?.Nickname ?? "?"} does not reload - {why}");
        }
    }
}
