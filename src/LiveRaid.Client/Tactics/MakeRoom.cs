using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
    /// "Drop the cheap stuff for something clearly better" (F12 -> 7). LootingBots takes only what fits and stops looking for loot
    /// altogether once the bag is nearly full. A player throws out junk for a find worth much more. Here:
    ///  - When a bot is about to pick up an item that does not fit, it drops the cheapest things from its bag, rig and pockets to make
    ///    room - only if the find is worth at least 10k (a PMC) / 6k (a scav), everything dropped together is worth at most 60 % of it and each
    ///    dropped thing is at least 1.6 times poorer per cell. Never dropped: meds, grenades, keys, quest items, money, pouches, rounds and
    ///    magazines for its own guns (those for no gun it carries go like junk). A find that fits the 2 last cells LootingBots keeps free is taken.
    ///    The drops go through LootingBots' own throw (with its delay) and are put on the bot's "already looted" list.
    ///  - A bot with a full bag does not scan for loot any more; every few seconds it checks for a body within 40 m worth at least 30k
    ///    at a glance that it has not searched yet, and then looks after all.
    /// </summary>
    public static class MakeRoom
    {
        // 0.9.7: "cheap stuff dropped for a better find" was 0 in every raid since it was made. LootingBots fills a PMC's bag only with things worth
        // 7k a cell and more (3k for a scav): the old bar - a find of 15k+, each thing dropped at most half as rich per cell, all of them at most 40 % of
        // it - left nothing in a PMC's bag cheap enough, and a scav's bodies rarely hold a single 15k thing.
        public const float PmcMinFind = 10000f;
        public const float ScavMinFind = 6000f;
        public const float MaxDropShare = 0.6f;
        // The find is at least this much richer per cell than each thing dropped for it
        private const float DensityGain = 1.6f;

        // Why a find was not worth making room for, by reason (LR Health)
        private static readonly Dictionary<string, int> _refusals = new Dictionary<string, int>();
        private static readonly HashSet<string> _refusedLogged = new HashSet<string>();

        public static int ReserveTaken { get; private set; }

        public static string RefusalSummary => _refusals.Count == 0 ? "none" : string.Join(", ", _refusals.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}"));

        internal static float MinFindFor(BotOwner bot) => BotProfiles.Get(bot)?.Kind == BotKind.Pmc ? PmcMinFind : ScavMinFind;

        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(LootingTransactionController), "_player");
        private static readonly Dictionary<string, float> _scoutNext = new Dictionary<string, float>();
        // 1.2.8: how many times a bot has gone to look at one rich body with a full bag without taking anything (bot id + body id -> tries, and when it may try again)
        private static readonly Dictionary<string, (int tries, float until)> _scoutBody = new Dictionary<string, (int, float)>();
        private static float _next;

        // A call to the original TryPickupItemAsync made from here: let it through
        [ThreadStatic] internal static bool Bypass;

        public static int Swaps { get; private set; }
        public static int Scouts { get; private set; }

        public static void Clear()
        {
            _scoutNext.Clear();
            _scoutBody.Clear();
            _refusals.Clear();
            _refusedLogged.Clear();
            _next = 0f;
            Swaps = Scouts = ReserveTaken = 0;
        }

        internal static BotOwner BotOf(LootingTransactionController tc) => (PlayerField?.GetValue(tc) as Player)?.AIData?.BotOwner;

        /// <summary>The cheapest things to drop so that <paramref name="item"/> fits, or null when it is not worth it / not possible.</summary>
        internal static List<Item> Plan(BotOwner bot, Item item, out float price, out string why)
        {
            price = 0f;
            why = null;
            if (!Settings.DropCheapForBetter.Value || bot == null || item == null || item.QuestItem)
            {
                return null;
            }

            BotProfile p = BotProfiles.Get(bot);
            InventoryController inv = bot.GetPlayer?.InventoryController;
            if (p == null || p.Kind == BotKind.Other || inv == null)
            {
                return null;
            }

            // A fallen squadmate's things are carried out more readily (MateRecovery)
            bool mate = MateRecovery.IsLootingMate(bot);
            float minFind = mate ? 6000f : MinFindFor(bot);
            float gain = mate ? 1.25f : DensityGain;
            float dropShare = mate ? 0.8f : MaxDropShare;
            price = LootBrain.Price(item);
            if (price < minFind)
            {
                why = $"worth less than {minFind / 1000f:0}k";
                return null;
            }

            if (inv.FindGridToPickUp(item) != null)
            {
                why = "it fits as it is";
                return null;
            }

            int need = Cells(item);
            float perCell = price / Mathf.Max(1, need);
            // The whole kit of a fallen squadmate (MateKit): his guns, armour, backpack and his own kit count twice per cell, and the bot's own kit stays
            bool kit = mate && MateKit.On;
            if (kit && MateKit.IsFriendPriority(bot, item))
            {
                perCell *= MateKit.KitWeight;
            }

            LootingInventoryController lic = bot.GetPlayer.GetComponent<LootingBrain>()?.InventoryController;

            // Overloaded (45 kg+): what is cheap for its weight goes too - a player drops the heavy rubbish for a better find (Load)
            bool heavy = Settings.WeightMatters.Value && Load.Kg(bot) >= Load.Heavy;
            float perKg = price / Mathf.Max(0.1f, item.TotalWeight);
            var candidates = new List<(Item it, float value, int cells)>();
            InventoryEquipment eq = inv.Inventory?.Equipment;
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets })
            {
                Item container = eq?.GetSlot(slot)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                foreach (Item child in container.GetAllItems())
                {
                    if (child == container || child.Parent?.Container?.ParentItem != container || Keep(lic, child) || (kit && MateKit.IsKit(bot.ProfileId, child)))
                    {
                        continue;
                    }

                    float v = LootBrain.Price(child);
                    int cells = Cells(child);
                    if (v / Mathf.Max(1, cells) * gain <= perCell || (heavy && child.TotalWeight >= 1f && v / child.TotalWeight * gain <= perKg))
                    {
                        candidates.Add((child, v, cells));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                why = $"nothing in its bag, rig or pockets is {gain:0.#}x poorer per cell than it ({perCell / 1000f:0.#}k a cell)";
                return null;
            }

            List<Item> drops = new List<Item>();
            int freed = 0;
            float dropped = 0f;
            foreach (var c in candidates.OrderBy(c => c.value / Mathf.Max(1, c.cells)))
            {
                if (dropped + c.value > price * dropShare)
                {
                    why = $"the cheap things it would drop are worth more than {dropShare:P0} of it together";
                    break;
                }

                drops.Add(c.it);
                freed += c.cells;
                dropped += c.value;
                if (freed >= need)
                {
                    return drops;
                }
            }

            why = why ?? $"dropping all {candidates.Count} cheap thing(s) frees {freed} of the {need} cells it takes";
            return null;
        }

        /// <summary>
        /// LootingBots calls a bag full with 2 cells or fewer free and then takes nothing - not even a find that fits those cells. A find worth making
        /// room for (by the same bar) that fits as it is goes in.
        /// </summary>
        internal static bool FitsAnyway(BotOwner bot, Item item, float price) =>
            Settings.DropCheapForBetter.Value && bot != null && item != null && !item.QuestItem && price >= MinFindFor(bot)
            && bot.GetPlayer?.InventoryController?.FindGridToPickUp(item) != null;

        /// <summary>
        /// A bot with a full bag goes to a body only if it could drop something there: a thing in its bag, rig or pockets that is not kept and is worth at
        /// most a third of the body's gear. Otherwise it walks there and walks away again.
        /// </summary>
        public static bool CouldMakeRoomFor(BotOwner bot, float worth)
        {
            if (!Settings.DropCheapForBetter.Value || bot?.GetPlayer == null)
            {
                return false;
            }

            InventoryEquipment eq = bot.GetPlayer.InventoryController?.Inventory?.Equipment;
            LootingInventoryController lic = bot.GetPlayer.GetComponent<LootingBrain>()?.InventoryController;
            if (eq == null)
            {
                return false;
            }

            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets })
            {
                Item container = eq.GetSlot(slot)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                foreach (Item child in container.GetAllItems())
                {
                    if (child != container && child.Parent?.Container?.ParentItem == container && !Keep(lic, child) && LootBrain.Price(child) <= worth / 3f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Why a find was not taken by a full bag: counted by reason (LR Health), one trace line per bot and thing.</summary>
        internal static void Refused(BotOwner bot, Item item, float price, string why)
        {
            if (bot == null || item == null || why == null)
            {
                return;
            }

            string reason = why.StartsWith("worth less") ? "find too cheap" : why.StartsWith("nothing in its bag") ? "nothing poor enough to drop"
                : why.StartsWith("the cheap things") ? "drops worth too much together" : why.StartsWith("dropping all") ? "not enough cells" : why;
            _refusals[reason] = _refusals.TryGetValue(reason, out int n) ? n + 1 : 1;
            if (_refusedLogged.Add(bot.ProfileId + item.Id))
            {
                Log.Detail("loot", () => $"{bot.Profile?.Nickname}: full bag, {item.ShortName.Localized()} (~{price / 1000f:0.#}k) is left - {why}");
            }
        }

        internal static void NoteReserve(BotOwner bot, Item item, float price)
        {
            ReserveTaken++;
            Log.Detail("loot", () => $"{bot.Profile?.Nickname}: full bag, but {item.ShortName.Localized()} (~{price / 1000f:0.#}k) fits its last cells - takes it");
        }

        /// <summary>Drops through LootingBots' own throw, then the original pick-up.</summary>
        internal static async Task<bool> DropThenPickUp(LootingTransactionController tc, BotOwner bot, Item item, float price, List<Item> drops, CancellationToken token)
        {
            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            float droppedValue = 0f;
            List<string> names = new List<string>();
            foreach (Item d in drops)
            {
                float v = LootBrain.Price(d);
                if (!await tc.ThrowItemAsync(d, token))
                {
                    break;
                }

                brain?.IgnoreLoot(d.Id);
                droppedValue += v;
                names.Add(d.ShortName.Localized());
            }

            brain?.UpdateGridStats();
            Bypass = true;
            Task<bool> pickup;
            try
            {
                pickup = tc.TryPickupItemAsync(item, token);
            }
            finally
            {
                Bypass = false;
            }

            bool ok = await pickup;
            if (names.Count > 0)
            {
                Swaps++;
                Log.Info($"LR Loot: {bot.Profile.Nickname} drops {string.Join(", ", names)} (~{droppedValue / 1000f:0}k) to take {item.ShortName.Localized()} (~{price / 1000f:0}k)" +
                         (ok ? "" : " - but it still did not fit"));
            }

            return ok;
        }

        internal static bool Keep(LootingInventoryController lic, Item it)
        {
            if (it.QuestItem || it is Meds || it is ThrowWeap || it is Money || it.GetItemComponent<KeyComponent>() != null || it is SearchableItem)
            {
                return true;
            }

            // Rounds and magazines for its own guns stay; those it cannot use go like any junk (LootingBots' own rule when it throws things out)
            if (it is Ammo a)
            {
                return lic == null || lic.IsUsableAmmo(a);
            }

            if (it is Magazine m)
            {
                return lic == null || lic.IsUsableMag(m);
            }

            return false;
        }

        private static int Cells(Item it)
        {
            var size = it.CalculateCellSize();
            return Mathf.Max(1, size.X * size.Y);
        }

        /// <summary>A bot with a full bag no longer scans for loot: a rich body close by makes it look after all.</summary>
        public static void Tick()
        {
            if (!Settings.DropCheapForBetter.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 4f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || (_scoutNext.TryGetValue(bot.ProfileId, out float t) && Time.time < t))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                LootingBrain brain = player.GetComponent<LootingBrain>();
                if (p == null || p.Kind == BotKind.Other || brain == null || brain.LootTaskRunning || brain.HasFreeSpace || !TacticalTasks.NoThreat(bot))
                {
                    continue;
                }

                KillEvent rich = WorldEvents.RichBodyNear(bot.Position, 40f, 30000f, id => brain.IsLootIgnored(id));
                if (rich == null)
                {
                    continue;
                }

                // Nothing it could drop there: the trip would be for nothing (LootingBots warned of ~100 forced scans of full bags a raid)
                if (!CouldMakeRoomFor(bot, rich.Value))
                {
                    _scoutNext[bot.ProfileId] = Time.time + 30f;
                    continue;
                }

                // Raid 2026-11-07 23:32 (Ground Zero, top floor of Capital Insight): a scav with a full bag "went to look" at the same ~121k body 29 m away every minute,
                // QuestingBots' quest steps took the brain back after seconds each time and it never got there - the walk to and fro the user saw. Two tries per body, then 5 minutes off
                string scoutKey = bot.ProfileId + rich.VictimId;
                _scoutBody.TryGetValue(scoutKey, out (int tries, float until) seen);
                if (seen.tries >= 2 && Time.time < seen.until)
                {
                    _scoutNext[bot.ProfileId] = Time.time + 60f;
                    continue;
                }

                _scoutBody[scoutKey] = (seen.tries >= 2 ? 1 : seen.tries + 1, Time.time + 300f);

                _scoutNext[bot.ProfileId] = Time.time + 60f;

                // Straight to that body: a scan does not even start with a full bag (raid 2026-10-26 15:48: 39 "goes to look", not one cheap thing dropped)
                if (LootDanger.Why(bot) == null && (RoomSweep.HandBody(bot, rich.VictimId, rich.Position, 50f, out _) || RoomSweep.ForceScan(bot)))
                {
                    Scouts++;
                    Log.Detail("loot", $"{p.Name}'s bag is full, but a body worth ~{rich.Value / 1000f:0}k lies {Vector3.Distance(bot.Position, rich.Position):0} m away - goes to look");
                }
            }
        }
    }

    /// <summary>A find that does not fit: drop cheaper things first (MakeRoom).</summary>
    public class LbMakeRoomPickupPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingTransactionController), nameof(LootingTransactionController.TryPickupItemAsync));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingTransactionController __instance, Item item, CancellationToken token, ref Task<bool> __result)
        {
            if (MakeRoom.Bypass)
            {
                return true;
            }

            try
            {
                BotOwner bot = MakeRoom.BotOf(__instance);
                // The summoned rounds and magazines give way first (Bottomless)
                Bottomless.Yield(bot, item);
                List<Item> drops = MakeRoom.Plan(bot, item, out float price, out _);
                if (drops == null)
                {
                    return true;
                }

                __result = MakeRoom.DropThenPickUp(__instance, bot, item, price, drops, token);
                return false;
            }
            catch (Exception e)
            {
                Diag.Report("LbMakeRoomPickupPatch", e);
                return true;
            }
        }
    }

    /// <summary>With the bag nearly full LootingBots refuses every pick-up up front; a clearly better find gets through to MakeRoom.</summary>
    public class LbMakeRoomAllowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.AllowedToPickup));

        [PatchPrefix]
        protected static bool PatchPrefix(LootingInventoryController __instance, Item lootItem, int itemSize, LootingBrain ____lootingBrain, bool ____isPMC, ref bool __result)
        {
            try
            {
                if (!Settings.DropCheapForBetter.Value || ____lootingBrain == null || ____lootingBrain.HasFreeSpace || lootItem == null || lootItem.QuestItem)
                {
                    return true;
                }

                // What LootingBots would take with room in the bag: the kinds of things it may put there, worth its value per cell
                BotOwner bot = ____lootingBrain.BotOwner;
                float price = LootBrain.Price(lootItem);
                bool kind = global::LootingBots.Utilities.EquipmentTypeUtils.IsItemEligible(____isPMC ? global::LootingBots.LootingBots.PMCGearToPickup.Value : global::LootingBots.LootingBots.ScavGearToPickup.Value, lootItem, true);
                if (!kind || !__instance.IsValuableEnough(price / Mathf.Max(1, itemSize)))
                {
                    return true;
                }

                // It fits the last cells LootingBots keeps free
                if (MakeRoom.FitsAnyway(bot, lootItem, price))
                {
                    MakeRoom.NoteReserve(bot, lootItem, price);
                    __result = true;
                    return false;
                }

                if (MakeRoom.Plan(bot, lootItem, out price, out string why) == null)
                {
                    MakeRoom.Refused(bot, lootItem, price, why);
                    return true;
                }

                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Diag.Report("LbMakeRoomAllowPatch", e);
                return true;
            }
        }
    }
}
