using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// At the exit is at the exit (F12 -> 8 "Leave when standing at the exit"). EFT starts a bot's exit timer only within 1 m of the exit's AI point
    /// (GoToExfiltrationPointNode); some points stand where a bot cannot quite get - by a fence, on a ledge. Raid 2026-10-31 05:46: Hjal and J0nathan550
    /// came to Old Gas Station, stood 1-2 m off its point, EFT's exit layer gave the stand up as "stuck" every 25-60 s, they went looting and came back -
    /// for twelve minutes, until the raid was over. A bot in EFT's exit layer that stands still within 4 m of its exit point (same floor) for 3 s is
    /// at the exit: its exit timer runs (the squad's wait for each other still holds - SquadExtract), and when it is up the bot leaves the raid.
    /// </summary>
    public static class ExitArrival
    {
        private class Stand
        {
            public Vector3 Pos;
            public float Since;
            public float LeaveAt = -1f;
        }

        private static readonly Dictionary<string, Stand> _stands = new Dictionary<string, Stand>();
        private static float _next;

        public static int Left { get; private set; }

        public static string Summary => $"bots let out of the raid from close to their exit point {Left}";

        public static void Clear()
        {
            _stands.Clear();
            _next = 0f;
            Left = 0;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.ExitArrival.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                // A bot being spawned has no brain yet: BigBrain's layer name threw for it and the throw cut the check short for every bot after it (raid 15:59, 47 times)
                if (p == null || p.Kind == BotKind.Other || bot.BotState != EBotState.Active || Raid.RaidMonitor.SafeLayerName(bot) != "Exfiltration")
                {
                    if (bot != null)
                    {
                        _stands.Remove(bot.ProfileId);
                    }

                    continue;
                }

                AIExfiltrationPoint point = bot.PatrollingData?.ExfiltrationData?.CachedExfiltrationPoint;
                if (point == null)
                {
                    continue;
                }

                Vector3 at = point.GetPosition(bot);
                Vector3 d = at - bot.Position;
                float dy = Mathf.Abs(d.y);
                d.y = 0f;
                float dist = d.magnitude;
                if (dist < 1f || dist > 4f || dy > 2f || bot.Memory?.HaveEnemy == true)
                {
                    // EFT's own case (within 1 m), not at the exit, or a fight
                    _stands.Remove(bot.ProfileId);
                    continue;
                }

                if (!_stands.TryGetValue(bot.ProfileId, out Stand s) || (s.Pos - bot.Position).sqrMagnitude > 0.5f * 0.5f)
                {
                    _stands[bot.ProfileId] = new Stand { Pos = bot.Position, Since = now };
                    continue;
                }

                if (now - s.Since < 3f)
                {
                    continue;
                }

                if (s.LeaveAt < 0f)
                {
                    float timer = Mathf.Max(1f, point.ExfiltrationTime);
                    if (!SquadExtract.AllowTimer(bot, now + timer))
                    {
                        continue;
                    }

                    s.LeaveAt = now + timer;
                    Log.Info($"LR Extract: {p.Name} stands {dist:0.0} m from the exit point of {point.Name} and cannot get closer - at the exit, leaves in {timer:0} s");
                    continue;
                }

                if (now >= s.LeaveAt)
                {
                    _stands.Remove(bot.ProfileId);
                    Left++;
                    bot.LeaveData.RemoveFromMap();
                }
            }
        }
    }
}
