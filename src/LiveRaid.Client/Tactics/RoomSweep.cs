using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using LootingBots.Utilities;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// How a player loots a building: he walks into a room and, if nobody bothers him, goes through all of it - every drawer, cabinet, bag, body and
    /// whatever lies on the floor - then into the next room, and the longer he has been at it the likelier he is to call it enough and leave.
    /// LootingBots takes the NEAREST thing by straight distance within 80 m (through walls, up and down floors too), gives up after three things whose way
    /// is too long, and hands the bot back to QuestingBots: one item, then the other side of the building - or nothing at all.
    ///
    /// Raid 2026-10-22 16:49 (0.5.0): 55 "has gone through room 1 and goes on to the next one", 30 of them followed by "forced scan found nothing" -
    /// the next room was left to LootingBots' scan, it found nothing within the building, and the visit silently ended after one container.
    ///
    /// Now the visit picks every next thing itself and hands it to LootingBots directly (as if its scan had found it):
    ///  - ROOM: from where the first thing was taken. The next is the nearest - on foot - unsearched container, body or loose thing worth taking in the
    ///    same room (straight distance &lt;= 14 m, the same floor, no wall between). Up to 12 things per room.
    ///  - Room done: chance to go on to the next room 100 / 95 / 85 / 65 / 55 / 45 / 35 / 25 % for the 1st, 2nd... room done (x0.8..1.0 by the bot's greed; see
    ///    <see cref="GoOnChance"/>). Yes: the
    ///    nearest unsearched thing elsewhere in the building (40 m round where the visit began, 9 m up or down), in a room no squadmate is going through
    ///    if there is one - its room is the next. Nothing left: the building is done.
    ///  - Patience: 150-470 s per building (by greed), 30 things at most; a fight, danger or a full bag ends it.
    ///  - Out: the bot goes about its quest, LootingBots does not scan for 20-45 s and leaves that building alone for 4 minutes (bodies excepted).
    ///
    /// A room opened with a key (RoomPeek) is searched in full: the visit is marked (<see cref="MarkFullRoom"/>) and then it does not end at the usual room
    /// limits - up to 40 things in the room, then everything within 20 m of the room's middle (on this floor, 40 m of walking), no chance to leave, 7 minutes
    /// of patience; it ends when nothing is left, the bag is full, or a fight/danger drives the bot off.
    /// F12 -> 7 "Search the whole room".
    /// </summary>
    public static class RoomSweep
    {
        private const float RoomRadius = 14f;
        // 0.9.7: 30 m / 7 m - raid 2026-10-30 23:28: 42 of 51 PMC visits ended after one room, 31 of them "nothing left to search in the building"
        private const float BuildingRadius = 40f;
        private const float BuildingHeight = 9f;
        private const int MaxRoomSteps = 12;
        private const int MaxVisitSteps = 30;
        private const float RoomPath = 30f;
        private const float BuildingPath = 65f;
        private const float DoneMemory = 240f;

        private enum Mode
        {
            Room,
            Done,
        }

        private class Visit
        {
            public Vector3 Anchor;
            public Vector3 RoomCenter;
            public Mode Mode;
            public int Rooms;
            public int Steps;
            public int VisitSteps;
            public float VisitUntil;

            // A room opened with the bot's key: searched to the end (MarkFullRoom)
            public bool Full;
            public Vector3 FullCenter;
            public readonly HashSet<string> Tried = new HashSet<string>();
        }

        private struct Target
        {
            public InteractableObject Obj;
            public LootFinder.LootType Type;
            public string Id;
            public Vector3 Pos;
            public Vector3 Dest;
            public float Path;
        }

        private static readonly Collider[] _colliders = new Collider[2048];
        private static readonly Dictionary<string, float> _pending = new Dictionary<string, float>();
        private static readonly Dictionary<string, Visit> _visits = new Dictionary<string, Visit>();
        private static readonly Dictionary<string, (Vector3 center, float until)> _full = new Dictionary<string, (Vector3, float)>();
        private const int MaxFullRoomSteps = 40;
        private const int MaxFullVisitSteps = 80;
        private const float FullRadius = 20f;
        private const float FullPath = 40f;
        private static readonly Dictionary<string, List<(Vector3 at, float until)>> _done = new Dictionary<string, List<(Vector3, float)>>();
        private static readonly System.Random _rng = new System.Random();
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static float _next;

        // Why the last search for the next thing came out empty (the "nothing left" line says it): what the loot layers held, what each filter took out
        private static int _seen, _ignored, _busy, _floor, _where, _share, _noStand, _noPath, _far, _taken, _refused;

        private static void ResetWhy() => _seen = _ignored = _busy = _floor = _where = _share = _noStand = _noPath = _far = _taken = _refused = 0;

        private static string Why() =>
            $"{_seen} lootable thing(s) round it: searched or ignored by LootingBots {_ignored}, in someone's hands {_busy}, another floor or too far {_floor}, " +
            $"behind a wall / outside the room {_where}, a squadmate's share {_share}, no spot to loot from {_noStand}; of the rest no full path {_noPath}, " +
            $"too long a way {_far}, taken by another bot {_taken}, refused {_refused}";

        public static int Rooms { get; private set; }
        public static int Steps { get; private set; }
        public static int NextRooms { get; private set; }
        public static int Leaves { get; private set; }
        public static int Emptied { get; private set; }
        public static int SplitRooms { get; private set; }

        public static string Summary => $"{Rooms} rooms gone through ({Steps} things handed over one after another), {NextRooms} times on to the next room " +
                                        $"({SplitRooms} times to a room no squadmate was in), {Leaves} times left the building, {Emptied} buildings searched to the end";

        public static void Clear()
        {
            _pending.Clear();
            _visits.Clear();
            _full.Clear();
            _done.Clear();
            _next = 0f;
            Rooms = Steps = NextRooms = Leaves = Emptied = SplitRooms = 0;
        }

        /// <summary>
        /// Asks the bot to scan for loot NOW. Through QuestingBots' own looting monitor, not straight to LootingBots: LootingBots' layer has a low priority and only gets the bot while
        /// QuestingBots stands aside, which it does for a forced scan (<c>forcedLootSearchTimer</c>, MaxLootScanTime) or while it sees the bot looting. A direct call started a scan
        /// that QuestingBots at once overrode with the next quest step - the bot took one thing and was led away ("loots one thing and leaves").
        /// </summary>
        public static bool ForceScan(BotOwner bot)
        {
            if (bot == null)
            {
                return false;
            }

            try
            {
                QuestingBots.BotLogic.BotMonitor.Monitors.BotLootingMonitor monitor =
                    QuestingBots.Controllers.BotObjectiveManagerFactory.GetObjectiveManager(bot)?.BotMonitor?.GetMonitor<QuestingBots.BotLogic.BotMonitor.Monitors.BotLootingMonitor>();
                if (monitor != null)
                {
                    return monitor.TryForceBotToScanLoot();
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("RoomSweep.ForceScan", e);
            }

            // No QuestingBots monitor for this bot: straight to LootingBots (this called itself before - a stack overflow waiting to happen)
            return LootingBots.External.ForceBotToScanLoot(bot);
        }

        /// <summary>The bot is going through a room or a building right now (between two things of it).</summary>
        public static bool InVisit(BotOwner bot) =>
            bot != null && Settings.RoomSweep.Value && _visits.TryGetValue(bot.ProfileId, out Visit v) && v.Mode != Mode.Done && Time.time < v.VisitUntil;

        /// <summary>
        /// LootingBots finished (or broke off) a loot target. The next thing of the room is handed over at once: with a pause QuestingBots took the bot
        /// back in between and walked it towards its objective - out of the building and back in for the next drawer. A quick retry covers the case
        /// where the job was still winding down.
        /// </summary>
        public static void OnLootEnded(BotOwner bot)
        {
            if (bot == null || !Settings.RoomSweep.Value)
            {
                return;
            }

            _pending[bot.ProfileId] = Time.time + 0.3f;
            Player player = bot.GetPlayer;
            if (player != null && _visits.ContainsKey(bot.ProfileId))
            {
                Diag.Guard("RoomSweep", () => Next(bot, player));
            }
        }

        /// <summary>The bot has started going through something (arrived at it): a new visit, a new room of the visit, or the same room going on.</summary>
        public static void OnStarted(BotOwner bot)
        {
            if (bot == null || !Settings.RoomSweep.Value)
            {
                return;
            }

            Vector3 pos = bot.Position;
            float now = Time.time;
            if (!_visits.TryGetValue(bot.ProfileId, out Visit v) || v.Mode == Mode.Done || now > v.VisitUntil || FlatDistance(v.Anchor, pos) > BuildingRadius + 5f
                || Mathf.Abs(v.Anchor.y - pos.y) > BuildingHeight + 3f)
            {
                BotProfile p = BotProfiles.Get(bot);
                float greed = Mathf.Clamp(p?.LootGreed ?? 1f, 0.4f, 1.6f);
                _visits[bot.ProfileId] = v = new Visit { Anchor = pos, RoomCenter = pos, Mode = Mode.Room, VisitUntil = now + 150f + 320f * (greed - 0.4f) / 1.2f + (p?.Packrat == true ? 240f : 0f), Rooms = 1 };
                ApplyFull(bot, v, pos);
                return;
            }

            if (!InRoom(v.RoomCenter, pos))
            {
                // LootingBots took the bot to another room of the same building on its own
                v.RoomCenter = pos;
                v.Rooms++;
                v.Steps = 0;
            }

            ApplyFull(bot, v, pos);
        }

        /// <summary>
        /// RoomPeek: the bot has just walked into a room it opened with its key - the visit it starts at the first thing it takes there searches the room to the
        /// end (<see cref="Visit.Full"/>), with no chance of leaving early.
        /// </summary>
        public static void MarkFullRoom(BotOwner bot, Vector3 center)
        {
            if (bot != null && Settings.RoomSweep.Value)
            {
                _full[bot.ProfileId] = (center, Time.time + 90f);
            }
        }

        private static void ApplyFull(BotOwner bot, Visit v, Vector3 pos)
        {
            if (!_full.TryGetValue(bot.ProfileId, out (Vector3 center, float until) m))
            {
                return;
            }

            if (Time.time > m.until)
            {
                _full.Remove(bot.ProfileId);
                return;
            }

            if (FlatDistance(m.center, pos) <= 25f && Mathf.Abs(m.center.y - pos.y) <= 4f)
            {
                v.Full = true;
                v.FullCenter = m.center;
                v.VisitUntil = Mathf.Max(v.VisitUntil, Time.time + 420f);
                _full.Remove(bot.ProfileId);
                Log.Detail("loot", () => $"{bot.Profile?.Nickname} searches the room it opened with its key to the end");
            }
        }

        /// <summary>For things that send a bot somewhere else on purpose (the body it has just killed): the room limit is lifted.</summary>
        public static void Release(BotOwner bot)
        {
            if (bot != null)
            {
                _visits.Remove(bot.ProfileId);
            }
        }

        public static bool Allows(BotOwner bot, Vector3 destination) => Allows(bot, destination, LootFinder.LootType.Container);

        /// <summary>LootingBots asks whether it may take a target at <paramref name="destination"/> (called for every candidate of a scan).</summary>
        public static bool Allows(BotOwner bot, Vector3 destination, LootFinder.LootType type)
        {
            if (bot == null || !Settings.RoomSweep.Value)
            {
                return true;
            }

            float now = Time.time;
            if (type != LootFinder.LootType.Corpse && _done.TryGetValue(bot.ProfileId, out List<(Vector3 at, float until)> done))
            {
                // A building it has just finished (or given up): not again for a while - only a new body draws it back
                done.RemoveAll(d => now > d.until);
                foreach ((Vector3 at, float until) d in done)
                {
                    if (FlatDistance(d.at, destination) <= BuildingRadius && Mathf.Abs(d.at.y - destination.y) <= BuildingHeight)
                    {
                        return false;
                    }
                }
            }

            if (!_visits.TryGetValue(bot.ProfileId, out Visit v))
            {
                return true;
            }

            Vector3 pos = bot.Position;
            if (now > v.VisitUntil || v.Mode == Mode.Done || FlatDistance(v.Anchor, pos) > BuildingRadius + 10f)
            {
                _visits.Remove(bot.ProfileId);
                return true;
            }

            // The room opened with a key: everything round its middle is part of the job
            if (v.Full && FlatDistance(v.FullCenter, destination) <= FullRadius && Mathf.Abs(v.FullCenter.y - destination.y) <= 3f)
            {
                return true;
            }

            // Walked off the room for another reason (a quest, a fight, a hit): the building is what counts now
            if (FlatDistance(v.RoomCenter, pos) > RoomRadius + 6f || Mathf.Abs(v.RoomCenter.y - pos.y) > 3f)
            {
                return FlatDistance(v.Anchor, destination) <= BuildingRadius && Mathf.Abs(v.Anchor.y - destination.y) <= BuildingHeight;
            }

            return InRoom(v.RoomCenter, destination);
        }

        public static void Tick()
        {
            if (_pending.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<string> due = null;
            foreach (KeyValuePair<string, float> kv in _pending)
            {
                if (Time.time >= kv.Value)
                {
                    (due ?? (due = new List<string>())).Add(kv.Key);
                }
            }

            if (due == null)
            {
                return;
            }

            foreach (string id in due)
            {
                _pending.Remove(id);
                Player p = Find(world, id);
                if (p != null)
                {
                    Diag.Guard("RoomSweep", () => Next(p.AIData?.BotOwner, p));
                }
            }
        }

        private static Player Find(GameWorld world, string id)
        {
            foreach (Player p in world.AllAlivePlayersList.ToArray())
            {
                if (p != null && p.ProfileId == id)
                {
                    return p;
                }
            }

            return null;
        }

        private static void Next(BotOwner bot, Player player)
        {
            if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || !_visits.TryGetValue(bot.ProfileId, out Visit v) || v.Mode == Mode.Done)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            LootingBrain brain = player.GetComponent<LootingBrain>();
            LootFinder finder = player.GetComponent<LootFinder>();
            if (p == null || p.Kind == BotKind.Other || brain == null)
            {
                return;
            }

            if (!brain.IsBrainEnabled)
            {
                // LootingBots has switched the bot off (far from the player): nothing handed over would ever be looted
                _visits.Remove(bot.ProfileId);
                return;
            }

            float now = Time.time;
            if (brain.LootTaskRunning || brain.HasActiveLootable || finder?.IsScanRunning == true)
            {
                // Still winding down (or it has its next thing already): look again in a moment
                if (now < v.VisitUntil && !brain.HasActiveLootable)
                {
                    _pending[bot.ProfileId] = now + 1f;
                }

                return;
            }

            if (LootingBots.External.CheckIfInventoryFull(bot))
            {
                Leave(bot, player, p, v, "its bag is full");
                return;
            }

            if (now > v.VisitUntil || v.VisitSteps >= (v.Full || p.Packrat ? MaxFullVisitSteps : MaxVisitSteps))
            {
                Leave(bot, player, p, v, "it has had enough of this building");
                return;
            }

            if (FlatDistance(v.Anchor, bot.Position) > BuildingRadius + 15f)
            {
                // Taken away for another reason (a fight, its squad, a quest): the visit is over, no pause
                _visits.Remove(bot.ProfileId);
                return;
            }

            // Danger or a fight: the room waits - it is taken up again when it is quiet (the visit has its own time limit). So does a squadmate's
            // handover: the receiver stands for it instead of walking to the next drawer (raid 2026-10-30 23:28: RedBeard went on to a body 10 m off)
            if (!TacticalTasks.NoThreat(bot) || LootDanger.Why(bot) != null || Patches.LootKick.WontLoot(bot, brain) != null || SquadShare.InScene(bot))
            {
                _pending[bot.ProfileId] = now + 3f;
                return;
            }

            // The next thing of this room
            ResetWhy();
            if (v.Steps < (v.Full ? MaxFullRoomSteps : MaxRoomSteps))
            {
                List<Target> room = Candidates(bot, brain, v, v.RoomCenter, RoomRadius, 2.2f, at => InRoom(v.RoomCenter, at));
                if (v.Full && room.Count == 0)
                {
                    // The room opened with a key: its other corner, seen from the middle of the room
                    room = Candidates(bot, brain, v, v.FullCenter, RoomRadius, 2.2f, at => InRoom(v.FullCenter, at));
                }

                if (Hand(bot, brain, v, room, RoomPath, out Target t))
                {
                    if (v.Steps == 0)
                    {
                        Rooms++;
                        Log.Info($"LR Loot: {p.Name} ({p.Style}) goes through the rest of the room - {room.Count} more place(s) to search");
                    }

                    v.Steps++;
                    Log.Detail("loot", () => $"{p.Name} goes on to {What(t)} in the same room ({t.Path:0} m)");
                    return;
                }
            }

            RoomFinished(bot, player, p, brain, v);
        }

        /// <summary>Everything in this room is done: on to the next room of the building, or out - the likelier out the more rooms the bot has done.</summary>
        private static void RoomFinished(BotOwner bot, Player player, BotProfile p, LootingBrain brain, Visit v)
        {
            if (v.Full)
            {
                // The room opened with a key is searched to the end: whatever is left round its middle (a big room, a second room behind it), no chance of leaving
                List<Target> all = Candidates(bot, brain, v, v.FullCenter, FullRadius, 3f, at => true);
                if (!Hand(bot, brain, v, all, FullPath, out Target next))
                {
                    Emptied++;
                    Leave(bot, player, p, v, $"the room it opened with its key is searched to the end ({v.VisitSteps} thing(s); {Why()})");
                    return;
                }

                if (!InRoom(v.RoomCenter, next.Pos))
                {
                    v.Rooms++;
                    v.RoomCenter = next.Pos;
                    v.Steps = 1;
                    NextRooms++;
                }

                Log.Detail("loot", () => $"{p.Name} goes on in the room it opened with its key - {What(next)} {next.Path:0} m away");
                return;
            }

            // The packrat is a vacuum cleaner: on to the next room almost every time, however many rooms it has done
            float chance = p.Packrat ? 0.97f : GoOnChance(v.Rooms, p.LootGreed);
            if (_rng.NextDouble() >= chance)
            {
                Leave(bot, player, p, v, $"{v.Rooms} room(s) are enough ({chance:P0} to go on)");
                return;
            }

            Vector3 room = v.RoomCenter;
            List<Target> rest = Candidates(bot, brain, v, v.Anchor, BuildingRadius, BuildingHeight, at => !InRoom(room, at));

            // A room a squadmate is going through is left to him: the squad spreads over the building instead of running to the same drawers
            List<Target> free = rest.Where(t => !MateRoom(bot, t.Pos)).ToList();
            if (free.Count > 0 && free.Count < rest.Count)
            {
                SplitRooms++;
                rest = free;
            }
            if (!Hand(bot, brain, v, rest, BuildingPath, out Target t))
            {
                Emptied++;
                Leave(bot, player, p, v, $"nothing left to search in the building after {v.Rooms} room(s) ({Why()})");
                return;
            }

            v.Rooms++;
            v.RoomCenter = t.Pos;
            v.Steps = 1;
            NextRooms++;
            Log.Info($"LR Loot: {p.Name} ({p.Style}) has gone through room {v.Rooms - 1} and goes on to the next one ({chance:P0}) - {What(t)} {t.Path:0} m away");
        }

        private static readonly float[] GoOn = { 1f, 0.95f, 0.85f, 0.65f, 0.55f, 0.45f, 0.35f };

        /// <summary>
        /// The chance to go on to the next room after <paramref name="roomsDone"/> rooms of the building: 100 / 95 / 85 / 65 / 55 / 45 / 35 %, then 25 % (2026-11-02, the owner's table;
        /// before it 90 / 77 / 65 / 55 / 47). A greedy bot (1.0 and over) keeps the whole figure, a lazier one loses up to a fifth of it (greed 0.4: x0.88).
        /// </summary>
        public static float GoOnChance(int roomsDone, float greed)
        {
            float basic = roomsDone >= 1 && roomsDone <= GoOn.Length ? GoOn[roomsDone - 1] : 0.25f;
            return Mathf.Clamp(basic * (0.8f + 0.2f * Mathf.Min(1f, Mathf.Clamp(greed, 0.4f, 1.6f))), 0.05f, 1f);
        }

        /// <summary>A squadmate is going through the room this point is in right now.</summary>
        private static bool MateRoom(BotOwner bot, Vector3 at)
        {
            foreach (string id in LootBrain.SquadIds(bot))
            {
                if (id != bot.ProfileId && _visits.TryGetValue(id, out Visit mate) && mate.Mode == Mode.Room && Mathf.Abs(mate.RoomCenter.y - at.y) < 2.2f
                    && FlatDistance(mate.RoomCenter, at) < RoomRadius)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Leave(BotOwner bot, Player player, BotProfile p, Visit v, string why)
        {
            v.Mode = Mode.Done;
            Leaves++;
            _visits.Remove(bot.ProfileId);
            if (!_done.TryGetValue(bot.ProfileId, out List<(Vector3 at, float until)> done))
            {
                _done[bot.ProfileId] = done = new List<(Vector3, float)>();
            }

            done.Add((v.Anchor, Time.time + DoneMemory));

            // Without a pause LootingBots would pick the next thing at once, and the bot would never leave
            player.GetComponent<LootFinder>()?.OverrideNextScanTime(20f + (float)_rng.NextDouble() * 25f);
            Log.Info($"LR Loot: {p.Name} ({p.Style}) leaves the building after {v.Rooms} room(s), {v.VisitSteps} thing(s) handed over - {why}");
        }

        /// <summary>
        /// Anything LootingBots would take within <paramref name="radius"/> (straight distance, 4 m up or down; a building just finished excluded) -
        /// worth stopping for (Harmonizer: QuestingBots' loot break).
        /// </summary>
        public static bool HasLootNear(BotOwner bot, float radius)
        {
            Player player = bot?.GetPlayer;
            LootingBrain brain = player != null ? player.GetComponent<LootingBrain>() : null;
            if (brain == null || !brain.HasFreeSpace)
            {
                return brain != null && brain.HasActiveLootable;
            }

            return Candidates(bot, brain, new Visit(), bot.Position, radius, 4f, at => Allows(bot, at), true).Count > 0;
        }

        /// <summary>
        /// QuestingBots' loot break (Harmonizer): the nearest thing worth taking within <paramref name="radius"/> - on foot, up to 1.4 x the radius - is handed
        /// to LootingBots straight away, instead of waiting for one of its three scan slots and its nearest-in-a-straight-line pick.
        /// </summary>
        public static bool HandNearest(BotOwner bot, float radius, out string what)
        {
            what = null;
            Player player = bot?.GetPlayer;
            LootingBrain brain = player != null ? player.GetComponent<LootingBrain>() : null;
            if (brain == null || !brain.IsBrainEnabled || !brain.HasFreeSpace || brain.IsBotLooting)
            {
                return false;
            }

            var probe = new Visit();
            List<Target> list = Candidates(bot, brain, probe, bot.Position, radius, 4f, at => Allows(bot, at));
            if (!Hand(bot, brain, probe, list, radius * 1.4f, out Target t))
            {
                return false;
            }

            what = $"{What(t)} {t.Path:0} m away";
            return true;
        }

        /// <summary>
        /// Hands one particular body to LootingBots as the bot's loot target (a body job, its own kill, a rich body for a full bag). Raid 2026-10-26 15:48: the
        /// body jobs went through a scan QuestingBots runs for its loot break, and the break handed over the NEAREST thing - "goes for DjLang's body (31 m)",
        /// a second later "goes for a thing on the floor 18 m away", and the body was never searched. True when the bot now walks to the body.
        /// </summary>
        public static bool HandBody(BotOwner bot, string victimId, Vector3 near, float maxPath, out float path)
        {
            path = 0f;
            Player player = bot?.GetPlayer;
            LootingBrain brain = player != null ? player.GetComponent<LootingBrain>() : null;
            if (brain == null || string.IsNullOrEmpty(victimId) || brain.LootTaskRunning || brain.HasActiveLootable || !brain.LootingEnabled)
            {
                return false;
            }

            if (!brain.IsBrainEnabled)
            {
                // Switched off for performance (far from the player): a body it means to search turns it back on
                brain.ForceBrainEnabled = true;
                if (!brain.IsBrainEnabled)
                {
                    return false;
                }
            }

            int hits = Physics.OverlapSphereNonAlloc(near, 4f, _colliders, LootingBots.Utilities.LootUtils.LootMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider c = _colliders[i];
                if (c == null || !(c.gameObject.GetComponentInParent<InteractableObject>() is Corpse corpse) || corpse.PlayerProfileID != victimId
                    || c.gameObject.GetComponentInParent<Player>() == null || !(corpse.ItemOwner?.RootItem is InventoryEquipment equipment))
                {
                    continue;
                }

                if (ActiveLootCache.IsLootInUse(equipment.Id, bot))
                {
                    // Someone else is going through it right now
                    return false;
                }

                Bounds bounds = c.bounds;
                Vector3 low = bounds.center;
                low.y -= bounds.extents.y + 0.4f;
                Vector3 dest = Destination(low);
                if (dest == Vector3.zero)
                {
                    return false;
                }

                var target = new Target { Obj = corpse, Type = LootFinder.LootType.Corpse, Id = equipment.Id, Pos = corpse.transform.position, Dest = dest };
                if (!Hand(bot, brain, new Visit(), new List<Target> { target }, maxPath, out Target chosen))
                {
                    return false;
                }

                path = chosen.Path;
                return true;
            }

            return false;
        }

        /// <summary>Hands the nearest (on foot) of <paramref name="list"/> to LootingBots as its loot target, as if its own scan had found it.</summary>
        private static bool Hand(BotOwner bot, LootingBrain brain, Visit v, List<Target> list, float maxPath, out Target chosen)
        {
            chosen = default;
            if (list.Count == 0 || !NavMesh.SamplePosition(bot.Position, out NavMeshHit from, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }

            Vector3 pos = bot.Position;
            list.Sort((a, b) => (a.Dest - pos).sqrMagnitude.CompareTo((b.Dest - pos).sqrMagnitude));
            var ways = new List<Target>();
            for (int i = 0; i < list.Count && i < 8; i++)
            {
                Target t = list[i];
                if (!NavMesh.CalculatePath(from.position, t.Dest, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
                {
                    v.Tried.Add(t.Id);
                    _noPath++;
                    continue;
                }

                float len = 0f;
                Vector3[] corners = _path.corners;
                for (int c = 1; c < corners.Length; c++)
                {
                    len += Vector3.Distance(corners[c - 1], corners[c]);
                }

                if (len > maxPath)
                {
                    _far++;
                    continue;
                }

                t.Path = len;
                ways.Add(t);
            }

            foreach (Target t in ways.OrderBy(w => w.Path))
            {
                v.Tried.Add(t.Id);
                if (!ActiveLootCache.CacheActiveLootId(t.Id, bot))
                {
                    _taken++;
                    continue;
                }

                brain.SetLoot(t.Obj, t.Type, t.Pos, t.Dest, t.Id, Mathf.Max(0.1f, t.Path));
                if (brain.ActiveLootId != t.Id)
                {
                    // Refused (LiveRaid's loot brain does not want this body now)
                    ActiveLootCache.Cleanup(t.Id, bot);
                    _refused++;
                    continue;
                }

                v.VisitSteps++;
                Steps++;
                chosen = t;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Unsearched things round <paramref name="center"/> that LootingBots would take: unlocked containers, bodies, and loose things worth picking up
        /// (the same rules as its own scan). A sphere over the loot layers (what LootingBots itself scans) - searching the whole scene for every
        /// container and body (FindObjectsOfType) every ten seconds cost up to 174 ms in one frame.
        /// </summary>
        private static List<Target> Candidates(BotOwner bot, LootingBrain brain, Visit v, Vector3 center, float radius, float height, System.Func<Vector3, bool> where,
            bool firstOnly = false)
        {
            var list = new List<Target>();
            int hits = Physics.OverlapSphereNonAlloc(center, radius, _colliders, LootingBots.Utilities.LootUtils.LootMask, QueryTriggerInteraction.Ignore);
            var seen = new HashSet<string>();
            int spaces = brain.Stats?.AvailableGridSpaces ?? 0;
            for (int i = 0; i < hits; i++)
            {
                Collider c = _colliders[i];
                if (c == null)
                {
                    continue;
                }

                InteractableObject obj = c.gameObject.GetComponentInParent<InteractableObject>();
                Item root = null;
                LootFinder.LootType type = LootFinder.LootType.None;
                if (obj is Corpse corpse)
                {
                    if (c.gameObject.GetComponentInParent<Player>() != null && corpse.ItemOwner?.RootItem is InventoryEquipment equipment)
                    {
                        root = equipment;
                        type = LootFinder.LootType.Corpse;
                    }
                }
                else if (obj is LootableContainer container)
                {
                    if (container.isActiveAndEnabled && container.DoorState != EDoorState.Locked)
                    {
                        root = container.ItemOwner?.RootItem;
                        type = LootFinder.LootType.Container;
                    }
                }
                else if (obj is LootItem item)
                {
                    root = item.ItemOwner?.RootItem;
                    if (root != null && !root.QuestItem && (root is SearchableItem || brain.InventoryController.IsBetterArmorThanEquipped(root)
                                                            || (brain.IsValuableEnough(root) && spaces > root.GetItemSize())))
                    {
                        type = LootFinder.LootType.Item;
                    }
                }

                if (type == LootFinder.LootType.None || root == null)
                {
                    continue;
                }

                string id = root.Id;
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    continue;
                }

                _seen++;
                if (v.Tried.Contains(id) || brain.IsLootIgnored(id) || LootAudit.Cooling(bot, id))
                {
                    _ignored++;
                    continue;
                }

                if (ActiveLootCache.IsLootInUse(id, bot))
                {
                    _busy++;
                    continue;
                }

                Vector3 at = obj.transform.position;
                if (Mathf.Abs(at.y - center.y) > height || FlatDistance(center, at) > radius)
                {
                    _floor++;
                    continue;
                }

                if (!where(at))
                {
                    _where++;
                    continue;
                }

                if (type != LootFinder.LootType.Corpse && !SquadLoot.InReach(bot, at))
                {
                    _share++;
                    continue;
                }

                Bounds bounds = c.bounds;
                Vector3 low = bounds.center;
                low.y -= bounds.extents.y + 0.4f;
                Vector3 dest = Destination(low);
                if (dest == Vector3.zero)
                {
                    _noStand++;
                    continue;
                }

                list.Add(new Target { Obj = obj, Type = type, Id = id, Pos = at, Dest = dest });
                if (firstOnly)
                {
                    break;
                }
            }

            return list;
        }

        /// <summary>LootingBots' own stand-point for a thing: on the navmesh next to it, pushed 1.5 m off so the bot does not press into it.</summary>
        private static Vector3 Destination(Vector3 center)
        {
            if (!NavMesh.SamplePosition(center, out NavMeshHit near, 1f, NavMesh.AllAreas))
            {
                return Vector3.zero;
            }

            Vector3 padding = center - near.position;
            padding.y = 0f;
            padding.Normalize();
            Vector3 dest = NavMesh.SamplePosition(center - padding * 1.5f, out NavMeshHit pushed, 1f, near.mask) ? pushed.position : near.position;

            // Not from the next room through the wall (the path from the bot is checked when the thing is handed over)
            return LootReach.Check(null, center, dest);
        }

        private static string What(Target t) => t.Type == LootFinder.LootType.Corpse ? "a body" : t.Type == LootFinder.LootType.Container ? "a container" : "a thing on the floor";

        /// <summary>The same room as <paramref name="center"/>: near, the same floor, no wall between (or a way on foot barely longer than the line).</summary>
        private static bool InRoom(Vector3 center, Vector3 point)
        {
            Vector3 d = point - center;
            if (Mathf.Abs(d.y) > 2.2f)
            {
                return false;
            }

            d.y = 0f;
            float flat = d.magnitude;
            if (flat > RoomRadius)
            {
                return false;
            }

            if (flat < 3f)
            {
                return true;
            }

            Vector3 from = center + Vector3.up * 1.3f;
            Vector3 to = point + Vector3.up * 0.7f;
            if (!Physics.Linecast(from, to, out RaycastHit hit, LayersMaskController.HighPolyWithTerrainMask) || (hit.point - to).sqrMagnitude < 1.6f * 1.6f)
            {
                return true;
            }

            // Furniture in the way is not a wall: walking there is about as far as the line
            NavMeshPath path = new NavMeshPath();
            if (NavMesh.SamplePosition(center, out NavMeshHit a, 2f, NavMesh.AllAreas) && NavMesh.SamplePosition(point, out NavMeshHit b, 2f, NavMesh.AllAreas)
                && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
            {
                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++)
                {
                    len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                }

                return len <= flat * 1.35f + 3f;
            }

            return false;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
