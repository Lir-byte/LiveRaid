using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using QuestingBots.Models;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Models.Questing;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Once QuestingBots has built its quests, checks every EFT quest objective of this map: did QuestingBots find a navmesh point
    /// for it, can bots walk there, and does that point stand on the real spot (not a floor below or outside the room)?
    /// Writes questaccess/&lt;map&gt;.csv and an "LR Quests on &lt;map&gt;" summary to the log.
    /// </summary>
    public static class QuestAudit
    {
        // A bot standing further than this from the zone centre / quest item is "off target" (other floor, outside the room)
        private const float OffTarget = 4f;

        private static IEnumerator _job;
        private static bool _done;

        public static void Clear()
        {
            _job = null;
            _done = false;
            _qbOverrides = null;
        }

        public static void Tick()
        {
            if (_done || !Settings.QuestAudit.Value)
            {
                return;
            }

            MapIntel intel = MapIntel.Current;
            if (_job == null)
            {
                GameWorld world = Singleton<GameWorld>.Instance;
                BotQuestBuilder builder = world == null ? null : world.GetComponent<BotQuestBuilder>();
                if (intel == null || !intel.Ready || builder == null || !builder.HaveQuestsBeenBuilt)
                {
                    return;
                }

                _job = Run(intel);
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3)
            {
                if (!_job.MoveNext())
                {
                    _job = null;
                    _done = true;
                    return;
                }
            }
        }

        private class Row
        {
            public string Quest, QuestId, Objective, Status, OverrideKey;
            public Vector3? Point, Target;
            public Bounds? Zone;
            public BotQuestObjective Source;
        }

        // Zones and items whose bot position QuestingBots sets by hand (zoneAndItemQuestPositions.json): far from the spot on purpose
        private static HashSet<string> _qbOverrides;

        private static HashSet<string> QbOverrides()
        {
            if (_qbOverrides != null)
            {
                return _qbOverrides;
            }

            _qbOverrides = new HashSet<string>();
            try
            {
                string path = Path.Combine(BepInEx.Paths.GameRootPath, "SPT_Runtime", "user", "mods", "QuestingBots", "zoneAndItemQuestPositions.json");
                if (File.Exists(path))
                {
                    foreach (JProperty prop in JObject.Parse(File.ReadAllText(path)).Properties())
                    {
                        _qbOverrides.Add(prop.Name);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug("quest audit: QuestingBots position overrides unreadable: " + e.Message);
            }

            return _qbOverrides;
        }

        private static IEnumerator Run(MapIntel intel)
        {
            List<BotQuest> quests;
            try
            {
                quests = (AccessTools.Field(typeof(BotJobAssignmentController), "allQuests")?.GetValue(null) as List<BotQuest>)?.ToList() ?? new List<BotQuest>();
            }
            catch (Exception e)
            {
                Log.Warn("quest audit: cannot read QuestingBots quests: " + e.Message);
                yield break;
            }

            // Zones that exist on this map; zone objectives of other maps are skipped
            Dictionary<string, Bounds> triggers = new Dictionary<string, Bounds>();
            foreach (TriggerWithId trigger in UnityEngine.Object.FindObjectsOfType<TriggerWithId>())
            {
                Collider c = trigger == null ? null : trigger.GetComponent<Collider>();
                if (c != null && !string.IsNullOrEmpty(trigger.Id) && !triggers.ContainsKey(trigger.Id))
                {
                    triggers[trigger.Id] = c.bounds;
                }
            }

            yield return null;

            List<Row> rows = new List<Row>();
            int eftQuests = quests.Count(q => q.IsEFTQuest);
            foreach (BotQuest quest in quests.Where(q => q.IsEFTQuest))
            {
                foreach (BotQuestObjective objective in quest.AllObjectives)
                {
                    Row row = new Row { Quest = quest.GetName(), QuestId = quest.Template?.Id ?? "", Source = objective };
                    if (objective is BotQuestZoneObjective zone)
                    {
                        if (zone.ZoneID == null || !triggers.TryGetValue(zone.ZoneID, out Bounds bounds))
                        {
                            continue;
                        }

                        row.Objective = "zone " + zone.ZoneID;
                        row.OverrideKey = zone.ZoneID;
                        row.Target = bounds.center;
                        row.Zone = bounds;
                    }
                    else if (objective is BotQuestItemObjective item)
                    {
                        row.Objective = "item " + (item.Item?.Item?.LocalizedName() ?? "?");
                        row.OverrideKey = item.Item?.Item?.TemplateId.ToString();
                        row.Target = item.Item != null ? item.Item.transform.position : (Vector3?)null;
                    }
                    else
                    {
                        continue;
                    }

                    row.Point = objective.StepCount > 0 ? objective.GetFirstStepPosition() : null;
                    row.Status = Classify(intel, row);
                    rows.Add(row);
                    yield return null;
                }
            }

            Write(intel.LocationId, rows);
            int ok = rows.Count(r => r.Status.StartsWith("ok"));
            int off = rows.Count(r => r.Status.StartsWith("off target"));
            int unreachable = rows.Count(r => r.Status == "unreachable");
            int locked = rows.Count(r => r.Status.StartsWith("behind locked door"));
            int noPoint = rows.Count(r => r.Status == "no navmesh point");
            Log.Info($"LR Quests on {intel.LocationId}: {eftQuests} EFT quests loaded, {rows.Select(r => r.QuestId).Distinct().Count()} have objectives here; " +
                     $"{rows.Count} objectives: {ok} ok, {off} off target (> {OffTarget:0} m from the spot), {locked} behind locked doors (key needed), {unreachable} unreachable, " +
                     $"{noPoint} without a navmesh point (QuestingBots dropped them) - see questaccess/{intel.LocationId}.csv");
            foreach (Row r in rows.Where(r => !r.Status.StartsWith("ok") || r.Status.Contains("by hand")).Take(20))
            {
                Log.Info($"LR Quests:   {r.Quest} - {r.Objective}: {r.Status}; spot {Fmt(r.Target)}, bot point {Fmt(r.Point)}");
            }
        }

        private static string Classify(MapIntel intel, Row row)
        {
            if (!row.Point.HasValue)
            {
                return "no navmesh point";
            }

            Vector3 p = row.Point.Value;
            if (NavMesh.SamplePosition(p, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
            {
                p = hit.position;
            }

            if (!intel.IsReachable(p))
            {
                LockedSpot door = DoorInFront(intel, p, row.Target);
                if (door == null)
                {
                    // QuestingBots took the nearest navmesh, which can be an island (a ledge, a chair on a cliff): find a spot bots can walk to
                    if (Settings.QuestPointFix.Value && (TryRelocate(intel, row, p, out Vector3 inside) || TryRelocateNextTo(intel, row, p, out inside)))
                    {
                        string was = $"{Vector3.Distance(p, inside):0.0} m away, height {p.y - inside.y:+0.0;-0.0} m";
                        bool inZone = row.Zone.HasValue && InZone(row.Zone.Value, inside);
                        row.Point = inside;
                        return $"ok, unreachable point moved {(inZone || !row.Zone.HasValue ? "onto the spot" : "next to the zone")} (QuestingBots had it {was})";
                    }

                    return "unreachable";
                }

                // Like a player: the quest needs the key. QuestingBots unlocks the door on the way if the bot carries it (LiveRaid real keys)
                if (Settings.RealKeys.Value && row.Source != null && string.IsNullOrEmpty(row.Source.DoorIDToUnlock) && door.OutsidePos.HasValue)
                {
                    Vector3 o = door.OutsidePos.Value;
                    row.Source.DoorIDToUnlock = door.Id;
                    row.Source.InteractionPositionToUnlockDoor = new SerializableVector3(o.x, o.y, o.z);
                    return $"behind locked door {door.Id} (key {door.KeyId}): door assigned to the objective";
                }

                return $"behind locked door {door.Id} (key {door.KeyId})";
            }

            // A zone counts when the bot stands inside its trigger (a tall box: its centre is often in mid-air above the floor)
            if (row.Zone.HasValue && InZone(row.Zone.Value, p))
            {
                return "ok";
            }

            // The point is outside the zone or on another floor than the item: move it onto the real spot, like a player would go
            if (Settings.QuestPointFix.Value && TryRelocate(intel, row, p, out Vector3 moved))
            {
                string was = $"{Vector3.Distance(p, moved):0.0} m away, height {p.y - moved.y:+0.0;-0.0} m";
                row.Point = moved;
                return $"ok, point moved onto the spot (QuestingBots had it {was})";
            }

            if (row.Target.HasValue)
            {
                Vector3 d = row.Target.Value - p;
                float flat = new Vector2(d.x, d.z).magnitude;
                if (flat > OffTarget || Mathf.Abs(d.y) > 2.5f)
                {
                    if (row.OverrideKey != null && QbOverrides().Contains(row.OverrideKey))
                    {
                        return $"ok, point set by hand in QuestingBots ({flat:0} m from the spot, height {d.y:+0.0;-0.0} m)";
                    }

                    return $"off target {flat:0.0} m, height {d.y:+0.0;-0.0} m";
                }
            }

            return "ok";
        }

        private static bool InZone(Bounds zone, Vector3 standing)
        {
            Bounds b = zone;
            b.Expand(new Vector3(1.0f, 0f, 1.0f));

            // Feet may be a little below the box bottom, the body is inside it
            return b.Contains(standing + Vector3.up * 0.9f) || b.Contains(standing + Vector3.up * 0.2f);
        }

        /// <summary>
        /// Finds a reachable navmesh point on the real spot: inside the zone box (nearest to its centre, lower floors cost more),
        /// or on the floor the quest item lies on. Moves every step of the objective that used the old point.
        /// </summary>
        private static bool TryRelocate(MapIntel intel, Row row, Vector3 current, out Vector3 moved)
        {
            moved = current;
            Vector3? best = null;
            if (row.Zone.HasValue)
            {
                // Collect navmesh points inside the box first (cheap), then check reachability from the best one down (expensive, few)
                Bounds z = row.Zone.Value;
                float stepXZ = Mathf.Max(1f, Mathf.Max(z.size.x, z.size.z) / 8f);
                int samples = 0;
                List<(Vector3 pos, float cost)> candidates = new List<(Vector3, float)>();
                for (float y = z.min.y + 0.5f; y <= z.max.y && samples < 400; y += 1.5f)
                {
                    for (float x = z.min.x; x <= z.max.x && samples < 400; x += stepXZ)
                    {
                        for (float zz = z.min.z; zz <= z.max.z && samples < 400; zz += stepXZ)
                        {
                            samples++;
                            if (!NavMesh.SamplePosition(new Vector3(x, y, zz), out NavMeshHit hit, 1.0f, NavMesh.AllAreas) || !InZone(z, hit.position))
                            {
                                continue;
                            }

                            candidates.Add((hit.position, Vector3.Distance(hit.position, z.center) + 3f * Mathf.Abs(hit.position.y - z.center.y)));
                        }
                    }
                }

                int checks = 0;
                foreach (var candidate in candidates.OrderBy(cd => cd.cost))
                {
                    if (++checks > 12)
                    {
                        break;
                    }

                    if (intel.IsReachable(candidate.pos))
                    {
                        best = candidate.pos;
                        break;
                    }
                }
            }
            else if (row.Target.HasValue)
            {
                // An item lies on the floor, a shelf or a table: the floor is at most ~2 m below it
                Vector3 item = row.Target.Value;
                Vector3 d = item - current;
                bool wrongFloor = d.y > 2.2f || d.y < -0.8f || new Vector2(d.x, d.z).magnitude > 3f;
                if (wrongFloor && NavMesh.SamplePosition(item + Vector3.down * 0.9f, out NavMeshHit hit, 1.6f, NavMesh.AllAreas))
                {
                    Vector3 h = hit.position;
                    if (item.y - h.y <= 2.2f && item.y - h.y >= -0.8f && new Vector2(item.x - h.x, item.z - h.z).magnitude < 2.5f && intel.IsReachable(h))
                    {
                        best = h;
                    }
                }
            }

            return MoveSteps(row, current, best, out moved);
        }

        /// <summary>
        /// For a point QuestingBots put on a navmesh island: the nearest reachable navmesh around the zone (or item) on its level,
        /// within 9 m - the bot walks up to the spot and does its objective there, as close as the map lets it.
        /// </summary>
        private static bool TryRelocateNextTo(MapIntel intel, Row row, Vector3 current, out Vector3 moved)
        {
            moved = current;
            Vector3 centre = row.Zone.HasValue ? new Vector3(row.Zone.Value.center.x, current.y, row.Zone.Value.center.z) : row.Target ?? current;
            List<(Vector3 pos, float cost)> candidates = new List<(Vector3, float)>();
            for (float r = 1.5f; r <= 9f; r += 1.5f)
            {
                for (int a = 0; a < 12; a++)
                {
                    float angle = a * Mathf.PI / 6f;
                    Vector3 probe = centre + new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - current.y) <= 1.5f)
                    {
                        Vector3 flat = hit.position - centre;
                        flat.y = 0f;
                        candidates.Add((hit.position, flat.magnitude + 2f * Mathf.Abs(hit.position.y - current.y)));
                    }
                }
            }

            Vector3? best = null;
            int checks = 0;
            foreach (var candidate in candidates.OrderBy(cd => cd.cost))
            {
                if (++checks > 12)
                {
                    break;
                }

                if (intel.IsReachable(candidate.pos))
                {
                    best = candidate.pos;
                    break;
                }
            }

            return MoveSteps(row, current, best, out moved);
        }

        /// <summary>Moves every step of the objective that stood on the old point.</summary>
        private static bool MoveSteps(Row row, Vector3 current, Vector3? best, out Vector3 moved)
        {
            moved = current;
            if (!best.HasValue || row.Source == null || Vector3.Distance(best.Value, current) < 1f)
            {
                return false;
            }

            Vector3 b = best.Value;
            int changed = 0;
            foreach (BotQuestObjectiveStep step in row.Source.AllSteps)
            {
                if (step.Position.HasValue && Vector3.Distance(step.Position.Value, current) < 1.5f)
                {
                    step.SerializablePosition = new SerializableVector3(b.x, b.y, b.z);

                    // Resets QuestingBots' cached position
                    step.TrySnapToNavMesh(0.5f);
                    changed++;
                }
            }

            moved = b;
            return changed > 0;
        }

        /// <summary>The locked room (a door analysed by map intel) the objective lies in, if any.</summary>
        private static LockedSpot DoorInFront(MapIntel intel, Vector3 point, Vector3? target)
        {
            LockedSpot best = null;
            float bestSq = 10f * 10f;
            foreach (LockedSpot spot in intel.Spots)
            {
                if (spot.IsContainer || !spot.InteriorPos.HasValue || !spot.StillLocked)
                {
                    continue;
                }

                foreach (Vector3 q in target.HasValue ? new[] { point, target.Value } : new[] { point })
                {
                    float sq = (spot.InteriorPos.Value - q).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = spot;
                    }
                }
            }

            return best;
        }

        private static void Write(string location, List<Row> rows)
        {
            try
            {
                string dir = Path.Combine(Plugin.PluginDir, "questaccess");
                Directory.CreateDirectory(dir);
                List<string> lines = new List<string> { "quest,quest_id,objective,status,spot_x,spot_y,spot_z,point_x,point_y,point_z" };
                foreach (Row r in rows)
                {
                    lines.Add(string.Join(",", Csv(r.Quest), r.QuestId, Csv(r.Objective), Csv(r.Status), Xyz(r.Target), Xyz(r.Point)));
                }

                File.WriteAllLines(Path.Combine(dir, location + ".csv"), lines);
            }
            catch (Exception e)
            {
                Log.Warn("quest audit: could not write the csv: " + e.Message);
            }
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";

        private static string Xyz(Vector3? v) => v.HasValue
            ? string.Join(",", new[] { v.Value.x, v.Value.y, v.Value.z }.Select(f => f.ToString("0.0", CultureInfo.InvariantCulture)))
            : ",,";

        private static string Fmt(Vector3? v) => v.HasValue ? $"({v.Value.x:0.0}, {v.Value.y:0.0}, {v.Value.z:0.0})" : "-";
    }
}
