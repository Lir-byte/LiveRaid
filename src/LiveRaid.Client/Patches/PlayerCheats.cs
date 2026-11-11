using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// Your own character, for testing (F12 -> After death): "God mode" (no damage, no death) and "Invisible to bots" (no bot group takes you for an enemy -
    /// the same lists the observer raid keeps its hidden body out of).
    /// </summary>
    internal static class PlayerCheats
    {
        private static Player Me => Singleton<GameWorld>.Instance?.MainPlayer;

        public static bool IsMe(IPlayer person)
        {
            Player me = Me;
            return me != null && person != null && person.ProfileId == me.ProfileId;
        }

        public static bool Invisible(IPlayer person) => Settings.Enabled.Value && Settings.PlayerInvisible.Value && IsMe(person);

        public static bool God(object health)
        {
            if (!Settings.Enabled.Value || !Settings.PlayerGodMode.Value)
            {
                return false;
            }

            Player me = Me;
            return me != null && ReferenceEquals(health, me.ActiveHealthController);
        }
    }

    public class CheatAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer person, ref bool __result)
        {
            if (!PlayerCheats.Invisible(person))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class CheatEnemiesAddPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.EnemiesAdd));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer person) => !PlayerCheats.Invisible(person);
    }

    public class CheatCheckAndAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.CheckAndAddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(IPlayer player, ref bool __result)
        {
            if (!PlayerCheats.Invisible(player))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    public class CheatIsPlayerEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(IPlayer player, ref bool __result)
        {
            if (__result && PlayerCheats.Invisible(player))
            {
                __result = false;
            }
        }
    }

    public class CheatIsEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsEnemy));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(IPlayer player, ref bool __result)
        {
            if (__result && PlayerCheats.Invisible(player))
            {
                __result = false;
            }
        }
    }

    /// <summary>No scav group "warns" an invisible player either (a stranger that is nobody's enemy is warned by EFT's scavs).</summary>
    public class CheatWarnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotGroupWarnData), nameof(BotGroupWarnData.CollectWarnTargets));

        [PatchPostfix]
        protected static void PatchPostfix(Dictionary<int, BotGroupEnemyInfo> warnTargets)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me != null && warnTargets != null && PlayerCheats.Invisible(me))
            {
                warnTargets.Remove(me.Id);
            }
        }
    }

    public class CheatDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance, ref float __result)
        {
            if (!PlayerCheats.God(__instance))
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }

    public class CheatKillPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance) => !PlayerCheats.God(__instance);
    }

    public class CheatDestroyBodyPartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DestroyBodyPart));

        [PatchPrefix]
        protected static bool PatchPrefix(ActiveHealthController __instance) => !PlayerCheats.God(__instance);
    }
}
