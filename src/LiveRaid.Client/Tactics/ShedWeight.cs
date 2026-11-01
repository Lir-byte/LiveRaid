using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// "Shed weight to run" (F12 -> 7). A bot over EFT's weight limit cannot sprint at all (PhysicalBase.Overweight >= 1: the sprint is not available) -
    /// raid 2026-10-31 17:08: H4RD walked to the dorms car at 81 kg, PanicMF at 70 kg, both slow targets in the open. A player in that state empties the
    /// cheapest heavy rubbish to run again. Here, in a lull (no enemy in sight, not hit or shot at, not looting, not treating), with a chance rolled once
    /// per overload (greedy bots less often, careful ones more), the bot drops what is cheapest per kilogram from its backpack, rig and pockets until
    /// it is 1.5 kg under the limit again:
    ///  - never meds, grenades, keys, money, quest items, containers, rounds and magazines for its own guns (MakeRoom.Keep);
    ///  - never the gun of a fallen squadmate it carries out; his other things only when they are cheap for their weight (under 4000 a kilo);
    ///  - nothing over 15000 a kilo: when only dear things are left it stays slow rather than throw them away.
    /// The dropped things are struck off its own and its squad's loot lists, so nobody picks them up again.
    /// </summary>
    public static class ShedWeight
    {
        private const float Margin = 1.5f;
        private const float MaxPerKg = 15000f;
        private const float CheapFriendPerKg = 4000f;

        private static readonly System.Random _rng = new System.Random();
        // Per bot: the roll of this overload (true = sheds), until it is under the limit again or 5 minutes have gone
        private static readonly Dictionary<string, (bool sheds, float at)> _rolls = new Dictionary<string, (bool, float)>();
        private static readonly HashSet<string> _busy = new HashSet<string>();
        private static readonly AccessTools.FieldRef<LootingInventoryController, LootingTransactionController> TransactionField =
            AccessTools.FieldRefAccess<LootingInventoryController, LootingTransactionController>("_transactionController");

        private static float _next;

        public static int Shed { get; private set; }
        public static int Declined { get; private set; }
        public static float Kilos { get; private set; }

        public static string Summary => $"overloaded bots that shed weight to run {Shed} ({Kilos:0} kg in all), kept it by their roll {Declined}";

        public static void Clear()
        {
            _rolls.Clear();
            _busy.Clear();
            _next = 0f;
            Shed = Declined = 0;
            Kilos = 0f;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.ShedWeight.Value || !Net.RunsBots || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || bot.BotState != EBotState.Active
                    || _busy.Contains(bot.ProfileId) || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                try
                {
                    Look(bot, player, p);
                }
                catch (Exception e)
                {
                    Diag.Report("ShedWeight", e);
                }
            }
        }

        private static void Look(BotOwner bot, Player player, BotProfile p)
        {
            PhysicalBase phys = player.Physical;
            if (phys == null || phys.Overweight < 1f)
            {
                _rolls.Remove(bot.ProfileId);
                return;
            }

            float now = Time.time;
            if (!_rolls.TryGetValue(bot.ProfileId, out var roll) || now - roll.at > 300f)
            {
                // Greedy ones keep their haul, careful ones (campers, rats) want their legs
                float chance = Settings.ShedWeightChance.Value / 100f * (1.25f - 0.5f * p.LootGreed) * (1f + 0.2f * p.Camp);
                roll = (_rng.NextDouble() < Mathf.Clamp01(chance), now);
                _rolls[bot.ProfileId] = roll;
                if (!roll.sheds)
                {
                    Declined++;
                    Log.Detail("loot", $"{p.Name} is overloaded ({Load.Kg(bot):0} kg) - keeps it all (its roll)");
                }
            }

            if (!roll.sheds || !Quiet(bot))
            {
                return;
            }

            LootingBrain brain = player.GetComponent<LootingBrain>();
            LootingInventoryController lic = brain?.InventoryController;
            LootingTransactionController tc = lic != null ? TransactionField(lic) : null;
            if (brain == null || tc == null || brain.IsBotLooting)
            {
                return;
            }

            float kg = Load.Kg(bot);
            float limit = phys.BaseOverweightLimits.y;
            float excess = kg - (limit - Margin);
            if (excess <= 0f)
            {
                return;
            }

            List<(Item it, float perKg, float kg)> pick = new List<(Item, float, float)>();
            InventoryEquipment eq = player.InventoryController?.Inventory?.Equipment;
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets })
            {
                Item container = eq?.GetSlot(slot)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                foreach (Item child in container.GetAllItems())
                {
                    if (child == container || child.Parent?.Container?.ParentItem != container || MakeRoom.Keep(lic, child))
                    {
                        continue;
                    }

                    float w = child.TotalWeight;
                    if (w < 0.3f)
                    {
                        continue;
                    }

                    float perKg = LootBrain.Price(child) / w;
                    bool friends = MateKit.IsOthersKit(bot, child);
                    if (perKg > MaxPerKg || (friends && (child is Weapon || perKg > CheapFriendPerKg)))
                    {
                        continue;
                    }

                    pick.Add((child, perKg, w));
                }
            }

            List<Item> drops = new List<Item>();
            float off = 0f;
            foreach (var c in pick.OrderBy(c => c.perKg))
            {
                drops.Add(c.it);
                off += c.kg;
                if (off >= excess)
                {
                    break;
                }
            }

            if (drops.Count == 0)
            {
                // Nothing cheap enough to part with: no new look until the next overload
                _rolls[bot.ProfileId] = (false, now);
                Log.Detail("loot", $"{p.Name} is overloaded ({kg:0} kg, runs from {limit:0} kg) - nothing cheap enough to drop, stays slow");
                return;
            }

            _busy.Add(bot.ProfileId);
            Drop(bot, p, brain, tc, drops, kg, limit);
        }

        /// <summary>No enemy in sight or seen within 10 s closer than 60 m, not hit or shot at lately, not treating, not on a tactical task.</summary>
        private static bool Quiet(BotOwner bot)
        {
            if (HitReaction.SinceHit(bot) < 15f || bot.Memory?.IsUnderFire == true || bot.Medecine?.Using == true || TacticalTasks.TryGet(bot, out _))
            {
                return false;
            }

            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            if (sain?.EnemyController?.KnownEnemies == null)
            {
                return true;
            }

            foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
            {
                if (e != null && e.CheckValid() && (e.IsVisible || (e.TimeSinceSeen < 10f && e.RealDistance < 60f)))
                {
                    return false;
                }
            }

            return true;
        }

        private static async void Drop(BotOwner bot, BotProfile p, LootingBrain brain, LootingTransactionController tc, List<Item> drops, float kg, float limit)
        {
            try
            {
                List<string> names = new List<string>();
                float value = 0f, kilos = 0f;
                foreach (Item item in drops)
                {
                    if (bot.IsDead || item.Parent == null)
                    {
                        break;
                    }

                    float v = LootBrain.Price(item), w = item.TotalWeight;
                    if (!await tc.ThrowItemAsync(item, CancellationToken.None))
                    {
                        break;
                    }

                    names.Add(item.ShortName.Localized());
                    value += v;
                    kilos += w;
                    // Nobody of the squad picks it up again on the way
                    foreach (string id in SquadTies.MatesOf(bot.ProfileId).Append(bot.ProfileId))
                    {
                        Player mate = Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id);
                        mate?.GetComponent<LootingBrain>()?.IgnoreLoot(item.Id);
                    }
                }

                brain.UpdateGridStats();
                if (names.Count > 0)
                {
                    Shed++;
                    Kilos += kilos;
                    Log.Info($"LR Loot: {p.Name} is overloaded ({kg:0} kg, runs only under {limit:0} kg) - drops {string.Join(", ", names)} " +
                             $"(~{value / 1000f:0}k, {kilos:0.0} kg) to run again");
                }
                else
                {
                    // The game would not take the throw: not again every 2 s for this overload
                    _rolls[bot.ProfileId] = (false, Time.time);
                    Log.Detail("loot", $"{p.Name} is overloaded ({kg:0} kg) - the game would not let it drop anything");
                }
            }
            catch (Exception e)
            {
                Diag.Report("ShedWeight", e);
            }
            finally
            {
                _busy.Remove(bot.ProfileId);
            }
        }
    }
}
