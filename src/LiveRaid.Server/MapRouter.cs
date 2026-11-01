using System.Text;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// /liveraid/map/lootpoints/&lt;location&gt; - every loose-loot spawn point of the map from the SPT database (all floors, all rooms),
/// not just the items that happened to spawn this raid. The client turns them into points of interest for bot routes.
/// Answer: {"location":"bigmap","points":[[x,y,z,probability],...]}
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 51)]
public class MapRouter(JsonUtil jsonUtil, LocationTable locationTable)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/map/lootpoints/",
                (url, info, sessionId, output, ct) => new ValueTask<string>(LootPoints(locationTable, url))
            ),
        ]
    )
{
    private static readonly Dictionary<string, string> Answers = new(StringComparer.OrdinalIgnoreCase);

    private static string LootPoints(LocationTable locations, string url)
    {
        string id = url.TrimEnd('/').Split('/').Last();
        lock (Answers)
        {
            if (Answers.TryGetValue(id, out string cached))
            {
                return cached;
            }
        }

        string answer;
        try
        {
            LooseLoot loot = locations.GetLocation(id)?.LooseLoot?.Value;
            if (loot == null)
            {
                return "{\"location\":\"" + id + "\",\"points\":[]}";
            }

            StringBuilder sb = new StringBuilder("{\"location\":\"").Append(id).Append("\",\"points\":[");
            int count = 0;
            void Add(Vector3? p, double probability)
            {
                if (p == null)
                {
                    return;
                }

                sb.Append(count++ == 0 ? "[" : ",[")
                    .Append(F(p.Value.X)).Append(',').Append(F(p.Value.Y)).Append(',').Append(F(p.Value.Z)).Append(',')
                    .Append(probability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            }

            foreach (Spawnpoint sp in (loot.Spawnpoints ?? []).Concat(loot.SpawnpointsForced ?? []))
            {
                double probability = sp.Probability ?? 1;
                Add(sp.Template?.Position, probability);
                foreach (GroupPosition gp in sp.Template?.GroupPositions ?? [])
                {
                    Add(gp.Position, probability);
                }
            }

            answer = sb.Append("]}").ToString();
            LrState.Info($"map {id}: {count} loose-loot spawn points sent to the client");
        }
        catch (Exception e)
        {
            LrState.Error($"loot points for {id} failed", e);
            return "{\"location\":\"" + id + "\",\"points\":[]}";
        }

        lock (Answers)
        {
            Answers[id] = answer;
        }

        return answer;
    }

    private static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
