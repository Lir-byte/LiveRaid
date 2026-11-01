using System;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using LiveRaid.Client.Spectator;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>Observer raid: no group takes your hidden body for an enemy (SAIN builds its enemies from these lists too).</summary>
    public class ObserverAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer person, ref bool __result)
        {
            if (!ObserverMode.Is(person))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class ObserverEnemiesAddPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.EnemiesAdd));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer person) => !ObserverMode.Is(person);
    }

    public class ObserverCheckAndAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.CheckAndAddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer player, ref bool __result)
        {
            if (!ObserverMode.Is(player))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    /// <summary>Last word over every hostility rule (LiveRaid's PMC hostility included).</summary>
    public class ObserverIsPlayerEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(IPlayer player, ref bool __result)
        {
            if (__result && ObserverMode.Is(player))
            {
                __result = false;
            }
        }
    }

    public class ObserverIsEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsEnemy));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(IPlayer player, ref bool __result)
        {
            if (__result && ObserverMode.Is(player))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Observer raid: no scav group "warns" your hidden body. Being nobody's enemy made it a stranger to warn: EFT's scavs warn every human that is not
    /// their enemy - the nearest scav walks up, aims at him and shouts, over and over (raid 2026-10-26 20:28: Зураб Ювелир 22 minutes in the warn layer
    /// next to where the body lay, his squad leader waiting for him the whole time).
    /// </summary>
    public class ObserverWarnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotGroupWarnData), nameof(BotGroupWarnData.CollectWarnTargets));

        [PatchPostfix]
        protected static void PatchPostfix(System.Collections.Generic.Dictionary<int, BotGroupEnemyInfo> warnTargets)
        {
            Player body = ObserverMode.Body;
            if (body != null && warnTargets != null)
            {
                warnTargets.Remove(body.Id);
            }
        }
    }

    /// <summary>Observer raid: stray bullets, grenades and falls do nothing to your hidden body.</summary>
    public class ObserverDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance, ref float __result)
        {
            if (!ObserverMode.IsHealth(__instance))
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }

    public class ObserverKillPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance) => !ObserverMode.IsHealth(__instance);
    }

    public class ObserverDestroyBodyPartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DestroyBodyPart));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance) => !ObserverMode.IsHealth(__instance);
    }
}
