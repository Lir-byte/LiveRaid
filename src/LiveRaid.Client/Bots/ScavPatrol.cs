using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// AI scavs as online (F12 -> 3 "Scavs patrol their area"). Online the game's scavs do not walk the whole map: a group keeps to the area it
    /// came in, goes round it, stops a moment at its spots, goes to look at noise and shooting nearby and calls the others of the area into a fight.
    /// LiveRaid's map-wide quests had them roam like PMCs. Here:
    ///  - each AI scav group gets an area: its spawn point and a radius of 70-120 m (explorers a little more, rats less); QuestingBots only hands it
    ///    quest objectives inside it (BotQuest / BotQuestObjective.CanAssignBot), so it walks from spot to spot of its area with the short stops of
    ///    LiveRaid's exploration quests, and comes back to it after going to a noise or a fight (GunfireResponse, NoiseResponse);
    ///  - an area with nothing to do in it (no objective for 25 s) grows by a third, up to 260 m;
    ///  - a scav in a fight calls its neighbours: calm scav groups of the area within 130 m (up to two) come to look (NeedHelp call);
    ///  - not sent away from its area for staying long (RoamPlanner) - staying in its area is what it does.
    /// Player scavs are not touched (they play like people). Off: the old "Scavs roam the map".
    /// </summary>
    public static class ScavPatrol
    {
        public class Area
        {
            public Vector3 Home;
            public float Radius;
            public float IdleSince = -1f;
            public int Grown;
        }

        private static readonly Dictionary<int, Area> _areas = new Dictionary<int, Area>();
        private static readonly Dictionary<string, float> _calledAt = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Areas => _areas.Count;
        public static int Grown { get; private set; }
        public static int Calls { get; private set; }
        public static int Answered { get; private set; }
        public static int Refused { get; private set; }

        public static string Summary =>
            $"scav patrols: {Areas} areas (radius {(_areas.Count > 0 ? _areas.Values.Average(a => a.Radius) : 0f):0} m on average, grown for lack of work {Grown}), " +
            $"objective checks refused as outside the area {Refused}, calls for help {Calls}, groups that came {Answered}";

        public static void Clear()
        {
            _areas.Clear();
            _calledAt.Clear();
            _next = 0f;
            Grown = Calls = Answered = Refused = 0;
        }

        public static bool On => Settings.Enabled.Value && Settings.ScavPatrol.Value;

        /// <summary>An AI scav (not a player scav) under the patrol rule.</summary>
        public static bool Applies(BotOwner bot, out BotProfile p)
        {
            p = bot != null ? BotProfiles.Get(bot) : null;
            return On && p != null && p.Kind == BotKind.Scav;
        }

        public static Area AreaOf(BotOwner bot, BotProfile p)
        {
            int squad = SquadTies.SquadOf(bot.ProfileId);
            if (!_areas.TryGetValue(squad, out Area a))
            {
                float r = 70f + (float)_rng.NextDouble() * 50f;
                if (p.Style == PlayStyle.Explorer || p.Style == PlayStyle.Rusher)
                {
                    r += 20f;
                }
                else if (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper)
                {
                    r -= 15f;
                }

                Vector3 home = p.SpawnPos != Vector3.zero ? p.SpawnPos : bot.Position;
                _areas[squad] = a = new Area { Home = home, Radius = r };
                Log.Info($"LR Scav: {p.Name}'s group patrols its area: {r:0} m round ({home.x:0}, {home.z:0})");
            }

            return a;
        }

        public static bool Inside(Area a, Vector3 pos)
        {
            Vector3 d = pos - a.Home;
            d.y = 0f;
            return d.sqrMagnitude <= a.Radius * a.Radius;
        }

        /// <summary>QuestingBots asks whether an objective may go to the bot: an AI scav takes only those of its area.</summary>
        public static bool Allows(BotOwner bot, Vector3? position)
        {
            if (!Applies(bot, out BotProfile p) || position == null)
            {
                return true;
            }

            bool inside = Inside(AreaOf(bot, p), position.Value);
            if (!inside)
            {
                Refused++;
            }

            return inside;
        }

        /// <summary>QuestingBots asks whether a quest may go to the bot: an AI scav needs at least one of its objectives in its area.</summary>
        public static bool HasWork(BotOwner bot, BotQuest quest)
        {
            if (!Applies(bot, out BotProfile p))
            {
                return true;
            }

            Area a = AreaOf(bot, p);
            return quest.GetValidObjectives().Any(o => o.GetFirstStepPosition() is Vector3 at && Inside(a, at));
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            List<BotOwner> scavs = new List<BotOwner>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot != null && !bot.IsDead && Applies(bot, out _) && !Spectator.TakeOver.IsTaken(bot))
                {
                    scavs.Add(bot);
                }
            }

            foreach (BotOwner bot in scavs)
            {
                BotProfile p = BotProfiles.Get(bot);
                if (Tactics.TacticalTasks.SquadLeaderOf(bot) == null)
                {
                    Grow(bot, p);
                }

                CallForHelp(bot, p, scavs);
            }
        }

        /// <summary>Nothing to do in its area for 25 s while calm: the area grows by a third (up to 260 m).</summary>
        private static void Grow(BotOwner bot, BotProfile p)
        {
            Area a = AreaOf(bot, p);
            BotObjectiveManager objectives = bot.GetObjectiveManager();
            bool idle = objectives != null && objectives.IsQuestingAllowed && !objectives.IsJobAssignmentActive && Tactics.TacticalTasks.BotIsCalm(bot);
            if (!idle)
            {
                a.IdleSince = -1f;
                return;
            }

            if (a.IdleSince < 0f)
            {
                a.IdleSince = Time.time;
                return;
            }

            if (Time.time - a.IdleSince < 18f || a.Radius >= 260f)
            {
                return;
            }

            a.Radius = Mathf.Min(260f, a.Radius * 1.33f);
            a.IdleSince = -1f;
            a.Grown++;
            Grown++;
            Log.Info($"LR Scav: {p.Name}'s group found nothing to do in its area - it grows to {a.Radius:0} m");
        }

        /// <summary>A scav in a fight calls the calm scav groups of its area within 130 m: up to two come to look.</summary>
        private static void CallForHelp(BotOwner bot, BotProfile p, List<BotOwner> scavs)
        {
            if (!Tactics.HitReaction.InRealFight(bot, 10f))
            {
                return;
            }

            float now = Time.time;
            int squad = SquadTies.SquadOf(bot.ProfileId);
            string key = squad.ToString();
            if (_calledAt.TryGetValue(key, out float at) && now - at < 60f)
            {
                return;
            }

            _calledAt[key] = now;
            Vector3 fight = bot.Position;
            int came = 0;
            HashSet<int> asked = new HashSet<int> { squad };
            foreach (BotOwner other in scavs.OrderBy(s => (s.Position - fight).sqrMagnitude))
            {
                if (came >= 2)
                {
                    break;
                }

                int otherSquad = SquadTies.SquadOf(other.ProfileId);
                if (asked.Contains(otherSquad) || Tactics.TacticalTasks.SquadLeaderOf(other) != null || (other.Position - fight).sqrMagnitude > 130f * 130f
                    || !Tactics.TacticalTasks.BotIsCalm(other) || Tactics.TacticalTasks.TryGet(other, out _))
                {
                    continue;
                }

                asked.Add(otherSquad);
                Vector3? stand = Short(other, fight, 20f + (float)_rng.NextDouble() * 20f);
                if (stand == null)
                {
                    continue;
                }

                Tactics.TacticalTask task = new Tactics.TacticalTask
                {
                    Kind = Tactics.TaskKind.Investigate, StandAt = stand, LookAt = fight, Speed = 1f, Sprint = true, SprintBeyond = 40f, MovePose = 1f,
                    Until = now + 45f,
                };
                if (Tactics.TacticalTasks.Set(other, task))
                {
                    came++;
                    Answered++;
                }
            }

            Calls++;
            Tactics.SquadBrain.Say(bot, EPhraseTrigger.NeedHelp, 0.6f);
            Log.Info($"LR Scav: {p.Name} is in a fight and calls the others of its area - {came} group(s) come");
        }

        /// <summary>A reachable spot of the bot's area 20 m or more from it (a patrol scav with nothing to do goes there), or null.</summary>
        public static Vector3? PointInArea(BotOwner bot, BotProfile p, out float pathLength)
        {
            pathLength = 0f;
            Area a = AreaOf(bot, p);
            NavMeshPath path = new NavMeshPath();
            for (int i = 0; i < 12; i++)
            {
                float angle = (float)_rng.NextDouble() * Mathf.PI * 2f;
                float r = (float)_rng.NextDouble() * a.Radius;
                Vector3 want = a.Home + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * r;
                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 6f, NavMesh.AllAreas) || (hit.position - bot.Position).sqrMagnitude < 20f * 20f
                    || !NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                for (int k = 1; k < path.corners.Length; k++)
                {
                    pathLength += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                }

                return hit.position;
            }

            return null;
        }

        /// <summary>A reachable point <paramref name="shortBy"/> m short of the place on the way from the bot.</summary>
        public static Vector3? Short(BotOwner bot, Vector3 place, float shortBy)
        {
            Vector3 dir = bot.Position - place;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1f)
            {
                return null;
            }

            Vector3 want = place + dir.normalized * Mathf.Min(shortBy, dir.magnitude * 0.8f);
            if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            {
                return null;
            }

            NavMeshPath path = new NavMeshPath();
            return NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete
                ? hit.position
                : (Vector3?)null;
        }
    }

    /// <summary>QuestingBots objective for an AI scav: only inside its patrol area (ScavPatrol).</summary>
    public class QbScavObjectivePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuestObjective), nameof(BotQuestObjective.CanAssignBot));

        [PatchPostfix]
        protected static void PatchPostfix(BotQuestObjective __instance, BotOwner bot, ref bool __result)
        {
            try
            {
                if (__result && bot != null)
                {
                    __result = ScavPatrol.Allows(bot, __instance.GetFirstStepPosition());
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("QbScavObjectivePatch", e);
            }
        }
    }
}
