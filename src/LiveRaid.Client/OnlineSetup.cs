#if LIVERAID_ONLINE
using System.Linq;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using QuestingBots.ExternalMods.Functions.Multiplayer;
using QuestingBots.ExternalMods.Functions.NetworkTransactions;

namespace LiveRaid.Client
{
    /// <summary>
    /// LiveRaid Online: what QuestingBots needs from Fika. QuestingBots asks a separate plugin (QuestingBotsFikaSync) for the list of human players
    /// (spawns keep away from them, "players just spawned") and to tell the other machines about a key it hands a bot. Without that plugin it warns
    /// and its keys exist only on the host. When the plugin is not installed, LiveRaid Online answers both the same way.
    /// </summary>
    internal static class OnlineSetup
    {
        private const string QbFikaSync = "com.danw.questingbotsfikasync";

        public static void Init()
        {
            if (Chainloader.PluginInfos.ContainsKey(QbFikaSync))
            {
                Plugin.Log.LogInfo("LiveRaid Online: QuestingBotsFikaSync is installed - it serves QuestingBots in Fika");
                return;
            }

            // QuestingBots 1.2.0 asks for four things (1.1.0 only for the human players)
            FikaMultiplayerFunctions.SetGetAliveHumanPayersFunc(() => Net.Humans().ToArray());
            FikaMultiplayerFunctions.SetGetAlivePayersFunc(() => Singleton<GameWorld>.Instance?.AllAlivePlayersList?.ToArray() ?? new Player[0]);
            FikaMultiplayerFunctions.SetGetMyPayerFunc(() => Singleton<GameWorld>.Instance?.MainPlayer);
            FikaMultiplayerFunctions.SetGetPayerFunc(PlayerByNetId);
            FikaRunNetworkTransactionFunctions.SetMoveItemFunc(SpawnedForQuestingBots);
            Plugin.Log.LogInfo("LiveRaid Online: serves QuestingBots in Fika (players, human players, keys it hands to bots)");
        }

        /// <summary>The living player with that Fika network id (QuestingBots' debug markers on the other machines).</summary>
        private static Player PlayerByNetId(int netId)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world?.AllAlivePlayersList == null)
            {
                return null;
            }

            foreach (Player p in world.AllAlivePlayersList.ToArray())
            {
                if (p is FikaPlayer fp && fp.NetId == netId)
                {
                    return p;
                }
            }

            world.TryGetAlivePlayer(netId, out Player alive);
            return alive;
        }

        /// <summary>QuestingBots already put the key into the bot's inventory on the host; the other machines make the same one.</summary>
        private static bool SpawnedForQuestingBots(Player player, Item item)
        {
            Net.ItemSpawned(player, item);
            return true;
        }
    }
}
#endif
