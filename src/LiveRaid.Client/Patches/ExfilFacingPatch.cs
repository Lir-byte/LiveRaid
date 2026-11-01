using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// A bot on EFT's way out (its "Exfiltration" layer, GoToExfiltrationPointNode) walks there facing the way it goes.
    /// Raid 2026-10-30 23:28: RedBeard and Traveler left for Trailer Park walking backwards for two minutes, eyes fixed on one spot - EFT's node only
    /// orders the path and the sprint and never turns the head, so the bot kept the last look anyone gave it: the body it had been lying over when the
    /// decision to leave cut the looting short. Facing backwards, the sprint EFT asks for never came either.
    ///
    /// At the point itself (0.10.13): EFT looks at "where it stood a frame ago + standing height" - a point inside the bot's own head, so the look
    /// direction swung with every centimetre the body settled; raid 2026-10-31 17:08: H4RD and TGT_HQ twitched on the spot for the whole minute of the
    /// car at the dorms. A player waiting for his exit crouches and watches the way he came: so does the bot (F12 -> 8 "Wait at the exit like a player").
    /// The paid car leaves when its time is up (<see cref="Bots.CarExits"/>).
    /// </summary>
    public class ExfilFacingPatch : ModulePatch
    {
        // A point some metres back along the way the bot came, per bot: where it looks while it waits
        private static readonly Dictionary<string, Vector3> _crumb = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, Vector3> _lookAt = new Dictionary<string, Vector3>();

        public static int Frames { get; private set; }
        public static int Waits { get; private set; }

        public static void Clear()
        {
            Frames = Waits = 0;
            _crumb.Clear();
            _lookAt.Clear();
        }

        public static string Summary => $"frames turned to face the way out {Frames}, waits at the exit crouched and watching the way it came {Waits}";

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GoToExfiltrationPointNode), nameof(GoToExfiltrationPointNode.UpdateNodeByBrain));

        [PatchPostfix]
        protected static void PatchPostfix(GoToExfiltrationPointNode __instance)
        {
            try
            {
                BotOwner bot = __instance._owner;
                if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
                {
                    return;
                }

                AIExfiltrationPoint point = bot.PatrollingData?.ExfiltrationData?.CachedExfiltrationPoint;
                bool there = point != null && (bot.Position - point.GetPosition(bot)).sqrMagnitude < 1f;
                if (!there)
                {
                    _lookAt.Remove(bot.ProfileId);
                    if (bot.Mover?.HasPathAndNoComplete == true)
                    {
                        bot.Steering.LookToMovingDirection();
                        Frames++;
                        // The crumb trails 6 m behind: where the bot came from when it stops
                        if (!_crumb.TryGetValue(bot.ProfileId, out Vector3 c))
                        {
                            _crumb[bot.ProfileId] = bot.Position;
                        }
                        else if ((bot.Position - c).sqrMagnitude > 6f * 6f)
                        {
                            _crumb[bot.ProfileId] = bot.Position + (c - bot.Position).normalized * 6f;
                        }
                    }

                    return;
                }

                Bots.CarExits.Arrived(bot, point.ExfiltrationPoint);
                if (!Settings.Enabled.Value || !Settings.ExitWait.Value)
                {
                    return;
                }

                if (!_lookAt.TryGetValue(bot.ProfileId, out Vector3 look))
                {
                    Vector3 back = _crumb.TryGetValue(bot.ProfileId, out Vector3 c) && (c - bot.Position).sqrMagnitude > 4f
                        ? c - bot.Position
                        : -bot.LookDirection;
                    back.y = 0f;
                    if (back.sqrMagnitude < 0.01f)
                    {
                        back = Vector3.forward;
                    }

                    look = bot.Position + back.normalized * 8f + Vector3.up * 0.9f;
                    _lookAt[bot.ProfileId] = look;
                    Waits++;
                    Log.Detail("extract", $"{bot.Profile.Nickname} waits at {point.ExfiltrationPoint?.Settings?.Name ?? "its exit"} crouched, watching the way it came");
                }

                bot.SetPose(0.25f);
                bot.Steering.LookToPoint(look);
            }
            catch (System.Exception e)
            {
                Diag.Report("ExfilFacingPatch", e);
            }
        }
    }
}
