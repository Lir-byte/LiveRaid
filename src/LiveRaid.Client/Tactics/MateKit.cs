using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
    /// The whole kit of a fallen squadmate is carried out (F12 -> 8 "Carry out the whole kit of a fallen squadmate", on top of "Recover the loot of a fallen
    /// squadmate"). Raid 2026-10-31 00:34: tyvas left 1 thing (~4k) in a bush for Kopat1ch's ~345k and still left ~339k on him - only own things 1.4 times
    /// poorer per cell than the friend's could go, and a PMC's bag holds nothing that poor. A player clears his bag of his own finds for his friend's gear:
    ///  - At its activation every PMC's (and player scav's) kit is remembered: what it walked into the raid with, so that later its own finds can be told
    ///    from its own kit, and a fallen friend's personal things from what he found.
    ///  - At the body the mate swaps its own finds for the friend's things right there (MakeRoom): his guns, armour, helmet, backpack and his own dear kit
    ///    weigh twice per cell; its own gear and kit, meds, keys, money, rounds and magazines for its guns are never dropped. LootingBots goes through his
    ///    slots biggest first: the backpack (put into the mate's own backpack whole when the game lets it, otherwise emptied thing by thing and then taken
    ///    if it fits), armour, helmet, guns, rig, pockets.
    ///  - Then, by chance (F12, 50 % by default), what of the friend's still did not fit is carried off to a hidden spot 6-22 m away (a bush, a corner) and
    ///    hidden there; otherwise the mate heads for an exit soon with what it carries (MateRecovery.AfterBody, ExtractPlanner.LeaveSoon).
    /// </summary>
    public static class MateKit
    {
        // The friend's gear and own kit weigh this much more per cell than finds (MakeRoom)
        public const float KitWeight = 2f;
        // Worth hiding in a bush: the friend's things still on him from this much
        private const float MinHidePrice = 5000f;
        private const int MaxHidden = 6;

        private static readonly Dictionary<string, HashSet<string>> _kit = new Dictionary<string, HashSet<string>>();
        private static readonly System.Random _rng = new System.Random();

        public static int Hidden { get; private set; }
        public static int CarriedOut { get; private set; }
        public static int WholeBags { get; private set; }
        public static int BagsRefused { get; private set; }

        public static string Summary => $"whole kits of fallen squadmates: the rest of the friend's hidden in a bush {Hidden} times, carried out and off to an exit {CarriedOut}; " +
                                        $"backpacks put whole into the bag {WholeBags} (the game would not {BagsRefused})";

        public static bool On => Settings.RecoverFallenMate.Value && Settings.MateFullKit.Value;

        public static void Clear()
        {
            _kit.Clear();
            Hidden = CarriedOut = WholeBags = BagsRefused = 0;
        }

        /// <summary>At activation: what the bot walks into the raid with (its secure container aside).</summary>
        public static void Remember(BotOwner bot)
        {
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            InventoryEquipment eq = bot?.GetPlayer?.Equipment;
            if (p == null || eq == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || _kit.ContainsKey(bot.ProfileId))
            {
                return;
            }

            HashSet<string> secure = new HashSet<string>();
            Item sc = eq.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem;
            if (sc != null)
            {
                foreach (Item it in sc.GetAllItems())
                {
                    secure.Add(it.Id);
                }
            }

            HashSet<string> ids = new HashSet<string>();
            foreach (Item it in eq.GetAllItems())
            {
                if (!secure.Contains(it.Id))
                {
                    ids.Add(it.Id);
                }
            }

            _kit[bot.ProfileId] = ids;
        }

        /// <summary>The thing was in the kit the bot (by profile id) walked in with.</summary>
        public static bool IsKit(string profileId, Item item) =>
            profileId != null && item != null && _kit.TryGetValue(profileId, out HashSet<string> ids) && ids.Contains(item.Id);

        /// <summary>The thing came into the raid with somebody else (a fallen squadmate's kit the bot carries out).</summary>
        public static bool IsOthersKit(BotOwner bot, Item item)
        {
            if (item == null || bot == null)
            {
                return false;
            }

            foreach (KeyValuePair<string, HashSet<string>> kv in _kit)
            {
                if (kv.Key != bot.ProfileId && kv.Value.Contains(item.Id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A thing the friend brought (his kit) or wore (guns, armour, helmet, backpack) - it weighs twice per cell against the mate's finds (MakeRoom).
        /// </summary>
        public static bool IsFriendPriority(BotOwner bot, Item item)
        {
            if (!On || item == null || bot == null)
            {
                return false;
            }

            if (item is Weapon || item is Backpack || item is Headwear || item.GetItemComponent<ArmorComponent>() != null)
            {
                return true;
            }

            foreach (KeyValuePair<string, HashSet<string>> kv in _kit)
            {
                if (kv.Key != bot.ProfileId && kv.Value.Contains(item.Id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The friend's things still on his body after the mate has gone through it, dearest first: what to hide in a bush.</summary>
        public static List<Item> Leftovers(InventoryEquipment eq)
        {
            var left = new List<(Item item, float value)>();
            if (eq == null)
            {
                return new List<Item>();
            }

            // Whole slots (a backpack or rig goes with what is in it)
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest, EquipmentSlot.Headwear,
                         EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
            {
                Item it = eq.GetSlot(slot)?.ContainedItem;
                if (it == null || Unlootable(it))
                {
                    continue;
                }

                float v = LootBrain.Price(it) + it.GetAllItems().Where(c => c != it).Sum(c => LootBrain.Price(c));
                if (v >= MinHidePrice)
                {
                    left.Add((it, v));
                }
            }

            // His pockets cannot be taken off: what lies in them
            Item pockets = eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem;
            if (pockets != null)
            {
                foreach (Item child in pockets.GetAllItems())
                {
                    if (child != pockets && child.Parent?.Container?.ParentItem == pockets && !Unlootable(child) && LootBrain.Price(child) >= MinHidePrice)
                    {
                        left.Add((child, LootBrain.Price(child)));
                    }
                }
            }

            return left.OrderByDescending(x => x.value).Take(MaxHidden).Select(x => x.item).ToList();
        }

        private static bool Unlootable(Item it)
        {
            UnlootableComponent u = it.GetItemComponent<UnlootableComponent>();
            return it.QuestItem || (u != null && it.Parent?.Container != null && u.IsUnlootableFrom(it.Parent.Container));
        }

        /// <summary>By chance the rest of the friend's goes into a hidden spot (true), otherwise the mate heads out with what it carries.</summary>
        public static bool DrawHidden() => _rng.Next(100) < Settings.MateStashChance.Value;

        public static void Noted(bool hidden)
        {
            if (hidden)
            {
                Hidden++;
            }
            else
            {
                CarriedOut++;
            }
        }

        private static readonly EquipmentSlot[] Order =
        {
            EquipmentSlot.Backpack, EquipmentSlot.ArmorVest, EquipmentSlot.Headwear, EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets,
        };

        /// <summary>A fallen squadmate's slots, biggest things first (LootingBots goes guns first when the bot has a bag).</summary>
        public static void Reorder(InventoryEquipment corpse, List<Item> items)
        {
            if (!On || corpse == null || items == null || items.Count < 2 || !MateRecovery.IsFallenMate(corpse.Id))
            {
                return;
            }

            int Rank(Item it)
            {
                for (int i = 0; i < Order.Length; i++)
                {
                    if (corpse.GetSlot(Order[i])?.ContainedItem == it)
                    {
                        return i;
                    }
                }

                return Order.Length;
            }

            List<Item> sorted = items.Select((it, i) => (it, i)).OrderBy(x => Rank(x.it)).ThenBy(x => x.i).Select(x => x.it).ToList();
            items.Clear();
            items.AddRange(sorted);
        }

        // A call made from here into LootingBots' own emptying of a backpack
        [ThreadStatic] internal static bool Inner;

        /// <summary>The friend's backpack is about to be emptied thing by thing: into the mate's own backpack whole, when the game lets it.</summary>
        internal static bool TryWhole(LootingInventoryController lic, LootingBrain brain, BotOwner bot, Item item, CancellationToken token, out Task<bool> result)
        {
            result = null;
            if (!On || Inner || !(item is Backpack) || bot == null || brain == null || !LootRules.OnMate(bot))
            {
                return false;
            }

            // His backpack - on his body, in its slot
            if (!(item.Parent?.Container is Slot slot) || slot.ID != EquipmentSlot.Backpack.ToString() || !(slot.ParentItem is InventoryEquipment eq)
                || !MateRecovery.IsFallenMate(eq.Id) || bot.GetPlayer?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem == null)
            {
                return false;
            }

            ItemAddress place = bot.GetPlayer.InventoryController?.FindGridToPickUp(item);
            if (place == null || string.Equals(place.GetRootItem()?.Parent?.Container?.ID, "securedcontainer", StringComparison.OrdinalIgnoreCase))
            {
                BagsRefused++;
                return false;
            }

            LootingTransactionController tc = Traverse.Create(lic).Field("_transactionController").GetValue<LootingTransactionController>();
            if (tc == null)
            {
                return false;
            }

            result = Whole(lic, tc, brain, bot, item, token);
            return true;
        }

        private static async Task<bool> Whole(LootingInventoryController lic, LootingTransactionController tc, LootingBrain brain, BotOwner bot, Item item, CancellationToken token)
        {
            float value = LootBrain.Price(item) + item.GetAllItems().Where(c => c != item).Sum(c => LootBrain.Price(c));
            if (await tc.MoveItemAsync(item, bot.GetPlayer.InventoryController.FindGridToPickUp(item), token))
            {
                WholeBags++;
                brain.Stats?.AddNetValue(value);
                brain.UpdateGridStats();
                Log.Info($"LR Squad: {bot.Profile.Nickname} puts its fallen squadmate's {item.ShortName.Localized()} into its own backpack whole (~{value / 1000f:0}k)");
                return true;
            }

            // The move failed after all: empty it the usual way
            Task<bool> usual;
            Inner = true;
            try
            {
                usual = lic.LootNestedItemsAsync(item, token);
            }
            finally
            {
                Inner = false;
            }

            return await usual;
        }

        private static int Cells(Item it)
        {
            var size = it.CalculateCellSize();
            return Mathf.Max(1, size.X * size.Y);
        }
    }

    /// <summary>A fallen squadmate's slots in the order MateKit wants them.</summary>
    public class MateKitOrderPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(global::LootingBots.Utilities.LootUtils), nameof(global::LootingBots.Utilities.LootUtils.GetPriorityItems));

        [PatchPostfix]
        protected static void PatchPostfix(InventoryEquipment corpseEquipment, List<Item> preallocatedList)
        {
            try
            {
                MateKit.Reorder(corpseEquipment, preallocatedList);
            }
            catch (Exception e)
            {
                Diag.Report("MateKitOrderPatch", e);
            }
        }
    }

    /// <summary>A fallen squadmate's backpack goes into the mate's own backpack whole, when the game lets it (MateKit).</summary>
    public class MateKitWholeBagPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.LootNestedItemsAsync));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingInventoryController __instance, Item item, CancellationToken token, LootingBrain ____lootingBrain, BotOwner ____botOwner,
            ref Task<bool> __result)
        {
            try
            {
                if (MateKit.TryWhole(__instance, ____lootingBrain, ____botOwner, item, token, out Task<bool> whole))
                {
                    __result = whole;
                    return false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("MateKitWholeBagPatch", e);
            }

            return true;
        }
    }
}
