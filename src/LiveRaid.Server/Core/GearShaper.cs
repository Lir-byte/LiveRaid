using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace LiveRaid.Server.Core;

/// <summary>
/// State of the bot currently being generated on this thread. SPT generates bots in parallel,
/// but each bot is built start-to-finish on one thread.
/// </summary>
public class GenContext
{
    [ThreadStatic] private static GenContext _current;
    [ThreadStatic] private static GenContext _last;

    public static GenContext Current => _current;

    // The context of the bot most recently finished on this thread (used by the debug generator)
    public static GenContext Last => _last;

    public bool IsPmc;
    public string Role;
    public string Difficulty;
    public Archetype Archetype;
    public double BandShift;
    public int KitLevel;
    public int DisplayLevel;

    // The raid this bot is for is a night raid (the player's raid settings and the in-raid hour)
    public bool IsNight;

    // Debug: loot counts of the template the loot generator will use
    public string LootCounts;

    public static void Begin(BotGenerationDetails details)
    {
        _current = new GenContext
        {
            IsPmc = details.IsPmc,
            Role = details.Role,
            Difficulty = details.BotDifficulty,
        };
    }

    public static void End()
    {
        _last = _current;
        _current = null;
    }
}

/// <summary>
/// Reshapes a cloned PMC bot template so the generated kit follows a randomly chosen archetype
/// (bum, budget, meta, sniper...) instead of being driven almost entirely by bot level.
/// </summary>
public class GearShaper
{
    private readonly ItemCatalog _catalog;
    private readonly List<Archetype> _archetypes;
    private readonly GearConfig _config;

    public ConcurrentDictionary<string, int> Stats { get; } = new();

    public GearShaper(ItemCatalog catalog, List<Archetype> archetypes, GearConfig config)
    {
        _catalog = catalog;
        _archetypes = archetypes;
        _config = config;
    }

    public bool Independent => _config.IndependentOfPlayerLevel;

    /// <summary>The level SPT's level-driven tables should see for this kit, or 0 to leave the bot's own level.</summary>
    public int PickKitLevel(Archetype arch, Random rng)
    {
        if (!_config.IndependentOfPlayerLevel || arch.KitLevel == null)
        {
            return 0;
        }

        return (int)Math.Round(arch.KitLevel.Min + rng.NextDouble() * (arch.KitLevel.Max - arch.KitLevel.Min));
    }

    public Archetype Choose(int level, string difficulty, Random rng)
    {
        double influence = _config.IndependentOfPlayerLevel ? _config.LevelInfluence : 1;
        double total = 0;
        foreach (Archetype a in _archetypes)
        {
            total += a.WeightFor(level, difficulty, influence);
        }

        if (total <= 0)
        {
            return null;
        }

        double roll = rng.NextDouble() * total;
        foreach (Archetype a in _archetypes)
        {
            roll -= a.WeightFor(level, difficulty, influence);
            if (roll <= 0)
            {
                return a;
            }
        }

        return _archetypes[^1];
    }

    /// <summary>
    /// Night raid: weapon lights and lasers on more guns, most on the cheap kits (SPT gives NVGs at night by the kit's level, so a bum has none -
    /// online a newcomer at night runs with a flashlight). +30 below kit level 15, +25 to 22, +15 to 45, +10 above.
    /// </summary>
    public void NightLights(BotType template, GenContext ctx)
    {
        Dictionary<string, double> mods = template.BotChances?.WeaponModsChances;
        if (mods == null)
        {
            return;
        }

        int level = ctx.KitLevel > 0 ? ctx.KitLevel : ctx.DisplayLevel;
        double add = level < 15 ? 30 : level < 23 ? 25 : level < 46 ? 15 : 10;
        foreach (string key in mods.Keys.ToList())
        {
            if (key.Contains("tactical", StringComparison.OrdinalIgnoreCase) || key.Contains("flashlight", StringComparison.OrdinalIgnoreCase))
            {
                mods[key] = Math.Min(100, mods[key] + add);
            }
        }

        int n = Interlocked.Increment(ref LrState.NightBots);
        if (n == 1 || n % 50 == 0)
        {
            LrState.Info($"night raid: weapon lights and lasers more likely on PMC guns (+{add:0} % at kit level {level}; {n} PMCs so far)");
        }
    }

