using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using LiveRaid.Client.Bots;
using QuestingBots.Components;
using QuestingBots.Helpers;
using QuestingBots.Models.Questing;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Night discipline (F12 -> 9). At night a light gives a player away from far off. Not everyone minds it - each PMC and player scav has its own
    /// habit, drawn once from its squad's doctrine (precision, cohesion), its skill and style:
    ///  - disciplined (a clan, rats, marksmen, the skilled ones most; randoms and novices seldom): keeps its light off while someone is about but the
    ///    fight is not on it - an enemy known anywhere, a noise in the last 30 s, shooting within 100 m in the last minute, a fight at range, an ambush
    ///    it waits in - and keeps out of the open (CoverRoute, careful +0.3);
    ///  - a blinder: once it is close contact - an enemy within 70 m and the shooting has started (it or the enemy fired, it was hit) or the enemy
    ///    is in sight within 25 m - switches every light on to blind him, and keeps it on 6 s after. Most of the disciplined ones do it, the bold
    ///    others now and then; one that waits in ambush stays dark until the first shot;
    ///  - the rest do as SAIN and EFT do (SAIN puts the light on for an enemy in sight within ~36 m).
    /// Twice a second the habit is applied, and SAIN's own light switching in a fight and a search follows it (SainLightPatch).
    /// </summary>
    public static class NightDiscipline
    {
        private class Habit
        {
            public bool Disciplined;
            public bool Blinds;
            public bool? Want;
            public float ContactUntil;
            public bool? Logged;
        }

        public const float CloseRange = 70f;

        private static readonly Dictionary<string, Habit> _habits = new Dictionary<string, Habit>();
        private static float _next;
        private static float _nightCheckedAt = -100f;
        private static bool _night;

        public static int LightsOff { get; private set; }
        public static int LightsOn { get; private set; }

        public static string Summary
        {
            get
            {
                int d = _habits.Values.Count(h => h.Disciplined), b = _habits.Values.Count(h => h.Blinds);
                return $"night discipline: {_habits.Count} bots (disciplined {d}, blind with their light up close {b}); lights turned off {LightsOff}, lights turned on to blind {LightsOn}";
            }
        }

        public static void Clear()
        {
            _habits.Clear();
            _next = 0f;
            _nightCheckedAt = -100f;
            _night = false;
            LightsOff = LightsOn = 0;
        }

        /// <summary>The raid's hour is night (QuestingBots' check of EFT's day hours), looked at once a minute.</summary>
        public static bool IsNight()
        {
            if (!Settings.NightDiscipline.Value)
            {
                return false;
            }

            if (Time.time - _nightCheckedAt > 60f)
            {
                _nightCheckedAt = Time.time;
                try
                {
                    _night = !RaidHelpers.IsDayInGame();
                }
                catch
                {
                    _night = false;
                }
            }

            return _night;
        }

        private static Habit HabitOf(BotOwner bot)
        {
            if (bot?.ProfileId == null)
            {
                return null;
            }

            if (_habits.TryGetValue(bot.ProfileId, out Habit h))
            {
                return h;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
            {
                return null;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(bot);
            float precision = d != null ? d[DoctrineDeck.Precision] : 0.5f;
            float cohesion = d != null ? d[DoctrineDeck.Cohesion] : 0.4f;
            float aggression = d != null ? d[DoctrineDeck.Aggression] : p.Aggression;
            double disc = 0.2 + 0.35 * precision + 0.15 * cohesion + (p.Skill == SkillTier.Novice ? -0.15 : p.Skill == SkillTier.Experienced ? 0.1 : p.Skill == SkillTier.Pro ? 0.2 : 0.0);
            switch (d?.Squad.Doctrine?.Kind)
            {
                case DoctrineKind.Clan:
                case DoctrineKind.Rats:
                case DoctrineKind.Marksmen:
                    disc += 0.15;
                    break;
                case DoctrineKind.Randoms:
                    disc -= 0.15;
                    break;
                case DoctrineKind.Chads:
                    disc -= 0.1;
                    break;
            }

            if (p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper || p.Style == PlayStyle.Sniper)
            {
                disc += 0.1;
            }
            else if (p.Style == PlayStyle.Rusher)
            {
                disc -= 0.1;
            }

            System.Random rng = new System.Random(bot.ProfileId.GetHashCode() ^ 0x11647);
            bool disciplined = rng.NextDouble() < Mathf.Clamp((float)disc, 0.05f, 0.9f);
            h = new Habit
            {
                Disciplined = disciplined,
                Blinds = rng.NextDouble() < (disciplined ? 0.55 + 0.35 * aggression : 0.1 + 0.35 * aggression),
            };
            _habits[bot.ProfileId] = h;
            return h;
        }

        /// <summary>Night and a disciplined bot: keeps out of the open (CoverRoute).</summary>
        public static bool KeepsDark(BotOwner bot) => Settings.Enabled.Value && IsNight() && HabitOf(bot)?.Disciplined == true;

        /// <summary>What the bot wants of its light now (on / off), or null when it leaves it to SAIN and EFT. Set by Tick, read by SainLightPatch.</summary>
        public static bool? Want(BotOwner bot) =>
            Settings.Enabled.Value && IsNight() && bot?.ProfileId != null && _habits.TryGetValue(bot.ProfileId, out Habit h) ? h.Want : null;

        private static bool? Decide(BotOwner bot, Habit h, float now)
        {
            SAIN.SAINComponent.Classes.EnemyClasses.Enemy e = SainBot.Of(bot)?.GoalEnemy;
            bool close = e != null && e.RealDistance <= CloseRange;
            bool shooting = e != null && (e.Status.ShotAtMeRecently || bot.ShootData?.Shooting == true || HitReaction.SinceHit(bot) < 5f);
            if (close && (shooting || (e.IsVisible && e.RealDistance <= 25f)))
            {
                h.ContactUntil = now + 6f;
            }

            if (now < h.ContactUntil)
            {
                // Close contact: a blinder lights him up, the others leave it to SAIN
                return h.Blinds ? true : (bool?)null;
            }

            if (!h.Disciplined)
            {
                return null;
            }

            bool someone = e != null || HitReaction.InRealFight(bot, 20f) || NoiseResponse.LastHeard(bot, 30f, out _)
                           || WorldEvents.ShotPressure(bot.Position, 100f, 60f, new HashSet<string> { bot.ProfileId }) > 0f;
            return someone ? false : (bool?)null;
        }

        public static void Tick()
        {
            if (!Settings.Enabled.Value || Time.time < _next || !IsNight())
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                Habit h = bot != null && !bot.IsDead && bot.BotLight != null && !Spectator.TakeOver.IsTaken(bot) ? HabitOf(bot) : null;
                if (h == null)
                {
                    continue;
                }

                h.Want = Decide(bot, h, now);
                if (h.Want == null || bot.BotLight.IsEnable == h.Want.Value)
                {
                    continue;
                }

                try
                {
                    SainBot.Of(bot)?.BotLight?.ToggleLight(h.Want.Value);
                    if (h.Want.Value)
                    {
                        bot.BotLight.TurnOn(true);
                    }
                    else
                    {
                        bot.BotLight.TurnOff(false, true);
                    }
                }
                catch
                {
                    // EFT refused (hands busy): next tick
                    continue;
                }

                if (h.Want.Value)
                {
                    LightsOn++;
                }
                else
                {
                    LightsOff++;
                }

                if (h.Logged != h.Want)
                {
                    h.Logged = h.Want;
                    string name = bot.Profile?.Nickname;
                    if (h.Want.Value)
                    {
                        Log.Detail("combat", () => $"{name} switches its light on up close - to blind the enemy");
                    }
                    else
                    {
                        Log.Detail("task", () => $"{name} turns its light off - someone is about at night");
                    }
                }
            }
        }
    }

    /// <summary>SAIN switches a bot's light in its own fight and search; at night the bot's habit (NightDiscipline) decides when it has one.</summary>
    public class SainLightPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController), nameof(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController.HandleLightForEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController __instance) => SainLightSearchPatch.Follow(__instance);
    }

    public class SainLightSearchPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController), nameof(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController.HandleLightForSearch));

        [PatchPrefix]
        protected static bool PatchPrefix(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController __instance) => Follow(__instance);

        internal static bool Follow(SAIN.SAINComponent.Classes.WeaponFunction.BotLightController light)
        {
            try
            {
                bool? want = NightDiscipline.Want(light.BotOwner);
                if (want == null)
                {
                    return true;
                }

                light.ToggleLight(want.Value);
                return false;
            }
            catch (System.Exception e)
            {
                Diag.Report("SainLightPatch", e);
                return true;
            }
        }
    }

    /// <summary>
    /// The airdrop as an event (F12 -> 3 "Airdrop fights"). QuestingBots makes a quest of a landed airdrop and every bot within 250 m may take it; with
    /// LiveRaid's weights, squads from all over drifted there. Online a drop draws the squads that are near, can make it and want it - and they fight
    /// over it. Here a squad (its leader decides once per drop) goes only if it is within 220 m of the drop, the raid has 6+ minutes left, the leader
    /// has 60 %+ health, and its character and doctrine want it (storm troopers and greedy ones most, the quiet ones seldom) - at most three squads per drop.
    /// The squads that go want it a lot (x3), the rest leave it (x0.02).
    /// </summary>
    public static class AirdropEvent
    {
        private class Drop
        {
            public Vector3 At;
            public float Landed;
            public readonly Dictionary<int, bool> Decided = new Dictionary<int, bool>();
            public int Going;

            // The crate itself (LootingBots hands it over when the drop lands) and the bots that were told to go through it
            public LootableContainer Crate;
            public readonly HashSet<string> Forced = new HashSet<string>();
        }

        private static readonly List<LootableContainer> _pendingCrates = new List<LootableContainer>();
        public static int Crates { get; private set; }
        public static int ForcedLoots { get; private set; }

        private static readonly List<Drop> _drops = new List<Drop>();
        private static readonly System.Random _rng = new System.Random();

        public static int Squads { get; private set; }

        public static string Summary => _drops.Count == 0 ? "airdrops: none landed" : $"airdrops: {_drops.Count} landed, squads that went for them {Squads}, bots sent through the crate {ForcedLoots}";

        public static void Clear()
        {
            _drops.Clear();
            _pendingCrates.Clear();
            Squads = 0;
            Crates = ForcedLoots = 0;
        }

        /// <summary>LootingBots tells us the crate once the drop has landed (its own priority queue gets it too).</summary>
        public static void CrateLanded(LootableContainer crate)
        {
            if (crate != null)
            {
                _pendingCrates.Add(crate);
                Crates++;
            }
        }

        /// <summary>
        /// A bot that has come to the crate goes through it (1.4.6). QuestingBots' "Airdrop Chaser" is only a walk to a point beside the crate, and the step is over on
        /// arrival; LootingBots queues the crate for every bot but takes it only on a scan that finds the bot free, in range, in sight and not hurried by its quest -
        /// the bots came, stood a moment and went on. Here a free bot within 20 m of a crate it has not been sent to is handed the crate as its loot (the same call
        /// LootingBots makes itself, with its own bookkeeping), once.
        /// </summary>
        private static void SendToCrate(GameWorld world, Drop drop)
        {
            LootableContainer crate = drop.Crate;
            if (crate == null || !crate.isActiveAndEnabled || crate.DoorState == EDoorState.Locked || Time.time - drop.Landed > 1500f)
            {
                return;
            }

            Vector3 at = crate.TrackableTransform != null ? crate.TrackableTransform.position : crate.transform.position;
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || drop.Forced.Contains(bot.ProfileId) || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                Vector3 d = bot.Position - at;
                if (d.y > 4f || d.y < -4f || new Vector2(d.x, d.z).sqrMagnitude > 45f * 45f || !TacticalTasks.NoThreat(bot) || HitReaction.InRealFight(bot, 15f)
                    || bot.Medecine?.Using == true)
                {
                    continue;
                }

                LootingBots.Components.LootingBrain brain = player.GetComponent<LootingBots.Components.LootingBrain>();
                // 1.4.9: only a loot job that is really running holds it off (HasActiveLootable stays set on a bot that left its target to ours), and 45 m, not 20:
                // raid 2026-11-11 16:06 - the drop landed at 19:23, Жека Киберспортсмен came at 20:30 to the point 20 m from it and went on; the first one to loot it was at 25:22
                if (brain == null || brain.LootTaskRunning)
                {
                    continue;
                }

                try
                {
                    if (LootingBots.External.CheckIfInventoryFull(bot))
                    {
                        drop.Forced.Add(bot.ProfileId);
                        continue;
                    }

                    string rootId = LootingBots.Utilities.LootUtils.GetRootItemId(crate);
                    Vector3 dest = NavMesh.SamplePosition(at, out NavMeshHit hit, 3f, NavMesh.AllAreas) ? hit.position : bot.Position;
                    if (!LootingBots.Utilities.ActiveLootCache.CacheActiveLootId(rootId, bot))
                    {
                        continue;
                    }

                    drop.Forced.Add(bot.ProfileId);
                    brain.SetLoot(crate, LootingBots.Components.LootFinder.LootType.Container, at, dest, rootId, Vector3.Distance(bot.Position, at));
                    ForcedLoots++;
                    Log.Info($"LR Airdrop: {p.Name} ({p.Kind}) is {Vector3.Distance(bot.Position, at):0} m from the crate and goes through it");
                }
                catch (System.Exception e)
                {
                    drop.Forced.Add(bot.ProfileId);
                    Diag.Report("AirdropEvent.SendToCrate", e);
                }
            }
        }

        /// <summary>
        /// The squads near a drop that has just landed decide at once (1.4.4). Before, a squad decided only when its bot happened to choose a new quest: of the
        /// 3 drops of the Reserve raids of 2026-11-10 none was looked at for 1-4 minutes and one not at all - online, the plane and the smoke are seen and heard
        /// by everyone near, and who wants it goes at once. A squad that goes is given a "go and look" task to the drop (the quest weight, x3, follows as before).
        /// </summary>
        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.AirdropEvent.Value || (_drops.Count == 0 && _pendingCrates.Count == 0) || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Drop drop in _drops)
            {
                if (drop.Crate == null)
                {
                    LootableContainer near = _pendingCrates.FirstOrDefault(cr => cr != null && (cr.transform.position - drop.At).sqrMagnitude < 60f * 60f);
                    if (near != null)
                    {
                        drop.Crate = near;
                        _pendingCrates.Remove(near);
                    }
                }

                SendToCrate(world, drop);
                if (Time.time - drop.Landed > 150f)
                {
                    continue;
                }

                foreach (Player player in world.AllAlivePlayersList.ToArray())
                {
                    BotOwner bot = player?.AIData?.BotOwner;
                    if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || TacticalTasks.SquadLeaderOf(bot) != null || Spectator.TakeOver.IsTaken(bot))
                    {
                        continue;
                    }

                    BotProfile p = BotProfiles.Get(bot);
                    int squad = SquadTies.SquadOf(bot.ProfileId);
                    if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || drop.Decided.ContainsKey(squad)
                        || Vector3.Distance(bot.Position, drop.At) > 250f || HitReaction.InRealFight(bot, 15f))
                    {
                        continue;
                    }

                    bool goes = Decide(bot, drop, out string why);
                    drop.Decided[squad] = goes;
                    if (goes)
                    {
                        drop.Going++;
                        Squads++;
                    }

                    float dist = Vector3.Distance(bot.Position, drop.At);
                    string sent = "";
                    if (goes)
                    {
                        Vector3? stand = ScavPatrol.Short(bot, drop.At, 9f);
                        TacticalTask task = new TacticalTask
                        {
                            Kind = TaskKind.Investigate, StandAt = stand ?? drop.At, LookAt = drop.At, Speed = 1f, Sprint = true, SprintBeyond = 60f, MovePose = 1f,
                            Until = Time.time + dist / 3f + 40f,
                        };
                        sent = TacticalTasks.Set(bot, task) ? "; sent there at once" : "; its task could not be set, the quest weight alone draws it";
                    }

                    Log.Info($"LR Airdrop: {p.Name}'s {(SquadTies.MatesOf(bot.ProfileId).Count() > 1 ? "squad" : "solo run")} {(goes ? "goes for" : "leaves")} the drop " +
                             $"{dist:0} m away - {why}{sent}");
                }
            }
        }

        private static float _next;

        public static void Landed(Vector3 at)
        {
            _drops.Add(new Drop { At = at, Landed = Time.time });
            Log.Info($"LR Airdrop: a drop has landed at ({at.x:0}, {at.z:0})");
        }

        /// <summary>QuestingBots' wish for the airdrop quest, for this bot.</summary>
        public static float Multiplier(BotOwner bot, BotQuest quest)
        {
            if (!Settings.Enabled.Value || !Settings.AirdropEvent.Value || bot == null || _drops.Count == 0)
            {
                return 1f;
            }

            Vector3? at = null;
            foreach (BotQuestObjective o in quest.GetValidObjectives())
            {
                at = o.GetFirstStepPosition();
                if (at != null)
                {
                    break;
                }
            }

            if (at == null)
            {
                return 1f;
            }

            Drop drop = null;
            foreach (Drop d in _drops)
            {
                if ((d.At - at.Value).sqrMagnitude < 60f * 60f)
                {
                    drop = d;
                }
            }

            if (drop == null)
            {
                return 1f;
            }

            BotOwner leader = TacticalTasks.SquadLeaderOf(bot) ?? bot;
            int squad = SquadTies.SquadOf(leader.ProfileId);
            if (!drop.Decided.TryGetValue(squad, out bool goes))
            {
                goes = Decide(leader, drop, out string why);
                drop.Decided[squad] = goes;
                if (goes)
                {
                    drop.Going++;
                    Squads++;
                }

                BotProfile lp = BotProfiles.Get(leader);
                Log.Info($"LR Airdrop: {lp?.Name ?? leader.Profile?.Nickname}'s {(SquadTies.MatesOf(leader.ProfileId).Count() > 1 ? "squad" : "solo run")} " +
                         $"{(goes ? "goes for" : "leaves")} the drop {Vector3.Distance(leader.Position, drop.At):0} m away - {why}");
            }

            return goes ? 3f : 0.02f;
        }

        private static bool Decide(BotOwner leader, Drop drop, out string why)
        {
            BotProfile p = BotProfiles.Get(leader);
            float dist = Vector3.Distance(leader.Position, drop.At);
            float left = RaidHelpers.GetRemainingRaidTimeSeconds();
            float hp = leader.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f;
            if (p == null || p.Kind == BotKind.Other || p.Kind == BotKind.Scav)
            {
                why = "not a raider";
                return false;
            }

            if (drop.Going >= 3)
            {
                why = "three squads already on their way";
                return false;
            }

            if (dist > 220f)
            {
                why = "too far to make it first";
                return false;
            }

            if (left < 360f)
            {
                why = $"only {left / 60f:0} min of the raid left";
                return false;
            }

            if (hp < 0.6f)
            {
                why = $"hurt ({hp:P0})";
                return false;
            }

            DoctrineDeck.Member d = SquadDoctrines.Of(leader);
            float want = d != null
                ? 0.15f + 0.45f * d[DoctrineDeck.ThirdParty] + 0.3f * d[DoctrineDeck.Greed] + 0.2f * d[DoctrineDeck.Aggression]
                : 0.2f + 0.3f * p.Aggression + 0.15f * Mathf.Clamp(p.LootGreed - 0.5f, 0f, 1f);
            want *= 1.2f - dist / 300f;
            want *= 1f - 0.5f * Mathf.Max(0f, PlayerSense.Stakes(leader));
            bool goes = _rng.NextDouble() < Mathf.Clamp(want, 0.05f, 0.9f);
            why = $"{dist:0} m, {left / 60f:0} min left, hp {hp:P0}, wants it {Mathf.Clamp(want, 0.05f, 0.9f):P0}";
            return goes;
        }
    }

    /// <summary>QuestingBots has just made a quest of a landed airdrop (AirdropEvent).</summary>
    public class QbAirdropLandedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuestBuilder), nameof(BotQuestBuilder.StartAddAirdropChaserQuest));

        [PatchPostfix]
        protected static void PatchPostfix(Vector3 airdropPosition)
        {
            try
            {
                AirdropEvent.Landed(airdropPosition);
            }
            catch (System.Exception e)
            {
                Diag.Report("QbAirdropLandedPatch", e);
            }
        }
    }
}
