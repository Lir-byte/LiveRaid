using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// LiveRaid's combat moves that SAIN does not make itself, priority 25: above SAIN's combat layers (20/22/24) and below QuestingBots
    /// regrouping (26).
    ///  - "Break contact": a beaten bot stops trading shots and runs for it, the way players disengage from a lost fight.
    ///    Control goes back to SAIN after the run (which then seeks cover and heals) or when an enemy gets too close.
    ///  - "Flank" (<see cref="Maneuvers"/>): the bot goes around the enemy to a point at his side, out of his sight where it can,
    ///    and waits there for a while. Any contact (sees him, is shot at) hands the bot back to SAIN at once.
    ///  - "Close in" (<see cref="RangeSense"/>): a bot whose gun does not reach its enemy sprints in until it does.
    ///  - "Out of sight" (<see cref="SightCover"/>): a bot standing in an enemy's view without a shot steps behind the nearest wall.
    /// </summary>
    public class DisengageLayer : CustomLayer
    {
        public const int LayerPriority = 25;

        private enum Mode
        {
            None,
            Break,
            Flank,
            Close,
            Hide,
        }

        private Mode _mode;
        private Vector3? _runFrom;
        private float _runProgressAt;

        public DisengageLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Break Contact";

        public override void Start()
        {
            SainHandover.Take(BotOwner);
            base.Start();
        }

        public override bool IsActive()
        {
            try
            {
                return Current() != Mode.None;
            }
            catch (System.Exception e)
            {
                Diag.Report("DisengageLayer", e);
                return false;
            }
        }

        private Mode Current()
        {
            if (BreakActive())
            {
                return Mode.Break;
            }

            _runFrom = null;

            if (FlankActive())
            {
                return Mode.Flank;
            }

            if (RangeSense.Closing(BotOwner))
            {
                return Mode.Close;
            }

            return SightCover.Active(BotOwner) ? Mode.Hide : Mode.None;
        }

        private bool BreakActive()
        {
            if (!Settings.CombatMorale.Value || !CombatSense.TryGetState(BotOwner, out CombatSense.State s))
            {
                return false;
            }

            if (s.Stance != CombatStance.BreakContact || s.BreakPoint == null || Time.time > s.BreakUntil)
            {
                return false;
            }

            // Arrived: SAIN takes over again (cover, heal, reload); no new run for a while so the bot gets to heal
            if ((BotOwner.Position - s.BreakPoint.Value).sqrMagnitude < 4f)
            {
                Log.Detail("combat", $"{BotOwner.Profile.Nickname} got away to its break point, SAIN takes over (cover, heal)");
                // Gave the fight up: back to its own business. Ran from a losing fight: fall back into cover there
                s.Stance = s.Leaving ? CombatStance.Normal : CombatStance.Retreat;
                s.Leaving = false;
                s.ChangedAt = Time.time;
                s.BreakCooldownUntil = Mathf.Max(s.BreakCooldownUntil, Time.time + 45f);
                return false;
            }

            // Already bandaging: don't drag it away mid-heal
            if (BotOwner.Medecine?.Using == true)
            {
                return false;
            }

            // Someone right on top of us: running is suicide, fight. The run is over for good - otherwise the layer flickered on and off
            // while the enemy moved about the 8 m mark
            SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(BotOwner);
            // Only one that is seen: one unseen behind a wall is no reason to stay - raid 2026-10-26 20:28, NastyInc and Queen-Goddess stood 6 m apart
            // with a wall between them for 70 s; his "give the fight up and leave" ended the same second on this rule
            // 0.10.13: any enemy SAIN knows, not only its goal - raid 2026-10-31 17:08: PanicMF ran from Артур Продавец 85 m off straight into Паштет Опасный,
            // who stood 4 m from him in plain view, and was shot without firing back (the goal enemy was still the far one)
            Enemy near = Nearest(sain, e => e.RealDistance < 8f && (e.IsVisible || e.TimeSinceSeen < 3f));
            if (near != null)
            {
                s.BreakUntil = Time.time;
                Log.Detail("combat", $"{BotOwner.Profile.Nickname} stops running: {near.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} within 8 m, fights");
                return false;
            }

            // An enemy in plain view close by: a bot that runs from him gets shot in the back - and this layer does not shoot
            // (raid 2026-10-22: TetrisGG 20 s "breaking contact" on the spot with lualeet in view 17 m away)
            Enemy seen = Settings.ReturnFire.Value ? Nearest(sain, e => e.IsVisible && e.CanShoot && e.RealDistance < 25f) : null;
            if (seen != null)
            {
                s.BreakUntil = Time.time;
                s.Stance = CombatStance.Normal;
                s.BreakCooldownUntil = Mathf.Max(s.BreakCooldownUntil, Time.time + 30f);
                Log.Detail("combat", $"{BotOwner.Profile.Nickname} stops running: {seen.EnemyPlayer?.Profile?.Nickname} in view {seen.RealDistance:0} m away, fights");
                return false;
            }

            // Running nowhere (blocked): the run is over, it fights from where it is
            Vector3 pos = BotOwner.Position;
            if (_runFrom == null || (pos - _runFrom.Value).sqrMagnitude > 1f)
            {
                _runFrom = pos;
                _runProgressAt = Time.time;
            }
            else if (Time.time - _runProgressAt > 3f)
            {
                _runFrom = null;
                CombatSense.GiveUpMove(BotOwner, "break contact, no way");
                return false;
            }

            return true;
        }

        private static Enemy Nearest(SAIN.Components.BotComponent sain, System.Func<Enemy, bool> match)
        {
            Enemy best = null;
            if (sain?.EnemyController?.KnownEnemies == null)
            {
                return null;
            }

            foreach (Enemy e in sain.EnemyController.KnownEnemies)
            {
                if (e != null && e.CheckValid() && match(e) && (best == null || e.RealDistance < best.RealDistance))
                {
                    best = e;
                }
            }

            return best;
        }

        private bool FlankActive()
        {
            if (!Maneuvers.TryGetFlank(BotOwner, out Maneuvers.Flank f))
            {
                return false;
            }

            string end = FlankEnd(f);
            if (end != null)
            {
                Maneuvers.EndFlank(BotOwner, end);
                return false;
            }

            return true;
        }

        /// <summary>Why the flank is over, or null while it goes on.</summary>
        private string FlankEnd(Maneuvers.Flank f)
        {
            if (!Settings.SquadFlanking.Value)
            {
                return "switched off";
            }

            CombatStance stance = CombatSense.StanceOf(BotOwner);
            if (stance == CombatStance.Retreat || stance == CombatStance.BreakContact)
            {
                return "the fight turned, falls back";
            }

            Enemy e = LiveRaid.Client.Bots.SainBot.Of(BotOwner)?.GoalEnemy;
            if (e == null)
            {
                return "no enemy any more";
            }

            if (e.IsVisible || HitReaction.SinceHit(BotOwner) < 3f)
            {
                return "contact";
            }

            if (e.EnemyProfileId != f.EnemyId && e.TimeSinceSeen < 3f)
            {
                return "another enemy";
            }

            if (e.RealDistance < 12f)
            {
                return "the enemy is close";
            }

            if (BotOwner.Medecine?.Using == true || BotOwner.Medecine?.FirstAid?.Have2Do == true || BotOwner.Medecine?.SurgicalKit?.HaveWork == true)
            {
                return "patching up";
            }

            if (Time.time > f.Until)
            {
                return "took too long";
            }

            if (f.ArrivedAt > 0f && Time.time - f.ArrivedAt > f.HoldFor)
            {
                return "in position, nothing showed up - SAIN takes over";
            }

            // Squadmates' sightings keep the picture of where he is up to date
            if (e.EnemyProfileId == f.EnemyId && e.LastKnownPosition.HasValue)
            {
                f.EnemyAt = e.LastKnownPosition.Value;
            }

            Vector3 pos = BotOwner.Position;
            if ((pos - f.LastPos).sqrMagnitude > 1.5f * 1.5f)
            {
                f.LastPos = pos;
                f.LastMoveAt = Time.time;
            }
            else if (f.ArrivedAt < 0f && Time.time - f.LastMoveAt > 8f)
            {
                return "stuck on the way";
            }

            return null;
        }

        public override Action GetNextAction()
        {
            _mode = Current();
            switch (_mode)
            {
                case Mode.Flank:
                    return new Action(typeof(FlankLogic), "flank");
                case Mode.Close:
                    return new Action(typeof(CloseInLogic), "close in");
                case Mode.Hide:
                    return new Action(typeof(SightCoverLogic), "out of sight");
                default:
                    return new Action(typeof(RunAwayLogic), "break contact");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            Mode now = Current();
            return now == Mode.None || now != _mode;
        }

        public override void BuildDebugText(StringBuilder sb)
        {
            if (Maneuvers.TryGetFlank(BotOwner, out Maneuvers.Flank f))
            {
                sb.AppendLine($"LiveRaid: flanking {f.EnemyName}, {Vector3.Distance(BotOwner.Position, f.Target):0} m to go");
            }
            else if (CombatSense.TryGetState(BotOwner, out CombatSense.State s))
            {
                sb.AppendLine("LiveRaid: breaking contact, " + s.LastReason);
            }
        }
    }

    public class RunAwayLogic : CustomLogic
    {
        private float _nextOrder;

        public RunAwayLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Run();
            }
            catch (System.Exception e)
            {
                Diag.Report("RunAwayLogic", e);
            }
        }

        private void Run()
        {
            if (!CombatSense.TryGetState(BotOwner, out CombatSense.State s) || s.BreakPoint == null)
            {
                return;
            }

            BotOwner.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Sprint(true);
            BotOwner.Steering.LookToMovingDirection();
            if (Time.time > _nextOrder)
            {
                _nextOrder = Time.time + 1f;
                BotOwner.GoToPoint(s.BreakPoint.Value, false, -1f, false, false);
            }
        }
    }

    /// <summary>
    /// The way around: running while far from the enemy, walking at full pace the last stretch with the gun pointed where he was last
    /// seen; at the point it crouches behind what cover there is and watches.
    /// </summary>
    public class FlankLogic : CustomLogic
    {
        private float _nextOrder;
        private float _holdPose = -1f;

        public FlankLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Stop()
        {
            BotOwner.Sprint(false);
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Move();
            }
            catch (System.Exception e)
            {
                Diag.Report("FlankLogic", e);
            }
        }

        private void Move()
        {
            if (!Maneuvers.TryGetFlank(BotOwner, out Maneuvers.Flank f))
            {
                return;
            }

            Vector3 pos = BotOwner.Position;
            float toEnemy = Vector3.Distance(pos, f.EnemyAt);
            Vector3 look = f.EnemyAt + Vector3.up * 1.3f;
            if ((pos - f.Target).sqrMagnitude > 1.8f * 1.8f && f.ArrivedAt < 0f)
            {
                // Runs while far, walks at full pace the last stretch (no creeping: it read as dawdling)
                bool sprint = toEnemy > 35f;
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 1f;
                    BotOwner.SetPose(1f);
                    BotOwner.SetTargetMoveSpeed(1f);
                    BotOwner.Sprint(sprint);
                    BotOwner.GoToPoint(f.Target, !sprint, -1f, false, false);
                }

                if (!sprint && toEnemy < 40f)
                {
                    BotOwner.Steering.LookToPoint(look);
                }
                else
                {
                    BotOwner.Steering.LookToMovingDirection();
                }

                return;
            }

            if (f.ArrivedAt < 0f)
            {
                f.ArrivedAt = Time.time;
                _holdPose = LootBrain.Openness(pos) > 0.6f ? 0.55f : 0.85f;
                Log.Detail("combat", $"{BotOwner.Profile.Nickname} is in position on {f.EnemyName}'s {(f.Side > 0 ? "right" : "left")} flank, " +
                                     $"{toEnemy:0} m from where he was, waits {f.HoldFor:0} s");
                SquadBrain.Say(BotOwner, EPhraseTrigger.OnPosition, f.Solo ? 0.05f : 0.4f);
            }

            BotOwner.StopMove();
            BotOwner.Sprint(false);
            BotOwner.SetPose(_holdPose > 0f ? _holdPose : 0.8f);
            BotOwner.Steering.LookToPoint(look);
        }
    }
}
