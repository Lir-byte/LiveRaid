using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    public enum SquadRole
    {
        None,
        Leader,
        Pointman,
        Overwatch,
        Rifleman,
        Rear,
    }

    /// <summary>
    /// PMC squads act like a team: members get roles (re-dealt when the leader falls), loot a place together and call things out by voice.
    /// </summary>
    public static class SquadBrain
    {
        private class SquadState
        {
            public string LooterId;

            // Squadmates asked to loot alongside the looter this episode, with the time they were asked
            public readonly Dictionary<string, float> Joined = new Dictionary<string, float>();

            // 0 = a bunch of randoms, 1 = drilled team. From the members' experience plus luck.
            public float Discipline = -1f;
            public float Talkativeness;
        }

        private static readonly Dictionary<BotsGroup, SquadState> _squads = new Dictionary<BotsGroup, SquadState>();
        private static readonly Dictionary<string, SquadRole> _roles = new Dictionary<string, SquadRole>();
        private static readonly Dictionary<string, SquadState> _squadOf = new Dictionary<string, SquadState>();
        private static readonly System.Random _rng = new System.Random();
        private static float _nextTick;

        public static void Clear()
        {
            _squads.Clear();
            _roles.Clear();
            _squadOf.Clear();
            _nextTick = 0;
        }

        public static SquadRole RoleOf(BotOwner bot) => _roles.TryGetValue(bot.ProfileId, out SquadRole r) ? r : SquadRole.None;

        /// <summary>Discipline of the bot's squad (0..1), or -1 when unknown/solo.</summary>
        public static float DisciplineOf(BotOwner bot) => _squadOf.TryGetValue(bot.ProfileId, out SquadState s) ? s.Discipline : -1f;

        public static void Tick()
        {
            if (!Settings.SquadTactics.Value || Time.time < _nextTick)
            {
                return;
            }

            _nextTick = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            Dictionary<BotsGroup, List<BotOwner>> groups = new Dictionary<BotsGroup, List<BotOwner>>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotsGroup == null || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
                {
                    continue;
                }

                if (!groups.TryGetValue(bot.BotsGroup, out List<BotOwner> list))
                {
                    groups[bot.BotsGroup] = list = new List<BotOwner>();
                }

                list.Add(bot);
            }

            foreach (KeyValuePair<BotsGroup, List<BotOwner>> g in groups)
            {
                if (g.Value.Count < 2)
                {
                    continue;
                }

                if (!_squads.TryGetValue(g.Key, out SquadState state))
                {
                    _squads[g.Key] = state = new SquadState();
                }

                if (state.Discipline < 0)
                {
                    DoctrineDeck.Squad doctrine = g.Value.Select(SquadDoctrines.SquadOf).FirstOrDefault(d => d != null);
                    if (doctrine != null)
                    {
                        // The squad's doctrine: how drilled it is and how much it talks
                        state.Discipline = Mathf.Clamp01(0.65f * doctrine.Values[DoctrineDeck.Precision] + 0.35f * doctrine.Values[DoctrineDeck.Cohesion]);
                        state.Talkativeness = doctrine.Values[DoctrineDeck.Comms];
                    }
                    else
                    {
                        float skill = (float)g.Value.Average(m => (int)(BotProfiles.Get(m)?.Skill ?? SkillTier.Average)) / 3f;
                        state.Discipline = Mathf.Clamp01(skill * 0.7f + (float)_rng.NextDouble() * 0.5f - 0.1f);
                        state.Talkativeness = (float)_rng.NextDouble();
                    }

                    Log.Info($"LR Squad: discipline {state.Discipline:0.00}, talkativeness {state.Talkativeness:0.00} for " +
                             string.Join(", ", g.Value.Select(m => m.Profile.Nickname)));
                }

                foreach (BotOwner m in g.Value)
                {
                    _squadOf[m.ProfileId] = state;
                }

                AssignRoles(g.Value);
                ManageLooting(g.Value, state);
            }
        }

        /// <summary>Called by the LootingBots patch the moment a bot starts looting.</summary>
        public static void OnStartedLooting(BotOwner bot)
        {
            if (Settings.SquadTactics.Value && bot.BotsGroup != null && bot.BotsGroup.MembersCount > 1)
            {
                Say(bot, EPhraseTrigger.CoverMe, 0.35f);
            }
        }

        private static void AssignRoles(List<BotOwner> members)
        {
            BotOwner boss = members.FirstOrDefault(m => m.Boss != null && m.Boss.HaveFollowers());
            bool newLeader = boss != null && _roles.TryGetValue(boss.ProfileId, out SquadRole was) && was != SquadRole.Leader;
            if (members.All(m => _roles.ContainsKey(m.ProfileId)) && !newLeader)
            {
                return;
            }

            if (newLeader)
            {
                // The leader fell (or the group was re-led): the squad closes up behind the new one with fresh roles
                Log.Info($"LR Squad: {boss.Profile.Nickname} takes the lead of {string.Join(", ", members.Where(m => m != boss).Select(m => m.Profile.Nickname))}");
                Say(boss, EPhraseTrigger.FollowMe, 0.8f);
            }

            BotOwner leader = boss ?? members[0];
            _roles[leader.ProfileId] = SquadRole.Leader;

            List<BotOwner> rest = members.Where(m => m != leader).ToList();
            bool pointTaken = false;
            bool overwatchTaken = false;
            for (int i = 0; i < rest.Count; i++)
            {
                BotProfile p = BotProfiles.Get(rest[i]);
                SquadRole role;
                if (!overwatchTaken && (p.WeaponClass == "sniperRifle" || p.WeaponClass == "marksmanRifle"))
                {
                    role = SquadRole.Overwatch;
                    overwatchTaken = true;
                }
                else if (!pointTaken && (p.Style == PlayStyle.Rusher || p.WeaponClass == "smg" || p.WeaponClass == "shotgun"))
                {
                    role = SquadRole.Pointman;
                    pointTaken = true;
                }
                else if (i == rest.Count - 1 && rest.Count >= 2)
                {
                    role = SquadRole.Rear;
                }
                else
                {
                    role = SquadRole.Rifleman;
                }

                _roles[rest[i].ProfileId] = role;
            }

            Log.Info("LR Squad: " + string.Join(", ", members.Select(m => $"{m.Profile.Nickname}={RoleOf(m)}")));
        }

        private static void ManageLooting(List<BotOwner> members, SquadState state)
        {
            BotOwner looter = members.FirstOrDefault(IsLooting);
            if (looter == null)
            {
                state.LooterId = null;
                return;
            }

            if (state.LooterId != looter.ProfileId)
            {
                // A new looting episode. Nobody stands guard any more (the user's call, session 14): online a squad strips a place together,
                // and danger is handled by LootDanger - a looter stops the moment there are shots or a stranger close by
                state.Joined.Clear();
                Log.Detail("squad", $"{looter.Profile.Nickname} loots; squad (discipline {state.Discipline:0.00}) loots together");
            }

            state.LooterId = looter.ProfileId;
            LootTogether(looter, members, state);
        }

        /// <summary>
        /// Squadmates near the looter who have room in the bag and nothing more urgent to do start looting too, so the squad strips the place
        /// together like players racing to clean a map. A mate who was busy is asked again a few seconds later.
        /// </summary>
        private static void LootTogether(BotOwner looter, List<BotOwner> members, SquadState state)
        {
            int joined = 0;
            float now = Time.time;
            foreach (BotOwner m in members)
            {
                if (m == looter || (state.Joined.TryGetValue(m.ProfileId, out float asked) && now - asked < 6f) || IsLooting(m)
                    || (m.Position - looter.Position).sqrMagnitude > 40f * 40f || !TacticalTasks.NoThreat(m) || LootDanger.Why(m) != null
                    || TacticalTasks.TryGet(m, out _) || LootingBots.External.CheckIfInventoryFull(m))
                {
                    continue;
                }

                state.Joined[m.ProfileId] = now;
                if (RoomSweep.ForceScan(m))
                {
                    joined++;
                    Say(m, EPhraseTrigger.OnLoot, 0.25f);
                }
            }

            if (joined > 0)
            {
                Log.Detail("squad", $"{joined} squadmate(s) of {looter.Profile.Nickname} loot alongside");
            }
        }

        private static bool IsLooting(BotOwner bot)
        {
            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            return brain != null && brain.LootTaskRunning;
        }

        public static void Say(BotOwner bot, EPhraseTrigger phrase, float chance)
        {
            if (_squadOf.TryGetValue(bot.ProfileId, out SquadState squad))
            {
                chance *= 0.3f + squad.Talkativeness;
            }

            if (!Settings.SquadVoice.Value || _rng.NextDouble() > chance)
            {
                return;
            }

            try
            {
                SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(bot);
                if (sain?.Talk != null)
                {
                    sain.Talk.Say(phrase, null, true);
                }
                else
                {
                    bot.BotTalk?.TrySay(phrase);
                }
            }
            catch (Exception e)
            {
                Log.Debug("voice line failed: " + e.Message);
            }
        }
    }
}
