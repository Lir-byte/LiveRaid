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
/// Money for a paid exit (data/config.json bots.pmcExitMoney). Online many players go in with roubles in the secure container for the car
/// out (Customs, Woods, Interchange, Streets: 5-10k). PMCs started with nothing, so the client could never send them to a paid exit. Now half
/// of them (a quarter of the bums, by the kit's level) carry 6-40k roubles in the secure container, where they are neither looted nor in the way.
/// </summary>
[Injectable]
public class ExitMoneyPatch : AbstractPatch
{
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    public static int Given;

    protected override MethodBase GetTargetMethod() =>
        typeof(BotInventoryGenerator).GetMethod(nameof(BotInventoryGenerator.GenerateInventory), BindingFlags.Instance | BindingFlags.Public);

    [PatchPostfix]
    public static void Postfix(MongoId botId, BotGenerationDetails botGenerationDetails, BotBaseInventory __result)
    {
        if (!LrState.Config.Bots.PmcExitMoney || __result == null || botGenerationDetails == null || !botGenerationDetails.IsPmc || botGenerationDetails.IsPlayerScav
            || LrState.BotHelper == null)
        {
            return;
        }

        try
        {
            GenContext ctx = GenContext.Current;
            int level = ctx != null && ctx.KitLevel > 0 ? ctx.KitLevel : botGenerationDetails.BotLevel;
            Random rng = Random.Shared;
            if (rng.NextDouble() > (level < 15 ? 0.25 : 0.5))
            {
                return;
            }

            int amount = (6 + rng.Next(35)) * 1000;
            MongoId id = new();
            List<Item> stack = [new Item { Id = id, Template = Roubles, Upd = new Upd { StackObjectsCount = amount } }];
            ItemAddedResult result = LrState.BotHelper.AddItemWithChildrenToEquipmentSlot(botId, [EquipmentSlots.SecuredContainer], id, Roubles, stack, __result);
            if (result != ItemAddedResult.SUCCESS)
            {
                // The secure container is full of spare rounds (SPT fills it): one stack of them becomes the money, in its place
                string equipment = __result.Equipment?.ToString();
                Item secure = __result.Items?.FirstOrDefault(i => i.ParentId == equipment && i.SlotId == "SecuredContainer");
                Item rounds = secure == null ? null : __result.Items.FirstOrDefault(i => i.ParentId == secure.Id.ToString()
                                                                                        && LrState.Catalog?.IsChildOf(i.Template, BaseClasses.AMMO) == true);
                if (rounds == null)
                {
                    return;
                }

                rounds.Template = Roubles;
                rounds.Upd ??= new Upd();
                rounds.Upd.StackObjectsCount = amount;
            }

            Interlocked.Increment(ref Given);
        }
        catch (Exception e)
        {
            LrState.Error("Exit money failed (bot kept as generated)", e);
        }
    }
}
