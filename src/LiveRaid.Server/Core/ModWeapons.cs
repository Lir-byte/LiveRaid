using System.Text.Json;
using Path = System.IO.Path;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace LiveRaid.Server.Core;

/// <summary>
/// Guns of content mods for the bots (data/config.json bots.modWeapons). WTT-style content mods (WTT-ContentBackport: the weapons of the 1.0 update) register their
/// items in the item database, but their "addtoBots" only copies an item into the bots' LOOT pools - a gun never becomes a bot's weapon, so no bot ever carried one.
/// Here every weapon, knife and round those mods list in db/CustomItems goes into the equipment and ammo pools of the PMC roles (and, if the gun is cheap
/// enough, of the scavs) at the average weight of the pool, with a mod pool SPT can build the gun from:
///  - the mod pool of the weapon the new one was cloned from (itemTplToClone), cut down to what the new gun's slots accept;
///  - the stock build of the gun (its preset, which the mods register too): the parts of that build join the slots' pools.
/// A gun that cannot be built (a required slot with nothing for it) is left out. If a generated build still lacks a required part, SPT falls back to the gun's
/// preset by itself, so a bot never carries half a gun.
/// </summary>
public static class ModWeapons
{
    private sealed class Gun
    {
        public MongoId Id;
        public string Name;
        public string WeaponClass;
        public string Caliber;
        public MongoId? DefAmmo;
        public double Price;
        public bool Knife;
        public Dictionary<string, HashSet<MongoId>> Pool;
    }

    public static int Guns { get; private set; }
    public static int Knives { get; private set; }

    // What was added, undone in reverse when the player turns the guns off in F12 (/liveraid/modweapons/set/0)
    private static readonly List<Action> _undo = new();
    private static readonly object Gate = new();
    private static TemplateTable _templates;
    private static GlobalTable _globals;
    private static BotTable _botTable;
    private static ItemCatalog _catalog;
    private static ModWeaponsConfig _cfg;
    private static string _modsDir;
    private static bool _applied;

    public static bool Applied => _applied;

    public static void Init(TemplateTable templates, GlobalTable globals, BotTable botTable, ItemCatalog catalog, ModWeaponsConfig cfg, string modsDir)
    {
        _templates = templates;
        _globals = globals;
        _botTable = botTable;
        _catalog = catalog;
        _cfg = cfg;
        _modsDir = modsDir;
    }

    public static bool ContentModFound => _cfg != null && FindFolders(_modsDir, _cfg).Count > 0;

    /// <summary>The client's F12 switch (guns of the content mod for the bots): on puts the guns into the pools, off takes them out again.</summary>
    public static string SetEnabled(bool on)
    {
        lock (Gate)
        {
            try
            {
                if (_cfg != null && on && !_applied)
                {
                    Apply();
                }
                else if (!on && _applied)
                {
                    Remove();
                }
            }
            catch (Exception e)
            {
                LrState.Error("mod weapons switch failed", e);
            }

            return Status();
        }
    }

