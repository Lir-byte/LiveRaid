using System.Reflection;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace LiveRaid.Server.Patches;

/// <summary>
/// The state of a PMC's guns as online (data/config.json -> bots.pmcWeaponCondition). SPT gives every PMC gun 95-100 max durability and 0-5 below
/// that - all PMCs walk in with practically new guns, while online most players run theirs at 65-80 % and repair them now and then; a brand-new
/// gun is the exception. Every PMC gun (primary, second, holster) rolls on its own: "pristine" (100 / 100) with its chance, otherwise one of the
/// bands by weight (current durability), the max a little above it (a gun repaired a few times). Bosses, followers and scavs keep SPT's values.
/// </summary>
[Injectable]
public class WeaponConditionPatch : AbstractPatch
{
    public static int Rolled;
    public static int Pristine;

    protected override MethodBase GetTargetMethod() =>
        typeof(BotGeneratorHelper).GetMethod("GenerateWeaponRepairableProperties", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(string botRole, ref UpdRepairable __result)
    {
        WeaponConditionConfig cfg = LrState.Config.Bots.PmcWeaponCondition;
        if (cfg == null || !cfg.Enabled || __result == null || !IsPmc(botRole))
        {
            return;
        }

        try
        {
            (double durability, double max) = Roll(cfg, Random.Shared);
            __result.Durability = Math.Round(durability, 5);
            __result.MaxDurability = Math.Round(max, 5);
            Rolled++;
            if (durability >= 100)
            {
                Pristine++;
            }
        }
        catch (Exception e)
        {
            LrState.Error("Weapon condition roll failed (SPT's value kept)", e);
        }
    }

    private static bool IsPmc(string role)
    {
        if (GenContext.Current != null)
        {
            return GenContext.Current.IsPmc;
        }

        string r = role?.ToLowerInvariant() ?? "";
        return r == "pmcusec" || r == "pmcbear" || r == "usec" || r == "bear";
    }

    /// <summary>Durability and max durability (percent of a new gun) for one gun.</summary>
    public static (double, double) Roll(WeaponConditionConfig cfg, Random rnd)
    {
        if (rnd.NextDouble() < cfg.PristineChance)
        {
            return (100, 100);
        }

        double total = cfg.Bands.Sum(b => Math.Max(0, b.Weight));
        WeaponConditionBand band = cfg.Bands.LastOrDefault();
        double pick = rnd.NextDouble() * total;
        foreach (WeaponConditionBand b in cfg.Bands)
        {
            pick -= Math.Max(0, b.Weight);
            if (pick <= 0)
            {
                band = b;
                break;
            }
        }

        if (band == null)
        {
            return (100, 100);
        }

        double lo = Math.Clamp(Math.Min(band.Min, band.Max), 1, 100);
        double hi = Math.Clamp(Math.Max(band.Min, band.Max), 1, 100);
        double durability = lo + rnd.NextDouble() * (hi - lo);
        // Repairs eat into the max: a little above the current state, never below it
        double max = Math.Min(100, durability + rnd.NextDouble() * Math.Max(0, cfg.MaxAboveCurrent));
        // A gun that rolled "100" in a band is still not a pristine one
        durability = Math.Min(durability, 99.9);
        return (durability, Math.Max(max, durability));
    }
}
