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
