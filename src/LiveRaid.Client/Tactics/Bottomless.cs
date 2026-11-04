using System.Collections.Generic;
using System.Linq;
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
    /// Summoned ammo (F12 -> 9 "Bottomless ammo"). Every bot, bosses and their guards included. Off = SPT's own: only what the bot was given.
    ///
    /// Raid 2026-10-27 03:29 (Epicenter): gley (RSASS) stood with an empty gun for 27 minutes with 800 loose rounds in his secure container - the magazines
    /// ran out, not the rounds: SPT puts spare magazines only into the rig and the pockets, and EFT's bots reload only from a loaded magazine there and never
    /// fill the one in the gun. Version 0.8.6 then topped up the loose rounds of every gun to several magazines and made magazines without a count; raid
    /// 2026-10-30: the stacks filled the bags of scavs (LootingBots counts the cells of bag, rig and pockets), a bot was "full" after one find.
    ///
    /// The rules (0.9.5), the same for every bot:
    ///  - Loose rounds: when a gun has no loaded spare magazine and the bot carries too few rounds to fill one, a stack of 30-40 rounds is summoned. Only when
    ///    the last summoned stack of that calibre is used up (rounds found in the raid are not counted against it and not limited). A bot with a secure
    ///    container (a PMC) gets them there and only there; one without (a scav) in the rig or the pockets.
    ///  - Magazines: never made for the gun the bot started the raid with (SPT gave it its magazines); with no spare one left, the empty magazine of the
    ///    gun itself is taken out, filled from the loose rounds and put back by the reload, as a player does (0.9.6). A gun found in the raid gets one spare magazine, made at
    ///    the moment of a reload, if the bot carries no magazine for it (taken from the body it took the gun from, or found) - and again only when that
    ///    count is back to nothing.
    ///  - Summoned things are not the bot's loot: they do not count as taken cells (LootingBots' free space) and give way - they are removed - when a find
    ///    needs the cell.
    /// The reload is as any other - the animation, the time, the old magazine going into the bag (MagKeeper).
    ///
    /// Learned in the first Fika raid (2026-10-28):
    ///  - the round is chosen by the calibre of the GUN, not by Weapon.CurrentAmmoTemplate: with nothing loaded EFT answers with the gun's default round,
    ///    and the PP-9 "Klin" (9x18 PMM) has the 9x18 PM round as its default;
    ///  - in Fika a bot's inventory operation is broadcast to the other players and run a frame later (BotInventoryController.Execute), so a magazine
    ///    filled just now still reads empty: it is waited for a few seconds, not given up on;
    ///  - nothing is changed in a stack another player could loot: only new stacks are made.
    /// </summary>
    public static class Bottomless
    {
        private static readonly EquipmentSlot[] SecureOnly = { EquipmentSlot.SecuredContainer };
        private static readonly EquipmentSlot[] RigSlots = { EquipmentSlot.TacticalVest, EquipmentSlot.Pockets };
        private static readonly EquipmentSlot[] MagSlotsRig = { EquipmentSlot.TacticalVest, EquipmentSlot.Pockets, EquipmentSlot.Backpack };
        private static readonly EquipmentSlot[] MagSlotsSecure = { EquipmentSlot.SecuredContainer, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets, EquipmentSlot.Backpack };
        // The containers whose free cells LootingBots counts
        private static readonly EquipmentSlot[] CountedSlots = { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets };

        private sealed class Pending
        {
            public Magazine Mag;
            public float Until;
            public bool Reused;
            public string Where;
        }

        // A thing summoned for a bot; Rounds = the size of a summoned stack (a bigger one has found rounds merged into it: no longer ours to take away)
        private sealed class Air
        {
            public Item It;
            public int Rounds;
        }

        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _noMake = new Dictionary<string, float>();
        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private static readonly Dictionary<string, HashSet<string>> _original = new Dictionary<string, HashSet<string>>();
        private static readonly Dictionary<string, string> _stack = new Dictionary<string, string>();
        private static readonly Dictionary<string, List<Air>> _air = new Dictionary<string, List<Air>>();
        private static readonly HashSet<string> _refusedNoted = new HashSet<string>();
        private static readonly Dictionary<string, float> _unloadTried = new Dictionary<string, float>();
        private static float _tick;
        private static int _infoLeft = 40;

        public static int RoundsAdded { get; private set; }
        public static int StacksMade { get; private set; }
        public static int BotsToppedUp { get; private set; }
        public static int MagsMade { get; private set; }
        public static int MagsReused { get; private set; }
        public static int MagsFailed { get; private set; }
        public static int NoRoom { get; private set; }
        public static int RefusedStart { get; private set; }
        public static int Evicted { get; private set; }
        public static int Unloaded { get; private set; }
        public static int Restored { get; private set; }

        private static readonly HashSet<string> _toppedBots = new HashSet<string>();

        public static string Summary =>
            $"summoned ammo: round stacks {StacksMade} ({RoundsAdded} rounds, bots {BotsToppedUp}, nowhere to put {NoRoom}), magazines made {MagsMade} " +
            $"(an empty one filled {MagsReused}, could not make {MagsFailed}), the gun's own empty magazine taken out to be filled {Unloaded} " +
            $"(put back in the gun when the rounds would not go in {Restored}), " +
            $"refused for a gun the bot started with {RefusedStart}, taken away for a find {Evicted}";

        public static void Clear()
        {
            _next.Clear();
            _noMake.Clear();
            _pending.Clear();
            _original.Clear();
            _stack.Clear();
            _air.Clear();
            _refusedNoted.Clear();
            _unloadTried.Clear();
            _toppedBots.Clear();
            _tick = 0f;
            _infoLeft = 40;
            RoundsAdded = StacksMade = BotsToppedUp = MagsMade = MagsReused = MagsFailed = NoRoom = RefusedStart = Evicted = Unloaded = Restored = 0;
        }

        private static string Name(BotOwner bot) => bot?.Profile?.Nickname ?? "?";

        private static bool Eligible(BotOwner bot) =>
            Settings.BottomlessAmmo.Value && bot != null && !bot.IsDead && bot.GetPlayer?.InventoryController != null && !Spectator.TakeOver.IsTaken(bot);

        /// <summary>The bot's guns when first seen: those are the ones SPT gave it, whatever it picks up later is found.</summary>
        private static void Snapshot(BotOwner bot)
        {
            if (_original.ContainsKey(bot.ProfileId))
            {
                return;
            }

            InventoryEquipment eq = bot.GetPlayer?.InventoryController?.Inventory?.Equipment;
            if (eq == null)
            {
                return;
            }

            var guns = new HashSet<string>();
            foreach (Item it in eq.GetAllItems())
            {
                if (it is Weapon)
                {
                    guns.Add(it.Id);
                }
            }

            _original[bot.ProfileId] = guns;
        }

        /// <summary>The gun is one the bot started the raid with (also: not known - then it is treated so, nothing is made for it).</summary>
        private static bool IsOriginal(BotOwner bot, Weapon gun) => !_original.TryGetValue(bot.ProfileId, out HashSet<string> guns) || guns.Contains(gun.Id);

        public static void Tick()
        {
            if (!Settings.BottomlessAmmo.Value || Time.time < _tick)
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
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
                {
                    continue;
                }

                try
                {
                    Snapshot(bot);
                }
                catch (System.Exception e)
                {
                    Diag.Report("Bottomless", e);
                }

                if ((_next.TryGetValue(bot.ProfileId, out float at) && now < at) || !Eligible(bot))
                {
                    continue;
                }

                _next[bot.ProfileId] = now + 3f;
                try
                {
                    // LootingBots moves items of a looting bot: no second hand in its bag meanwhile
                    if (DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) == "Looting")
                    {
                        continue;
                    }

                    InventoryEquipment eq = bot.GetPlayer.InventoryController.Inventory?.Equipment;
                    if (eq != null && bot.GetPlayer.HandsController is Player.FirearmController fc && !fc.IsInReloadOperation() && fc.Item is Weapon hands
                        && bot.WeaponManager?.Reload != null && !bot.WeaponManager.Reload.Reloading)
                    {
                        Prepare(bot, eq, hands);
                    }
                }
                catch (System.Exception e)
                {
                    Diag.Report("Bottomless", e);
                }
            }
        }

        /// <summary>The round to stock for the gun: one of the gun's calibre, whatever EFT's CurrentAmmoTemplate says (null = none found).</summary>
        private static AmmoTemplate RoundFor(Weapon gun, InventoryEquipment eq, string caliber)
        {
            AmmoTemplate tpl = gun.CurrentAmmoTemplate;
            if (tpl != null && tpl.Caliber.Replace("Caliber", string.Empty) == caliber)
            {
                return tpl;
            }

            // Rounds of the right calibre the bot already carries, in the gun or out of it
            foreach (Item it in eq.GetAllItems())
            {
                if (it is Ammo a && a.Caliber == caliber && a.Template is AmmoTemplate carried)
                {
                    return carried;
                }
            }

            // What the chamber / the magazine of the gun accepts
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            IEnumerable<ItemFilter> filters = Enumerable.Empty<ItemFilter>();
            if (gun.HasChambers && gun.Chambers.Length > 0 && gun.Chambers[0].Filters != null)
            {
                filters = filters.Concat(gun.Chambers[0].Filters);
            }

            Magazine mag = gun.GetCurrentMagazine();
            if (mag?.Cartridges?.Filters != null)
            {
                filters = filters.Concat(mag.Cartridges.Filters);
            }

            foreach (ItemFilter f in filters)
            {
                if (f?.Filter == null)
                {
                    continue;
                }

                foreach (MongoID id in f.Filter)
                {
                    if (factory.ItemTemplates.TryGetValue(id, out ItemTemplate t) && t is AmmoTemplate at && at.Caliber.Replace("Caliber", string.Empty) == caliber)
                    {
                        return at;
                    }
                }
            }

            return null;
        }

        /// <summary>The loose rounds of the gun's calibre the bot carries (not in a gun or a magazine), and whether the stack summoned last is still there.</summary>
        private static int Loose(BotOwner bot, InventoryEquipment eq, Weapon gun, string caliber, out bool stackAlive)
        {
            _stack.TryGetValue(bot.ProfileId + caliber, out string stackId);
            stackAlive = false;
            int loose = 0;
            foreach (Item it in eq.GetAllItems())
            {
                if (!(it is Ammo a) || a.Caliber != caliber || a.Parent?.Container?.ParentItem is Magazine || a.Parent?.Container?.ParentItem is Weapon || MeleeSwitch.IsInside(a, gun))
                {
                    continue;
                }

                loose += a.StackObjectsCount;
                if (stackId != null && a.Id == stackId)
                {
                    stackAlive = true;
                }
            }

            return loose;
        }

        private static int Capacity(Weapon gun)
        {
            Magazine inGun = gun.GetCurrentMagazine();
            return Mathf.Max(5, inGun != null ? inGun.MaxCount : 30);
        }

        /// <summary>
        /// Rounds for the gun: when the bot carries fewer than <paramref name="need"/> and the stack summoned last is used up, a new stack of 30-40 (F12) is
        /// made - in the secure container for a bot that has one, else in the rig or pockets. Returns the rounds made (0 = none: enough, not used up, no room).
        /// </summary>
        private static int SummonRounds(BotOwner bot, InventoryEquipment eq, Weapon gun, int need)
        {
            string caliber = gun.AmmoCaliber;
            if (string.IsNullOrEmpty(caliber))
            {
                return 0;
            }

            int loose = Loose(bot, eq, gun, caliber, out bool alive);
            if (loose >= need || alive)
            {
                return 0;
            }

            AmmoTemplate tpl = RoundFor(gun, eq, caliber);
            if (tpl == null)
            {
                _next[bot.ProfileId] = Time.time + 60f;
                Log.Detail("weapon", () => $"{Name(bot)}: no round of the calibre {caliber} found for its {gun.ShortName.Localized()}");
                return 0;
            }

            Ammo fresh = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), tpl._id, null) as Ammo;
            if (fresh == null || fresh.Caliber != caliber)
            {
                return 0;
            }

            int top = Mathf.Max(10, Settings.BottomlessReserve.Value);
            int n = Mathf.Clamp(Random.Range(Mathf.Max(5, top - 10), top + 1), 1, Mathf.Max(1, fresh.StackMaxSize));
            fresh.StackObjectsCount = n;
            bool secure = eq.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem != null;
            if (!BotSetup.Place(bot, fresh, secure ? SecureOnly : RigSlots))
            {
                // Nowhere to put it: not tried again for a while
                NoRoom++;
                _next[bot.ProfileId] = Time.time + 30f;
                return 0;
            }

            _stack[bot.ProfileId + caliber] = fresh.Id;
            Mark(bot, fresh, n);
            RoundsAdded += n;
            StacksMade++;
            if (_toppedBots.Add(bot.ProfileId))
            {
                BotsToppedUp++;
            }

            if (_infoLeft > 0)
            {
                _infoLeft--;
                Log.Info($"LR Ammo: {Name(bot)} was down to {loose} rounds for its {gun.ShortName.Localized()} - {n} {tpl.ShortName.Localized()} summoned into its {(secure ? "secure container" : "rig or pockets")}");
            }
            else
            {
                Log.Detail("weapon", () => $"{Name(bot)}: {n} {tpl.ShortName.Localized()} summoned for the {gun.ShortName.Localized()} (was {loose})");
            }

            return n;
        }

        /// <summary>The gun in the hands has a spare magazine to go on with: an empty one the bot carries and no round to fill it - rounds are summoned and put in.</summary>
        private static void Prepare(BotOwner bot, InventoryEquipment eq, Weapon gun)
        {
            if (string.IsNullOrEmpty(gun.AmmoCaliber))
            {
                return;
            }

            if (gun.ReloadMode != Weapon.EReloadMode.ExternalMagazine)
            {
                // Pump, bolt, revolver: rounds only when the gun is dry
                if (gun.GetAllItems().OfType<Ammo>().Sum(a => a.StackObjectsCount) <= 0)
                {
                    SummonRounds(bot, eq, gun, 8);
                }

                return;
            }

            Slot magSlot = gun.GetMagazineSlot();
            BotReload reload = bot.WeaponManager?.Reload;
            if (magSlot == null || reload == null)
            {
                return;
            }

            FindMagazines(eq, gun, magSlot, gun.GetCurrentMagazine(), out Magazine loaded, out Magazine empty);
            if (loaded != null || empty == null)
            {
                return;
            }

            if (SummonRounds(bot, eq, gun, Capacity(gun)) > 0)
            {
                SafeReload.Run(() => reload.AddAmmoToMag(gun, empty));
            }
        }

        /// <summary>The magazines of the gun the bot carries (not the one in the gun): the best loaded one, the best empty one (those in reach first).</summary>
        private static void FindMagazines(InventoryEquipment eq, Weapon gun, Slot magSlot, Magazine inGun, out Magazine loaded, out Magazine empty)
        {
            loaded = null;
            empty = null;
            foreach (Item it in eq.GetAllItems())
            {
                if (!(it is Magazine m) || m == inGun || !magSlot.CanAccept(m) || MeleeSwitch.IsInside(m, gun))
                {
                    continue;
                }

                if (m.Count > 0)
                {
                    bool better = loaded == null || (MagKeeper.InReach(m) && !MagKeeper.InReach(loaded)) || (MagKeeper.InReach(m) == MagKeeper.InReach(loaded) && m.Count > loaded.Count);
                    if (better)
                    {
                        loaded = m;
                    }
                }
                else if (empty == null || (MagKeeper.InReach(m) && !MagKeeper.InReach(empty)))
                {
                    empty = m;
                }
            }
        }

        /// <summary>
        /// The bot could be given a loaded magazine for this gun when its reload asks: it carries one, or an empty one to fill (rounds it has or that may be
        /// summoned), or the gun was found in the raid and a magazine may be made. MeleeSwitch asks this before it takes a knife for a gun it takes to be dry.
        /// </summary>
        public static bool CanSupply(BotOwner bot, Weapon gun)
        {
            if (!Eligible(bot) || gun == null || string.IsNullOrEmpty(gun.AmmoCaliber))
            {
                return false;
            }

            InventoryEquipment eq = bot.GetPlayer.InventoryController.Inventory?.Equipment;
            if (eq == null)
            {
                return false;
            }

            int loose = Loose(bot, eq, gun, gun.AmmoCaliber, out bool alive);
            bool rounds = loose > 0 || !alive;
            Slot magSlot = gun.ReloadMode == Weapon.EReloadMode.ExternalMagazine ? gun.GetMagazineSlot() : null;
            if (magSlot == null)
            {
                return rounds;
            }

            FindMagazines(eq, gun, magSlot, gun.GetCurrentMagazine(), out Magazine loaded, out Magazine empty);
            if (loaded != null)
            {
                return true;
            }

            // No magazine carried: the empty one in the gun is taken out and filled (Unload), a found gun may have one made
            return empty != null || gun.GetCurrentMagazine() != null ? rounds : !IsOriginal(bot, gun);
        }

        /// <summary>
        /// A loaded magazine for the gun: one the bot carries, else an empty one filled, else (a gun found in the raid) one made. Null = none yet (a magazine
        /// being filled is waited for) or the bot cannot be helped; EFT then goes its way. EFT asks "can I reload" several times a second - the same magazine
        /// is handed back until it is used.
        /// </summary>
        internal static Magazine Conjure(BotOwner bot, Weapon gun)
        {
            if (!Eligible(bot) || gun == null)
            {
                return null;
            }

            InventoryController ic = bot.GetPlayer.InventoryController;
            InventoryEquipment eq = ic.Inventory?.Equipment;
            BotReload reload = bot.WeaponManager?.Reload;
            Slot magSlot = gun.GetMagazineSlot();
            if (eq == null || reload == null || magSlot == null)
            {
                return null;
            }

            string id = bot.ProfileId;
            Magazine inGun = gun.GetCurrentMagazine();
            FindMagazines(eq, gun, magSlot, inGun, out Magazine loaded, out Magazine empty);
            _pending.TryGetValue(id, out Pending pend);
            if (loaded != null)
            {
                if (pend != null && pend.Mag == loaded)
                {
                    _pending.Remove(id);
                    Done(bot, gun, loaded, pend.Reused, pend.Where);
                }

                return loaded;
            }

            float now = Time.time;
            if (pend != null)
            {
                if (now < pend.Until)
                {
                    // Fika runs the operation a frame later
                    return null;
                }

                _pending.Remove(id);
                // Raid 2026-11-02 15:58: 8 guns had their empty magazine taken out and none was filled - the gun stood without a magazine for minutes
                // (SAIN threw 880 NullReferenceExceptions in CalcAmmoRatio, cutting the cover steering of those bots short on every tick). The empty
                // magazine goes back into the gun, and the gun is not unloaded again for five minutes
                RestoreEmpty(bot, gun, pend.Mag);
                _unloadTried[id + gun.Id] = now + 290f;
                return Failed(bot, gun, now, "the magazine stayed empty - the rounds did not go in");
            }

            if (_noMake.TryGetValue(id, out float until) && now < until)
            {
                return null;
            }

            Magazine target = empty;
            string where = null;
            if (target == null)
            {
                // The bot carries no magazine for the gun. The one it started the raid with has its SPT magazines and no more - but the one in it is
                // taken out and filled as a player does it (raid 2026-10-30 23:28: RedBeard's P90 and a guard's RSASS stood empty with rounds in the
                // secure container, the bots swapped from one dry gun to the next); a found gun gets a spare
                if (IsOriginal(bot, gun))
                {
                    if (Unload(bot, ic, eq, gun, inGun, now))
                    {
                        return null;
                    }

                    if (_refusedNoted.Add(id + gun.Id))
                    {
                        RefusedStart++;
                        Log.Detail("weapon", () => $"{Name(bot)}: no loaded magazine for its {gun.ShortName.Localized()}, and it is a gun it started the raid with - none is made");
                    }

                    return null;
                }

                string tplId = inGun?.Template?._id ?? gun.GetTemplate<WeaponTemplate>()?.defMagType;
                Magazine fresh = string.IsNullOrEmpty(tplId) ? null : Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), tplId, null) as Magazine;
                bool secure = eq.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem != null;
                if (fresh == null || !magSlot.CanAccept(fresh) || !BotSetup.Place(bot, fresh, secure ? MagSlotsSecure : MagSlotsRig))
                {
                    return Failed(bot, gun, now, "no magazine of its kind could be made or put anywhere");
                }

                Mark(bot, fresh, 0);
                target = fresh;
                where = fresh.Parent?.Container?.ParentItem?.ShortName?.Localized();
            }

            // Rounds to fill it with, if the bot carries too few
            SummonRounds(bot, eq, gun, inGun != null ? inGun.MaxCount : 30);
            bool reused = empty != null;
            SafeReload.Run(() => reload.AddAmmoToMag(gun, target));
            if (target.Count <= 0)
            {
                _pending[id] = new Pending { Mag = target, Until = now + 3f, Reused = reused, Where = where };
                return null;
            }

            Done(bot, gun, target, reused, where);
            return target;
        }

        /// <summary>
        /// The empty magazine of the gun is taken out into the rig, pockets, bag or secure container, if there are rounds to fill it with (carried, or a
        /// stack that may be summoned). It is then an empty magazine the bot carries: the next ask of EFT's reload fills it and puts it back in. True = taken out.
        /// </summary>
        private static bool Unload(BotOwner bot, InventoryController ic, InventoryEquipment eq, Weapon gun, Magazine inGun, float now)
        {
            if (inGun == null || inGun.Count > 0 || string.IsNullOrEmpty(gun.AmmoCaliber))
            {
                return false;
            }

            string key = bot.ProfileId + gun.Id;
            if (_unloadTried.TryGetValue(key, out float at) && now - at < 10f)
            {
                return false;
            }

            int loose = Loose(bot, eq, gun, gun.AmmoCaliber, out bool alive);
            if (loose <= 0 && alive)
            {
                return false;
            }

            _unloadTried[key] = now;
            GridItemAddress to = MagKeeper.FreeSpot(eq, inGun, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets, EquipmentSlot.Backpack, EquipmentSlot.SecuredContainer);
            if (to == null)
            {
                return false;
            }

            var move = ItemManipulator.Move(inGun, to, ic, true);
            if (move.Failed)
            {
                return false;
            }

            ic.TryRunNetworkTransaction(move, null);
            Unloaded++;
            string where = to.Container?.ParentItem?.ShortName?.Localized() ?? "kit";
            if (_infoLeft > 0)
            {
                _infoLeft--;
                Log.Info($"LR Ammo: {Name(bot)} has no spare magazine for its {gun.ShortName.Localized()} - takes the empty {inGun.ShortName.Localized()} out into its {where} to fill it ({loose} loose rounds)");
            }
            else
            {
                Log.Detail("weapon", () => $"{Name(bot)}: the empty {inGun.ShortName.Localized()} of its {gun.ShortName.Localized()} out into its {where} to be filled");
            }

            return true;
        }

        /// <summary>The gun has no magazine and the empty one that was taken out could not be filled: it goes back where it was.</summary>
        private static void RestoreEmpty(BotOwner bot, Weapon gun, Magazine mag)
        {
            try
            {
                if (mag == null || gun == null || gun.GetCurrentMagazine() != null)
                {
                    return;
                }

                Slot magSlot = gun.GetMagazineSlot();
                InventoryController ic = bot.GetPlayer?.InventoryController;
                if (magSlot == null || ic == null || !magSlot.CanAccept(mag))
                {
                    return;
                }

                var move = ItemManipulator.Move(mag, magSlot.CreateItemAddress(), ic, true);
                if (move.Failed)
                {
                    return;
                }

                ic.TryRunNetworkTransaction(move, null);
                Restored++;
                Log.Detail("weapon", () => $"{Name(bot)}: the empty {mag.ShortName.Localized()} is put back into its {gun.ShortName.Localized()}");
            }
            catch (System.Exception e)
            {
                Diag.Report("Bottomless.RestoreEmpty", e);
            }
        }

        private static void Done(BotOwner bot, Weapon gun, Magazine target, bool reused, string where)
        {
            if (reused)
            {
                MagsReused++;
            }
            else
            {
                MagsMade++;
            }

            if (_infoLeft > 0)
            {
                _infoLeft--;
                Log.Info($"LR Ammo: {Name(bot)} had no loaded magazine for its {gun.ShortName.Localized()} - " +
                         (reused ? "an empty one it carries is filled" : $"a spare {target.ShortName.Localized()} is made in its {where ?? "kit"}") + $" ({target.Count} rounds)");
            }
            else
            {
                Log.Detail("weapon", () => $"{Name(bot)}: a loaded {target.ShortName.Localized()} for the {gun.ShortName.Localized()} ({target.Count} rounds){(reused ? ", an empty one filled" : ", made")}");
            }
        }

        private static Magazine Failed(BotOwner bot, Weapon gun, float now, string why)
        {
            MagsFailed++;
            _noMake[bot.ProfileId] = now + 10f;
            Log.Info($"LR Ammo: {Name(bot)} has no loaded magazine for its {gun.ShortName.Localized()} and {why}");
            return null;
        }

        // ---- Summoned things are not the bot's loot ----

        private static void Mark(BotOwner bot, Item item, int rounds)
        {
            if (!_air.TryGetValue(bot.ProfileId, out List<Air> list))
            {
                _air[bot.ProfileId] = list = new List<Air>();
            }

            list.Add(new Air { It = item, Rounds = rounds });
        }

        /// <summary>The summoned things that still lie in bag, rig or pockets (those LootingBots counts), as they are now.</summary>
        private static List<Air> AirOnGrids(BotOwner bot, InventoryEquipment eq)
        {
            var found = new List<Air>();
            if (eq == null || !_air.TryGetValue(bot.ProfileId, out List<Air> list) || list.Count == 0)
            {
                return found;
            }

            var topLevel = new HashSet<Item>();
            foreach (EquipmentSlot s in CountedSlots)
            {
                Item c = eq.GetSlot(s)?.ContainedItem;
                if (c == null)
                {
                    continue;
                }

                foreach (Item ch in c.GetAllItems())
                {
                    if (ch != c && ch.Parent?.Container?.ParentItem == c)
                    {
                        topLevel.Add(ch);
                    }
                }
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                Air a = list[i];
                // Rounds with found ones merged into the stack are the bot's now; a thing used up or moved into a gun is gone from the grids
                if (a.It is Ammo ammo && a.Rounds > 0 && ammo.StackObjectsCount > a.Rounds)
                {
                    list.RemoveAt(i);
                }
                else if (topLevel.Contains(a.It))
                {
                    found.Add(a);
                }
            }

            return found;
        }

        /// <summary>The cells of the summoned things that LootingBots would count as taken - given back to its free space (UpdateGridStats).</summary>
        internal static int AirCells(BotOwner bot)
        {
            if (!Settings.BottomlessAmmo.Value || !Settings.SummonedYield.Value || bot == null)
            {
                return 0;
            }

            int cells = 0;
            foreach (Air a in AirOnGrids(bot, bot.GetPlayer?.InventoryController?.Inventory?.Equipment))
            {
                var size = a.It.CalculateCellSize();
                cells += Mathf.Max(1, size.X * size.Y);
            }

            return cells;
        }

        /// <summary>A find does not fit: summoned things give way, until it does (called before LootingBots' pick-up).</summary>
        internal static void Yield(BotOwner bot, Item incoming)
        {
            if (!Settings.BottomlessAmmo.Value || !Settings.SummonedYield.Value || bot == null || incoming == null)
            {
                return;
            }

            InventoryController ic = bot.GetPlayer?.InventoryController;
            if (ic == null || ic.FindGridToPickUp(incoming) != null)
            {
                return;
            }

            List<Air> air = AirOnGrids(bot, ic.Inventory?.Equipment);
            if (air.Count == 0)
            {
                return;
            }

            List<string> names = new List<string>();
            foreach (Air a in air)
            {
                var removal = ItemManipulator.Remove(a.It, ic, true);
                if (removal.Failed)
                {
                    continue;
                }

                ic.TryRunNetworkTransaction(removal, null);
                _air[bot.ProfileId].Remove(a);
                Evicted++;
                names.Add(a.It.ShortName.Localized());
                if (ic.FindGridToPickUp(incoming) != null)
                {
                    break;
                }
            }

            if (names.Count > 0)
            {
                bot.GetPlayer.GetComponent<LootingBrain>()?.UpdateGridStats();
                Log.Detail("loot", () => $"{Name(bot)}: gives up its summoned {string.Join(", ", names)} to make room for {incoming.ShortName.Localized()}");
            }
        }
    }

    /// <summary>EFT's search for a loaded magazine to reload with: when it finds none, Bottomless fills or makes one.</summary>
    public class BottomlessMagazinePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotReload), nameof(BotReload.GetMagazineForReload));

        [PatchPostfix]
        protected static void PatchPostfix(BotReload __instance, Weapon weapon, ref Magazine __result)
        {
            if (__result != null || !Settings.BottomlessAmmo.Value)
            {
                return;
            }

            try
            {
                __result = Bottomless.Conjure(__instance._owner, weapon);
            }
            catch (System.Exception e)
            {
                Diag.Report("BottomlessMagazinePatch", e);
            }
        }
    }

    /// <summary>LootingBots' free space without the summoned things: they are not loot taking the cells of the bag.</summary>
    public class SummonedGridStatsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.UpdateGridStats));

        [PatchPostfix]
        protected static void PatchPostfix(LootingInventoryController __instance, LootingBrain ____lootingBrain)
        {
            try
            {
                int cells = Bottomless.AirCells(____lootingBrain?.BotOwner);
                if (cells > 0)
                {
                    __instance.Stats.AvailableGridSpaces += cells;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SummonedGridStatsPatch", e);
            }
        }
    }
}
