using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// The F12 switch of the client for the content mod's guns (shown there only when the content mod is installed on the client):
/// /liveraid/modweapons/set/1 puts the guns into the bots' pools, /liveraid/modweapons/set/0 takes them out; /liveraid/modweapons/status tells what is in.
/// The client sends its value when the game starts and whenever it changes (like the packrat chance).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 54)]
public class ModWeaponsRouter(JsonUtil jsonUtil)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/modweapons/set/",
                (url, info, sessionId, output, ct) =>
                {
                    string last = url.TrimEnd('/').Split('/').Last();
                    return new ValueTask<string>(ModWeapons.SetEnabled(last == "1"));
                }
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/modweapons/status",
                (url, info, sessionId, output, ct) => new ValueTask<string>(ModWeapons.Status())
            ),
        ]
    )
{
}
