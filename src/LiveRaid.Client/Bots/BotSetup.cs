using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Raid;
using QuestingBots.Helpers;

namespace LiveRaid.Client.Bots
{
    /// <summary>One-time setup of a freshly activated bot: personal skill and the keys it walks in with.</summary>
    public static class BotSetup
    {
        private const string FakeStash = "liveraid key spawn";
        private static readonly Dictionary<string, int> _keyPrices = new Dictionary<string, int>();

        // 1.3.3: the keys dealt in this raid (a key no one has yet weighs more)
        private static readonly HashSet<string> _dealt = new HashSet<string>();

        public static void Clear()
        {
            _dealt.Clear();
            _keyPrices.Clear();
            KeysHidden = KeysOpen = SpawnedKeysMoved = KeysToLeader = 0;
        }

        public static void OnActivated(BotOwner bot)
        {
            BotProfile profile = BotProfiles.Get(bot);
            if (profile == null || profile.Kind == BotKind.Other)
            {
                return;
            }

            if (profile.ActivatedAt >= 0)
            {
                return;
            }

            profile.ActivatedAt = UnityEngine.Time.time;
            profile.SpawnPos = bot.Position;
            Tactics.WeaponSteadiness.LowerChangeChance(bot.Settings);
            if (Settings.SkillSpread.Value)
            {
                ApplySkill(bot, profile);
            }

            if (Settings.RealKeys.Value && !profile.KeysGiven)
            {
                profile.KeysGiven = true;
                HideSpawnedKeys(bot, profile);
                GiveKeys(bot, profile);
                PrizeKeys.TryGive(bot, profile);
            }

            if (profile.Kind == BotKind.Pmc)
            {
                Patches.QbPmcReservePatch.StartedPmcs++;
            }

            Log.Debug("activated " + profile + (profile.Keys.Count > 0 ? " keys=" + string.Join(",", profile.Keys) : ""));
            if (profile.HotDrop && Settings.HotDrop.Value && HotDrop.Active(profile)
                && (bot.BotFollower == null || !bot.BotFollower.HaveBoss))
            {
                Log.Info($"LR Route: {profile.Name} ({profile.Kind}, {profile.Style}) is a hot-dropper - runs for the richest place of the map right away");
            }
        }

        /// <summary>
        /// Per-bot multipliers on aim, spread and senses. Talent above zero is better than the difficulty preset, below is worse.
        /// Uses EFT's own temporary-modifier channel (the same one SAIN uses), applied permanently.
        /// </summary>
        private static void ApplySkill(BotOwner bot, BotProfile p)
        {
            float t = p.Talent;
            float worse = 1f - 0.3f * t;
            BotSettingsInGameModif mod = new BotSettingsInGameModif
            {
                PrecicingSpeedCoef = worse,
                AccuratySpeedCoef = worse,
                ScatteringCoef = 1f - 0.25f * t,
                PriorityScatteringCoef = 1f - 0.2f * t,
                RuntimeVisionEffectK = 1f - 0.2f * t,
                VisibleDistCoef = 1f + 0.1f * t,
                HearingDistCoef = 1f + 0.12f * t,
                LayChanceDangerCoef = 1f,
                TriggerDownDelay = 1f - 0.15f * t,
                WaitInCoverCoef = p.Style == PlayStyle.Rusher ? 0.7f : (p.Style == PlayStyle.Camper || p.Style == PlayStyle.Rat ? 0.9f : 0.85f),
            };

            switch (p.Style)
            {
                case PlayStyle.Sniper:
                    mod.VisibleDistCoef *= 1.12f;
                    break;
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                    mod.HearingDistCoef *= 1.15f;
                    break;
            }

            bot.Settings?.Current?.Apply(mod, 0f);
        }

