using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// How the bots' fire reaches the bot you play: every hit (part, damage, shooter, distance, health left) and every bullet that passed within 10 m
    /// (SAIN's fly-by), with a summary when the take-over ends. Session 34: "bots hit the taken bot much harder than you, as if only its camera were a
    /// hitbox" - nothing in EFT or SAIN treats its colliders differently, so the log has to show what really happens: few shots at it, or many
    /// that miss.
    /// </summary>
    internal static class TakenBotFire
    {
        private static Player _player;
        private static PlayerComponent _sain;
        private static int _hits;
        private static int _flyBys;
        private static float _damage;
        private static float _since;
        private static readonly Dictionary<EBodyPart, int> Parts = new Dictionary<EBodyPart, int>();
        private static readonly Dictionary<string, int> FlyBysBy = new Dictionary<string, int>();

        public static void Watch(Player player)
        {
            Stop();
            if (player?.HealthController == null)
            {
                return;
            }

            _player = player;
            _hits = 0;
            _flyBys = 0;
            _damage = 0f;
            _since = UnityEngine.Time.time;
            Parts.Clear();
            FlyBysBy.Clear();
            player.HealthController.ApplyDamageEvent += OnDamage;
            try
            {
                if (GameWorldComponent.TryGetPlayerComponent(player, out PlayerComponent component) && component != null)
                {
                    _sain = component;
                    _sain.OnBulletFlyBy += OnFlyBy;
                }
            }
            catch (Exception e)
            {
                Log.Detail("debug", "LR Spectator: no SAIN fly-by count for the taken bot: " + e.Message);
            }
        }

        /// <summary>The take-over ended: one line of what the bot you played went through.</summary>
        public static void Stop()
        {
            Player player = _player;
            if (player == null)
            {
                return;
            }

            try
            {
                player.HealthController.ApplyDamageEvent -= OnDamage;
            }
            catch
            {
                // a destroyed body
            }

            if (_sain != null)
            {
                _sain.OnBulletFlyBy -= OnFlyBy;
            }

            string parts = string.Join(", ", Parts.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"));
            string shooters = string.Join(", ", FlyBysBy.OrderByDescending(p => p.Value).Take(5).Select(p => $"{p.Key} {p.Value}"));
            Log.Info($"LR Spectator: under fire as {player.Profile.Nickname} for {UnityEngine.Time.time - _since:0} s - {_flyBys} bullets passed within 10 m" +
                     $"{(shooters.Length > 0 ? " (" + shooters + ")" : "")}, {_hits} hits for {_damage:0} damage{(parts.Length > 0 ? " (" + parts + ")" : "")}");
            _player = null;
            _sain = null;
        }

        private static void OnDamage(EBodyPart part, float damage, DamageInfo info)
        {
            Diag.Guard("Spectator.TakenBotHit", () =>
            {
                Player player = _player;
                if (player == null || damage <= 0f)
                {
                    return;
                }

                _hits++;
                _damage += damage;
                Parts[part] = Parts.TryGetValue(part, out int n) ? n + 1 : 1;
                Player shooter = info.Player?.iPlayer as Player;
                ValueStruct hp = player.HealthController.GetBodyPartHealth(EBodyPart.Common);
                Log.Info($"LR Spectator: {player.Profile.Nickname} hit in {part} for {damage:0} ({info.DamageType}) by " +
                         $"{(shooter != null ? shooter.Profile.Nickname + $" from {(shooter.Position - player.Position).magnitude:0} m" : "nobody known")}; " +
                         $"health {hp.Current:0}/{hp.Maximum:0}");
            });
        }

        private static void OnFlyBy(PlayerComponent source, Shot shot)
        {
            _flyBys++;
            string name = source?.Player?.Profile?.Nickname ?? "?";
            FlyBysBy[name] = FlyBysBy.TryGetValue(name, out int n) ? n + 1 : 1;
        }
    }
}
