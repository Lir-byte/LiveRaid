using System;
using System.Collections.Generic;
using System.Linq;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using QuestingBots.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A bot carrying the key for a locked safe, case or trunk walks to it, unlocks it with the key and has the loot scanned.
    /// (Locked doors are handled by QuestingBots quests - except the doors of a prize place on its carrier's run, walked to from here; LootingBots skips
    /// locked containers and has no key logic.)
    /// Priority 21, like the tactics layer: above QuestingBots questing/following (18/19), so a bot with the key really makes the detour
    /// (at 7 it sat under QuestingBots and almost never got a turn), below SAIN squad combat (22). It is only active while the bot is calm,
    /// so SAIN solo combat (20) still takes over the moment an enemy shows up. It never runs together with a tactics task.
    /// </summary>
    public class ContainerUnlockLayer : CustomLayer
    {
        public const int LayerPriority = 21;

        // Bots walking to a container right now (other systems leave them alone meanwhile)
        internal static readonly HashSet<string> Busy = new HashSet<string>();

        private static readonly HashSet<string> _done = new HashSet<string>();
        // Per bot and container: rolled once whether this bot bothers (a player with the key does not always make the detour)
        private static readonly Dictionary<string, bool> _wants = new Dictionary<string, bool>();
        private static readonly System.Random _rng = new System.Random();
        private LockedSpot _target;
        private Vector3 _stand;
        private float _nextSearch;
        private float _giveUpAt;

        public static int Went { get; private set; }
        public static int Skipped { get; private set; }
        public static int InnerDoors { get; private set; }
        public static int PrizeDoors { get; private set; }

        public static string Summary => $"locked containers: went to open {Went} (a roll said no {Skipped}), doors of prize places {PrizeDoors} (inner ones {InnerDoors})";

        public ContainerUnlockLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            LayerRegistry.Register(botOwner, this);
        }

        public static void Clear()
        {
            _done.Clear();
            _wants.Clear();
            Busy.Clear();
            LayerRegistry.Clear();
            Went = Skipped = InnerDoors = PrizeDoors = 0;
        }

        /// <summary>
        /// Rolled once per bot and container: 0.5-0.85 by greed (looters +0.1) - with the key in the pocket most players open the safe on the way, not all.
        /// </summary>
        private static bool Wants(BotOwner bot, BotProfile p, LockedSpot spot)
        {
            string key = bot.ProfileId + "|" + spot.Id;
            if (_wants.TryGetValue(key, out bool yes))
            {
                return yes;
            }

            float chance = 0.5f + 0.35f * Mathf.Clamp01(p.LootGreed / 1.3f) + (p.Style == PlayStyle.Looter ? 0.1f : 0f);
            yes = _rng.NextDouble() < chance;
            _wants[key] = yes;
            if (!yes)
            {
                Skipped++;
                Log.Detail("container", $"{p.Name} has the key for {spot.Id} but does not bother ({chance:P0})");
            }

            return yes;
        }

        /// <summary>A navmesh point next to a locked object the bot can walk to now (the inner door of a place, once the first door is open).</summary>
        private Vector3? StandNear(WorldInteractiveObject obj)
        {
            Vector3 center = obj.transform.position;
            NavMeshPath path = new NavMeshPath();
            for (int i = 0; i < 12; i++)
            {
                float angle = i * 30f * Mathf.Deg2Rad;
                float radius = i % 2 == 0 ? 1.1f : 1.7f;
                Vector3 probe = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 1f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - center.y) < 2.2f
                    && NavMesh.CalculatePath(BotOwner.Position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    return hit.position;
                }
            }

            return null;
        }

        public override string GetName() => "LiveRaid Unlock Container";

        public override bool IsActive()
        {
            try
            {
                return CheckActive();
            }
            catch (Exception e)
            {
                Diag.Report("ContainerUnlockLayer", e);
                Drop();
                return false;
            }
        }

        private void Drop()
        {
            if (_target != null)
            {
                _done.Add(_target.Id);
            }

            _target = null;
            _openUntil = 0f;
            Busy.Remove(BotOwner.ProfileId);
        }

        private bool CheckActive()
        {
            if (!Settings.ContainerKeys.Value || BotOwner.BotState != EBotState.Active)
            {
                if (_target != null)
                {
                    Drop();
                }

                return false;
            }

            if (_scanAt > 0f && Time.time > _scanAt)
            {
                // The lid or the door is open now: what is inside is looked for (a scan at the moment of the key's turn found it still locked)
                _scanAt = 0f;
                if (TacticalTasks.NoThreat(BotOwner))
                {
                    Tactics.RoomSweep.ForceScan(BotOwner);
                }
            }

            if (_target != null)
            {
                if (!StillWorking() || Time.time > _giveUpAt || BotOwner.IsDead)
                {
                    bool opened = !_target.StillLocked;
                    Log.Detail("container", $"{BotOwner.Profile.Nickname} {(opened ? "is done with" : "gave up on")} {_target.Id}");
                    Drop();
                    if (opened)
                    {
                        _scanAt = Time.time + 1.5f;
                    }

                    return false;
                }

                // A fight or a squad order (tactics task) pauses the detour; the give-up timer keeps running
                return TacticalTasks.NoThreat(BotOwner) && !TacticalTasks.TryGet(BotOwner, out _);
            }

            if (Time.time < _nextSearch)
            {
                return false;
            }

            _nextSearch = Time.time + 4f;
            BotProfile p = BotProfiles.Get(BotOwner);
            if (p == null || p.Kind == BotKind.Other || p.Keys.Count == 0 || MapIntel.Current == null)
            {
                return false;
            }

            if (!TacticalTasks.BotIsCalm(BotOwner) || TacticalTasks.TryGet(BotOwner, out _) || TacticalTasks.SquadLeaderOf(BotOwner) != null)
            {
                return false;
            }

            // Inside its prize place: the next locked door of it (KIBA's grate behind the shop door) comes first
            LockedSpot inner = PrizeKeys.InnerDoor(BotOwner);
            if (inner != null && !_done.Contains(inner.Id))
            {
                Vector3? at = StandNear(inner.Object);
                if (at == null)
                {
                    return false;
                }

                _target = inner;
                _stand = at.Value;
                _giveUpAt = Time.time + 60f;
                Busy.Add(BotOwner.ProfileId);
                InnerDoors++;
                Log.Info($"LR Container: {p.Name} goes to open the inner door {inner.Id} of its prize place with its key");
                return true;
            }

            // Its prize run is on (PrizeKeys): straight to the first door of the place - not left to QuestingBots' quest, which a wound or a wait held back
            LockedSpot prize = PrizeKeys.FirstDoor(BotOwner);
            if (prize != null && !_done.Contains(prize.Id))
            {
                NavMeshPath way = new NavMeshPath();
                if (NavMesh.CalculatePath(BotOwner.Position, prize.OutsidePos.Value, NavMesh.AllAreas, way) && way.status == NavMeshPathStatus.PathComplete)
                {
                    _target = prize;
                    _stand = prize.OutsidePos.Value;
                    _giveUpAt = Time.time + 240f;
                    Busy.Add(BotOwner.ProfileId);
                    PrizeDoors++;
                    Log.Info($"LR Container: {p.Name} goes to open the door {prize.Id} of its prize place with its key ({Vector3.Distance(BotOwner.Position, prize.OutsidePos.Value):0} m away)");
                    return true;
                }
            }

            HashSet<string> owned = new HashSet<string>(BotSetup.OwnedKeyIds(BotOwner));
            LockedSpot best = null;
            float bestDist = 110f * 110f;
            foreach (LockedSpot spot in MapIntel.Current.Spots)
            {
                if (!spot.IsContainer || !spot.StillLocked || spot.OutsidePos == null || _done.Contains(spot.Id) || !owned.Contains(spot.KeyId))
                {
                    continue;
                }

                float d = (spot.OutsidePos.Value - BotOwner.Position).sqrMagnitude;
                if (d < bestDist && Wants(BotOwner, p, spot))
                {
                    bestDist = d;
                    best = spot;
                }
            }

            if (best == null)
            {
                return false;
            }

            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(BotOwner.Position, best.OutsidePos.Value, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                _done.Add(best.Id);
                return false;
            }

            _target = best;
            _stand = best.OutsidePos.Value;
            _giveUpAt = Time.time + 90f;
            Busy.Add(BotOwner.ProfileId);
            Went++;
            Log.Info($"LR Container: {p.Name} goes to open {best.Id} with its key ({Mathf.Sqrt(bestDist):0} m away)");
            return true;
        }

        public override Action GetNextAction() => new Action(typeof(UnlockContainerLogic), "unlock container");

        public override bool IsCurrentActionEnding() => _target == null || !StillWorking();

        private float _openUntil;
        private float _scanAt;

        /// <summary>Locked still - or a door just unlocked that has yet to be pushed open (4 s for it).</summary>
        private bool StillWorking()
        {
            if (_target.StillLocked)
            {
                _openUntil = 0f;
                return true;
            }

            if (!(_target.Object is Door door) || door.DoorState != EDoorState.Shut)
            {
                return false;
            }

            if (_openUntil <= 0f)
            {
                _openUntil = Time.time + 4f;
            }

            return Time.time < _openUntil;
        }

        public override void Start()
        {
            BotOwner.PatrollingData.Pause();
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData.Unpause();
            base.Stop();
        }

        internal LockedSpot Target => _target;

        internal Vector3 Stand => _stand;

        internal void Finished() => Drop();
    }

    public class UnlockContainerLogic : CustomLogic
    {
        private float _nextOrder;
        private float _interactedAt;
        private bool _bundleRequested;
        private bool _opened;

        public UnlockContainerLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Diag.Report("ContainerUnlockLogic", e);
                FindLayer()?.Finished();
            }
        }

        private void Step()
        {
            ContainerUnlockLayer layer = FindLayer();
            LockedSpot spot = layer?.Target;
            if (spot == null || spot.Object == null)
            {
                return;
            }

            Vector3 stand = layer.Stand;
            float dist = Vector3.Distance(BotOwner.Position, stand);
            if (dist > 1.6f)
            {
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 1f;
                    BotOwner.SetPose(1f);
                    BotOwner.SetTargetMoveSpeed(0.9f);
                    BotOwner.Sprint(dist > 40f);
                    BotOwner.GoToPoint(stand, true, 0.8f, false, false);
                }

                BotOwner.Steering.LookToMovingDirection();
                return;
            }

            BotOwner.StopMove();
            BotOwner.Steering.LookToPoint(spot.Object.transform.position);
            if (_interactedAt > 0)
            {
                // A door is pushed open once the key has turned (a container is looted shut as it is)
                if (!_opened && spot.Object is Door door && door.DoorState == EDoorState.Shut && Time.time - _interactedAt > 1.5f)
                {
                    _opened = DoorAssistLayer.OpenNow(BotOwner, door);
                }

                // Wait for the animation to finish; the layer notices the state change
                if (Time.time - _interactedAt > 10f)
                {
                    layer.Finished();
                }

                return;
            }

            KeyComponent key = BotOwner.FindKeyComponent(spot.Object);
            if (key == null)
            {
                layer.Finished();
                return;
            }

            // The unlock animation fails when the key's model has not been loaded yet
            if (!key.Item.IsBundleLoaded())
            {
                if (!_bundleRequested)
                {
                    _bundleRequested = true;
                    key.Item.LoadBundle();
                }

                return;
            }

            InteractionResult result = spot.Object.GetInteractionResult(EInteractionType.Unlock, BotOwner, key);
            BotOwner.InteractWithWorldInteractiveObject(spot.Object, result);
            _interactedAt = Time.time;
            Log.Info($"LR Container: {BotOwner.Profile.Nickname} unlocks {spot.Id}");
            Tactics.RoomSweep.ForceScan(BotOwner);
        }

        private ContainerUnlockLayer FindLayer()
        {
            // BigBrain does not expose the layer instance to its logic; the active layer is looked up by name
            return LayerRegistry.Get(BotOwner);
        }
    }

    /// <summary>Remembers each bot's container layer so its logic can reach it.</summary>
    internal static class LayerRegistry
    {
        private static readonly Dictionary<string, ContainerUnlockLayer> _layers = new Dictionary<string, ContainerUnlockLayer>();

        public static void Register(BotOwner bot, ContainerUnlockLayer layer) => _layers[bot.ProfileId] = layer;

        /// <summary>The layers of a raid that is over hold its bots (and through them the whole unloaded map) in memory: dropped with the raid.</summary>
        public static void Clear() => _layers.Clear();

        public static ContainerUnlockLayer Get(BotOwner bot) => _layers.TryGetValue(bot.ProfileId, out ContainerUnlockLayer l) ? l : null;
    }
}
