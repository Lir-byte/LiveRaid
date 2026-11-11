using SPTarkov.Common.Models.Logging;

namespace LiveRaid.Server.Core;

/// <summary>Static access for Harmony patches, which cannot receive services through DI.</summary>
public static class LrState
{
    public static LrServerConfig Config { get; set; } = new();
    public static GearShaper Gear { get; set; }
    public static ItemCatalog Catalog { get; set; }
    public static ISptLogger<LiveRaidServer> Logger { get; set; }
    public static string ModPath { get; set; } = "";
    public static SPTarkov.Server.Core.Models.Spt.Tables.TemplateTable Templates { get; set; }
    public static SPTarkov.Server.Core.Helpers.Bot.BotGeneratorHelper BotHelper { get; set; }
    public static SPTarkov.Server.Core.Services.Profile.ProfileActivityService Activity { get; set; }
    public static SPTarkov.Server.Core.Helpers.InRaid.WeatherHelper Weather { get; set; }
    public static int NightBots;
    public static int SpareAmmoFixed;
    public static int SpareMagazinesAdded;
    public static int SpareMagazinesNoRoom;

    public static void Info(string msg) => Logger?.Info("[LiveRaid] " + msg);

    public static void Warn(string msg) => Logger?.Warning("[LiveRaid] " + msg);

    public static void Error(string msg, Exception e = null) => Logger?.Error("[LiveRaid] " + msg + (e != null ? ": " + e : ""));

    public static void Debug(string msg)
    {
        if (Config.DebugLogging)
        {
            Logger?.Info("[LiveRaid:debug] " + msg);
        }
    }
}
