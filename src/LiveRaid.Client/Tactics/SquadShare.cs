using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Squadmates hand each other things, PMCs and scavs alike (F12 -> 8 "Share loot and meds"):
    ///  - meds: a member who needs treatment EFT's first aid cannot give him - bleeding with no kit that can still stop it, a broken leg with no splint or
    ///    painkiller (LegCare), hurt (under 85 %) with no medkit at all, a destroyed limb with no surgical kit - gets the right thing from a squadmate who can spare it;
    ///  - loot: a member whose bag is full hands his most valuable spare item (10k+) to one who has room for it.
    /// One scene, both busy with it (a "Handover" errand, rank of a call for help): the giver walks up to the receiver, who stops and turns to him; at arm's
    /// length the giver throws the item (LootingBots' throw, with its delay), and half a second later the receiver picks up exactly that item (LootingBots'
    /// pick-up). A fight, a death or 15-45 s (by distance) without the job done ends the scene; a thrown item the receiver could not take is left to his normal loot scan.
    /// Only what EFT's bots can use is handed over as meds: their first aid takes medkits only (MedKit items - not bandages, splints or painkillers), their
    /// surgery a kit that fixes a destroyed part. EFT reads a bot's meds anew only every 20 s - after a handover the receiver's list is refreshed at once.
    /// Why nobody helped a squadmate in need is written to the trace (squad) and counted in "LR Health on".
    ///
    /// 0.9.8 - in five raids not one meds handover came through (12 tried) and loot only 4 times in 20: the giver walked to a spot 1.3 m in front of the
    /// receiver and was "there" within 1.5 m of it (TacticsLayer), up to 2.8 m off - but the throw waited for 2.2 m, so he stood 2.3 m away until the 15 s
    /// ran out (Traveler and RedBeard twice, 2.33 / 2.34 m). Also: 15 s for any distance, the receiver went on looting the room meanwhile (RoomSweep), a
    /// receiver who began treating himself ended the scene, and anything short of urgent waited for SAIN to forget every enemy. Now: the throw from 3 m, the
    /// time by distance (15-45 s), the giver's spot follows the receiver, the receiver is left alone by the room sweep and may treat himself in the
    /// meantime (he picks the thing up after), and a mate's meds wait only for a lull (no enemy seen for 30 s nearer than 60 m), with looting stopped for it.
    /// </summary>
    public static class SquadShare
    {
        private const float Range = 40f;
        // The giver is "there" within 1.5 m of a spot 1.3 m from the receiver (TacticsLayer): the throw has to reach that far
        private const float Reach = 3f;
        private const float MinTime = 15f;
        private const float MaxTime = 45f;
        private const float MinLoot = 10000f;

        private enum Phase
        {
            Approach,
            Throw,
            Pick,
        }

        /// <summary>What a squadmate needs from the others, in order of importance.</summary>
        private enum Need
        {
            None,
            StopBleeding,
            Leg,
            Medkit,
            Surgery,
        }

        private class Handover
        {
            public BotOwner Giver;
            public BotOwner Receiver;
            public Item Item;
            public string What;
            public Phase Phase;
            public float Until;
            public float PickAt;
            public Task<bool> Job;
            public bool Done;
            public Vector3 ReceiverAt;
            public float WaitedForMeds;
        }

        private static readonly FieldInfo TransactionField = AccessTools.Field(typeof(LootingInventoryController), "_transactionController");
        private static readonly List<Handover> _active = new List<Handover>();
        private static readonly Dictionary<string, float> _cooldown = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _noteAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> _refusals = new Dictionary<string, int>();

        // Hand-overs between the same two that came to nothing in a row (raid 2026-10-26 20:28: Зураб Ювелир brought the same ТП-200 to Игорь Золотой
        // six times, every 30 s, each broken off - the receiver stood in a loop of being called and released)
        private static readonly Dictionary<string, int> _pairFails = new Dictionary<string, int>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next, _nextLook;

        public static int Meds { get; private set; }
        public static int Loot { get; private set; }
        public static int Failed { get; private set; }

        public static void Clear()
        {
            _active.Clear();
            _cooldown.Clear();
            _noteAt.Clear();
            _refusals.Clear();
            _pairFails.Clear();
            _next = _nextLook = 0f;
            Meds = Loot = Failed = 0;
        }

        public static string Summary =>
            $"meds handed over {Meds}, loot handed over {Loot}, failed {Failed}" +
            (_refusals.Count == 0 ? "" : "; not handed over: " + string.Join(", ", _refusals.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}")));

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Handover h = _active[i];
                if (h.Done)
                {
                    _active.RemoveAt(i);
                    continue;
                }

                try
                {
                    Advance(h);
                }
                catch (Exception e)
                {
                    Diag.Report("SquadShare", e);
                    End(h, false, "error");
                }

                if (h.Done)
                {
                    _active.RemoveAt(i);
                }
            }

            if (!Settings.SquadShare.Value || Time.time < _nextLook)
            {
                return;
            }

            _nextLook = Time.time + 3f;
            LookForNeeds();
        }

        // ---------------- who needs what ----------------

        private static void LookForNeeds()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            Dictionary<BotsGroup, List<BotOwner>> groups = new Dictionary<BotsGroup, List<BotOwner>>();
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || bot.BotsGroup == null || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                if (!groups.TryGetValue(bot.BotsGroup, out List<BotOwner> list))
                {
                    groups[bot.BotsGroup] = list = new List<BotOwner>();
                }

                list.Add(bot);
            }

            foreach (List<BotOwner> members in groups.Values)
            {
                if (members.Count < 2)
                {
                    continue;
                }

                // Meds first: someone who bleeds matters more than a full bag
                bool started = false;
                foreach (BotOwner receiver in members.OrderBy(_ => _rng.Next()))
                {
                    try
                    {
                        if (TryMeds(receiver, members))
                        {
                            started = true;
                        }
                    }
                    catch (Exception e)
                    {
                        Diag.Report("SquadShare", e);
                    }
                }

                if (started)
                {
                    continue;
                }

                foreach (BotOwner receiver in members.OrderBy(_ => _rng.Next()))
                {
                    try
                    {
                        if (TryLoot(receiver, members))
                        {
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        Diag.Report("SquadShare", e);
                    }
                }
            }
        }

        private static bool TryMeds(BotOwner receiver, List<BotOwner> members)
        {
            if (OnCooldown(receiver) || InHandover(receiver))
            {
                return false;
            }

            float hp = receiver.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            Need need = MedNeed(receiver, hp, out bool urgent);
            if (need == Need.None)
            {
                return false;
            }

            string what = Describe(need);
            string busy = Busy(receiver, urgent, true, true);
            if (busy != null)
            {
                Refuse(receiver, "needs " + what, "himself " + busy, $"needs {what} (hp {hp:P0}) - no handover: he is {busy}");
                return false;
            }

            List<string> reasons = new List<string>();
            bool anyNear = false;
            foreach (BotOwner giver in members.OrderBy(m => (m.Position - receiver.Position).sqrMagnitude))
            {
                if (giver == receiver)
                {
                    continue;
                }

                if ((giver.Position - receiver.Position).sqrMagnitude > Range * Range)
                {
                    continue;
                }

                anyNear = true;
                if (OnCooldown(giver) || InHandover(giver))
                {
                    reasons.Add($"{giver.Profile.Nickname} has just handed something over");
                    continue;
                }

                string giverBusy = Busy(giver, urgent, false, true);
                if (giverBusy != null)
                {
                    reasons.Add($"{giver.Profile.Nickname} is {giverBusy}");
                    continue;
                }

                Item item = SpareMeds(giver, need, urgent, out string why);
                if (item == null)
                {
                    reasons.Add($"{giver.Profile.Nickname} {why}");
                    continue;
                }

                // A squadmate's wound comes before a drawer
                StopLooting(receiver);
                StopLooting(giver);

                if (Start(giver, receiver, item, "meds", what))
                {
                    return true;
                }

                reasons.Add($"{giver.Profile.Nickname} could not be given the errand");
            }

            if (!anyNear)
            {
                Refuse(receiver, "needs " + what, $"nobody within {Range:0} m", $"needs {what} (hp {hp:P0}) - no handover: no squadmate within {Range:0} m");
            }
            else
            {
                bool noneSpare = reasons.All(r => r.Contains("no ") || r.Contains("keeps"));
                Refuse(receiver, "needs " + what, noneSpare ? "nobody can spare it" : "squadmates busy",
                       $"needs {what} (hp {hp:P0}) - no handover: {string.Join("; ", reasons)}");
            }

            return false;
        }

        private static bool TryLoot(BotOwner receiver, List<BotOwner> members)
        {
            if (OnCooldown(receiver) || InHandover(receiver) || LootingBots.External.CheckIfInventoryFull(receiver))
            {
                return false;
            }

            foreach (BotOwner giver in members.OrderBy(m => (m.Position - receiver.Position).sqrMagnitude))
            {
                if (giver == receiver || OnCooldown(giver) || InHandover(giver) || !LootingBots.External.CheckIfInventoryFull(giver))
                {
                    continue;
                }

                // From here on a full bag stands next to a squadmate with room: say why nothing moved
                if ((giver.Position - receiver.Position).sqrMagnitude > Range * Range)
                {
                    Refuse(giver, "full bag", "too far from a mate with room", $"has a full bag - {receiver.Profile.Nickname} has room but is {Vector3.Distance(giver.Position, receiver.Position):0} m away");
                    continue;
                }

                string busy = Busy(giver, false, false, false) ?? Busy(receiver, false, true, false);
                if (busy != null)
                {
                    Refuse(giver, "full bag", "busy", $"has a full bag, {receiver.Profile.Nickname} has room - no handover: one of them is {busy}");
                    continue;
                }

                Item item = SpareLoot(giver, receiver);
                if (item == null)
                {
                    Refuse(giver, "full bag", "nothing worth it fits", $"has a full bag - nothing of {MinLoot / 1000f:0}k+ in it fits {receiver.Profile.Nickname}'s bag");
                    continue;
                }

                if (Start(giver, receiver, item, "loot", "loot"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>What the bot needs that its own kit cannot give it. Urgent: heavy bleeding or under half health.</summary>
        private static Need MedNeed(BotOwner bot, float hp, out bool urgent)
        {
            urgent = false;
            BotMedecine med = bot.Medecine;
            if (med?.FirstAid == null || med.Using || !bot.HealthController.IsAlive)
            {
                return Need.None;
            }

            List<Meds> own = Carried(bot, true).OfType<Meds>().ToList();
            bool bleeding = med.FirstAid.IsBleeding;
            bool heavy = bot.HealthController.FindExistingEffect<EFT.HealthSystem.IHeavyBleeding>() != null;
            bleeding = bleeding || heavy || bot.HealthController.FindExistingEffect<EFT.HealthSystem.ILightBleeding>() != null;
            if (bleeding && !own.Any(m => m is MedKit && StopsBleeding(m, heavy)))
            {
                urgent = heavy || hp < 0.5f;
                return Need.StopBleeding;
            }

            // A leg it can only walk on, and nothing of its own for it (LegCare would have used it)
            if (Settings.TreatLegs.Value && Bots.LegCare.Limps(bot.GetPlayer) && !Bots.LegCare.Plan(bot, out _, out _))
            {
                return Need.Leg;
            }

            if ((hp < 0.85f || med.FirstAid.Damaged) && !own.Any(m => m is MedKit))
            {
                urgent = hp < 0.5f;
                return Need.Medkit;
            }

            // A surgical kit in the secure container is no use to it (MedsCheck: surgery only with a kit from the bag)
            if (med.SurgicalKit != null && med.SurgicalKit.Damaged
                && !(Settings.SurgeryFromBag.Value ? Carried(bot, false).OfType<Meds>() : own).Any(FixesLimb))
            {
                return Need.Surgery;
            }

            return Need.None;
        }

        private static string Describe(Need need)
        {
            switch (need)
            {
                case Need.StopBleeding: return "a kit that stops bleeding";
                case Need.Leg: return "a splint or a painkiller";
                case Need.Surgery: return "a surgical kit";
                default: return "a medkit";
            }
        }

        // With enough left in it to do it (MedsCheck): a nearly empty kit does not stop a bleed
        private static bool StopsBleeding(Meds m, bool heavy) =>
            MedsCheck.CanTreat(m, EDamageEffectType.HeavyBleeding) || (!heavy && MedsCheck.CanTreat(m, EDamageEffectType.LightBleeding));

        private static bool ForLeg(Meds m) => MedsCheck.CanTreat(m, EDamageEffectType.Fracture) || MedsCheck.CanTreat(m, EDamageEffectType.Pain);

        private static bool FixesLimb(Meds m) => MedsCheck.CanTreat(m, EDamageEffectType.DestroyedPart);

        private static bool Fits(Meds m, Need need)
        {
            switch (need)
            {
                case Need.StopBleeding: return m is MedKit && StopsBleeding(m, false);
                case Need.Leg: return ForLeg(m);
                case Need.Surgery: return FixesLimb(m);
                default: return m is MedKit;
            }
        }

        /// <summary>
        /// The item of the kind the receiver needs that the giver can spare. He keeps one of that kind for himself (secured container counted, never given
        /// from it) - except when the squadmate's need is urgent and the giver is healthy and not bleeding: then he gives his last.
        /// </summary>
        private static Item SpareMeds(BotOwner giver, Need need, bool urgent, out string why)
        {
            List<Meds> givable = Carried(giver, false).OfType<Meds>().Where(m => !m.QuestItem && Fits(m, need)).ToList();
            int total = Carried(giver, true).OfType<Meds>().Count(m => Fits(m, need));
            if (givable.Count == 0)
            {
                why = total > 0 ? "keeps his only one in the secure container" : $"has no {Describe(need).Replace("a ", "")}";
                return null;
            }

            float hp = giver.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            bool healthy = hp >= 0.9f && giver.Medecine?.FirstAid?.IsBleeding != true && giver.Medecine?.SurgicalKit?.Damaged != true;

            // A splint or a painkiller is given even as the last one by a squadmate whose own legs are fine: a mate who can only walk slows the squad down
            bool legsFine = need == Need.Leg && !Bots.LegCare.Limps(giver.GetPlayer);
            if (total < 2 && !(urgent && healthy) && !legsFine)
            {
                why = "keeps his only one";
                return null;
            }

            why = null;

            // A badly hurt mate gets the biggest kit, a lighter wound the smallest that does the job
            float Resource(Meds m) => m.MedKitComponent?.HpResource ?? 0f;
            return urgent ? givable.OrderByDescending(Resource).First() : givable.OrderBy(Resource).First();
        }

        /// <summary>
        /// Why the bot cannot take part in a handover now, or null. A wound that cannot wait (heavy bleeding, under half health) is treated even with an enemy
        /// known somewhere about, as long as nobody is on the squad right now; a giver or receiver who is only looting stops for it.
        /// </summary>
        private static string Busy(BotOwner bot, bool urgent, bool receiver, bool meds)
        {
            if (bot.IsDead || bot.BotState != EBotState.Active)
            {
                return "not active";
            }

            if (TacticalTasks.TryGet(bot, out TacticalTask t) && TacticalTasks.Rank(t.Kind) >= TacticalTasks.Rank(TaskKind.Relocate))
            {
                return $"busy ({t.Kind})";
            }

            // Meds for a mate wait for a lull, not for SAIN to forget every enemy (it remembers one for minutes after the shooting)
            if (urgent ? !QuietEnough(bot, 15f, 40f) : meds ? !QuietEnough(bot, 30f, 60f) : !TacticalTasks.NoThreat(bot))
            {
                return "in a fight";
            }

            if (!receiver && bot.Medecine?.Using == true)
            {
                return "patching himself up";
            }

            // A giver about to treat himself would leave in the middle of it (HealLayer cancels his errand)
            if (!receiver && ((bot.Medecine?.FirstAid?.Have2Do == true && bot.Medecine.FirstAid.HaveSmth2Use) || Bots.LegCare.Plan(bot, out _, out _)))
            {
                return "about to patch himself up";
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            if (!urgent && !meds && brain != null && brain.LootTaskRunning)
            {
                return "looting";
            }

            return null;
        }

        /// <summary>Nobody on the bot right now: no enemy in sight or seen within <paramref name="seen"/> s closer than <paramref name="near"/> m, not under fire, not hit for 8 s.</summary>
        private static bool QuietEnough(BotOwner bot, float seen, float near)
        {
            if (bot.Memory?.IsUnderFire == true || HitReaction.SinceHit(bot) < 8f)
            {
                return false;
            }

            var enemy = SainBot.Of(bot)?.GoalEnemy;
            return enemy == null || (!enemy.IsVisible && enemy.TimeSinceSeen > seen && enemy.RealDistance > near);
        }

        /// <summary>A squadmate is on his way to this bot with something for it (HealLayer lets it treat itself without ending the scene).</summary>
        public static bool Receiving(BotOwner bot) => bot != null && _active.Any(h => !h.Done && h.Receiver == bot);

        /// <summary>The bot gives or receives in a handover scene right now (the room sweep hands it nothing meanwhile).</summary>
        public static bool InScene(BotOwner bot) => bot != null && InHandover(bot);

        private static void StopLooting(BotOwner bot)
        {
            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            if (brain != null && brain.LootTaskRunning)
            {
                LootingBots.External.PreventBotFromLooting(bot, 15f);
            }
        }

        private static bool InHandover(BotOwner bot) => _active.Any(h => !h.Done && (h.Giver == bot || h.Receiver == bot));

        private static bool OnCooldown(BotOwner bot) => _cooldown.TryGetValue(bot.ProfileId, out float until) && Time.time < until;

        /// <summary>Counts a reason nothing was handed over, and writes it to the trace at most once in 45 s per bot and reason.</summary>
        private static void Refuse(BotOwner bot, string need, string reason, string line)
        {
            string key = need + ": " + reason;
            string noteKey = bot.ProfileId + "|" + key;
            if (_noteAt.TryGetValue(noteKey, out float at) && Time.time < at)
            {
                return;
            }

            _noteAt[noteKey] = Time.time + 45f;
            _refusals[key] = _refusals.TryGetValue(key, out int n) ? n + 1 : 1;
            Log.Detail("squad", () => $"{bot.Profile.Nickname} {line}");
        }

        private static IEnumerable<Item> Carried(BotOwner bot, bool withSecured)
        {
            InventoryEquipment eq = bot.GetPlayer?.InventoryController?.Inventory?.Equipment;
            if (eq == null)
            {
                yield break;
            }

            List<EquipmentSlot> slots = new List<EquipmentSlot> { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets };
            if (withSecured)
            {
                slots.Add(EquipmentSlot.SecuredContainer);
            }

            foreach (EquipmentSlot slot in slots)
            {
                Item container = eq.GetSlot(slot)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                foreach (Item it in container.GetAllItems())
                {
                    if (it != container)
                    {
                        yield return it;
                    }
                }
            }
        }

        /// <summary>The most valuable spare item of a full bag that fits the receiver.</summary>
        private static Item SpareLoot(BotOwner giver, BotOwner receiver)
        {
            InventoryController recv = receiver.GetPlayer?.InventoryController;
            Item backpack = giver.GetPlayer?.InventoryController?.Inventory?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem;
            if (recv == null || backpack == null)
            {
                return null;
            }

            return backpack.GetAllItems()
                .Where(it => it != backpack && it.Parent?.Container?.ParentItem == backpack && !it.QuestItem && it.GetItemComponent<KeyComponent>() == null && !(it is Meds))
                .Select(it => (it, value: LootBrain.Price(it)))
                .Where(x => x.value >= MinLoot)
                .OrderByDescending(x => x.value)
                .Select(x => x.it)
                .FirstOrDefault(it => recv.FindGridToPickUp(it) != null);
        }

        /// <summary>
        /// SquadKeys: a key a squadmate found goes to the leader (the same walk-up, throw and pick-up as for meds and loot). False when it is not the moment:
        /// too far, one of them busy or in a handover, the key not in reach (in the secure container it cannot be thrown).
        /// </summary>
        public static bool HandOverKey(BotOwner giver, BotOwner receiver, string keyId)
        {
            if (giver == null || receiver == null || giver.IsDead || receiver.IsDead || OnCooldown(giver) || OnCooldown(receiver) || InHandover(giver) || InHandover(receiver))
            {
                return false;
            }

            if ((giver.Position - receiver.Position).sqrMagnitude > Range * Range)
            {
                return false;
            }

            if (Busy(giver, false, false, false) != null || Busy(receiver, false, true, false) != null)
            {
                return false;
            }

            Item key = giver.GetPlayer?.InventoryController?.Inventory?.Equipment?.GetItemComponentsInChildren<KeyComponent>(false).FirstOrDefault(k => k.Template.KeyId == keyId)?.Item;
            return key != null && Start(giver, receiver, key, "key", "key");
        }
        // ---------------- the scene ----------------

        private static bool Start(BotOwner giver, BotOwner receiver, Item item, string what, string need)
        {
            if (Transactions(giver) == null || Transactions(receiver) == null)
            {
                return false;
            }

            float dist = Vector3.Distance(giver.Position, receiver.Position);
            Handover h = new Handover
            {
                Giver = giver, Receiver = receiver, Item = item, What = what, Until = Time.time + Mathf.Clamp(10f + dist * 0.8f, MinTime, MaxTime),
                ReceiverAt = receiver.Position,
            };
            bool giverSet = TacticalTasks.Set(giver, new TacticalTask
            {
                Kind = TaskKind.Handover,
                StandAt = SpotNear(receiver.Position, giver.Position),
                LookAt = receiver.Position,
                Until = h.Until,
                Speed = 1f,
                MovePose = 1f,
                Cleanup = () => End(h, false, "the giver was interrupted"),
            });
            if (!giverSet)
            {
                return false;
            }

            bool receiverSet = TacticalTasks.Set(receiver, new TacticalTask
            {
                Kind = TaskKind.Handover,
                StandAt = null,
                LookAt = giver.Position,
                Until = h.Until,
                Cleanup = () => End(h, false, "the receiver was interrupted"),
            });
            if (!receiverSet)
            {
                TacticalTasks.Cancel(giver, "handover not possible");
                return false;
            }

            _active.Add(h);
            _cooldown[giver.ProfileId] = Time.time + 30f;
            _cooldown[receiver.ProfileId] = Time.time + 20f;
            SquadBrain.Say(receiver, what == "meds" ? EPhraseTrigger.NeedMedkit : EPhraseTrigger.Ready, 0.6f);
            Log.Detail("squad", $"{giver.Profile.Nickname} brings {item.ShortName.Localized()} ({need}) to {receiver.Profile.Nickname}, " +
                                $"{Vector3.Distance(giver.Position, receiver.Position):0} m away");
            return true;
        }

        private static void Advance(Handover h)
        {
            BotOwner g = h.Giver, r = h.Receiver;
            if (g == null || r == null || g.IsDead || r.IsDead)
            {
                End(h, false, "a member is gone");
                return;
            }

            if (Time.time > h.Until)
            {
                End(h, false, $"took too long ({h.Phase})");
                return;
            }

            // The receiver keeps facing the giver; the giver keeps aiming his walk at the receiver
            if (TacticalTasks.TryGet(r, out TacticalTask rt) && rt.Kind == TaskKind.Handover)
            {
                rt.LookAt = g.Position;
            }

            switch (h.Phase)
            {
                case Phase.Approach:
                    if ((g.Position - r.Position).sqrMagnitude > Reach * Reach)
                    {
                        // The receiver moved (a step aside, a turn of his own): the giver's spot goes with him
                        if ((r.Position - h.ReceiverAt).sqrMagnitude > 1.5f * 1.5f && TacticalTasks.TryGet(g, out TacticalTask gt) && gt.Kind == TaskKind.Handover)
                        {
                            h.ReceiverAt = r.Position;
                            gt.StandAt = SpotNear(r.Position, g.Position);
                            gt.Arrived = false;
                        }

                        return;
                    }

                    g.Steering.LookToPoint(r.Position + Vector3.up * 1.2f);
                    h.Job = Transactions(g)?.ThrowItemAsync(h.Item, CancellationToken.None);
                    h.Phase = Phase.Throw;
                    SquadBrain.Say(g, EPhraseTrigger.GoLoot, 0.5f);
                    return;

                case Phase.Throw:
                    if (h.Job == null || !h.Job.IsCompleted)
                    {
                        return;
                    }

                    if (h.Job.Status != TaskStatus.RanToCompletion || !h.Job.Result)
                    {
                        End(h, false, "the throw failed");
                        return;
                    }

                    // The giver will not pick it back up
                    g.GetPlayer?.GetComponent<LootingBrain>()?.IgnoreLoot(h.Item.Id);
                    h.Phase = Phase.Pick;
                    h.PickAt = Time.time + 0.5f;
                    h.Job = null;
                    return;

                case Phase.Pick:
                    if (h.Job == null)
                    {
                        if (Time.time < h.PickAt)
                        {
                            return;
                        }

                        // Treating himself: he picks it up when the meds are put away (20 s more at most)
                        if (r.Medecine?.Using == true && h.WaitedForMeds < 20f)
                        {
                            h.WaitedForMeds += 0.25f;
                            Extend(h, 1f);
                            return;
                        }

                        r.Steering.LookToPoint(g.Position);
                        h.Job = Transactions(r)?.TryPickupItemAsync(h.Item, CancellationToken.None);
                        if (h.Job == null)
                        {
                            End(h, false, "the receiver cannot pick up");
                        }

                        return;
                    }

                    if (!h.Job.IsCompleted)
                    {
                        return;
                    }

                    bool ok = h.Job.Status == TaskStatus.RanToCompletion && h.Job.Result;
                    if (ok)
                    {
                        r.GetPlayer?.GetComponent<LootingBrain>()?.UpdateGridStats();
                        g.GetPlayer?.GetComponent<LootingBrain>()?.UpdateGridStats();
                        if (h.What == "meds")
                        {
                            // EFT re-reads a bot's meds only every 20 s (BotMedecine.ManualUpdate): without this the kit sat unused that long
                            RefreshMeds(r);
                            RefreshMeds(g);
                        }

                        SquadBrain.Say(r, EPhraseTrigger.Roger, 0.5f);
                    }
                    else
                    {
                        // Lying on the floor next to him: his own loot scan may still take it
                        RoomSweep.ForceScan(r);
                    }

                    End(h, ok, ok ? "picked up" : "could not pick it up");
                    return;
            }
        }

        /// <summary>The scene gets at least <paramref name="seconds"/> more - and the errands of both with it (their end would end the scene).</summary>
        private static void Extend(Handover h, float seconds)
        {
            float until = Time.time + seconds;
            if (h.Until >= until)
            {
                return;
            }

            h.Until = until;
            foreach (BotOwner b in new[] { h.Giver, h.Receiver })
            {
                if (b != null && TacticalTasks.TryGet(b, out TacticalTask t) && t.Kind == TaskKind.Handover)
                {
                    t.Until = until;
                }
            }
        }

        private static void RefreshMeds(BotOwner bot)
        {
            try
            {
                if (bot?.Medecine != null && !bot.Medecine.Using)
                {
                    bot.Medecine.RefreshCurMeds();
                    bot.Medecine.FirstAid?.CheckParts();
                    bot.Medecine.SurgicalKit?.FindDamagedPart();
                }
            }
            catch (Exception e)
            {
                Log.Debug("meds refresh after a handover failed: " + e.Message);
            }
        }

        private static void End(Handover h, bool ok, string why)
        {
            if (h.Done)
            {
                return;
            }

            h.Done = true;
            if (ok)
            {
                if (h.What == "meds")
                {
                    Meds++;
                }
                else
                {
                    Loot++;
                }
            }
            else
            {
                Failed++;
            }

            if (h.Giver != null && h.Receiver != null)
            {
                string pair = h.Giver.ProfileId + ">" + h.Receiver.ProfileId;
                if (ok)
                {
                    _pairFails.Remove(pair);
                }
                else
                {
                    int n = _pairFails.TryGetValue(pair, out int f) ? f + 1 : 1;
                    _pairFails[pair] = n;
                    if (n >= 2)
                    {
                        // Two failures in a row: these two leave it for a few minutes (each try had stopped both of them where they were)
                        _pairFails.Remove(pair);
                        _cooldown[h.Giver.ProfileId] = Time.time + 180f;
                        _cooldown[h.Receiver.ProfileId] = Time.time + 180f;
                        Log.Detail("squad", $"{h.Giver.Profile?.Nickname} and {h.Receiver.Profile?.Nickname} failed to hand something over twice in a row - leave it for 3 min");
                    }
                }
            }

            string item = h.Item?.ShortName.Localized() ?? "?";
            string line = $"LR Squad: {h.Giver?.Profile?.Nickname} hands {item} ({h.What}) to {h.Receiver?.Profile?.Nickname} - {why}";
            if (ok)
            {
                Log.Info(line);
            }
            else
            {
                Log.Detail("squad", line);
            }

            // Release the other one as well (its cleanup comes back here and finds the scene done)
            foreach (BotOwner b in new[] { h.Giver, h.Receiver })
            {
                if (b != null && TacticalTasks.TryGet(b, out TacticalTask t) && t.Kind == TaskKind.Handover)
                {
                    if (ok)
                    {
                        TacticalTasks.Finish(b);
                    }
                    else
                    {
                        TacticalTasks.Cancel(b, why);
                    }
                }
            }
        }

        private static LootingTransactionController Transactions(BotOwner bot)
        {
            LootingBrain brain = bot?.GetPlayer?.GetComponent<LootingBrain>();
            return brain?.InventoryController == null ? null : TransactionField?.GetValue(brain.InventoryController) as LootingTransactionController;
        }

        /// <summary>A navmesh spot 1.3 m in front of the receiver on the giver's side.</summary>
        private static Vector3? SpotNear(Vector3 receiver, Vector3 giver)
        {
            Vector3 dir = giver - receiver;
            dir.y = 0f;
            Vector3 want = receiver + (dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward) * 1.3f;
            return NavMesh.SamplePosition(want, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) ? hit.position : receiver;
        }
    }
}