    public static string Status() =>
        "{\"applied\":" + (_applied ? "true" : "false") + ",\"found\":" + (ContentModFound ? "true" : "false") + ",\"guns\":" + Guns + ",\"knives\":" + Knives + "}";

    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied || _cfg == null)
            {
                return;
            }

            Apply(_templates, _globals, _botTable, _catalog, _cfg, _modsDir);
        }
    }

    private static void Remove()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i]();
            }
            catch (Exception e)
            {
                LrState.Warn("mod weapons: could not take one entry out: " + e.Message);
            }
        }

        _undo.Clear();
        _applied = false;
        LrState.Info("mod weapons: the content mod's guns are out of the bots' pools again (switched off in F12)");
    }

    private static void Apply(TemplateTable templates, GlobalTable globals, BotTable botTable, ItemCatalog catalog, ModWeaponsConfig cfg, string modsDir)
    {
        List<string> folders = FindFolders(modsDir, cfg);
        if (folders.Count == 0)
        {
            LrState.Info("mod weapons: no content mod found (looked for " + (cfg.ModFolders.Count > 0 ? string.Join(", ", cfg.ModFolders) : "any WTT-style mod") + " in " + modsDir + ")");
            return;
        }

        Dictionary<MongoId, MongoId?> listed = ReadListed(folders);
        HashSet<string> excluded = cfg.Exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Gun> guns = new();
        List<MongoId> ammo = new();
        List<string> skipped = new();
        foreach ((MongoId id, MongoId? clone) in listed)
        {
            if (excluded.Contains(id.ToString()) || !templates.Items.TryGetValue(id, out TemplateItem tpl) || tpl.Properties == null)
            {
                continue;
            }

            if (catalog.IsChildOf(id, BaseClasses.AMMO))
            {
                if (cfg.Ammo && !string.IsNullOrEmpty(tpl.Properties.Caliber))
                {
                    ammo.Add(id);
                }

                continue;
            }

            bool knife = catalog.IsChildOf(id, BaseClasses.KNIFE);
            if (!knife && !catalog.IsChildOf(id, BaseClasses.WEAPON))
            {
                continue;
            }

            if (knife && !cfg.Knives)
            {
                continue;
            }

            Gun gun = new()
            {
                Id = id,
                Name = tpl.Name ?? id.ToString(),
                WeaponClass = tpl.Properties.WeapClass,
                Caliber = tpl.Properties.AmmoCaliber,
                DefAmmo = tpl.Properties.DefAmmo,
                Price = catalog.Value(id),
                Knife = knife,
            };

            if (!knife)
            {
                // No machine guns / launchers: bots have no slot for them in their pools
                if (gun.WeaponClass is "machinegun" or "grenadeLauncher" or "specialWeapon" || string.IsNullOrEmpty(gun.Caliber))
                {
                    continue;
                }

                gun.Pool = BuildModPool(templates, globals, botTable, id, clone, tpl);
                if (gun.Pool == null)
                {
                    skipped.Add(gun.Name);
                    continue;
                }
            }

            guns.Add(gun);
        }

        if (guns.Count == 0)
        {
            LrState.Info($"mod weapons: {listed.Count} items listed by {string.Join(", ", folders.Select(Path.GetFileName))}, no usable weapon among them");
            return;
        }

        Dictionary<string, List<MongoId>> ammoByCaliber = ammo
            .GroupBy(a => templates.Items[a].Properties.Caliber)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<string> done = new();
        foreach (string role in cfg.PmcRoles)
        {
            done.Add($"{role} {AddToRole(botTable, role, guns, ammoByCaliber, cfg, double.MaxValue)}");
        }

        if (cfg.Scavs)
        {
            foreach (string role in cfg.ScavRoles)
            {
                done.Add($"{role} {AddToRole(botTable, role, guns, ammoByCaliber, cfg, cfg.ScavMaxPrice)}");
            }
        }

        Guns = guns.Count(g => !g.Knife);
        Knives = guns.Count(g => g.Knife);
        _applied = true;
        LrState.Info($"mod weapons: {Guns} guns, {Knives} knives and {ammo.Count} rounds from {string.Join(", ", folders.Select(Path.GetFileName))} into the bots' pools " +
                     $"({string.Join(", ", done)}); weight x{cfg.WeightOfAverage:0.##} of the pool's average" +
                     (skipped.Count > 0 ? $"; left out - no usable build: {string.Join(", ", skipped)}" : ""));
    }

    private static List<string> FindFolders(string modsDir, ModWeaponsConfig cfg)
    {
        List<string> found = new();
        if (!Directory.Exists(modsDir))
        {
            return found;
        }

        foreach (string dir in Directory.GetDirectories(modsDir))
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("LiveRaid", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(dir, "db", "CustomItems")))
            {
                continue;
            }

            if (cfg.ModFolders.Count == 0 || cfg.ModFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(dir);
            }
        }

        return found;
    }

    /// <summary>Every item the mods' db/CustomItems configs create: id -> the template it was cloned from.</summary>
    private static Dictionary<MongoId, MongoId?> ReadListed(List<string> folders)
    {
        Dictionary<MongoId, MongoId?> listed = new();
        JsonDocumentOptions options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        foreach (string folder in folders)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(folder, "db", "CustomItems"), "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), options);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (JsonProperty item in doc.RootElement.EnumerateObject())
                    {
                        if (item.Name.Length != 24 || item.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        MongoId? clone = null;
                        if (item.Value.TryGetProperty("itemTplToClone", out JsonElement c) && c.ValueKind == JsonValueKind.String && c.GetString()?.Length == 24)
                        {
                            clone = new MongoId(c.GetString());
                        }

                        listed[new MongoId(item.Name)] = clone;
                    }
                }
                catch (Exception e)
                {
                    LrState.Warn($"mod weapons: could not read {file}: {e.Message}");
                }
            }
        }

        return listed;
    }

    /// <summary>The mod pool of a new gun, or null when no complete build can come out of it.</summary>
    private static Dictionary<string, HashSet<MongoId>> BuildModPool(TemplateTable templates, GlobalTable globals, BotTable botTable, MongoId id, MongoId? clone, TemplateItem tpl)
    {
        List<Slot> slots = tpl.Properties.Slots?.ToList() ?? new();
        Dictionary<string, HashSet<MongoId>> accepts = new();
        foreach (Slot slot in slots)
        {
            HashSet<MongoId> set = new();
            foreach (SlotFilter f in slot.Properties?.Filters ?? [])
            {
                if (f.Filter != null)
                {
                    set.UnionWith(f.Filter);
                }
            }

            accepts[slot.Name ?? ""] = set;
        }

        Dictionary<string, HashSet<MongoId>> pool = new();

        // What the gun it was cloned from carries in the bots' pools, as far as the new gun's slots take it
        if (clone.HasValue && botTable.Types.TryGetValue("usec", out BotType usec) && usec?.BotInventory?.Mods != null
            && usec.BotInventory.Mods.TryGetValue(clone.Value, out Dictionary<string, HashSet<MongoId>> source))
        {
            foreach ((string slotName, HashSet<MongoId> options) in source)
            {
                if (accepts.TryGetValue(slotName, out HashSet<MongoId> ok))
                {
                    HashSet<MongoId> both = options.Where(ok.Contains).ToHashSet();
                    if (both.Count > 0)
                    {
                        pool[slotName] = both;
                    }
                }
            }
        }

        // The stock build: its parts always may come
        foreach (Preset preset in globals.ItemPresets.Values)
        {
            if (preset.Items == null || preset.Items.Count == 0 || preset.Items[0].Template != id)
            {
                continue;
            }

            string root = preset.Items[0].Id.ToString();
            foreach (Item part in preset.Items.Skip(1))
            {
                if (part.ParentId == root && !string.IsNullOrEmpty(part.SlotId))
                {
                    if (!pool.TryGetValue(part.SlotId, out HashSet<MongoId> set))
                    {
                        pool[part.SlotId] = set = new HashSet<MongoId>();
                    }

                    set.Add(part.Template);
                }
            }
        }

        foreach (Slot slot in slots)
        {
            if (slot.Required == true && (!pool.TryGetValue(slot.Name ?? "", out HashSet<MongoId> set) || set.Count == 0))
            {
                return null;
            }
        }

        // Parts the bots have no pool for (the mod's own handguards, grips...): the stock build gives them one, so the build does not stop short.
        // Only a part nobody had a pool for gets one; a pool that exists is never touched.
        HashSet<(object, MongoId)> mine = new();
        foreach (Preset preset in globals.ItemPresets.Values)
        {
            if (preset.Items == null || preset.Items.Count == 0 || preset.Items[0].Template != id)
            {
                continue;
            }

            string root = preset.Items[0].Id.ToString();
            Dictionary<string, Item> byId = preset.Items.ToDictionary(i => i.Id.ToString());
            foreach (Item part in preset.Items.Skip(1))
            {
                if (string.IsNullOrEmpty(part.ParentId) || part.ParentId == root || string.IsNullOrEmpty(part.SlotId) || !byId.TryGetValue(part.ParentId, out Item parent))
                {
                    continue;
                }

                foreach (BotType type in botTable.Types.Values)
                {
                    Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>> mods = type?.BotInventory?.Mods;
                    if (mods == null)
                    {
                        continue;
                    }

                    if (!mods.ContainsKey(parent.Template))
                    {
                        MongoId parentTpl = parent.Template;
                        mods[parentTpl] = new Dictionary<string, HashSet<MongoId>>();
                        mine.Add((mods, parentTpl));
                        _undo.Add(() => mods.Remove(parentTpl));
                    }

                    if (mine.Contains((mods, parent.Template)))
                    {
                        Dictionary<string, HashSet<MongoId>> entry = mods[parent.Template];
                        if (!entry.TryGetValue(part.SlotId, out HashSet<MongoId> set))
                        {
                            entry[part.SlotId] = set = new HashSet<MongoId>();
                        }

                        set.Add(part.Template);
                    }
                }
            }
        }

        return pool;
    }

    private static int AddToRole(BotTable botTable, string role, List<Gun> guns, Dictionary<string, List<MongoId>> ammoByCaliber, ModWeaponsConfig cfg, double maxPrice)
    {
        if (!botTable.Types.TryGetValue(role, out BotType type) || type?.BotInventory?.Equipment == null)
        {
            return 0;
        }

        BotTypeInventory inv = type.BotInventory;
        int added = 0;
        HashSet<string> calibers = new();
        foreach (Gun gun in guns)
        {
            if (gun.Price > maxPrice)
            {
                continue;
            }

            bool any = false;
            if (gun.Knife)
            {
                any = Put(inv, EquipmentSlots.Scabbard, gun.Id, cfg.WeightOfAverage);
            }
            else if (gun.WeaponClass == "pistol")
            {
                any = Put(inv, EquipmentSlots.Holster, gun.Id, cfg.WeightOfAverage);
            }
            else
            {
                any = Put(inv, EquipmentSlots.FirstPrimaryWeapon, gun.Id, cfg.WeightOfAverage);
                any |= Put(inv, EquipmentSlots.SecondPrimaryWeapon, gun.Id, cfg.WeightOfAverage);
            }

            if (!any)
            {
                continue;
            }

            added++;
            if (!gun.Knife)
            {
                // The gun's own mod pool (never over a pool that is already there)
                inv.Mods ??= new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>();
                if (!inv.Mods.ContainsKey(gun.Id))
                {
                    Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>> modPools = inv.Mods;
                    MongoId gunId = gun.Id;
                    modPools[gunId] = gun.Pool.ToDictionary(kv => kv.Key, kv => new HashSet<MongoId>(kv.Value));
                    _undo.Add(() => modPools.Remove(gunId));
                }

                calibers.Add(gun.Caliber);
                if (inv.Ammo != null && (!inv.Ammo.TryGetValue(gun.Caliber, out Dictionary<MongoId, double> pool) || pool.Count == 0) && gun.DefAmmo.HasValue)
                {
                    Dictionary<string, Dictionary<MongoId, double>> ammoPools = inv.Ammo;
                    string made = gun.Caliber;
                    ammoPools[made] = new Dictionary<MongoId, double> { [gun.DefAmmo.Value] = 1 };
                    _undo.Add(() => ammoPools.Remove(made));
                }
            }
        }

        // The mod's rounds of the calibres the new guns fire
        if (inv.Ammo != null)
        {
            foreach (string caliber in calibers)
            {
                if (!ammoByCaliber.TryGetValue(caliber, out List<MongoId> rounds) || !inv.Ammo.TryGetValue(caliber, out Dictionary<MongoId, double> pool) || pool.Count == 0)
                {
                    continue;
                }

                double weight = Math.Max(1, Math.Round(pool.Values.Average() * cfg.WeightOfAverage));
                foreach (MongoId round in rounds)
                {
                    MongoId roundId = round;
                    Dictionary<MongoId, double> roundPool = pool;
                    if (roundPool.TryAdd(roundId, weight))
                    {
                        _undo.Add(() => roundPool.Remove(roundId));
                    }
                }
            }
        }

        return added;
    }

    private static bool Put(BotTypeInventory inv, EquipmentSlots slot, MongoId id, double mult)
    {
        if (!inv.Equipment.TryGetValue(slot, out Dictionary<MongoId, double> pool) || pool == null || pool.Count == 0)
        {
            return false;
        }

        if (!pool.ContainsKey(id))
        {
            pool[id] = Math.Max(1, Math.Round(pool.Values.Average() * mult));
            _undo.Add(() => pool.Remove(id));
        }

        return true;
    }
}
