using System.Reflection;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace LiveRaid.Server.Patches;

/// <summary>
/// No PMC goes into the raid without spare rounds for its gun (raid 2026-10-22: "some PMCs spawn without any ammo").
/// SPT puts spare magazines only into the rig and the pockets: with no rig (LiveRaid lets poor PMCs go without armour and rig) or full pockets they do
/// not fit, the stacks for the secure container depend on the bot config, and an odd weapon/ammo pairing can leave the magazine itself unusable -
/// a PMC then had what was in its gun and nothing more. Right after each weapon is generated: if the bot carries fewer spare rounds of that gun's
/// calibre than two magazines' worth, stacks of the round the gun is loaded with go into its secure container (bots refill their magazines from
/// there), else into pockets / rig. Every fix is written to the server log.
/// </summary>
[Injectable]
public class SpareAmmoPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotInventoryGenerator).GetMethod(nameof(BotInventoryGenerator.AddWeaponAndMagazinesToInventory), BindingFlags.Instance | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(MongoId botId, DesiredWeapons weaponSlot, BotBaseInventory botInventory, BotGenerationDetails botGenerationDetails)
    {
        if (!LrState.Config.Bots.PmcSpareAmmo || botGenerationDetails == null || !botGenerationDetails.IsPmc || LrState.Templates == null || LrState.BotHelper == null)
        {
            return;
        }

        try
        {
            Ensure(botId, weaponSlot.Slot, botInventory, botGenerationDetails);
        }
        catch (Exception e)
        {
            LrState.Error("Spare ammo check failed (bot kept as generated)", e);
        }
    }

    private static TemplateItem Tpl(MongoId id) => LrState.Templates.Items.TryGetValue(id, out TemplateItem t) ? t : null;

    private static void Ensure(MongoId botId, EquipmentSlots slot, BotBaseInventory inv, BotGenerationDetails details)
    {
        List<Item> items = inv.Items;
        string equipmentId = inv.Equipment?.ToString();
        Item weapon = items?.FirstOrDefault(i => i.ParentId == equipmentId && i.SlotId == slot.ToString());
        TemplateItem weaponTpl = weapon != null ? Tpl(weapon.Template) : null;
        string caliber = weaponTpl?.Properties?.AmmoCaliber;
        if (weaponTpl == null || string.IsNullOrEmpty(caliber))
        {
            return;
        }

        // Everything attached to the gun (magazine, rounds in it and in the chamber)
        HashSet<string> inGun = new() { weapon.Id.ToString() };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Item i in items)
            {
                if (i.ParentId != null && inGun.Contains(i.ParentId) && inGun.Add(i.Id.ToString()))
                {
                    grew = true;
                }
            }
        }

        MongoId? round = null;
        double magazineSize = 0;
        foreach (Item i in items.Where(i => inGun.Contains(i.Id.ToString())))
        {
            TemplateItem t = Tpl(i.Template);
            if (t?.Properties?.Caliber == caliber && round == null)
            {
                round = i.Template;
            }

            if (i.SlotId == "mod_magazine")
            {
                magazineSize = t?.Properties?.Cartridges?.FirstOrDefault()?.MaxCount ?? 0;
            }
        }

        round ??= weaponTpl.Properties.DefAmmo;
        TemplateItem roundTpl = round.HasValue ? Tpl(round.Value) : null;
        if (roundTpl == null)
        {
            return;
        }

        // Spare rounds of that calibre the bot carries: loose stacks and rounds in spare magazines
        double spare = 0;
        foreach (Item i in items)
        {
            if (inGun.Contains(i.Id.ToString()))
            {
                continue;
            }

            if (Tpl(i.Template)?.Properties?.Caliber == caliber)
            {
                spare += i.Upd?.StackObjectsCount ?? 1;
            }
        }

        double want = Math.Max(30, 2 * Math.Max(magazineSize, 5));
        if (spare >= want)
        {
            return;
        }

        int stackMax = Math.Max(1, roundTpl.Properties.StackMaxSize ?? 60);
        double toAdd = want - spare;
        int added = 0;
        while (toAdd > 0 && added < 400)
        {
            int count = (int)Math.Min(stackMax, Math.Ceiling(toAdd));
            MongoId id = new();
            List<Item> stack = [new Item { Id = id, Template = round.Value, Upd = new Upd { StackObjectsCount = count } }];
            ItemAddedResult result = LrState.BotHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.SecuredContainer], id, round.Value, stack, inv);
            if (result != ItemAddedResult.SUCCESS)
            {
                result = LrState.BotHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.Pockets, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack], id, round.Value, stack, inv);
            }

            if (result != ItemAddedResult.SUCCESS)
            {
                break;
            }

            added += count;
            toAdd -= count;
        }

        LrState.SpareAmmoFixed++;
        LrState.Warn($"PMC {details.Role} ({slot} {weaponTpl.Name}, {caliber}) had {spare:0} spare rounds (wanted {want:0}): added {added} x {roundTpl.Name}" +
                     (added == 0 ? " - no room anywhere, the bot keeps only what is in its gun" : ""));
    }
}

