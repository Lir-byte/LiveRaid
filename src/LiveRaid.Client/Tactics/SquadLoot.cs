using System.Collections.Generic;
using System.Linq;
using EFT;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A squad loots as one (F12 -> 8 "Wait for squadmates who loot").
    /// Raid 2026-10-22 16:49: lualeet's squad - R3 went through a shelf, lualeet walked on with his quest, 35 m later QuestingBots pulled R3 off the shelf
    /// to "regroup", R3 lost him in a fight, looted alone 180 m away and ran after him for a minute (03:44-05:39). QuestingBots lets a follower loot only
    /// while the leader is within 30 m, and the leader does not look back at all.
    ///  - The leader waits while a squadmate within 80 m loots (a thing in hand, a room it goes through, a body it means to search). Far from them (20 m+)
    ///    it walks back to the nearest of them.
    ///  - Patience: 100-190 s by the looters' greed (40 s when every looter is farther than 25 m). Then the leader calls them on: their looting stops, no new loot for 40 s, they follow. No new wait for
    ///    90 s after that - the loot cycle always ends.
    ///  - One loot spot per squad (0.6.1). Raid 2026-10-22 18:02: Tadikas and Szonszczyk pulled each other apart - the leash was 30 m from the leader's
    ///    CURRENT position, so while Tadikas walked 20 m to his next shelf Szonszczyk's shelf fell out of reach and QuestingBots pulled him to "follow",
    ///    while Tadikas dropped his own looting every 10-20 s to "walk back" to him. Now the first squadmate to start looting fixes the squad's spot; every
    ///    squadmate (the leader too) takes only loot within half the spread of it (F12 -> 8 "Squad loot spread, m", 60 m: the two are never more than
    ///    that apart). Before a spot exists, loot further than the spread from any squadmate is not taken - both go for one place, not two.
    ///  - The leader walks back only when it is not looting itself and the looter is past 3/4 of the spread.
    /// </summary>
    public static class SquadLoot
    {
        private static float Spread => Mathf.Clamp(Settings.SquadLootSpread.Value, 20f, 150f);
        private static float Reach => Spread + 20f;
        private static float GoBackPast => Spread * 0.75f;
        private const float FarLooter = 25f;
        private const float FarPatience = 40f;
        private static float SpotRadius => Mathf.Max(20f, Spread * 0.5f);

        private static readonly Dictionary<string, (Vector3 at, float until)> _spots = new Dictionary<string, (Vector3, float)>();

        private class Wait
        {
            public float From;
            public float Until;
            public float CalmUntil;
            public float NextCheck;
            public float LastBusy;
            public bool Result;
            public string Names;
            public float NextPost;
        }

        // A leader with nothing to loot itself waits this long at most (raid 2026-10-31 05:46: kikirio, his bag full, stood 164 s for his looters)
        private const float IdlePatience = 90f;
        private static readonly System.Random _rng = new System.Random();

        public static int Posts { get; private set; }

        private static readonly Dictionary<string, Wait> _waits = new Dictionary<string, Wait>();

        public static int Waits { get; private set; }
        public static int WentBack { get; private set; }
        public static int CalledOn { get; private set; }
        public static int Leashed { get; private set; }
        public static int Spots { get; private set; }

        public static string Summary => $"leaders waited for their looters {Waits} times (went back to them {WentBack}, called them on after their time {CalledOn}, guard posts taken meanwhile {Posts}), " +
                                        $"squad loot spots {Spots}, loot too far from the squad's spot skipped {Leashed}";

        public static void Clear()
        {
            _waits.Clear();
            _spots.Clear();
            Waits = WentBack = CalledOn = Leashed = Spots = Posts = 0;
        }

        /// <summary>The squad leader should not go on now: squadmates near it are looting (QbDecisionPatch makes it wait for the group).</summary>
        public static bool LeaderWaits(BotOwner leader)
        {
            if (!Settings.SquadLootWait.Value || leader == null || leader.IsDead || leader.Boss?.Followers == null || leader.Boss.Followers.Count == 0)
            {
                return false;
            }

            float now = Time.time;
            if (!_waits.TryGetValue(leader.ProfileId, out Wait w))
            {
                _waits[leader.ProfileId] = w = new Wait();
            }

            // Asked several times a frame by QuestingBots' monitors: the answer holds half a second
            if (now < w.NextCheck)
            {
                return w.Result;
            }

            // Not asked for a while (the leader fought, followed its own errand): an old wait does not carry over ("has waited 513 s")
            if (w.From > 0f && now - w.NextCheck > 10f)
            {
                w.From = 0f;
            }

            w.NextCheck = now + 0.5f;
            w.Result = Decide(leader, w, now);
            return w.Result;
        }

        private static bool Decide(BotOwner leader, Wait w, float now)
        {
            if (now < w.CalmUntil || !TacticalTasks.NoThreat(leader))
            {
                w.From = 0f;
                return false;
            }

            List<BotOwner> looters = leader.Boss.Followers
                .Where(f => f != null && !f.IsDead && (f.Position - leader.Position).sqrMagnitude < Reach * Reach && Busy(f))
                .ToList();
            string name = leader.Profile?.Nickname;
            if (looters.Count > 0)
            {
                w.LastBusy = now;
            }
            else if (w.From > 0f && now - w.LastBusy < 3f)
            {
                // Between two things of a looter (one done, the next not yet handed over): still waiting
                return true;
            }

            if (looters.Count == 0)
            {
                if (w.From > 0f)
                {
                    Log.Detail("squad", () => $"{name}: the squad is done looting after {now - w.From:0} s - on we go");
                }

                _spots.Remove(leader.ProfileId);

                w.From = 0f;
                return false;
            }

            if (w.From <= 0f)
            {
                float greed = looters.Average(f => Mathf.Clamp(BotProfiles.Get(f)?.LootGreed ?? 1f, 0.4f, 1.6f));
                w.From = now;
                w.Until = now + (QuestDrive.LeaderWaitCap(leader) ?? 100f + 75f * (greed - 0.4f));
                w.Names = string.Join(", ", looters.Select(f => f.Profile?.Nickname));
                Waits++;
                Log.Info($"LR Squad: {name} waits for {w.Names} to finish looting (up to {w.Until - now:0} s)");
            }

            // Raid 2026-10-30 13:44: Дед Хипстер stood 79 s on one spot while Колян Лютый went through a building 50 m away on another floor.
            // With every looter beyond FarLooter the leader's patience is FarPatience, not the full 100-190 s: a player does not wait that long
            // for a mate he cannot even see
            BotOwner nearest = looters.OrderBy(f => (f.Position - leader.Position).sqrMagnitude).First();
            float dist = Vector3.Distance(nearest.Position, leader.Position);
            bool idle = !Busy(leader);
            if (now > w.Until || (dist > FarLooter && now - w.From > FarPatience) || (idle && now - w.From > IdlePatience))
            {
                CallOn(leader, looters, now - w.From);
                _spots.Remove(leader.ProfileId);
                w.From = 0f;
                w.CalmUntil = now + 90f;
                return false;
            }

            // Nothing to loot itself: it covers them - from a post 6-12 m off the nearest looter, watching outwards, a new one every 15-25 s - instead of standing
            if (idle && dist <= GoBackPast && now - w.From > 6f && now >= w.NextPost && !TacticalTasks.TryGet(leader, out _) && leader.Medecine?.Using != true)
            {
                w.NextPost = now + 15f + (float)_rng.NextDouble() * 10f;
                Vector3? post = Bots.IdleBreaker.PostNear(nearest, leader);
                if (post != null)
                {
                    TacticalTasks.Set(leader, new TacticalTask
                    {
                        Kind = TaskKind.Relocate,
                        StandAt = post,
                        LookAt = post.Value + (post.Value - nearest.Position),
                        Until = now + 14f,
                        Speed = 1f,
                        MovePose = 1f,
                        Sprint = false,
                    });
                    Posts++;
                    Log.Detail("squad", () => $"{name} covers {nearest.Profile?.Nickname} while he loots - takes a post {Vector3.Distance(leader.Position, post.Value):0} m off");
                }
            }

            // Too far from them: back to the nearest looter, as a player walks back to the mate who still goes through a room
            if (dist > GoBackPast && !Busy(leader) && !TacticalTasks.TryGet(leader, out _) && leader.Medecine?.Using != true)
            {
                Vector3 to = nearest.Position + (leader.Position - nearest.Position).normalized * 6f;
                if (NavMesh.SamplePosition(to, out NavMeshHit hit, 3f, NavMesh.AllAreas) || NavMesh.SamplePosition(nearest.Position, out hit, 3f, NavMesh.AllAreas))
                {
                    TacticalTasks.Set(leader, new TacticalTask
                    {
                        Kind = TaskKind.Relocate,
                        StandAt = hit.position,
                        LookAt = nearest.Position,
                        Until = now + dist / 2.5f + 8f,
                        Speed = 0.8f,
                        MovePose = 1f,
                        Sprint = false,
                    });
                    WentBack++;
                    Log.Info($"LR Squad: {name} walks back {dist:0} m to {nearest.Profile?.Nickname}, who is still looting");
                }
            }

            return true;
        }

        /// <summary>The leader stands waiting for its looters right now (a reason to stand, not idling - it ends by itself).</summary>
        public static bool IsWaiting(BotOwner leader) =>
            leader != null && _waits.TryGetValue(leader.ProfileId, out Wait w) && w.From > 0f && w.Result && Time.time < w.Until;

        /// <summary>Time is up: the looters stop and follow (no loot scans for 40 s).</summary>
        private static void CallOn(BotOwner leader, List<BotOwner> looters, float waited)
        {
            CalledOn++;
            foreach (BotOwner f in looters)
            {
                RoomSweep.Release(f);
                LootingBots.External.PreventBotFromLooting(f, 40f);
            }

            SquadBrain.Say(leader, EPhraseTrigger.FollowMe, 0.8f);
            Log.Info($"LR Squad: {leader.Profile?.Nickname} has waited {waited:0} s - calls {string.Join(", ", looters.Select(f => f.Profile?.Nickname))} on");
        }

        /// <summary>
        /// A squadmate busy with loot right now: going through something, on its way to a target with LootingBots' layer on, a room it goes through, a body it
        /// was sent to in the last 25 s. Raid 2026-10-26 15:48: a pending body job (one it could not take up) or a target LootingBots never walked to counted as
        /// "looting" - Пахомий Почтальон waited 90 s twice for Станислав Зырь-труба, who stood idle next to him waiting for his leader.
        /// </summary>
        private static bool Busy(BotOwner f)
        {
            LootingBrain brain = f.GetPlayer?.GetComponent<LootingBrain>();
            if (brain != null && brain.LootTaskRunning)
            {
                return true;
            }

            if (brain != null && brain.HasActiveLootable && DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(f) == "Looting")
            {
                return true;
            }

            return RoomSweep.InVisit(f) || LootJobs.WorkingOn(f);
        }

        /// <summary>The squad's leader (the bot itself when it leads), or null for a bot without a squad.</summary>
        private static BotOwner LeaderOf(BotOwner bot)
        {
            BotOwner leader = TacticalTasks.SquadLeaderOf(bot);
            if (leader != null && leader != bot && !leader.IsDead)
            {
                return leader;
            }

            return bot.Boss?.Followers != null && bot.Boss.Followers.Any(f => f != null && !f.IsDead) ? bot : null;
        }

        /// <summary>A squadmate started looting: the first one fixes the squad's loot spot (it holds while anyone loots, 5 minutes at most).</summary>
        public static void OnStartedLooting(BotOwner bot)
        {
            if (!Settings.SquadLootWait.Value || bot == null)
            {
                return;
            }

            BotOwner leader = LeaderOf(bot);
            if (leader == null)
            {
                return;
            }

            float now = Time.time;
            if (_spots.TryGetValue(leader.ProfileId, out (Vector3 at, float until) spot) && now < spot.until)
            {
                return;
            }

            _spots[leader.ProfileId] = (bot.Position, now + 300f);
            Spots++;
            Log.Detail("squad", () => $"{bot.Profile?.Nickname} starts looting - the squad loots within {SpotRadius:0} m of ({bot.Position.x:0}, {bot.Position.z:0})");
        }

        /// <summary>
        /// A squadmate may take loot at <paramref name="point"/>: within half the spread of the squad's loot spot; with no spot yet, no further than the
        /// spread from any squadmate near (one place for the squad, not two). True for a bot without a squad.
        /// </summary>
        public static bool InReach(BotOwner bot, Vector3 point)
        {
            if (!Settings.SquadLootWait.Value || bot == null)
            {
                return true;
            }

            BotOwner leader = LeaderOf(bot);
            if (leader == null)
            {
                return true;
            }

            bool ok;
            if (_spots.TryGetValue(leader.ProfileId, out (Vector3 at, float until) spot) && Time.time < spot.until)
            {
                ok = Flat(spot.at, point) <= SpotRadius;
            }
            else
            {
                ok = true;
                float near = Spread * 2f;
                foreach (BotOwner m in Members(leader))
                {
                    if (m != bot && Flat(m.Position, bot.Position) < near && Flat(m.Position, point) > Spread)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                Leashed++;
            }

            return ok;
        }

        private static IEnumerable<BotOwner> Members(BotOwner leader)
        {
            yield return leader;
            if (leader.Boss?.Followers == null)
            {
                yield break;
            }

            foreach (BotOwner f in leader.Boss.Followers)
            {
                if (f != null && !f.IsDead)
                {
                    yield return f;
                }
            }
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
