using System.Reflection;
using Path = System.IO.Path;
using LiveRaid.Server.Core;
using LiveRaid.Server.Patches;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace LiveRaid.Server;

// After SAIN's PostDbLoad (PostLoad + 5) so our bot-difficulty tweaks are not overwritten
[Injectable(TypePriority = OnLoadOrder.PostLoad + 20)]
public class LiveRaidServer(
    ISptLogger<LiveRaidServer> logger,
    TemplateTable templateTable,
    GlobalTable globalTable,
    BotTable botTable,
    BotConfig botConfig,
    PmcConfig pmcConfig,
    LocationTable locationTable,
    SPTarkov.Server.Core.Helpers.Bot.BotGeneratorHelper botGeneratorHelper,
    SPTarkov.Server.Core.Services.Profile.ProfileActivityService profileActivityService,
    SPTarkov.Server.Core.Helpers.InRaid.WeatherHelper weatherHelper,
    IEnumerable<IRuntimePatch> patches
) : IOnLoad
{
    private static readonly MongoId MechanicalKey = new("5c99f98d86f7745c314214b3");
    private static readonly MongoId Keycard = new("5c164d2286f774194c5e69fa");
    private static readonly string[] ScavRoles = ["assault", "marksman", "cursedassault", "assaultgroup", "crazyassaultevent"];

    // Roles whose behaviour LiveRaid tunes. Bosses, their followers, cultists, goons etc. keep vanilla behaviour
    // (they stay on their own spawn zone, patrol like in vanilla and stand by when nobody is near).
    private static readonly HashSet<string> TunedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "assault", "assaultgroup", "marksman", "cursedassault", "usec", "bear", "pmcusec", "pmcbear", "pmcbot", "exusec", "arenafighter",
    };

    private static readonly string[] PmcRoles = ["usec", "bear", "pmcusec", "pmcbear"];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        LrState.Logger = logger;
        LrState.Templates = templateTable;
        LrState.BotHelper = botGeneratorHelper;
        LrState.Activity = profileActivityService;
        LrState.Weather = weatherHelper;
        LrState.ModPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";

        try
        {
            string dataDir = Path.Combine(LrState.ModPath, "data");
            LrState.Config = LrJson.Load<LrServerConfig>(Path.Combine(dataDir, "config.json"));
            if (!LrState.Config.Enabled)
            {
                LrState.Info("disabled in data/config.json");
                return Task.CompletedTask;
            }

            ItemCatalog catalog = new(templateTable, globalTable);
            LrState.Catalog = catalog;
            ApplyBotTweaks(catalog);

            if (LrState.Config.Gear.Enabled)
            {
                ArchetypeFile archetypes = LrJson.Load<ArchetypeFile>(Path.Combine(dataDir, "archetypes.json"));
                LrState.Gear = new GearShaper(catalog, archetypes.Archetypes, LrState.Config.Gear);
                EnablePatch<PrepareAndGenerateBotPatch>();
                EnablePatch<FilterBotEquipmentPatch>();
                EnablePatch<FilterPlatesPatch>();
                if (LrState.Config.Gear.IndependentOfPlayerLevel)
                {
                    EnablePatch<BotLevelRangePatch>();
                    EnablePatch<ChooseLevelPatch>();
                    EnablePatch<BlacklistLevelPatch>();
                    EnablePatch<PlayerLevelWeightingPatch>();
                }
                LrState.Info($"gear archetypes: {string.Join(", ", archetypes.Archetypes.Select(a => a.Id))}");
            }

            if (LrState.Config.Bots.PmcAlwaysHostile)
            {
                int changed = PmcHostility.Force(pmcConfig.HostilitySettings.GetValueOrDefault("pmcbear"))
                              + PmcHostility.Force(pmcConfig.HostilitySettings.GetValueOrDefault("pmcusec"));
                int maps = 0;
                foreach (Location location in locationTable.GetDictionary().Values)
                {
                    int c = PmcHostility.Force(location?.Base);
                    changed += c;
                    maps += c > 0 ? 1 : 0;
                }

                // ...and again on the map sent to the game at each raid start, after SPT, its seasonal events and other mods had their say
                EnablePatch<RaidHostilityPatch>();
                LrState.Info($"PMC hostility: every PMC squad hostile to every other squad and player ({changed} values changed, {maps} maps)");
            }

            // SPT puts loot counts back after the load-time emptying (level brackets, Unheard pockets): undo that per bot
            if (LrState.Config.Bots.PmcEmptyLoot)
            {
                if (!LrState.Config.Gear.Enabled)
                {
                    EnablePatch<FilterBotEquipmentPatch>();
                }

                EnablePatch<UnheardPocketLootPatch>();
            }

            // Money for a paid exit (the car)
            if (LrState.Config.Bots.PmcExitMoney)
            {
                EnablePatch<ExitMoneyPatch>();
            }

            // Every PMC goes in with spare rounds for its guns
            if (LrState.Config.Bots.PmcSpareAmmo)
            {
                EnablePatch<SpareAmmoPatch>();
            }

            // PMC guns worn as online (most at 65-80 %, a new one now and then)
            WeaponConditionConfig wc = LrState.Config.Bots.PmcWeaponCondition;
            if (wc != null && wc.Enabled)
            {
                EnablePatch<WeaponConditionPatch>();
                LrState.Info($"PMC weapon condition: {wc.PristineChance:P0} brand new, the rest " +
                             string.Join(", ", wc.Bands.Select(b => $"{b.Min:0}-{b.Max:0} % (weight {b.Weight:0})")));
            }

            // Raids watched from the start (client F12 -> 11) leave the profile as it was
            EnablePatch<ObserverStartRaidPatch>();
            EnablePatch<ObserverEndRaidPatch>();

            logger.Success("[LiveRaid] server loaded");
        }
        catch (Exception e)
        {
            LrState.Error("failed to load, server part disabled", e);
        }

        return Task.CompletedTask;
    }

    private void EnablePatch<T>() where T : IRuntimePatch
    {
        IRuntimePatch patch = patches.FirstOrDefault(p => p.GetType() == typeof(T));
        if (patch == null)
        {
            LrState.Error($"patch {typeof(T).Name} was not registered");
            return;
        }

        try
        {
            patch.Enable();
        }
        catch (Exception e)
        {
            LrState.Error($"patch {typeof(T).Name} failed, that feature is off", e);
        }
    }

    private void ApplyBotTweaks(ItemCatalog catalog)
    {
        BotTweaksConfig cfg = LrState.Config.Bots;
        int standByChanged = 0;
        int scavKeysRemoved = 0;
        int pmcLootEmptied = 0;

        foreach ((string role, BotType type) in botTable.Types)
        {
            if (type?.BotDifficulty == null)
            {
                continue;
            }

            if (cfg.PmcEmptyLoot && PmcRoles.Contains(role, StringComparer.OrdinalIgnoreCase) && type.BotGeneration?.Items != null)
            {
                EmptyLootCounts(type.BotGeneration.Items);
                pmcLootEmptied++;
            }

            if (!TunedRoles.Contains(role))
            {
                continue;
            }

            foreach (DifficultyCategories difficulty in type.BotDifficulty.Values)
            {
                if (difficulty?.Mind == null)
                {
                    continue;
                }

                if (cfg.DisableStandBy && difficulty.Mind.CanStandBy == true)
                {
                    difficulty.Mind.CanStandBy = false;
                    standByChanged++;
                }
            }

            if (ScavRoles.Contains(role.ToLowerInvariant()) && type.BotInventory?.Items != null)
            {
                scavKeysRemoved += ScaleKeyWeights(type.BotInventory.Items.Backpack, catalog, cfg.ScavKeyWeightMultiplier);
                scavKeysRemoved += ScaleKeyWeights(type.BotInventory.Items.Pockets, catalog, cfg.ScavKeyWeightMultiplier);
                scavKeysRemoved += ScaleKeyWeights(type.BotInventory.Items.TacticalVest, catalog, cfg.ScavKeyWeightMultiplier);
            }
        }

        if (cfg.PmcAllowUnarmored && botConfig.Equipment.TryGetValue("pmc", out EquipmentFilters pmcFilters) && pmcFilters != null)
        {
            pmcFilters.ForceOnlyArmoredRigWhenNoArmor = false;
        }

        if (cfg.PmcEmptyLoot)
        {
            // No weapon thrown loose into the backpack either
            pmcConfig.LooseWeaponInBackpackLootMinMax.Max = 0;
            pmcConfig.LooseWeaponInBackpackChancePercent = 0;
        }

        foreach ((string map, int cap) in cfg.MaxBotCapOverrides)
        {
            botConfig.MaxBotCap[map] = cap;
        }

        LrState.Info($"bot tweaks: stand-by off in {standByChanged} difficulty sets, " +
                     $"{scavKeysRemoved} scav key entries dropped, {cfg.MaxBotCapOverrides.Count} bot caps overridden, PMC start loot emptied for {pmcLootEmptied} bot types");
    }

    /// <summary>Counts of valuables in bag, rig, pockets, special items and cash become "none"; meds, food, drink, stims and grenades stay.</summary>
    internal static void EmptyLootCounts(GenerationWeightingItems items)
    {
        foreach (GenerationData data in new[] { items.BackpackLoot, items.VestLoot, items.PocketLoot, items.SpecialItems, items.Currency, items.LooseLoot })
        {
            if (data?.Weights != null)
            {
                data.Weights = new Dictionary<double, double> { [0] = 1 };
            }
        }
    }

    private static int ScaleKeyWeights(Dictionary<MongoId, double> pool, ItemCatalog catalog, double mult)
    {
        if (pool == null || mult >= 1)
        {
            return 0;
        }

        int removed = 0;
        foreach (MongoId tpl in pool.Keys.ToList())
        {
            if (!catalog.IsChildOf(tpl, MechanicalKey) && !catalog.IsChildOf(tpl, Keycard))
            {
                continue;
            }

            double weight = Math.Round(pool[tpl] * mult);
            if (weight < 1)
            {
                pool.Remove(tpl);
                removed++;
            }
            else
            {
                pool[tpl] = weight;
            }
        }

        return removed;
    }
}
