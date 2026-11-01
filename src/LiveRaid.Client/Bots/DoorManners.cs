using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using LiveRaid.Client.Tactics;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Doors as players use them (F12 -> 2 "Door manners"). Bots opened doors and left them open; nobody stopped at a closed one.
    ///  - shutting it behind: a PMC or player scav that went in through a door and is now 2.5-4.5 m past it, inside, moving away, with no squadmate
    ///    left behind near it, shuts it - so whoever comes after hears it opening (rats and drilled squads mostly, rushers hardly);
    ///  - listening at it: a calm one walking up to a closed door stops 1-2 s, facing it, before it goes through (the cautious ones, once per door).
    /// The door's own sounds then tell everyone near (NoiseResponse).
    /// </summary>
    public static class DoorManners
    {
        private class Passing
        {
            public Door Door;
            public Vector3 Near;
            public float At;
        }

        private static readonly Dictionary<string, Passing> _passing = new Dictionary<string, Passing>();
        private static readonly HashSet<string> _done = new HashSet<string>();
        private static readonly Dictionary<string, Vector3> _lastPos = new Dictionary<string, Vector3>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Shut { get; private set; }
        public static int Listened { get; private set; }

        public static string Summary => $"door manners: doors shut behind {Shut}, stops to listen at a closed door {Listened}";

        public static bool On => Settings.Enabled.Value && Settings.DoorManners.Value;

        public static void Clear()
        {
            _passing.Clear();
            _done.Clear();
            _lastPos.Clear();
            _next = 0f;
            Shut = Listened = 0;
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            List<Door> doors = DoorAssistLayer.Doors;
            if (world == null || doors.Count == 0)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || !TacticalTasks.BotIsCalm(bot))
                {
                    _lastPos.Remove(bot.ProfileId);
                    continue;
                }

                Vector3 pos = bot.Position;
                Vector3 last = _lastPos.TryGetValue(bot.ProfileId, out Vector3 l) ? l : pos;
                _lastPos[bot.ProfileId] = pos;
                Vector3 moved = pos - last;
                moved.y = 0f;

                Door near = null;
                float best = 2.5f * 2.5f;
                foreach (Door d in doors)
                {
                    if (d == null)
                    {
                        continue;
                    }

                    float dd = (d.transform.position - pos).sqrMagnitude;
                    if (dd < best)
                    {
                        best = dd;
                        near = d;
                    }
                }

                if (near != null)
                {
                    if (near.DoorState == EDoorState.Shut && moved.sqrMagnitude > 0.25f
                        && Vector3.Dot(moved.normalized, (near.transform.position - pos).normalized) > 0.5f)
                    {
                        ListenAt(bot, p, near);
                    }
                    else if (near.DoorState == EDoorState.Open)
                    {
                        _passing[bot.ProfileId] = new Passing { Door = near, Near = pos, At = Time.time };
                    }

                    continue;
                }

                if (_passing.TryGetValue(bot.ProfileId, out Passing pass))
                {
                    ShutBehind(bot, p, pass, moved, world);
                }
            }
        }

        private static float Care(BotOwner bot, BotProfile p)
        {
            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            float care = d != null ? 0.5f * d[DoctrineDeck.Precision] + 0.5f * (1f - d[DoctrineDeck.Aggression]) : 0.4f;
            if (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper)
            {
                care += 0.25f;
            }
            else if (p.Style == PlayStyle.Rusher)
            {
                care -= 0.25f;
            }

            return Mathf.Clamp(care, 0.05f, 0.9f);
        }

        private static void ListenAt(BotOwner bot, BotProfile p, Door door)
        {
            string key = bot.ProfileId + "|" + door.Id;
            if (_done.Contains(key) || TacticalTasks.TryGet(bot, out _))
            {
                return;
            }

            _done.Add(key);
            if (_rng.NextDouble() > Care(bot, p) * 0.6f)
            {
                return;
            }

            float seconds = 1f + (float)_rng.NextDouble();
            if (TacticalTasks.Set(bot, new TacticalTask { Kind = TaskKind.Listen, LookAt = door.transform.position, Until = Time.time + seconds }))
            {
                Listened++;
                Log.Detail("task", () => $"{p.Name} stops {seconds:0.0} s at a closed door to listen");
            }
        }

        private static void ShutBehind(BotOwner bot, BotProfile p, Passing pass, Vector3 moved, GameWorld world)
        {
            Door door = pass.Door;
            if (door == null || Time.time - pass.At > 6f)
            {
                _passing.Remove(bot.ProfileId);
                return;
            }

            Vector3 doorPos = door.transform.position;
            float dist = Vector3.Distance(bot.Position, doorPos);
            if (dist < 2.5f || dist > 4.5f)
            {
                return;
            }

            _passing.Remove(bot.ProfileId);
            string key = bot.ProfileId + "|shut|" + door.Id;
            if (_done.Contains(key) || door.DoorState != EDoorState.Open || !SquadPlaybook.Indoors(bot.Position)
                || Vector3.Dot(moved, bot.Position - doorPos) <= 0f)
            {
                return;
            }

            _done.Add(key);

            // Nobody of its own still to come through, nobody else right at the door
            if (world.AllAlivePlayersList.Any(x => x != null && x.ProfileId != bot.ProfileId && (x.Position - doorPos).sqrMagnitude < 8f * 8f
                                                   && SquadTies.AreMates(bot.ProfileId, x.ProfileId)))
            {
                return;
            }

            if (_rng.NextDouble() > Care(bot, p) * 0.7f)
            {
                return;
            }

            Player player = bot.GetPlayer;
            player.MovementContext.ResetCanUsePropState();
            var result = Door.Interact(player, EInteractionType.Close);
            if (!result.Succeeded)
            {
                return;
            }

            player.ExecuteInteraction(door, result.Value);
            Shut++;
            Log.Detail("task", () => $"{p.Name} shuts the door behind it ({dist:0.0} m)");
        }
    }
}
