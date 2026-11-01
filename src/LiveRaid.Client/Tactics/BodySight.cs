using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A body nobody has searched, as a player sees it (F12 -> 7 "Notice bodies nobody has searched").
    ///
    /// Raid 2026-10-26 15:48: bodies lay untouched next to bots walking past - a body became a job only for its killer, his squad, or within 50 m right after the
    /// bot's own fight; anyone else went by unless LootingBots' scan happened to pick it. A player who sees a body stops to size it up: the gun, the armour, the
    /// helmet and the rig he can see, a PMC or a scav - and a backpack, whose contents nobody sees, is nearly always worth opening. Then he loots it or walks on.
    ///  - Seen: in front of the bot (within 75 degrees of where it looks) up to 45 m with nothing in the way, or anywhere within 12 m.
    ///  - Chance: 0.2 + 0.32 x log10(1 + gear seen / 15k) (a bum ~0.25, a regular PMC ~0.45, a full kit ~0.6), +0.15 for a PMC, at least 0.88 with a backpack on it;
    ///    x 0.82-1.18 by the bot's greed; a full bag x 0.5 (x 0.85 for 60k+ of gear, worth making room for); a stranger's fresh kill with the killer about x 0.75.
    ///  - A scav and a PMC's body: at least 0.92 (raid 2026-10-26 18:34: of 14 dead PMCs 7 were ever searched) - a PMC's kit is what a scav is out for.
    ///  - Yes: the body is the bot's job (LootJobs - taken up whenever it is quiet). No: the bot may think again if it still sees the body 3 minutes later.
    /// </summary>
    public static class BodySight
    {
        private const float SeeRange = 45f;
        private const float NearRange = 12f;
        private const float FieldOfView = 75f;

        private const float ThinkAgainAfter = 180f;

        // Bot|body -> when it may weigh the body again (never, once it took it or the body was searched)
        private static readonly Dictionary<string, float> _weighed = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Seen { get; private set; }
        public static int Taken { get; private set; }

        public static string Summary => $"bodies nobody searched sized up {Seen} times, {Taken} of them taken as jobs";

        public static void Clear()
        {
            _weighed.Clear();
            _next = 0f;
            Seen = Taken = 0;
        }

        /// <summary>Twice a second: calm bots look at the bodies around them.</summary>
        public static void Tick()
        {
            if (!Settings.NoticeBodies.Value || !Settings.CleanUpAfterFight.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<KillEvent> bodies = null;
            foreach (KillEvent k in WorldEvents.Kills)
            {
                // A moment for the killer to act first; no gear id - nothing LootingBots could search
                if (Time.time - k.Time > 3f && !string.IsNullOrEmpty(k.EquipmentId) && k.Equipment != null)
                {
                    (bodies ?? (bodies = new List<KillEvent>())).Add(k);
                }
            }

            if (bodies == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other || !TacticalTasks.NoThreat(bot))
                {
                    continue;
                }

                Diag.Guard("BodySight", () => Look(bot, player, p, bodies));
            }
        }

        private static void Look(BotOwner bot, Player player, BotProfile p, List<KillEvent> bodies)
        {
            LootingBrain brain = player.GetComponent<LootingBrain>();
            if (brain == null || !brain.LootingEnabled)
            {
                return;
            }

            Vector3 eye = bot.Position + Vector3.up * 1.5f;
            HashSet<string> squad = null;
            foreach (KillEvent k in bodies)
            {
                Vector3 to = k.Position - bot.Position;
                if (to.sqrMagnitude > SeeRange * SeeRange)
                {
                    continue;
                }

                string key = bot.ProfileId + "|" + k.VictimId;
                if (_weighed.TryGetValue(key, out float again) && Time.time < again)
                {
                    continue;
                }

                // Searched already (by it or its squad: LootShareCapturePatch tells them), a job already, or a fallen squadmate (MateRecovery's)
                squad ??= LootBrain.SquadIds(bot);
                if (brain.IsLootIgnored(k.EquipmentId) || LootJobs.IsJob(bot, k.VictimId) || squad.Contains(k.VictimId))
                {
                    _weighed[key] = float.MaxValue;
                    continue;
                }

                float dist = to.magnitude;
                if (dist > NearRange)
                {
                    Vector3 flat = new Vector3(to.x, 0f, to.z);
                    Vector3 look = new Vector3(bot.LookDirection.x, 0f, bot.LookDirection.z);
                    if (look.sqrMagnitude < 0.01f || Vector3.Angle(look, flat) > FieldOfView
                        || Physics.Linecast(eye, k.Position + Vector3.up * 0.35f, LayersMaskController.HighPolyWithTerrainMask))
                    {
                        // Not seen yet: it may be on the way back
                        continue;
                    }
                }

                _weighed[key] = Weigh(bot, p, k, dist, squad) ? float.MaxValue : Time.time + ThinkAgainAfter;
            }
        }

        private static bool Weigh(BotOwner bot, BotProfile p, KillEvent k, float dist, HashSet<string> squad)
        {
            Seen++;
            InventoryEquipment eq = k.Equipment;
            float seen = 0f;
            foreach (EquipmentSlot slot in new[]
                     {
                         EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.ArmorVest,
                         EquipmentSlot.TacticalVest, EquipmentSlot.Headwear, EquipmentSlot.Backpack,
                     })
            {
                seen += LootBrain.Price(eq.GetSlot(slot)?.ContainedItem);
            }

            bool backpack = eq.GetSlot(EquipmentSlot.Backpack)?.ContainedItem != null;
            float chance = 0.2f + 0.32f * Mathf.Log10(1f + seen / 15000f);
            if (k.VictimPmc)
            {
                chance += 0.15f;
            }

            if (backpack)
            {
                chance = Mathf.Max(chance, 0.88f);
            }

            chance *= 0.7f + 0.3f * Mathf.Clamp(p.LootGreed, 0.4f, 1.6f);
            if (k.VictimPmc && (p.Kind == BotKind.Scav || p.Kind == BotKind.PlayerScav))
            {
                chance = Mathf.Max(chance, 0.92f);
            }

            if (LootingBots.External.CheckIfInventoryFull(bot))
            {
                chance *= seen >= 60000f ? 0.85f : 0.5f;
            }

            // Somebody else's fresh kill, and he may still be about: a little more careful
            if (k.KillerId != null && !squad.Contains(k.KillerId) && Time.time - k.Time < 60f)
            {
                Player killer = null;
                foreach (Player pl in Singleton<GameWorld>.Instance.AllAlivePlayersList)
                {
                    if (pl != null && pl.ProfileId == k.KillerId)
                    {
                        killer = pl;
                        break;
                    }
                }

                if (killer != null && (killer.Position - k.Position).sqrMagnitude < 100f * 100f)
                {
                    chance *= 0.75f;
                }
            }

            chance = Mathf.Clamp(chance, 0.05f, 0.95f);
            bool take = _rng.NextDouble() < chance;
            string line = $"LR Loot: {p.Name} ({p.Kind} {p.Style}) sees {k.VictimName}'s body {dist:0} m away ({(k.VictimPmc ? "a PMC" : "a scav")}, ~{seen / 1000f:0}k " +
                          $"of gear{(backpack ? ", a backpack" : "")}) - {(take ? "goes to loot it" : "leaves it")} ({chance:P0})";
            if (Seen <= 60)
            {
                Log.Info(line);
            }
            else
            {
                Log.Detail("loot", line);
            }

            if (take)
            {
                Taken++;
                LootJobs.Add(bot, k, LootJobs.Kind.Body);
            }

            return take;
        }
    }
}
