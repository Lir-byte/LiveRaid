using Comfort.Common;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Could a human watching the raid see this spot right now? Your eyes, or the camera after your death - and in LiveRaid Online the eyes of every
    /// player of the group (a bot that "slips" or is moved along a route must not jump in front of anybody).
    /// </summary>
    public static class ViewerSight
    {
        public static bool CanSee(Vector3 point, float range = 200f)
        {
            if (Spectator.SpectatorSession.Active && EFT.CameraControl.CameraManager.Exist)
            {
                Transform cam = EFT.CameraControl.CameraManager.Instance.Camera.transform;
                return Sees(cam.position, cam.forward, point, range);
            }

            foreach (Player human in Net.Humans())
            {
                if (Sees(human.Position + Vector3.up * 1.6f, human.LookDirection, point, range))
                {
                    return true;
                }
            }

            // Online: you are dead and look around with Fika's free camera
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (Net.Online && !Net.IsHeadless && me != null && me.HealthController?.IsAlive != true && EFT.CameraControl.CameraManager.Exist)
            {
                Transform cam = EFT.CameraControl.CameraManager.Instance.Camera?.transform;
                return cam != null && Sees(cam.position, cam.forward, point, range);
            }

            return false;
        }

        private static bool Sees(Vector3 eye, Vector3 look, Vector3 point, float range)
        {
            Vector3 to = point - eye;
            if (to.sqrMagnitude > range * range || Vector3.Angle(look, to) > 75f)
            {
                return false;
            }

            return !Physics.Linecast(eye, point, LayersMaskController.HighPolyWithTerrainMask);
        }
    }
}
