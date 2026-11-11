using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// The bodies a bot means to go through, kept until it has (F12 -> 7 "Clean up after a fight").
    ///
    /// Raid 2026-10-22: bots decided "loot now" on their own kill six times in a row and never got anything - every try was broken off (shots, a stranger
    /// heard, a squadmate called for help) and nothing brought the bot back: LootingBots forgot the target and QuestingBots led the bot on. A fallen
    /// squadmate's body was asked for five times and never searched. Here every body worth going to is a job of the bot:
    ///  - its own kill, the bodies a squadmate killed nearby, its fallen squadmate's body (MateRecovery), and after a fight every body within 50 m that it
    ///    or its squad has not searched (enemies of this fight and the ones lying there before);
    ///  - whenever the bot is calm and nothing is about (LootDanger), the most important and nearest job is asked for (LootingBots' priority corpse, a scan
    ///    QuestingBots stands aside for); interrupted, it is asked for again later - up to 5 times within 8 minutes;
    ///  - a job is done when the bot (or a squadmate) has searched the body;
    ///  - while any member of a squad still has jobs nearby, the squad does not move on to its next objective (the leader waits) - like players who strip
    ///    the bodies and the place after a fight and only then go on.
    /// </summary>
    public static class LootJobs
    {
        // Raid 2026-10-22 15:57: 355 jobs, 926 forced scans, 26 counted done while 161 bodies were in fact gone through - every job a squadmate had
        // searched, or that held nothing worth taking any more, was asked for every 15 s up to 5 times, each time pulling the bot off what it was doing.
        // Now: done when anyone of the squad searched it or it holds less than 5k; 3 tries 30 s apart (5 for a fallen squadmate)
        private const int MaxAttempts = 3;
        private const int MaxMateAttempts = 5;
        private const float RetryAfter = 30f;
        private const float EmptyBelow = 5000f;
        private const float JobLife = 480f;
        // A job does not run out while the bot is still fighting (raid 2026-10-26 18:34: Waffle's six own kills ran out one by one while he fought on for
        // eight minutes, and when it was quiet at last none was left) - only after this long without a fight, and never later than MaxLife
        private const float QuietLife = 180f;
        private const float MaxLife = 1200f;
        // How far (straight) a bot goes back for a body. Raid 2026-10-26 15:48: Макс Ипотека killed two PMCs, QuestingBots walked him on, and 60 m later the
        // jobs were silently out of reach - a player goes back a hundred metres for his own kill. The body is handed over directly (RoomSweep.HandBody),
        // the walk may be up to 1.6 x the straight distance
        private const float BodyReach = 60f;
        private const float OwnKillReach = 110f;
        private const float MateReach = 150f;
        private const float RearmReach = 90f;
        private const float CleanupRadius = 50f;
        private const float MinWorth = 8000f;
        // A full bag still goes to a body worth this much at a glance (MakeRoom throws out cheap things for it)
        private const float FullBagWorth = 30000f;

        public enum Kind
        {
            Body,
            OwnKill,
            Mate,
            // Out of rounds for every gun: the nearest body may have ammo or a gun
            Rearm,
        }

        private class Job
        {
            public string VictimId;
            public string EquipmentId;
            public EFT.InventoryLogic.InventoryEquipment Equipment;
            public string Name;
            public Vector3 Pos;
            public Kind Kind;
            public float Added;
            public int Attempts;
            public float NextTry;
            public float AskedAt = -1f;
        }

        private static readonly Dictionary<string, List<Job>> _jobs = new Dictionary<string, List<Job>>();
        private static readonly Dictionary<string, float> _cleanedAt = new Dictionary<string, float>();
        // Bodies a bot is done with (bot|victim): the look round after a fight found them again every 20 s and they were added and dropped at once
        // (raid 2026-10-30 23:28: Traveler "will go through" / "is done with" Lenya Leshch's body seven times)
        private static readonly HashSet<string> _doneBodies = new HashSet<string>();
        private static float _next;

        public static int Added { get; private set; }
        public static int Done { get; private set; }
        public static int Asked { get; private set; }
        public static int Handed { get; private set; }
        public static int Dropped { get; private set; }
        public static int Waits { get; private set; }

        public static string Summary => $"body jobs: {Added} added, {Done} searched, {Asked} times asked for ({Handed} handed straight to the body), {Dropped} given up, " +
                                        $"squads waited for their looters {Waits} times";

        public static void Clear()
        {
            _jobs.Clear();
            _cleanedAt.Clear();
            _doneBodies.Clear();
            _waitFrom.Clear();
            _waitSeen.Clear();
            _next = 0f;
            Added = Done = Asked = Handed = Dropped = Waits = 0;
        }

        private static bool Eligible(BotOwner bot)
        {
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            return p != null && p.Kind != BotKind.Other;
        }

        public static void Add(BotOwner bot, KillEvent kill, Kind kind)
        {
            if (!Settings.CleanUpAfterFight.Value || bot == null || kill == null || string.IsNullOrEmpty(kill.EquipmentId) || !Eligible(bot) || bot.ProfileId == kill.VictimId)
            {
                return;
            }

            if (!_jobs.TryGetValue(bot.ProfileId, out List<Job> list))
            {
                _jobs[bot.ProfileId] = list = new List<Job>();
            }

            if (kind != Kind.Mate && kind != Kind.Rearm && _doneBodies.Contains(bot.ProfileId + "|" + kill.VictimId))
            {
                return;
            }

            Job old = list.FirstOrDefault(j => j.VictimId == kill.VictimId);
            if (old != null)
            {
                if (kind > old.Kind)
                {
                    old.Kind = kind;
                }

                return;
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            if (brain != null && brain.IsLootIgnored(kill.EquipmentId))
            {
                return;
            }

            list.Add(new Job { VictimId = kill.VictimId, EquipmentId = kill.EquipmentId, Equipment = kill.Equipment, Name = kill.VictimName ?? "a body", Pos = kill.Position, Kind = kind, Added = Time.time });
            Added++;
            Log.Detail("loot", () => $"{bot.Profile?.Nickname} will go through {kill.VictimName}'s body ({kind}, {Vector3.Distance(bot.Position, kill.Position):0} m)");
        }

        /// <summary>A kill: the killer and its squadmates nearby will go through the body.</summary>
        public static void OnKill(KillEvent kill, Player killer)
        {
            BotOwner bot = killer?.AIData?.BotOwner;
            if (bot == null || kill == null)
            {
                return;
            }

            Add(bot, kill, Kind.OwnKill);
            foreach (string id in LootBrain.SquadIds(bot))
            {
                Player mate = Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id);
                BotOwner m = mate?.AIData?.BotOwner;
                if (m != null && m != bot && (m.Position - kill.Position).sqrMagnitude < 60f * 60f)
                {
                    Add(m, kill, Kind.Body);
                }
            }
        }

        /// <summary>The bot has a job it means to do now or soon: QuestingBots should not lead it (or its squad) away.</summary>
        /// <summary>The bot is sent away from a place for good (RoamPlanner's overstay): the bodies it meant to go through there are let go, or they call it back.</summary>
        public static int DropNear(BotOwner bot, Vector3 centre, float radius)
        {
            if (bot == null || !_jobs.TryGetValue(bot.ProfileId, out List<Job> list))
            {
                return 0;
            }

            return list.RemoveAll(j =>
            {
                float dx = j.Pos.x - centre.x, dz = j.Pos.z - centre.z;
                return dx * dx + dz * dz < radius * radius;
            });
        }

        public static bool HasWorkNear(BotOwner bot, float range = 60f)
        {
            if (bot == null || !_jobs.TryGetValue(bot.ProfileId, out List<Job> list))
            {
                return false;
            }

            float now = Time.time;
            return list.Any(j => j.Attempts < Limit(j) && !Expired(bot, j, now) && (j.Pos - bot.Position).sqrMagnitude < range * range);
        }

        /// <summary>
        /// A squad leader with squadmates still going through bodies (or looting) nearby waits for them before the next objective (QbDecisionPatch).
        /// </summary>
        public static bool LeaderShouldWait(BotOwner leader)
        {
            // The squad's own rule (any looting, not only bodies after a fight - with patience and a call to go on)
            if (Settings.SquadLootWait.Value)
            {
                return SquadLoot.LeaderWaits(leader);
            }

            if (!Settings.CleanUpAfterFight.Value || leader?.Boss?.Followers == null || leader.Boss.Followers.Count == 0)
            {
                return false;
            }

            // At most 2 minutes in a row, then the squad goes on (and the leader does not wait again for 3 minutes)
            float now = Time.time;
            if (_waitFrom.TryGetValue(leader.ProfileId, out float from) && now - from > 120f)
            {
                if (now - from > 300f)
                {
                    _waitFrom.Remove(leader.ProfileId);
                }

                return false;
            }

            if (!Waiting(leader))
            {
                if (_waitFrom.TryGetValue(leader.ProfileId, out float started) && now - started < 120f && now - _waitSeen.GetValueOrDefault(leader.ProfileId) > 5f)
                {
                    _waitFrom.Remove(leader.ProfileId);
                }

                return false;
            }

            if (!_waitFrom.ContainsKey(leader.ProfileId))
            {
                _waitFrom[leader.ProfileId] = now;
            }

            _waitSeen[leader.ProfileId] = now;
            Waits++;
            return true;
        }

        private static readonly Dictionary<string, float> _waitFrom = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _waitSeen = new Dictionary<string, float>();

        private static bool Waiting(BotOwner leader)
        {
            foreach (BotOwner f in leader.Boss.Followers)
            {
                if (f == null || f.IsDead || (f.Position - leader.Position).sqrMagnitude > 50f * 50f)
                {
                    continue;
                }

                LootingBrain brain = f.GetPlayer?.GetComponent<LootingBrain>();
                if (HasWorkNear(f, 50f) || (brain != null && (brain.LootTaskRunning || brain.HasActiveLootable)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Once a second: after a fight bodies around are noted; calm bots are sent to their most important body.</summary>
        public static void Tick()
        {
            if (!Settings.CleanUpAfterFight.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot) || !Eligible(bot))
                {
                    continue;
                }

                NoteBodiesAfterFight(bot, now);
                if (_jobs.TryGetValue(bot.ProfileId, out List<Job> list) && list.Count > 0)
                {
                    Diag.Guard("LootJobs", () => Work(bot, player, list, now));
                }
            }
        }

        /// <summary>A fight ended a little while ago (the bot saw an enemy or was shot at in the last 5 minutes): the bodies around it become jobs.</summary>
        private static void NoteBodiesAfterFight(BotOwner bot, float now)
        {
            if (BotMind.SinceEngaged(bot) > 300f || !TacticalTasks.NoThreat(bot) || (_cleanedAt.TryGetValue(bot.ProfileId, out float at) && now - at < 20f))
            {
                return;
            }

            _cleanedAt[bot.ProfileId] = now;
            HashSet<string> squad = LootBrain.SquadIds(bot);
            foreach (KillEvent k in WorldEvents.Kills)
            {
                if (squad.Contains(k.VictimId) || (k.Position - bot.Position).sqrMagnitude > CleanupRadius * CleanupRadius || now - k.Time > 900f)
                {
                    continue;
                }

                float worth = LootBrain.VisibleValue(k.Equipment, "body" + k.VictimId);
                bool ours = k.KillerId != null && squad.Contains(k.KillerId);
                if (ours || worth >= MinWorth)
                {
                    Add(bot, k, ours && k.KillerId == bot.ProfileId ? Kind.OwnKill : Kind.Body);
                }
            }
        }

        private static void Work(BotOwner bot, Player player, List<Job> list, float now)
        {
            LootingBrain brain = player.GetComponent<LootingBrain>();
            LootFinder finder = player.GetComponent<LootFinder>();
            if (brain == null || finder == null)
            {
                return;
            }

            // Searched (by the bot or anyone of its squad), emptied, gone or too old
            HashSet<string> squad = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Job j = list[i];
                if (brain.IsLootIgnored(j.EquipmentId) || SquadSearched(bot, j, ref squad)
                    || (j.Kind != Kind.Mate && j.Kind != Kind.Rearm && j.Equipment?.GetSlot(EFT.InventoryLogic.EquipmentSlot.Backpack)?.ContainedItem == null
                        && LootBrain.VisibleValue(j.Equipment, "body" + j.VictimId) < EmptyBelow))
                {
                    list.RemoveAt(i);
                    Done++;
                    _doneBodies.Add(bot.ProfileId + "|" + j.VictimId);
                    Log.Detail("loot", () => $"{bot.Profile?.Nickname} is done with {j.Name}'s body");
                }
                else if (j.Attempts >= Limit(j) || Expired(bot, j, now) || (j.Pos - bot.Position).sqrMagnitude > 250f * 250f)
                {
                    list.RemoveAt(i);
                    Dropped++;
                    Log.Detail("loot", () => $"{bot.Profile?.Nickname} gives up on {j.Name}'s body ({j.Attempts} tries)");
                }
            }

            if (list.Count == 0 || brain.LootTaskRunning || brain.HasActiveLootable || finder.IsScanRunning)
            {
                return;
            }

            Ask(bot, brain, finder, list, now);
        }

        /// <summary>
        /// The most important body within reach is handed to the bot now, if it is quiet and the bot is free. QuestingBots' loot break asks here first
        /// (Harmonizer), so a break never sends the bot past its own kill to a drawer. True when the bot goes for a body.
        /// </summary>
        public static bool AskNow(BotOwner bot)
        {
            if (!Settings.CleanUpAfterFight.Value || bot == null || bot.IsDead || !_jobs.TryGetValue(bot.ProfileId, out List<Job> list) || list.Count == 0)
            {
                return false;
            }

            Player player = bot.GetPlayer;
            LootingBrain brain = player != null ? player.GetComponent<LootingBrain>() : null;
            LootFinder finder = player != null ? player.GetComponent<LootFinder>() : null;
            if (brain == null || finder == null || brain.LootTaskRunning || brain.HasActiveLootable || finder.IsScanRunning)
            {
                return false;
            }

            return Ask(bot, brain, finder, list, Time.time);
        }

        private static bool Ask(BotOwner bot, LootingBrain brain, LootFinder finder, List<Job> list, float now)
        {
            // Only when it is quiet and nothing else (an assist, a handover, a relocation) is going on
            if (!TacticalTasks.NoThreat(bot) || LootDanger.Why(bot) != null || TacticalTasks.TryGet(bot, out _) || bot.Medecine?.Using == true)
            {
                return false;
            }

            // LootingBots would not take the bot now (a wound it treats first, not allowed): no try is spent. A full bag is no reason here - the body is
            // handed over directly, and whether the bot can take anything is up to the rules below
            string wont = Patches.LootKick.WontLoot(bot, brain);
            if (wont != null && wont != "its bag is full")
            {
                return false;
            }

            bool bagFull = LootingBots.External.CheckIfInventoryFull(bot);
            Job next = list.Where(j => now >= j.NextTry && (j.Pos - bot.Position).sqrMagnitude < Reach(j) * Reach(j) && (!bagFull || WorthWithFullBag(bot, j)))
                .OrderByDescending(j => (int)j.Kind)
                .ThenBy(j => (j.Pos - bot.Position).sqrMagnitude)
                .FirstOrDefault();
            if (next == null)
            {
                return false;
            }

            next.Attempts++;
            next.AskedAt = now;
            next.NextTry = now + RetryAfter;
            Asked++;
            RoomSweep.Release(bot);
            float straight = Vector3.Distance(bot.Position, next.Pos);
            if (RoomSweep.HandBody(bot, next.VictimId, next.Pos, Reach(next) * 1.6f, out float path))
            {
                Handed++;
                Log.Detail("loot", () => $"{bot.Profile?.Nickname} goes for {next.Name}'s body ({next.Kind}, try {next.Attempts}, {path:0} m on foot)");
                return true;
            }

            // Not on the navmesh near the kill spot (the body slid, or lies where no path goes): LootingBots' own scan with the body first in line
            finder.EnqueuePriorityCorpse(next.VictimId);
            RoomSweep.ForceScan(bot);
            Log.Detail("loot", () => $"{bot.Profile?.Nickname} goes for {next.Name}'s body ({next.Kind}, try {next.Attempts}, {straight:0} m, by a scan)");
            return true;
        }

        private static float Reach(Job j)
        {
            switch (j.Kind)
            {
                case Kind.OwnKill: return OwnKillReach;
                case Kind.Mate: return MateReach;
                case Kind.Rearm: return RearmReach;
                default: return BodyReach;
            }
        }

        /// <summary>A full bag still goes for a fallen squadmate, for ammo, or for a body clearly worth making room for.</summary>
        private static bool WorthWithFullBag(BotOwner bot, Job j)
        {
            if (j.Kind == Kind.Mate || j.Kind == Kind.Rearm)
            {
                return true;
            }

            // Only if it could drop something for what lies there - otherwise it walks to the body and away again
            float worth = Settings.DropCheapForBetter.Value ? LootBrain.VisibleValue(j.Equipment, "body" + j.VictimId) : 0f;
            return worth >= FullBagWorth && MakeRoom.CouldMakeRoomFor(bot, worth);
        }

        /// <summary>The bot was sent to one of its bodies within the last 25 s and has not finished with it: it is busy with loot (SquadLoot).</summary>
        public static bool WorkingOn(BotOwner bot)
        {
            if (bot == null || !_jobs.TryGetValue(bot.ProfileId, out List<Job> list))
            {
                return false;
            }

            float now = Time.time;
            return list.Any(j => j.AskedAt > 0f && now - j.AskedAt < 25f);
        }

        /// <summary>Out of rounds for every gun: the nearest body it has not searched within reach becomes its first job.</summary>
        public static bool AddRearm(BotOwner bot)
        {
            if (!Settings.CleanUpAfterFight.Value || bot == null || !Eligible(bot))
            {
                return false;
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            KillEvent best = null;
            float bestSq = RearmReach * RearmReach;
            foreach (KillEvent k in WorldEvents.Kills)
            {
                float sq = (k.Position - bot.Position).sqrMagnitude;
                if (sq < bestSq && k.VictimId != bot.ProfileId && !string.IsNullOrEmpty(k.EquipmentId) && (brain == null || !brain.IsLootIgnored(k.EquipmentId)))
                {
                    best = k;
                    bestSq = sq;
                }
            }

            if (best == null)
            {
                return false;
            }

            Add(bot, best, Kind.Rearm);
            return true;
        }

        private static bool Expired(BotOwner bot, Job j, float now) =>
            now - j.Added > MaxLife || (now - j.Added > JobLife && BotMind.SinceEngaged(bot) > QuietLife);

        /// <summary>
        /// A body that matters and is still to be gone through by the bot or a squadmate of its: a fallen squadmate's, its own kill, ammo when out of rounds -
        /// within reach, tries left. Described for the log ("Spock's body (a squadmate, 61 m)"), or null. The exit waits for it (ExtractPlanner).
        /// </summary>
        public static string Unfinished(BotOwner bot)
        {
            if (!Settings.CleanUpAfterFight.Value || bot == null)
            {
                return null;
            }

            var who = new List<BotOwner> { bot };
            if (bot.Boss?.Followers != null)
            {
                who.AddRange(bot.Boss.Followers.Where(f => f != null && !f.IsDead));
            }

            float now = Time.time;
            foreach (BotOwner b in who)
            {
                if (!_jobs.TryGetValue(b.ProfileId, out List<Job> list))
                {
                    continue;
                }

                foreach (Job j in list)
                {
                    if (j.Kind == Kind.Body || j.Attempts >= Limit(j) || Expired(b, j, now) || (j.Pos - b.Position).sqrMagnitude > Reach(j) * Reach(j))
                    {
                        continue;
                    }

                    string kind = j.Kind == Kind.Mate ? "a squadmate" : j.Kind == Kind.OwnKill ? "its kill" : "ammo";
                    return $"{j.Name}'s body ({kind}{(b != bot ? " of " + b.Profile?.Nickname : "")}, {Vector3.Distance(b.Position, j.Pos):0} m)";
                }
            }

            return null;
        }

        private static int Limit(Job j) => j.Kind == Kind.Mate || j.Kind == Kind.Rearm ? MaxMateAttempts : MaxAttempts;

        /// <summary>Someone of the bot's squad has gone through the body (their LootingBots lists).</summary>
        private static bool SquadSearched(BotOwner bot, Job j, ref HashSet<string> squad)
        {
            squad ??= LootBrain.SquadIds(bot);
            if (squad.Count <= 1)
            {
                return false;
            }

            foreach (Player p in Singleton<GameWorld>.Instance?.AllAlivePlayersList ?? new List<Player>())
            {
                if (p == null || p.ProfileId == bot.ProfileId || !squad.Contains(p.ProfileId))
                {
                    continue;
                }

                LootingBrain b = p.GetComponent<LootingBrain>();
                if (b != null && b.IsLootIgnored(j.EquipmentId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The body is one of the bot's jobs (LootBrain loots it without weighing again).</summary>
        public static bool IsJob(BotOwner bot, string victimId) =>
            bot != null && victimId != null && _jobs.TryGetValue(bot.ProfileId, out List<Job> list) && list.Any(j => j.VictimId == victimId);
    }
}
