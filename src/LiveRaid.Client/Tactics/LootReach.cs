using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LootingBots.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Loot is gone to from its own side of the wall (F12 -> 7 "Loot from the right side of the wall"). LootingBots puts the spot a bot loots from at
    /// the navmesh point nearest the thing (within 1 m), pushed 1.5 m further off - for a cabinet against a thin wall the nearest navmesh is often in the
    /// next room. The path there was not checked to the end either (EFT's CalcPath gives the corners of a partial path too): bots walked up to the wall,
    /// stood 2-3 m from the spot and gave the thing up (raid 2026-10-31 15:59: 12 times, "cannot get to ... (2.3-3.0 m, 0.0 m up/down)"), or went round
    /// into the next room and looted through the wall. Here a spot is good when nothing solid stands between it (a crouching man's eyes) and the thing,
    /// and - for a spot LootingBots' own scan found - when the path from the bot gets there (or ends at a door the bot can open). A bad spot is replaced by
    /// one on the thing's own side (0.8-1.3 m round it); with none the thing is left out of the scan.
    /// </summary>
    public static class LootReach
    {
        private struct Answer
        {
            public Vector3 Dest;
            public float Until;
        }

        // Per bot and thing (its point rounded to half a metre): LootingBots' scan asks for the same things every few seconds
        private static readonly Dictionary<string, Answer> _cache = new Dictionary<string, Answer>();
        private static readonly NavMeshPath _path = new NavMeshPath();

        public static int Moved { get; private set; }
        public static int Dropped { get; private set; }
        public static int WallAtHand { get; private set; }

        public static string Summary => $"loot spots moved to the thing's side of the wall {Moved}, things left out (no way to their side) {Dropped}, looting through a wall refused {WallAtHand}, of them behind a locked door {LockedDoors}";

        public static void Clear()
        {
            _cache.Clear();
            Moved = Dropped = WallAtHand = LockedDoors = 0;
        }

        public static bool On => Settings.Enabled.Value && Settings.LootRightSide.Value;

        /// <summary>
        /// Something solid between a crouching man's eyes at <paramref name="from"/> and the thing at <paramref name="center"/> (LootingBots' point: the
        /// thing's position, for a container the bottom of its box 0.4 m low). Hits within 0.7 m of the thing are the thing itself (a cabinet's side).
        /// </summary>
        public static bool WallBetween(Vector3 from, Vector3 center, float eyes = 1f)
        {
            Vector3 eye = from + Vector3.up * eyes;
            foreach (float h in new[] { 0.5f, 1f })
            {
                Vector3 to = center + Vector3.up * h;
                Vector3 d = to - eye;
                float len = d.magnitude;
                if (len < 0.8f || !Physics.Raycast(eye, d / len, out RaycastHit hit, len, LayersMaskController.HighPolyWithTerrainMask) || hit.distance >= len - 0.7f)
                {
                    // No wall in the mask of solid things - but a closed door that is LOCKED is a wall too (1.3.0: at the military checkpoint on Customs a PMC looted a thing
                    // through a locked door: the door is not in the solid mask, the spot outside it was "clear", and a locked door never opens for the unstick layer)
                    return LockedDoorBetween(eye, to);
                }
            }

            return true;
        }

        private static readonly RaycastHit[] _doorHits = new RaycastHit[8];

        /// <summary>A door on the line from <paramref name="eye"/> to <paramref name="to"/> that is locked (a bot that has its key opens it by its own errand, not by looting through it).</summary>
        private static bool LockedDoorBetween(Vector3 eye, Vector3 to)
        {
            Vector3 d = to - eye;
            float len = d.magnitude;
            if (len < 0.8f)
            {
                return false;
            }

            int n = Physics.RaycastNonAlloc(eye, d / len, _doorHits, len, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                EFT.Interactive.Door door = _doorHits[i].collider != null ? _doorHits[i].collider.GetComponentInParent<EFT.Interactive.Door>() : null;
                if (door != null && door.DoorState == EFT.Interactive.EDoorState.Locked)
                {
                    LockedDoors++;
                    return true;
                }
            }

            return false;
        }

        public static int LockedDoors { get; private set; }

        /// <summary>The spot a bot should loot <paramref name="center"/> from: <paramref name="dest"/> when it is good, another one, or Vector3.zero.</summary>
        public static Vector3 Check(BotOwner bot, Vector3 center, Vector3 dest)
        {
            if (!On || dest == Vector3.zero)
            {
                return dest;
            }

            string key = null;
            float now = Time.time;
            if (bot != null)
            {
                key = $"{bot.ProfileId}@{Mathf.Round(center.x * 2f)},{Mathf.Round(center.y * 2f)},{Mathf.Round(center.z * 2f)}";
                if (_cache.TryGetValue(key, out Answer a) && now < a.Until)
                {
                    return a.Dest;
                }
            }

            NavMeshHit fromHit = default;
            bool haveFrom = bot != null && NavMesh.SamplePosition(bot.Position, out fromHit, 1.5f, NavMesh.AllAreas);
            Vector3 from = haveFrom ? fromHit.position : Vector3.zero;
            Vector3 result = Decide(bot, center, dest, haveFrom, from);
            if (key != null)
            {
                if (_cache.Count > 4000)
                {
                    _cache.Clear();
                }

                _cache[key] = new Answer { Dest = result, Until = now + 15f };
            }

            return result;
        }

        private static Vector3 Decide(BotOwner bot, Vector3 center, Vector3 dest, bool haveFrom, Vector3 from)
        {
            bool wall = WallBetween(dest, center);
            bool way = !haveFrom || WayThere(from, dest);
            if (!wall && way)
            {
                return dest;
            }

            Vector3? other = OtherSide(center, haveFrom, from, bot != null ? bot.Position : dest);
            if (other != null)
            {
                Moved++;
                Log.Detail("loot", () => $"{bot?.Profile?.Nickname ?? "a room visit"}: the spot for the thing at ({center.x:0.0}, {center.z:0.0}) " +
                                         $"{(wall ? "was behind a wall from it" : "could not be walked to")} - loots it from ({other.Value.x:0.0}, {other.Value.z:0.0})");
                return other.Value;
            }

            // A thing it can see from the spot it cannot walk to may still lie behind a door the bot can open: LootingBots goes, the unstick layer opens it
            if (!wall)
            {
                return dest;
            }

            Dropped++;
            Log.Detail("loot", () => $"{bot?.Profile?.Nickname ?? "a room visit"}: the thing at ({center.x:0.0}, {center.z:0.0}) can only be reached through a wall - left out");
            return Vector3.zero;
        }

        /// <summary>The path from the bot gets to the spot, or stops at a door that can be opened (behind it the way goes on).</summary>
        private static bool WayThere(Vector3 from, Vector3 dest)
        {
            if (!NavMesh.CalculatePath(from, dest, NavMesh.AllAreas, _path))
            {
                return false;
            }

            if (_path.status == NavMeshPathStatus.PathComplete)
            {
                return true;
            }

            Vector3[] corners = _path.corners;
            if (_path.status != NavMeshPathStatus.PathPartial || corners.Length == 0)
            {
                return false;
            }

            EFT.Interactive.Door door = Bots.DoorAssistLayer.NearestDoor(corners[corners.Length - 1], 2.5f);
            return door != null && door.DoorState != EFT.Interactive.EDoorState.Locked;
        }

        private static readonly List<(Vector3 pos, float d)> _candidates = new List<(Vector3, float)>();

        /// <summary>A spot 0.8-1.3 m round the thing, on about the same floor, that sees it and (when the bot is known) that it can walk to.</summary>
        private static Vector3? OtherSide(Vector3 center, bool haveFrom, Vector3 from, Vector3 botPos)
        {
            _candidates.Clear();
            Vector3 floor = center + Vector3.up * 0.4f;
            foreach (float r in new[] { 0.8f, 1.3f })
            {
                for (int k = 0; k < 10; k++)
                {
                    Vector3 want = floor + Quaternion.Euler(0f, k * 36f, 0f) * Vector3.forward * r;
                    if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 0.6f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - floor.y) > 1f
                        || WallBetween(hit.position, center))
                    {
                        continue;
                    }

                    _candidates.Add((hit.position, (hit.position - botPos).sqrMagnitude));
                }
            }

            if (_candidates.Count == 0)
            {
                return null;
            }

            _candidates.Sort((a, b) => a.d.CompareTo(b.d));
            if (!haveFrom)
            {
                return _candidates[0].pos;
            }

            for (int i = 0; i < _candidates.Count && i < 4; i++)
            {
                if (NavMesh.CalculatePath(from, _candidates[i].pos, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
                {
                    return _candidates[i].pos;
                }
            }

            return null;
        }

        /// <summary>Stalled near its spot, the bot would loot from where it stands: not with a wall between it and the thing.</summary>
        public static bool RefuseFromHere(BotOwner bot, Vector3 lootPos)
        {
            if (!On || bot == null || lootPos == Vector3.zero || !WallBetween(bot.Position, lootPos - Vector3.up * 0.4f, 1.2f))
            {
                return false;
            }

            WallAtHand++;
            return true;
        }
    }

    /// <summary>LootingBots' spot for a thing its scan found is checked (and moved or dropped) before the scan weighs it - <see cref="LootReach"/>.</summary>
    public class LbLootSpotPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<LootFinder, BotOwner> BotField = AccessTools.FieldRefAccess<LootFinder, BotOwner>("_botOwner");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootFinder), "GetDestination");

        [PatchPostfix]
        protected static void PatchPostfix(LootFinder __instance, Vector3 center, ref Vector3 __result)
        {
            try
            {
                if (LootReach.On && __result != Vector3.zero)
                {
                    __result = LootReach.Check(BotField(__instance), center, __result);
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("LbLootSpotPatch", e);
            }
        }
    }
}
