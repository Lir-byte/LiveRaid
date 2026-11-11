using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// The F12 settings of the client for the content mod's clothing on the PMC bots (shown there only when the content mod is installed):
/// /liveraid/modclothes/set/&lt;on 0|1&gt;, /liveraid/modclothes/status. Sent when the game starts and whenever one changes.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 56)]
public class ModClothesRouter(JsonUtil jsonUtil)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/modclothes/set/",
                (url, info, sessionId, output, ct) =>
                {
                    string last = url.TrimEnd('/').Split('/').Last();
                    return new ValueTask<string>(ModClothes.SetState(last == "1"));
                }
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/modclothes/status",
                (url, info, sessionId, output, ct) => new ValueTask<string>(ModClothes.Status())
            ),
        ]
    )
{
}
