using System.Text.Json;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// /liveraid/debug/gear      - archetype counts since server start
/// /liveraid/debug/genpmc/N/L - generate N PMCs of level L right now and summarise their kits
/// /liveraid/debug/hostility  - PMC hostility as the server holds it now: pmc.json and every map
/// /liveraid/debug/pmcloot/N/L - generate N PMCs and list, by kind, everything they carry in rig, backpack, pockets and secure container
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 50)]
public class DebugRouter(JsonUtil jsonUtil, BotGenerator botGenerator, SaveServer saveServer, PmcConfig pmcConfig, LocationTable locationTable)
    : DynamicRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/debug/gear",
                (url, info, sessionId, output, ct) => new ValueTask<string>(GearStats())
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/debug/genpmc/",
                (url, info, sessionId, output, ct) => new ValueTask<string>(GeneratePmcs(botGenerator, saveServer, url, sessionId))
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/debug/hostility",
                (url, info, sessionId, output, ct) => new ValueTask<string>(Hostility(pmcConfig, locationTable))
            ),
            new RouteAction<EmptyRequestData>(
                "/liveraid/debug/pmcloot/",
                (url, info, sessionId, output, ct) => new ValueTask<string>(PmcCarried(botGenerator, saveServer, url, sessionId))
            ),
        ]
    )
{
    private static string Hostility(PmcConfig pmcConfig, LocationTable locationTable)
    {
        return JsonSerializer.Serialize(new
        {
            pmcConfig = pmcConfig.HostilitySettings.ToDictionary(kv => kv.Key, kv => new
            {
                kv.Value.BearEnemyChance,
                kv.Value.UsecEnemyChance,
                chanced = (kv.Value.ChancedEnemies ?? []).Select(e => $"{e.Role} {e.EnemyChance}%"),
                friendly = kv.Value.AdditionalFriendlyTypes,
            }),
            maps = locationTable.GetDictionary().Where(kv => kv.Value?.Base?.BotLocationModifier?.AdditionalHostilitySettings != null)
                .ToDictionary(kv => kv.Key, kv => PmcHostility.Describe(kv.Value.Base)),
        }, LrJson.Options);
    }

    private static string GearStats()
    {
        return JsonSerializer.Serialize(LrState.Gear?.Stats ?? new(), LrJson.Options);
    }

    private static readonly HashSet<string> KitSlots =
    [
        "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "ArmorVest", "TacticalVest", "Headwear",
        "Backpack", "Earpiece", "FaceCover", "Eyewear",
    ];

    /// <summary>Value of everything lying inside the backpack (not the backpack itself): this should be meds, food and spare ammo only.</summary>
    private static double BagValue(List<Item> items)
    {
        MongoId inventoryTpl = "55d7217a4bdc2d86028b456d";
        Item root = items.FirstOrDefault(i => i.Template == inventoryTpl);
        Item bag = root == null ? null : items.FirstOrDefault(i => i.ParentId == root.Id.ToString() && i.SlotId == "Backpack");
        if (bag == null)
        {
            return 0;
        }

        Dictionary<string, List<Item>> children = items.Where(i => i.ParentId != null).GroupBy(i => i.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        double total = 0;
        Queue<Item> queue = new(children.GetValueOrDefault(bag.Id.ToString()) ?? []);
        while (queue.Count > 0)
        {
            Item it = queue.Dequeue();
            total += (LrState.Catalog?.Price(it.Template) ?? 0) * Math.Max(1, it.Upd?.StackObjectsCount ?? 1);
            foreach (Item c in children.GetValueOrDefault(it.Id.ToString()) ?? [])
            {
                queue.Enqueue(c);
            }
        }

        return total;
    }

    /// <summary>What the bot visibly carries: the equipment slots that matter and everything inside them (not pockets or the secure container).</summary>
    private static double KitValue(List<Item> items)
    {
        MongoId inventoryTpl = "55d7217a4bdc2d86028b456d";
        Item root = items.FirstOrDefault(i => i.Template == inventoryTpl);
        if (root == null)
        {
            return 0;
        }

        Dictionary<string, List<Item>> children = items.Where(i => i.ParentId != null).GroupBy(i => i.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        double total = 0;
        Queue<Item> queue = new(children.GetValueOrDefault(root.Id.ToString())?.Where(i => KitSlots.Contains(i.SlotId)) ?? []);
        while (queue.Count > 0)
        {
            Item it = queue.Dequeue();
            total += (LrState.Catalog?.Price(it.Template) ?? 0) * Math.Max(1, it.Upd?.StackObjectsCount ?? 1);
            foreach (Item c in children.GetValueOrDefault(it.Id.ToString()) ?? [])
            {
                queue.Enqueue(c);
            }
        }

        return total;
    }

    /// <summary>Lets the routes be opened from a browser: borrow any existing profile when the request has no session.</summary>
    private static bool ResolveSession(SaveServer saveServer, ref MongoId sessionId)
    {
        if (saveServer.ProfileExists(sessionId))
        {
            return true;
        }

        MongoId? anyProfile = saveServer.GetProfiles().Keys.Cast<MongoId?>().FirstOrDefault();
        if (anyProfile == null)
        {
            return false;
        }

        sessionId = anyProfile.Value;
        return true;
    }

    private static readonly string[] Difficulties = ["easy", "normal", "hard", "impossible"];

    /// <summary>The same request the raid makes for one PMC.</summary>
    private static BotGenerationDetails PmcRequest(int i, int level) => new()
    {
        IsPmc = true,
        Role = i % 2 == 0 ? "pmcUSEC" : "pmcBEAR",
        Side = i % 2 == 0 ? "Usec" : "Bear",
        PlayerLevel = level,
        BotRelativeLevelDeltaMin = 0,
        BotRelativeLevelDeltaMax = 0,
        BotCountToGenerate = 1,
        BotDifficulty = Difficulties[Random.Shared.Next(Difficulties.Length)],
        GameVersion = "standard",
    };

    /// <summary>What a carried item is, for checking that PMCs start without loot: kit (ammo, magazines, meds, food, grenades) or loot.</summary>
    private static string KindOf(MongoId tpl)
    {
        ItemCatalog c = LrState.Catalog;
        if (c == null) return "?";
        if (c.IsChildOf(tpl, BaseClasses.ARMOR_PLATE) || c.IsChildOf(tpl, BaseClasses.BUILT_IN_INSERTS)) return "kit: armor insert";
        if (c.IsChildOf(tpl, BaseClasses.AMMO) || c.IsChildOf(tpl, BaseClasses.AMMO_BOX)) return "kit: ammo";
        if (c.IsChildOf(tpl, BaseClasses.MAGAZINE)) return "kit: magazine";
        if (c.IsChildOf(tpl, BaseClasses.STIMULATOR)) return "kit: stim";
        if (c.IsChildOf(tpl, BaseClasses.MEDICAL_SUPPLIES)) return "LOOT: medical barter";
        if (c.IsChildOf(tpl, BaseClasses.MEDS)) return "kit: meds";
        if (c.IsChildOf(tpl, BaseClasses.FOOD_DRINK)) return "kit: food/drink";
        if (c.IsChildOf(tpl, BaseClasses.THROW_WEAP)) return "kit: grenade";
        if (c.IsChildOf(tpl, BaseClasses.KNIFE)) return "kit: knife";
        if (c.IsChildOf(tpl, BaseClasses.MONEY)) return "LOOT: money";
        if (c.IsChildOf(tpl, BaseClasses.KEY)) return "LOOT: key";
        if (c.IsChildOf(tpl, BaseClasses.WEAPON)) return "LOOT: loose weapon";
        if (c.IsChildOf(tpl, BaseClasses.MOD)) return "LOOT: weapon part";
        if (c.IsChildOf(tpl, BaseClasses.BARTER_ITEM)) return "LOOT: barter";
        if (c.IsChildOf(tpl, BaseClasses.INFO)) return "LOOT: info";
        if (c.IsChildOf(tpl, BaseClasses.JEWELRY)) return "LOOT: valuables";
        if (c.IsChildOf(tpl, BaseClasses.SPEC_ITEM)) return "special (radio, compass...)";
        return "LOOT: other";
    }

    private static string PmcCarried(BotGenerator generator, SaveServer saveServer, string url, MongoId sessionId)
    {
        if (!ResolveSession(saveServer, ref sessionId))
        {
            return "{\"error\":\"create a profile in the SPT launcher first\"}";
        }

        string[] parts = url.TrimEnd('/').Split('/');
        int count = parts.Length >= 5 && int.TryParse(parts[4], out int n) ? Math.Clamp(n, 1, 500) : 100;
        int level = parts.Length >= 6 && int.TryParse(parts[5], out int l) ? Math.Clamp(l, 1, 79) : 30;
        string[] containers = ["TacticalVest", "Backpack", "Pockets", "SecuredContainer"];

        // container -> kind -> pieces
        Dictionary<string, Dictionary<string, int>> kinds = containers.ToDictionary(x => x, _ => new Dictionary<string, int>());
        Dictionary<string, int> lootItems = new();
        Dictionary<string, int> emptyBags = new() { ["with backpack"] = 0, ["backpack empty"] = 0 };
        Dictionary<string, int> lootCounts = new();
        // State of the PMC guns (bots.pmcWeaponCondition): durability in 5 % steps, and max durability
        SortedDictionary<string, int> gunState = new();
        SortedDictionary<string, int> gunMax = new();
        // How many spare magazines a PMC carries in rig, pockets, backpack and secure container (raid 2026-11-08: h4rd111 had none after one fight)
        SortedDictionary<string, int> magsPerBot = new();

        for (int i = 0; i < count; i++)
        {
            BotBase bot = generator.PrepareAndGenerateBot(sessionId, PmcRequest(i, level));
            foreach (Item gun in (bot.Inventory?.Items ?? []).Where(it => it.SlotId is "FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster"))
            {
                double d = gun.Upd?.Repairable?.Durability ?? -1;
                double m = gun.Upd?.Repairable?.MaxDurability ?? -1;
                string band = d < 0 ? "none" : d >= 100 ? "100 (new)" : $"{Math.Floor(d / 5) * 5:00}-{Math.Floor(d / 5) * 5 + 5:00}";
                gunState[band] = gunState.GetValueOrDefault(band) + 1;
                string mb = m < 0 ? "none" : $"{Math.Floor(m / 5) * 5:00}+";
                gunMax[mb] = gunMax.GetValueOrDefault(mb) + 1;
            }
            string counts = GenContext.Last?.LootCounts ?? "not shaped";
            lootCounts[counts] = lootCounts.GetValueOrDefault(counts) + 1;
            List<Item> items = bot.Inventory?.Items ?? [];
            Dictionary<string, List<Item>> children = items.Where(it => it.ParentId != null).GroupBy(it => it.ParentId).ToDictionary(g => g.Key, g => g.ToList());
            MongoId inventoryTpl = "55d7217a4bdc2d86028b456d";
            Item root = items.FirstOrDefault(it => it.Template == inventoryTpl);
            if (root == null)
            {
                continue;
            }

            int botMags = 0;
            foreach (string slot in containers)
            {
                Item holder = children.GetValueOrDefault(root.Id.ToString())?.FirstOrDefault(it => it.SlotId == slot);
                if (holder == null)
                {
                    continue;
                }

                // Items lying in the container's grids; what is inside a magazine or attached to a weapon belongs to that item
                List<Item> inside = children.GetValueOrDefault(holder.Id.ToString()) ?? [];
                if (slot == "Backpack")
                {
                    emptyBags["with backpack"]++;
                    if (inside.Count == 0)
                    {
                        emptyBags["backpack empty"]++;
                    }
                }

                foreach (Item it in inside)
                {
                    string kind = KindOf(it.Template);
                    if (slot == "SecuredContainer" && kind == "LOOT: money")
                    {
                        // Roubles for a paid exit (ExitMoneyPatch): kit, not loot
                        kind = "kit: exit money";
                    }
                    int pieces = (int)Math.Max(1, it.Upd?.StackObjectsCount ?? 1);
                    if (kind == "kit: magazine")
                    {
                        botMags++;
                    }

                    kinds[slot][kind] = kinds[slot].GetValueOrDefault(kind) + pieces;
                    if (!kind.StartsWith("kit"))
                    {
                        string key = $"{slot}/{it.SlotId}: {LrState.Catalog?.Name(it.Template)} ({kind}, ~{LrState.Catalog?.Price(it.Template) ?? 0:0} rub)";
                        lootItems[key] = lootItems.GetValueOrDefault(key) + pieces;
                    }
                }
            }

            string mk = botMags.ToString("00");
            magsPerBot[mk] = magsPerBot.GetValueOrDefault(mk) + 1;
        }

        return JsonSerializer.Serialize(new
        {
            count,
            level,
            pmcEmptyLoot = LrState.Config.Bots.PmcEmptyLoot,
            backpacks = emptyBags,
            lootCounts,
            kinds,
            notKit = lootItems.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            gunDurability = gunState,
            gunMaxDurability = gunMax,
            spareMagazinesPerBot = magsPerBot,
        }, LrJson.Options);
    }

    private static string GeneratePmcs(BotGenerator generator, SaveServer saveServer, string url, MongoId sessionId)
    {
        if (!ResolveSession(saveServer, ref sessionId))
        {
            return "{\"error\":\"create a profile in the SPT launcher first\"}";
        }

        string[] parts = url.TrimEnd('/').Split('/');
        int count = parts.Length >= 5 && int.TryParse(parts[4], out int n) ? Math.Clamp(n, 1, 500) : 50;
        int level = parts.Length >= 6 && int.TryParse(parts[5], out int l) ? Math.Clamp(l, 1, 79) : 30;

        List<object> bots = new();
        Dictionary<string, int> archetypes = new();
        Dictionary<string, int> weaponClasses = new();
        List<double> values = new();
        List<double> bagValues = new();

        for (int i = 0; i < count; i++)
        {
            BotGenerationDetails details = PmcRequest(i, level);

            BotBase bot = generator.PrepareAndGenerateBot(sessionId, details);
            string arch = GenContext.Last?.Archetype?.Id ?? "none";
            int kitLevel = GenContext.Last?.KitLevel ?? 0;
            archetypes[arch] = archetypes.GetValueOrDefault(arch) + 1;

            List<Item> items = bot.Inventory?.Items ?? [];
            double value = KitValue(items);
            bagValues.Add(BagValue(items));
            values.Add(value);

            string Slot(string slot)
            {
                Item it = items.FirstOrDefault(x => x.SlotId == slot);
                return it == null ? null : it.Template.ToString();
            }

            string primary = Slot("FirstPrimaryWeapon");
            string weaponClass = primary == null ? "pistol-only" : LrState.Catalog?.WeaponClass(primary) ?? "?";
            weaponClasses[weaponClass] = weaponClasses.GetValueOrDefault(weaponClass) + 1;

            bots.Add(new
            {
                arch,
                kitLevel,
                difficulty = details.BotDifficulty,
                level = bot.Info?.Level,
                weaponClass,
                primary,
                holster = Slot("Holster"),
                armor = Slot("ArmorVest"),
                rig = Slot("TacticalVest"),
                helmet = Slot("Headwear"),
                backpack = Slot("Backpack"),
                itemCount = items.Count,
                value = Math.Round(value),
                bagValue = Math.Round(BagValue(items)),
                body = bot.Customization?.Body.ToString(),
                feet = bot.Customization?.Feet.ToString(),
                hands = bot.Customization?.Hands.ToString(),
            });
        }

        values.Sort();
        return JsonSerializer.Serialize(new
        {
            count,
            level,
            archetypes,
            weaponClasses,
            avgBagValue = bots.Count > 0 ? Math.Round(bagValues.Average()) : 0,
            valueP10 = values.Count > 0 ? values[values.Count / 10] : 0,
            valueMedian = values.Count > 0 ? values[values.Count / 2] : 0,
            valueP90 = values.Count > 0 ? values[values.Count * 9 / 10] : 0,
            bots,
        }, LrJson.Options);
    }
}
