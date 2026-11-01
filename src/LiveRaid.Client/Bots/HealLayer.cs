using System;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using LiveRaid.Client.Tactics;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A wounded bot that nobody is shooting at right now patches itself up - and is NOT taken away in the middle of it.
    ///
    /// Why a layer of our own (raid 19:08, matt5brasil bled from 66 % to 2 % hp over three minutes with meds in his bag): SAIN starts first aid only when
    /// EVERY enemy it still "knows" has gone cold (a gap of 3-20 s since it last saw or heard him, per enemy), and only while one of SAIN's own layers is
    /// in charge (SAINSelfActionClass checks SAINLayersActive). In a firefight with several unseen enemies about that is almost never true, and every time a
    /// heal did start something took the bot away: our assist order, a stance change, a squad regroup. Players bandage at the first quiet moment.
    ///
    /// Priority 27: above SAIN's combat/squad/extract layers and our own, below SAIN's grenade dodge (80) and the route crossing (95). Active when the bot
    /// has something to treat, has meds, no enemy is in sight (or was a moment ago), nobody has hit it in the last seconds - and it stays active until
    /// the treatment is over (or danger returns). F12 -> 9 "Heal at the first quiet moment".
    /// A broken leg is treated at the same quiet moment (LegCare: a splint, or a painkiller). A treatment that changes nothing - the bleed goes on and the hit
    /// points do not rise, twice in a row - is not repeated for 45 s (raid 2026-10-26 18:34: Waffle put the same empty kit on every 5 s while he bled out).
    /// </summary>
    public class HealLayer : CustomLayer
    {
        public const int LayerPriority = 27;

        public static int Started { get; private set; }
        public static int Finished { get; private set; }
        public static int Interrupted { get; private set; }

        public static int Futile { get; private set; }

        public static void ClearCounters() => Started = Finished = Interrupted = Futile = 0;

        public static string Summary => $"heals at the first quiet moment: started {Started}, finished {Finished}, broken off by danger {Interrupted}, " +
                                        $"futile treatments stopped {Futile}";

        private bool _on;
        private float _since;
        private float _lastUsing;
        private float _cooldownUntil;
        private float _nextCheck;
        private bool _cached;
        private float _hpAtStart;
        private bool _bleedAtStart;
        private int _futile;

        public HealLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        public override string GetName() => "LiveRaid Heal";

        public override bool IsActive()
        {
            try
            {
                // BigBrain asks every frame: the full check is made 4 times a second
                if (Time.time < _nextCheck)
                {
                    return _cached;
                }

                _nextCheck = Time.time + 0.25f;
                return _cached = Check();
            }
            catch (Exception e)
            {
                Diag.Report("HealLayer", e);
                End(false);
                return _cached = false;
            }
        }

        private bool Check()
        {
            BotOwner bot = BotOwner;
            if (!Settings.HealFirst.Value || bot.BotState != EBotState.Active || bot.IsDead || bot.GetPlayer?.HealthController?.IsAlive != true
                || Spectator.TakeOver.IsTaken(bot))
            {
                End(false);
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return false;
            }

            BotMedecine med = bot.Medecine;
            if (med?.FirstAid == null)
            {
                return false;
            }

            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            bool critical = hp < 0.3f;
            string danger = Danger(bot, critical, critical || med.FirstAid.IsBleeding);

            if (_on)
            {
                if (med.Using)
                {
                    _lastUsing = Time.time;
                }

                if (danger != null)
                {
                    Log.Detail("combat", $"{p.Name} breaks off treatment: {danger}");
                    HealBreak.CancelNow(bot);
                    End(true);
                    _cooldownUntil = Time.time + (critical ? 2f : 5f);
                    return false;
                }

                // Over: nothing left to treat or nothing left to treat it with - or nothing was used for 8 s (it does not start). Between two items the
                // layer stays on: before, it ended after each one, QuestingBots' "StopToHeal" took the bot for a moment and the layer came back
                // (raid 2026-10-22 15:57: ScottieKnowz LiveRaid Heal <-> BotRecoveryLayer every ~9 s)
                bool aidWork = med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use;
                bool legWork = LegCare.Plan(bot, out _, out _);
                if (!med.Using && ((!aidWork && !legWork) || Time.time - _lastUsing > 8f))
                {
                    bool stalled = Time.time - _lastUsing > 8f && (aidWork || legWork);
                    End(false);
                    if (!stalled && !legWork && WasFutile(bot, p, med, hp))
                    {
                        return false;
                    }

                    if (stalled)
                    {
                        // Nothing could be applied for 8 s: not straight back into another 8 s of standing still (raid 2026-10-22 22:42: MDZZWOC
                        // "starts patching itself up" every 8 s for ten minutes while its hung reload kept the bandage from starting)
                        _cooldownUntil = Time.time + 12f;
                        Log.Detail("combat", $"{p.Name} could not start treatment for 8 s ({(bot.WeaponManager?.Reload?.Reloading == true ? "reload flag up" : "EFT would not start it")}) - tries again in 12 s");
                    }
                    else
                    {
                        Finished++;
                    }

                    return false;
                }

                if (Time.time - _since > 60f)
                {
                    End(false);
                    _cooldownUntil = Time.time + 15f;
                    return false;
                }

                return true;
            }

            if (Time.time < _cooldownUntil || med.Using)
            {
                // Someone else (SAIN) is already treating it: let it
                return false;
            }

            // A light wound is treated too once it is quiet: LootingBots does not loot while there is anything left to treat (the bot stood in loot breaks)
            bool something = med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use;
            bool leg = !something && LegCare.Plan(bot, out _, out _);
            if ((!something && !leg) || (something && hp > 0.7f && !med.FirstAid.IsBleeding && !TacticalTasks.NoThreat(bot)))
            {
                return false;
            }

            if (danger != null)
            {
                return false;
            }

            _on = true;
            _since = Time.time;
            _lastUsing = Time.time;
            _hpAtStart = hp;
            _bleedAtStart = med.FirstAid.IsBleeding;
            Started++;
            Log.Detail("combat", $"{p.Name} starts patching itself up (hp {hp:P0}{(med.FirstAid.IsBleeding ? ", bleeding" : "")}" +
                                 $"{(leg ? ", " + LegCare.Describe(bot.GetPlayer) : "")}{(something ? ", " + med.FirstAid.CurUsingMeds?.ShortName.Localized() : "")})");
            // A squadmate on his way with something for it: it treats itself where it stands and takes the thing after (SquadShare)
            if (!SquadShare.Receiving(bot))
            {
                TacticalTasks.Cancel(bot, "treatment");
            }

            return true;
        }

        /// <summary>
        /// The treatment just over changed nothing: still bleeding and no more hit points than before. Twice in a row - no more tries for 45 s, and it says so.
        /// </summary>
        private bool WasFutile(BotOwner bot, BotProfile p, BotMedecine med, float hp)
        {
            bool still = _bleedAtStart && med.FirstAid.IsBleeding && hp <= _hpAtStart + 0.01f;
            if (!still)
            {
                _futile = 0;
                return false;
            }

            if (++_futile < 2)
            {
                return false;
            }

            _futile = 0;
            Futile++;
            _cooldownUntil = Time.time + 45f;
            Log.Detail("combat", $"{p.Name} keeps treating a bleed that does not stop (hp {hp:P0}, {med.FirstAid.CurUsingMeds?.ShortName.Localized() ?? "no kit"}) - leaves it for 45 s");
            return true;
        }

        /// <summary>Why the bot cannot sit down to treat itself right now, or null when it can.</summary>
        /// <param name="urgent">
        /// Bleeding or near death: an enemy in view far off (35 m and more when near death, 60 m when bleeding) who has not hit the bot for 6 s does not stop
        /// it - raid 2026-10-22 13:52: Jeo bled out over five minutes in view of a marksman 112 m away who never hit him again.
        /// </param>
        private static string Danger(BotOwner bot, bool critical, bool urgent)
        {
            float hitWindow = critical ? 1.5f : 3.5f;
            float seenWindow = critical ? 2f : 5f;

            // EFT's own time of the last hit too: LiveRaid's hit watch runs 4 times a second, and a heal that started in between began in the shooter's
            // face (raid 2026-10-26 20:28: DasWulf, hit by NastyInc from 6 m, sat down to bandage and was killed with the kit in his hands)
            float sinceHit = Mathf.Min(HitReaction.SinceHit(bot), bot.Memory != null && bot.Memory.LastTimeHit > 0f ? Time.time - bot.Memory.LastTimeHit : float.MaxValue);
            if (sinceHit < hitWindow)
            {
                return "was hit";
            }

            if (bot.Memory?.IsUnderFire == true || (bot.Memory != null && bot.Memory.UnderFireTime > 0f && Time.time - bot.Memory.UnderFireTime < (critical ? 1.5f : 3f)))
            {
                return "under fire";
            }

            // It has just broken a treatment off (HealBreak): not straight back into the next one
            if (HealBreak.Since(bot) < (critical ? 2f : 5f))
            {
                return "has just broken off a treatment";
            }

            // An enemy that shot it a moment ago and stands close, whether SAIN has taken him in yet or not
            if (HitReaction.TryGetHit(bot, out HitReaction.Hit hit) && hit.By != null && Time.time - hit.At < 10f && Vector3.Distance(hit.By.Position, bot.Position) < 30f)
            {
                return "the shooter is right here";
            }

            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            if (sain != null)
            {
                foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
                {
                    if (e == null || !e.CheckValid())
                    {
                        continue;
                    }

                    if (e.IsVisible || e.TimeSinceSeen < seenWindow)
                    {
                        if (urgent && e.RealDistance > (critical ? 35f : 60f) && HitReaction.SinceHit(bot) > 6f)
                        {
                            continue;
                        }

                        return "an enemy in sight";
                    }

                    // Close and heard a moment ago: not the time (a badly hurt bot takes the risk further out)
                    if (e.RealDistance < (critical ? 12f : 18f) && e.TimeSinceSeen < 12f)
                    {
                        return "an enemy right here";
                    }
                }
            }

            return null;
        }

        private void End(bool interrupted)
        {
            if (!_on)
            {
                return;
            }

            _on = false;
            if (interrupted)
            {
                Interrupted++;
            }

            BotOwner.SetPose(1f);
        }

        public override Action GetNextAction() => new Action(typeof(HealLogic), "heal");

        public override bool IsCurrentActionEnding() => !_on;

        public override void Stop()
        {
            End(false);
            base.Stop();
        }

        public override void BuildDebugText(StringBuilder sb)
        {
            sb.AppendLine("LiveRaid: patching up " + (_on ? $"for {Time.time - _since:0} s" : "-"));
        }
    }

    public class HealLogic : CustomLogic
    {
        private float _nextTry;

        public HealLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            BotOwner.PatrollingData?.Pause();
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.PatrollingData?.Unpause();
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                BotOwner bot = BotOwner;
                bot.StopMove();
                bot.Sprint(false);
                bot.SetPose(0.5f);
                BotMedecine med = bot.Medecine;
                if (med?.FirstAid == null || med.Using || Time.time < _nextTry)
                {
                    return;
                }

                // Same call SAIN makes: pick the part and the item, then use it
                _nextTry = Time.time + 1.2f;
                if (bot.WeaponManager.Reload.Reloading || bot.GetPlayer.HandsController.IsInInteractionStrictCheck())
                {
                    return;
                }

                if (med.FirstAid.ShallStartUse())
                {
                    med.FirstAid.TryApplyToCurrentPart();
                }
                else if (LegCare.Plan(bot, out EFT.InventoryLogic.Meds item, out EBodyPart part))
                {
                    LegCare.Apply(bot, item, part);
                }
            }
            catch (Exception e)
            {
                Diag.Report("HealLogic", e);
            }
        }
    }
}
