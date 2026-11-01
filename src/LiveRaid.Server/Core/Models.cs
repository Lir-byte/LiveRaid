using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveRaid.Server.Core;

public class LrServerConfig
{
    public bool Enabled { get; set; } = true;
    public bool DebugLogging { get; set; } = false;
    public GearConfig Gear { get; set; } = new();
    public BotTweaksConfig Bots { get; set; } = new();
}

public class GearConfig
{
    public bool Enabled { get; set; } = true;

    // 1 = keep SPT weights as they are, 0 = every allowed item equally likely
    public double WeightFlattening { get; set; } = 0.5;

    // Online there is no link between your level and what other PMCs wear. ON: the PMC's displayed level is drawn from the
    // whole 1-79 range (not around yours) and the kit comes from the archetype, not from player/bot level brackets.
    public bool IndependentOfPlayerLevel { get; set; } = true;

    // How much the displayed level of the PMC still hints at the kit (0 = nothing, 1 = SPT-like brackets)
    public double LevelInfluence { get; set; } = 0.15;

    // Shape of the level draw: 1 = uniform over 1-79, higher = more low levels (1.6 ~ median 26)
    public double LevelCurve { get; set; } = 1.6;
}

public class BotTweaksConfig
{
    // Vanilla stand-by puts far bots to sleep; LiveRaid keeps everyone awake
    public bool DisableStandBy { get; set; } = true;

    // Scavs spawn with keys from their own backpack pool; LiveRaid hands out map-relevant keys on the client instead
    public double ScavKeyWeightMultiplier { get; set; } = 0.15;
    public Dictionary<string, int> MaxBotCapOverrides { get; set; } = new();

    // SPT forces an armored rig on PMCs that rolled no body armor; online plenty of people run without any
    public bool PmcAllowUnarmored { get; set; } = true;

    // PMCs start the raid with their kit only (weapons, mags, a few meds/food/grenades) - no valuables in bag, rig and pockets.
    // Their loot comes from searching containers, rooms and bodies during the raid.
    public bool PmcEmptyLoot { get; set; } = true;

    // Night raids: weapon lights and lasers more often on cheap kits (SPT already gives NVGs at night by the kit's level)
    public bool NightLights { get; set; } = true;

    // Half of the PMCs (a quarter of the bums) carry 6-40k roubles in the secure container for a paid exit (the car)
    public bool PmcExitMoney { get; set; } = true;

    // Every PMC carries at least two magazines' worth of spare rounds for each of its guns (added to the secure container when SPT could not fit any).
    // Off by default since 0.4.2: EFT's bots refill from the ammo SPT already puts into their secure container, so vanilla needs no help
    public bool PmcSpareAmmo { get; set; } = false;

    // Every PMC is always hostile to every PMC and player outside its own squad, BEAR to BEAR and USEC to USEC included (like online)
    public bool PmcAlwaysHostile { get; set; } = true;

    // The state of PMC guns as online (most at 65-80 %, a new one now and then) instead of SPT's 90-100 %
    public WeaponConditionConfig PmcWeaponCondition { get; set; } = new();
}

public class WeaponConditionConfig
{
    public bool Enabled { get; set; } = true;

    // Chance of a brand-new gun (100 / 100)
    public double PristineChance { get; set; } = 0.15;

    // How far the max durability may sit above the current one (repairs eat into the max)
    public double MaxAboveCurrent { get; set; } = 8;

    // The rest: current durability in percent, a band picked by weight
    public List<WeaponConditionBand> Bands { get; set; } = new()
    {
        new WeaponConditionBand { Min = 80, Max = 95, Weight = 22 },
        new WeaponConditionBand { Min = 65, Max = 80, Weight = 60 },
        new WeaponConditionBand { Min = 50, Max = 65, Weight = 14 },
        new WeaponConditionBand { Min = 35, Max = 50, Weight = 4 },
    };
}

public class WeaponConditionBand
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Weight { get; set; }
}

public class Range2
{
    public double Min { get; set; }
    public double Max { get; set; } = 1;

    public bool Contains(double v) => v >= Min && v <= Max;
}

public class LevelWeight
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public double Weight { get; set; }
}

public class SlotSpec
{
    public double Chance { get; set; } = -1;
    public Range2 Price { get; set; }
}

public class WeaponSpec
{
    public double Chance { get; set; } = 100;
    public List<string> Classes { get; set; } = new();
    public Range2 Price { get; set; }
}

public class Archetype
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<LevelWeight> Weights { get; set; } = new();

    // Level-free weight used when gear is independent of level (<0 = fall back to the level brackets)
    public double Online { get; set; } = -1;

    // The "level" SPT's own tables (blacklists, plate weights, loot budgets...) see for this kit
    public Range2 KitLevel { get; set; }

    // multiplier on the archetype weight by requested bot difficulty (easy/normal/hard/impossible)
    public Dictionary<string, double> SkillAffinity { get; set; } = new();

    public WeaponSpec Primary { get; set; } = new();
    public SlotSpec Holster { get; set; }
    public double SecondPrimaryChance { get; set; } = -1;
    public Dictionary<string, SlotSpec> Slots { get; set; } = new();

    // plate class -> weight
    public Dictionary<string, double> Plates { get; set; } = new();

    // percentile band of armor penetration inside each caliber
    public Range2 Ammo { get; set; }

    public double WeaponModChanceMult { get; set; } = 1;
    public double EquipmentModChanceMult { get; set; } = 1;
    public double LootCountMult { get; set; } = 1;
    public double MedsMult { get; set; } = 1;
    public double GrenadeMult { get; set; } = 1;

    // random widening/shift of every price band, so two bots of the same archetype still differ
    public double Variance { get; set; } = 0.1;

    public double WeightFor(int level, string difficulty, double levelInfluence = 1)
    {
        double w = 0;
        foreach (LevelWeight lw in Weights)
        {
            if (level >= lw.Min && level <= lw.Max)
            {
                w = lw.Weight;
                break;
            }
        }

        if (Online >= 0 && levelInfluence < 1)
        {
            w = Online * (1 - levelInfluence) + w * levelInfluence;
        }

        if (w > 0 && difficulty != null && SkillAffinity.TryGetValue(difficulty.ToLowerInvariant(), out double mult))
        {
            w *= mult;
        }

        return w;
    }
}

public class ArchetypeFile
{
    public List<Archetype> Archetypes { get; set; } = new();
}

public static class LrJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
    }
}