        private static void GiveKeys(BotOwner bot, BotProfile p)
        {
            MapIntel intel = MapIntel.Current;
            if (intel == null)
            {
                return;
            }

            intel.EnsureDoors();
            if (intel.SpotsByKey.Count == 0)
            {
                return;
            }

            Random rng = new Random(p.ProfileId.GetHashCode() ^ 0x5eed);
            int count = KeyCount(p, rng);
            if (count == 0)
            {
                return;
            }

            // Keys already carried (e.g. scav backpack loot) count toward the total
            HashSet<string> owned = new HashSet<string>(OwnedKeyIds(bot));
            List<(string keyId, double weight)> pool = new List<(string, double)>();
            foreach (KeyValuePair<string, List<LockedSpot>> kv in intel.SpotsByKey)
            {
                if (owned.Contains(kv.Key) || !kv.Value.Any(s => s.StillLocked))
                {
                    continue;
                }

                int roomValue = kv.Value.Max(s => s.RoomValue);
                bool hasRoom = kv.Value.Any(s => s.Interior != null || s.IsContainer);
                int price = KeyPrice(kv.Key);

                double w = (1 + roomValue) * (hasRoom ? 1.0 : 0.25) / Math.Sqrt(1 + price / 15000.0);
                if (price > 1_000_000)
                {
                    w *= 0.3;
                }

                if (p.Kind == BotKind.Scav)
                {
                    // Scavs mostly carry the cheap, common keys
                    w /= Math.Sqrt(1 + price / 10000.0);
                }

                // Built in (always on)
                {
                    // 1.3.3: whoever starts near the door, a key no one has yet, the key of a door with an EFT quest item (raids of 2026-11-09: the key of the Customs terminal was found in
                    // the raid 4 times, carried from the start never)
                    float nearest = float.MaxValue;
                    foreach (LockedSpot s in kv.Value)
                    {
                        UnityEngine.Vector3? o = s.OutsidePos;
                        if (o.HasValue)
                        {
                            UnityEngine.Vector3 d = o.Value - bot.Position;
                            nearest = Math.Min(nearest, UnityEngine.Mathf.Sqrt(d.x * d.x + d.z * d.z));
                        }
                    }

                    if (nearest < float.MaxValue)
                    {
                        w *= 1.0 + 2.0 * Math.Exp(-nearest / 220.0);
                    }

                    if (!_dealt.Contains(kv.Key))
                    {
                        w *= 2.5;
                    }

                    if (kv.Value.Any(s => QuestAudit.QuestDoors.Contains(s.Id)))
                    {
                        w *= 2.5;
                    }
                }

                pool.Add((kv.Key, w));
            }

            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                double total = pool.Sum(x => x.weight);
                double roll = rng.NextDouble() * total;
                int idx = 0;
                for (; idx < pool.Count - 1; idx++)
                {
                    roll -= pool[idx].weight;
                    if (roll <= 0)
                    {
                        break;
                    }
                }

                string keyId = pool[idx].keyId;
                pool.RemoveAt(idx);
                _dealt.Add(keyId);
                // 1.3.0: a squad goes where its LEADER takes it and followers do not walk to doors - the key of a follower is rolled for the leader (85 %)
                BotOwner holder = bot;
                BotProfile holderProfile = p;
                if (Settings.KeyLeaderHolds.Value)
                {
                    BotOwner leader = Tactics.TacticalTasks.SquadLeaderOf(bot);
                    BotProfile leaderProfile = leader != null && leader != bot && !leader.IsDead ? BotProfiles.Get(leader) : null;
                    if (leaderProfile != null && leaderProfile.Kind == p.Kind && rng.NextDouble() < 0.85 && !OwnedKeyIds(leader).Contains(keyId))
                    {
                        holder = leader;
                        holderProfile = leaderProfile;
                    }
                }

                // A key handed to the leader goes into its secure pouch every time (not on the body, not to be looted); a leader with no pouch or no room in it
                // does not take it, and the key stays with the bot it was rolled for, as before
                bool given = holder != bot && TryGiveKey(holder, holderProfile, keyId, rng, true);
                if (given)
                {
                    holderProfile.Keys.Add(keyId);
                    KeysToLeader++;
                    Log.Info($"LR Key: the key {KeyName(keyId)} rolled for {p.Name} goes to its squad leader {holderProfile.Name}, into the secure pouch");
                }
                else if (TryGiveKey(bot, p, keyId, rng))
                {
                    p.Keys.Add(keyId);
                }
            }

