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

        public static void Clear() => _keyPrices.Clear();

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
                GiveKeys(bot, profile);
                PrizeKeys.TryGive(bot, profile);
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
                if (TryGiveKey(bot, keyId))
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

        /// <summary>Creates the key and puts it into pockets, rig or backpack - never the secure container, so it drops with the body.</summary>
        private static bool TryGiveKey(BotOwner bot, string keyId)
        {
            try
            {
                Item key = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), keyId, null);
                return key != null && Place(bot, key, new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack });
            }
            catch (Exception e)
            {
                Log.Warn($"could not give key {keyId} to {bot.Profile?.Nickname}: {e.Message}");
                return false;
            }
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

            inv.TryRunNetworkTransaction(move, null);
            return true;
        }
    }
}
