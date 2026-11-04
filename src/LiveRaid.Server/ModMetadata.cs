#nullable enable
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace LiveRaid.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.lir.liveraid";
    public string Name { get; init; } = "LiveRaid";
    public string Author { get; init; } = "Lir";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.2.4");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; } = ["com.chazut.orbit.server"];
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.danw.questingbots"] = new(">=1.2.0 <2.0.0"),
        ["me.sol.sain"] = new(">=4.5.0 <5.0.0"),
        ["me.skwizzy.lootingbots"] = new(">=1.8.0 <2.0.0"),
    };
    public string? Url { get; init; } = "https://github.com/Lir-byte/LiveRaid";
    public bool? IsBundleMod { get; init; } = false;
    public string License { get; init; } = "CC-BY-NC-SA-4.0";
    public bool HasPrepatcher { get; init; } = false;
}