            if (p.Keys.Count > 0)
            {
                Log.Info($"LR Key: {p.Name} ({p.Kind} {p.Style}) carries {string.Join(", ", p.Keys.Select(KeyName))} " +
                         $"usable on {p.Keys.Sum(k => intel.SpotsByKey[k].Count)} locked spots");
            }
        }

        private static int KeyCount(BotProfile p, Random rng)
        {
            double chance = p.Kind == BotKind.Pmc ? Settings.PmcKeyChance.Value : Settings.ScavKeyChance.Value;
            if (p.Style == PlayStyle.Looter || p.Style == PlayStyle.Explorer)
            {
                chance = Math.Min(1, chance * 1.3);
            }

            if (rng.NextDouble() >= chance)
            {
                return 0;
            }

            int count = 1;
            int max = Settings.MaxKeysPerBot.Value;
            double more = p.Kind == BotKind.Pmc ? 0.4 : 0.1;
            while (count < max && rng.NextDouble() < more)
            {
                count++;
            }

            return count;
        }

        public static IEnumerable<string> OwnedKeyIds(BotOwner bot)
        {
            InventoryController inv = bot.GetPlayer?.InventoryController;
            if (inv == null)
            {
                return Enumerable.Empty<string>();
            }

            return inv.Inventory.Equipment.GetItemComponentsInChildren<KeyComponent>(false).Select(k => k.Template.KeyId).ToList();
        }

        public static bool HasKeyFor(BotOwner bot, string keyId)
        {
            return !string.IsNullOrEmpty(keyId) && OwnedKeyIds(bot).Contains(keyId);
        }

        internal static int KeyPrice(string keyId)
        {
            if (_keyPrices.TryGetValue(keyId, out int price))
            {
                return price;
            }

            try
            {
                Item probe = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null);
                price = (int)Tactics.LootBrain.Price(probe);
            }
            catch
            {
                price = 0;
            }

