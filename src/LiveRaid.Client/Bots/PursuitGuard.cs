using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// EFT's own "Pursuit" layer ("axeman" hunting): wild bots go for a player who has attacked scavs or carries only a melee weapon
    /// (<c>AIData.ShallPursuit</c>; a PMC bot is such a player to a scav), run to the cover point nearest to him - and then HOLD there ("waitaxeman",
    /// <c>HoldFor(3)</c> over and over) for as long as he is within 200 m. Raid 2026-10-30 15:18: Петя Копирайтер, Влад Седло and Моня Газобетон ran to one
    /// wall (579.6, -64.5) from three sides and stood pressed against it for 80 s up to the end of the log, Рауль Родненький 70 s on his own - none of
    /// the idle watchers saw it because "Pursuit" is not one of the layers that do nothing, and QuestingBots (the "go on" nudge) ranks below it.
    /// Here: a bot in Pursuit that has not moved 3 m for 15 s, with no enemy in sight, not treating a wound and not just hit, drops the pursuit for two
    /// minutes (<see cref="Suppressed"/> makes the layer say "not now"), so the bot goes back to its quest and its loot. A pursuit that keeps moving is left alone.
    /// 0.9.7: an enemy in sight (or a hit) was taken for "the bot is fighting" - but the Pursuit layer only aims and holds, it never shoots, and SAIN's fight
    /// sits below it. Raid 2026-10-30 23:28: Фаддей Мукунзор stood 337 s at (595, 74) aiming at a guard 13 m off (VPO-209 10/10, "aims ... without a shot"
    /// every 24 s) while he bled from 72 % to 2 %. Now a bot standing 4 s in Pursuit with an enemy in sight, or hit, drops the pursuit for a minute and SAIN fights.
    /// </summary>
    public static class PursuitGuard
    {
        private const float StandSeconds = 15f;
        private const float DropSeconds = 120f;
        private const float SightSeconds = 4f;
        private const float SightDropSeconds = 60f;

        private class State
        {
            public Vector3 At;
            public float Since;
            public float DroppedUntil;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static float _next;

        public static int Dropped { get; private set; }
        public static int DroppedInSight { get; private set; }

        public static string Summary => $"pursuits dropped after standing {Dropped} (with an enemy in sight or hit, for SAIN to fight {DroppedInSight})";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Dropped = DroppedInSight = 0;
        }

        public static bool Suppressed(BotOwner bot) => bot != null && _states.TryGetValue(bot.ProfileId, out State s) && Time.time < s.DroppedUntil;

        public static void Tick()
        {
            if (!Settings.NeverIdle.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
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

                Diag.Guard("PursuitGuard", () => Check(bot, p, now));
            }
        }

        private static void Check(BotOwner bot, BotProfile p, float now)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { At = bot.Position, Since = now };
            }

            Vector3 pos = bot.Position;
            if ((pos - s.At).sqrMagnitude > 3f * 3f || DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) != "Pursuit")
            {
                s.At = pos;
                s.Since = now;
                return;
            }

            bool seen = SainBot.Of(bot)?.GoalEnemy?.IsVisible == true;
            bool hit = HitReaction.SinceHit(bot) < 8f;
            if (seen || hit)
            {
                // The layer aims and holds but never shoots: it gives way to SAIN's fight
                if (now - s.Since < SightSeconds)
                {
                    return;
                }

                DroppedInSight++;
                s.DroppedUntil = now + SightDropSeconds;
                s.Since = now;
                Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {SightSeconds:0}+ s at ({pos.x:0}, {pos.z:0}) in EFT's Pursuit layer " +
                         (seen ? $"with {SainBot.Of(bot)?.GoalEnemy?.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} in sight" : "after a hit") + $" and no shot - drops the pursuit for {SightDropSeconds:0} s, SAIN fights");
                return;
            }

            if (bot.Medecine?.Using == true)
            {
                s.Since = now;
                return;
            }

            if (now - s.Since < StandSeconds)
            {
                return;
            }

            Dropped++;
            s.DroppedUntil = now + DropSeconds;
            s.Since = now;
            Log.Info($"LR Idle: {p.Name} ({p.Kind}) stood {StandSeconds:0}+ s at ({pos.x:0}, {pos.z:0}) in EFT's Pursuit layer, waiting for the player it hunts - drops the pursuit for {DropSeconds:0} s");
        }
    }

    /// <summary>EFT's Pursuit layer says "not now" for a bot that stood waiting in it (PursuitGuard).</summary>
    public class PursuitLayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PursuitLayer), nameof(PursuitLayer.ShallUseNow));

        [PatchPostfix]
        protected static void PatchPostfix(ref bool __result, BotOwner ____owner)
        {
            try
            {
                if (__result && PursuitGuard.Suppressed(____owner))
                {
                    __result = false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("PursuitLayerPatch", e);
            }
        }
    }
}
