using System;
using System.Collections.Generic;
using System.Linq;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Unsticking: a bot that wants to walk on but has not moved for two seconds.
    /// EFT opens a door only through door links baked into the navmesh, SAIN only in its own fight, and nobody handles a bot pressed
    /// into the leaf of an open door, a glass wall or a fence corner (videos 2026-10-19: a scav stood minutes in the bank's glass door).
    /// - A shut door in the way: look at it and open it (the animation), then walk on.
    /// - An open door in the way: the bot is pressed into the leaf - it slips past it (no collision with that leaf for 3 s, like SAIN
    ///   does while opening); a door that keeps stopping different bots is cut out of the navmesh for 5 minutes.
    /// - No door: a step to the side of the way (most snags are a corner or a post), next time - if nobody watches - a small push
    ///   along the path; after three tries in 25 s the bot is left to QuestingBots / SAIN for half a minute.
    /// Priority 23 (above QuestingBots, LiveRaid tactics and SAIN's squad and solo layers); works in SAIN's fights too, as long as no
    /// enemy is in sight (then SAIN fights).
    /// </summary>
    public class DoorAssistLayer : CustomLayer
    {
        public const int LayerPriority = 23;

        private const float StuckSeconds = 1.5f;
        private const float DoorRange = 2.2f;
        private const float SlipSeconds = 3f;

        private static readonly List<Door> _doors = new List<Door>();
        private static readonly Dictionary<string, float> _leftAlone = new Dictionary<string, float>();
        private static string _doorsLocation;

        public static int Opened { get; private set; }
        public static int BackedOff { get; private set; }
        public static int GaveUp { get; private set; }
        public static int Nudged { get; private set; }

        internal enum JobKind
        {
            None,
            OpenDoor,
            SideStep,
        }

        private float _nextCheck;
        private Vector3 _lastPos;
        private float _stillFor;

        // The current job
        internal JobKind Job;
        internal Door Door;
        internal Vector3? Target;
        internal Vector3? Resume;
        internal float JobUntil;
        private int _strikes;
        private float _strikesSince;
        private int _sideSign = 1;

        // A door leaf the bot may walk through for a moment
        private Collider _ignored;
        private float _ignoreUntil;

        private static readonly Dictionary<string, DoorAssistLayer> _registry = new Dictionary<string, DoorAssistLayer>();

        // Doors that beat several bots (a decor door the navmesh runs through): cut out of the navmesh for a while so paths go around
        private const int BlockAfterBots = 2;
        private const float BlockSeconds = 300f;
        private static readonly Dictionary<string, HashSet<string>> _beaten = new Dictionary<string, HashSet<string>>();
        private static readonly Dictionary<string, (GameObject cut, float until)> _cut = new Dictionary<string, (GameObject, float)>();
        public static int CutOut { get; private set; }

        public DoorAssistLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            _registry[botOwner.ProfileId] = this;
        }

        internal static DoorAssistLayer Of(BotOwner bot) => _registry.TryGetValue(bot.ProfileId, out DoorAssistLayer l) ? l : null;

        public static void Clear()
        {
            _doors.Clear();
            _doorsLocation = null;
            _leftAlone.Clear();
            _registry.Clear();
            _beaten.Clear();
            foreach ((GameObject cut, float _) in _cut.Values)
            {
                if (cut != null)
                {
                    UnityEngine.Object.Destroy(cut);
                }
            }

            _cut.Clear();
            Opened = BackedOff = GaveUp = CutOut = Nudged = 0;
        }

        /// <summary>Puts the doors that were cut out back into the navmesh when their time is up.</summary>
        public static void Tick()
        {
            if (_cut.Count == 0)
            {
                return;
            }

            foreach (string id in new List<string>(_cut.Keys))
            {
                (GameObject cut, float until) = _cut[id];
                if (Time.time >= until)
                {
                    if (cut != null)
                    {
                        UnityEngine.Object.Destroy(cut);
                    }

                    _cut.Remove(id);
                    _beaten.Remove(id);
                    Log.Info($"LR Door: door {id.Split('@')[0]} is back in the navmesh");
                }
            }
        }

        public override string GetName() => "LiveRaid Unstick";

        public override Action GetNextAction() => new Action(typeof(DoorAssistLogic), Job == JobKind.OpenDoor ? "open door" : "side step");

        public override bool IsCurrentActionEnding() => Job == JobKind.None;

        public override bool IsActive()
        {
            try
            {
                return CheckActive();
            }
            catch (Exception e)
            {
                Diag.Report("DoorAssistLayer", e);
                EndJob();
                return false;
            }
        }

        private bool CheckActive()
        {
            if (_ignored != null && Time.time > _ignoreUntil)
            {
                StopIgnoring();
            }

            if (Job != JobKind.None)
            {
                if (Time.time > JobUntil || Fighting())
                {
                    EndJob();
                    return false;
                }

                return true;
            }

            if (!Settings.DoorAssist.Value || BotOwner.BotState != EBotState.Active || Time.time < _nextCheck)
            {
                return false;
            }

            // Bosses, followers, raiders stay vanilla
            if (BotProfiles.Get(BotOwner)?.Kind == BotKind.Other)
            {
                _nextCheck = Time.time + 30f;
                return false;
            }

            _nextCheck = Time.time + 0.5f;
            Vector3 pos = BotOwner.Position;
            float moved = (pos - _lastPos).magnitude;
            _lastPos = pos;
            if (!WantsToWalk() || moved > 0.25f || Fighting())
            {
                _stillFor = 0f;
                return false;
            }

            _stillFor += 0.5f;
            if (_stillFor < StuckSeconds)
            {
                return false;
            }

            // At the end of a route the bot waits for the climb, not for a door (a decor door stands next to the cliff route)
            if (NearRouteEnd(pos) || (_leftAlone.TryGetValue(BotOwner.ProfileId, out float calmUntil) && Time.time < calmUntil))
            {
                _stillFor = 0f;
                return false;
            }

            _stillFor = 0f;
            if (Time.time - _strikesSince > 25f)
            {
                _strikes = 0;
                _strikesSince = Time.time;
            }

            _strikes++;
            string name = BotOwner.Profile.Nickname;
            Door door = DoorInTheWay(pos);
            if (door != null && !(_leftAlone.TryGetValue(KeyOf(door) + BotOwner.ProfileId, out float doorUntil) && Time.time < doorUntil))
            {
                return HandleDoor(door, name);
            }

            // Raid 15:59: The_Antman stood 80 s on a narrow ledge where no side step fitted, a push was not allowed (the camera was on him) and four strikes
            // only "left him alone for 30 s", twice. A person who cannot get on steps back the way he came and tries another way
            if ((_strikes >= 4 || (_strikes >= 2 && SideStepPoint(pos) == null)) && BackOffLayer.Begin(BotOwner, BotOwner.Mover?.RealDestPoint, "QuestingBots walk"))
            {
                _strikes = 0;
                BackedOff++;
                return false;
            }

            if (_strikes >= 4)
            {
                GaveUp++;
                _leftAlone[BotOwner.ProfileId] = Time.time + 30f;
                Log.Info($"LR Stuck: {name} still cannot get on at ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0}) after side steps and pushes - left to its own logic for 30 s");
                return false;
            }

            Vector3 dest = BotOwner.Mover.RealDestPoint;
            if (_strikes == 3 && !ViewerSight.CanSee(pos + Vector3.up * 1.2f) && TryNudge(pos))
            {
                Nudged++;
                Log.Info($"LR Stuck: {name} snagged at ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0}) - nobody watching, nudged 1 m along its way");
                BotOwner.GoToPoint(dest, true, -1f, false, false);
                return false;
            }

            Vector3? side = SideStepPoint(pos);
            if (side == null)
            {
                return false;
            }

            Job = JobKind.SideStep;
            Target = side;
            Resume = dest;
            JobUntil = Time.time + 1.8f;
            BackedOff++;
            Log.Detail("task", $"{name} snagged at ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0}) - steps aside (try {_strikes})");
            return true;
        }

        private bool HandleDoor(Door door, string name)
        {
            string id = door.Id;
            if (door.DoorState == EDoorState.Locked)
            {
                // QuestingBots opens it if the bot carries the key; without one there is nothing to do but go another way
                GaveUp++;
                _leftAlone[KeyOf(door) + BotOwner.ProfileId] = Time.time + 60f;
                Log.Info($"LR Door: {name} is stuck at locked door {id} without its key - leaves it for a minute");
                return false;
            }

            if (door.DoorState == EDoorState.Shut)
            {
                Job = JobKind.OpenDoor;
                Door = door;
                Resume = BotOwner.Mover.RealDestPoint;
                JobUntil = Time.time + 2.5f;
                Log.Info($"LR Door: {name} stuck at door {id} (Shut) -> opens it");
                return true;
            }

            // Open (or swinging): pressed into the leaf - walk past it
            Ignore(door.Collider);
            BackedOff++;
            if (_strikes >= 3)
            {
                Beaten(door);
                GaveUp++;
                _leftAlone[KeyOf(door) + BotOwner.ProfileId] = Time.time + 60f;
                Log.Info($"LR Door: {name} still cannot get through door {id} ({door.DoorState}) - leaves it for a minute");
            }
            else
            {
                Log.Info($"LR Door: {name} pressed into the leaf of door {id} {Where(door)} ({door.DoorState}) -> slips past it");
            }

            return false;
        }

        internal void Ignore(Collider collider)
        {
            if (collider == null)
            {
                return;
            }

            if (_ignored != null && _ignored != collider)
            {
                StopIgnoring();
            }

            BotOwner.GetPlayer?.MovementContext?.IgnoreInteractionCollision(collider, true);
            _ignored = collider;
            _ignoreUntil = Time.time + SlipSeconds;
        }

        private void StopIgnoring()
        {
            try
            {
                if (_ignored != null)
                {
                    BotOwner.GetPlayer?.MovementContext?.IgnoreInteractionCollision(_ignored, false);
                }
            }
            catch
            {
                // the door may be gone with the map
            }

            _ignored = null;
        }

        internal void EndJob()
        {
            JobKind was = Job;
            Job = JobKind.None;
            Door = null;
            Target = null;
            if (was != JobKind.None && Resume.HasValue && BotOwner.BotState == EBotState.Active)
            {
                // Back on the way at once (QuestingBots / SAIN would re-issue it a moment later anyway)
                BotOwner.GoToPoint(Resume.Value, true, -1f, false, false);
            }

            Resume = null;
        }

        private void Beaten(Door door)
        {
            string id = KeyOf(door);
            if (!_beaten.TryGetValue(id, out HashSet<string> bots))
            {
                _beaten[id] = bots = new HashSet<string>();
            }

            bots.Add(BotOwner.ProfileId);
            if (bots.Count < BlockAfterBots || _cut.ContainsKey(id) || door.DoorState == EDoorState.Locked || door.Collider == null)
            {
                return;
            }

            // A carving box over the doorway: QuestingBots' and SAIN's next paths go around it (or over a route), nobody presses into it
            Bounds b = door.Collider.bounds;
            GameObject cut = new GameObject("LiveRaid door cut " + id);
            cut.transform.position = b.center;
            NavMeshObstacle obstacle = cut.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.size = new Vector3(Mathf.Max(b.size.x, 0.6f), Mathf.Max(b.size.y, 2f), Mathf.Max(b.size.z, 0.6f));
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
            _cut[id] = (cut, Time.time + BlockSeconds);
            CutOut++;
            Log.Info($"LR Door: door {door.Id} at {Where(door)} ({door.DoorState}) stopped {bots.Count} bots - cut out of the navmesh for {BlockSeconds / 60f:0} min, paths go around it");
        }

        internal void CountOpened() => Opened++;

        /// <summary>An enemy in sight: SAIN fights, nobody fiddles with doors.</summary>
        private bool Fighting()
        {
            SAIN.Components.BotComponent sain = SainBot.Of(BotOwner);
            var enemy = sain?.GoalEnemy;
            return enemy != null && (enemy.IsVisible || enemy.TimeSinceSeen < 3f);
        }

        private static bool NearRouteEnd(Vector3 pos)
        {
            foreach (Raid.Route route in Raid.Routes.Active)
            {
                if (route.Linked && ((route.Start - pos).sqrMagnitude < 2.5f * 2.5f || (route.End - pos).sqrMagnitude < 2.5f * 2.5f))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The bot is walking somewhere (QuestingBots, SAIN, a tactics task, following its leader), not standing on purpose.</summary>
        private bool WantsToWalk()
        {
            BotMover mover = BotOwner.Mover;
            if (mover == null || RouteTraverseLayer.Traversing.Contains(BotOwner.ProfileId) || Spectator.TakeOver.IsTaken(BotOwner)
                || BotOwner.Medecine?.Using == true || BotOwner.BotLay?.IsLay == true)
            {
                return false;
            }

            if (!mover.HasPathAndNoComplete)
            {
                return false;
            }

            return (mover.RealDestPoint - BotOwner.Position).sqrMagnitude > 2.5f * 2.5f;
        }

        /// <summary>A navmesh spot 1.2 m to the side of the way (alternating sides), on the same level and reachable in a straight line.</summary>
        private Vector3? SideStepPoint(Vector3 pos)
        {
            Vector3 heading = BotOwner.Mover.CurrentCornerPoint - pos;
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.01f)
            {
                heading = BotOwner.LookDirection;
                heading.y = 0f;
            }

            heading.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, heading);
            _sideSign = -_sideSign;
            foreach (float sign in new[] { _sideSign, -_sideSign })
            {
                foreach (float back in new[] { 0f, -0.6f })
                {
                    Vector3 probe = pos + side * (1.2f * sign) + heading * back;
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.6f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - pos.y) < 0.8f
                        && !NavMesh.Raycast(pos, hit.position, out _, NavMesh.AllAreas))
                    {
                        return hit.position;
                    }
                }
            }

            return null;
        }

        /// <summary>One metre further along the path, on the navmesh; only while nobody watches.</summary>
        private bool TryNudge(Vector3 pos)
        {
            Vector3 corner = BotOwner.Mover.CurrentCornerPoint;
            Vector3 dir = corner - pos;
            if (dir.sqrMagnitude < 0.04f)
            {
                return false;
            }

            Vector3 probe = pos + dir.normalized * Mathf.Min(1f, dir.magnitude);
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - pos.y) > 1f)
            {
                return false;
            }

            RouteTraverseLayer.PlaceOnNavmesh(BotOwner, hit.position);
            return true;
        }

        /// <summary>A door within reach, in the direction the bot is trying to go (or right against it).</summary>
        private Door DoorInTheWay(Vector3 pos)
        {
            EnsureDoors();
            Vector3 corner = BotOwner.Mover.CurrentCornerPoint;
            Vector3 heading = corner - pos;
            heading.y = 0f;
            Door best = null;
            float bestDist = DoorRange;
            foreach (Door door in _doors)
            {
                if (door == null || door.Collider == null || !door.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 closest = ClosestOnDoor(door.Collider, pos + Vector3.up);
                if (Mathf.Abs(closest.y - (pos.y + 1f)) > 1.5f)
                {
                    continue;
                }

                Vector3 to = closest - pos;
                to.y = 0f;
                float dist = to.magnitude;
                if (dist > bestDist)
                {
                    continue;
                }

                // Right against it, or ahead on the way to the next corner
                if (dist > 0.9f && heading.sqrMagnitude > 0.01f && Vector3.Angle(heading, to) > 75f)
                {
                    continue;
                }

                best = door;
                bestDist = dist;
            }

            return best;
        }

        /// <summary>The nearest working door within <paramref name="range"/> m of the bot's body (same storey), or null.</summary>
        internal static Door NearestDoor(Vector3 pos, float range)
        {
            EnsureDoors();
            Door best = null;
            float bestDist = range;
            foreach (Door door in _doors)
            {
                if (door == null || door.Collider == null || !door.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 closest = ClosestOnDoor(door.Collider, pos + Vector3.up);
                if (Mathf.Abs(closest.y - (pos.y + 1f)) > 1.5f)
                {
                    continue;
                }

                Vector3 to = closest - pos;
                to.y = 0f;
                float dist = to.magnitude;
                if (dist < bestDist)
                {
                    best = door;
                    bestDist = dist;
                }
            }

            return best;
        }

        /// <summary>The working doors within <paramref name="range"/> m of the bot's body (same storey), nearest first.</summary>
        internal static IEnumerable<Door> DoorsNear(Vector3 pos, float range)
        {
            EnsureDoors();
            var near = new List<(Door door, float dist)>();
            foreach (Door door in _doors)
            {
                if (door == null || door.Collider == null || !door.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 closest = ClosestOnDoor(door.Collider, pos + Vector3.up);
                Vector3 to = closest - pos;
                if (Mathf.Abs(closest.y - (pos.y + 1f)) > 1.5f)
                {
                    continue;
                }

                to.y = 0f;
                float dist = to.magnitude;
                if (dist < range)
                {
                    near.Add((door, dist));
                }
            }

            foreach ((Door door, float _) in near.OrderBy(n => n.dist))
            {
                yield return door;
            }
        }

        /// <summary>
        /// A door that will not open the normal way (stuck half-open after an interrupted opening, or shut and the bot's hands busy): swung open at once
        /// (EFT's own state change, forced). In LiveRaid Online the door's state is the host's to send, so there it only asks the game to open it.
        /// </summary>
        internal static bool SnapOpen(Door door)
        {
            if (door == null || door.DoorState == EDoorState.Locked || door.DoorState == EDoorState.Open)
            {
                return false;
            }

            try
            {
#if LIVERAID_ONLINE
                door.Interact(EInteractionType.Open);
#else
                door.SetDoorState(EDoorState.Open, true);
#endif
                Opened++;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens a shut door from wherever the bot stands (the interaction and its animation, as SAIN does). True when it opened.</summary>
        internal static bool OpenNow(BotOwner bot, Door door)
        {
            if (door == null || door.DoorState != EDoorState.Shut || bot?.GetPlayer == null)
            {
                return false;
            }

            Player player = bot.GetPlayer;
            player.MovementContext.ResetCanUsePropState();
            var result = Door.Interact(player, EInteractionType.Open);
            if (!result.Succeeded)
            {
                return false;
            }

            player.ExecuteInteraction(door, result.Value);
            Opened++;
            return true;
        }

        /// <summary>
        /// The point of a door collider nearest to <paramref name="p"/>. Collider.ClosestPoint returns the QUERY POINT itself for a concave mesh collider
        /// (Unity logs nothing), so such a "door" was at distance 0 from every bot on the map and always won the search: the real door a bot pressed into
        /// was never found (raid 19:08: the same door (69,15,104) in every stuck report, 100+ m from the bots). Bounds always work; a collider whose bounds are
        /// bigger than any door (a hull, a wall) is skipped (result far away).
        /// </summary>
        internal static Vector3 ClosestOnDoor(Collider c, Vector3 p)
        {
            Bounds b = c.bounds;
            if (b.size.x > 8f || b.size.z > 8f || b.size.y > 6f)
            {
                return p + new Vector3(0f, 100f, 0f);
            }

            return b.ClosestPoint(p);
        }

        /// <summary>Door ids are not unique on the map (the same id on doors in several places): the instance is the key.</summary>
        internal static string KeyOf(Door door) => door.Id + "@" + door.GetInstanceID();

        internal static string Where(Door door)
        {
            Vector3 c = door.Collider != null ? door.Collider.bounds.center : door.transform.position;
            return $"({c.x:0}, {c.y:0}, {c.z:0})";
        }

        /// <summary>Cuts a door out of the navmesh for a while, so the next paths go around it (another doorway, another room).</summary>
        internal static bool CutOutNow(Door door, float seconds, string why)
        {
            if (door == null || door.Collider == null || _cut.ContainsKey(KeyOf(door)))
            {
                return false;
            }

            Bounds b = door.Collider.bounds;
            GameObject cut = new GameObject("LiveRaid door cut " + door.Id);
            cut.transform.position = b.center;
            NavMeshObstacle obstacle = cut.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.size = new Vector3(Mathf.Max(b.size.x, 0.6f), Mathf.Max(b.size.y, 2f), Mathf.Max(b.size.z, 0.6f));
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
            _cut[KeyOf(door)] = (cut, Time.time + seconds);
            CutOut++;
            Log.Info($"LR Door: door {door.Id} at {Where(door)} ({door.DoorState}) cut out of the navmesh for {seconds:0} s - {why}");
            return true;
        }

        /// <summary>Every door of the map (NoiseResponse listens to them opening and closing).</summary>
        public static List<Door> Doors
        {
            get
            {
                EnsureDoors();
                return _doors;
            }
        }

        private static void EnsureDoors()
        {
            string location = Comfort.Common.Singleton<GameWorld>.Instance?.LocationId;
            if (_doorsLocation == location && _doors.Count > 0)
            {
                return;
            }

            _doors.Clear();
            _doors.AddRange(UnityEngine.Object.FindObjectsOfType<Door>());
            _doorsLocation = location;
        }
    }

    public class DoorAssistLogic : CustomLogic
    {
        private bool _interacted;
        private float _orderedAt;

        public DoorAssistLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            _interacted = false;
            _orderedAt = 0f;
            base.Start();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            DoorAssistLayer layer = DoorAssistLayer.Of(BotOwner);
            if (layer == null || layer.Job == DoorAssistLayer.JobKind.None)
            {
                return;
            }

            try
            {
                if (layer.Job == DoorAssistLayer.JobKind.OpenDoor)
                {
                    Open(layer, layer.Door);
                }
                else
                {
                    SideStep(layer);
                }
            }
            catch (Exception e)
            {
                Diag.Report("DoorAssistLogic", e);
                layer.EndJob();
            }
        }

        private void Open(DoorAssistLayer layer, Door door)
        {
            if (door == null)
            {
                layer.EndJob();
                return;
            }

            BotOwner.StopMove();
            BotOwner.Steering.LookToPoint(door.Collider.bounds.center);
            if (_interacted)
            {
                // A second for the animation; then the bot walks through (the swinging leaf must not shove it back)
                if (Time.time - _orderedAt > 1.2f || door.DoorState == EDoorState.Open)
                {
                    layer.Ignore(door.Collider);
                    layer.EndJob();
                }

                return;
            }

            _interacted = true;
            _orderedAt = Time.time;
            if (door.DoorState != EDoorState.Shut)
            {
                return;
            }

            Player player = BotOwner.GetPlayer;
            player.MovementContext.ResetCanUsePropState();
            var result = Door.Interact(player, EInteractionType.Open);
            if (result.Succeeded)
            {
                layer.Ignore(door.Collider);
                player.ExecuteInteraction(door, result.Value);
                layer.CountOpened();
            }
        }

        private void SideStep(DoorAssistLayer layer)
        {
            Vector3? target = layer.Target;
            if (target == null || (BotOwner.Position - target.Value).sqrMagnitude < 0.35f * 0.35f)
            {
                layer.EndJob();
                return;
            }

            if (Time.time - _orderedAt > 0.6f)
            {
                _orderedAt = Time.time;
                BotOwner.SetTargetMoveSpeed(0.7f);
                BotOwner.GoToPoint(target.Value, true, 0.25f, false, false);
            }
        }
    }
}
