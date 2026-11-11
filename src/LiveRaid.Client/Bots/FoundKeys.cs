using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Raid;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A key found in the raid is used in the raid (F12 -> 2 "Use keys found in the raid"). Keys were dealt at activation and only those sent a bot
    /// to a door or a locked container; a key a bot took off a body or out of a drawer just sat in its rig. A player who finds a key to something on
    /// this map goes to see what is behind it. Every 5 s a PMC or player scav is looked at: a key it did not have before that opens a still-locked
    /// door or container of this map makes that place its wish for the next 10 minutes (the QuestingBots quest of the locked room x3, its locked
    /// containers open to ContainerUnlockLayer).
    /// </summary>
    public static class FoundKeys
    {
        private static readonly Dictionary<string, HashSet<string>> _known = new Dictionary<string, HashSet<string>>();
        private static readonly Dictionary<string, Dictionary<string, float>> _fresh = new Dictionary<string, Dictionary<string, float>>();
        private static float _next;
        private static int _cursor;

        public static int Found { get; private set; }

        public static string Summary => $"keys found in the raid and gone to use {Found}";

        public static bool On => Settings.Enabled.Value && Settings.FoundKeys.Value;

        public static void Clear()
        {
            _known.Clear();
            _fresh.Clear();
            _next = 0f;
            _cursor = 0;
            Found = 0;
        }

        /// <summary>QuestingBots' wish for the quest of a locked room: x3 while its key was found in the last 10 minutes.</summary>
        public static float Multiplier(BotOwner bot, string keyId) =>
            On && bot != null && keyId != null && _fresh.TryGetValue(bot.ProfileId, out var keys) && keys.TryGetValue(keyId, out float at) && Time.time - at < 600f
                ? 3f
                : 1f;

        public static void Tick()
        {
            if (!On || Time.time < _next || MapIntel.Current == null)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world.AllAlivePlayersList.Count == 0)
            {
                return;
            }

            // About one bot per tick: every bot about every 5-10 s
            _cursor = (_cursor + 1) % world.AllAlivePlayersList.Count;
            BotOwner bot = world.AllAlivePlayersList[_cursor]?.AIData?.BotOwner;
            BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || !p.KeysGiven)
            {
                return;
            }

            HashSet<string> owned = new HashSet<string>(BotSetup.OwnedKeyIds(bot));
            if (!_known.TryGetValue(bot.ProfileId, out HashSet<string> known))
            {
                // First look: what it came in with (or was dealt) is not "found"
                _known[bot.ProfileId] = owned;
                return;
            }

            foreach (string key in owned.Where(k => !known.Contains(k)).ToList())
            {
                known.Add(key);
                if (!MapIntel.Current.SpotsByKey.TryGetValue(key, out List<LockedSpot> spots) || !spots.Any(s => s.StillLocked))
                {
                    continue;
                }

                if (!_fresh.TryGetValue(bot.ProfileId, out var keys))
                {
                    _fresh[bot.ProfileId] = keys = new Dictionary<string, float>();
                }

                keys[key] = Time.time;
                if (!p.Keys.Contains(key))
                {
                    p.Keys.Add(key);
                }

                Found++;
                SquadKeys.Found(bot, key, spots.First(x => x.StillLocked).Id);
                LockedSpot near = spots.Where(s => s.StillLocked).OrderBy(s => (s.Object != null ? (s.Object.transform.position - bot.Position).sqrMagnitude : float.MaxValue)).First();
                Log.Info($"LR Key: {p.Name} found the key to {near.Id}{(near.IsContainer ? " (a container)" : "")} in the raid - wants to see what is behind it");
            }
        }
    }
}
