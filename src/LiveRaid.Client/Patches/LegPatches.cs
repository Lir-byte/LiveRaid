using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// No jumping on a broken leg (F12 -> 4 "No jumping on broken legs"). A jump with a fractured or blacked-out leg hurts it again on landing - players with a
    /// broken leg walk round an obstacle. SAIN's rush jumps (RushEnemyAction), QuestingBots' "jump when stuck" and LiveRaid's route jumps all go through
    /// MovementContext.TryJump / PlayerAnimatorEnableJump; for a bot with a leg broken or destroyed both do nothing. The human player is never touched.
    /// </summary>
    public static class LegCheck
    {
        public static int Refused { get; private set; }

        public static void ClearCounters() => Refused = 0;

        public static string Summary => $"jumps skipped on a broken leg {Refused}";

        public static bool MayJump(Player player)
        {
            if (!Settings.NoJumpOnBrokenLegs.Value || player == null || !player.IsAI || player.HealthController == null)
            {
                return true;
            }

            var health = player.HealthController;
            bool hurt = health.IsBodyPartBroken(EBodyPart.LeftLeg) || health.IsBodyPartBroken(EBodyPart.RightLeg)
                        || health.IsBodyPartDestroyed(EBodyPart.LeftLeg) || health.IsBodyPartDestroyed(EBodyPart.RightLeg);
            if (hurt)
            {
                Refused++;
                Log.Detail("task", () => $"{player.Profile?.Nickname} does not jump: a leg is broken");
            }

            return !hurt;
        }
    }

    public class NoJumpPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), nameof(MovementContext.TryJump));

        [PatchPrefix]
        protected static bool PatchPrefix(Player ____player) => Diag.Allow(() => LegCheck.MayJump(____player), "NoJumpPatch");
    }

    public class NoJumpAnimatorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), nameof(MovementContext.PlayerAnimatorEnableJump));

        [PatchPrefix]
        protected static bool PatchPrefix(bool enabled, Player ____player) => !enabled || Diag.Allow(() => LegCheck.MayJump(____player), "NoJumpAnimatorPatch");
    }
}
