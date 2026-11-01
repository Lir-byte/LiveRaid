using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// What SAIN's squads do not do. SAIN's squad decisions are suppress (only for a member who falls back), help (walk at a friend's
    /// enemy seen under 30 m), group search and push a suppressed enemy; "Surround", "SpreadOut" and "BoundingRetreat" exist in its enum
    /// but are never chosen. So squads fought from where they stood, bunched up, and came at the enemy along one line. Here, on top of
    /// SAIN (it still does all the shooting, cover and peeking):
    ///  - Flank: in a fight that has settled (the enemy known but out of the flanker's sight), one member - the pointman, a rusher, a
    ///    hunter - goes around: a point 60-110 degrees off the squad's line to the enemy, reached by a path that keeps away from the enemy and
    ///    out of his sight where it can; the rest keep the enemy busy from where they are. Drilled squads of four or more sometimes send
    ///    two, one each side (a pincer). A lone bold bot facing an enemy that went quiet may flank on its own. Contact hands the bot back to
    ///    SAIN - now from the side.
    ///  - Spread out: two members fighting the same enemy from the same spot (within 2.5 m, both stuck there) - one shifts to another cover.
    ///  - A squadmate down: the others learn roughly where the killer is; bold ones go to trade the kill, the rest hold and watch.
    /// Each one has its own switch in F12 -> 8 and depends on the squad's discipline, the characters and luck, so not every squad does it.
    /// </summary>
    public static class Maneuvers
    {
        public class Flank
        {
            public BotOwner Bot;
            public string EnemyId;
            public string EnemyName;
            public Vector3 EnemyAt;
            public Vector3 Target;
            public int Side;
            public float StartedAt;
            public float Until;
            public float ArrivedAt = -1f;
            public float HoldFor;
            public bool Solo;

            // progress watch: a flanker that does not move gives up
            public Vector3 LastPos;
            public float LastMoveAt;
        }

        private static readonly Dictionary<string, Flank> _flanks = new Dictionary<string, Flank>();
        private static readonly Dictionary<BotsGroup, float> _squadNextTry = new Dictionary<BotsGroup, float>();
        private static readonly Dictionary<BotsGroup, float> _engagedSince = new Dictionary<BotsGroup, float>();
        // How much each squad likes sending one man round (0 = never): a squad's habit, drawn once, so not every squad plays it
        private static readonly Dictionary<BotsGroup, float> _liking = new Dictionary<BotsGroup, float>();
        private static readonly Dictionary<string, float> _spreadCooldown = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _soloCooldown = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Flanks { get; private set; }
        public static int FlankContacts { get; private set; }
        public static int Spreads { get; private set; }
        public static int Trades { get; private set; }

        public static void Clear()
        {
            _flanks.Clear();
            _styles.Clear();
            _squadNextTry.Clear();
            _engagedSince.Clear();
            _liking.Clear();
            _spreadCooldown.Clear();
            _soloCooldown.Clear();
            _next = 0f;
            Flanks = FlankContacts = Spreads = Trades = 0;
        }

        public static bool TryGetFlank(BotOwner bot, out Flank flank)
        {
            flank = null;
            return bot != null && _flanks.TryGetValue(bot.ProfileId, out flank);
        }

        public static bool IsFlanking(BotOwner bot) => bot != null && _flanks.ContainsKey(bot.ProfileId);

        /// <summary>How a flanker goes round (SquadPlaybook): the usual 60-110 degrees, a wide one behind the enemy, or by the high ground.</summary>
        public enum FlankStyle
        {
            Normal,
            Wide,
            High,
        }

        private static readonly Dictionary<string, FlankStyle> _styles = new Dictionary<string, FlankStyle>();

        /// <summary>The squad's call says how this bot goes round next time it flanks (for the call's length).</summary>
        public static void SetStyle(BotOwner bot, FlankStyle style)
        {
            if (bot != null)
            {
                _styles[bot.ProfileId] = style;
            }
        }

        public static void EndFlank(BotOwner bot, string why)
        {
            if (bot == null || !_flanks.TryGetValue(bot.ProfileId, out Flank f))
            {
                return;
            }

            _flanks.Remove(bot.ProfileId);
            if (why == "contact")
            {
                FlankContacts++;
            }

            Log.Detail("combat", $"{bot.Profile?.Nickname} ends the flank on {f.EnemyName} after {Time.time - f.StartedAt:0} s: {why}");
        }

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Flank f in _flanks.Values.ToList())
            {
                if (f.Bot == null || f.Bot.IsDead || f.Bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(f.Bot))
                {
                    _flanks.Remove(f.Bot?.ProfileId ?? "");
                }
            }

            if (!Settings.SquadFlanking.Value && !Settings.SquadSpreadOut.Value)
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

                try
                {
                    if (Settings.SquadFlanking.Value)
                    {
                        TrySquadFlank(g.Key, g.Value);
                    }

                    if (Settings.SquadSpreadOut.Value)
                    {
                        TrySpreadOut(g.Value);
                    }
                }
                catch (Exception e)
                {
                    Diag.Report("Maneuvers", e);
                }
            }
        }

        // ---------------- flanking ----------------

        private static void TrySquadFlank(BotsGroup group, List<BotOwner> members)
        {
            // The enemy most of the squad is fighting
            Dictionary<string, List<(BotOwner bot, Enemy enemy)>> byEnemy = new Dictionary<string, List<(BotOwner, Enemy)>>();
            foreach (BotOwner m in members)
            {
                Enemy e = SainBot.Of(m)?.GoalEnemy;
                if (e?.EnemyPlayer == null || !e.EnemyKnown)
                {
                    continue;
                }

                if (!byEnemy.TryGetValue(e.EnemyProfileId, out var list))
                {
                    byEnemy[e.EnemyProfileId] = list = new List<(BotOwner, Enemy)>();
                }

                list.Add((m, e));
            }

            if (byEnemy.Count == 0)
            {
                _engagedSince.Remove(group);
                return;
            }

            if (!_engagedSince.TryGetValue(group, out float since))
            {
                _engagedSince[group] = since = Time.time;
            }

            if (Time.time - since < 6f || (_squadNextTry.TryGetValue(group, out float nextTry) && Time.time < nextTry)
                || members.Any(IsFlanking))
            {
                return;
            }

            // Not every few seconds: a squad weighs it now and then
            _squadNextTry[group] = Time.time + 4f;
            var fight = byEnemy.OrderByDescending(kv => kv.Value.Count).First().Value;

            // Where the squad thinks the enemy is: the freshest sighting among the members (never the enemy's real position)
            var freshest = fight.Where(x => x.enemy.LastKnownPosition.HasValue).OrderBy(x => x.enemy.TimeSinceSeen).FirstOrDefault();
            if (freshest.enemy == null || freshest.enemy.TimeSinceSeen > 45f)
            {
                return;
            }

            Vector3 enemyAt = freshest.enemy.LastKnownPosition.Value;
            Vector3 center = Vector3.zero;
            foreach (BotOwner m in members)
            {
                center += m.Position;
            }

            center /= members.Count;
            float dist = Vector3.Distance(center, enemyAt);
            if (dist < 20f || dist > 140f)
            {
                return;
            }

            // A squad that is losing does not split up
            int shaken = members.Count(m => CombatSense.StanceOf(m) == CombatStance.Retreat || CombatSense.StanceOf(m) == CombatStance.BreakContact);
            if (shaken * 2 >= members.Count)
            {
                return;
            }

            float discipline = Mathf.Max(0f, SquadBrain.DisciplineOf(members[0]));

            // Sending a man round is one of the things a squad does in a stalled fight (next to holding, pushing together, falling back, trading
            // a fallen mate), not the thing it always does: a third of squads hardly ever, a third now and then, a third readily; at most two
            // flankers on the whole map at a time
            if (!_liking.TryGetValue(group, out float liking))
            {
                double roll = _rng.NextDouble();
                _liking[group] = liking = roll < 0.3 ? 0.1f : roll < 0.7 ? 0.6f : 1.2f;
            }

            if (_flanks.Count >= 2)
            {
                return;
            }

            float chance = (0.04f + 0.14f * discipline) * liking;
            if (_rng.NextDouble() > chance)
            {
                return;
            }

            List<BotOwner> candidates = fight.Select(x => x.bot).Where(m => CanFlank(m, freshest.enemy.EnemyProfileId)).ToList();
            if (candidates.Count == 0 || candidates.Count == members.Count && members.Count == 2 && _rng.NextDouble() < 0.5)
            {
                // A pair where both could go: half the time they stay together
                _squadNextTry[group] = Time.time + 20f;
                return;
            }

            BotOwner first = candidates.OrderByDescending(m => FlankerScore(m) + (float)_rng.NextDouble() * 1.5f).First();
            Flank f1 = StartFlank(first, freshest.enemy, enemyAt, center, 0, false);
            if (f1 == null)
            {
                _squadNextTry[group] = Time.time + 20f;
                return;
            }

            string pincer = "";
            if (members.Count >= 4 && discipline >= 0.6f && _rng.NextDouble() < 0.35)
            {
                BotOwner second = candidates.Where(m => m != first).OrderByDescending(m => FlankerScore(m) + (float)_rng.NextDouble()).FirstOrDefault();
                Flank f2 = second != null ? StartFlank(second, freshest.enemy, enemyAt, center, -f1.Side, false) : null;
                if (f2 != null)
                {
                    pincer = $", {second.Profile.Nickname} takes the other side (pincer)";
                }
            }

            _squadNextTry[group] = Time.time + 100f + (float)_rng.NextDouble() * 80f;

            // Somebody keeps the enemy busy
            BotOwner support = members.FirstOrDefault(m => !IsFlanking(m));
            if (support != null)
            {
                SquadBrain.Say(support, _rng.NextDouble() < 0.5 ? EPhraseTrigger.Covering : EPhraseTrigger.Suppress, 0.5f);
            }

            Log.Info($"LR Squad: {first.Profile.Nickname} flanks {(f1.Side > 0 ? "right" : "left")} around {f1.EnemyName} " +
                     $"({dist:0} m from the squad, discipline {discipline:0.00}){pincer}");
        }

        /// <summary>A lone bot that stood facing a quiet enemy: the bold go around instead of walking straight at it. True when it started.</summary>
        public static bool TrySoloFlank(BotOwner bot, Enemy enemy)
        {
            if (!Settings.SquadFlanking.Value || bot == null || enemy?.EnemyPlayer == null || !enemy.LastKnownPosition.HasValue
                || (_soloCooldown.TryGetValue(bot.ProfileId, out float cd) && Time.time < cd) || !CanFlank(bot, enemy.EnemyProfileId))
            {
                return false;
            }

            _soloCooldown[bot.ProfileId] = Time.time + 90f;
            Vector3 enemyAt = enemy.LastKnownPosition.Value;
            float dist = Vector3.Distance(bot.Position, enemyAt);
            if (dist < 18f || dist > 120f)
            {
                return false;
            }

            Flank f = StartFlank(bot, enemy, enemyAt, bot.Position, 0, true);
            if (f == null)
            {
                return false;
            }

            Log.Info($"LR Combat: {bot.Profile.Nickname} goes around {(f.Side > 0 ? "right" : "left")} to flank {f.EnemyName} ({dist:0} m)");
            return true;
        }

        private static bool CanFlank(BotOwner bot, string enemyId)
        {
            BotProfile p = BotProfiles.Get(bot);
            if (p == null || IsFlanking(bot) || Spectator.TakeOver.IsTaken(bot))
            {
                return false;
            }

            if (p.Style == PlayStyle.Sniper || p.Style == PlayStyle.Camper || SquadBrain.RoleOf(bot) == SquadRole.Overwatch)
            {
                return false;
            }

            // A broken or blacked-out leg: no long way round at a limp (raid 2026-10-31 05:46: Sheepy flanked 77 m on a blacked-out leg)
            if (LegCare.Limps(bot.GetPlayer) || (bot.GetPlayer?.MovementContext?.PhysicalConditionContainsAny(EPhysicalCondition.LeftLegDamaged | EPhysicalCondition.RightLegDamaged) ?? false))
            {
                return false;
            }

            if (p.Style == PlayStyle.Rat && _rng.NextDouble() < 0.8)
            {
                return false;
            }

            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            Enemy e = sain?.GoalEnemy;
            if (e == null || e.EnemyProfileId != enemyId || e.IsVisible || e.TimeSinceSeen < 3f || HitReaction.SinceHit(bot) < 4f)
            {
                return false;
            }

            if (bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.6f || bot.Medecine?.Using == true
                || bot.Medecine?.FirstAid?.Have2Do == true)
            {
                // (a wound that needs a surgical kit only is no reason to stay put: SAIN's surgery wants a quiet minute anyway)
                return false;
            }

            if (sain.Decision?.SelfActionDecisions != null && sain.Decision.SelfActionDecisions.AmmoRatio < 0.4f)
            {
                return false;
            }

            CombatStance stance = CombatSense.StanceOf(bot);
            if (stance != CombatStance.Normal && stance != CombatStance.Push && stance != CombatStance.Probe)
            {
                return false;
            }

            return !TacticalTasks.TryGet(bot, out TacticalTask t) || t.Kind == TaskKind.Assist;
        }

        private static float FlankerScore(BotOwner bot)
        {
            BotProfile p = BotProfiles.Get(bot);
            float score = p.Aggression * 2f + (int)p.Skill * 0.3f;
            switch (SquadBrain.RoleOf(bot))
            {
                case SquadRole.Pointman: score += 3f; break;
                case SquadRole.Rifleman: score += 1f; break;
                case SquadRole.Rear: score += 0.5f; break;
                case SquadRole.Leader: score -= 1f; break;
            }

            switch (p.Style)
            {
                case PlayStyle.Rusher:
                case PlayStyle.Hunter: score += 2f; break;
                case PlayStyle.Explorer: score += 0.5f; break;
                case PlayStyle.Rat: score -= 2f; break;
            }

            return score;
        }

        private static Flank StartFlank(BotOwner bot, Enemy enemy, Vector3 enemyAt, Vector3 from, int side, bool solo)
        {
            Vector3? point = FindFlankPoint(bot, enemyAt, from, side, out int chosenSide, out float pathLength);
            if (point == null)
            {
                Log.Detail("combat", $"{bot.Profile.Nickname} found no way around {enemy.EnemyPlayer?.Profile?.Nickname}");
                return null;
            }

            if (TacticalTasks.TryGet(bot, out _))
            {
                TacticalTasks.Cancel(bot, "flanking");
            }

            Flank f = new Flank
            {
                Bot = bot,
                EnemyId = enemy.EnemyProfileId,
                EnemyName = enemy.EnemyPlayer?.Profile?.Nickname ?? "an enemy",
                EnemyAt = enemyAt,
                Target = point.Value,
                Side = chosenSide,
                StartedAt = Time.time,
                Until = Time.time + Mathf.Clamp(pathLength / 2.2f + 20f, 30f, 110f),
                HoldFor = 5f + (float)_rng.NextDouble() * 7f,
                Solo = solo,
                LastPos = bot.Position,
                LastMoveAt = Time.time,
            };
            _flanks[bot.ProfileId] = f;
            Flanks++;
            SquadBrain.Say(bot, chosenSide > 0 ? EPhraseTrigger.RightFlank : EPhraseTrigger.LeftFlank, solo ? 0.1f : 0.6f);
            Log.Detail("combat", $"{bot.Profile.Nickname} flank point {Vector3.Distance(point.Value, enemyAt):0} m off {f.EnemyName}, path {pathLength:0} m");
            return f;
        }

        /// <summary>
        /// A point to the side of the enemy (60-110 degrees off the line from the squad to him, 15-35 m from him), reachable by a path that
        /// does not run into him and stays out of his sight where it can. Scored by path length, how much of the path he can see,
        /// a view on him from the point and cover around it.
        /// </summary>
        private static Vector3? FindFlankPoint(BotOwner bot, Vector3 enemyAt, Vector3 from, int preferSide, out int side, out float pathLength)
        {
            side = 0;
            pathLength = 0f;
            Vector3 axis = from - enemyAt;
            axis.y = 0f;
            float dist = axis.magnitude;
            if (dist < 12f)
            {
                return null;
            }

            axis /= dist;
            FlankStyle style = _styles.TryGetValue(bot.ProfileId, out FlankStyle st) ? st : FlankStyle.Normal;
            _styles.Remove(bot.ProfileId);

            // Wide: round to his side and back (110-140 degrees, further out); high: the usual angles, but a spot above him is worth a lot
            float radius = style == FlankStyle.Wide ? Mathf.Clamp(dist * 0.8f, 25f, 50f) : Mathf.Clamp(dist * 0.6f, 15f, 35f);
            float maxLength = style == FlankStyle.Wide ? 260f : 200f;
            float detour = style == FlankStyle.Wide ? 3.2f : 2.6f;
            float climb = style == FlankStyle.High ? 8f : 4f;
            int[] sides = preferSide != 0 ? new[] { preferSide, -preferSide } : _rng.NextDouble() < 0.5 ? new[] { 1, -1 } : new[] { -1, 1 };
            float[] angles = style == FlankStyle.Wide ? new[] { 125f, 140f, 110f } : new[] { 85f, 65f, 105f };
            Vector3 enemyEye = enemyAt + Vector3.up * 1.5f;
            Vector3 toEnemy = -axis;
            NavMeshPath path = new NavMeshPath();
            Vector3? best = null;
            float bestScore = float.MinValue;

            foreach (int s in sides)
            {
                // A side asked for (the other half of a pincer) is taken if it works at all
                if (preferSide != 0 && s != preferSide && best != null)
                {
                    break;
                }

                foreach (float angle in angles)
                {
                    Vector3 dir = Quaternion.Euler(0f, s * angle, 0f) * axis;
                    Vector3 probe = enemyAt + dir * radius;
                    if (style == FlankStyle.High)
                    {
                        probe += Vector3.up * 5f;
                    }

                    if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, style == FlankStyle.High ? 6f : 4f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - enemyAt.y) > climb
                        || (style != FlankStyle.High && hit.position.y - enemyAt.y < -4f))
                    {
                        continue;
                    }

                    Vector3 p = hit.position;
                    if (!NavMesh.CalculatePath(bot.Position, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        continue;
                    }

                    float length = 0f;
                    for (int i = 1; i < path.corners.Length; i++)
                    {
                        length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                    }

                    float straight = Vector3.Distance(bot.Position, p);
                    if (length > maxLength || length > straight * detour + 15f)
                    {
                        continue;
                    }

                    // The way must not lead past the enemy's nose, and the less of it he can see the better
                    float closest = float.MaxValue;
                    int seen = 0, samples = 0;
                    for (int i = 0; i < path.corners.Length; i++)
                    {
                        Vector3 c = path.corners[i];
                        Vector3 mid = i > 0 ? (path.corners[i - 1] + c) * 0.5f : c;
                        foreach (Vector3 q in i > 0 ? new[] { mid, c } : new[] { c })
                        {
                            closest = Mathf.Min(closest, Vector3.Distance(q, enemyAt));
                            if (samples < 14)
                            {
                                samples++;
                                if (!Physics.Linecast(enemyEye, q + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask))
                                {
                                    seen++;
                                }
                            }
                        }
                    }

                    if (closest < radius * 0.5f)
                    {
                        continue;
                    }

                    float exposure = samples > 0 ? (float)seen / samples : 1f;
                    bool view = !Physics.Linecast(p + Vector3.up * 1.4f, enemyAt + Vector3.up * 1.2f, LayersMaskController.HighPolyWithTerrainMask);
                    float score = -length * 0.02f - exposure * 3f + (view ? 1f : 0f) + (1f - LootBrain.Openness(p)) * 0.8f
                                  - Mathf.Abs(angle - (style == FlankStyle.Wide ? 125f : 85f)) * 0.01f
                                  + (style == FlankStyle.High ? Mathf.Clamp(p.y - enemyAt.y, 0f, 6f) * 0.5f : 0f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p;
                        pathLength = length;
                        Vector3 cross = Vector3.Cross(toEnemy, p - from);
                        side = cross.y > 0f ? 1 : -1;
                    }
                }
            }

            return best;
        }

        // ---------------- spreading out ----------------

        private static void TrySpreadOut(List<BotOwner> members)
        {
            List<BotOwner> fighting = members.Where(m => !IsFlanking(m) && SainBot.Of(m)?.GoalEnemy != null && HitReaction.InRealFight(m, 15f)).ToList();
            for (int i = 0; i < fighting.Count; i++)
            {
                for (int j = i + 1; j < fighting.Count; j++)
                {
                    BotOwner a = fighting[i], b = fighting[j];
                    Vector3 d = a.Position - b.Position;
                    if (Mathf.Abs(d.y) > 1.5f || d.x * d.x + d.z * d.z > 2.5f * 2.5f)
                    {
                        continue;
                    }

                    if (!StillFor(a, 4f) || !StillFor(b, 4f))
                    {
                        continue;
                    }

                    // The one lower in the squad moves; the leader stays
                    BotOwner mover = SquadBrain.RoleOf(a) == SquadRole.Leader ? b : SquadBrain.RoleOf(b) == SquadRole.Leader ? a : (_rng.NextDouble() < 0.5 ? a : b);
                    BotOwner stayer = mover == a ? b : a;
                    if (_spreadCooldown.TryGetValue(mover.ProfileId, out float cd) && Time.time < cd)
                    {
                        continue;
                    }

                    _spreadCooldown[mover.ProfileId] = Time.time + 40f;
                    _spreadCooldown[stayer.ProfileId] = Time.time + 20f;
                    if (CombatSense.Nudge(mover, CombatStance.Reposition, $"bunched up with {stayer.Profile.Nickname}, spreads out"))
                    {
                        Spreads++;
                        SquadBrain.Say(stayer, EPhraseTrigger.Spreadout, 0.4f);
                    }
                }
            }
        }

        private static bool StillFor(BotOwner bot, float seconds) =>
            CombatSense.TryGetState(bot, out CombatSense.State s) && s.StillSince > 0f && Time.time - s.StillSince > seconds;

        // ---------------- a squadmate down ----------------

        /// <summary>A squad member was killed: the others know roughly where from; bold ones go and trade, the rest hold.</summary>
        public static void OnKilled(Player victim, IPlayer killer)
        {
            if (!Settings.SquadTrade.Value || victim == null || killer == null)
            {
                return;
            }

            BotOwner dead = victim.AIData?.BotOwner;
            if (dead?.BotsGroup == null)
            {
                return;
            }

            HashSet<string> squad = LootBrain.SquadIds(dead);
            if (squad.Count <= 1 || squad.Contains(killer.ProfileId))
            {
                return;
            }

            Vector3 at = victim.Position;
            List<string> going = new List<string>(), holding = new List<string>();
            bool said = false;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                BotOwner mate = p?.AIData?.BotOwner;
                if (mate == null || mate == dead || mate.IsDead || !squad.Contains(p.ProfileId) || Spectator.TakeOver.IsTaken(mate))
                {
                    continue;
                }

                BotProfile prof = BotProfiles.Get(mate);
                float dist = Vector3.Distance(mate.Position, at);
                if (prof == null || prof.Kind == BotKind.Other || dist > 150f)
                {
                    continue;
                }

                try
                {
                    mate.BotsGroup?.AddEnemy(killer, EBotEnemyCause.callBot);
                }
                catch
                {
                    // an enemy this group cannot take
                }

                if (!said)
                {
                    said = true;
                    SquadBrain.Say(mate, EPhraseTrigger.OnFriendlyDown, 0.8f);
                }

                bool bold = prof.Style == PlayStyle.Rusher || prof.Style == PlayStyle.Hunter || prof.Aggression >= 0.6f;
                float hp = mate.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
                if (bold && hp >= 0.6f && dist < 90f && Vector3.Distance(mate.Position, killer.Position) < 120f && _rng.NextDouble() < 0.7
                    && CombatSense.Nudge(mate, CombatStance.Probe, $"{victim.Profile.Nickname} is down, goes to trade", 25f))
                {
                    going.Add(mate.Profile.Nickname);
                    Trades++;
                }
                else
                {
                    holding.Add(mate.Profile.Nickname);
                    if (!HitReaction.InRealFight(mate, 5f))
                    {
                        mate.Steering.LookToPoint(at + Vector3.up * 1.2f);
                    }
                }
            }

            if (going.Count + holding.Count > 0)
            {
                Log.Info($"LR Squad: {victim.Profile.Nickname} is down (killed by {killer.Profile?.Nickname ?? "?"})" +
                         (going.Count > 0 ? $" - {string.Join(", ", going)} go(es) to trade" : "") +
                         (holding.Count > 0 ? $" - {string.Join(", ", holding)} hold(s) and watch" : ""));
            }
        }
    }
}
