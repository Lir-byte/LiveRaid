using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using LiveRaid.Client.Tactics;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A broken leg is treated like a player treats it (F12 -> 9 "Treat a broken leg").
    ///
    /// Raid 2026-10-26 18:34: Waffle broke a leg at 10:41 and walked for the rest of his life - into the fight, to his exit, round the map - never once running.
    /// That is EFT's rule (Player.UpdateSpeedLimitByHealth): a broken or blacked-out leg without a painkiller takes the sprint away and caps the speed at 30 %
    /// (both legs 20 %). EFT's bots never put a splint on: their first aid uses medkits only, their surgery fixes destroyed parts, their stimulators are
    /// injectors. A player splints the leg at the first quiet moment, or swallows a painkiller to run on it, or asks his squad for one.
    ///  - What: a splint (any item that removes a fracture, with enough left in it) on the broken leg; with no splint (or a destroyed leg, which takes surgery)
    ///    a painkiller, if none is working yet.
    ///  - When: by HealLayer, the same quiet moment as a bandage (no enemy in sight, not hit, nobody close), through EFT's own first aid hands
    ///    (BotFirstAid.ApplyToSelf with the item and the leg), so the animation, the "using meds" flag and the weapon afterwards are EFT's.
    ///  - Nothing for it: the bot limps on - and its squad is asked for a splint or a painkiller (SquadShare), a solo PMC weighs leaving earlier (ExtractPlanner).
    /// </summary>
    public static class LegCare
    {
        public static int Splinted { get; private set; }
        public static int Painkillers { get; private set; }
        public static int Limping { get; private set; }

        public static void ClearCounters()
        {
            Splinted = Painkillers = Limping = 0;
            _noted.Clear();
            _nextTry.Clear();
        }

        public static string Summary => $"broken legs: splinted {Splinted}, painkillers taken {Painkillers}, bots that limped with nothing for it {Limping}";

        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static readonly Dictionary<string, float> _nextTry = new Dictionary<string, float>();
        private static readonly List<Meds> _meds = new List<Meds>();

        /// <summary>A leg is broken or blacked out and no painkiller lets the bot run on it.</summary>
        public static bool Limps(Player player)
        {
            IHealthController h = player?.HealthController;
            if (h == null || !h.IsAlive)
            {
                return false;
            }

            bool hurt = h.IsBodyPartBroken(EBodyPart.LeftLeg) || h.IsBodyPartBroken(EBodyPart.RightLeg)
                        || h.IsBodyPartDestroyed(EBodyPart.LeftLeg) || h.IsBodyPartDestroyed(EBodyPart.RightLeg);
            return hurt && !OnPainkillers(h);
        }

        /// <summary>For the trace: "broken L", "blacked R", "+painkiller".</summary>
        public static string Describe(Player player)
        {
            IHealthController h = player?.HealthController;
            if (h == null || !h.IsAlive)
            {
                return null;
            }

            string l = h.IsBodyPartDestroyed(EBodyPart.LeftLeg) ? "blacked L" : h.IsBodyPartBroken(EBodyPart.LeftLeg) ? "broken L" : null;
            string r = h.IsBodyPartDestroyed(EBodyPart.RightLeg) ? "blacked R" : h.IsBodyPartBroken(EBodyPart.RightLeg) ? "broken R" : null;
            if (l == null && r == null)
            {
                return null;
            }

            return (l != null && r != null ? l + "+" + r.Substring(r.Length - 1) : l ?? r) + (OnPainkillers(h) ? ",painkiller" : "");
        }

        private static bool OnPainkillers(IHealthController h) => h.FindActiveEffect<IPainKiller>() != null || h.FindActiveEffect<IBerserk>() != null;

        /// <summary>
        /// What the bot would put on its leg now, or false: a splint on a broken leg, else a painkiller when none works. Asked by HealLayer a few times a second.
        /// </summary>
        public static bool Plan(BotOwner bot, out Meds item, out EBodyPart part)
        {
            item = null;
            part = EBodyPart.Common;
            Player player = bot?.GetPlayer;
            IHealthController h = player?.HealthController;
            if (!Settings.TreatLegs.Value || h == null || !h.IsAlive || bot.Medecine?.FirstAid == null)
            {
                return false;
            }

            bool brokenL = h.IsBodyPartBroken(EBodyPart.LeftLeg) && !h.IsBodyPartDestroyed(EBodyPart.LeftLeg);
            bool brokenR = h.IsBodyPartBroken(EBodyPart.RightLeg) && !h.IsBodyPartDestroyed(EBodyPart.RightLeg);
            bool blacked = h.IsBodyPartDestroyed(EBodyPart.LeftLeg) || h.IsBodyPartDestroyed(EBodyPart.RightLeg);
            if (!brokenL && !brokenR && !blacked)
            {
                return false;
            }

            _meds.Clear();
            player.InventoryController.GetAcceptableItemsNonAlloc(BotMedecine.anySlots, _meds);
            if (brokenL || brokenR)
            {
                // The quickest one: a splint (3-5 s) before a surgical kit (Surv12 sets a bone too, but takes 20 s - raid 2026-10-30 23:28: RedBeard
                // started it eight times in a minute and never finished it)
                foreach (Meds m in _meds)
                {
                    if (MedsCheck.CanTreat(m, EDamageEffectType.Fracture) && (item == null || m.HealthEffectsComponent.UseTime < item.HealthEffectsComponent.UseTime))
                    {
                        item = m;
                    }
                }

                if (item != null)
                {
                    part = brokenL ? EBodyPart.LeftLeg : EBodyPart.RightLeg;
                    return true;
                }
            }

            // A painkiller already swallowed but not working yet counts: not a second one
            if (h.FindExistingEffect<IPainKiller>() == null && h.FindActiveEffect<IBerserk>() == null)
            {
                foreach (Meds m in _meds)
                {
                    if (MedsCheck.CanTreat(m, EDamageEffectType.Pain))
                    {
                        // The whole body, as a player takes it (Player.TargetBodyParts): the balm, pill and injector animations exist only for that -
                        // put "on the chest" the hands just hung down for the whole use (the "Golden Star" balm, raid 2026-10-26 20:28)
                        item = m;
                        part = EBodyPart.Common;
                        return true;
                    }
                }

                if (_noted.Add(bot.ProfileId))
                {
                    Limping++;
                    Log.Detail("combat", () => $"{bot.Profile?.Nickname} has {Describe(player)} and nothing for it (no splint, no painkiller) - it can only walk");
                }
            }

            return false;
        }

        /// <summary>Puts the item on through EFT's own first aid; false when it cannot start now.</summary>
        public static bool Apply(BotOwner bot, Meds item, EBodyPart part)
        {
            BotFirstAid aid = bot?.Medecine?.FirstAid;
            Player player = bot?.GetPlayer;
            // Not while a weapon change is under way: the meds never reached the hands, the flag stayed up and HandsGuard ended the use 3 s later
            if (aid == null || player == null || item == null || aid.Using || bot.Medecine.Using || bot.WeaponManager?.Reload?.Reloading == true
                || bot.WeaponManager?.Selector?.IsWeaponReady == false || (_nextTry.TryGetValue(bot.ProfileId, out float next) && Time.time < next))
            {
                return false;
            }

            _nextTry[bot.ProfileId] = Time.time + 6f;
            bool splint = MedsCheck.CanTreat(item, EDamageEffectType.Fracture) && (part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg);
            string how = Describe(player);
            aid.CurUsingMeds = item;
            aid._bodyPartToHeal = part;

            // EFT's callback after the use takes the gun back, looks for the wounded part again and puts its own medkit back in charge
            aid.ApplyToSelf(null, () =>
            {
                if (splint)
                {
                    Splinted++;
                }
                else
                {
                    Painkillers++;
                }

                _noted.Remove(bot.ProfileId);
            });
            Log.Detail("combat", () => $"{bot.Profile?.Nickname} {(splint ? "splints its " + (part == EBodyPart.LeftLeg ? "left" : "right") + " leg" : "takes a painkiller for its leg")} " +
                                       $"({item.ShortName.Localized()}, {how})");
            return true;
        }
    }
}

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// On a painkiller a bot with a broken or blacked-out leg may sprint again - and EFT hurts the leg 2 hp every 1-1.5 s of it (Player.InflictSelfDamage);
    /// damage to a blacked-out leg spreads over the body. A player on a painkiller with a broken leg walks at a normal pace and runs only when he must, in
    /// short dashes (F12 -> 9 "Run on a hurt leg only when it must"):
    ///  - broken leg: a dash of up to 4 s, then 8 s of walking, and only when hit in the last 6 s, under fire, an enemy in sight within 60 m, or running away
    ///    (stance Retreat / Break contact);
    ///  - blacked-out leg: only when hit in the last 6 s or under fire, dashes of up to 2.5 s with 15 s between them.
    /// Every sprint of every bot goes through Player.EnableSprint (EFT's mover, SAIN, QuestingBots): there the request is turned into "no sprint".
    /// Without a painkiller EFT itself allows no sprint on such a leg; the human player is never touched.
    /// </summary>
    public static class LegSprint
    {
        private class State
        {
            public float BurstFrom = -1f;
            public float RestUntil;
            public float NoteAt;
        }

        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        public static int Dashes { get; private set; }
        public static int Cut { get; private set; }

        public static void ClearCounters()
        {
            _states.Clear();
            Dashes = Cut = 0;
        }

        public static string Summary => $"sprints on a hurt leg (painkiller): dashes {Dashes}, cut short {Cut}";

        /// <summary>The sprint the bot asks for, or false when it must walk.</summary>
        public static bool Allow(Player player, bool enable)
        {
            if (!Settings.LegSprintLimit.Value || player == null || !player.IsAI)
            {
                return enable;
            }

            MovementContext mc = player.MovementContext;
            if (mc == null || !mc.PhysicalConditionContainsAny(EPhysicalCondition.LeftLegDamaged | EPhysicalCondition.RightLegDamaged)
                || !mc.PhysicalConditionIs(EPhysicalCondition.OnPainkillers))
            {
                // A sound leg - or no painkiller, and then EFT allows no sprint by itself
                return enable;
            }

            BotOwner bot = player.AIData?.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other)
            {
                return enable;
            }

            int id = player.Id;
            if (!_states.TryGetValue(id, out State s))
            {
                _states[id] = s = new State();
            }

            IHealthController h = player.HealthController;
            bool blacked = h.IsBodyPartDestroyed(EBodyPart.LeftLeg) || h.IsBodyPartDestroyed(EBodyPart.RightLeg);
            float maxDash = blacked ? 2.5f : 4f;
            float rest = blacked ? 15f : 8f;
            float now = Time.time;
            if (!enable)
            {
                if (s.BurstFrom >= 0f)
                {
                    // A dash it ended itself: a rest as long as the share of the dash it used
                    s.RestUntil = now + rest * Mathf.Clamp01((now - s.BurstFrom) / maxDash);
                    s.BurstFrom = -1f;
                }

                return false;
            }

            if (s.BurstFrom >= 0f)
            {
                if (now - s.BurstFrom <= maxDash)
                {
                    return true;
                }

                s.BurstFrom = -1f;
                s.RestUntil = now + rest;
                Cut++;
                return false;
            }

            if (now < s.RestUntil)
            {
                return false;
            }

            string why = MustRun(bot, blacked);
            if (why == null)
            {
                return false;
            }

            s.BurstFrom = now;
            Dashes++;
            if (now >= s.NoteAt)
            {
                s.NoteAt = now + 20f;
                Log.Detail("combat", () => $"{p.Name} dashes on its {(blacked ? "blacked-out" : "broken")} leg ({why}) - {maxDash:0.#} s at most, then it walks");
            }

            return true;
        }

        private static string MustRun(BotOwner bot, bool blacked)
        {
            if (Tactics.HitReaction.SinceHit(bot) < 6f)
            {
                return "was hit";
            }

            if (bot.Memory?.IsUnderFire == true)
            {
                return "under fire";
            }

            if (blacked)
            {
                return null;
            }

            Tactics.CombatStance stance = Tactics.CombatSense.StanceOf(bot);
            if (stance == Tactics.CombatStance.Retreat || stance == Tactics.CombatStance.BreakContact)
            {
                return "getting away";
            }

            var enemy = SainBot.Of(bot)?.GoalEnemy;
            if (enemy != null && enemy.IsVisible && enemy.RealDistance < 60f)
            {
                return "an enemy in sight";
            }

            return null;
        }
    }

    public class LegSprintPatch : SPT.Reflection.Patching.ModulePatch
    {
        protected override System.Reflection.MethodBase GetTargetMethod() => HarmonyLib.AccessTools.Method(typeof(Player), nameof(Player.EnableSprint));

        [SPT.Reflection.Patching.PatchPrefix]
        protected static void PatchPrefix(Player __instance, ref bool enable)
        {
            try
            {
                enable = LegSprint.Allow(__instance, enable);
            }
            catch (System.Exception e)
            {
                Diag.Report("LegSprintPatch", e);
            }
        }
    }
}
