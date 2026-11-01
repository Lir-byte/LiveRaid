using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// "Start raids as an observer" (F12 -> 11). EFT cannot run a raid without your character, so the body stays where it spawned,
    /// but it is taken out of the game: its controls are off (as at death), it is hidden, nothing can hurt or kill it
    /// (<c>ObserverDamagePatch</c>), and no bot or group may take it for an enemy (<c>ObserverEnemyPatches</c>; anything that slipped
    /// into a group's or a bot's enemy list is swept out every second). The server mod is told at the start that this raid does not
    /// count (/liveraid/observer/begin) and the raid ends as a run-through with <see cref="ExitTag"/>: the server keeps the profile as it
    /// was before the raid.
    /// </summary>
    internal static class ObserverMode
    {
        // Same text as the server's ObserverRaids.ExitTag
        public const string ExitTag = "LiveRaid observer";

        private static ActiveHealthController _health;
        private static readonly List<Renderer> _renderers = new List<Renderer>();
        private static float _nextSweep;
        private static float _startableAt = -1f;
        private static int _removed;

        public static Player Body { get; private set; }

        public static bool ServerMarked { get; private set; }

        public static void Clear()
        {
            Body = null;
            _health = null;
            _renderers.Clear();
            _nextSweep = 0f;
            _startableAt = -1f;
            _removed = 0;
            ServerMarked = false;
        }

        public static bool Is(IPlayer player) => Body != null && player != null && player.ProfileId == Body.ProfileId;

        public static bool IsHealth(object healthController) => _health != null && ReferenceEquals(healthController, _health);

        /// <summary>Every frame of the raid until it began: the free camera starts a moment after the raid itself has started.</summary>
        public static void TryStart()
        {
            if (!Settings.ObserverFromStart.Value || SpectatorSession.Observer || SpectatorSession.State != SpectatorState.Off
                || Plugin.DisabledFeatures.Contains("observer"))
            {
                return;
            }

            BaseLocalGame<EftGamePlayerOwner> game = Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (game == null || game.Status != GameStatus.Started || me == null || me.HealthController?.IsAlive != true)
            {
                _startableAt = -1f;
                return;
            }

            if (_startableAt < 0f)
            {
                _startableAt = Time.time + 1f;
                return;
            }

            if (Time.time >= _startableAt)
            {
                SpectatorSession.BeginObserver(game);
            }
        }

        public static void Begin(Player me)
        {
            Body = me;
            _health = me.ActiveHealthController;
            try
            {
                string answer = SPT.Common.Http.RequestHandler.GetJson("/liveraid/observer/begin");
                ServerMarked = answer != null && answer.Contains("true");
            }
            catch (Exception e)
            {
                Log.Warn("LR Spectator: the LiveRaid server mod did not answer (" + e.Message + "); the raid ends as a run-through and may still count");
            }

            if (!ServerMarked)
            {
                Log.Warn("LR Spectator: the server did not confirm the observer raid - is the LiveRaid server mod installed? The raid ends as a run-through");
            }

            _renderers.Clear();
            me.GetComponentsInChildren(true, _renderers);

            // No hunger, thirst or their screen effects while you only watch: EFT's own "metabolism off" (the raid setting of co-op) and full bars
            _health?.DisableMetabolism();
            Sweep();
        }

        /// <summary>
        /// Energy and hydration of the hidden body stay full and their effects (exhaustion, dehydration) never set in: the screen showed your character's
        /// thirst and hunger while you only watched. A bot you take over is not touched - it tires, thirsts and gets hurt like any player (session 34:
        /// the bot of an observer raid kept full bars and never got exhausted).
        /// </summary>
        private static void KeepNeedsFull()
        {
            KeepNeedsFull(_health);
        }

        private static void KeepNeedsFull(ActiveHealthController h)
        {
            if (h == null || !h.IsAlive)
            {
                return;
            }

            if (!h.MetabolismDisabled)
            {
                h.DisableMetabolism();
            }

            ValueStruct energy = h.Energy;
            if (energy.Current < energy.Maximum)
            {
                h.ChangeEnergy(energy.Maximum - energy.Current);
            }

            ValueStruct hydration = h.Hydration;
            if (hydration.Current < hydration.Maximum)
            {
                h.ChangeHydration(hydration.Maximum - hydration.Current);
            }

            h.RemoveEffect<ActiveHealthController.Dehydration>(EBodyPart.Stomach);
            h.RemoveEffect<ActiveHealthController.Exhaustion>(EBodyPart.Head);
        }

        /// <summary>Once a second: keep the body hidden and out of every enemy list.</summary>
        public static void Tick()
        {
            if (Body == null || Time.time < _nextSweep)
            {
                return;
            }

            _nextSweep = Time.time + 1f;
            Sweep();
        }

        private static void Sweep()
        {
            Player body = Body;
            if (body == null)
            {
                return;
            }

            // EFT turns renderers back on when the point of view or culling changes
            foreach (Renderer r in _renderers)
            {
                if (r != null && (r.enabled || r.shadowCastingMode != ShadowCastingMode.Off))
                {
                    r.enabled = false;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                }
            }

            try
            {
                KeepNeedsFull();
            }
            catch (Exception e)
            {
                Diag.Report("ObserverMode.Needs", e);
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            int removed = 0;
            foreach (Player p in world.AllAlivePlayersList)
            {
                BotOwner bot = p?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                try
                {
                    BotsGroup group = bot.BotsGroup;
                    if (group?.Enemies != null && group.Enemies.ContainsKey(body))
                    {
                        group.RemoveEnemy(body, EBotEnemyCause.initial);
                        removed++;
                    }

                    if (bot.EnemiesController?.EnemyInfos != null && bot.EnemiesController.EnemyInfos.ContainsKey(body))
                    {
                        bot.EnemiesController.Remove(body);
                        removed++;
                    }

                    // A warning begun before the patch saw it (or by another route) ends
                    if (bot.WarnData?.WarnPlayerRequest?.warnTargetEnemyInfo?.Person?.ProfileId == body.ProfileId)
                    {
                        bot.WarnData.StopWarn();
                    }
                }
                catch (Exception e)
                {
                    Diag.Report("ObserverMode.Sweep", e);
                }
            }

            if (removed > 0)
            {
                _removed += removed;
                Log.Detail("debug", $"LR Spectator: your hidden body taken out of {removed} enemy lists ({_removed} this raid)");
            }
        }
    }
}
