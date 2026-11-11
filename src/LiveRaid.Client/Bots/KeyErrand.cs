using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Raid;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A bot that carries the key of a locked room goes to see what is behind it (F12 -> 2 "Own key errand"). Raids 2026-11-07 / 11-08 (1.2.7): 18-25 bots a raid
    /// carried a door key, 1-6 of them ever held the "LR Locked" quest of their door, 0-2 doors a raid were opened (Customs: 36 carriers, none opened) -
    /// the locked-room quest competed with every other quest of QuestingBots on equal terms and mostly lost. Online a player who has the key of the dorms
    /// room goes there, not "when it comes up". Each key carrier rolls once per raid whether this is its errand (chance from F12); a bot that has it
    /// weighs the quest of its own door x3.5, the others as before. No forced walk: QuestingBots still picks the quest, checks the way and the key, and
    /// the room is searched whole when the door is open (RoomPeek / RoomSweep).
    /// </summary>
    public static class KeyErrand
    {
        private static readonly Dictionary<string, bool> _keen = new Dictionary<string, bool>();
        private static int _carriers;
        private static int _keenCount;

        public static string Summary => $"key errands: {_keenCount} of {_carriers} bots with a door key took their door as an errand, a near door weighed more {Near} times";

        public static void Clear()
        {
            _keen.Clear();
            _carriers = 0;
            _keenCount = 0;
            Near = 0;
        }

        /// <summary>QuestingBots' desirability of a locked-room quest the bot holds the key to.</summary>
        public static float Multiplier(BotOwner bot, LockedSpot door = null)
        {
            if (!Settings.Enabled.Value || !Settings.KeyErrand.Value || bot == null)
            {
                return 1f;
            }

            if (!_keen.TryGetValue(bot.ProfileId, out bool keen))
            {
                keen = Random.value < Mathf.Clamp01(Settings.KeyErrandChance.Value);
                _keen[bot.ProfileId] = keen;
                _carriers++;
                if (keen)
                {
                    _keenCount++;
                }
            }

            if (!keen)
            {
                return 1f;
            }

            // 1.3.0, "a door on the way": 3.5 for a far door (the 1.2.9 raids: errands of 275-480 m were dropped, 3 doors opened in 8 raids), more for a near one
            float near = 1f;
            if (Settings.DoorOnTheWay.Value && door?.Outside != null && door.Outside.Length >= 3)
            {
                Vector3 at = new Vector3(door.Outside[0], door.Outside[1], door.Outside[2]);
                Vector3 d = bot.Position - at;
                float away = Mathf.Sqrt(d.x * d.x + d.z * d.z + 16f * d.y * d.y);
                near = away <= 60f ? 1.8f : away <= 150f ? 1.3f : away <= 300f ? 1f : 0.75f;
                if (near > 1.2f)
                {
                    Near++;
                }
            }

            // 1.3.3: the door of an EFT quest item pulls harder wherever the bot is (the terminal of Customs: 732 m away it was dropped)
            if (door != null && QuestAudit.QuestDoors.Contains(door.Id))
            {
                near = Mathf.Max(near, 1.6f);
                Near++;
            }

            return 3.5f * near;
        }

        public static int Near { get; private set; }
    }
}
