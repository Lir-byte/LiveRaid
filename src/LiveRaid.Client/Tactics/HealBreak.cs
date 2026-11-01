using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A bot stops treating itself the moment it is shot at, like a player who cancels the heal (F12 -> 9 "Stop treatment when shot at").
    ///
    /// A player who is bandaging, injecting or operating and hears a bullet go by cancels (right mouse button): the animation stops, the item keeps what
    /// was not used yet and the gun is back in the hands. EFT's bots did that only when a bullet actually hit them (BotAbstractMeds.OnBeingHitAction ->
    /// CancelCurrent); a bullet that missed, an enemy that walked into view a few metres off or shot at them from the side left them in the animation with
    /// meds in the hands - raid 2026-10-26 20:28: DasWulf began to bandage the moment NastyInc's first shot hit him and was killed with the kit in his hands.
    /// Here, 4 times a second, a bot whose treatment is running (first aid, surgery, a stimulator, a splint or painkiller) breaks it off when:
    ///  - it was hit or shot at (a near miss) after the treatment began;
    ///  - an enemy is in sight within 50 m (a badly hurt bot that is not being shot at goes on when the enemy is farther than 25 m);
    ///  - an enemy it knows of is within 12 m.
    /// Breaking off is EFT's own cancel (the same as on a hit): the med effect in progress is removed - nothing of the item is used up - and the previous
    /// weapon is taken. LiveRaid's heal layer does not start again for a few seconds (<see cref="Since"/>).
    /// While a treatment runs nobody else may take the meds out of the hands for a weapon (<see cref="Running"/>, checked by WeaponSteadiness): SAIN's
    /// and LootingBots' weapon changes cut heals short in the middle (raid 20:28: NastyInc's bandage, twice), without any reason a player would have.
    /// </summary>
    public static class HealBreak
    {
        private static readonly Dictionary<string, float> _startedAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _brokeAt = new Dictionary<string, float>();
        private static float _next;

        public static int Broken { get; private set; }
        public static int ChangesRefused { get; private set; }

        public static string Summary => $"treatments broken off when shot at {Broken}, weapon changes refused in the middle of a treatment {ChangesRefused}";

        public static void Clear()
        {
            _startedAt.Clear();
            _brokeAt.Clear();
            _next = 0f;
            Broken = ChangesRefused = 0;
        }

        /// <summary>Seconds since the bot last broke off a treatment.</summary>
        public static float Since(BotOwner bot) => bot != null && _brokeAt.TryGetValue(bot.ProfileId, out float at) ? Time.time - at : float.MaxValue;

        /// <summary>The meds component that is treating the bot right now (its use not cancelled), or null.</summary>
        private static BotAbstractMeds Using(BotOwner bot)
        {
            BotMedecine med = bot?.Medecine;
            if (med == null)
            {
                return null;
            }

            if (med.FirstAid?.Using == true)
            {
                return med.FirstAid;
            }

            if (med.SurgicalKit?.Using == true)
            {
                return med.SurgicalKit;
            }

            if (med.Stimulators?.Using == true)
            {
                return med.Stimulators;
            }

            return null;
        }

        /// <summary>A treatment is running in the hands: meds out, the use not cancelled, the item not used up yet.</summary>
        public static bool Running(BotOwner bot)
        {
            object hands = bot?.GetPlayer?.HandsController;
            if (hands is Player.MedsController mc)
            {
                Player.MedsController.MedsInHandsOperation op = mc.CurrentOperation;
                return op != null && !op._finished && Using(bot) != null;
            }

            // Injectors go through the quick-use hands
            return hands != null && hands.GetType().Name.Contains("QuickUse") && Using(bot) != null;
        }

        /// <summary>A weapon change asked for while the bot treats itself: refused (the treatment is broken off here, when it has to be).</summary>
        public static bool RefusesChange(BotOwner bot)
        {
            if (!Settings.HealBreakOff.Value || bot == null || !Running(bot))
            {
                return false;
            }

            ChangesRefused++;
            return true;
        }

        /// <summary>LiveRaid's heal layer gives up a treatment because of danger: the meds come out of the hands the player's way.</summary>
        public static void CancelNow(BotOwner bot)
        {
            BotAbstractMeds using_ = Using(bot);
            if (using_ == null || !Running(bot))
            {
                return;
            }

            SafeReload.Run(using_.CancelCurrent);
            _startedAt.Remove(bot.ProfileId);
            _brokeAt[bot.ProfileId] = Time.time;
            Broken++;
        }

        public static void Tick()
        {
            if (!Settings.HealBreakOff.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                try
                {
                    TickBot(bot, player, p, now);
                }
                catch (System.Exception e)
                {
                    Diag.Report("HealBreak", e);
                }
            }
        }

        private static void TickBot(BotOwner bot, Player player, BotProfile p, float now)
        {
            BotAbstractMeds using_ = Using(bot);
            if (using_ == null || !Running(bot))
            {
                _startedAt.Remove(bot.ProfileId);
                return;
            }

            if (!_startedAt.TryGetValue(bot.ProfileId, out float since))
            {
                _startedAt[bot.ProfileId] = since = now;
                return;
            }

            string why = Reason(bot, player, since);
            if (why == null)
            {
                return;
            }

            string item = (using_ as BotAbstractMedsToPart)?.CurUsingMeds?.ShortName?.Localized() ?? "meds";
            SafeReload.Run(using_.CancelCurrent);
            _startedAt.Remove(bot.ProfileId);
            _brokeAt[bot.ProfileId] = now;
            Broken++;
            Log.Detail("combat", () => $"{p.Name} breaks off its {item} after {now - since:0.0} s - {why} - nothing used up, gun in hand");
        }

        private static string Reason(BotOwner bot, Player player, float since)
        {
            if (bot.Memory != null)
            {
                if (bot.Memory.LastTimeHit > since)
                {
                    return "hit";
                }

                if (bot.Memory.UnderFireTime > since)
                {
                    return "shot at";
                }
            }

            float hp = player.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            var sain = SainBot.Of(bot);
            if (sain == null)
            {
                return null;
            }

            foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
            {
                if (e == null || !e.CheckValid())
                {
                    continue;
                }

                float d = e.RealDistance;
                if (e.IsVisible && d < (hp < 0.35f ? 25f : 50f))
                {
                    return $"{e.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} in sight {d:0} m away";
                }

                if (d < 12f && e.TimeSinceSeen < 10f)
                {
                    return $"{e.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} {d:0} m away";
                }
            }

            return null;
        }
    }
}
