using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using LootingBots.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Out through a "no backpack" exit (F12 -> 8 "Leave the backpack for an exit", 1.4.0): the sewer manhole of Reserve and the like.
    ///
    /// The game asks for an empty backpack slot there. ExitRules took such an exit only for a bot that had no backpack to begin with - next to nobody - so on
    /// Reserve, where every PMC exit has a condition, bots had the D-2 switch or nothing. A player whose bag holds little drops it at the manhole and goes.
    ///
    /// Each bot rolls once whether it would (F12 chance; a bag worth under 80k - as rolled, 80-250k - half as likely, more - a fifth: nobody throws a rich bag away,
    /// that one goes for the switch exit or stays). A willing bot counts the exit as its own (ExitRules.Allows), goes there like to any other and, 25 m short of it,
    /// throws the backpack off; the game's own exit timer does the rest.
    /// </summary>
    public static class BackpackExit
    {
        private static readonly AccessTools.FieldRef<LootingInventoryController, LootingTransactionController> TransactionField =
            AccessTools.FieldRefAccess<LootingInventoryController, LootingTransactionController>("_transactionController");

        private static readonly Dictionary<string, bool> _willing = new Dictionary<string, bool>();
        private static readonly HashSet<string> _dropping = new HashSet<string>();
        private static float _next;
        private static int _failNoted;

        public static int Would { get; private set; }
        public static int Dropped { get; private set; }

        public static bool On => Settings.Enabled.Value && Settings.SpecialExits.Value && Settings.BackpackExits.Value;

        public static string Summary => $"no-backpack exits: {Would} bot(s) would leave their bag for one, {Dropped} did";

        public static void Clear()
        {
            _willing.Clear();
            _dropping.Clear();
            _next = 0f;
            Would = Dropped = 0;
        }

        private static bool NeedsNoBackpack(ExfiltrationPoint exit) =>
            exit?.Requirements != null && exit.Requirements.Any(r => r != null && r.Requirement == ERequirementState.Empty && r.RequiredSlot == EquipmentSlot.Backpack);

        /// <summary>A bot at a no-backpack exit of its own plan still carries the bag: the timer does not start yet.</summary>
        public static bool Blocks(ExfiltrationPoint exit, BotOwner bot) => NeedsNoBackpack(exit) && Bag(bot) != null;

        private static Item Bag(BotOwner bot) => bot.GetPlayer?.Inventory?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem;

        /// <summary>The requirement "this slot empty" is not met: a bot that would leave its backpack behind meets it all the same.</summary>
        public static bool Willing(BotOwner bot, ExfiltrationRequirement r)
        {
            if (!On || bot == null || r == null || r.Requirement != ERequirementState.Empty || r.RequiredSlot != EquipmentSlot.Backpack)
            {
                return false;
            }

            if (_willing.TryGetValue(bot.ProfileId, out bool known))
            {
                return known;
            }

            float carried = 0f;
            try
            {
                carried = Mathf.Max(0f, LootingBots.External.GetNetLootValue(bot));
            }
            catch
            {
                // LootingBots knows nothing of this bot yet
            }

            float chance = Settings.BackpackExitChance.Value / 100f * (carried < 80000f ? 1f : carried < 250000f ? 0.5f : 0.2f);
            BotProfile p = BotProfiles.Get(bot);
            if (p != null && (p.Packrat || p.LootGreed > 1.3f))
            {
                // The bag is what it came for
                chance *= 0.3f;
            }

            bool would = Raid.FreshSteps.Roll(bot.ProfileId, 901, 0) < chance;
            _willing[bot.ProfileId] = would;
            if (would)
            {
                Would++;
            }

            return would;
        }

        /// <summary>Once a second: a bot on its way out through a no-backpack exit throws its bag off 25 m short of it.</summary>
        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || _willing.Count == 0)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || !_willing.TryGetValue(bot.ProfileId, out bool would) || !would
                    || _dropping.Contains(bot.ProfileId))
                {
                    continue;
                }

                // An exit of LiveRaid's own plan (the manhole is no AI exit point of the game), else the game's own cached one
                ExfiltrationPoint exitPoint = null;
                Vector3 exitAt = default;
                string exitName = null;
                if (SwitchExtract.TryGetPlan(bot, out SwitchExtract.Plan plan) && plan.Exit.Direct)
                {
                    exitPoint = plan.Exit.Exit;
                    exitAt = plan.Exit.Stand;
                    exitName = plan.Exit.Name;
                }
                else if (bot.Exfiltration?.WannaLeave() == true)
                {
                    AIExfiltrationPoint point = bot.PatrollingData?.ExfiltrationData?.CachedExfiltrationPoint;
                    if (point != null)
                    {
                        exitPoint = point.ExfiltrationPoint;
                        exitAt = point.Position;
                        exitName = point.Name;
                    }
                }

                if (exitPoint == null || !NeedsNoBackpack(exitPoint) || (exitAt - bot.Position).sqrMagnitude > 25f * 25f)
                {
                    continue;
                }

                Item bag = Bag(bot);
                if (bag == null)
                {
                    continue;
                }

                LootingBrain brain = player.GetComponent<LootingBrain>();
                LootingInventoryController lic = brain?.InventoryController;
                LootingTransactionController tc = lic != null ? TransactionField(lic) : null;
                // 1.4.9: only a loot job that is really running holds the throw off - HasActiveLootable stays set on a bot whose layer LootingBots no longer has, and then
                // the bag was never thrown (raid 2026-11-11 16:06: 3 would, 0 did; Тихон Зверь and Мирон Копирайтер stood 2-4 minutes at the manhole)
                if (tc == null || brain.LootTaskRunning)
                {
                    continue;
                }

                _dropping.Add(bot.ProfileId);
                Throw(bot, tc, bag, exitName);
            }
        }

        private static async void Throw(BotOwner bot, LootingTransactionController tc, Item bag, string exit)
        {
            try
            {
                string name = bag.ShortName.Localized();
                if (await tc.ThrowItemAsync(bag, CancellationToken.None))
                {
                    Dropped++;
                    Log.Info($"LR Extract: {bot.Profile?.Nickname} throws off its backpack ({name}) at the exit {exit} - it takes nobody with a backpack");
                }
                else
                {
                    _dropping.Remove(bot.ProfileId);
                    if (_failNoted++ < 6)
                    {
                        Log.Info($"LR Extract: {bot.Profile?.Nickname} could not throw off its backpack ({name}) at the exit {exit} - the game refused");
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("BackpackExit", e);
            }
        }
    }

    /// <summary>
    /// The packrat comes in with the starting lobby (F12 -> 6 "Packrat from the start", 1.4.0). Raid 2026-11-10 12:37 (Reserve): its profile went to a lone group
    /// that QuestingBots had drawn among the last, i.e. the reserve - it came in at 5:09, into a map already at war, looted nothing and was dead at 8:32.
    /// QuestingBots spawns its PMC groups in the order it drew them; the packrat's group is put first in that order before each spawning pass until it is in.
    /// </summary>
    public class QbPackratFirstPatch : ModulePatch
    {
        internal static int Moved;
        private static bool _noPackratNoted;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Components.Spawning.BotGenerator"), "spawnBotGroups");

        [PatchPrefix]
        protected static void PatchPrefix(object __instance, ref QuestingBots.Models.BotSpawnInfo[] botGroups)
        {
            try
            {
                string id = Packrat.Assigned;
                if (id == null && !_noPackratNoted && Packrat.Armed && __instance.GetType().Name == "PMCGenerator" && QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds() > 45f)
                {
                    // 1.4.12: the server armed a packrat for this raid but no lone group has taken its profile (raid 2026-11-11 17:27: six tagged candidates, none given out)
                    _noPackratNoted = true;
                    int lone = botGroups == null ? 0 : botGroups.Count(g => g != null && g.GeneratedBotCount == 1);
                    Log.Info($"LR Packrat: armed by the server but no packrat was given to any group by {QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds():0} s - {Packrat.TaggedCount} tagged profile(s), {lone} lone group(s) of {botGroups?.Length ?? 0}");
                }

                if (id == null || !Settings.PackratFromStart.Value || botGroups == null || botGroups.Length < 2 || __instance.GetType().Name != "PMCGenerator")
                {
                    return;
                }

                int at = System.Array.FindIndex(botGroups, g => g != null && !g.HasSpawnStarted && g.GetGeneratedProfiles().Any(p => p != null && p.Id == id));
                if (at <= 0)
                {
                    return;
                }

                // Before every group that has not come in yet
                int first = System.Array.FindIndex(botGroups, g => g != null && !g.HasSpawnStarted && !g.HaveAllBotsSpawned);
                if (first < 0 || first >= at)
                {
                    return;
                }

                QuestingBots.Models.BotSpawnInfo mine = botGroups[at];
                List<QuestingBots.Models.BotSpawnInfo> list = botGroups.ToList();
                list.RemoveAt(at);
                list.Insert(first, mine);
                botGroups = list.ToArray();
                if (mine.RaidETRangeToSpawn.Min > 0)
                {
                    mine.RaidETRangeToSpawn.Min = 0;
                }

                if (Moved++ == 0)
                {
                    Log.Info($"LR Packrat: the packrat's group was no. {at + 1} in QuestingBots' order - it comes in first of those still to come (no. {first + 1})");
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbPackratFirstPatch", e);
            }
        }
    }
}
