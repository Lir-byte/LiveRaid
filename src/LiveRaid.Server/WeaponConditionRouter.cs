using System.Globalization;
using LiveRaid.Server.Core;
using LiveRaid.Server.Patches;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// The F12 settings of the client for the state of PMC guns: /liveraid/weaponcondition/set/&lt;on 0|1&gt;/&lt;brand-new chance %&gt;/&lt;max above current&gt;/ then for each of the 4 bands &lt;from %&gt;/&lt;to %&gt;/&lt;weight&gt; (15 numbers).
/// Sent when the game starts and whenever one of them changes (like the packrat chance); they stand over bots.pmcWeaponCondition of data/config.json.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 55)]
public class WeaponConditionRouter(JsonUtil jsonUtil)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/weaponcondition/set/",
                (url, info, sessionId, output, ct) =>
                {
                    string[] parts = url.TrimEnd('/').Split('/');
                    string[] v = parts.Skip(Math.Max(0, parts.Length - 15)).ToArray();
                    double[] n = new double[15];
                    if (v.Length == 15 && Enumerable.Range(1, 14).All(i => double.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])))
                    {
                        List<WeaponConditionBand> bands = new();
                        for (int i = 0; i < 4; i++)
                        {
                            bands.Add(new WeaponConditionBand { Min = n[3 + i * 3], Max = n[4 + i * 3], Weight = n[5 + i * 3] });
                        }

                        return new ValueTask<string>(WeaponConditionPatch.Set(v[0] == "1", n[1], n[2], bands));
                    }

                    return new ValueTask<string>(WeaponConditionPatch.Status());
                }
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/weaponcondition/status",
                (url, info, sessionId, output, ct) => new ValueTask<string>(WeaponConditionPatch.Status())
            ),
        ]
    )
{
}