/// <summary>
/// No PMC goes into the raid with a gun and no spare magazine (raid 2026-11-08, Customs: h4rd111 fired his one magazine in the first fight and had
/// none left - 760 loose rounds in the bag, nothing to put them in, the rest of the fight with a knife). SPT's InventoryMagGen puts spare magazines only
/// into the rig and pockets and fails quietly when they do not fit (a PMC without a rig, a full vest): 300 generated PMCs - 57 with no spare magazine
/// at all, 42 with one. A player never leaves the hideout like that. Right after each primary weapon is generated: a bot with fewer spare magazines of the
/// gun's own magazine than it was rolled (2-5, like the kits of live players) gets full ones of the round the gun is loaded with - rig, then pockets,
/// then the bag, the secure container last (the client's MagKeeper carries them from there into the rig). Only the starting kit is topped up;
/// nothing is made during the raid.
/// </summary>
[Injectable]
public class SpareMagazinePatch : AbstractPatch
{
    private static readonly Random Rng = new();

    protected override MethodBase GetTargetMethod() =>
        typeof(BotInventoryGenerator).GetMethod(nameof(BotInventoryGenerator.AddWeaponAndMagazinesToInventory), BindingFlags.Instance | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(MongoId botId, DesiredWeapons weaponSlot, BotBaseInventory botInventory, BotGenerationDetails botGenerationDetails)
    {
        if (!LrState.Config.Bots.PmcSpareMagazines || botGenerationDetails == null || !botGenerationDetails.IsPmc || LrState.Templates == null || LrState.BotHelper == null)
        {
            return;
        }

        // Rifles, SMGs and the like only: the holster gun and the knife keep what SPT gave them
        if (weaponSlot.Slot != EquipmentSlots.FirstPrimaryWeapon && weaponSlot.Slot != EquipmentSlots.SecondPrimaryWeapon)
        {
            return;
        }

        try
        {
            Ensure(botId, weaponSlot.Slot, botInventory, botGenerationDetails);
        }
        catch (Exception e)
        {
            LrState.Error("Spare magazine check failed (bot kept as generated)", e);
        }
    }

    private static TemplateItem Tpl(MongoId id) => LrState.Templates.Items.TryGetValue(id, out TemplateItem t) ? t : null;

    private static int RollWant()
    {
        // 2 - 20 %, 3 - 35 %, 4 - 30 %, 5 - 15 %
        double r = Rng.NextDouble();
        return r < 0.20 ? 2 : r < 0.55 ? 3 : r < 0.85 ? 4 : 5;
    }

    private static void Ensure(MongoId botId, EquipmentSlots slot, BotBaseInventory inv, BotGenerationDetails details)
    {
        List<Item> items = inv.Items;
        string equipmentId = inv.Equipment?.ToString();
        Item weapon = items?.FirstOrDefault(i => i.ParentId == equipmentId && i.SlotId == slot.ToString());
        TemplateItem weaponTpl = weapon != null ? Tpl(weapon.Template) : null;
        if (weaponTpl == null)
        {
            return;
        }

        // Everything attached to the gun
        HashSet<string> inGun = new() { weapon.Id.ToString() };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Item i in items)
            {
                if (i.ParentId != null && inGun.Contains(i.ParentId) && inGun.Add(i.Id.ToString()))
                {
                    grew = true;
                }
            }
        }

        Item gunMag = items.FirstOrDefault(i => inGun.Contains(i.Id.ToString()) && i.SlotId == "mod_magazine");
        TemplateItem magTpl = gunMag != null ? Tpl(gunMag.Template) : null;
        // Guns with an internal magazine (shotguns, bolt rifles) and ones whose magazine is not in the data have nothing to spare
        double size = magTpl?.Properties?.Cartridges?.FirstOrDefault()?.MaxCount ?? 0;
        if (magTpl == null || size < 2)
        {
            return;
        }

        // The round the gun is loaded with
        Item loaded = items.FirstOrDefault(i => i.ParentId == gunMag.Id.ToString() && i.SlotId == "cartridges");
        MongoId? round = loaded?.Template ?? weaponTpl.Properties?.DefAmmo;
        TemplateItem roundTpl = round.HasValue ? Tpl(round.Value) : null;
        if (roundTpl == null)
        {
            return;
        }

        // Spare magazines of that kind (not in the gun), anywhere on the bot
        int have = items.Count(i => i.Template == gunMag.Template && !inGun.Contains(i.Id.ToString()) && i.ParentId != null && i.SlotId != "mod_magazine");
        int want = RollWant();
        if (have >= want)
        {
            return;
        }

        int added = 0;
        string where = "";
        for (int n = have; n < want; n++)
        {
            MongoId magId = new();
            List<Item> mag =
            [
                new Item { Id = magId, Template = gunMag.Template },
                new Item { Id = new MongoId(), Template = round.Value, ParentId = magId.ToString(), SlotId = "cartridges", Location = 0, Upd = new Upd { StackObjectsCount = (int)size } },
            ];
            ItemAddedResult result = LrState.BotHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.TacticalVest, EquipmentSlots.Pockets], magId, gunMag.Template, mag, inv);
            if (result != ItemAddedResult.SUCCESS)
            {
                result = LrState.BotHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.Backpack, EquipmentSlots.SecuredContainer], magId, gunMag.Template, mag, inv);
                where = " (some in the bag / secure container - rig and pockets were full)";
            }

            if (result != ItemAddedResult.SUCCESS)
            {
                break;
            }

            added++;
        }

        if (added > 0)
        {
            LrState.SpareMagazinesAdded += added;
            LrState.Debug($"PMC {details.Role} ({slot} {weaponTpl.Name}) had {have} spare magazines, wanted {want}: added {added} x {magTpl.Name}{where}");
        }
        else
        {
            LrState.SpareMagazinesNoRoom++;
            LrState.Warn($"PMC {details.Role} ({slot} {weaponTpl.Name}) has {have} spare magazines (wanted {want}) and no room anywhere for more");
        }
    }
}
