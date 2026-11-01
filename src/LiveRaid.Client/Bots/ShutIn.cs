using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A bot shut in somewhere it cannot path out of (F12 -> 2 "Free bots shut in a room").
    /// Raid 2026-10-22: two Scavs stood ten minutes in one small room at (30, 4, -55) - every quest step failed within 0-6 s ("gave up a step of Storage,
    /// 285 m from it", 9 times), every loot 4-37 m away was "cannot get to", nothing moved them: a door had shut behind them (one of them spawned in there).
    /// Here every path that fails at once - a quest step given up within 15 s, loot given up as unreachable - counts against the place the bot stands;
    /// three within 2.5 minutes without the bot leaving 8 m of that spot and it is shut in:
    /// before anything is done the navmesh must agree: no full path leads from the bot to any other bot 40+ m away (a step into a locked room or loot
    /// behind a fence alone is no trap). Then:
    ///  - a shut door within 15 m (same storey) is opened from where the bot stands (the nearest first; one per try);
    ///  - with none, and while nobody watches the bot or the spot, it is put back on the last point of its own way (or, spawned in there, a point
    ///    around) from which the open map can be reached - what a player would have walked through.
    /// </summary>
    public static class ShutIn
    {
        private const int Strikes = 3;
        private const float Window = 150f;
        private const float Spot = 8f;

        private class State
        {
            public Vector3 Anchor;
            public readonly List<float> Fails = new List<float>();
            public readonly List<Vector3> Crumbs = new List<Vector3>();
            public float NextCrumb;
            public float CalmUntil;

            // After a door was opened: look again whether the way out is free now (raid 2026-10-26 20:28: Филипп Академик, the door opened was not his
            // way out; no quest step was given to him any more, so no new failure ever called this again - he stood 5 minutes)
            public float RecheckAt = -1f;
            public int Rechecks;
            public string What;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _tick;

        public static int DoorsOpened { get; private set; }
        public static int PutOut { get; private set; }
        public static int Unsolved { get; private set; }

        public static string Summary => $"shut-in bots freed: doors opened {DoorsOpened}, put back outside {PutOut}, could not help {Unsolved}";

        public static void Clear()
        {
            _states.Clear();
            _tick = 0f;
            DoorsOpened = PutOut = Unsolved = 0;
        }

        private static State Of(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Anchor = bot.Position };
            }

            return s;
        }

        /// <summary>A path of the bot failed at once (a quest step, a loot it could not reach).</summary>
        public static void PathFailed(BotOwner bot, string what)
        {
            if (!Settings.FreeShutIn.Value || bot == null || bot.IsDead || bot.BotState != EBotState.Active || BotProfiles.Get(bot)?.Kind == BotKind.Other)
            {
                return;
            }

            float now = Time.time;
            State s = Of(bot);
            if ((bot.Position - s.Anchor).sqrMagnitude > Spot * Spot)
            {
                s.Anchor = bot.Position;
                s.Fails.Clear();
            }

            s.Fails.RemoveAll(t => now - t > Window);
            s.Fails.Add(now);
            if (s.Fails.Count < Strikes || now < s.CalmUntil)
            {
                return;
            }

            s.Fails.Clear();
            s.CalmUntil = now + 45f;
            Free(bot, s, what);
        }

        private static void Free(BotOwner bot, State s, string what)
        {
            Vector3 pos = bot.Position;
            string name = bot.Profile?.Nickname;
            List<Vector3> open = OpenMap(bot);
            if (open.Count == 0 || open.Any(o => Reaches(pos, o)))
            {
                // Paths fail, but the open map is reachable (a step into a locked room, loot behind a fence): not shut in
                Log.Detail("task", $"{name}: ways fail at ({pos.x:0}, {pos.y:0}, {pos.z:0}) ({what}), but it is not shut in");
                return;
            }

            string where = $"({pos.x:0}, {pos.y:0}, {pos.z:0})";
            Door door = DoorAssistLayer.DoorsNear(pos, 15f).FirstOrDefault(d => d.DoorState == EDoorState.Shut);
            if (door != null && s.Rechecks < 4 && DoorAssistLayer.OpenNow(bot, door))
            {
                DoorsOpened++;
                s.RecheckAt = Time.time + 8f;
                s.Rechecks++;
                s.What = what;
                Log.Info($"LR Stuck: {name} is shut in at {where} - every way fails ({what}) -> opens door {door.Id} {DoorAssistLayer.Where(door)}");
                return;
            }

            s.RecheckAt = -1f;
            s.Rechecks = 0;

            if (!ViewerSight.CanSee(pos + Vector3.up * 1.2f))
            {
                // The way it came in, newest first; for a bot that spawned in there, the navmesh around
                IEnumerable<Vector3> candidates = s.Crumbs.AsEnumerable().Reverse().Concat(Around(pos));
                foreach (Vector3 c in candidates)
                {
                    if ((c - pos).sqrMagnitude < 12f * 12f || ViewerSight.CanSee(c + Vector3.up * 1.2f) || !open.Any(o => Reaches(c, o)))
                    {
                        continue;
                    }

                    RouteTraverseLayer.PlaceOnNavmesh(bot, c);
                    s.Anchor = c;
                    s.Crumbs.Clear();
                    PutOut++;
                    Log.Info($"LR Stuck: {name} is shut in at {where} - every way fails ({what}), no door to open -> put back outside at ({c.x:0}, {c.y:0}, {c.z:0}), nobody watching");
                    return;
                }
            }

            Unsolved++;
            Log.Info($"LR Stuck: {name} is shut in at {where} - every way fails ({what}), no shut door near and no way out found");
        }

        /// <summary>Where the other bots are, 40+ m away: the open map (up to 4 of them).</summary>
        private static List<Vector3> OpenMap(BotOwner bot)
        {
            var list = new List<Vector3>();
            foreach (Player p in Singleton<GameWorld>.Instance?.AllAlivePlayersList ?? new List<Player>())
            {
                if (p == null || p.ProfileId == bot.ProfileId || p.AIData?.BotOwner == null || (p.Position - bot.Position).sqrMagnitude < 40f * 40f)
                {
                    continue;
                }

                list.Add(p.Position);
                if (list.Count >= 4)
                {
                    break;
                }
            }

            return list;
        }

        /// <summary>Points on the navmesh 15-30 m around (eight directions, two distances).</summary>
        private static IEnumerable<Vector3> Around(Vector3 pos)
        {
            foreach (float r in new[] { 15f, 30f })
            {
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                    if (NavMesh.SamplePosition(pos + dir * r, out NavMeshHit hit, 4f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - pos.y) < 6f)
                    {
                        yield return hit.position;
                    }
                }
            }
        }

        /// <summary>A full navmesh path leads from one point to the other.</summary>
        private static bool Reaches(Vector3 from, Vector3 to)
        {
            if (!NavMesh.SamplePosition(from, out NavMeshHit a, 1.5f, NavMesh.AllAreas) || !NavMesh.SamplePosition(to, out NavMeshHit b, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }

            var path = new NavMeshPath();
            return NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>Every 2 s: the way each bot walked (a point every 10 s once it moved 6 m, the last 12 kept).</summary>
        public static void Tick()
        {
            if (!Settings.FreeShutIn.Value || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
                {
                    continue;
                }

                State s = Of(bot);
                if (s.RecheckAt > 0f && now >= s.RecheckAt)
                {
                    s.RecheckAt = -1f;
                    if ((bot.Position - s.Anchor).sqrMagnitude < Spot * Spot)
                    {
                        Free(bot, s, s.What + ", looked at again after a door");
                    }
                    else
                    {
                        s.Rechecks = 0;
                    }
                }

                if (now < s.NextCrumb)
                {
                    continue;
                }

                s.NextCrumb = now + 10f;
                Vector3 pos = bot.Position;
                if (s.Crumbs.Count == 0 || (s.Crumbs[s.Crumbs.Count - 1] - pos).sqrMagnitude > 6f * 6f)
                {
                    s.Crumbs.Add(pos);
                    if (s.Crumbs.Count > 12)
                    {
                        s.Crumbs.RemoveAt(0);
                    }
                }
            }
        }
    }
}
