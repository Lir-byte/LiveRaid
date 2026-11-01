using LiveRaid.Client.Raid;
using QuestingBots.Models.Questing;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// "Hot-drop" role (F12 -> 3 "Hot-drop rushers"): like online players racing for the dorms on Customs or the bunker on Reserve, some
    /// PMCs and squads go for the richest place of the map right at the start. For the first 6 minutes of the raid the exploration quests
    /// of the hottest regions (the top loot-weight ranks, QuestFactory.Heat >= 0.85) pull them six times as hard and everything else much
    /// less; QuestingBots then sends them there, running between objectives. The squad leader's role decides for the squad.
    /// </summary>
    public static class HotDrop
    {
        private const float Seconds = 360f;

        public static bool Active(BotProfile p)
        {
            if (p == null || !p.HotDrop || !Settings.HotDrop.Value || !Settings.HotZones.Value)
            {
                return false;
            }

            return QuestingBots.Helpers.RaidHelpers.GetRaidElapsedSeconds() < Seconds;
        }

        public static float Multiplier(BotProfile p, BotQuest quest)
        {
            if (!Active(p))
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (KeyPlaces.IsKeyPlaceQuest(name))
            {
                // The key places are what a hot-dropper runs for
                return 6f;
            }

            if (!name.StartsWith(QuestFactory.ExplorePrefix))
            {
                // EFT quests, locked rooms, camping and the like: later
                return 0.25f;
            }

            QuestFactory.Heat.TryGetValue(name, out float heat);
            return heat >= 0.85f ? 6f : heat >= 0.6f ? 1f : 0.3f;
        }
    }
}