            _keyPrices[keyId] = price;
            return price;
        }

        internal static string KeyName(string keyId)
        {
            try
            {
                return Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null)?.ShortName.Localized() ?? keyId;
            }
            catch
            {
                return keyId;
            }
        }

        private static readonly EquipmentSlot[] OpenKeySlots = { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack };
        private static readonly EquipmentSlot[] HiddenKeySlots = { EquipmentSlot.SecuredContainer };

        /// <summary>Keys put by LiveRaid, in the pouch / out in the open, and keys SPT spawned the bot with that were moved into the pouch.</summary>
        public static int KeysHidden { get; private set; }
        public static int KeysOpen { get; private set; }
        public static int SpawnedKeysMoved { get; private set; }

        public static int KeysToLeader;

        public static string KeySummary => $"keys of the bots: {KeysHidden} dealt into the pouch, {KeysOpen} in the open, {SpawnedKeysMoved} that SPT spawned moved into the pouch, {KeysToLeader} of followers rolled for the leader";

        /// <summary>F12: the chance that a key the bot went into the raid with lies in its secure pouch (PMC 85 %, scav 50 % by default).</summary>
        private static float HiddenChance(BotProfile p) =>
            UnityEngine.Mathf.Clamp01((p.Kind == BotKind.Pmc ? Settings.PmcKeyHiddenChance : Settings.ScavKeyHiddenChance).Value);

        /// <summary>
        /// Creates the key and puts it where it was rolled to go: the secure pouch (not on the body, not to be looted) or pockets, rig, backpack (drops with the body).
        /// A pouch that has no room (or no pouch) leaves the key in the open.
        /// </summary>
        private static bool TryGiveKey(BotOwner bot, BotProfile p, string keyId, Random rng, bool onlyHidden = false)
        {
            try
            {
                Item key = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null);
                if (key == null)
                {
                    return false;
                }

                if (onlyHidden || rng.NextDouble() < HiddenChance(p))
                {
                    if (Place(bot, key, HiddenKeySlots))
                    {
                        KeysHidden++;
                        return true;
                    }

                    if (onlyHidden)
                    {
                        return false;
                    }

                    // No pouch or no room in it: a fresh key (the first was already registered in the fake stash) goes into the open
                    key = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null);
                }

                if (key == null || !Place(bot, key, OpenKeySlots))
                {
                    return false;
                }

                KeysOpen++;
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"could not give key {keyId} to {bot.Profile?.Nickname}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The keys SPT spawned the bot with (a scav's backpack loot) are rolled the same way: each may move from where SPT put it into the secure pouch.
        /// </summary>
        private static void HideSpawnedKeys(BotOwner bot, BotProfile p)
        {
            try
            {
                float chance = HiddenChance(p);
                InventoryController inv = bot.GetPlayer?.InventoryController;
                if (chance <= 0f || inv == null)
                {
                    return;
                }

                var eq = inv.Inventory.Equipment;
                Item pouch = eq.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem;
                if (pouch == null)
                {
                    return;
                }

                HashSet<Item> inPouch = new HashSet<Item>(pouch.GetAllItems());
                List<Item> keys = eq.GetItemComponentsInChildren<KeyComponent>(false).Select(k => k.Item).Where(it => it != null && !inPouch.Contains(it)).ToList();
                System.Random rng = new System.Random(p.ProfileId.GetHashCode() ^ 0x4b3d);
                foreach (Item key in keys)
                {
                    if (rng.NextDouble() < chance && Relocate(bot, key, HiddenKeySlots))
                    {
                        SpawnedKeysMoved++;
                        Log.Debug($"{p.Name}: its own key {key.ShortName.Localized()} moved into the secure pouch");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"could not sort the spawned keys of {bot.Profile?.Nickname}: {e.Message}");
            }
        }

        /// <summary>Moves an item the bot already has into the first of the slots' containers with room for it.</summary>
        private static bool Relocate(BotOwner bot, Item item, EquipmentSlot[] slots)
        {
            InventoryController inv = bot.GetPlayer?.InventoryController;
            if (item == null || inv == null)
            {
                return false;
            }

            ItemAddress address = bot.FindLocationForItem(item, slots, inv);
            if (address == null)
            {
                return false;
            }

            var move = ItemManipulator.Move(item, address, inv, false);
            if (move.Failed)
            {
                return false;
            }

            // The item exists on every machine already: an ordinary network transaction is right for it
            inv.TryRunNetworkTransaction(move, null);
            return true;
        }

        /// <summary>Puts a new item into the first of the bot's containers with room for it.</summary>
        internal static bool Place(BotOwner bot, Item item, EquipmentSlot[] slots)
        {
            InventoryController inv = bot.GetPlayer?.InventoryController;
            if (item == null || inv == null || !item.TryAddToFakeStash(inv, FakeStash))
            {
                return false;
            }

            ItemAddress address = bot.FindLocationForItem(item, slots, inv);
            if (address == null)
            {
                return false;
            }

            var move = ItemManipulator.Move(item, address, inv, false);
            if (move.Failed)
            {
                return false;
            }

            if (Net.SpawnWithoutTransaction)
            {
                // Fika host: the move above already put the key in place; the other machines are told to make the same key there
                // (a network transaction would send them an operation on an item they do not have)
                Net.ItemSpawned(bot.GetPlayer, item);
                return true;
            }

            // 1.2.6: Move(..., false) already carried the item over; running its transaction as well made EFT log "Trying to move item to the same place
            // where it is" for 20 of 21 keys of the raid of 2026-11-07. Only an item that is not there yet gets the transaction
            if (item.Parent != null && ReferenceEquals(item.Parent.Container, address.Container))
            {
                return true;
            }

            inv.TryRunNetworkTransaction(move, null);
            return true;
        }
    }
}
