using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.Preset.Shared.Models.Enums;
using SAIN.Preset.Shared.Models.Preset.Personalities;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    public enum CombatStance
    {
        Normal,
        Push,
        Reposition,
        Retreat,
        BreakContact,

        /// <summary>Stood too long facing an enemy nobody sees: go and look for it (SAIN's search).</summary>
        Probe,
    }

    /// <summary>
    /// Combat morale. SAIN fights well moment to moment, but on its own it only falls back when out of ammo.
    /// Here every fighting bot keeps weighing the fight like a player would: how many enemies vs friends,
    /// how hurt it is, how many squadmates it already lost, how pinned down it is, and its own nerve.
    /// The result nudges SAIN: push, change position, fall back to cover, or break contact and leave.
    /// </summary>
    public static class CombatSense
    {
        public class State
        {
            public CombatStance Stance;
            public float Morale;
            public float NextEval;
            public float Nerve;
            public int InitialSquad = -1;
            public readonly Queue<(float time, float health)> HealthHistory = new Queue<(float, float)>();
            public float BreakUntil;
            public float BreakCooldownUntil;
            public Vector3? BreakPoint;
            public string LastReason = "";
            public float ChangedAt;

            // When the bot last fell back or broke off, and where its enemy was (ExtractPlanner: a lost fight makes it leave the raid sooner; MateRecovery: not back there)
            public float LostFightAt = -1f;
            public Vector3 LostFightPos;

            // After a push ends the bot does not throw itself forward again at once (an enemy stepping in and out of view made it see-saw)
            public float PushCooldownUntil;

            // Morale is smoothed: it drops at once when the fight turns bad, and recovers over a few evaluations
            public bool HasMorale;

            // Standing still (a bot SAIN keeps "in cover" or "retreating" without a place to go stood for minutes - raid 2026-10-19 04:25)
            public Vector3 LastPos;
            public float StillSince;
            public float RetreatCooldownUntil;
            public float StandoffCooldownUntil;
            public float ProbeUntil;
            public bool Leaving;

            // A squad fall-back called by the playbook (SquadPlaybook): running back is the plan, not a lost fight
            public float RegroupUntil;

            // After a manoeuvre the bot could not carry out (MoveWatch gave up): it fights from where it stands until then (CombatPatches)
            public float HoldGroundUntil;

            // Lost squadmates weigh on morale, less and less over two minutes (it used to weigh the same for the rest of the raid)
            public int LastLost;
            public float LastLossAt;
        }

        public static int Flees { get; private set; }
        private const float LossFadeSeconds = 120f;
        private const float RetreatMaxSeconds = 35f;
        private const float RetreatStillSeconds = 8f;

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();
        private static float _nextTick;

        public static void Clear()
        {
            _states.Clear();
            _nextTick = 0;
            Flees = 0;
            OutrangedLeft = 0;
        }

        /// <summary>The bot could not get where SAIN or its stance sent it a moment ago: it holds its ground and shoots.</summary>
        public static bool HoldingGround(BotOwner bot) => bot != null && _states.TryGetValue(bot.ProfileId, out State s) && Time.time < s.HoldGroundUntil;

        public static CombatStance StanceOf(BotOwner bot) =>
            bot != null && _states.TryGetValue(bot.ProfileId, out State s) ? s.Stance : CombatStance.Normal;

        public static bool TryGetState(BotOwner bot, out State state) => _states.TryGetValue(bot.ProfileId, out state);

        /// <summary>The bot fell back or broke off a fight within the last <paramref name="seconds"/> (where it was then).</summary>
        public static bool LostFightRecently(BotOwner bot, float seconds, out Vector3 where)
        {
            where = default;
            if (bot == null || !_states.TryGetValue(bot.ProfileId, out State s) || s.LostFightAt < 0f || Time.time - s.LostFightAt > seconds)
            {
                return false;
            }

            where = s.LostFightPos;
            return true;
        }

        /// <summary>
        /// The bot cannot get where its stance sends it (pressed into a door, a corner, a squadmate - MoveWatch gave up). The run, the
        /// fall-back, the push or the search is dropped and not tried again for a while; SAIN fights from where the bot is.
        /// Raid 2026-10-20 13:38: two PMCs 17 m apart "broke contact" and "fell back" for eight minutes without moving a metre.
        /// </summary>
        public static void GiveUpMove(BotOwner bot, string why)
        {
            if (bot == null || !_states.TryGetValue(bot.ProfileId, out State s))
            {
                return;
            }

            float now = Time.time;
            CombatStance old = s.Stance;
            if (old != CombatStance.Normal && old != CombatStance.Reposition)
            {
                s.Stance = CombatStance.Normal;
                s.ChangedAt = now;
            }

            s.BreakUntil = now;
            s.Leaving = false;
            s.ProbeUntil = now;
            s.BreakCooldownUntil = Mathf.Max(s.BreakCooldownUntil, now + 90f);
            s.RetreatCooldownUntil = Mathf.Max(s.RetreatCooldownUntil, now + 45f);
            s.PushCooldownUntil = Mathf.Max(s.PushCooldownUntil, now + 30f);
            s.StandoffCooldownUntil = Mathf.Max(s.StandoffCooldownUntil, now + 20f);
            s.StillSince = now;

            // With no round for any gun there is nothing to hold the ground with (Badger stood at one spot for a minute with both rifles dry)
            s.HoldGroundUntil = SafeReload.OutOfRounds(bot) ? now : now + 12f;
            Vector3 at = bot.Position;
            Log.Info($"LR Stuck: {bot.Profile.Nickname} cannot get on ({why}) at ({at.x:0}, {at.y:0}, {at.z:0}) - {old} given up, fights from where it is");
        }

        /// <summary>
        /// Squad maneuvers move a bot that is fighting normally into another stance (spread out = Reposition, trade a fallen friend = Probe).
        /// Only from Normal: a bot already falling back, pushing or running is left alone. True when changed.
        /// </summary>
        public static bool Nudge(BotOwner bot, CombatStance stance, string why, float probeSeconds = 25f)
        {
            if (!Settings.CombatMorale.Value || bot == null || bot.IsDead)
            {
                return false;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Nerve = ((float)_rng.NextDouble() - 0.5f) * 0.3f };
            }

            if (s.Stance != CombatStance.Normal)
            {
                return false;
            }

            s.Stance = stance;
            s.ChangedAt = Time.time;
            if (stance == CombatStance.Probe)
            {
                s.ProbeUntil = Time.time + probeSeconds;
            }

            s.LastReason = why;
            Log.Info($"LR Combat: {bot.Profile.Nickname} Normal -> {stance}: {why}");
            return true;
        }

        public static void Tick()
        {
            if (!Settings.CombatMorale.Value || Time.time < _nextTick)
            {
                return;
            }

            _nextTick = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                try
                {
                    TickBot(player?.AIData?.BotOwner);
                }
                catch (Exception e)
                {
                    // One odd bot must not stop the evaluation of everyone after it in the list
                    Diag.Report("CombatSense", e);
                }
            }
        }

        private static void TickBot(BotOwner bot)
        {
            if (bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
            {
                return;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Nerve = ((float)_rng.NextDouble() - 0.5f) * 0.3f };
            }

            float health = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            s.HealthHistory.Enqueue((Time.time, health));
            while (s.HealthHistory.Count > 0 && Time.time - s.HealthHistory.Peek().time > 10f)
            {
                s.HealthHistory.Dequeue();
            }

            Vector3 pos = bot.Position;
            if ((pos - s.LastPos).sqrMagnitude > 1.5f * 1.5f || s.StillSince <= 0f)
            {
                s.LastPos = pos;
                s.StillSince = Time.time;
            }

            SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(bot);
            if (sain == null || sain.GoalEnemy == null)
            {
                // Fight over (or no enemy known any more): back to normal, the next fight starts with fresh morale.
                // A bot sent to trade a fallen friend (Probe) may not have the killer in SAIN yet: it keeps the stance a moment longer
                FightRoles.Forget(bot);
                if (s.Stance != CombatStance.Normal && (s.Stance != CombatStance.BreakContact || Time.time > s.BreakUntil)
                    && (s.Stance != CombatStance.Probe || Time.time > Mathf.Min(s.ProbeUntil, s.ChangedAt + 3f)))
                {
                    Log.Detail("combat", $"{p.Name} {s.Stance} -> Normal: no enemy any more");
                    s.Stance = CombatStance.Normal;
                    s.ChangedAt = Time.time;
                }

                s.HasMorale = false;
                return;
            }

            if (Time.time < s.NextEval)
            {
                return;
            }

            s.NextEval = Time.time + 1.5f;
            Evaluate(bot, p, sain, s, health);
        }

        private static void Evaluate(BotOwner bot, BotProfile p, SAIN.Components.BotComponent sain, State s, float health)
        {
            // Enemies that are actually in the fight right now
            List<Enemy> known = sain.EnemyController.KnownEnemies.Where(e => e != null && e.EnemyKnown && e.RealDistance < 150f).ToList();
            List<Enemy> active = known.Where(e => e.IsVisible || e.TimeSinceSeen < 20f).ToList();
            if (Standoff(bot, p, s, known))
            {
                return;
            }

            if (active.Count == 0)
            {
                // SAIN still knows of someone, but nobody seen within 20 s and 150 m: no fight to weigh here, SAIN handles the rest.
                // Without this a coward heard a fight 250 m away and sat in Retreat for minutes (morale = its own low courage).
                if (s.Stance == CombatStance.Probe && Time.time < s.ProbeUntil)
                {
                    return;
                }

                FightRoles.Forget(bot);

                if (s.Stance != CombatStance.Normal && (s.Stance != CombatStance.BreakContact || Time.time > s.BreakUntil))
                {
                    Log.Detail("combat", $"{p.Name} {s.Stance} -> Normal: no enemy close or seen lately");
                    s.Stance = CombatStance.Normal;
                    s.ChangedAt = Time.time;
                }

                s.HasMorale = false;
                return;
            }

            int enemies = active.Count;
            int humans = active.Count(e => !e.IsAI);

            if (s.Stance == CombatStance.Probe && Time.time < s.ProbeUntil && !active.Any(e => e.IsVisible))
            {
                // Out looking for the enemy: no weighing until it is found or the search time is over
                return;
            }

            HashSet<string> squad = LootBrain.SquadIds(bot);
            int allies = 0;
            foreach (Player ally in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (squad.Contains(ally.ProfileId) && (ally.Position - bot.Position).sqrMagnitude < 70f * 70f)
                {
                    allies++;
                }
            }

            allies = Math.Max(1, allies);
            if (s.InitialSquad < 0)
            {
                s.InitialSquad = squad.Count;
            }

            int aliveSquad = Singleton<GameWorld>.Instance.AllAlivePlayersList.Count(pl => squad.Contains(pl.ProfileId));
            int lost = Math.Max(0, s.InitialSquad - aliveSquad);
            if (lost > s.LastLost)
            {
                s.LastLossAt = Time.time;
            }

            s.LastLost = lost;
            float lossWeight = lost * Mathf.Clamp01(1f - (Time.time - s.LastLossAt) / LossFadeSeconds);

            float healthDrop = s.HealthHistory.Count > 0 ? s.HealthHistory.Peek().health - health : 0f;
            float suppression;
            switch (sain.Suppression.CurrentState)
            {
                case ESuppressionState.Extreme: suppression = 0.7f; break;
                case ESuppressionState.Heavy: suppression = 0.4f; break;
                case ESuppressionState.Medium: suppression = 0.15f; break;
                default: suppression = 0f; break;
            }

            float outnumbered = Mathf.Clamp((float)enemies / allies - 1f, 0f, 3f);
            float danger = 0.6f * outnumbered + (1f - health) * 1.4f + 0.35f * lossWeight + suppression
                           + (healthDrop > 0.25f ? 0.5f : 0f) + 0.1f * humans;

            // Nearly dead is nearly dead, however brave
            if (health < 0.3f)
            {
                danger += 1.2f * (0.3f - health) / 0.3f;
            }

            float courage = 0.4f + p.Aggression + (int)p.Skill * 0.12f + s.Nerve;

            // The squad's doctrine as this member plays it: a storm squad stays in, a quiet one lets go sooner
            DoctrineDeck.Member doctrine = SquadDoctrines.Of(bot);
            if (doctrine != null)
            {
                courage += (doctrine[DoctrineDeck.Aggression] - 0.5f) * 0.6f + (doctrine[DoctrineDeck.Grit] - 0.5f) * 0.5f;
            }

            // A rich kit and a bag of loot to lose make it careful, nothing to lose makes it bold (PlayerSense)
            courage -= 0.35f * PlayerSense.Stakes(bot);

            // Shaken by a close call, on a kill streak, a hoarder with a rich bag (RaidMood)
            courage += RaidMood.CourageDelta(bot);
            switch (p.Style)
            {
                case PlayStyle.Rusher:
                case PlayStyle.Hunter: courage += 0.3f; break;
                case PlayStyle.Rat: courage -= 0.3f; break;
            }

            if (p.SainPersonality == EPersonality.Coward || p.SainPersonality == EPersonality.Timmy)
            {
                courage -= 0.4f;
            }
            else if (p.SainPersonality == EPersonality.GigaChad || p.SainPersonality == EPersonality.Wreckless)
            {
                courage += 0.4f;
            }

            // Fear is quick, confidence comes back slowly: a bad moment counts at once, a good one needs a few seconds to settle in.
            // Without this the stance flipped every evaluation when an enemy stepped in and out of view.
            float raw = courage - danger;
            s.Morale = !s.HasMorale || raw < s.Morale ? raw : Mathf.Lerp(s.Morale, raw, 0.4f);
            s.HasMorale = true;
            CombatStance old = s.Stance;
            float held = Time.time - s.ChangedAt;

            // Wounded with meds in the pocket and nobody shooting at it right now: let SAIN patch the bot up.
            // Without this a badly hurt bot kept being sent running (retreat / break contact every minute) and never healed.
            bool needsHeal = bot.Medecine?.FirstAid?.Have2Do == true || bot.Medecine?.SurgicalKit?.HaveWork == true || bot.Medecine?.Using == true;
            // 0.10.13: one seen close by a little while ago counts too - raid 2026-10-31 17:08: PanicMF (hp 14 %) sat down to bandage "with no enemy in sight"
            // while Паштет Опасный, seen 4 m off five seconds before, stood 14 m away - and shot him with the kit in his hands
            bool engaged = active.Any(e => e.IsVisible || e.TimeSinceSeen < 5f || (e.RealDistance < 20f && e.TimeSinceSeen < 15f));
            if (needsHeal && !engaged)
            {
                s.Stance = CombatStance.Normal;
                s.BreakCooldownUntil = Mathf.Max(s.BreakCooldownUntil, Time.time + 30f);
                s.RetreatCooldownUntil = Mathf.Max(s.RetreatCooldownUntil, Time.time + 20f);
                s.LastReason = $"patching up (hp {health:P0}, no enemy in sight)";
                if (old != CombatStance.Normal)
                {
                    s.ChangedAt = Time.time;
                    Log.Info($"LR Combat: {p.Name} ({p.Style}, {p.Skill}) {old} -> Normal to heal; {s.LastReason}");
                }

                return;
            }

            Enemy closest = active.OrderBy(e => e.RealDistance).FirstOrDefault();

            // A rifleman with a sniper/marksman rifle fights from where it stands while the enemy is far: falling back, shifting and going
            // looking every few seconds made such bots run back and forth (they lose their reach the moment they move)
            bool holdLong = Settings.LongGunHolds.Value && IsLongGun(p) && closest != null && closest.RealDistance >= 35f && health >= 0.4f
                            && suppression < 0.7f && !active.Any(e => e.RealDistance < 35f && e.TimeSinceSeen < 20f);

            // Who pushes: with "Fight roles" a role drawn for this fight (character, health and above all the numbers - any character may go in with the
            // edge); without it only aggressive characters (snipers, campers and rats hold and shoot).
            // A bot that still has to patch up does not push: it went Push -> heal -> Push every few seconds and never finished healing.
            FightRole role = FightRoles.Current(bot, p, enemies, allies, health, closest, holdLong);
            bool assault = role != FightRole.None
                ? role == FightRole.Assault
                : p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter
                  || (p.Aggression >= 0.65f && p.Style != PlayStyle.Sniper && p.Style != PlayStyle.Camper && p.Style != PlayStyle.Rat);
            bool canPush = assault && !needsHeal && (old == CombatStance.Push || Time.time > s.PushCooldownUntil);

            // Losing nerve: a clear edge against the bot makes it run, with a small chance that grows with the pressure
            string fleeWhy = null;
            bool fleeing = old != CombatStance.BreakContact && closest != null && closest.RealDistance > 12f && Time.time > s.BreakCooldownUntil
                           && LosesNerve(bot, p, s, health, enemies, allies, aliveSquad, lost, suppression, courage, active, out fleeWhy);

            // Nothing left to shoot with: a player gets out of the fight (and looks for ammo or leaves - LootJobs, ExtractPlanner)
            if (!fleeing && old != CombatStance.BreakContact && closest != null && closest.RealDistance > 6f && Time.time > s.BreakCooldownUntil
                && SafeReload.OutOfRounds(bot))
            {
                fleeing = true;
                fleeWhy = "no rounds left for any of its guns";
            }

            CombatStance want;
            if (fleeing)
            {
                want = CombatStance.BreakContact;
            }
            else if (s.Morale < -0.9f && (health < 0.45f || enemies >= allies + 2) && Time.time > s.BreakCooldownUntil
                && closest != null && closest.RealDistance > 10f && old != CombatStance.BreakContact)
            {
                want = CombatStance.BreakContact;
            }
            else if (old == CombatStance.BreakContact && Time.time < s.BreakUntil)
            {
                // keep running
                want = CombatStance.BreakContact;
            }
            else if (holdLong && (old == CombatStance.Retreat || old == CombatStance.Reposition))
            {
                want = CombatStance.Normal;
                s.RetreatCooldownUntil = Mathf.Max(s.RetreatCooldownUntil, Time.time + 40f);
            }
            else if (old == CombatStance.Retreat && (held > RetreatMaxSeconds || (held > RetreatStillSeconds && Time.time - s.StillSince > RetreatStillSeconds)))
            {
                // SAIN found no cover further away (the bot stood) or the fall-back simply went on too long: fight on from here
                want = CombatStance.Normal;
                s.RetreatCooldownUntil = Time.time + 25f + (float)_rng.NextDouble() * 20f;
                Log.Detail("combat", $"{p.Name} ends Retreat after {held:0} s ({(held > RetreatMaxSeconds ? "long enough" : "nowhere to fall back to, stood still")})");
            }
            else if (!holdLong && ((s.Morale < -0.3f && Time.time > s.RetreatCooldownUntil) || (old == CombatStance.Retreat && ((s.Morale < 0.05f && danger > 0.25f) || held < 6f))))
            {
                // Falling back: entered below -0.3, left after at least 6 s once morale is clearly back above zero
                // or the danger itself is gone (a timid bot's morale never climbs above zero, it would fall back forever)
                want = CombatStance.Retreat;
            }
            else if (!holdLong && ((s.Morale < 0.1f && (healthDrop > 0.15f || suppression >= 0.4f)) || (old == CombatStance.Reposition && held < 5f && s.Morale < 0.3f)))
            {
                want = CombatStance.Reposition;
            }
            else if (canPush && closest != null && (role != FightRole.None
                         // The role already weighed the numbers: going in needs the nerve (more of it against more enemies), staying in needs the edge to roughly hold
                         ? (old == CombatStance.Push
                             ? s.Morale > -0.1f && allies + 1 >= enemies && closest.RealDistance < 150f
                             : s.Morale > (allies >= enemies ? 0.1f : 0.5f) && closest.RealDistance < 120f)
                         : (old == CombatStance.Push
                             ? s.Morale > 0.55f && allies + 1 >= enemies && closest.RealDistance < 100f
                             : s.Morale > 0.8f && allies >= enemies && closest.RealDistance < 80f)))
            {
                // Pushing: needs a clear edge to start, keeps going while the edge roughly holds
                want = CombatStance.Push;
            }
            else
            {
                want = CombatStance.Normal;
            }

            // Calming down (towards Normal / Push) waits at least 4 s in a stance; getting more careful happens at once
            if (want != old && Rank(want) < Rank(old) && held < 4f)
            {
                want = old;
            }

            if (want == CombatStance.BreakContact && old != CombatStance.BreakContact)
            {
                s.BreakPoint = FindBreakPoint(bot, active);
                if (s.BreakPoint == null)
                {
                    want = CombatStance.Retreat;
                    Log.Detail("combat", $"{p.Name} wanted to break contact but found no way out, falls back instead");
                }
                else
                {
                    s.BreakUntil = Time.time + 25f;
                    s.BreakCooldownUntil = Time.time + 60f;
                    if (fleeing)
                    {
                        // Leaving the fight, not falling back inside it: on arrival back to the day's business, and the enemies are forgotten for a while
                        s.Leaving = true;
                        Flees++;
                        EnemyLapse.Fled(bot, 45f + (float)_rng.NextDouble() * 75f);
                        Log.Info($"LR Combat: {p.Name} ({p.Style}, {p.Skill}) loses its nerve and leaves the fight: {fleeWhy}");
                    }
                }
            }

            if (old == CombatStance.Push && want != CombatStance.Push)
            {
                s.PushCooldownUntil = Time.time + 12f + (float)_rng.NextDouble() * 10f;
            }

            // Flank role: once the enemy is out of sight the bot goes round him (Maneuvers); if it cannot, the role is drawn again without it
            if (role == FightRole.Flank && want == CombatStance.Normal && closest != null && !closest.IsVisible && closest.TimeSinceSeen > 3f
                && !Maneuvers.IsFlanking(bot) && !Maneuvers.TrySoloFlank(bot, closest))
            {
                FightRoles.FlankFailed(bot);
            }

            s.Stance = want;
            if (s.Stance != old)
            {
                s.ChangedAt = Time.time;
            }

            if ((want == CombatStance.Retreat || want == CombatStance.BreakContact) && Time.time >= s.RegroupUntil)
            {
                s.LostFightAt = Time.time;
                // Where the enemy was (the place not to walk back through), or the bot itself
                s.LostFightPos = closest != null ? closest.LastKnownPosition ?? closest.EnemyPosition : bot.Position;
            }

            s.LastReason = $"morale {s.Morale:0.00} (courage {courage:0.00}, danger {danger:0.00}: {enemies} vs {allies}, hp {health:P0}, lost {lost}, supp {suppression:0.0}{(role != FightRole.None ? ", role " + role : "")})";
            if (s.Stance != old)
            {
                Log.Info($"LR Combat: {p.Name} ({p.Style}, {p.Skill}) {old} -> {s.Stance}; {s.LastReason}");
                Announce(bot, s.Stance);
            }
        }

        /// <summary>
        /// Pressure to leave the fight, from the numbers and the bot's own state, weighed by its character. Every term is small, so one thing alone
        /// (a lone man against two) rarely breaks anyone, and several together (alone, hurt, three against one, better kit against him) often do.
        ///   outnumbered          0.30 per extra enemy per friend (2 v 1 = 0.30, 3 v 1 = 0.60, 4 v 1 = 0.90)
        ///   alone after a squad  0.30 (0.40 if the squad was 3+), fading over 3 minutes since the last loss; a part of the squad lost: 0.25 x share
        ///   hurt                 0.35 x (1 - health), +0.15 under 35 %
        ///   pinned down          0.40 x suppression
        ///   better kit against   0.12 per 100 % more equipment power (up to +200 %)
        /// The sum is scaled by nerve: x1.5 for the most timid, x0.45 for the boldest (courage = aggression, skill, style, SAIN personality).
        /// Chance per check (every 1.5 s) = ((pressure - 0.5) / 1.2)^1.3 x 0.22 x the F12 factor: 1.0 effective pressure is about 7 % per check
        /// (some 20 s of it before a run), 0.7 about 2 %, under 0.5 nothing; x3 for six seconds after a squadmate fell.
        /// </summary>
        private static bool LosesNerve(BotOwner bot, BotProfile p, State s, float health, int enemies, int allies, int aliveSquad, int lost, float suppression,
            float courage, List<Enemy> active, out string why)
        {
            why = null;
            if (!Settings.FleeMorale.Value)
            {
                return false;
            }

            float press = 0.3f * Mathf.Clamp((enemies - allies) / (float)Mathf.Max(1, allies), 0f, 3f);
            float fade = s.LastLossAt > 0f ? Mathf.Clamp01(1f - (Time.time - s.LastLossAt) / 180f) : 0f;
            bool wasSquad = s.InitialSquad > 1;
            bool alone = wasSquad && aliveSquad <= 1;
            if (alone)
            {
                press += (s.InitialSquad >= 3 ? 0.4f : 0.3f) * Mathf.Max(0.35f, fade);
            }
            else if (wasSquad && lost > 0)
            {
                press += 0.25f * lost / s.InitialSquad * fade;
            }

            press += 0.35f * (1f - health) + (health < 0.35f ? 0.15f : 0f);
            press += 0.4f * suppression;

            float theirPower = 0f;
            foreach (Enemy e in active)
            {
                float power = e.EnemyPlayer?.AIData?.PowerOfEquipment ?? 0f;
                theirPower = Mathf.Max(theirPower, power);
            }

            float myPower = bot.GetPlayer?.AIData?.PowerOfEquipment ?? 0f;
            if (theirPower > 0f && myPower > 0f)
            {
                press += 0.12f * Mathf.Clamp(theirPower / myPower - 1f, 0f, 2f);
            }

            float bravery = Mathf.Clamp01((courage - 0.4f) / 1.6f);
            float effective = press * Mathf.Lerp(1.5f, 0.45f, bravery);
            float chance = Mathf.Pow(Mathf.Clamp01((effective - 0.5f) / 1.2f), 1.3f) * 0.22f * Settings.FleeChance.Value;
            if (lost > 0 && Time.time - s.LastLossAt < 6f)
            {
                chance *= 3f;
            }

            if (chance <= 0.0005f || _rng.NextDouble() >= chance)
            {
                return false;
            }

            why = $"{enemies} against {allies}{(alone ? $", left alone (the squad was {s.InitialSquad})" : lost > 0 ? $", {lost} of the squad down" : "")}, hp {health:P0}" +
                  $"{(suppression > 0.3f ? ", pinned down" : "")}{(theirPower > myPower * 1.5f && myPower > 0f ? ", better kit against it" : "")} " +
                  $"(pressure {press:0.00} x nerve {Mathf.Lerp(1.5f, 0.45f, bravery):0.00}, chance {chance:P0} per check)";
            return true;
        }

        /// <summary>
        /// How far the gun in the bot's hands really reaches (a player with a shotgun does not trade shots with a rifle at 90 m): with "Fight at the range
        /// of the gun" the effective range of its weapon type from the game plus the F12 margin (RangeSense), otherwise the old table by class.
        /// </summary>
        public static float Reach(BotOwner bot)
        {
            EFT.InventoryLogic.Weapon gun = bot?.WeaponManager?.CurrentWeapon;
            float reach = Settings.GunRange.Value ? RangeSense.EffectiveRange(gun) : RangeSense.OldReach(gun?.WeapClass);
            return reach > 0f ? reach : 999f;
        }

        public static int OutrangedLeft { get; private set; }

        /// <summary>
        /// Out-gunned at range: an enemy beyond the reach of the bot's gun has it in his sights, and SAIN keeps leading it back into his view
        /// (raid 2026-10-26 14:59: cybensis with a shotgun stood 40 s on one "cover" spot facing a marksman on a tower 93 m away; stepped out of his
        /// sight twice, SAIN walked him back both times, headshot). A player goes round the shooter out of his view - or leaves him be.
        /// True when the bot now flanks or breaks contact.
        /// </summary>
        public static bool LeaveOutranged(BotOwner bot, Enemy shooter)
        {
            if (!Settings.CombatMorale.Value || bot == null || bot.IsDead || shooter == null)
            {
                return false;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Nerve = ((float)_rng.NextDouble() - 0.5f) * 0.3f };
            }

            float now = Time.time;
            if (s.Stance == CombatStance.BreakContact || s.Stance == CombatStance.Push || now < s.BreakCooldownUntil)
            {
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            string who = p?.Name ?? bot.Profile.Nickname;
            bool bold = p != null && (p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter || p.Aggression >= 0.6f);
            if (bold && _rng.NextDouble() < 0.5 && Maneuvers.TrySoloFlank(bot, shooter))
            {
                OutrangedLeft++;
                Log.Info($"LR Combat: {who} is out-gunned by {shooter.EnemyPlayer?.Profile?.Nickname} at {shooter.RealDistance:0} m - goes round him out of his view");
                return true;
            }

            return BreakOff(bot, shooter, $"out-gunned by {shooter.EnemyPlayer?.Profile?.Nickname} at {shooter.RealDistance:0} m " +
                                          $"(its {bot.WeaponManager?.CurrentWeapon?.WeapClass} reaches ~{Reach(bot):0} m)");
        }

        /// <summary>
        /// Leaves an enemy it cannot answer be: a run of 45-90 m away from him, then back to its own business; he is "not known" to SAIN for 30-70 s.
        /// Used for a shooter out of the reach of the bot's gun (LeaveOutranged, RangeSense). True when the bot now breaks contact.
        /// </summary>
        /// <summary>A bound of the squad (SquadPlaybook): the bot may go in again now, not only after the usual pause between pushes.</summary>
        public static void AllowPush(BotOwner bot)
        {
            if (bot != null && _states.TryGetValue(bot.ProfileId, out State s) && Time.time >= s.HoldGroundUntil)
            {
                s.PushCooldownUntil = 0f;
            }
        }

        /// <summary>The squad falls back to regroup (SquadPlaybook): a break-off that is a plan, so it is no lost fight for leaving the raid.</summary>
        public static bool FallBack(BotOwner bot, Enemy shooter, float seconds, string why)
        {
            if (!Settings.CombatMorale.Value || bot == null || bot.IsDead || shooter == null)
            {
                return false;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Nerve = ((float)_rng.NextDouble() - 0.5f) * 0.3f };
            }

            float now = Time.time;
            if (s.Stance == CombatStance.BreakContact)
            {
                return false;
            }

            // A place 45-90 m back with walls about; unlike a break-off the enemy is not forgotten - the squad comes back at him
            Vector3? point = FindBreakPoint(bot, new List<Enemy> { shooter });
            if (point == null)
            {
                return false;
            }

            CombatStance old = s.Stance;
            s.BreakPoint = point;
            s.Stance = CombatStance.BreakContact;
            s.BreakUntil = now + Mathf.Min(seconds, 25f);

            // Running back over open ground with a smoke in the rig: some put it between them and the enemy (FireTricks)
            if (_rng.NextDouble() < 0.4)
            {
                FireTricks.Smoke(bot, shooter.LastKnownPosition ?? shooter.EnemyPosition, "covers its fall-back");
            }

            // On arrival: back to the squad's fight (Normal), not on into a retreat
            s.Leaving = true;
            s.ChangedAt = now;
            s.StillSince = now;
            s.RegroupUntil = now + seconds;
            Log.Info($"LR Combat: {BotProfiles.Get(bot)?.Name ?? bot.Profile.Nickname} {why} ({old} -> BreakContact)");
            Announce(bot, s.Stance);
            return true;
        }

        public static bool BreakOff(BotOwner bot, Enemy shooter, string why)
        {
            if (!Settings.CombatMorale.Value || bot == null || bot.IsDead || shooter == null)
            {
                return false;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { Nerve = ((float)_rng.NextDouble() - 0.5f) * 0.3f };
            }

            float now = Time.time;
            if (s.Stance == CombatStance.BreakContact || now < s.BreakCooldownUntil)
            {
                return false;
            }

            Vector3? point = FindBreakPoint(bot, new List<Enemy> { shooter });
            if (point == null)
            {
                return false;
            }

            CombatStance old = s.Stance;
            s.BreakPoint = point;
            s.Stance = CombatStance.BreakContact;
            s.BreakUntil = now + 25f;
            s.BreakCooldownUntil = now + 60f;
            if (_rng.NextDouble() < 0.35)
            {
                FireTricks.Smoke(bot, shooter.LastKnownPosition ?? shooter.EnemyPosition, "covers its break-off");
            }
            s.Leaving = true;
            s.ChangedAt = now;
            s.StillSince = now;
            OutrangedLeft++;
            EnemyLapse.Fled(bot, 30f + (float)_rng.NextDouble() * 40f);
            Log.Info($"LR Combat: {BotProfiles.Get(bot)?.Name ?? bot.Profile.Nickname} is {why} - leaves him be ({old} -> BreakContact)");
            Announce(bot, s.Stance);
            return true;
        }

        /// <summary>Carries a sniper or marksman rifle (or is a sniper by style): fights at range from one place.</summary>
        internal static bool IsLongGun(BotProfile p) =>
            p != null && (p.WeaponClass == "sniperRifle" || p.WeaponClass == "marksmanRifle" || p.Style == PlayStyle.Sniper);

        /// <summary>
        /// Two bots 30 m apart, each "in cover" facing an enemy it has not seen for a minute, stood for minutes (SAIN waits for a sound).
        /// A player would not: after a while that depends on the character it goes looking (rushers in seconds, rats much later),
        /// shifts to another spot, or gives the fight up and leaves. True when the stance was changed here.
        /// </summary>
        private static bool Standoff(BotOwner bot, BotProfile p, State s, List<Enemy> known)
        {
            // The bot's own target unseen for 6 s and nobody in view right now. It used to wait until no known enemy at all had been seen
            // for 6 s - with several enemies about that was rarely true, and one bot stood 177 s before going looking (raid 2026-10-20)
            Enemy goal = LiveRaid.Client.Bots.SainBot.Of(bot)?.GoalEnemy;
            if (known.Count == 0 || Time.time < s.StandoffCooldownUntil || s.Stance == CombatStance.BreakContact || s.Stance == CombatStance.Probe
                || known.Any(e => e.IsVisible) || (goal != null && (goal.IsVisible || goal.TimeSinceSeen < 6f)))
            {
                return false;
            }

            float wait;
            switch (p.Style)
            {
                // Session 10: nobody holds an unseen enemy for long (was 14 / 20 / 55 / 70 / 30 s)
                case PlayStyle.Rusher: wait = 8f; break;
                case PlayStyle.Hunter: wait = 12f; break;
                case PlayStyle.Sniper: wait = 22f; break;
                case PlayStyle.Camper:
                case PlayStyle.Rat: wait = 18f; break;
                default: wait = 18f; break;
            }

            // Personal patience: the same bot is not always as patient (+-25 %)
            wait *= 0.75f + 0.5f * (float)_rng.NextDouble();
            bool longGun = Settings.LongGunHolds.Value && IsLongGun(p);
            if (longGun)
            {
                wait *= 2f;
            }

            float still = Time.time - s.StillSince;
            if (still < wait)
            {
                return false;
            }

            Enemy nearest = known.OrderBy(e => e.RealDistance).First();
            bool bold = p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter || p.Aggression >= 0.6f;
            bool timid = p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper || s.Morale < -0.2f;
            double roll = _rng.NextDouble();
            CombatStance old = s.Stance;
            string what;
            if (!timid && bold && roll < 0.45 && Maneuvers.TrySoloFlank(bot, nearest))
            {
                // Instead of walking straight at where it went quiet: around to its side (Maneuvers, flank layer)
                what = "goes around to flank it";
            }
            else if (!timid && (bold || roll < 0.5) && nearest.RealDistance < 120f)
            {
                s.Stance = CombatStance.Probe;
                s.ProbeUntil = Time.time + 25f + (float)_rng.NextDouble() * 20f;
                what = "goes looking for it";
            }
            else if (!timid && roll < 0.8)
            {
                s.Stance = CombatStance.Reposition;
                what = "shifts to another spot";
            }
            else
            {
                s.BreakPoint = FindBreakPoint(bot, known);
                if (s.BreakPoint == null)
                {
                    s.Stance = CombatStance.Probe;
                    s.ProbeUntil = Time.time + 20f;
                    what = "cannot get away, goes looking for it";
                }
                else
                {
                    s.Stance = CombatStance.BreakContact;
                    s.BreakUntil = Time.time + 25f;
                    s.Leaving = true;
                    what = "gives the fight up and leaves";
                }
            }

            s.ChangedAt = Time.time;
            s.StillSince = Time.time;
            s.StandoffCooldownUntil = Time.time + (longGun ? 120f : 40f) + (float)_rng.NextDouble() * 30f;
            Log.Info($"LR Combat: {p.Name} ({p.Style}) stood {still:0} s facing {nearest.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} " +
                     $"{nearest.RealDistance:0} m away, unseen for {Mathf.Min(nearest.TimeSinceSeen, 999f):0} s - {what} ({old} -> {s.Stance})");
            Announce(bot, s.Stance);
            return true;
        }

        /// <summary>How defensive a stance is: Push 0 ... BreakContact 4.</summary>
        private static int Rank(CombatStance stance)
        {
            switch (stance)
            {
                case CombatStance.Push:
                case CombatStance.Probe: return 0;
                case CombatStance.Normal: return 1;
                case CombatStance.Reposition: return 2;
                case CombatStance.Retreat: return 3;
                default: return 4;
            }
        }

        private static void Announce(BotOwner bot, CombatStance stance)
        {
            switch (stance)
            {
                case CombatStance.Push: SquadBrain.Say(bot, EPhraseTrigger.Gogogo, 0.5f); break;
                case CombatStance.Retreat: SquadBrain.Say(bot, EPhraseTrigger.GetBack, 0.5f); break;
                case CombatStance.BreakContact: SquadBrain.Say(bot, EPhraseTrigger.Regroup, 0.6f); break;
                case CombatStance.Reposition: SquadBrain.Say(bot, EPhraseTrigger.CoverMe, 0.3f); break;
                case CombatStance.Probe: SquadBrain.Say(bot, EPhraseTrigger.Going, 0.3f); break;
            }
        }

        /// <summary>A point 45-90 m away from the enemies, reachable, preferably with walls around it.</summary>
        private static Vector3? FindBreakPoint(BotOwner bot, List<Enemy> enemies)
        {
            if (enemies.Count == 0)
            {
                return null;
            }

            Vector3 centroid = Vector3.zero;
            foreach (Enemy e in enemies)
            {
                centroid += e.EnemyPosition;
            }

            centroid /= enemies.Count;
            Vector3 away = bot.Position - centroid;
            away.y = 0;
            if (away.sqrMagnitude < 0.01f)
            {
                away = -bot.LookDirection;
            }

            away.Normalize();
            NavMeshPath path = new NavMeshPath();
            Vector3? best = null;
            float bestScore = float.MinValue;
            float[] angles = { 0f, 30f, -30f, 60f, -60f };
            float[] distances = { 45f, 65f, 90f };
            foreach (float angle in angles)
            {
                Vector3 dir = Quaternion.Euler(0, angle, 0) * away;
                foreach (float dist in distances)
                {
                    if (!NavMesh.SamplePosition(bot.Position + dir * dist, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    if (!NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        continue;
                    }

                    float fromEnemies = enemies.Min(e => Vector3.Distance(e.EnemyPosition, hit.position));
                    float score = fromEnemies * 0.05f + (1f - LootBrain.Openness(hit.position)) * 1.5f - Mathf.Abs(angle) * 0.01f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = hit.position;
                    }
                }
            }

            return best;
        }
    }
}
