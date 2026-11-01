using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
#if LIVERAID_ONLINE
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.Packets.Player;
#endif

namespace LiveRaid.Client
{
    /// <summary>
    /// The edition and this machine's part in the raid. LiveRaid (single player) is the only machine there is. LiveRaid Online runs on Project Fika:
    /// the bots live only on the host (a player who hosts, or a headless client) - their brains, SAIN, QuestingBots, LootingBots and LiveRaid run
    /// there; the other players' machines only see them (Fika sends where they are and what they do). So on a Fika client LiveRaid stays out of the raid.
    /// </summary>
    public static class Net
    {
#if LIVERAID_ONLINE
        public const bool Online = true;
        public const string Edition = "Online";
#else
        public const bool Online = false;
        public const string Edition = "";
#endif

        /// <summary>This machine runs the bots: always in single player, the host (or headless) in Fika.</summary>
        public static bool RunsBots
        {
            get
            {
#if LIVERAID_ONLINE
                return !FikaBackendUtils.IsClient;
#else
                return true;
#endif
            }
        }

        /// <summary>A Fika client: the bots are run by somebody else.</summary>
        public static bool IsClient
        {
            get
            {
#if LIVERAID_ONLINE
                return FikaBackendUtils.IsClient;
#else
                return false;
#endif
            }
        }

        /// <summary>A Fika headless host: nobody sits at this machine (its own "player" is a stand-in and not a human).</summary>
        public static bool IsHeadless
        {
            get
            {
#if LIVERAID_ONLINE
                return FikaBackendUtils.IsHeadless;
#else
                return false;
#endif
            }
        }

        public static string Role => !Online ? "single player" : IsHeadless ? "Fika headless host" : IsClient ? "Fika client" : RunsBots ? "Fika host" : "Fika";

        private static readonly List<Player> _humans = new List<Player>();

        /// <summary>The living human players of the raid (yours in single player; every player of the group in Fika, never a headless stand-in).</summary>
        public static List<Player> Humans()
        {
            _humans.Clear();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return _humans;
            }

#if LIVERAID_ONLINE
            if (CoopHandler.TryGetCoopHandler(out CoopHandler coop) && coop.HumanPlayers != null)
            {
                foreach (FikaPlayer p in coop.HumanPlayers)
                {
                    if (IsLivingHuman(p))
                    {
                        _humans.Add(p);
                    }
                }

                return _humans;
            }
#endif
            if (IsLivingHuman(world.MainPlayer))
            {
                _humans.Add(world.MainPlayer);
            }

            return _humans;
        }

        /// <summary>A human player (not a bot, not a headless host's stand-in), alive.</summary>
        public static bool IsLivingHuman(Player p) => IsHuman(p) && p.HealthController?.IsAlive == true;

        public static bool IsHuman(Player p)
        {
            if (p == null || p.IsAI)
            {
                return false;
            }

            // The headless host's own player stands somewhere out of the way: nobody plays it
            return !(IsHeadless && p.IsYourPlayer);
        }

        /// <summary>The players go in as scavs (not PMCs of the lobby).</summary>
        public static bool ScavRaid
        {
            get
            {
#if LIVERAID_ONLINE
                return FikaBackendUtils.IsScav;
#else
                return QuestingBots.Helpers.RaidHelpers.IsScavRun;
#endif
            }
        }

        /// <summary>
        /// How many human players are in this raid: the ones already in it, or (Fika) the number Fika expects - early in the raid, while the others still
        /// load, the bots of the lobby are already being planned.
        /// </summary>
        public static int GroupSize()
        {
            int n = Humans().Count;
#if LIVERAID_ONLINE
            if (Singleton<IFikaNetworkManager>.Instantiated)
            {
                n = Mathf.Max(n, Singleton<IFikaNetworkManager>.Instance.PlayerAmount - (IsHeadless ? 1 : 0));
            }
#endif
            return Mathf.Max(1, n);
        }

        /// <summary>The human the raid is measured against: you if you are alive, else the first living human of the group.</summary>
        public static Player MainHuman()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (IsLivingHuman(me))
            {
                return me;
            }

            List<Player> all = Humans();
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>Distance from a point to the nearest living human (float.MaxValue when there is none).</summary>
        public static float NearestHuman(Vector3 point)
        {
            float best = float.MaxValue;
            foreach (Player p in Humans())
            {
                best = Mathf.Min(best, Vector3.Distance(p.Position, point));
            }

            return best;
        }

        /// <summary>
        /// An item LiveRaid created and put into a bot's inventory (a key). In Fika the other machines do not know the item: they are told to make the same
        /// one at the same place (the way QuestingBots and LootingBots do it) - otherwise the key would be missing from the body they loot.
        /// </summary>
        public static void ItemSpawned(Player owner, Item item)
        {
#if LIVERAID_ONLINE
            if (!FikaBackendUtils.IsServer || !(owner is FikaBot bot) || item == null || !Singleton<IFikaNetworkManager>.Instantiated)
            {
                return;
            }

            SpawnItemInInventoryPacket packet = new SpawnItemInInventoryPacket
            {
                NetId = bot.NetId,
                ItemId = item.Id,
                TemplateId = item.TemplateId,
                Amount = item.StackObjectsCount,
                ItemAddress = item.Parent,
            };
            Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
#endif
        }

        /// <summary>True when a move of a brand-new item must not go through the inventory's network transaction (Fika would send an operation on an item the others do not have).</summary>
        public static bool SpawnWithoutTransaction
        {
            get
            {
#if LIVERAID_ONLINE
                return FikaBackendUtils.IsServer;
#else
                return false;
#endif
            }
        }
    }
}
