using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Tactics;
using LootingBots.Components;
using QuestingBots.BotLogic.BotMonitor;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using QuestingBots.Controllers;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// The game's exfil layer sits above SAIN and gives way only to an enemy in EFT's own bot memory, which SAIN does not fill:
    /// a PMC on its way out (or waiting in the zone) let itself be shot in the back. Under fire or with an enemy close and seen it fights.
    /// </summary>
    public class ExfilUnderFirePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ExfiltrationLayer), nameof(ExfiltrationLayer.ShallUseNow));

        [PatchPostfix]
        protected static void PatchPostfix(BotOwner ____owner, ref bool __result)
        {
            try
            {
                if (__result && (HitReaction.InRealFight(____owner, 15f) || Bots.SwitchExtract.HasPlan(____owner) || ExfilFirst.Waits(____owner)))
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                Diag.Report("ExfilUnderFirePatch", e);
            }
        }
    }

    /// <summary>
    /// What a player settles before walking to the exit (F12 -> 9 "Patch up and fight before heading out"). EFT's exit layer (priority 79, above SAIN and
    /// LiveRaid's heal layer) took a bot out as it was: raid 2026-10-26 20:28, NastyInc walked to his exit bleeding at 17 % with an empty rifle and a
    /// blacked-out leg, past Жорес Панцер, who shot him in the back from 8-56 m - he never bandaged, never reloaded, never turned round.
    ///  - an enemy known to be close (in sight, seen in the last 15 s or heard in the last 10 s, within 60 m): the fight comes first;
    ///  - bleeding, or under 45 % hp, with something to treat it with (or a broken leg with a splint or painkiller): treatment first (LiveRaid's heal layer
    ///    takes it at the first quiet moment).
    /// The exit waits at most 45 s at a time for the treatment, then not again for 2 minutes (a treatment that cannot start must not keep the bot in the raid).
    /// </summary>
    public static class ExfilFirst
    {
        private static readonly Dictionary<string, float> _healSince = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _healBlockedUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _noteAt = new Dictionary<string, float>();

        public static int ForFights { get; private set; }
        public static int ForTreatment { get; private set; }

        public static string Summary => $"exits put off for a close enemy {ForFights} times, for a treatment {ForTreatment} times";

        public static void Clear()
        {
            _healSince.Clear();
            _healBlockedUntil.Clear();
            _noteAt.Clear();
            ForFights = ForTreatment = 0;
        }

        public static bool Waits(BotOwner bot)
        {
            if (!Settings.ExfilPatchUp.Value || bot == null || bot.IsDead)
            {
                return false;
            }

            Bots.BotProfile p = Bots.BotProfiles.Get(bot);
            if (p == null || p.Kind == Bots.BotKind.Other)
            {
                return false;
            }

            float now = Time.time;
            string id = bot.ProfileId;
            var sain = Bots.SainBot.Of(bot);
            if (sain != null)
            {
                foreach (SAIN.SAINComponent.Classes.EnemyClasses.Enemy e in sain.EnemyController.KnownEnemies)
                {
                    if (e != null && e.CheckValid() && e.RealDistance < 60f && (e.IsVisible || e.TimeSinceSeen < 15f || (e.Heard && e.TimeSinceHeard < 10f)))
                    {
                        Note(bot, p, now, $"{e.EnemyPlayer?.Profile?.Nickname ?? "an enemy"} is {e.RealDistance:0} m away - deals with him first");
                        ForFights++;
                        return true;
                    }
                }
            }

            BotMedecine med = bot.Medecine;
            float hp = bot.HealthController?.GetBodyPartHealth(EBodyPart.Common).Normalized ?? 1f;
            bool treat = med?.FirstAid != null && ((med.FirstAid.IsBleeding || hp < 0.45f) && med.FirstAid.HaveSmth2Use || Bots.LegCare.Plan(bot, out _, out _));
            if (!treat || (_healBlockedUntil.TryGetValue(id, out float blocked) && now < blocked))
            {
                _healSince.Remove(id);
                return false;
            }

            if (!_healSince.TryGetValue(id, out float since))
            {
                _healSince[id] = since = now;
            }

            if (now - since > 45f && !med.Using)
            {
                _healSince.Remove(id);
                _healBlockedUntil[id] = now + 120f;
                Log.Detail("extract", $"{p.Name} could not get its treatment done in 45 s - heads out as it is");
                return false;
            }

            Note(bot, p, now, $"patches itself up first (hp {hp:P0}{(med.FirstAid.IsBleeding ? ", bleeding" : "")}{(Bots.LegCare.Describe(bot.GetPlayer) is string legs ? ", " + legs : "")})");
            ForTreatment++;
            return true;
        }

        private static void Note(BotOwner bot, Bots.BotProfile p, float now, string what)
        {
            if (_noteAt.TryGetValue(bot.ProfileId, out float at) && now < at)
            {
                return;
            }

            _noteAt[bot.ProfileId] = now + 20f;
            Log.Detail("extract", $"{p.Name} on its way out: {what}");
        }
    }

    /// <summary>
    /// A bot that really leaves the raid. EFT's exfil node walks the bot to the nearest open PMC exit without requirements, keeps it
    /// standing on the spot for that exit's own timer (the player's one) and then removes it from the map with all it carries
    /// (BotLeaveData.RemoveFromMap). Logged here so that it can be checked, not only assumed.
    /// </summary>
    public class BotLeftRaidPatch : ModulePatch
    {
        public static int Left { get; private set; }

        public static void Clear() => Left = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLeaveData), nameof(BotLeaveData.RemoveFromMap));

        [PatchPrefix]
        protected static void PatchPrefix(BotOwner ____owner)
        {
            try
            {
                if (____owner == null || ____owner.IsDead || Bots.SwitchExtract.LeavingNow == ____owner)
                {
                    return;
                }

                AIExfiltrationPoint exit = ____owner.PatrollingData?.ExfiltrationData?.CachedExfiltrationPoint;
                float timer = exit?.ExfiltrationPoint != null ? exit.ExfiltrationTime : 0f;
                float leave = ____owner.Exfiltration?.LeaveTime ?? 0f;
                float stood = timer > 0f && leave < float.MaxValue ? Time.time - (leave - timer) : -1f;
                string name = exit?.ExfiltrationPoint?.Settings?.Name;
                float hp = ____owner.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
                if (stood < 0f)
                {
                    // EFT's other way out (BotLeaveData: walk to an unspawn point and vanish) - not an exit with a timer
                    Log.Info($"LR Extract: {____owner.Profile.Nickname} vanished through EFT's own 'leave the map' (no exit, no timer), hp {hp:P0}");
                    return;
                }

                Left++;
                Log.Info($"LR Extract: {____owner.Profile.Nickname} left the raid through {(name != null ? name.Localized() : "an exit")}" +
                         $" after standing {stood:0} s on the exit's {timer:0} s timer, hp {hp:P0}");
            }
            catch (Exception e)
            {
                Diag.Report("BotLeftRaidPatch", e);
            }
        }
    }

    /// <summary>
    /// QuestingBots stops questing for "Fight" and "Wait for the group" by loose signs (any enemy set in EFT's memory, a hit or a sound
    /// within 20-45 s, any group member "in combat"), and SAIN keeps refreshing them - so bots spent half the raid in QuestingBots'
    /// idle recovery layer (scavs 50-80 %, raid 2026-10-19 05:38), standing in a corner. When there is no real fight for this bot
    /// (<see cref="HitReaction.InRealFight"/>) and none in its squad, it goes on with its objective.
    /// </summary>
    public class QbDecisionPatch : ModulePatch
    {
        private static readonly MethodInfo BotOwnerGetter = AccessTools.PropertyGetter(typeof(AbstractBotMonitor), "BotOwner");

        public static int Overruled { get; private set; }

        public static void Clear() => Overruled = 0;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotQuestingDecisionMonitor), nameof(BotQuestingDecisionMonitor.UpdateIfQuesting));

        [PatchPostfix]
        protected static void PatchPostfix(BotQuestingDecisionMonitor __instance)
        {
            try
            {
                EBotQuestingDecision decision = __instance.CurrentDecision;

                // A bot that has stood idle too long goes on, whatever QuestingBots decided (IdleBreaker)
                EBotQuestingDecision? goOn = Bots.IdleBreaker.Override(BotOwnerGetter?.Invoke(__instance, null) as BotOwner, __instance);
                if (goOn.HasValue)
                {
                    __instance.ForceDecision(goOn.Value);
                    return;
                }
                if (decision == EBotQuestingDecision.HelpBoss || decision == EBotQuestingDecision.FollowBoss || decision == EBotQuestingDecision.Regroup)
                {
                    // Running to a squadmate under attack (SquadAssist): QuestingBots' follow / regroup layers (19, 26) must not pull it back to the leader
                    BotOwner helper = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                    if (helper != null && TacticalTasks.Has(helper, TaskKind.Assist))
                    {
                        __instance.ForceDecision(EBotQuestingDecision.Hunt);
                        return;
                    }
                }

                // After a fight the squad strips the place before it moves on: the leader waits while squadmates go through bodies (LootJobs)
                if (decision == EBotQuestingDecision.Quest || decision == EBotQuestingDecision.WaitForAssignment)
                {
                    BotOwner leader = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                    if (leader != null && TacticalTasks.NoThreat(leader) && LootJobs.LeaderShouldWait(leader))
                    {
                        __instance.ForceDecision(EBotQuestingDecision.WaitForGroup);
                        return;
                    }
                }

                if (decision == EBotQuestingDecision.HelpBoss)
                {
                    // A follower out flanking is far from its leader on purpose: QuestingBots' regroup layer (26) must not pull it back
                    BotOwner flanker = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                    if (flanker != null && Maneuvers.IsFlanking(flanker))
                    {
                        __instance.ForceDecision(EBotQuestingDecision.Hunt);
                    }

                    return;
                }

                if (decision == EBotQuestingDecision.CheckForLoot)
                {
                    BotOwner looter = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                    EBotQuestingDecision? instead = LootKick.OnQbWantsLoot(looter, __instance);
                    if (instead.HasValue)
                    {
                        __instance.ForceDecision(instead.Value);
                    }

                    return;
                }

                if (decision != EBotQuestingDecision.Fight && decision != EBotQuestingDecision.WaitForGroup && decision != EBotQuestingDecision.StopToHeal)
                {
                    return;
                }

                BotOwner bot = BotOwnerGetter?.Invoke(__instance, null) as BotOwner;
                if (bot == null || Bots.BotProfiles.Get(bot)?.Kind == Bots.BotKind.Other || !ShouldGoOn(bot, decision))
                {
                    return;
                }

                // A leader waiting for its squad to finish with the bodies keeps waiting
                if (decision == EBotQuestingDecision.WaitForGroup && LootJobs.LeaderShouldWait(bot))
                {
                    return;
                }

                bool hasJob = bot.GetObjectiveManager()?.IsJobAssignmentActive == true;
                __instance.ForceDecision(hasJob ? EBotQuestingDecision.Quest : EBotQuestingDecision.WaitForAssignment);
                Overruled++;
            }
            catch (Exception e)
            {
                Diag.Report("QbDecisionPatch", e);
            }
        }

        private class HealWatch
        {
            public float Health;
            public float GainAt;
            public float GoOnUntil;
        }

        private static readonly Dictionary<string, HealWatch> _heal = new Dictionary<string, HealWatch>();

        public static int HealGiveUps { get; private set; }

        public static void ClearHeal()
        {
            _heal.Clear();
            HealGiveUps = 0;
        }

        /// <summary>
        /// QuestingBots parks the bot (its recovery layer: standing still) while it thinks the bot fights, waits for its group or heals.
        /// Raid 2026-10-20 13:09: 22 % of bot time, scavs stood for minutes "in a fight" because a group mate 190 m away was in one,
        /// and a PMC stood 4 minutes "healing" at 73 % when nothing healed any more. Standing still makes sense only while SAIN is
        /// actually fighting (it had a decision in the last 1.5 s - a short gap between its moves), right after a hit, next to a
        /// squadmate who is in a fight, or while the healing goes on.
        /// </summary>
        private static bool ShouldGoOn(BotOwner bot, EBotQuestingDecision decision)
        {
            if (HitReaction.SinceHit(bot) < 5f)
            {
                return false;
            }

            if (decision == EBotQuestingDecision.StopToHeal)
            {
                return HealingStalled(bot);
            }

            if (Bots.BotMind.SinceSainActive(bot) < 1.5f)
            {
                return false;
            }

            return decision != EBotQuestingDecision.WaitForGroup || !SquadAssist.SquadInRealFight(bot, 45f);
        }

        /// <summary>Healing that does not move health for 15 s (and no medicine in hand) has stalled: go on for a minute.</summary>
        private static bool HealingStalled(BotOwner bot)
        {
            float now = Time.time;
            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            if (!_heal.TryGetValue(bot.ProfileId, out HealWatch w))
            {
                _heal[bot.ProfileId] = w = new HealWatch { Health = hp, GainAt = now };
            }

            if (now < w.GoOnUntil)
            {
                return true;
            }

            if (hp > w.Health + 0.005f || bot.Medecine?.Using == true || HitReaction.InRealFight(bot, 10f))
            {
                w.GainAt = now;
            }

            w.Health = hp;
            if (now - w.GainAt < 15f)
            {
                return false;
            }

            w.GoOnUntil = now + 60f;
            w.GainAt = now;
            HealGiveUps++;
            Log.Detail("task", $"{bot.Profile.Nickname} stopped to heal but nothing heals any more (hp {hp:P0}) - goes on");
            return true;
        }
    }

    /// <summary>
    /// A squad shares what it has searched: when a bot is done with a body or a container, its squadmates leave it alone - unless
    /// something worth taking is still in it (the first one's bag was full). LootingBots keeps such lists only per bot.
    /// </summary>
    public class LootShareCapturePatch : ModulePatch
    {
        private const float StillWorth = 25000f;

        public static int Shared { get; private set; }

        public static void Clear() => Shared = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LootingBrain), nameof(LootingBrain.CleanupLoot));

        [PatchPrefix]
        protected static void PatchPrefix(LootingBrain __instance, bool ignore)
        {
            try
            {
                if (!ignore || !Settings.SquadTactics.Value || string.IsNullOrEmpty(__instance.ActiveLootId) || __instance.BotOwner == null)
                {
                    return;
                }

                Share(__instance.BotOwner, __instance.ActiveLootId, __instance.ActiveLoot);
            }
            catch (Exception e)
            {
                Diag.Report("LootShareCapturePatch", e);
            }
        }

        private static void Share(BotOwner looter, string lootId, InteractableObject loot)
        {
            HashSet<string> squad = LootBrain.SquadIds(looter);
            if (squad.Count <= 1)
            {
                return;
            }

            float left = Worth(loot);
            if (left >= StillWorth)
            {
                Log.Detail("squad", $"{looter.Profile.Nickname} left ~{left / 1000f:0}k in {Name(loot)} for the squad");
                return;
            }

            int told = 0;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null || p.ProfileId == looter.ProfileId || !squad.Contains(p.ProfileId))
                {
                    continue;
                }

                LootingBrain brain = p.GetComponent<LootingBrain>();
                if (brain != null && !brain.IgnoredLootIds.Contains(lootId))
                {
                    brain.IgnoreLoot(lootId);
                    told++;
                }
            }

            if (told > 0)
            {
                Shared++;
                Log.Detail("squad", $"{looter.Profile.Nickname} searched {Name(loot)} (~{left / 1000f:0}k left) - {told} squadmates skip it");
            }
        }

        private static float Worth(InteractableObject loot)
        {
            switch (loot)
            {
                case Corpse corpse:
                    return LootBrain.VisibleValue(corpse.ItemOwner?.RootItem as InventoryEquipment);
                case LootableContainer container:
                    return LootBrain.ContentsValue(container.ItemOwner?.RootItem);
                default:
                    return 0f;
            }
        }

        private static string Name(InteractableObject loot) => loot is Corpse ? "a body" : loot is LootableContainer ? "a container" : "an item";
    }
}

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// QuestingBots "stops to check for loot" by standing aside for LootingBots - which scans only on its own timer (every ~10 s). In between the bot stood
    /// in QuestingBots' idle layer doing nothing until the window closed (raid 2026-10-22: 571 samples, 14 scavs never looted in up to 12 minutes).
    /// Here the scan starts the moment QuestingBots stands aside; nothing found within 2.5 s (no target, the scan over) - the bot goes back to its errand
    /// instead of standing there.
    /// </summary>
    public static class LootKick
    {
        private class Ask
        {
            public float At;
            public bool Found;
            public float ReleasedUntil;
            public EBotQuestingDecision Instead;
        }

        private static readonly Dictionary<string, Ask> _asks = new Dictionary<string, Ask>();

        public static int Kicked { get; private set; }
        public static int Released { get; private set; }

        public static string Summary => $"QuestingBots loot breaks: scans started at once {Kicked}, idle breaks ended {Released}";

        public static void Clear()
        {
            _asks.Clear();
            Kicked = Released = 0;
        }

        public static EBotQuestingDecision? OnQbWantsLoot(BotOwner bot, BotQuestingDecisionMonitor monitor)
        {
            if (bot == null || bot.IsDead || Bots.BotProfiles.Get(bot)?.Kind == Bots.BotKind.Other)
            {
                return null;
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            LootingBots.Components.LootFinder finder = bot.GetPlayer?.GetComponent<LootingBots.Components.LootFinder>();
            if (brain == null || finder == null)
            {
                return null;
            }

            float now = Time.time;
            bool busy = brain.LootTaskRunning || brain.HasActiveLootable || finder.IsScanRunning || DrakiaXYZ.BigBrain.Brains.BrainManager.GetActiveLayerName(bot) == "Looting";
            if (_asks.TryGetValue(bot.ProfileId, out Ask released) && now < released.ReleasedUntil && !busy)
            {
                // Sent back to its errand a moment ago: QuestingBots' loot window is still open, the bot does not stand in it
                return released.Instead;
            }

            // Kept here on purpose: a room being searched, a body it means to go through
            bool wanted = RoomSweep.InVisit(bot) || LootJobs.HasWorkNear(bot, 15f);
            if (!_asks.TryGetValue(bot.ProfileId, out Ask ask) || now - ask.At > 20f)
            {
                // A new break: scan now - or, if LootingBots will not take the bot at all (its brain off for performance, a wound it would treat first,
                // danger about), do not stand in the break
                _asks[bot.ProfileId] = ask = new Ask { At = now };
                if (busy)
                {
                    ask.Found = true;
                    return null;
                }

                string why = WontLoot(bot, brain);
                if (why == null)
                {
                    // Harmonizer: the nearest thing is handed over at once - or, nothing to stop for, the bot walks on instead of standing through the break
                    if (Bots.Harmonizer.LootBreak(bot, out string skip))
                    {
                        ask.Found = true;
                        Kicked++;
                        return null;
                    }

                    if (skip != null && !wanted)
                    {
                        Log.Detail("loot", () => $"{bot.Profile?.Nickname}: QuestingBots stops it to loot, but {skip} - goes on");
                        return Release(bot, ask, now);
                    }
                }

                if (why == null)
                {
                    finder.ForceScan();
                    Kicked++;
                    return null;
                }

                if (!wanted)
                {
                    Log.Detail("loot", () => $"{bot.Profile?.Nickname}: QuestingBots stops it to loot, but {why} - goes on instead");
                    return Release(bot, ask, now);
                }

                return null;
            }

            if (busy)
            {
                ask.Found = true;
                return null;
            }

            // Asked, nothing to go to and nothing going on: back to the errand (a squad member waiting for the others keeps waiting)
            if (!ask.Found && now - ask.At > 2.5f && !wanted)
            {
                return Release(bot, ask, now);
            }

            return null;
        }

        private static EBotQuestingDecision Release(BotOwner bot, Ask ask, float now)
        {
            Released++;
            bool follower = TacticalTasks.SquadLeaderOf(bot) != null;
            ask.Instead = follower ? EBotQuestingDecision.None
                : bot.GetObjectiveManager()?.IsJobAssignmentActive == true ? EBotQuestingDecision.Quest : EBotQuestingDecision.WaitForAssignment;
            ask.ReleasedUntil = now + 10f;
            return ask.Instead;
        }

        /// <summary>Why LootingBots' layer would not run for the bot now, or null when it would.</summary>
        public static string WontLoot(BotOwner bot, LootingBrain brain)
        {
            if (!brain.LootingEnabled)
            {
                return "it is not allowed to loot";
            }

            if (LootDanger.Why(bot) is string danger)
            {
                return danger;
            }

            if (!brain.HasFreeSpace && !brain.ForceBrainEnabled)
            {
                return "its bag is full";
            }

            BotMedecine med = bot.Medecine;
            if (med?.FirstAid != null && med.SurgicalKit != null)
            {
                bool treatable = (med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use) || (med.SurgicalKit.HaveWork && med.SurgicalKit.HaveSmth2Use);
                bool hurt = med.FirstAid.Have2Do || med.SurgicalKit.HaveWork;
                if (treatable || (hurt && !Settings.WoundedLoot.Value))
                {
                    return "it has a wound to treat first";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// QuestingBots' idle layer gives way to the loot LootingBots was given (F12 -> 7 "Bots go for the loot they are given").
    /// For scavs LootingBots registers its looting layer at priority 4 - the same as QuestingBots' recovery layer - and the recovery layer steps aside only
    /// when QuestingBots sees looting already under way (the active logic is LootingBots'). A scav handed a body (a body job, a loot break, its own kill) or
    /// a container therefore stood in the idle layer for a few seconds and walked on: raid 2026-10-26 18:34 - 7 of 63 bodies handed to scavs were searched
    /// (PMCs, whose looting layer is at 5: 37 of 44). Here the recovery layer is off while LootingBots has a target for the bot.
    /// </summary>
    public class QbRecoveryYieldsToLootPatch : ModulePatch
    {
        public static int Yielded { get; private set; }

        public static void ClearCounters() => Yielded = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(AccessTools.TypeByName("QuestingBots.BotLogic.Recovery.BotRecoveryLayer"), "IsActive");

        [PatchPostfix]
        protected static void PatchPostfix(DrakiaXYZ.BigBrain.Brains.CustomLayer __instance, ref bool __result)
        {
            if (!__result || !Settings.LootGivenFirst.Value)
            {
                return;
            }

            try
            {
                BotOwner bot = __instance.BotOwner;
                LootingBrain brain = bot?.GetPlayer?.GetComponent<LootingBrain>();
                if (brain == null || !brain.IsBrainEnabled || !brain.IsBotLooting || Bots.BotProfiles.Get(bot)?.Kind is null or Bots.BotKind.Other)
                {
                    return;
                }

                // LootingBots' own layer must be able to take the bot: not while it has a wound to treat with what it carries (HealLayer does that first)
                BotMedecine med = bot.Medecine;
                if (med?.FirstAid != null && ((med.FirstAid.Have2Do && med.FirstAid.HaveSmth2Use) || med.Using))
                {
                    return;
                }

                __result = false;
                Yielded++;
            }
            catch (Exception e)
            {
                Diag.Report("QbRecoveryYieldsToLootPatch", e);
            }
        }
    }

    /// <summary>
    /// Short holds on QuestingBots' standard quests (F12 -> 3 "Short holds on QuestingBots quests"). Their ambush and hold steps last 30-120 s
    /// (raid 2026-10-26 18:34: Макар Пупкин stood 35 s and then 70+ s on the ambush step of "Dorms 3 Story" on Customs) - for PMCs and scavs 5-15 s
    /// (an ambush 8-15 s); LiveRaid's own quests are short already; bosses and their followers keep the original times.
    /// </summary>
    public class QbShortHoldPatch : ModulePatch
    {
        private static readonly System.Random _rng = new System.Random();
        private static MethodInfo _setMin;

        public static int Shortened { get; private set; }

        public static void ClearCounters() => Shortened = 0;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("QuestingBots.Models.Questing.BotJobAssignment"), "InitializeObjectiveStep");

        [PatchPostfix]
        protected static void PatchPostfix(QuestingBots.Models.Questing.BotJobAssignment __instance)
        {
            if (!Settings.ShortQbHolds.Value)
            {
                return;
            }

            try
            {
                var step = __instance.QuestObjectiveStepAssignment;
                if (step == null || __instance.MinElapsedTime <= 15d)
                {
                    return;
                }

                var action = step.ActionType;
                bool ambush = action == QuestingBots.Models.Questing.QuestAction.Ambush || action == QuestingBots.Models.Questing.QuestAction.Snipe;
                if (!ambush && action != QuestingBots.Models.Questing.QuestAction.HoldAtPosition)
                {
                    return;
                }

                Bots.BotProfile p = __instance.BotOwner != null ? Bots.BotProfiles.Get(__instance.BotOwner) : null;
                if (p == null || p.Kind == Bots.BotKind.Other)
                {
                    return;
                }

                _setMin ??= AccessTools.PropertySetter(typeof(QuestingBots.Models.Questing.BotJobAssignment), nameof(QuestingBots.Models.Questing.BotJobAssignment.MinElapsedTime));
                double was = __instance.MinElapsedTime;
                double now = ambush ? 8d + 7d * _rng.NextDouble() : 5d + 10d * _rng.NextDouble();
                _setMin.Invoke(__instance, new object[] { Math.Round(now, 1) });
                Shortened++;
                Log.Detail("quest", () => $"{p.Name}: {action} step of \"{__instance.QuestAssignment?.GetName()}\" {was:0} s -> {now:0} s (players do not sit there)");
            }
            catch (Exception e)
            {
                Diag.Report("QbShortHoldPatch", e);
            }
        }
    }
}
