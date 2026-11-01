using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The paid car leaves with the bot that paid for it (F12 -> 8 "Paid car drives off"). Raid 2026-10-31 17:08: H4RD and later TGT_HQ left through the
    /// car at the dorms (Dorms V-Ex), and the car stood there for the rest of the raid as if nobody had used it. A player who pays starts the car's
    /// countdown for everyone (EExfiltrationStatus.Countdown, a shared timer), and when it is up the car drives off and the exit is gone (NotPresent).
    ///  - LiveRaid Online: the bot's payment sets the countdown on the host; Fika's own exit manager (FikaExfilManager) stamps the start, sends it to
    ///    every player, takes out the players inside who paid and sends the car off - exactly as for a human;
    ///  - single player: EFT's local game has no such clock - the car leaves on LiveRaid's (the exit turns NotPresent), unless you are inside it.
    /// The bot leaves with the car: its own exit timer is the exit's time, the same one.
    /// </summary>
    public static class CarExits
    {
        private static readonly Dictionary<ExfiltrationPoint, float> _departAt = new Dictionary<ExfiltrationPoint, float>();
        private static float _next;

        public static int Paid { get; private set; }
        public static int Departed { get; private set; }

        public static string Summary => $"paid cars started by bots {Paid}, driven off {Departed}";

        public static void Clear()
        {
            _departAt.Clear();
            _next = 0f;
            Paid = Departed = 0;
        }

        private static bool On => Settings.Enabled.Value && Settings.SpecialExits.Value && Settings.CarLeaves.Value && Net.RunsBots;

        public static bool IsCar(ExfiltrationPoint e) =>
            e?.Settings != null && e.Requirements != null && e.Requirements.Any(r => r != null && r.Requirement == ERequirementState.TransferItem);

        /// <summary>From the exit node: the bot stands at the point of its exit.</summary>
        public static void Arrived(BotOwner bot, ExfiltrationPoint e)
        {
            if (!On || bot == null || !IsCar(e) || _departAt.ContainsKey(e) || e.Status == EExfiltrationStatus.NotPresent)
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            float time = Mathf.Max(5f, e.Settings.ExfiltrationTime);
#if LIVERAID_ONLINE
            if (e.Status != EExfiltrationStatus.Countdown)
            {
                // Fika's exit manager takes it from here, for the host and every player
                e.SetStatusLogged(EExfiltrationStatus.Countdown, "LiveRaid: a bot paid for the car");
            }
#endif
            // Online: looked at a moment after Fika's own clock has sent it off
            _departAt[e] = Time.time + time + (Net.Online ? 3f : 0f);
            Paid++;
            // The bot goes with the car, not a second later
            if (bot.Exfiltration != null && bot.Exfiltration.LeaveTime > Time.time + time + 0.5f)
            {
                bot.Exfiltration.SetLeaveTime(Time.time + time);
            }

            Log.Info($"LR Extract: {p.Name} pays for the car at {e.Settings.Name.Localized()} - it drives off in {time:0} s");
        }

        public static void Tick()
        {
            if (_departAt.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            foreach (ExfiltrationPoint e in _departAt.Keys.ToList())
            {
                if (e == null)
                {
                    _departAt.Remove(e);
                    continue;
                }

                if (Time.time < _departAt[e])
                {
                    continue;
                }

#if !LIVERAID_ONLINE
                // You are in the car (or have paid for it): it is your ride, EFT's own way decides
                bool yours = e.Entered.Any(pl => pl != null && Net.IsHuman(pl)) || e.QueuedPlayers.Any(id => Net.Humans().Any(h => h.ProfileId == id));
                if (!yours && e.Status != EExfiltrationStatus.NotPresent)
                {
                    e.SetStatusLogged(EExfiltrationStatus.NotPresent, "LiveRaid: the car drove off");
                }
#endif
                _departAt.Remove(e);
                Departed++;
                Log.Info($"LR Extract: the car at {e.Settings.Name.Localized()} has driven off - the exit is {(e.Status == EExfiltrationStatus.NotPresent ? "closed" : "still open (a player is in it)")}");
            }
        }
    }
}
