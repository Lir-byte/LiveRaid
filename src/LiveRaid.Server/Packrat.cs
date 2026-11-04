using System.Globalization;
using System.Reflection;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// The packrat ("Барахольщик"): with a small chance (client F12 -> 6 "Packrat chance, %") one lone PMC of a raid comes in with a pistol and the
/// biggest bag, there only for the loot (a player's tester asked for a "loot rat" - with a knife it never comes, with a pistol it does).
///  - The client sends its chance (/liveraid/packrat/chance/&lt;percent&gt;) at the start of the game and whenever it changes; kept in packrat.json.
///  - At the start of a raid (client/match/local/start) the server rolls it once for that raid.
///  - Armed, the first PMC generated of each side (USEC, BEAR) gets the kit of the archetype "packrat" of archetypes.json (never drawn otherwise: no
///    weights): a pistol and a big bag. The server cannot know which PMCs will be alone: the client takes the bots of a request into its own pool
///    (a request for one PMC brings the role's preset limit of them, BotCountToGenerate) and QuestingBots forms groups from that pool as it goes.
///  - The client asks /liveraid/packrat/who for the profile ids and sees to it that such a profile only ever goes to a group of one (LiveRaid.Client
///    Patches/PackratPoolPatch.cs), and plays it as the packrat (Bots/Packrat.cs).
/// </summary>
public static class Packrat
{
    public const string ArchetypeId = "packrat";

    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "packrat.json");
    private static double _chance = -1;
    private static bool _armed;
    private static readonly HashSet<string> _takenRoles = new();
    private static readonly List<string> _ids = new();
    private static string _name = "";
    private static DateTime _raidAt = DateTime.MinValue;

    public static double Chance
    {
        get
        {
            lock (Gate)
            {
                if (_chance < 0)
                {
                    _chance = 15;
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            string text = File.ReadAllText(FilePath).Trim().Trim('{', '}');
                            string number = text.Split(':').Last().Trim();
                            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                            {
                                _chance = Math.Clamp(v, 0, 100);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // keep the default
                    }
                }

                return _chance;
            }
        }
    }

    public static void SetChance(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (Gate)
        {
            if (Math.Abs(percent - _chance) < 0.01)
            {
                return;
            }

            _chance = percent;
            try
            {
                File.WriteAllText(FilePath, "{\"chance\":" + percent.ToString("0.##", CultureInfo.InvariantCulture) + "}");
            }
            catch (Exception)
            {
                // memory only
            }
        }

        LrState.Info($"packrat chance per raid: {percent:0.#} %");
    }

    public static void NewRaid(string location)
    {
        double chance = Chance;
        string prev;
        lock (Gate)
        {
            // Fika: the host and every client start the same raid - one roll per raid
            if ((DateTime.UtcNow - _raidAt).TotalSeconds < 60)
            {
                return;
            }

            _raidAt = DateTime.UtcNow;
            _armed = Random.Shared.NextDouble() * 100 < chance;
            prev = $"last raid: PMC requests for one bot {_reqLone}, for groups {_reqGroup}; packrat kits {_ids.Count} ({(string.IsNullOrEmpty(_name) ? "none" : _name)})";
            _takenRoles.Clear();
            _reqLone = 0;
            _reqGroup = 0;
            _ids.Clear();
            _name = "";
        }

        LrState.Info("packrat " + prev);

        LrState.Info($"packrat on {location}: {(_armed ? "one lone PMC of this raid will be the packrat" : "none this raid")} (chance {chance:0.#} %)");
    }

    private static int _reqLone;
    private static int _reqGroup;

    /// <summary>A PMC request of the client, counted: how many bots it asked for (GenerateCondition.Limit). For the log only - see the class summary.</summary>
    public static void NoteRequest(GenerateCondition request, BotGenerationDetails details)
    {
        if (details == null || !details.IsPmc || details.IsPlayerScav)
        {
            return;
        }

        if (request != null && request.Limit == 1)
        {
            Interlocked.Increment(ref _reqLone);
        }
        else
        {
            Interlocked.Increment(ref _reqGroup);
        }
    }

    /// <summary>
    /// This PMC gets the packrat's kit: the raid is armed and no PMC of its side has it yet (one per side, so that a lone group of either side has one to take;
    /// the client uses one packrat per raid and keeps the other away from every group).
    /// </summary>
    public static bool TryTake(BotGenerationDetails details)
    {
        if (!_armed || details == null || !details.IsPmc || details.IsPlayerScav)
        {
            return false;
        }

        string role = (details.Role ?? "").ToLowerInvariant();
        lock (Gate)
        {
            if (!_takenRoles.Add(role))
            {
                return false;
            }
        }

        LrState.Info($"packrat: the first {role} generated gets the packrat's kit");
        return true;
    }

    public static void Generated(string profileId, string name)
    {
        lock (Gate)
        {
            _ids.Add(profileId ?? "");
            _name = string.IsNullOrEmpty(_name) ? name ?? "" : _name + "," + name;
        }

        LrState.Info($"packrat candidate of this raid: {name}");
    }

    public static string Who()
    {
        lock (Gate)
        {
            return "{\"ids\":\"" + string.Join(",", _ids) + "\",\"name\":\"" + _name.Replace("\"", "") + "\",\"armed\":\"" + (_armed ? "1" : "0") + "\"}";
        }
    }
}

[Injectable(TypePriority = OnLoadOrder.Routers + 53)]
public class PackratRouter(JsonUtil jsonUtil)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/packrat/chance/",
                (url, info, sessionId, output, ct) =>
                {
                    string last = url.TrimEnd('/').Split('/').Last();
                    if (double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        Packrat.SetChance(v);
                    }

                    return new ValueTask<string>("{\"chance\":" + Packrat.Chance.ToString("0.##", CultureInfo.InvariantCulture) + "}");
                }
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/packrat/who",
                (url, info, sessionId, output, ct) => new ValueTask<string>(Packrat.Who())
            ),
        ]
    )
{
}

[Injectable]
public class PackratStartRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaidAsync), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        try
        {
            Packrat.NewRaid(request?.Location ?? "?");
        }
        catch (Exception e)
        {
            LrState.Error("packrat roll failed", e);
        }
    }
}

[Injectable]
public class PackratWavePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotController).GetMethod("GenerateBotWave", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(GenerateCondition generateRequest, BotGenerationDetails botGenerationDetails)
    {
        try
        {
            Packrat.NoteRequest(generateRequest, botGenerationDetails);
        }
        catch (Exception e)
        {
            LrState.Error("packrat request note failed", e);
        }
    }
}
