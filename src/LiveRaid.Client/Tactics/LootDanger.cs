using System.Collections.Generic;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Nobody loots with danger about (F12 -> 7 "No looting in danger"). LootingBots' layer has the lowest priority, but a looting job that has started runs on by
    /// itself, and SAIN often does not "know" an enemy yet (or any more): bots went on emptying a body with an enemy right in front of them. A player stops looting
    /// the moment he hears shots near him or footsteps of a stranger, and does not open anything before it is quiet again.
    ///
    /// Danger for a PMC or a scav (squadmates and the bot's own allies never count):
    ///  - it was hit, shot at, or a bullet flew by (EFT's "under fire") a moment ago;
    ///  - shots within 50 m in the last 8 s (not its squad's own);
    ///  - an enemy in sight now or a moment ago, or heard within 50 m in the last 10 s (SAIN);
    ///  - a stranger within 50 m it would hear: running up to 50 m, walking upright up to 25 m, anything closer than 10 m (half that through a floor),
    ///    or one in front of it in plain view within 40 m.
    /// Then a running job is broken off, no new one starts and no scan runs; 6 s after the last sign of danger the bot may loot again.
    /// </summary>
    public static class LootDanger
    {
        private const float HearRange = 50f;
        private const float QuietSeconds = 6f;

        private static readonly Dictionary<string, float> _dangerUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, Vector3> _lastSound = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, (float at, string why)> _cache = new Dictionary<string, (float, string)>();
        private static float _next;

        public static int Stopped { get; private set; }
        public static int Refused { get; private set; }

        public static string Summary => $"looting broken off by danger {Stopped}, loot jobs refused in danger {Refused}";

        public static void Clear()
        {
            _dangerUntil.Clear();
            _lastSound.Clear();
            _cache.Clear();
            _next = 0f;
            Stopped = Refused = 0;
        }

        /// <summary>
        /// Nerve at the loot (F12 -> 7 "Nerve at the loot"), 0.05-0.85: how much it takes to make this bot leave a body or a drawer. A player's tester
        /// (2026-11-02): "bots stop looting to fight whenever they hear noise". Traces of 31.10-02.11: 322 of 771 looting stops were steps heard 20-50 m
        /// off. Greedy and bold ones (looters, rushers, hunters) go on looting through distant steps and only stop for someone close, shots near them or
        /// an enemy in sight; rats and campers keep the old wariness. The hearing ranges, the "someone ahead" range, the gunfire radius and the quiet time
        /// before looting again all shrink with it (nerve 0.6: steps from 32 m instead of 50, quiet again after 5 s).
        /// </summary>
        public static float Nerve(BotOwner bot, BotProfile p)
        {
            if (!Settings.LootNerve.Value || p == null)
            {
                return 0f;
            }

            float n = 0.2f + 0.18f * Mathf.Clamp(p.LootGreed - 1f, -0.6f, 1.2f) + 0.45f * p.Aggression;
            if (p.Style == PlayStyle.Rat)
            {
                n -= 0.15f;
            }
            else if (p.Style == PlayStyle.Camper)
            {
                n -= 0.1f;
            }

            // A steady per-bot spread, so two looters are not alike
            n += ((bot.ProfileId.GetHashCode() & 0xFF) / 255f - 0.5f) * 0.2f;
            return Mathf.Clamp(n, 0.05f, 0.85f);
        }

        private static float Ear(float nerve) => 1f - 0.6f * nerve;

        /// <summary>Why the bot must not loot right now, or null. Cached for a quarter of a second (LootingBots asks often).</summary>
        public static string Why(BotOwner bot)
        {
            if (!Settings.NoLootInDanger.Value || bot == null || bot.IsDead || Spectator.TakeOver.IsTaken(bot))
            {
                return null;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return null;
            }

            float now = Time.time;
            if (_cache.TryGetValue(bot.ProfileId, out var c) && now - c.at < 0.25f)
            {
                return c.why;
            }

            float nerve = Nerve(bot, p);
            string why = Check(bot, nerve);
            if (why != null)
            {
                _dangerUntil[bot.ProfileId] = now + QuietSeconds * (1.2f - 0.6f * nerve);
            }
            else if (_dangerUntil.TryGetValue(bot.ProfileId, out float until) && now < until)
            {
                why = "not quiet yet";
            }

            _cache[bot.ProfileId] = (now, why);
            return why;
        }

        private static string Check(BotOwner bot, float nerve)
        {
            float ear = Ear(nerve);
            float now = Time.time;
            if (HitReaction.SinceHit(bot) < 10f)
            {
                return "was hit";
            }

            var memory = bot.Memory;
            if (memory != null && (memory.IsUnderFire || now - memory.UnderFireTime < 8f))
            {
                return "under fire";
            }

            Vector3 pos = bot.Position;
            HashSet<string> squad = LootBrain.SquadIds(bot);
            if (WorldEvents.ShotPressure(pos, HearRange * (1f - 0.35f * nerve), 8f, squad) > 0.05f)
            {
                return "shooting nearby";
            }

            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            if (sain?.EnemyController?.KnownEnemies != null)
            {
                foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
                {
                    if (e == null || !e.CheckValid() || e.EnemyPlayer == null || squad.Contains(e.EnemyPlayer.ProfileId))
                    {
                        continue;
                    }

                    if (e.IsVisible || e.TimeSinceSeen < 4f)
                    {
                        return $"{e.EnemyPlayer.Profile?.Nickname ?? "an enemy"} in sight";
                    }

                    if (e.RealDistance < HearRange * ear && e.Heard && e.TimeSinceHeard < 10f)
                    {
                        return $"heard {e.EnemyPlayer.Profile?.Nickname ?? "an enemy"} {e.RealDistance:0} m away";
                    }
                }
            }

            return Stranger(bot, pos, squad, ear, nerve);
        }

        /// <summary>A stranger close enough to be heard (or seen standing in front) - what SAIN may not have registered yet.</summary>
        private static string Stranger(BotOwner bot, Vector3 pos, HashSet<string> squad, float ear, float nerve)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            BotsGroup group = bot.BotsGroup;
            if (world == null || group == null)
            {
                return null;
            }

            Vector3 eye = bot.WeaponRoot != null ? bot.WeaponRoot.position : pos + Vector3.up * 1.5f;
            Vector3 facing = bot.LookDirection;
            facing.y = 0f;
            foreach (Player other in world.AllAlivePlayersList)
            {
                if (other == null || other.ProfileId == bot.ProfileId || squad.Contains(other.ProfileId) || other.HealthController?.IsAlive != true)
                {
                    continue;
                }

                Vector3 d = other.Position - pos;
                float dist = d.magnitude;
                if (dist > HearRange)
                {
                    continue;
                }

                bool enemy;
                try
                {
                    enemy = group.IsPlayerEnemy(other);
                }
                catch
                {
                    enemy = false;
                }

                if (!enemy)
                {
                    continue;
                }

                // Through a floor footsteps carry half as far
                float range = (Mathf.Abs(d.y) > 3f ? 0.5f : 1f) * ear;
                Vector3 v = other.Velocity;
                v.y = 0f;
                float speed = v.magnitude;
                bool upright = other.Pose == EPlayerPose.Stand;

                // Coming this way: running steps are worth stopping for at the full 50 m, otherwise from 35 m (raid 2026-10-22: 41 of 84 stops were
                // someone running past 46-50 m off, and the bot never came back to the body)
                Vector3 flatDir = new Vector3(-d.x, 0f, -d.z);
                bool closing = flatDir.sqrMagnitude > 0.01f && Vector3.Dot(v, flatDir.normalized) > 1.5f;
                float runRange = closing ? HearRange : 35f;
                if ((other.IsSprintEnabled && speed > 3f && dist < runRange * range)
                    || (speed > 1.2f && upright && dist < 20f * range)
                    || (speed > 0.4f && dist < 8f * range)
                    || dist < 4f)
                {
                    _lastSound[bot.ProfileId] = other.Position;
                    return $"hears someone {dist:0} m away";
                }

                // Standing in front of it in plain view
                if (dist < 40f * (1f - 0.4f * nerve) && facing.sqrMagnitude > 0.01f)
                {
                    Vector3 flat = d;
                    flat.y = 0f;
                    if (Vector3.Angle(facing, flat) < 70f
                        && !Physics.Linecast(eye, other.Position + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask))
                    {
                        return $"sees someone {dist:0} m ahead";
                    }
                }
            }

            return null;
        }

        /// <summary>The bot may start a loot job now (LootingBots' StartLooting).</summary>
        public static bool AllowStart(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            string why = Why(bot);
            if (why == null)
            {
                return true;
            }

            Refused++;
            Log.Detail("loot", () => $"{bot.Profile?.Nickname} does not start looting: {why}");
            LootingBots.External.PreventBotFromLooting(bot, QuietSeconds);
            return false;
        }

        /// <summary>Four times a second: looting bots in danger stop, bots in danger do not scan.</summary>
        public static void Tick()
        {
            if (!Settings.NoLootInDanger.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
                {
                    continue;
                }

                LootingBrain brain = player.GetComponent<LootingBrain>();
                LootFinder finder = player.GetComponent<LootFinder>();
                if (brain == null || finder == null)
                {
                    continue;
                }

                bool busy = brain.LootTaskRunning || brain.HasActiveLootable;
                bool wantsScan = finder.IsScheduledScan || finder.IsScanRunning;
                if (!busy && !wantsScan)
                {
                    continue;
                }

                string why = Why(bot);
                if (why == null)
                {
                    continue;
                }

                if (busy)
                {
                    Stopped++;
                    Log.Info($"LR Loot: {bot.Profile?.Nickname} stops looting - {why}{(Settings.LootNerve.Value ? $" (nerve {Nerve(bot, BotProfiles.Get(bot)):0.00})" : "")}");
                    LootingBots.External.PreventBotFromLooting(bot, QuietSeconds);

                    // Heard someone: a player freezes and listens that way, he does not walk off (the body / the room is taken up again when it is quiet)
                    if (_lastSound.TryGetValue(bot.ProfileId, out Vector3 sound) && why.StartsWith("hears") && !TacticalTasks.TryGet(bot, out _))
                    {
                        TacticalTasks.Set(bot, new TacticalTask { Kind = TaskKind.Listen, LookAt = sound, Until = Time.time + 3f + UnityEngine.Random.value * 2f });
                    }
                }
                else
                {
                    finder.OverrideNextScanTime(QuietSeconds);
                }
            }
        }
    }
}
