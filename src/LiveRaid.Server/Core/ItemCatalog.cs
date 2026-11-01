using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace LiveRaid.Server.Core;

/// <summary>
/// Precomputed facts about items that gear shaping needs on every bot: price, weapon class, ammo penetration, plate class.
/// </summary>
public class ItemCatalog
{
    private readonly Dictionary<MongoId, double> _price = new();
    private readonly Dictionary<MongoId, string> _weaponClass = new();
    private readonly Dictionary<MongoId, int> _penetration = new();
    private readonly Dictionary<MongoId, int> _armorClass = new();
    private readonly Dictionary<MongoId, MongoId> _parent = new();
    private readonly Dictionary<MongoId, string> _name = new();
    private readonly Dictionary<MongoId, double> _presetValue = new();

    public int Count => _price.Count;

    public ItemCatalog(TemplateTable templates, GlobalTable globals)
    {
        foreach (HandbookItem hb in templates.Handbook.Items)
        {
            _price[hb.Id] = hb.Price ?? 0;
        }

        foreach ((MongoId id, double flea) in templates.Prices)
        {
            if (!_price.TryGetValue(id, out double hb) || flea > hb)
            {
                _price[id] = flea;
            }
        }

        foreach ((MongoId id, TemplateItem item) in templates.Items)
        {
            _parent[id] = item.Parent;
            _name[id] = item.Name ?? "";
            TemplateItemProperties props = item.Properties;
            if (props == null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(props.WeapClass))
            {
                _weaponClass[id] = props.WeapClass;
            }

            if (props.PenetrationPower.HasValue)
            {
                _penetration[id] = props.PenetrationPower.Value;
            }

            if (props.ArmorClass.HasValue)
            {
                _armorClass[id] = props.ArmorClass.Value;
            }
        }

        // A bare weapon receiver or plate carrier says little about the kit; its stock build
        // (default mods, default plates) is what a bot actually walks around with.
        Dictionary<MongoId, bool> fromDefault = new();
        foreach (Preset preset in globals.ItemPresets.Values)
        {
            if (preset.Encyclopedia is not MongoId tpl || preset.Items == null)
            {
                continue;
            }

            double value = preset.Items.Sum(i => Price(i.Template));
            bool isDefault = !preset.ChangeWeaponName;
            if (!_presetValue.ContainsKey(tpl) || (isDefault && !fromDefault[tpl]) || (isDefault == fromDefault[tpl] && value > _presetValue[tpl]))
            {
                _presetValue[tpl] = value;
                fromDefault[tpl] = isDefault;
            }
        }
    }

    /// <summary>Price of the item as a bot would carry it: the stock build when one exists.</summary>
    public double Value(MongoId id) => Math.Max(Price(id), _presetValue.TryGetValue(id, out double v) ? v : 0);

    public string Name(MongoId id) => _name.TryGetValue(id, out string n) ? n : id.ToString();

    public double Price(MongoId id) => _price.TryGetValue(id, out double p) ? p : 0;

    public string WeaponClass(MongoId id) => _weaponClass.TryGetValue(id, out string c) ? c : null;

    public int Penetration(MongoId id) => _penetration.TryGetValue(id, out int p) ? p : 0;

    public int ArmorClass(MongoId id) => _armorClass.TryGetValue(id, out int c) ? c : 0;

    public bool IsChildOf(MongoId id, MongoId baseClass)
    {
        MongoId current = id;
        for (int depth = 0; depth < 12; depth++)
        {
            if (!_parent.TryGetValue(current, out MongoId parent) || parent.IsEmpty)
            {
                return false;
            }

            if (parent == baseClass)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }
}
