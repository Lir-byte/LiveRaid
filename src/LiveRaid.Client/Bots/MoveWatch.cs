using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using LiveRaid.Client.Tactics;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A bot that should be moving and is not - whatever layer drives it. The unstick layer (DoorAssistLayer) only helps bots walking
    /// QuestingBots' paths, and only when its own layer can take the bot; a bot in SAIN's search, in LiveRaid's break-contact run, a flank or
    /// an errand stood pressed into an open door for minutes (raid 2026-10-20 13:38: prezidento23 and Dirtbikercj, 17 m apart, 8 minutes,
    /// both "running away" without moving a metre). Every half second, for every PMC and scav that means to move (a movement decision of
    /// SAIN, a run, a flank, an errand with a place to go): no 0.4 m of progress in 2.5 s is a strike.
    ///  1st: a door at hand - open it if shut, slip past its leaf if open; a squadmate in the way - squeeze past him; nothing - a small
    ///       push along the way if nobody watches.
    ///  2nd: a door at hand is cut out of the navmesh for two minutes, so the next path goes another way; a search that cannot get
    ///       through tries to go around (a flank); otherwise another push.
    ///  3rd: the move is given up - the run / fall-back / search ends (SAIN fights from there), the errand or flank is dropped.
    /// Strikes are forgotten after 25 s of progress. QuestingBots' own walking stays with the unstick layer.
    /// </summary>
    public static class MoveWatch
    {
        private const float Window = 1.6f;
        private const float Progress = 0.3f;

        private class Track
        {
            public Vector3 Pos;
            public float Since;
            public int Strikes;
            public float StrikesSince;
        }

        private class Ignored
        {
            public Player Player;
            public Collider Collider;
            public float Until;
        }

        // Where a bot was stuck last: stuck again at the same place soon means the gentle fix did not work, so the next one is the sterner
        private static readonly Dictionary<string, (Vector3 pos, float at)> _lastStuck = new Dictionary<string, (Vector3, float)>();
        // Degrees each bot has turned since the last timeline line (to see a bot that spins on the spot)
        private static readonly Dictionary<string, (float yaw, float turned)> _yaw = new Dictionary<string, (float, float)>();
        private static readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        private static readonly List<Ignored> _ignored = new List<Ignored>();
        private static float _next;

        public static int Opened { get; private set; }
        public static int Slipped { get; private set; }
        public static int Squeezed { get; private set; }
        public static int Rerouted { get; private set; }
        public static int Nudged { get; private set; }
        public static int GaveUp { get; private set; }

        public static void Clear()
        {
            _tracks.Clear();
            _yaw.Clear();
            _lastStuck.Clear();
            _ignored.Clear();
            _next = 0f;
            Opened = Slipped = Squeezed = Rerouted = Nudged = GaveUp = 0;
        }

        public static string Summary => $"opened {Opened}, slipped past leaves {Slipped}, squeezed past squadmates {Squeezed}, other way {Rerouted}, pushes {Nudged}, gave up {GaveUp}";

        public static void Tick()
        {
            if (!Settings.DoorAssist.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            RestoreCollisions();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                try
                {
                    TrackYaw(bot, player);
                    LootWatch.Tick(bot, player.GetComponent<LootingBots.Components.LootingBrain>());
                    Watch(bot, player, now);
                }
                catch (Exception e)
                {
                    Diag.Report("MoveWatch", e);
                }
            }
        }

        private static void TrackYaw(BotOwner bot, Player player)
        {
            float yaw = player.Rotation.x;
            if (_yaw.TryGetValue(bot.ProfileId, out var y))
            {
                _yaw[bot.ProfileId] = (yaw, y.turned + Mathf.Abs(Mathf.DeltaAngle(y.yaw, yaw)));
            }
            else
            {
                _yaw[bot.ProfileId] = (yaw, 0f);
            }
        }

        /// <summary>Degrees the bot has turned since the previous call (for the timeline).</summary>
        public static float TakeTurned(BotOwner bot)
        {
            if (!_yaw.TryGetValue(bot.ProfileId, out var y))
            {
                return 0f;
            }

            _yaw[bot.ProfileId] = (y.yaw, 0f);
            return y.turned;
        }

        private static void Watch(BotOwner bot, Player player, float now)
        {
            BackOffLayer.Record(bot);
            Vector3 pos = bot.Position;
            if (!_tracks.TryGetValue(bot.ProfileId, out Track t))
            {
                _tracks[bot.ProfileId] = t = new Track { Pos = pos, Since = now, StrikesSince = now };
                return;
            }

            if ((pos - t.Pos).sqrMagnitude > Progress * Progress)
            {
                t.Pos = pos;
                t.Since = now;
                if (now - t.StrikesSince > 25f)
                {
                    t.Strikes = 0;
                }

                return;
            }

            // Backing off on purpose: not stuck
            if (BackOffLayer.Has(bot))
            {
                t.Since = now;
                return;
            }

            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            string intent = Intent(bot, sain, out Vector3? goal);
            if (intent == null || sain?.GoalEnemy?.IsVisible == true || bot.Medecine?.Using == true || bot.BotLay?.IsLay == true
                || RouteTraverseLayer.Traversing.Contains(bot.ProfileId))
            {
                t.Since = now;
                return;
            }

            if (now - t.Since < Window)
            {
                return;
            }

            // Stuck: one strike per window
            t.Since = now;
            if (now - t.StrikesSince > 25f)
            {
                t.Strikes = 0;
                t.StrikesSince = now;
            }

            t.Strikes++;
            if (t.Strikes == 1 && _lastStuck.TryGetValue(bot.ProfileId, out var last) && now - last.at < 90f && (last.pos - bot.Position).sqrMagnitude < 4f * 4f)
            {
                // Same place as a moment ago: skip the gentle step
                t.Strikes = 2;
            }

            _lastStuck[bot.ProfileId] = (bot.Position, now);
            Strike(bot, player, intent, goal, t.Strikes);
            if (t.Strikes >= 3)
            {
                // The move was given up; if the bot is stuck again the steps start over (the same-place memory makes them quick)
                t.Strikes = 0;
                t.StrikesSince = now;
            }
        }

        /// <summary>What the bot is trying to do that needs it to move, or null when standing is its intent.</summary>
        private static string Intent(BotOwner bot, SAIN.Components.BotComponent sain, out Vector3? goal)
        {
            goal = null;
            if (CombatSense.TryGetState(bot, out CombatSense.State s) && s.Stance == CombatStance.BreakContact && s.BreakPoint.HasValue && Time.time < s.BreakUntil)
            {
                goal = s.BreakPoint;
                return "break contact";
            }

            if (Maneuvers.TryGetFlank(bot, out Maneuvers.Flank f) && f.ArrivedAt < 0f)
            {
                goal = f.Target;
                return "flank";
            }

            if (TacticalTasks.TryGet(bot, out TacticalTask task) && task.StandAt.HasValue && (task.StandAt.Value - bot.Position).sqrMagnitude > 2f * 2f)
            {
                goal = task.StandAt;
                return task.Kind.ToString().ToLowerInvariant();
            }

            ECombatDecision d = sain?.Decision?.CurrentCombatDecision ?? ECombatDecision.None;
            switch (d)
            {
                case ECombatDecision.Search:
                case ECombatDecision.MoveToEngage:
                case ECombatDecision.RushEnemy:
                case ECombatDecision.Retreat:
                case ECombatDecision.RunAway:
                case ECombatDecision.ShiftCover:
                    return "SAIN " + d;
                case ECombatDecision.SeekCover:
                    return sain.Mover?.Moving == true ? "SAIN SeekCover" : null;
            }

            ESquadDecision sq = sain?.Decision?.CurrentSquadDecision ?? ESquadDecision.None;
            if (sq == ESquadDecision.GroupSearch || sq == ESquadDecision.Search || sq == ESquadDecision.Regroup || sq == ESquadDecision.Help)
            {
                return "SAIN " + sq;
            }

            // QuestingBots' walking is the unstick layer's job
            return null;
        }

        private static void Strike(BotOwner bot, Player player, string intent, Vector3? goal, int strike)
        {
            string name = bot.Profile.Nickname;
            Vector3 pos = bot.Position;
            Door door = DoorAssistLayer.NearestDoor(pos, 2.5f);
            if (strike >= 3)
            {
                // A few steps back and another way before the move is dropped (a third time at the same place: dropped)
                if (BackOffLayer.Begin(bot, goal, intent))
                {
                    Rerouted++;
                    return;
                }

                GiveUp(bot, intent);
                return;
            }

            if (door != null)
            {
                if (strike == 1)
                {
                    // A door caught half-way (its opening cut short by a fight, a shot, a reload) blocks like a wall: swing it open
                    if (door.DoorState == EDoorState.Interacting && DoorAssistLayer.SnapOpen(door))
                    {
                        Opened++;
                        IgnoreFor(player, door.Collider, 6f);
                        Log.Info($"LR Stuck: {name} ({intent}) at door {door.Id} {DoorAssistLayer.Where(door)} stuck half-open - swung open");
                    }
                    else if (door.DoorState == EDoorState.Shut && DoorAssistLayer.OpenNow(bot, door))
                    {
                        Opened++;
                        IgnoreFor(player, door.Collider, 4f);
                        Log.Info($"LR Stuck: {name} ({intent}) at shut door {door.Id} {DoorAssistLayer.Where(door)} - opens it");
                    }
                    else
                    {
                        Slipped++;
                        IgnoreFor(player, door.Collider, 4f);
                        Log.Info($"LR Stuck: {name} ({intent}) pressed into door {door.Id} {DoorAssistLayer.Where(door)} ({door.DoorState}) at ({pos.x:0}, {pos.y:0}, {pos.z:0}) - slips past the leaf");
                    }

                    return;
                }

                // Raid 2026-10-31 05:46: kikirio's door stayed half-open after the opening (a fight), was cut out of the navmesh under his feet and he stood
                // 35 s in the doorway. A door that is not open is swung open first; only an open one that still stops the bot is cut out
                if ((door.DoorState == EDoorState.Interacting || door.DoorState == EDoorState.Shut) && DoorAssistLayer.SnapOpen(door))
                {
                    Opened++;
                    IgnoreFor(player, door.Collider, 8f);
                    Log.Info($"LR Stuck: {name} ({intent}) still stopped at door {door.Id} {DoorAssistLayer.Where(door)} ({door.DoorState}) - swung open");
                    return;
                }

                if (door.DoorState != EDoorState.Locked && DoorAssistLayer.CutOutNow(door, 120f, $"{name} ({intent}) could not get through"))
                {
                    Rerouted++;
                    Reroute(bot, intent);
                    return;
                }
            }

            // The gentle help did not free it: step back along its track and take a way round
            if (strike == 2 && BackOffLayer.Begin(bot, goal, intent))
            {
                Rerouted++;
                return;
            }

            // A squadmate right in the way: squeeze past him (both are moving the same way, usually through a doorway)
            Player mate = SquadmateInTheWay(bot, pos, goal);
            if (mate != null)
            {
                Squeezed++;
                Collider c = mate.MovementContext?.CharacterController?.GetCollider();
                IgnoreFor(player, c, 2.5f);
                Log.Detail("task", $"{name} ({intent}) blocked by squadmate {mate.Profile.Nickname} - squeezes past");
                return;
            }

            if (strike == 2 && intent.StartsWith("SAIN Search") && Maneuvers.TrySoloFlank(bot, LiveRaid.Client.Bots.SainBot.Of(bot)?.GoalEnemy))
            {
                Rerouted++;
                Log.Info($"LR Stuck: {name} cannot get through searching for its enemy - goes around instead");
                return;
            }

            if (TryNudge(bot, pos, goal))
            {
                Nudged++;
                Log.Detail("task", $"{name} ({intent}) snagged at ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0}) - nobody watching, pushed along its way");
            }
        }

        /// <summary>After a door was cut out: the runner and the flanker get a fresh path; SAIN re-plans by itself.</summary>
        private static void Reroute(BotOwner bot, string intent)
        {
            if (intent == "break contact" && CombatSense.TryGetState(bot, out CombatSense.State s) && s.BreakPoint.HasValue)
            {
                bot.GoToPoint(s.BreakPoint.Value, false, -1f, false, false);
            }
            else if (intent == "flank" && Maneuvers.TryGetFlank(bot, out Maneuvers.Flank f))
            {
                bot.GoToPoint(f.Target, false, -1f, false, false);
            }
        }

        private static void GiveUp(BotOwner bot, string intent)
        {
            GaveUp++;

            // It cannot get to the one it is after (and nobody is shooting): the hunt is over, back to the day's business
            if (intent.StartsWith("SAIN") || intent == "break contact" || intent == "flank" || intent == "assist")
            {
                Tactics.EnemyLapse.Drop(bot, intent);
            }

            if (Maneuvers.IsFlanking(bot))
            {
                Maneuvers.EndFlank(bot, "stuck");
            }

            if (TacticalTasks.TryGet(bot, out _))
            {
                TacticalTasks.Cancel(bot, "stuck on the way");
            }

            CombatSense.GiveUpMove(bot, intent);
        }

        private static Player SquadmateInTheWay(BotOwner bot, Vector3 pos, Vector3? goal)
        {
            HashSet<string> squad = LootBrain.SquadIds(bot);
            if (squad.Count <= 1)
            {
                return null;
            }

            Vector3 heading = (goal ?? bot.Mover.CurrentCornerPoint) - pos;
            heading.y = 0f;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null || p.ProfileId == bot.ProfileId || !squad.Contains(p.ProfileId))
                {
                    continue;
                }

                Vector3 to = p.Position - pos;
                if (Mathf.Abs(to.y) > 1.5f)
                {
                    continue;
                }

                to.y = 0f;
                if (to.sqrMagnitude < 1.4f * 1.4f && (heading.sqrMagnitude < 0.01f || Vector3.Angle(heading, to) < 80f))
                {
                    return p;
                }
            }

            return null;
        }

        /// <summary>One metre towards where the bot wants to go, on the navmesh - only while nobody (you or your camera) can see it.</summary>
        private static bool TryNudge(BotOwner bot, Vector3 pos, Vector3? goal)
        {
            Vector3 toward = bot.Mover != null && bot.Mover.HasPathAndNoComplete ? bot.Mover.CurrentCornerPoint : goal ?? pos;
            Vector3 dir = toward - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f || ViewerSight.CanSee(pos + Vector3.up * 1.2f))
            {
                return false;
            }

            Vector3 probe = pos + dir.normalized * Mathf.Min(1f, dir.magnitude);
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - pos.y) > 1f
                || NavMesh.Raycast(pos, hit.position, out _, NavMesh.AllAreas))
            {
                return false;
            }

            RouteTraverseLayer.PlaceOnNavmesh(bot, hit.position);
            return true;
        }

        private static void IgnoreFor(Player player, Collider collider, float seconds)
        {
            if (player?.MovementContext == null || collider == null)
            {
                return;
            }

            player.MovementContext.IgnoreInteractionCollision(collider, true);
            _ignored.Add(new Ignored { Player = player, Collider = collider, Until = Time.time + seconds });
        }

        private static void RestoreCollisions()
        {
            for (int i = _ignored.Count - 1; i >= 0; i--)
            {
                Ignored g = _ignored[i];
                if (Time.time < g.Until)
                {
                    continue;
                }

                _ignored.RemoveAt(i);
                try
                {
                    if (g.Player != null && g.Collider != null && g.Player.HealthController?.IsAlive == true)
                    {
                        g.Player.MovementContext?.IgnoreInteractionCollision(g.Collider, false);
                    }
                }
                catch
                {
                    // gone with the bot or the map
                }
            }
        }
    }
}
