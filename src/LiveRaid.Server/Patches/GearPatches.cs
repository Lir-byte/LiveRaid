using System.Reflection;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace LiveRaid.Server.Patches;

/// <summary>Opens a per-bot generation context and closes it whatever happens.</summary>
[Injectable]
public class PrepareAndGenerateBotPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotGenerator).GetMethod(nameof(BotGenerator.PrepareAndGenerateBot), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, BotGenerationDetails botGenerationDetails)
    {
        GenContext.Begin(botGenerationDetails);
        try
        {
            var raid = LrState.Activity?.GetProfileActivityRaidData(sessionId)?.RaidConfiguration;
            if (raid != null && LrState.Weather != null && GenContext.Current != null)
            {
                GenContext.Current.IsNight = LrState.Weather.IsNightTime(raid.TimeVariant, raid.Location);
            }
        }
        catch (Exception)
        {
            // No raid settings (a debug generation): day
        }
    }

    [PatchFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        GenContext.End();
        return __exception;
    }
}

/// <summary>
/// Runs after SPT applied its level-based weights and chances to the cloned template;
/// LiveRaid then picks an archetype and reshapes the template.
/// </summary>
[Injectable]
public class FilterBotEquipmentPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotEquipmentFilterService).GetMethod(nameof(BotEquipmentFilterService.FilterBotEquipment), BindingFlags.Instance | BindingFlags.Public);

    // The archetype is chosen before SPT applies its tables, so that SPT's level-driven tables can be pointed at the kit's level
    [PatchPrefix]
    public static void Prefix(BotGenerationDetails botGenerationDetails)
    {
        GenContext ctx = GenContext.Current;
        GearShaper shaper = LrState.Gear;
        if (ctx == null || shaper == null || !botGenerationDetails.IsPmc || botGenerationDetails.IsPlayerScav)
        {
            return;
        }

        try
        {
            Random rng = Random.Shared;
            ctx.DisplayLevel = botGenerationDetails.BotLevel;
            Archetype arch = shaper.Choose(botGenerationDetails.BotLevel, botGenerationDetails.BotDifficulty, rng);
            ctx.Archetype = arch;
            if (arch == null)
            {
                return;
            }

            // Low-level maps (Ground Zero) keep their own level window
            if (botGenerationDetails.LocationSpecificPmcLevelOverride == null)
            {
                int kitLevel = shaper.PickKitLevel(arch, rng);
                if (kitLevel > 0)
                {
                    ctx.KitLevel = kitLevel;
                    botGenerationDetails.BotLevel = kitLevel;
                }
            }
        }
        catch (Exception e)
        {
            LrState.Error("Archetype choice failed, SPT defaults kept", e);
            ctx.Archetype = null;
        }
    }

    [PatchPostfix]
    public static void Postfix(BotType baseBotNode, BotGenerationDetails botGenerationDetails)
    {
        GenContext ctx = GenContext.Current;
        GearShaper shaper = LrState.Gear;

        // SPT's level brackets (bot.json equipment.pmc.randomisation[].generation) have just put loot counts back into the template:
        // 1 PMC in 3 started with barter items, cigarettes and spare parts in rig, bag and pockets. Empty them again for this bot.
        GenerationWeightingItems gen = baseBotNode?.BotGeneration?.Items;
        if (LrState.Config.Bots.PmcEmptyLoot && botGenerationDetails.IsPmc && !botGenerationDetails.IsPlayerScav && gen != null)
        {
            LiveRaidServer.EmptyLootCounts(gen);
        }

        if (ctx?.Archetype == null || shaper == null || !botGenerationDetails.IsPmc || botGenerationDetails.IsPlayerScav)
        {
            return;
        }

        try
        {
            Random rng = Random.Shared;
            Archetype arch = ctx.Archetype;
            shaper.Apply(baseBotNode, arch, ctx, rng);
            if (ctx.IsNight && LrState.Config.Bots.NightLights)
            {
                shaper.NightLights(baseBotNode, ctx);
            }

            // For the debug check (/liveraid/debug/pmcloot): the loot counts the loot generator will roll from
            string W(GenerationData d) => d?.Weights == null ? "-" : string.Join(",", d.Weights.Where(kv => kv.Value > 0).Select(kv => kv.Key.ToString()));
            ctx.LootCounts = gen == null ? "no generation" : $"backpack {W(gen.BackpackLoot)} vest {W(gen.VestLoot)} pocket {W(gen.PocketLoot)} special {W(gen.SpecialItems)} currency {W(gen.Currency)}";
            LrState.Debug($"PMC shown lvl {ctx.DisplayLevel}, kit lvl {(ctx.KitLevel > 0 ? ctx.KitLevel : ctx.DisplayLevel)} {botGenerationDetails.BotDifficulty} -> {arch.Id} (shift {ctx.BandShift:+0.00;-0.00})");
        }
        catch (Exception e)
        {
            LrState.Error("Gear shaping failed, SPT defaults kept", e);
        }
    }
}

