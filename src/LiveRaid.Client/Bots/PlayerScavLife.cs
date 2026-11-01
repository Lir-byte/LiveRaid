using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Player scavs as online (F12 -> 3 "Player scavs play like online"). QuestingBots already brings them in late, at the raid times the game
    /// gives scav runs. What they did then was a PMC's raid. Online a player scav:
    ///  - is in for a short run: 7-15 minutes from coming in (a looter 10-18, a "chad" 12-20), then off to an exit - not the PMC's hour;
    ///  - goes for the bodies a fight left behind: a PMC body of the last 8 minutes within 220 m, once it has gone quiet there (no shots within
    ///    50 m for 25 s), runs there and strips it - the classic "scav that came for the loot";
    ///  - stays out of PMC fights (it hears them and waits or leaves) - except one in eight, the "chad scav", who goes PMC hunting;
    ///  - is friends with the AI scavs until it kills one: then the AI scavs within 150 m of the kill take it for an enemy (the game's karma).
    /// The GunfireResponse weights for player scavs are in GunfireResponse (IsChad).
    /// </summary>
    public static class PlayerScavLife
    {
        private static readonly HashSet<string> _chads = new HashSet<string>();
        private static readonly HashSet<string> _bodiesTried = new HashSet<string>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Runs { get; private set; }
        public static int BodyRuns { get; private set; }
        public static int Karma { get; private set; }

        public static string Summary => $"player scavs: {Runs} came in ({_chads.Count} chads), runs for bodies {BodyRuns}, AI scavs turned on a player scav for a kill {Karma}";

        public static bool On => Settings.Enabled.Value && Settings.PlayerScavLife.Value;

        public static void Clear()
        {
            _chads.Clear();
            _bodiesTried.Clear();
            _next = 0f;
            Runs = BodyRuns = Karma = 0;
        }

        public static bool IsChad(BotOwner bot) => bot != null && _chads.Contains(bot.ProfileId);

        /// <summary>At activation: a short run of its own from now, one in eight is a chad.</summary>
        public static void OnActivated(BotOwner bot)
        {
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (!On || p == null || p.Kind != BotKind.PlayerScav)
            {
                return;
            }

            bool chad = new System.Random(bot.ProfileId.GetHashCode()).NextDouble() < 0.125;
            if (chad)
            {
                _chads.Add(bot.ProfileId);
            }

            float minutes = chad ? 12f + (float)_rng.NextDouble() * 8f
                : p.Style == PlayStyle.Looter ? 10f + (float)_rng.NextDouble() * 8f
                : 7f + (float)_rng.NextDouble() * 8f;
            float total = Mathf.Max(600f, RaidHelpers.OriginalEscapeTimeSeconds);
            float now = RaidHelpers.GetRaidTimeRemainingFraction();
            p.ExtractAtRemaining = Mathf.Clamp(now - minutes * 60f / total, 0.03f, 0.9f);
            Runs++;
            Log.Info($"LR PScav: {p.Name} comes in with {now * total / 60f:0} min left{(chad ? " - a chad, hunts PMCs" : "")}, a run of about {minutes:0} min " +
                     $"(leaves at {p.ExtractAtRemaining:P0} left)");
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            List<KillEvent> bodies = WorldEvents.Kills.Where(k => k.VictimPmc && now - k.Time > 30f && now - k.Time < 480f && !string.IsNullOrEmpty(k.EquipmentId)).ToList();
            if (bodies.Count == 0)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind != BotKind.PlayerScav || Spectator.TakeOver.IsTaken(bot) || TacticalTasks.SquadLeaderOf(bot) != null
                    || TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || ExtractPlanner.IsLeaving(bot) || LootJobs.WorkingOn(bot))
                {
                    continue;
                }

                LootingBots.Components.LootingBrain brain = player.GetComponent<LootingBots.Components.LootingBrain>();
                if (brain != null && (brain.LootTaskRunning || !brain.HasFreeSpace))
                {
                    continue;
                }

                KillEvent body = bodies
                    .Where(k => !_bodiesTried.Contains(bot.ProfileId + "|" + k.VictimId) && (k.Position - bot.Position).sqrMagnitude < 220f * 220f
                                && (brain == null || !brain.IsLootIgnored(k.EquipmentId)))
                    .OrderBy(k => (k.Position - bot.Position).sqrMagnitude)
                    .FirstOrDefault();
                if (body == null)
                {
                    continue;
                }

                _bodiesTried.Add(bot.ProfileId + "|" + body.VictimId);

                // Still shooting there: wait (a chad goes anyway)
                if (!IsChad(bot) && WorldEvents.ShotPressure(body.Position, 50f, 25f, null) > 0f)
                {
                    _bodiesTried.Remove(bot.ProfileId + "|" + body.VictimId);
                    continue;
                }

                float dist = Vector3.Distance(bot.Position, body.Position);
                Vector3? stand = dist < 8f ? body.Position : ScavPatrol.Short(bot, body.Position, 3f);
                if (stand == null)
                {
                    continue;
                }

                KillEvent target = body;
                TacticalTask task = new TacticalTask
                {
                    Kind = TaskKind.Investigate, StandAt = stand, LookAt = body.Position, Speed = 1f, Sprint = true, SprintBeyond = 15f, MovePose = 1f,
                    Until = now + dist / 3f + 20f,
                    OnFinished = b =>
                    {
                        LootJobs.Add(b, target, LootJobs.Kind.OwnKill);
                        LootJobs.AskNow(b);
                    },
                };
                if (TacticalTasks.Set(bot, task))
                {
                    BodyRuns++;
                    Log.Info($"LR PScav: {p.Name} runs for {body.VictimName}'s body {dist:0} m away (a PMC, dead {(now - body.Time) / 60f:0.0} min)");
                }
            }
        }

        /// <summary>A player scav bot killed an AI scav: the AI scavs within 150 m take it for an enemy from now on.</summary>
        public static void OnKill(KillEvent kill, Player killer)
        {
            BotOwner killerBot = killer?.AIData?.BotOwner;
            if (!On || killerBot == null || kill == null || kill.VictimKind != BotKind.Scav || BotProfiles.Get(killerBot)?.Kind != BotKind.PlayerScav)
            {
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            int turned = 0;
            HashSet<BotsGroup> groups = new HashSet<BotsGroup>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotsGroup == null || BotProfiles.Get(bot)?.Kind != BotKind.Scav || (bot.Position - kill.Position).sqrMagnitude > 150f * 150f)
                {
                    continue;
                }

                if (groups.Add(bot.BotsGroup))
                {
                    bot.BotsGroup.AddEnemy(killer, EBotEnemyCause.followGetHit);
                    turned++;
                }
            }

            if (turned > 0)
            {
                Karma++;
                Log.Info($"LR PScav: {killer.Profile?.Nickname} killed the AI scav {kill.VictimName} - {turned} scav group(s) about take him for an enemy");
            }
        }
    }
}