    public void Apply(BotType template, Archetype arch, GenContext ctx, Random rng)
    {
        ctx.Archetype = arch;
        ctx.BandShift = arch.Variance * (rng.NextDouble() * 2 - 1);
        Stats.AddOrUpdate(arch.Id, 1, (_, n) => n + 1);

        Dictionary<EquipmentSlots, Dictionary<MongoId, double>> equipment = template.BotInventory.Equipment;
        Dictionary<string, double> chances = template.BotChances.EquipmentChances;

        // Primary weapon: class filter + price band inside each class
        if (equipment.TryGetValue(EquipmentSlots.FirstPrimaryWeapon, out var primaries))
        {
            ShapeWeapons(primaries, arch.Primary, ctx.BandShift, rng);
        }

        SetChance(chances, "FirstPrimaryWeapon", arch.Primary.Chance);

        if (arch.Holster != null)
        {
            SetChance(chances, "Holster", arch.Holster.Chance);
            if (arch.Holster.Price != null && equipment.TryGetValue(EquipmentSlots.Holster, out var holsters))
            {
                ShapeByPrice(holsters, arch.Holster.Price, ctx.BandShift, arch.Variance);
            }
        }

        SetChance(chances, "SecondPrimaryWeapon", arch.SecondPrimaryChance);

        foreach ((string slotName, SlotSpec spec) in arch.Slots)
        {
            SetChance(chances, slotName, spec.Chance);
            if (spec.Price == null || !Enum.TryParse(slotName, out EquipmentSlots slot))
            {
                continue;
            }

            if (equipment.TryGetValue(slot, out var pool))
            {
                ShapeByPrice(pool, spec.Price, ctx.BandShift, arch.Variance);
            }
        }

        if (arch.Ammo != null)
        {
            foreach (Dictionary<MongoId, double> caliberPool in template.BotInventory.Ammo.Values)
            {
                ShapeAmmo(caliberPool, arch.Ammo, ctx.BandShift);
            }
        }

        ScaleChances(template.BotChances.WeaponModsChances, arch.WeaponModChanceMult);
        ScaleChances(template.BotChances.EquipmentModsChances, arch.EquipmentModChanceMult);

        GenerationWeightingItems gen = template.BotGeneration?.Items;
        if (gen != null)
        {
            ScaleCounts(gen.BackpackLoot, arch.LootCountMult);
            ScaleCounts(gen.VestLoot, arch.LootCountMult);
            ScaleCounts(gen.PocketLoot, arch.LootCountMult);
            ScaleCounts(gen.Healing, arch.MedsMult);
            ScaleCounts(gen.Drugs, arch.MedsMult);
            ScaleCounts(gen.Stims, arch.MedsMult);
            ScaleCounts(gen.Grenades, arch.GrenadeMult);
        }
    }

    /// <summary>
    /// Picks plates for one plate slot according to the archetype's plate-class weights.
    /// Returns null when nothing in the pool fits, so SPT's own choice stays.
    /// </summary>
    public HashSet<MongoId> ChoosePlates(Archetype arch, HashSet<MongoId> pool, Random rng)
    {
        if (arch.Plates.Count == 0 || pool == null || pool.Count == 0)
        {
            return null;
        }

        Dictionary<int, List<MongoId>> byClass = new();
        foreach (MongoId tpl in pool)
        {
            int cls = _catalog.ArmorClass(tpl);
            if (!byClass.TryGetValue(cls, out var list))
            {
                byClass[cls] = list = new List<MongoId>();
            }

            list.Add(tpl);
        }

        double total = arch.Plates.Values.Sum();
        double roll = rng.NextDouble() * total;
        int wanted = 0;
        foreach ((string cls, double w) in arch.Plates)
        {
            roll -= w;
            if (roll <= 0)
            {
                wanted = int.Parse(cls);
                break;
            }
        }

        // Nearest available class, ties go to the lower class
        int best = -1;
        int bestDist = int.MaxValue;
        foreach (int cls in byClass.Keys)
        {
            int dist = Math.Abs(cls - wanted) * 2 + (cls > wanted ? 1 : 0);
            if (dist < bestDist)
            {
                best = cls;
                bestDist = dist;
            }
        }

        return best < 0 ? null : byClass[best].ToHashSet();
    }

    private void ShapeWeapons(Dictionary<MongoId, double> pool, WeaponSpec spec, double shift, Random rng)
    {
        if (pool.Count == 0)
        {
            return;
        }

        HashSet<string> classes = spec.Classes.Count > 0 ? spec.Classes.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        Dictionary<MongoId, double> allowed = new();

        foreach (IGrouping<string, KeyValuePair<MongoId, double>> group in pool.GroupBy(kv => _catalog.WeaponClass(kv.Key) ?? "unknown"))
        {
            if (classes != null && !classes.Contains(group.Key))
            {
                continue;
            }

            Dictionary<MongoId, double> classPool = group.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (spec.Price != null)
            {
                ShapeByPrice(classPool, spec.Price, shift, 0.05);
            }

            foreach ((MongoId id, double w) in classPool)
            {
                allowed[id] = w;
            }
        }

        if (allowed.Count == 0)
        {
            // The archetype's classes don't exist in this pool; keep SPT's pool rather than produce an empty slot
            return;
        }

        ReplacePool(pool, allowed);
    }