/// <summary>Plate class follows the archetype instead of the level bracket.</summary>
[Injectable]
public class FilterPlatesPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotEquipmentModGenerator).GetMethod(nameof(BotEquipmentModGenerator.FilterPlateModsForSlotByLevel), BindingFlags.Instance | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(FilterPlateModsForSlotByLevelResult __result, HashSet<MongoId> existingPlateTplPool)
    {
        Archetype arch = GenContext.Current?.Archetype;
        if (arch == null || __result == null || __result.Result != Result.SUCCESS || LrState.Gear == null)
        {
            return;
        }

        try
        {
            HashSet<MongoId> plates = LrState.Gear.ChoosePlates(arch, existingPlateTplPool, Random.Shared);
            if (plates != null && plates.Count > 0)
            {
                __result.PlateModTemplates = plates;
            }
        }
        catch (Exception e)
        {
            LrState.Error("Plate selection failed", e);
        }
    }
}

/// <summary>
/// SPT draws a PMC's level from a window around the PLAYER's level. Online there is no such window:
/// the whole population is there. With independence on, the level comes from the full range of the bot type.
/// </summary>
[Injectable]
public class BotLevelRangePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotLevelGenerator).GetMethod(nameof(BotLevelGenerator.GetRelativePmcBotLevelRange), BindingFlags.Instance | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(BotGenerationDetails botGenerationDetails, MinMax<int> levelDetails, int maxAvailableLevel, ref MinMax<int> __result)
    {
        if (LrState.Gear == null || !LrState.Gear.Independent || botGenerationDetails.LocationSpecificPmcLevelOverride != null)
        {
            return;
        }

        int min = Math.Clamp(levelDetails.Min, 1, maxAvailableLevel);
        int max = Math.Clamp(levelDetails.Max, min, maxAvailableLevel);
        __result = new MinMax<int>(min, max);
    }
}

/// <summary>Item blacklists (best ammo, NVGs, top optics) were chosen by PLAYER level; they follow the kit's level now.</summary>
[Injectable]
public class BlacklistLevelPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotEquipmentFilterService).GetMethod(nameof(BotEquipmentFilterService.GetBotEquipmentBlacklist), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static void Prefix(ref double playerLevel)
    {
        int kit = GenContext.Current?.KitLevel ?? 0;
        if (kit > 0)
        {
            playerLevel = kit;
        }
    }
}

/// <summary>Weighting adjustments "by player level" use the kit's level too.</summary>
[Injectable]
public class PlayerLevelWeightingPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotEquipmentFilterService).GetMethod("GetBotWeightingAdjustmentsByPlayerLevel", BindingFlags.Instance | BindingFlags.NonPublic);

    [PatchPrefix]
    public static void Prefix(ref int playerLevel)
    {
        int kit = GenContext.Current?.KitLevel ?? 0;
        if (kit > 0)
        {
            playerLevel = kit;
        }
    }
}

/// <summary>
/// SPT's level draw is biased towards the top of the window. Online most players sit at low-to-mid levels with a thin tail of veterans,
/// so with independence on the level is drawn as 1 + range * u^1.6 (median around 25-30 over 1-79).
/// </summary>
[Injectable]
public class ChooseLevelPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotLevelGenerator).GetMethod(nameof(BotLevelGenerator.ChooseBotLevel), BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    public static bool Prefix(double min, double max, ref int __result)
    {
        GenContext ctx = GenContext.Current;
        if (ctx == null || !ctx.IsPmc || LrState.Gear == null || !LrState.Gear.Independent)
        {
            return true;
        }

        double u = Random.Shared.NextDouble();
        __result = (int)Math.Clamp(Math.Round(min + (max - min) * Math.Pow(u, LrState.Config.Gear.LevelCurve)), min, max);
        return false;
    }
}

/// <summary>
/// Unheard-edition PMCs: after all the filtering SPT adds "5 or 6 items" to the pocket loot weights,
/// so with an empty start 1 PMC in 5 still came with a barter item or a spare part in a pocket. Skipped when PMCs start without loot.
/// </summary>
[Injectable]
public class UnheardPocketLootPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotGenerator).GetMethod("AddAdditionalPocketLootWeightsForUnheardBot", BindingFlags.Instance | BindingFlags.NonPublic);

    [PatchPrefix]
    public static bool Prefix() => !LrState.Config.Bots.PmcEmptyLoot;
}

/// <summary>
/// At raid start SPT copies pmc.json hostility into the map (and Halloween replaces the map's hostility first):
/// the map that reaches the game gets PMC-vs-PMC hostility forced to 100 % once more, whatever came before.
/// </summary>
[Injectable]
public class RaidHostilityPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod("AdjustBotHostilitySettings", BindingFlags.Instance | BindingFlags.NonPublic);

    [PatchPostfix]
    public static void Postfix(LocationBase location)
    {
        try
        {
            int changed = PmcHostility.Force(location);
            LrState.Info($"PMC hostility on {location?.Id}: {(changed > 0 ? changed + " values forced back" : "already all hostile")} - {PmcHostility.Describe(location)}");
        }
        catch (Exception e)
        {
            LrState.Error("PMC hostility at raid start failed", e);
        }
    }
}
