using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    public struct ShotEvent
    {
        public Vector3 Position;
        public float Time;
        public string ShooterId;
        public float Range;
    }

    public class KillEvent
    {
        public string VictimId;
        public string VictimName;
        public string KillerId;
        public bool VictimHuman;
        // A PMC (BEAR/USEC), not a scav: what a player sees at a glance from the kit
        public bool VictimPmc;

        // The victim's role (a boss is "boss...": BossHunt) and what LiveRaid made of him (an AI scav, a player scav... - PlayerScavLife)
        public WildSpawnType VictimRole;
        public Bots.BotKind VictimKind = Bots.BotKind.Other;
        public Vector3 Position;
        public float Time;

        // The body's gear (its id is the one LootingBots keeps in its "already looted" lists) and what it is worth at a glance
        public EFT.InventoryLogic.InventoryEquipment Equipment;
        public string EquipmentId;
        public float Value;
    }

    /// <summary>
    /// What happened recently on the map: gunshots and kills. Bots use it to judge how dangerous a spot is,
    /// the way a player listens for shots before stepping onto a body.
    /// </summary>
    public static class WorldEvents
    {
        private const float ShotMemorySeconds = 90f;
        private static readonly List<ShotEvent> _shots = new List<ShotEvent>();
        private static readonly Dictionary<string, KillEvent> _kills = new Dictionary<string, KillEvent>();

        public static void Begin()
        {
            _shots.Clear();
            _kills.Clear();
            Player.OnPlayerDeadStatic -= OnPlayerDead;
            Player.OnPlayerDeadStatic += OnPlayerDead;
        }

        public static void End()
        {
            Player.OnPlayerDeadStatic -= OnPlayerDead;
            _shots.Clear();
            _kills.Clear();
        }

        public static void RecordShot(Player shooter)
        {
            if (shooter == null)
            {
                return;
            }

            float audible = AudibleRange(shooter);
            Diag.Guard("shot: player sense", () => PlayerSense.OnShot(shooter));

            float now = Time.time;
            if (_shots.Count > 0 && now - _shots[0].Time > ShotMemorySeconds)
            {
                _shots.RemoveAll(s => now - s.Time > ShotMemorySeconds);
            }

            // Full-auto bursts would flood the list; one entry per shooter per half second is enough
            for (int i = _shots.Count - 1; i >= 0 && now - _shots[i].Time < 0.35f; i--)
            {
                if (_shots[i].ShooterId == shooter.ProfileId)
                {
                    return;
                }
            }

            _shots.Add(new ShotEvent { Position = shooter.Position, Time = now, ShooterId = shooter.ProfileId, Range = audible });
            Raid.PopulationBalance.OnShot();
            GunfireResponse.OnShot(shooter.Position, now, shooter.ProfileId, audible, !shooter.IsAI);
            Raid.EncounterLog.OnShot(shooter.Position, shooter.ProfileId);
        }

        /// <summary>How far this weapon is heard, from SAIN's weapon model (suppressors and ammo included).</summary>
        private static float AudibleRange(Player shooter)
        {
            try
            {
                if (SAIN.Components.GameWorldComponent.TryGetPlayerComponent(shooter, out var component))
                {
                    float range = component.Equipment?.CurrentWeaponInfo?.CalculatedAudibleRange ?? 0f;
                    if (range > 1f)
                    {
                        return range;
                    }
                }
            }
            catch
            {
            }

            return 130f;
        }

        /// <summary>Shots fired within radius of a point during the last seconds, ignoring the given shooters.</summary>
        public static float ShotPressure(Vector3 point, float radius, float seconds, ICollection<string> ignoreShooters)
        {
            float now = Time.time;
            float sq = radius * radius;
            float pressure = 0;
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                ShotEvent shot = _shots[i];
                float age = now - shot.Time;
                if (age > seconds)
                {
                    break;
                }

                if ((shot.Position - point).sqrMagnitude > sq || (ignoreShooters != null && ignoreShooters.Contains(shot.ShooterId)))
                {
                    continue;
                }

                // Fresh shots weigh more than old ones
                pressure += 1f - age / seconds;
            }

            return pressure;
        }

        /// <summary>The shooter fired within the last <paramref name="seconds"/>.</summary>
        public static bool ShotBy(string shooterId, float seconds)
        {
            float now = Time.time;
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (now - _shots[i].Time > seconds)
                {
                    break;
                }

                if (_shots[i].ShooterId == shooterId)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetKill(string victimId, out KillEvent kill) => _kills.TryGetValue(victimId, out kill);

        /// <summary>Bodies of this raid (the dead, newest last).</summary>
        public static IEnumerable<KillEvent> Kills => _kills.Values;

        /// <summary>The nearest body within <paramref name="radius"/> worth at least <paramref name="minValue"/> at a glance that <paramref name="searched"/> does not know.</summary>
        public static KillEvent RichBodyNear(Vector3 pos, float radius, float minValue, System.Func<string, bool> searched)
        {
            KillEvent best = null;
            float bestSq = radius * radius;
            foreach (KillEvent k in _kills.Values)
            {
                float sq = (k.Position - pos).sqrMagnitude;
                if (sq > bestSq || k.Equipment == null || string.IsNullOrEmpty(k.EquipmentId) || searched(k.EquipmentId))
                {
                    continue;
                }

                k.Value = LootBrain.VisibleValue(k.Equipment, "body" + k.VictimId);
                if (k.Value >= minValue)
                {
                    best = k;
                    bestSq = sq;
                }
            }

            return best;
        }

        private static void OnPlayerDead(Player victim, IPlayer killer, DamageInfo damageInfo, EBodyPart part)
        {
            if (victim == null)
            {
                return;
            }

            KillEvent kill = new KillEvent
            {
                VictimId = victim.ProfileId,
                VictimName = victim.Profile?.Nickname,
                KillerId = killer?.ProfileId,
                VictimHuman = Net.IsHuman(victim),
                VictimPmc = victim.Profile?.Info != null && victim.Profile.Info.Side != EPlayerSide.Savage,
                Position = victim.Position,
                Time = Time.time,
                Equipment = victim.Equipment,
                EquipmentId = victim.Equipment?.Id,
                VictimRole = victim.Profile?.Info?.Settings?.Role ?? WildSpawnType.marksman,
                VictimKind = Bots.BotProfiles.TryGet(victim.ProfileId, out Bots.BotProfile vp) ? vp.Kind : Bots.BotKind.Other,
            };
            _kills[victim.ProfileId] = kill;
            Diag.Guard("OnKill: loot brain", () => LootBrain.OnKill(kill, killer as Player));
            Diag.Guard("OnKill: encounters", () => Raid.EncounterLog.OnKill(kill, killer as Player));
            Diag.Guard("OnKill: squad", () => Maneuvers.OnKilled(victim, killer));
            Diag.Guard("OnKill: recover the loot", () => MateRecovery.OnKilled(victim));
            Diag.Guard("OnKill: loot jobs", () => LootJobs.OnKill(kill, killer as Player));
            Diag.Guard("OnKill: player scavs", () => Bots.PlayerScavLife.OnKill(kill, killer as Player));
            Diag.Guard("OnKill: boss hunts", () => Bots.BossHunt.OnKill(kill));
            Diag.Guard("OnKill: squad comms", () => SquadComms.OnKilled(victim, killer));
            Diag.Guard("OnKill: moods", () => RaidMood.OnKill(killer as Player));
            Log.Detail("kill", () => $"{killer?.Profile?.Nickname ?? "?"} killed {victim.Profile?.Nickname} ({part}) at {victim.Position}");
        }
    }

    /// <summary>Same hook SAIN uses to hear gunshots.</summary>
    public class ShotHeardPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot));

        [PatchPrefix]
        protected static void PatchPrefix(Player __instance)
        {
            try
            {
                RunPrefix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("ShotHeardPatch", e);
            }
        }

        private static void RunPrefix(Player __instance)
        {
            WorldEvents.RecordShot(__instance);
        }
    }
}
