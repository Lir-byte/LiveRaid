using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A door a bot has just unlocked with its key is a room it searches in full (F12 -> 2 "Look into a room you unlocked"): always, to the end.
    ///
    /// A player's tester (2026-11-02): bots open a door with a key and walk straight off without a glance inside. Traces of 31.10-02.11: almost every
    /// unlock was QuestingBots' own (a locked door near the bot's quest step - "Dorms 3 Story"), after which the bot went on to its step; the room
    /// behind was never entered unless loot happened to be scanned there. A player who spent a key goes in.
    /// Here, when QuestingBots reports a door unlocked (BotObjectiveManager.DoorIsUnlocked), the bot - always - opens the door if it is still shut,
    /// walks 3-6 m into the room (the room point of the locked-room survey, or a point on the far side of the door), looks round and hands the nearest
    /// thing of the room to LootingBots; the room sweep (RoomSweep.MarkFullRoom) then goes through ALL of the room: every container, body and thing worth
    /// taking round its middle, with no chance of leaving early (a key was spent for it).
    /// A fight or danger holds it up for 45 s; QuestingBots takes the bot back afterwards.
    /// </summary>
    public static class RoomPeek
    {
        private class Pending
        {
            public BotOwner Bot;
            public WorldInteractiveObject Door;
            public float NotBefore;
            public float GiveUpAt;
            public bool Opened;
        }

        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private static readonly HashSet<string> _done = new HashSet<string>();
        private static readonly System.Random _rng = new System.Random();
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static float _next;

        public static int Unlocked { get; private set; }
        public static int Went { get; private set; }
        public static int Arrived { get; private set; }
        public static int Looted { get; private set; }
        public static int NoWay { get; private set; }

        public static string Summary => $"rooms behind unlocked doors: unlocked {Unlocked}, went in {Went} (got inside {Arrived}, loot handed over there {Looted}), " +
                                        $"no way in {NoWay}";

        public static bool On => Settings.Enabled.Value && Settings.RoomPeek.Value;

        public static void Clear()
        {
            _pending.Clear();
            _done.Clear();
            _next = 0f;
            Unlocked = Went = Arrived = Looted = NoWay = 0;
        }

        /// <summary>QuestingBots has just unlocked <paramref name="door"/> for <paramref name="bot"/> (called before it forgets which door it was).</summary>
        public static void OnUnlocked(BotOwner bot, WorldInteractiveObject door)
        {
            if (!On || bot == null || door == null || bot.IsDead)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            Unlocked++;
            string key = bot.ProfileId + "|" + door.Id;
            if (!_done.Add(key))
            {
                return;
            }

            float now = Time.time;
            // A moment for the key animation to end and QuestingBots to let go of the door
            _pending[bot.ProfileId] = new Pending { Bot = bot, Door = door, NotBefore = now + 1.2f, GiveUpAt = now + 45f };
        }

        public static void Tick()
        {
            if (_pending.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.4f;
            float now = Time.time;
            foreach (string id in new List<string>(_pending.Keys))
            {
                Pending pd = _pending[id];
                BotOwner bot = pd.Bot;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot) || now > pd.GiveUpAt)
                {
                    _pending.Remove(id);
                    continue;
                }

                if (now < pd.NotBefore)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || !TacticalTasks.NoThreat(bot) || LootDanger.Why(bot) != null)
                {
                    // Not now; the give-up time runs on
                    continue;
                }

                WorldInteractiveObject door = pd.Door;
                if (door.DoorState == EDoorState.Locked)
                {
                    continue;
                }

                if (door.DoorState == EDoorState.Shut && door is Door d && !pd.Opened)
                {
                    pd.Opened = true;
                    if ((d.transform.position - bot.Position).sqrMagnitude < 2.8f * 2.8f && DoorAssistLayer.OpenNow(bot, d))
                    {
                        pd.NotBefore = now + 1.2f;
                        continue;
                    }
                }

                _pending.Remove(id);
                Vector3? inside = InsidePoint(bot, door);
                if (inside == null)
                {
                    NoWay++;
                    Log.Info($"LR Key: {p.Name} unlocked {door.Id} but finds no way into the room behind it");
                    continue;
                }

                GoIn(bot, p, door, inside.Value);
            }
        }

        private static void GoIn(BotOwner bot, BotProfile p, WorldInteractiveObject door, Vector3 inside)
        {
            Vector3 doorPos = door.transform.position;
            Vector3 deeper = inside + Flat(inside - doorPos).normalized * 3f;
            string name = p.Name;
            var task = new TacticalTask
            {
                Kind = TaskKind.Investigate,
                StandAt = inside,
                LookAt = deeper + Vector3.up * 1.2f,
                Until = Time.time + 18f,
                Speed = 1f,
                MovePose = 1f,
                OnFinished = b =>
                {
                    if ((b.Position - inside).sqrMagnitude < 2.5f * 2.5f)
                    {
                        Arrived++;
                    }

                    // The whole room is the job: the visit that starts at the first thing searches it to the end (RoomSweep)
                    // Raid 2026-11-02 14:11: both rooms were "nothing in it worth taking" - LootingBots switches its brain off for bots far from the camera, a bag
                    // may be full; HandNearest said "no" without a word. Now the brain is switched on for this job and the reason is told.
                    LootingBots.Components.LootingBrain brain = b.GetPlayer?.GetComponent<LootingBots.Components.LootingBrain>();
                    string why = null;
                    if (brain == null)
                    {
                        why = "LootingBots is not on this bot";
                    }
                    else
                    {
                        if (!brain.IsBrainEnabled)
                        {
                            brain.ForceBrainEnabled = true;
                        }

                        why = !brain.IsBrainEnabled ? "LootingBots keeps this bot switched off" : !brain.HasFreeSpace ? "its bag is full" : brain.IsBotLooting ? "it is busy looting" : null;
                    }

                    RoomSweep.MarkFullRoom(b, inside);
                    if (why == null && RoomSweep.HandNearest(b, 16f, out string what))
                    {
                        Looted++;
                        Log.Info($"LR Key: {name} looks round the room behind {door.Id} and goes for {what}");
                    }
                    else
                    {
                        Log.Info($"LR Key: {name} looks round the room behind {door.Id} - {why ?? "nothing in it worth taking"}");
                        if (why == null && Settings.RoomMemory.Value)
                        {
                            // Nothing in it: the squad does not walk in to see (RoomSweep keeps it 10 minutes)
                            RoomSweep.RememberForSquad(b, inside, Time.time + 600f);
                        }
                    }
                },
            };

            if (TacticalTasks.Set(bot, task))
            {
                Went++;
                Log.Info($"LR Key: {p.Name} ({p.Style}) goes into the room behind {door.Id} it has unlocked ({Vector3.Distance(bot.Position, inside):0} m)");
            }
        }

        /// <summary>
        /// A point 3-6 m into the room: the room point of the locked-room survey (MapIntel) for this door, else the reachable navmesh point on the side
        /// of the door away from the bot.
        /// </summary>
        private static Vector3? InsidePoint(BotOwner bot, WorldInteractiveObject door)
        {
            Vector3 from = bot.Position;
            if (MapIntel.Current != null)
            {
                foreach (LockedSpot spot in MapIntel.Current.Spots)
                {
                    if (spot.Id == door.Id && spot.InteriorPos.HasValue && Reachable(from, spot.InteriorPos.Value))
                    {
                        return spot.InteriorPos.Value;
                    }
                }
            }

            Vector3 doorPos = door.transform.position;
            Vector3 away = Flat(doorPos - from);
            Transform t = door.transform;
            Vector3[] dirs = { Flat(t.forward), -Flat(t.forward), Flat(t.right), -Flat(t.right) };
            Vector3? best = null;
            float bestScore = float.MinValue;
            foreach (Vector3 dir in dirs)
            {
                if (dir.sqrMagnitude < 0.01f || Vector3.Dot(dir, away) <= 0.2f)
                {
                    continue;
                }

                foreach (float reach in new[] { 4f, 3f, 5.5f })
                {
                    Vector3 probe = doorPos + dir.normalized * reach;
                    if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - from.y) > 2f
                        || Vector3.Dot(Flat(hit.position - doorPos), away) <= 0.5f || !Reachable(from, hit.position))
                    {
                        continue;
                    }

                    float score = Vector3.Dot(Flat(hit.position - doorPos), away);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = hit.position;
                    }

                    break;
                }
            }

            return best;
        }

        private static bool Reachable(Vector3 from, Vector3 to)
        {
            if (!NavMesh.SamplePosition(from, out NavMeshHit a, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }

            return NavMesh.CalculatePath(a.position, to, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }
}
