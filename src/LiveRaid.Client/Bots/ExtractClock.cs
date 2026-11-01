using System.Collections.Generic;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// When PMCs leave the raid (F12 -> 4 "Online-like extract times").
    /// Raid 2026-10-22 16:49: three of the PMCs that left headed out at minutes 10, 13 and 15 of a 40-minute raid - their personal moment (rushers: 40-75 %
    /// of the raid left) or a bag worth carrying home. Online the first squads go around minute 10, most between the middle and the last minutes.
    ///  - Before the "early" mark - minute 20, or half the raid if it is shorter - a bot whose moment has come leaves only by a roll: 3 % at the start
    ///    rising to 20 % at the mark (urgent reasons - badly hurt with nothing to treat it, half the squad dead - at least 60 %).
    ///  - Refused: its moment moves to a later point of the raid (between the mark and the last minutes, most likely around 70 % gone), and nothing but an
    ///    urgent reason sends it out before the mark.
    ///  - After the mark the character's own moment counts as before.
    /// </summary>
    public static class ExtractClock
    {
        private const float EarlyMinutes = 20f;

        private static readonly Dictionary<string, bool> _rolled = new Dictionary<string, bool>();
        private static readonly System.Random _rng = new System.Random();

        public static int Kept { get; private set; }
        public static int Allowed { get; private set; }

        public static string Summary => $"early extracts allowed {Allowed}, kept in the raid for later {Kept}";

        public static void Clear()
        {
            _rolled.Clear();
            Kept = Allowed = 0;
        }

        /// <summary>Minutes since the raid began, and the early mark for this raid.</summary>
        private static bool Clock(out float elapsed, out float mark, out float total)
        {
            elapsed = mark = total = 0f;
            float left = RaidHelpers.GetRemainingRaidTimeSeconds();
            float fraction = RaidHelpers.GetRaidTimeRemainingFraction();
            if (fraction <= 0.01f || left <= 0f)
            {
                return false;
            }

            total = left / fraction / 60f;
            elapsed = total - left / 60f;
            mark = Mathf.Min(EarlyMinutes, total * 0.5f);
            return true;
        }

        /// <summary>
        /// The bot's moment to leave has come (its personal hour, or the raid has gone so that it would leave now). True: it goes. False: it stays -
        /// and for a refused personal hour <paramref name="p"/>'s hour has been moved later.
        /// </summary>
        public static bool MayLeave(BotProfile p, bool urgent, bool personalHour, out string note)
        {
            note = null;
            if (!Settings.ExtractCurve.Value || p == null || p.Kind != BotKind.Pmc || !Clock(out float elapsed, out float mark, out float total) || elapsed >= mark)
            {
                return true;
            }

            float chance = 0.03f + 0.17f * Mathf.Pow(elapsed / mark, 2f);
            if (urgent)
            {
                chance = Mathf.Max(chance, 0.6f);
            }
            else if (_rolled.TryGetValue(p.ProfileId, out bool earlier))
            {
                // One roll per bot before the mark (rolled again every check, a 5 % chance would come up within minutes)
                if (!earlier && personalHour)
                {
                    Defer(p, elapsed, mark, total, out note);
                }

                return earlier;
            }

            bool go = _rng.NextDouble() < chance;
            if (!urgent)
            {
                _rolled[p.ProfileId] = go;
            }

            if (go)
            {
                Allowed++;
                note = $"one of the few who leave this early (minute {elapsed:0}, chance {chance:P0})";
                return true;
            }

            Kept++;
            if (personalHour)
            {
                Defer(p, elapsed, mark, total, out note);
            }
            else
            {
                note = $"would leave at minute {elapsed:0}, but that is too early ({chance:P0}) - stays";
            }

            Log.Info($"LR Extract: {p.Name} ({p.Style}) {note}");
            return false;
        }

        /// <summary>A later moment: between the early mark and the last minutes, most likely around 70 % of the raid gone.</summary>
        private static void Defer(BotProfile p, float elapsed, float mark, float total, out string note)
        {
            float a = Mathf.Max(mark, elapsed + 2f) / total;
            float b = Mathf.Max(a + 0.02f, 0.93f);
            float c = Mathf.Clamp(0.7f, a, b);
            double u = _rng.NextDouble();
            float f = u < (c - a) / (b - a)
                ? a + Mathf.Sqrt((float)u * (b - a) * (c - a))
                : b - Mathf.Sqrt((float)(1 - u) * (b - a) * (b - c));
            p.ExtractAtRemaining = Mathf.Clamp(1f - f, 0.05f, 0.95f);
            note = $"would leave at minute {elapsed:0} - too early for most players, stays until about minute {f * total:0} ({p.ExtractAtRemaining:P0} left)";
        }
    }
}
