using System.Reflection;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace LiveRaid.Server;

/// <summary>
/// "Start raids as an observer" (client F12 -> 11): the player watches the raid from a free camera and the raid must not count.
/// The client calls /liveraid/observer/begin when such a raid starts and ends it with this exit name. At the raid end SPT
/// (LocationLifecycleService.EndLocalRaidAsync) would copy the client's profile over the server's - inventory, experience, stats,
/// quests, health - and with any status but "survived" it would also strip found-in-raid from every item. For an observer raid the
/// request's profile is replaced with a copy of the server's own profile from before the raid, with status "survived": every step
/// of SPT's end-of-raid then writes back exactly what was there. Nothing is lost, nothing is gained, no raid in the statistics.
/// </summary>
public static class ObserverRaids
{
    public const string ExitTag = "LiveRaid observer";

    private static readonly HashSet<MongoId> Sessions = new();

    public static ProfileHelper Profiles { get; set; }
    public static ICloner Cloner { get; set; }

    public static bool Init(ProfileHelper profiles, ICloner cloner)
    {
        Profiles = profiles;
        Cloner = cloner;
        return true;
    }

    public static void Mark(MongoId sessionId)
    {
        lock (Sessions)
        {
            Sessions.Add(sessionId);
        }
    }

    public static bool Take(MongoId sessionId)
    {
        lock (Sessions)
        {
            return Sessions.Remove(sessionId);
        }
    }
}

[Injectable(TypePriority = OnLoadOrder.Routers + 52)]
public class ObserverRouter(JsonUtil jsonUtil, ProfileHelper profileHelper, ICloner cloner)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/observer/begin",
                (url, info, sessionId, output, ct) =>
                {
                    ObserverRaids.Profiles = profileHelper;
                    ObserverRaids.Cloner = cloner;
                    ObserverRaids.Mark(sessionId);
                    LrState.Info("observer raid started: it will not count (profile kept as it is now)");
                    return new ValueTask<string>("{\"ok\":true}");
                }
            ),
        ]
    )
{
    // The end-of-raid patch needs these even when the begin route was never called this server session
    private readonly bool _ready = ObserverRaids.Init(profileHelper, cloner);
}

[Injectable]
public class ObserverEndRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.EndLocalRaidAsync), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        try
        {
            bool marked = ObserverRaids.Take(sessionId);
            if (!marked && request?.Results?.ExitName != ObserverRaids.ExitTag)
            {
                return;
            }

            if (request?.Results == null || ObserverRaids.Profiles == null || ObserverRaids.Cloner == null)
            {
                LrState.Warn("observer raid ended, but the profile could not be kept (no data) - SPT handles it as usual");
                return;
            }

            var full = ObserverRaids.Profiles.GetFullProfile(sessionId);
            string side = request.ServerId?.Split('.').ElementAtOrDefault(1) ?? "pmc";
            PmcData before = side.Contains("pmc", StringComparison.OrdinalIgnoreCase) ? full?.CharacterData?.PmcData : full?.CharacterData?.ScavData;
            if (before == null)
            {
                LrState.Warn("observer raid ended, but the server profile was not found - SPT handles it as usual");
                return;
            }

            request.Results.Profile = ObserverRaids.Cloner.Clone(before);
            request.Results.Result = ExitStatus.SURVIVED;
            request.Results.ExitName = null;
            request.Results.KillerId = null;
            request.Results.KillerAid = null;
            request.LostInsuredItems = [];
            request.TransferItems = new Dictionary<string, IEnumerable<SPTarkov.Server.Core.Models.Eft.Common.Tables.Item>>();
            request.LocationTransit = null;
            LrState.Info($"observer raid ended: {side} profile left exactly as before the raid (no loot lost or gained, no experience, no statistics)");
        }
        catch (Exception e)
        {
            LrState.Error("observer raid end failed - SPT handles the raid end as usual", e);
        }
    }
}

/// <summary>
/// A new raid starts: any observer mark left over is dropped (a raid that never reached its end - a crash - must not make the next,
/// normal raid "not count"). The client marks an observer raid only after this, once it is in the game.
/// </summary>
[Injectable]
public class ObserverStartRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaidAsync), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(MongoId sessionId)
    {
        if (ObserverRaids.Take(sessionId))
        {
            LrState.Info("a stale observer mark from an unfinished raid was dropped at the start of a new raid");
        }
    }
}