    /// <summary>
    /// Keeps items whose price percentile inside this pool falls in the (shifted) band and flattens their weights.
    /// </summary>
    private void ShapeByPrice(Dictionary<MongoId, double> pool, Range2 band, double shift, double minWidth)
    {
        if (pool.Count <= 1)
        {
            return;
        }

        List<MongoId> sorted = pool.Keys.OrderBy(_catalog.Value).ToList();
        double lo = Math.Clamp(band.Min + shift, 0, 1);
        double hi = Math.Clamp(band.Max + shift, 0, 1);
        if (hi - lo < minWidth)
        {
            double mid = (lo + hi) / 2;
            lo = Math.Max(0, mid - minWidth / 2);
            hi = Math.Min(1, mid + minWidth / 2);
        }

        Dictionary<MongoId, double> kept = new();
        for (int i = 0; i < sorted.Count; i++)
        {
            double pct = sorted.Count == 1 ? 0.5 : i / (double)(sorted.Count - 1);
            if (pct >= lo && pct <= hi)
            {
                kept[sorted[i]] = pool[sorted[i]];
            }
        }

        if (kept.Count == 0)
        {
            // Band fell between items: take the one nearest to the band centre
            int idx = (int)Math.Round((lo + hi) / 2 * (sorted.Count - 1));
            kept[sorted[idx]] = pool[sorted[idx]];
        }

        ReplacePool(pool, kept);
    }

    private void ShapeAmmo(Dictionary<MongoId, double> pool, Range2 band, double shift)
    {
        if (pool.Count <= 1)
        {
            return;
        }

        List<MongoId> sorted = pool.Keys.OrderBy(_catalog.Penetration).ToList();
        double lo = Math.Clamp(band.Min + shift, 0, 1);
        double hi = Math.Clamp(band.Max + shift, 0, 1);

        Dictionary<MongoId, double> kept = new();
        for (int i = 0; i < sorted.Count; i++)
        {
            double pct = i / (double)(sorted.Count - 1);
            if (pct >= lo && pct <= hi)
            {
                kept[sorted[i]] = Math.Max(1, pool[sorted[i]]);
            }
        }

        if (kept.Count == 0)
        {
            int idx = (int)Math.Round((lo + hi) / 2 * (sorted.Count - 1));
            kept[sorted[idx]] = 1;
        }

        pool.Clear();
        foreach ((MongoId id, double w) in kept)
        {
            pool[id] = w;
        }
    }

    private void ReplacePool(Dictionary<MongoId, double> pool, Dictionary<MongoId, double> kept)
    {
        double flat = Math.Clamp(_config.WeightFlattening, 0, 1);
        pool.Clear();
        foreach ((MongoId id, double w) in kept)
        {
            // w^flat keeps rare-but-allowed items possible; +1 so zero-weight entries can still roll
            pool[id] = Math.Max(1, Math.Round(Math.Pow(Math.Max(w, 0), flat) * 10 + 1));
        }
    }

    private static void SetChance(Dictionary<string, double> chances, string key, double value)
    {
        if (value >= 0)
        {
            chances[key] = Math.Clamp(value, 0, 100);
        }
    }

    private static void ScaleChances(Dictionary<string, double> chances, double mult)
    {
        if (chances == null || Math.Abs(mult - 1) < 0.001)
        {
            return;
        }

        foreach (string key in chances.Keys.ToList())
        {
            chances[key] = Math.Clamp(Math.Round(chances[key] * mult), 0, 100);
        }
    }

    /// <summary>
    /// Scales "how many items" weight tables, e.g. {0:1, 3:5, 8:6} with mult 0.5 becomes {0:1, 2:5, 4:6}.
    /// </summary>
    private static void ScaleCounts(GenerationData data, double mult)
    {
        if (data?.Weights == null || data.Weights.Count == 0 || Math.Abs(mult - 1) < 0.001)
        {
            return;
        }

        Dictionary<double, double> scaled = new();
        foreach ((double count, double weight) in data.Weights)
        {
            double newCount = Math.Max(0, Math.Round(count * mult));
            scaled[newCount] = scaled.GetValueOrDefault(newCount) + weight;
        }

        data.Weights = scaled;
    }
}
