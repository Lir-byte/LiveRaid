using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Squad talk that changes what the squad does (F12 -> 8 "Squad comms"), by the squad's doctrine:
    ///  - contact calls: the first member to see an enemy calls it ("contact, north-east, 80 m"); squadmates QuestingBots put in a group of their
    ///    own (who do not share the group's enemies) learn of him after a delay - half a second in a talkative clan, three in a quiet one; a squad
    ///    that hardly talks sometimes does not pass it on at all;
    ///  - a fallen squadmate: a vengeful squad goes for the killer ("revenge" call of the playbook), a shaken one falls back - and a squad that lost
    ///    its leader without the stomach for revenge soon heads for an exit; the rest carry on (the old "trade the kill" still goes on);
    ///  - after a fight: once it has been quiet 8-60 s, the leader stops a few seconds for the squad to gather ("regroup") - patching up and handing
    ///    over meds happen meanwhile (SquadShare, HealLayer).
    /// </summary>
    public static class SquadComms
    {
        private class Pending
        {
            public float At;
            public BotOwner Mate;
            public Player Enemy;
        }

        private static readonly Dictionary<string, float> _called = new Dictionary<string, float>();
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly Dictionary<int, float> _lastFight = new Dictionary<int, float>();
        private static readonly HashSet<int> _regrouped = new HashSet<int>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Contacts { get; private set; }
        public static int Passed { get; private set; }
        public static int Silent { get; private set; }
        public static int Revenges { get; private set; }
        public static int Shaken { get; private set; }
        public static int Regroups { get; private set; }
        public static int Fled { get; private set; }

        public static string Summary => $"squad comms: contacts called {Contacts} (passed to split squadmates {Passed}, not passed on {Silent}), " +
                                        $"fallen squadmates avenged {Revenges}, squads shaken {Shaken}, last ones of a squad who fled {Fled}, regroups after a fight {Regroups}, " +
                                        $"rear men holding the street at a door {Patches.Formation.StreetHolds}";

        public static void Clear()
        {
            _called.Clear();
            _pending.Clear();
            _lastFight.Clear();
            _regrouped.Clear();
            _next = 0f;
            Contacts = Passed = Silent = Revenges = Shaken = Regroups = Fled = 0;
        }

        private static bool On => Settings.Enabled.Value && Settings.SquadComms.Value;

        public static void Tick()
        {
            if (!On)
            {
                return;
            }

            float now = Time.time;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (now < p.At)
                {
                    continue;
                }

                _pending.RemoveAt(i);
                if (p.Mate != null && !p.Mate.IsDead && p.Mate.BotsGroup != null && p.Enemy != null && p.Enemy.HealthController?.IsAlive == true)
                {
                    p.Mate.BotsGroup.AddEnemy(p.Enemy, EBotEnemyCause.followGetHit);
                    Passed++;
                }
            }

            if (now < _next)
            {
                return;
            }

            _next = now + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            Dictionary<int, List<BotOwner>> squads = new Dictionary<int, List<BotOwner>>();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || BotProfiles.Get(bot)?.Kind != BotKind.Pmc || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                int squad = SquadTies.SquadOf(bot.ProfileId);
                if (!squads.TryGetValue(squad, out List<BotOwner> list))
                {
                    squads[squad] = list = new List<BotOwner>();
                }

                list.Add(bot);
            }

            foreach (KeyValuePair<int, List<BotOwner>> kv in squads)
            {
                if (kv.Value.Count < 2)
                {
                    continue;
                }

                DoctrineDeck.Squad doctrine = SquadDoctrines.SquadOf(kv.Value[0]);
                foreach (BotOwner m in kv.Value)
                {
                    CallContact(m, kv.Value, doctrine);
                }

                AfterFight(kv.Key, kv.Value, doctrine);
            }
        }

        private static void CallContact(BotOwner spotter, List<BotOwner> squad, DoctrineDeck.Squad doctrine)
        {
            Enemy e = SainBot.Of(spotter)?.GoalEnemy;
            Player enemy = e?.EnemyPlayer;
            if (enemy == null || !e.IsVisible)
            {
                return;
            }

            float now = Time.time;
            string key = SquadTies.SquadOf(spotter.ProfileId) + "|" + enemy.ProfileId;
            if (_called.TryGetValue(key, out float at) && now - at < 30f)
            {
                return;
            }

            _called[key] = now;
            Contacts++;
            float comms = doctrine?.Values[DoctrineDeck.Comms] ?? 0.5f;
            Vector3 d = enemy.Position - spotter.Position;
            string where = $"{Compass(d)}, {d.magnitude:0} m";
            SquadBrain.Say(spotter, EPhraseTrigger.OnFight, 0.3f + 0.5f * comms);

            // A quiet squad does not always say it - and the ones QuestingBots put apart then only learn of him by themselves
            if (_rng.NextDouble() > 0.15f + 0.85f * comms)
            {
                Silent++;
                Log.Detail("squad", () => $"{spotter.Profile.Nickname} sees {enemy.Profile?.Nickname} ({where}) and says nothing");
                return;
            }

            int told = 0;
            float delay = 0.5f + (1f - comms) * 2.5f;
            foreach (BotOwner mate in squad)
            {
                if (mate == spotter || mate.BotsGroup == spotter.BotsGroup || mate.BotsGroup == null)
                {
                    continue;
                }

                _pending.Add(new Pending { At = now + delay + (float)_rng.NextDouble() * 0.5f, Mate = mate, Enemy = enemy });
                told++;
            }

            Log.Detail("squad", () => $"{spotter.Profile.Nickname} calls contact: {enemy.Profile?.Nickname}, {where}" +
                                      (told > 0 ? $" - {told} split squadmate(s) hear it in {delay:0.0} s" : ""));
        }

        private static string Compass(Vector3 d)
        {
            float a = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (a < 0f)
            {
                a += 360f;
            }

            string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return names[Mathf.RoundToInt(a / 45f) % 8];
        }

        /// <summary>Once it has been quiet 8-60 s after a fight: the leader stops a few seconds for the squad to gather.</summary>
        private static void AfterFight(int squad, List<BotOwner> members, DoctrineDeck.Squad doctrine)
        {
            float now = Time.time;
            if (members.Any(m => HitReaction.InRealFight(m, 15f)))
            {
                _lastFight[squad] = now;
                _regrouped.Remove(squad);
                return;
            }

            if (!_lastFight.TryGetValue(squad, out float last) || _regrouped.Contains(squad) || now - last < 8f || now - last > 60f)
            {
                return;
            }

            BotOwner leader = members.FirstOrDefault(m => TacticalTasks.SquadLeaderOf(m) == null) ?? members[0];
            if (!members.All(TacticalTasks.BotIsCalm) || TacticalTasks.TryGet(leader, out _) || ExtractPlanner.IsLeaving(leader))
            {
                // Not yet: the squad is still settling (it is calm only some 15-25 s after a fight), asked again next tick
                return;
            }

            _regrouped.Add(squad);
            float cohesion = doctrine?.Values[DoctrineDeck.Cohesion] ?? 0.5f;
            if (_rng.NextDouble() > 0.3f + 0.6f * cohesion)
            {
                return;
            }

            float seconds = 6f + (float)_rng.NextDouble() * 6f;
            if (TacticalTasks.Set(leader, new TacticalTask { Kind = TaskKind.Listen, LookAt = leader.Position + leader.LookDirection * 10f, Until = now + seconds }))
            {
                Regroups++;
                SquadBrain.Say(leader, _rng.NextDouble() < 0.5 ? EPhraseTrigger.Regroup : EPhraseTrigger.GoodWork, 0.6f);
                Log.Info($"LR Squad: {leader.Profile.Nickname} stops {seconds:0} s after the fight for the squad to gather");
            }
        }

        /// <summary>A PMC fell: his squad avenges him, is shaken, or carries on - by its doctrine (the leader's death weighs more).</summary>
        public static void OnKilled(Player victim, IPlayer killer)
        {
            if (!On || victim == null || !Settings.SquadPlaybook.Value)
            {
                return;
            }

            DoctrineDeck.Member fallen = SquadDoctrines.Of(victim.AIData?.BotOwner);
            if (fallen == null)
            {
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            List<BotOwner> left = world.AllAlivePlayersList.Select(x => x?.AIData?.BotOwner)
                .Where(b => b != null && !b.IsDead && b.ProfileId != victim.ProfileId && SquadTies.AreMates(b.ProfileId, victim.ProfileId)).ToList();
            if (left.Count == 0)
            {
                return;
            }

            // The last one of a squad of two or more: most leave the fight and the raid (Settings: "Flee when the squad is wiped")
            if (Settings.SelfPreservation.Value && left.Count == 1 && SquadTies.MatesOf(victim.ProfileId).Count() >= 2 && _rng.NextDouble() < 0.85)
            {
                BotOwner last = left[0];
                SAIN.SAINComponent.Classes.EnemyClasses.Enemy e = SainBot.Of(last)?.GoalEnemy;
                if (e != null)
                {
                    CombatSense.BreakOff(last, e, "the last one of its squad");
                }

                ExtractPlanner.LeaveSoon(last, $"its whole squad is dead ({victim.Profile?.Nickname} was the last)");
                Fled++;
                Log.Info($"LR Squad: {last.Profile?.Nickname} is the last of its squad after {victim.Profile?.Nickname} fell - breaks off and heads out");
                return;
            }

            DoctrineDeck.Squad squad = fallen.Squad;
            bool leader = squad.Leader == victim.ProfileId;
            float revenge = squad.Values[DoctrineDeck.Revenge] * (leader ? 1.15f : 1f);
            BotOwner first = left.OrderBy(b => (b.Position - victim.Position).sqrMagnitude).First();
            string name = victim.Profile?.Nickname;
            double roll = _rng.NextDouble();
            if (revenge > 0.55f && roll < revenge && (killer == null || (killer.Position - first.Position).sqrMagnitude < 150f * 150f))
            {
                if (SquadPlaybook.Force(first, SquadTactic.Revenge, $"{name} fell{(leader ? " - their leader" : "")}, they go for {killer?.Profile?.Nickname ?? "his killer"}"))
                {
                    Revenges++;
                    SquadBrain.Say(first, EPhraseTrigger.OnFriendlyDown, 0.8f);
                }

                return;
            }

            if (revenge < 0.4f && roll < 1f - revenge)
            {
                Shaken++;
                SquadPlaybook.Force(first, left.Count >= 2 ? SquadTactic.FallBackAndRegroup : SquadTactic.Scatter, $"{name} fell{(leader ? " - their leader" : "")}, the squad is shaken");
                if (leader)
                {
                    foreach (BotOwner b in left)
                    {
                        ExtractPlanner.LeaveSoon(b, $"lost its leader {name}");
                    }
                }
            }
        }
    }
}
