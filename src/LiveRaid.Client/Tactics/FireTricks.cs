using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// The playbook's tricks that work on single bots (SquadPlaybook calls them; each card has its F12 switch in "Squads"):
    ///  - peek to draw fire: 3-4 times a step out from cover for about half a second and back for 2-3 s (SAIN's "move to engage" / "seek cover"), so an
    ///    enemy who waits for him shoots and shows where he is; once the enemy is in sight the bot shoots and the trick is over;
    ///  - turn to a third party: the bot looks where the new shots came from for 6 s (SAIN's steering), so it sees the newcomer and SAIN fights him;
    ///  - smoke: a smoke grenade thrown between the bot and the enemy (EFT's own bot throw), the bot's grenade set back to its frag afterwards.
    /// </summary>
    public static class FireTricks
    {
        private class Peek
        {
            public int CyclesLeft;
            public float PhaseEnd;
            public bool Out;
        }

        private static readonly Dictionary<string, Peek> _peeks = new Dictionary<string, Peek>();
        private static readonly Dictionary<string, (Vector3 at, float until)> _turns = new Dictionary<string, (Vector3, float)>();
        private static readonly Dictionary<string, float> _restoreAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _smokedAt = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();

        public static int Peeks { get; private set; }
        public static int PeeksAnswered { get; private set; }
        public static int Turns { get; private set; }
        public static int Smokes { get; private set; }

        public static string Summary => $"fire tricks: peeks to draw fire {Peeks} (the enemy showed himself {PeeksAnswered}), turns to a third party {Turns}, smoke screens {Smokes}";

        public static void Clear()
        {
            _peeks.Clear();
            _turns.Clear();
            _restoreAt.Clear();
            _smokedAt.Clear();
            Peeks = PeeksAnswered = Turns = Smokes = 0;
        }

        // ---------------- peek to draw fire ----------------

        public static void StartPeek(BotOwner bot)
        {
            if (bot == null || _peeks.ContainsKey(bot.ProfileId))
            {
                return;
            }

            _peeks[bot.ProfileId] = new Peek { CyclesLeft = 3 + _rng.Next(2), PhaseEnd = Time.time + 0.5f };
            Peeks++;
            Log.Detail("combat", () => $"{bot.Profile?.Nickname} peeks out to draw fire");
        }

        /// <summary>SAIN's decision while the bot plays the peek: out for ~0.5 s, back for 2-3 s; the enemy in sight - shoot, done.</summary>
        public static void ApplyPeek(BotOwner bot, Enemy enemy, ref ECombatDecision result, ref bool __result)
        {
            if (bot == null || enemy == null || !_peeks.TryGetValue(bot.ProfileId, out Peek p))
            {
                return;
            }

            float now = Time.time;
            if (enemy.IsVisible)
            {
                _peeks.Remove(bot.ProfileId);
                PeeksAnswered++;
                result = ECombatDecision.StandAndShoot;
                __result = true;
                return;
            }

            if (now >= p.PhaseEnd)
            {
                p.Out = !p.Out;
                if (p.Out)
                {
                    if (--p.CyclesLeft < 0)
                    {
                        _peeks.Remove(bot.ProfileId);
                        return;
                    }

                    p.PhaseEnd = now + 0.45f + (float)_rng.NextDouble() * 0.3f;
                }
                else
                {
                    p.PhaseEnd = now + 2f + (float)_rng.NextDouble();
                }
            }

            result = p.Out ? ECombatDecision.MoveToEngage : ECombatDecision.SeekCover;
            __result = true;
        }

        // ---------------- turn to a third party ----------------

        /// <summary>The bot was hit in the last 3 s by someone who is not the enemy it fights, from 70 degrees or more off: where from.</summary>
        public static bool ThirdParty(BotOwner bot, Enemy closest, out Vector3 from)
        {
            from = default;
            if (bot == null || closest?.EnemyPlayer == null || !HitReaction.TryGetHit(bot, out HitReaction.Hit hit) || Time.time - hit.At > 3f || hit.By == null
                || hit.By.ProfileId == closest.EnemyProfileId)
            {
                return false;
            }

            Vector3 toEnemy = (closest.LastKnownPosition ?? closest.EnemyPosition) - bot.Position;
            Vector3 toShooter = hit.From - bot.Position;
            toEnemy.y = toShooter.y = 0f;
            if (toEnemy.sqrMagnitude < 1f || toShooter.sqrMagnitude < 1f || Vector3.Angle(toEnemy, toShooter) < 70f)
            {
                return false;
            }

            from = hit.From;
            return true;
        }

        public static void Turn(BotOwner bot, Vector3 at, float seconds)
        {
            if (bot == null)
            {
                return;
            }

            _turns[bot.ProfileId] = (at, Time.time + seconds);
            Turns++;
            SquadBrain.Say(bot, _rng.NextDouble() < 0.5 ? EPhraseTrigger.LeftFlank : EPhraseTrigger.RightFlank, 0.4f);
        }

        /// <summary>SAIN's steering for a turned bot: while no enemy is in sight, the way the third party's shots came from.</summary>
        internal static bool TurnedLook(BotOwner bot, out Vector3 at)
        {
            at = default;
            if (bot == null || !_turns.TryGetValue(bot.ProfileId, out var t))
            {
                return false;
            }

            if (Time.time > t.until)
            {
                _turns.Remove(bot.ProfileId);
                return false;
            }

            at = t.at + Vector3.up * 1.4f;
            return true;
        }

        // ---------------- smoke ----------------

        public static bool HasSmoke(BotOwner bot) => bot?.WeaponManager?.Grenades?.HaveGrenadeOfType(ThrowWeapType.smoke_grenade) == true;

        /// <summary>A smoke between the bot and <paramref name="enemyAt"/> (8-15 m from the bot along that line). True when the throw started.</summary>
        public static bool Smoke(BotOwner bot, Vector3 enemyAt, string why)
        {
            if (!Settings.SmokeScreens.Value || bot == null || bot.IsDead || !HasSmoke(bot) || HandsGuard.RealReload(bot) || bot.Medecine?.Using == true
                || (_smokedAt.TryGetValue(bot.ProfileId, out float last) && Time.time - last < 30f))
            {
                return false;
            }

            BotGrenadeController g = bot.WeaponManager.Grenades;
            if (g.ThrowindNow)
            {
                return false;
            }

            Vector3 dir = enemyAt - bot.Position;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 6f)
            {
                return false;
            }

            Vector3 target = bot.Position + dir / dist * Mathf.Min(dist * 0.5f, 8f + (float)_rng.NextDouble() * 7f);
            try
            {
                g.CheckGrenadeWithType(ThrowWeapType.smoke_grenade);
                if (!g.CanThrowGrenade(target) || !g.DoThrow())
                {
                    g.CheckGrenade();
                    return false;
                }
            }
            catch
            {
                g.CheckGrenade();
                return false;
            }

            _smokedAt[bot.ProfileId] = Time.time;
            _restoreAt[bot.ProfileId] = Time.time + 4f;
            Smokes++;
            SquadBrain.Say(bot, EPhraseTrigger.Covering, 0.5f);
            Log.Info($"LR Combat: {bot.Profile?.Nickname} throws a smoke - {why}");
            return true;
        }

        /// <summary>Twice a second: a bot's grenade goes back to its frag a few seconds after a smoke (SAIN would throw the smoke at an enemy next).</summary>
        public static void Tick(IEnumerable<BotOwner> alive)
        {
            if (_restoreAt.Count == 0)
            {
                return;
            }

            float now = Time.time;
            foreach (BotOwner bot in alive)
            {
                if (bot != null && _restoreAt.TryGetValue(bot.ProfileId, out float at) && now >= at && bot.WeaponManager?.Grenades?.ThrowindNow != true)
                {
                    _restoreAt.Remove(bot.ProfileId);
                    bot.WeaponManager?.Grenades?.CheckGrenade();
                }
            }
        }
    }

    /// <summary>SAIN's steering: a bot turned to a third party looks there while nobody is in sight (FireTricks).</summary>
    public class SainTurnLookPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAINSteeringClass), nameof(SAINSteeringClass.SteerByPriority));

        [PatchPrefix]
        protected static bool PatchPrefix(SAINSteeringClass __instance, ref bool __result)
        {
            try
            {
                BotOwner bot = __instance.BotOwner;
                if (!FireTricks.TurnedLook(bot, out Vector3 at) || __instance.Bot?.EnemyController?.VisibleEnemies?.Count > 0)
                {
                    return true;
                }

                __instance.LookToPoint(at);
                __result = true;
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("SainTurnLookPatch", e);
                return true;
            }
        }
    }
}
