using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Paid and conditional exits (F12 -> 8 "Paid and conditional exits"). The game sends bots only to exits with no requirement, so the car out,
    /// the cliff descent, "no backpack" and the like were never used. A player uses them when he can:
    ///  - paid (the car): it needs the price in roubles in its inventory - PMCs get 6-40k for it in the secure container (server, ExitMoneyPatch);
    ///  - conditional (an empty slot, a required item worn or carried, a skill): the game's own check of the requirement for this bot (Met);
    ///  - switch exits stay with SwitchExtract; co-op, train, timer and event exits are not used by bots.
    /// The exit then takes its place in the bot's choice like any other (ExitPlan: its side, nearer likelier).
    /// </summary>
    public static class ExitRules
    {
        private static readonly Dictionary<string, int> _used = new Dictionary<string, int>();

        public static bool On => Settings.Enabled.Value && Settings.SpecialExits.Value;

        public static string Summary => _used.Count == 0 ? "paid and conditional exits chosen: none"
            : "paid and conditional exits chosen: " + string.Join(", ", _used.Select(kv => $"{kv.Key} {kv.Value}"));

        public static void Clear() => _used.Clear();

        /// <summary>The bot may leave through this exit: no requirement, or (with the feature on) every requirement met as a player would meet it.</summary>
        public static bool Allows(BotOwner bot, ExfiltrationPoint exit, out string note)
        {
            note = null;
            if (exit?.Requirements == null || exit.Requirements.Length == 0)
            {
                return true;
            }

            List<string> notes = new List<string>();
            foreach (ExfiltrationRequirement r in exit.Requirements)
            {
                if (r == null || r.Requirement == ERequirementState.None)
                {
                    continue;
                }

                if (!On || bot?.GetPlayer == null)
                {
                    return false;
                }

                switch (r.Requirement)
                {
                    case ERequirementState.Train:
                        // The armoured train (TrainRequirement): from its arrival the bot may go and board; the game lets it go when the train pulls out
                        if (exit.Status != EExfiltrationStatus.AwaitsManualActivation && exit.Status != EExfiltrationStatus.Countdown)
                        {
                            return false;
                        }

                        notes.Add("boards the train");
                        break;

                    case ERequirementState.TransferItem:
                    {
                        int have = Count(bot.GetPlayer, r.Id);
                        if (have < r.Count)
                        {
                            return false;
                        }

                        notes.Add($"pays {r.Count}");
                        break;
                    }

                    case ERequirementState.Empty:
                        try
                        {
                            if (r.Met(bot.GetPlayer, exit))
                            {
                                notes.Add("empty");
                                break;
                            }
                        }
                        catch
                        {
                            return false;
                        }

                        // 1.4.0: "no backpack" - a bot that would leave its bag behind takes the exit all the same (BackpackExit)
                        if (!BackpackExit.Willing(bot, r))
                        {
                            return false;
                        }

                        notes.Add("leaves its backpack");
                        break;

                    case ERequirementState.NotEmpty:
                    case ERequirementState.HasItem:
                    case ERequirementState.WearsItem:
                    case ERequirementState.EmptyOrSize:
                    case ERequirementState.SkillLevel:
                        try
                        {
                            if (!r.Met(bot.GetPlayer, exit))
                            {
                                return false;
                            }
                        }
                        catch
                        {
                            return false;
                        }

                        notes.Add(r.Requirement.ToString().ToLowerInvariant());
                        break;

                    default:
                        return false;
                }
            }

            note = notes.Count > 0 ? string.Join(", ", notes) : null;
            return true;
        }

        public static void Chosen(string exit, string note)
        {
            if (note != null)
            {
                _used[exit] = _used.TryGetValue(exit, out int n) ? n + 1 : 1;
            }
        }

        private static int Count(Player player, string tpl)
        {
            if (string.IsNullOrEmpty(tpl) || player.Inventory?.Equipment == null)
            {
                return 0;
            }

            int sum = 0;
            foreach (Item it in player.Inventory.Equipment.GetAllItems())
            {
                if (it.TemplateId.ToString() == tpl)
                {
                    sum += Mathf.Max(1, it.StackObjectsCount);
                }
            }

            return sum;
        }
    }

    /// <summary>What a bot carries, in kg (refreshed every 3 s): an overloaded player drops the heavy cheap things and leaves sooner.</summary>
    public static class Load
    {
        private static readonly Dictionary<string, (float at, float kg)> _kg = new Dictionary<string, (float, float)>();

        public const float Heavy = 45f;
        public const float Overloaded = 60f;

        public static void Clear() => _kg.Clear();

        public static float Kg(BotOwner bot)
        {
            if (bot?.GetPlayer?.Inventory?.Equipment == null)
            {
                return 0f;
            }

            if (_kg.TryGetValue(bot.ProfileId, out var w) && Time.time - w.at < 3f)
            {
                return w.kg;
            }

            float kg = bot.GetPlayer.Inventory.Equipment.GetEquipmentWeight();
            _kg[bot.ProfileId] = (Time.time, kg);
            return kg;
        }
    }
}
