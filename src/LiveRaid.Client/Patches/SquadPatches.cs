using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots followers all run to the leader's position - and walk into him and into each other, then stand pressed together.
    /// Here each follower aims for its own slot: pointman ahead, riflemen on the flanks, rear guard behind, overwatch far back; in
    /// buildings a column with a gap between men. Loose squads keep no formation, but still a place of their own a few metres off the
    /// leader (never his exact spot). With no navmesh at the slot, the follower stops 2.5 m short of the leader on its own side.
    /// </summary>
    public class QbFormationPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            System.Type action = AccessTools.TypeByName("QuestingBots.BehaviorExtensions.GoToPositionAbstractAction");
            System.Type reason = AccessTools.TypeByName("QuestingBots.Models.Pathing.BotPathUpdateNeededReason");
            return AccessTools.Method(action, "RecalculatePath",
                new[] { typeof(Vector3?), typeof(float), typeof(float), typeof(bool), reason.MakeByRefType() });
        }

        [PatchPrefix]
        protected static void PatchPrefix(object __instance, ref Vector3? position)
        {
            try
            {
                RunPrefix(__instance, ref position);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbFormationPatch", e);
            }
        }

        private static void RunPrefix(object __instance, ref Vector3? position)
        {
            if (!Settings.SquadTactics.Value || !Settings.SquadFormation.Value || position == null || __instance.GetType().Name != "FollowBossAction")
            {
                return;
            }

            BotOwner bot = Traverse.Create(__instance).Property("BotOwner").GetValue<BotOwner>();
            BotOwner boss = bot != null ? QuestingBots.BotLogic.HiveMind.BotHiveMindMonitor.GetGroupLeader(bot) : null;
            if (boss == null || boss == bot)
            {
                return;
            }

            List<BotOwner> followers = boss.Boss?.Followers;
            int index = followers != null ? Mathf.Max(0, followers.IndexOf(bot)) : 0;

            // Loose squads keep no formation, just a spot of their own near the leader
            float discipline = SquadBrain.DisciplineOf(bot);
            bool loose = discipline >= 0 && discipline < 0.45f;
            Vector3? slot = loose ? Formation.LooseSpot(boss, bot, index) : Formation.SlotFor(boss, bot, SquadBrain.RoleOf(bot), index);
            position = slot ?? Formation.ShortOfLeader(boss, bot);
        }
    }

    public static class Formation
    {
        // Heading of each leader from where he actually walked, so slots don't swing every time he looks around
        private static readonly Dictionary<string, (Vector3 lastPos, Vector3 heading)> _headings = new Dictionary<string, (Vector3, Vector3)>();

        private static readonly Dictionary<string, (Vector3 lastPos, Vector3 heading)> _slowHeadings = new Dictionary<string, (Vector3, Vector3)>();

        // Where each leader last was under the open sky and when he went in (a rear man holds the street a moment - doctrine squads)
        private static readonly Dictionary<string, (Vector3 pos, float wentInAt, bool inside)> _entrances = new Dictionary<string, (Vector3, float, bool)>();

        // How open the leader's spot is, looked at every 2 s (several raycasts - not for every slot query)
        private static readonly Dictionary<string, (float at, float open)> _openness = new Dictionary<string, (float, float)>();

        public static int StreetHolds { get; private set; }

        public static void Clear()
        {
            _headings.Clear();
            _slowHeadings.Clear();
            _entrances.Clear();
            _openness.Clear();
            StreetHolds = 0;
        }

        private static float OpennessAt(BotOwner boss)
        {
            if (_openness.TryGetValue(boss.ProfileId, out var o) && Time.time - o.at < 2f)
            {
                return o.open;
            }

            float open = Tactics.LootBrain.Openness(boss.Position);
            _openness[boss.ProfileId] = (Time.time, open);
            return open;
        }

        /// <summary>The leader went in less than 15 s ago: where he went in (the street a rear man keeps an eye on).</summary>
        private static bool WentInRecently(BotOwner boss, bool indoors, out Vector3 entrance)
        {
            _entrances.TryGetValue(boss.ProfileId, out var e);
            if (!indoors)
            {
                _entrances[boss.ProfileId] = (boss.Position, 0f, false);
                entrance = default;
                return false;
            }

            if (!e.inside)
            {
                e = (e.pos == Vector3.zero ? boss.Position : e.pos, Time.time, true);
                _entrances[boss.ProfileId] = e;
            }

            entrance = e.pos;
            return Time.time - e.wentInAt < 15f && e.pos != Vector3.zero;
        }

        private static Vector3 Heading(BotOwner boss)
        {
            Vector3 pos = boss.Position;
            if (!_headings.TryGetValue(boss.ProfileId, out var h))
            {
                Vector3 look = boss.LookDirection;
                look.y = 0;
                h = (pos, look.sqrMagnitude > 0.01f ? look.normalized : Vector3.forward);
            }

            Vector3 moved = pos - h.lastPos;
            moved.y = 0;
            if (moved.sqrMagnitude > 9f)
            {
                h = (pos, Vector3.Slerp(h.heading, moved.normalized, 0.6f).normalized);
            }

            _headings[boss.ProfileId] = h;
            return h.heading;
        }

        /// <summary>
        /// The heading for the overwatch slot (20 m behind the leader): it follows the leader's real course only after 8 m of walking and turns slowly. With the normal heading
        /// a leader turning back (to loot, to help) swung the slot from 20 m behind to 20 m ahead, and the marksman ran back and forth across the leader.
        /// </summary>
        private static Vector3 SlowHeading(BotOwner boss)
        {
            Vector3 pos = boss.Position;
            if (!_slowHeadings.TryGetValue(boss.ProfileId, out var h))
            {
                h = (pos, Heading(boss));
            }

            Vector3 moved = pos - h.lastPos;
            moved.y = 0;
            if (moved.sqrMagnitude > 64f)
            {
                h = (pos, Vector3.Slerp(h.heading, moved.normalized, 0.35f).normalized);
            }

            _slowHeadings[boss.ProfileId] = h;
            return h.heading;
        }

        /// <summary>A loose squad member: 3-4 m behind and to one side of the leader (sides alternate), never his exact spot.</summary>
        public static Vector3? LooseSpot(BotOwner boss, BotOwner follower, int index)
        {
            Vector3 forward = Heading(boss);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float side = index % 2 == 0 ? 1f : -1f;
            Vector3 want = boss.Position - forward * (3f + index) + right * 2.5f * side;
            return NavMesh.SamplePosition(want, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - boss.Position.y) < 1.5f
                ? hit.position
                : (Vector3?)null;
        }

        /// <summary>No slot on the navmesh: 2.5 m short of the leader on the follower's own side (so it does not walk into him).</summary>
        public static Vector3 ShortOfLeader(BotOwner boss, BotOwner follower)
        {
            Vector3 from = follower.Position - boss.Position;
            from.y = 0f;
            if (from.sqrMagnitude < 0.01f)
            {
                from = -Heading(boss);
            }

            Vector3 want = boss.Position + from.normalized * 2.5f;
            return NavMesh.SamplePosition(want, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) ? hit.position : boss.Position;
        }

        public static Vector3? SlotFor(BotOwner boss, BotOwner follower, SquadRole role, int index)
        {
            Vector3 forward = role == SquadRole.Overwatch ? SlowHeading(boss) : Heading(boss);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 origin = boss.Position;
            bool indoors = Physics.Raycast(origin + Vector3.up * 1.6f, Vector3.up, 6f, LayersMaskController.HighPolyWithTerrainMask);

            Vector3 offset;
            Tactics.DoctrineDeck.Squad doctrine = Tactics.SquadDoctrines.SquadOf(follower);
            bool wentIn = WentInRecently(boss, indoors, out Vector3 entrance);
            if (indoors && wentIn && role == SquadRole.Rear && doctrine != null && doctrine.Values[Tactics.DoctrineDeck.Cohesion] >= 0.45f)
            {
                // Going into a building: the rear man keeps the street a few seconds at the door, then follows
                if (NavMesh.SamplePosition(entrance, out NavMeshHit door, 2f, NavMesh.AllAreas))
                {
                    if ((follower.Position - door.position).sqrMagnitude > 4f)
                    {
                        StreetHolds++;
                    }

                    return door.position;
                }
            }

            if (indoors)
            {
                // Tight column in corridors and rooms
                offset = role == SquadRole.Pointman ? forward * 3f : -forward * (2.5f * (index + 1));
            }
            else
            {
                float side = index % 2 == 0 ? 1f : -1f;
                switch (role)
                {
                    case SquadRole.Pointman: offset = forward * 8f + right * 2f * side; break;
                    case SquadRole.Overwatch: offset = -forward * 20f + right * 6f * side; break;
                    case SquadRole.Rear: offset = -forward * 9f; break;
                    default: offset = -forward * 4f + right * 6f * side; break;
                }

                // The squad's doctrine: its formation on open ground and how far apart (a tight clan, randoms all over)
                if (doctrine != null)
                {
                    float spacing = 1.6f - doctrine.Values[Tactics.DoctrineDeck.Cohesion];
                    switch (doctrine.Shape)
                    {
                        case Tactics.SquadShape.Line:
                            offset = role == SquadRole.Overwatch ? offset : right * side * 6f * (index / 2 + 1) - forward * 1.5f;
                            break;
                        case Tactics.SquadShape.Pairs:
                            // One beside the leader, the rest a second pair some 30 m behind
                            offset = index == 0 ? right * 3f * side - forward * 1.5f : -forward * (26f + 4f * (index % 2)) + right * 3f * side;
                            break;
                        case Tactics.SquadShape.Scout:
                            if (role == SquadRole.Pointman)
                            {
                                offset = forward * 40f + right * 4f * side;
                            }

                            break;
                    }

                    offset *= spacing;
                }

                // Open ground (a field, a car park): a squad spreads out so one burst or grenade does not take two
                if (OpennessAt(boss) > 0.75f)
                {
                    offset *= 1.4f;
                }
            }

            // Less disciplined squads hold their slots sloppily
            float discipline = SquadBrain.DisciplineOf(follower);
            if (discipline >= 0)
            {
                float sloppy = (1f - discipline) * 4f;
                int seed = follower.ProfileId.GetHashCode();
                offset += new Vector3(((seed & 0xff) / 255f - 0.5f) * sloppy, 0, (((seed >> 8) & 0xff) / 255f - 0.5f) * sloppy);
            }

            Vector3 want = origin + offset;
            if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - origin.y) > 2f)
            {
                return null;
            }

            // A slot that landed on top of the leader (narrow places) is no slot
            Vector3 d = hit.position - origin;
            d.y = 0f;
            return d.sqrMagnitude < 1.8f * 1.8f ? (Vector3?)null : hit.position;
        }
    }
}
