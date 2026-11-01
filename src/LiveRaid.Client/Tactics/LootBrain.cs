using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using QuestingBots.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    public enum LootDecision
    {
        LootNow,
        Later,
        Ignore,
    }

    /// <summary>
    /// Should I go for that body? Weighs what the body is visibly worth against how dangerous it is to step up to it,
    /// through the eyes of this bot's character. Mirrors what an online player thinks before looting: his own kill he goes
    /// through at once (a squadmate watches his back, or nobody does), a stranger's body whenever it is worth the walk.
    /// There is no hiding at a distance to wait for someone to take the bait - it only parked bots for half a minute.
    /// </summary>
    public static class LootBrain
    {
        private class Memory
        {
            public LootDecision Decision;
            public float DecidedAt;
            public float NotBefore;
        }

        private static readonly Dictionary<string, Memory> _memory = new Dictionary<string, Memory>();
        // Body value per loot id with the time it was appraised: a body other bots are emptying gets re-appraised after a minute
        private static readonly Dictionary<string, (float value, float at)> _valueCache = new Dictionary<string, (float, float)>();

        private class AfterKill
        {
            public float At;
            public float Until;
            public Vector3 Pos;
        }

        // Shooters who have just killed someone and are to scan for loot as soon as it is quiet (their own kill is the nearest body)
        private static readonly Dictionary<string, AfterKill> _afterKill = new Dictionary<string, AfterKill>();
        private static float _nextAfterKill;
        public static int KillScans { get; private set; }
        private static readonly System.Random _rng = new System.Random();

        public static void Clear()
        {
            _memory.Clear();
            _valueCache.Clear();
            _afterKill.Clear();
            KillScans = 0;
        }

        /// <summary>
        /// Raid 2026-10-20 14:21: vargrasen killed eight scavs in one building and went through one body; the others lay untouched while he searched
        /// for ghosts. LootingBots scans on its own timer (and QuestingBots' checks), and not at all while a combat layer holds the bot. A player
        /// goes to the man he has just shot as soon as it is quiet.
        /// </summary>
        public static void Tick()
        {
            if (_afterKill.Count == 0 || Time.time < _nextAfterKill)
            {
                return;
            }

            _nextAfterKill = Time.time + 1f;
            List<string> done = null;
            foreach (KeyValuePair<string, AfterKill> kv in _afterKill)
            {
                if (Time.time < kv.Value.At)
                {
                    continue;
                }

                if (Time.time > kv.Value.Until)
                {
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    continue;
                }

                BotOwner bot = Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == kv.Key)?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || (bot.Position - kv.Value.Pos).sqrMagnitude > 60f * 60f)
                {
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    continue;
                }

                if (!TacticalTasks.NoThreat(bot))
                {
                    continue;
                }

                (done ?? (done = new List<string>())).Add(kv.Key);
                LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
                if (brain != null && brain.LootTaskRunning)
                {
                    continue;
                }

                RoomSweep.Release(bot);
                KillScans++;
                if (LootJobs.AskNow(bot))
                {
                    // Straight to the body (its own kill is its most important job) - a scan handed it the nearest drawer instead (raid 2026-10-26 15:48)
                    Log.Detail("loot", $"{bot.Profile.Nickname} has killed and it is quiet - goes to look through the body");
                    continue;
                }

                RoomSweep.ForceScan(bot);
                Log.Detail("loot", $"{bot.Profile.Nickname} has killed and it is quiet - looks for the body");
            }

            if (done != null)
            {
                foreach (string id in done)
                {
                    _afterKill.Remove(id);
                }
            }
        }

        /// <summary>Called by the LootingBots patch when a bot has picked a body to loot.</summary>
        public static LootDecision Decide(BotOwner bot, Corpse corpse, string lootId)
        {
            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other || !Settings.LootThinking.Value)
            {
                return LootDecision.LootNow;
            }

            string key = bot.ProfileId + "|" + lootId;
            _memory.TryGetValue(key, out Memory mem);
            if (mem != null && Time.time < mem.NotBefore)
            {
                return LootDecision.Later;
            }

            // Danger about (shots, a stranger heard or seen): come back when it is quiet
            if (LootDanger.Why(bot) != null || LootAudit.Cooling(bot, lootId))
            {
                if (mem == null)
                {
                    _memory[key] = mem = new Memory();
                }

                mem.Decision = LootDecision.Later;
                mem.DecidedAt = Time.time;
                mem.NotBefore = Time.time + 8f;
                return LootDecision.Later;
            }

            // A fallen squadmate's body, recovered on purpose (MateRecovery): no weighing
            if (MateRecovery.IsRecovering(bot, corpse.PlayerProfileID) || LootJobs.IsJob(bot, corpse.PlayerProfileID))
            {
                Log.Info($"LR Loot: {p.Name} ({p.Style}) goes through {(MateRecovery.IsRecovering(bot, corpse.PlayerProfileID) ? "its fallen squadmate's" : "a")} body it came for");
                return LootDecision.LootNow;
            }

            Vector3 corpsePos = corpse.transform.position;
            float value = VisibleValue(corpse.ItemOwner?.RootItem as InventoryEquipment, lootId);
            bool ownKill = WorldEvents.TryGetKill(corpse.PlayerProfileID, out KillEvent kill) && kill.KillerId == bot.ProfileId;
            bool bagFull = LootingBots.External.CheckIfInventoryFull(bot);

            // His own kill is what he came for: it weighs a lot whatever it carries
            float desire = (float)Math.Log10(1 + value / 15000f) * p.LootGreed * (bagFull ? 0.35f : 1f) + (ownKill ? 0.6f : 0f);

            // A backpack on a body: nobody sees what is in it, and players nearly always open it
            if (!bagFull && (corpse.ItemOwner?.RootItem as InventoryEquipment)?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem != null)
            {
                desire += 0.3f;
            }
            float open = Openness(corpsePos);
            float risk = Risk(bot, p, corpsePos, kill, open);
            float caution = 1.25f - p.Aggression;

            // People are not calculators: every decision has some noise, and newcomers often just run in
            float noise = ((float)_rng.NextDouble() - 0.5f) * 0.4f;
            if (p.Skill == SkillTier.Novice && _rng.NextDouble() < 0.35)
            {
                caution *= 0.2f;
            }

            // Looting itself only starts when the bot is out of any fight (LootingBots yields to combat), so the risk here is only a nudge
            float score = desire - risk * caution * 0.6f + noise;

            LootDecision decision;
            if (desire < 0.12f && !ownKill)
            {
                decision = LootDecision.Ignore;
            }
            else if (score >= -0.1f || (ownKill && risk < 1.6f))
            {
                decision = LootDecision.LootNow;
            }
            else if (score > -0.9f)
            {
                decision = LootDecision.Later;
            }
            else
            {
                decision = LootDecision.Ignore;
            }

            if (mem == null)
            {
                _memory[key] = mem = new Memory();
            }

            mem.Decision = decision;
            mem.DecidedAt = Time.time;
            if (decision == LootDecision.Later)
            {
                mem.NotBefore = Time.time + 8f + (float)_rng.NextDouble() * 22f;
            }

            // Lying down at a body is drawn when the bot is at it, not here (ProneLoot): the body is not where the bot stands yet
            Log.Info($"LR Loot: {p.Name} ({p.Style}, {p.Skill}) -> {decision} on body worth ~{value / 1000f:0}k " +
                     $"(desire {desire:0.00}, risk {risk:0.00}, open {open:0.0}{(ownKill ? ", own kill" : "")})");
            return decision;
        }

        /// <summary>After a kill the shooter freezes for a moment (1-2 s) and listens for the victim's squad.</summary>
        public static void OnKill(KillEvent kill, Player killer)
        {
            BotOwner bot = killer?.AIData?.BotOwner;
            if (bot == null || !Settings.LootThinking.Value)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            // The body is what the shooter killed for: once the shooting is over he goes through it
            _afterKill[bot.ProfileId] = new AfterKill { At = Time.time + 2f, Until = Time.time + 60f, Pos = kill.Position };
            if (TacticalTasks.TryGet(bot, out _))
            {
                return;
            }

            float seconds;
            switch (p.Style)
            {
                case PlayStyle.Rusher: seconds = 0.6f; break;
                case PlayStyle.Hunter: seconds = 0.8f; break;
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                case PlayStyle.Sniper: seconds = 1.8f; break;
                default: seconds = 1.2f; break;
            }

            seconds *= 0.7f + (float)_rng.NextDouble() * 0.6f;
            TacticalTasks.Set(bot, new TacticalTask
            {
                Kind = TaskKind.Listen,
                LookAt = kill.Position,
                Until = Time.time + seconds,
                Prone = false,
            });
            Log.Debug($"{p.Name} listens for {seconds:0.0}s after the kill");
        }

        /// <summary>What a player sees at a glance: gun, armor, helmet, rig and bag. Bag contents count less (not visible).</summary>
        public static float VisibleValue(InventoryEquipment equipment, string cacheKey = null)
        {
            if (equipment == null)
            {
                return 0;
            }

            if (cacheKey != null && _valueCache.TryGetValue(cacheKey, out var cached) && Time.time - cached.at < 60f)
            {
                return cached.value;
            }

            float total = 0;
            EquipmentSlot[] slots =
            {
                EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.ArmorVest,
                EquipmentSlot.TacticalVest, EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover,
            };
            foreach (EquipmentSlot slot in slots)
            {
                // The appraiser prices weapons with their mods and armor with its plates
                total += Price(equipment.GetSlot(slot)?.ContainedItem);
            }

            // Rig and bag contents are only half-visible
            total += ContentsValue(equipment.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem) * 0.3f;
            Item backpack = equipment.GetSlot(EquipmentSlot.Backpack)?.ContainedItem;
            if (backpack != null)
            {
                total += Price(backpack) + ContentsValue(backpack) * 0.4f;
            }

            if (cacheKey != null)
            {
                _valueCache[cacheKey] = (total, Time.time);
            }

            return total;
        }

        public static float Price(Item item)
        {
            if (item == null)
            {
                return 0;
            }

            try
            {
                // LootingBots' appraiser already prices the whole stack (count x price): multiplied again here a stack of 50 000 roubles was "worth" 2.5 billion
                return LootingBots.External.GetItemPrice(item);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Value of the items lying directly in a container's grids (not the container itself).</summary>
        internal static float ContentsValue(Item container)
        {
            if (!(container is CompoundItem compound) || compound.Grids == null)
            {
                return 0;
            }

            float sum = 0;
            foreach (var grid in compound.Grids)
            {
                foreach (Item child in grid.Items)
                {
                    sum += Price(child);
                }
            }

            return sum;
        }

        private static float Risk(BotOwner bot, BotProfile p, Vector3 corpsePos, KillEvent kill, float open)
        {
            HashSet<string> friends = SquadIds(bot);
            float risk = 0;

            // Shooting around the body and around me
            risk += Math.Min(1.2f, 0.35f * WorldEvents.ShotPressure(corpsePos, 70f, 40f, friends));
            risk += Math.Min(0.6f, 0.25f * WorldEvents.ShotPressure(bot.Position, 50f, 20f, friends));

            // Somebody else killed him and may still be watching the body
            if (kill != null && kill.KillerId != null && !friends.Contains(kill.KillerId))
            {
                float age = Time.time - kill.Time;
                if (age < 120f)
                {
                    risk += 0.6f * (1f - age / 120f);
                }

                Player killer = Singleton<GameWorld>.Instance.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == kill.KillerId);
                if (killer != null && (killer.Position - corpsePos).sqrMagnitude < 100f * 100f)
                {
                    risk += 0.5f;
                }
            }

            // A body in the open is a body in someone's crosshair
            risk += 0.35f * open;

            if (bot.Medecine?.FirstAid?.Have2Do == true)
            {
                risk += 0.3f;
            }

            // The more I carry, the more I can lose
            float myKit = VisibleValue(bot.GetPlayer?.Equipment);
            risk += 0.15f * (float)Math.Log10(1 + myKit / 50000f);
            float carried = LootingBots.External.GetNetLootValue(bot);
            risk += 0.1f * (float)Math.Log10(1 + Math.Max(0, carried) / 50000f);

            // Friends nearby cover me
            int alliesNear = 0;
            foreach (string id in friends)
            {
                if (id == bot.ProfileId)
                {
                    continue;
                }

                Player ally = Singleton<GameWorld>.Instance.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id);
                if (ally != null && (ally.Position - bot.Position).sqrMagnitude < 40f * 40f)
                {
                    alliesNear++;
                }
            }

            risk -= Math.Min(0.5f, 0.2f * alliesNear);
            return Math.Max(0, risk);
        }

        public static HashSet<string> SquadIds(BotOwner bot)
        {
            HashSet<string> ids = new HashSet<string> { bot.ProfileId };
            BotsGroup group = bot.BotsGroup;
            if (group != null)
            {
                for (int i = 0; i < group.MembersCount; i++)
                {
                    BotOwner member = group.Member(i);
                    if (member != null)
                    {
                        ids.Add(member.ProfileId);
                    }
                }
            }

            // Squadmates QuestingBots moved into a group of their own are squadmates all the same (SquadTies)
            foreach (string mate in SquadTies.MatesOf(bot.ProfileId))
            {
                ids.Add(mate);
            }

            return ids;
        }

        /// <summary>0 = boxed in by walls, 1 = wide open. Horizontal rays at chest height.</summary>
        public static float Openness(Vector3 pos)
        {
            int hits = 0;
            Vector3 origin = pos + Vector3.up * 1.2f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                if (Physics.Raycast(origin, dir, 6f, LayersMaskController.HighPolyWithTerrainMask))
                {
                    hits++;
                }
            }

            return 1f - hits / 8f;
        }

    }
}
