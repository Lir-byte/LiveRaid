using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A squad member looks into the next room (F12 -> 3 "Squadmates look into nearby rooms", 1.3.0). In the 1.2.9 raids the followers of a squad finished 0 steps of any quest
    /// (QuestingBots makes them follow the leader), so 5-10 of 40-60 bots of a raid covered no ground of their own, and a squad of four explored what one bot would.
    /// A follower who is with its leader, calm and idle, now and then (every 25-55 s, by its curiosity) walks off for up to 40 s to a spot no bot has been to in this raid,
    /// 12-30 m from the leader (a floor up or down included, and the places the atlas missed in earlier raids first), stands there a moment and comes back - the way a player
    /// of a squad checks the room next door while the others loot. It is a tactical task: a fight, the squad's leaving (the leash of TacticalTasks) or a better task ends it.
    /// </summary>
    public static class FollowerSweep
    {
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static float _tick;

        public static int Sent { get; private set; }
        public static int NoWay { get; private set; }
        public static int NoSpot { get; private set; }
        public static int Upstairs { get; private set; }

        // Why a follower's turn came to nothing (1.4.1: the raids of 1.3.x showed 0 sent, 0 no spot, 0 no way - every turn ended before the search)
        private static int _noLeader, _busy, _apart, _leaderMoving, _roll;

        public static string Summary => $"squad members sent to look into nearby places {Sent} (a floor away {Upstairs}), no spot {NoSpot}, no way {NoWay}; " +
                                        $"turns that ended earlier: not a follower {_noLeader}, busy or not calm {_busy}, over 22 m from the leader {_apart}, leader on the move or in danger {_leaderMoving}, by its own roll {_roll}";

        public static void Clear()
        {
            _next.Clear();
            _tick = 0f;
            Sent = NoWay = NoSpot = Upstairs = 0;
            _noLeader = _busy = _apart = _leaderMoving = _roll = 0;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.FollowerSweep.Value || Time.time < _tick)
            {
                return;
            }

            _tick = Time.time + 3f;
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

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other || p.Packrat)
                {
                    continue;
                }

                if (_next.TryGetValue(bot.ProfileId, out float at) && now < at)
                {
                    continue;
                }

                _next[bot.ProfileId] = now + 25f + (float)_rng.NextDouble() * 30f / Mathf.Clamp(p.Explore, 0.4f, 1.6f);
                try
                {
                    Try(bot, p);
                }
                catch (System.Exception e)
                {
                    Diag.Report("FollowerSweep", e);
                }
            }
        }

        private static void Try(BotOwner bot, BotProfile p)
        {
            // The game's own boss-follower tie is often missing for QuestingBots' squads: four raids of 1.4.1 gave "not a follower" 589 times and not one
            // other outcome, with squads of three alive for half an hour. QuestingBots' own leader of the bot's group is asked second
            Player leaderPlayer = TacticalTasks.LeaderOf(bot);
            if (leaderPlayer == null)
            {
                BotOwner qbLeader = QuestingBots.BotLogic.HiveMind.BotHiveMindMonitor.GetGroupLeader(bot);
                if (qbLeader != null && qbLeader != bot && !qbLeader.IsDead)
                {
                    leaderPlayer = qbLeader.GetPlayer;
                }
            }

            BotOwner leader = leaderPlayer?.AIData?.BotOwner;
            if (leader == null || leader.IsDead)
            {
                _noLeader++;
                return;
            }

            if (TacticalTasks.TryGet(bot, out _) || !TacticalTasks.BotIsCalm(bot) || !TacticalTasks.NoThreat(bot) || RoomSweep.InVisit(bot) || BotMind.SinceEngaged(bot) < 25f)
            {
                _busy++;
                return;
            }

            // Only a follower that is really with its leader, whose leader is not on the run
            float together = Vector3.Distance(bot.Position, leader.Position);
            if (together > 22f)
            {
                _apart++;
                return;
            }

            // A leader at a full walk (about 3.5 m/s) is not "on the run": only a sprint takes the follower with it
            if (!TacticalTasks.NoThreat(leader) || leaderPlayer.Velocity.sqrMagnitude > 4.5f * 4.5f)
            {
                _leaderMoving++;
                return;
            }

            // The curiosity of the bot decides how often it goes
            if (_rng.NextDouble() > Mathf.Clamp(0.35f + 0.35f * p.Explore, 0.2f, 0.9f))
            {
                _roll++;
                return;
            }

            List<Vector3> mates = new List<Vector3> { leader.Position, bot.Position };
            BotsGroup group = bot.BotsGroup;
            for (int i = 0; group != null && i < group.MembersCount; i++)
            {
                BotOwner m = group.Member(i);
                if (m != null && !m.IsDead && m != bot)
                {
                    mates.Add(m.Position);
                }
            }

            float maxR = Settings.TightSquads.Value ? 22f : 30f;
            if (!CoverageLedger.TryColdSpotNear(leader.Position, 12f, maxR, mates, _rng, out Poi spot))
            {
                NoSpot++;
                return;
            }

            // A real way, not much longer than the straight line (no walk round the block)
            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(bot.Position, spot.Pos, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                NoWay++;
                return;
            }

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            if (length > 70f)
            {
                NoWay++;
                return;
            }

            bool upstairs = Mathf.Abs(spot.Y - leader.Position.y) > 2.5f;
            TacticalTask task = new TacticalTask
            {
                Kind = TaskKind.Relocate,
                StandAt = spot.Pos,
                LookAt = spot.Pos + bot.LookDirection * 4f,
                Until = Time.time + 25f + length / 2.5f,
                Dwell = 3f,
                Speed = 1f,
                MovePose = 1f,
                Sprint = false,
            };
            if (TacticalTasks.Set(bot, task))
            {
                Sent++;
                if (upstairs)
                {
                    Upstairs++;
                }

                Log.Detail("squad", $"{p.Name} goes to look at a place {length:0} m away{(upstairs ? " on another floor" : "")} while {leader.Profile.Nickname} is busy");
            }
        }
    }
}
