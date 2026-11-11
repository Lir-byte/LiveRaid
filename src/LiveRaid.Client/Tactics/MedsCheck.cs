using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// What a medical item can really do now (F12 -> 9 "Only a medkit that can stop the bleeding").
    ///
    /// Raid 2026-10-26 18:34: Waffle bled from 38 % to death over three and a half minutes, "starting to patch himself up" every 4-5 s. EFT's bot first aid
    /// (BotFirstAid.RefreshMeds) takes the first medkit that can stop bleeding at all - whatever is left in it. The game removes a bleed only when the kit
    /// still holds its cost (ActiveHealthController.MedEffect.Residue: HpResource &lt; cost -> the bleed stays), so a nearly empty car kit was put on the
    /// wound, the bleed went on, and five seconds later (HEAL_DELAY_SEC) the same kit again. Here:
    ///  - after EFT picks its medkit, the one that can afford the bleed the bot has is taken (heavy first), or one that heals at all;
    ///  - when the chosen kit cannot afford the bleed, EFT's "bleeding" mark is dropped for it and the wounded part is looked for as EFT does for hit points
    ///    (the kit heals what it can), so no kit is wasted in a loop;
    ///  - CanTreat is what the squad's handovers and the leg care (LegCare) ask too.
    /// </summary>
    public static class MedsCheck
    {
        public static int Swapped { get; private set; }
        public static int Unstoppable { get; private set; }
        public static int SurgeryKits { get; private set; }

        public static int StaleStims { get; private set; }

        public static void ClearCounters()
        {
            Swapped = Unstoppable = SurgeryKits = StaleStims = 0;
            _surgeryNoted.Clear();
        }

        public static string Summary => $"medkits: a kit that can afford the bleed taken instead {Swapped}, bleeds no kit carried could stop {Unstoppable}; " +
                                        $"bots with a surgical kit in the bag for a blacked-out part {SurgeryKits}; injectors already used up or given away not reached for {StaleStims}";

        /// <summary>
        /// The injector the game remembers for the bot is looked up again before it is used. Raid 2026-11-10 12:37 (Reserve): Hotdog took its Propital as a
        /// painkiller for a blacked-out leg (LegCare, through the first aid hands) - and the game's BotStimulators still held the used-up item: FindMeds is called
        /// only after the bot's own injection, and when it finds nothing it clears "HaveSmt" but leaves the old item in place. SAIN then chose "Stims" in the
        /// fight again and again (ten SeekCover/Stims ping-pongs in a row), each try threw "Trying to get parent of an item that doesn't have a parent" in the
        /// hands and left "Using" set. The same happens to an injector handed to a squadmate, thrown out for a better find or shed as weight.
        /// </summary>
        public static void BeforeStim(BotStimulators stims)
        {
            if (stims == null || stims.Using)
            {
                return;
            }

            Stimulator before = stims._stimulator;
            stims.Refresh();
            if (before != null && stims._stimulator != before)
            {
                StaleStims++;
                Log.Detail("combat", () => $"{stims._owner?.Profile?.Nickname}: the injector it meant to use ({before.ShortName.Localized()}) is not on it any more - " +
                                           (stims._stimulator != null ? "takes another" : "has none left"));
            }
        }

        // Where a surgical kit may come from: what the bot carries - not its secure container
        private static readonly EquipmentSlot[] BagSlots = { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets };
        private static readonly List<Meds> _surgeryList = new List<Meds>();
        private static readonly HashSet<string> _surgeryNoted = new HashSet<string>();

        /// <summary>
        /// After EFT's BotSurgicalKit.RefreshMeds (F12 -> 9 "Surgery with a kit from the bag"). EFT reads the surgical kit (CMS, Surv12 - anything that restores a
        /// blacked-out part) from the secure container only, unless the role says otherwise (SURGE_KIT_ONLY_SAFE_CONTAINER: scavs false, PMCs - not set,
        /// so EFT's own true), and SPT puts no surgical kit into a PMC's kit at all: a PMC never operated, not even with a CMS taken off a body. Here the kit
        /// is the one in the backpack, rig or pockets (with uses left), never the secure container; no such kit - no surgery.
        /// </summary>
        public static void AfterSurgeryRefresh(BotSurgicalKit kit)
        {
            BotOwner bot = kit?._owner;
            Player player = bot?.GetPlayer;
            if (!Settings.SurgeryFromBag.Value || player?.InventoryController == null || !Ours(bot) || bot.Settings?.FileSettings?.Mind?.CAN_USE_MEDS != true)
            {
                return;
            }

            _surgeryList.Clear();
            player.InventoryController.GetAcceptableItemsNonAlloc(BagSlots, _surgeryList);
            Meds found = null;
            foreach (Meds m in _surgeryList)
            {
                if (CanTreat(m, EDamageEffectType.DestroyedPart))
                {
                    found = m;
                    break;
                }
            }

            kit.CurUsingMeds = found;
            if (found != null && _surgeryNoted.Add(bot.ProfileId))
            {
                SurgeryKits++;
                Log.Detail("combat", () => $"{bot.Profile?.Nickname} has {found.ShortName.Localized()} in its bag for surgery");
            }
        }

        /// <summary>The item can treat that effect now: it has it, and a medkit still holds the effect's cost.</summary>
        public static bool CanTreat(Meds m, EDamageEffectType type)
        {
            HealthEffectsComponent h = m?.HealthEffectsComponent;
            if (h?.DamageEffects == null || !h.DamageEffects.TryGetValue(type, out JsonType.DamageEffectSpecification e))
            {
                return false;
            }

            MedKitComponent kit = m.MedKitComponent;
            return kit == null || kit.HpResource >= e.Cost;
        }

        // Bosses and their followers keep EFT's own first aid
        private static bool Ours(BotOwner bot)
        {
            Bots.BotProfile p = bot != null ? Bots.BotProfiles.Get(bot) : null;
            return p != null && p.Kind != Bots.BotKind.Other;
        }

        /// <summary>The item heals hit points (a medkit with something left in it).</summary>
        public static bool HealsHp(Meds m) => m is MedKit && (m.MedKitComponent == null || m.MedKitComponent.HpResource > 0f);

        /// <summary>After EFT's RefreshMeds: the kit for the bleed the bot has, if one can afford it.</summary>
        public static void AfterRefresh(BotFirstAid aid)
        {
            if (!Settings.UsableMedkit.Value || aid?._health == null || !aid._health.IsAlive || aid._medsList == null || !Ours(aid._owner))
            {
                return;
            }

            bool heavy = aid._health.FindExistingEffect<IHeavyBleeding>() != null;
            bool light = aid._health.FindExistingEffect<ILightBleeding>() != null;
            if (!heavy && !light)
            {
                return;
            }

            Meds cur = aid.CurUsingMeds;
            if ((heavy && CanTreat(cur, EDamageEffectType.HeavyBleeding)) || (!heavy && light && CanTreat(cur, EDamageEffectType.LightBleeding)))
            {
                return;
            }

            List<MedKit> kits = aid._medsList.OfType<MedKit>().ToList();
            MedKit better = (heavy ? kits.FirstOrDefault(k => CanTreat(k, EDamageEffectType.HeavyBleeding)) : null)
                            ?? (light ? kits.FirstOrDefault(k => CanTreat(k, EDamageEffectType.LightBleeding)) : null)
                            ?? (cur != null && HealsHp(cur) ? null : kits.FirstOrDefault(HealsHp));
            if (better != null && better != cur)
            {
                aid.CurUsingMeds = better;
                Swapped++;
            }
        }

        /// <summary>After EFT's FindDamagedPart: a bleed the chosen kit cannot afford is not its job - the part to heal is looked for by hit points.</summary>
        public static void AfterFindDamagedPart(BotFirstAid aid, BotOwner owner)
        {
            if (!Settings.UsableMedkit.Value || aid?._health == null || !aid.IsBleeding || aid.CurUsingMeds == null || !Ours(owner))
            {
                return;
            }

            Meds cur = aid.CurUsingMeds;
            bool afford = (aid._isBleedingHeavy && CanTreat(cur, EDamageEffectType.HeavyBleeding)) || (aid._isBleedingLight && CanTreat(cur, EDamageEffectType.LightBleeding));
            if (afford)
            {
                return;
            }

            aid._isBleedingHeavy = false;
            aid._isBleedingLight = false;
            Unstoppable++;
            float limit = owner?.Settings?.FileSettings?.Mind?.PART_PERCENT_TO_HEAL ?? 0.65f;
            foreach (EBodyPart part in aid.parts)
            {
                float n = aid._health.GetBodyPartHealth(part).Normalized;
                if (n < limit && n > aid.min_percent)
                {
                    aid._bodyPartToHeal = part;
                    aid.Damaged = true;
                    return;
                }
            }

            aid._bodyPartToHeal = null;
            aid.Damaged = false;
        }
    }

    public class FirstAidRefreshPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotFirstAid), nameof(BotFirstAid.RefreshMeds));

        [PatchPostfix]
        protected static void PatchPostfix(BotFirstAid __instance)
        {
            try
            {
                MedsCheck.AfterRefresh(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("FirstAidRefreshPatch", e);
            }
        }
    }

    public class SurgeryKitRefreshPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotSurgicalKit), nameof(BotSurgicalKit.RefreshMeds));

        [PatchPostfix]
        protected static void PatchPostfix(BotSurgicalKit __instance)
        {
            try
            {
                MedsCheck.AfterSurgeryRefresh(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("SurgeryKitRefreshPatch", e);
            }
        }
    }

    /// <summary>EFT's bot is about to inject a stimulator: the item is checked against what the bot really carries first (MedsCheck.BeforeStim).</summary>
    public class StimRefreshPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotStimulators), nameof(BotStimulators.TryApply));

        [PatchPrefix]
        protected static void PatchPrefix(BotStimulators __instance)
        {
            try
            {
                MedsCheck.BeforeStim(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("StimRefreshPatch", e);
            }
        }
    }

    public class FirstAidDamagedPartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotFirstAid), nameof(BotFirstAid.FindDamagedPart));

        [PatchPostfix]
        protected static void PatchPostfix(BotFirstAid __instance)
        {
            try
            {
                MedsCheck.AfterFindDamagedPart(__instance, __instance._owner);
            }
            catch (Exception e)
            {
                Diag.Report("FirstAidDamagedPartPatch", e);
            }
        }
    }
}
