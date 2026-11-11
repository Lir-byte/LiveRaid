using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Tactics;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A key found in the raid is the squad's (F12 -> 2 "Found key goes to the squad"). FoundKeys made the finder want the room; but a squad goes where its LEADER
    /// takes it, and the leader picks only quests of doors whose key he carries - a follower who took a key off a body made no difference to the squad. Now a
    /// squadmate who finds a key to a door of this map that the leader does not have hands it to the leader as the squad hands over meds and loot (SquadShare:
    /// the giver walks up, throws it, the leader picks it up), in a quiet moment, within 5 minutes - after that it is the finder's alone.
    /// </summary>
    public static class SquadKeys
    {
        private class Pending
        {
            public string FinderId;
            public string KeyId;
            public string DoorId;
            public float Until;
            public float NextTry;
        }

        private static readonly List<Pending> _pending = new List<Pending>();
        private static float _next;

        public static int Told { get; private set; }
        public static int Handed { get; private set; }
        public static int GaveUp { get; private set; }

        public static string Summary => $"found keys told to the squad {Told}, handed to the leader {Handed}, gave up {GaveUp}";

        public static void Clear()
        {
            _pending.Clear();
            _next = 0f;
            Told = Handed = GaveUp = 0;
        }

        /// <summary>FoundKeys: the bot has picked up a key that opens a still-locked door of this map.</summary>
        public static void Found(BotOwner finder, string keyId, string doorId)
        {
            if (!Settings.Enabled.Value || !Settings.KeyToSquad.Value || finder == null)
            {
                return;
            }

            BotOwner leader = TacticalTasks.SquadLeaderOf(finder);
            if (leader == null || leader == finder || leader.IsDead || BotSetup.HasKeyFor(leader, keyId) || _pending.Any(p => p.FinderId == finder.ProfileId && p.KeyId == keyId))
            {
                return;
            }

            Told++;
            _pending.Add(new Pending { FinderId = finder.ProfileId, KeyId = keyId, DoorId = doorId, Until = Time.time + 300f, NextTry = Time.time + 3f });
            Log.Info($"LR Key: {finder.Profile.Nickname} tells {leader.Profile.Nickname} of the key to {doorId} - it goes to the leader");
        }

        public static void Tick()
        {
            if (_pending.Count == 0 || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                BotOwner finder = world.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == p.FinderId)?.AIData?.BotOwner;
                BotOwner leader = finder != null && !finder.IsDead ? TacticalTasks.SquadLeaderOf(finder) : null;
                if (finder == null || leader == null || leader == finder || leader.IsDead || BotSetup.HasKeyFor(leader, p.KeyId) || !BotSetup.HasKeyFor(finder, p.KeyId))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (Time.time > p.Until)
                {
                    _pending.RemoveAt(i);
                    GaveUp++;
                    Log.Detail("squad", $"{finder.Profile.Nickname} keeps the key to {p.DoorId} - no quiet moment to hand it to {leader.Profile.Nickname}");
                    continue;
                }

                if (Time.time < p.NextTry)
                {
                    continue;
                }

                p.NextTry = Time.time + 8f;
                try
                {
                    if (SquadShare.HandOverKey(finder, leader, p.KeyId))
                    {
                        _pending.RemoveAt(i);
                        Handed++;
                    }
                }
                catch (System.Exception e)
                {
                    Diag.Report("SquadKeys", e);
                }
            }
        }
    }
}
