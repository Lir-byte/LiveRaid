using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// A fallen squadmate's gear is not left lying (F12 -> 8 "Recover the loot of a fallen squadmate"). Online, once it is quiet a teammate goes to the body,
    /// throws his own junk into a bush nearby to make room and carries out as much of his friend's loot as he can.
    ///
    ///  - Some seconds after a squadmate's death, when it is calm and nobody is about (no danger, see LootDanger), the nearest surviving mate within 150 m
    ///    takes the job.
    ///  - If the friend's bag, rig and pockets hold more worth taking than the mate has room for, the mate first walks to a hidden spot 6-22 m from the
    ///    body (a bush, a corner the surroundings do not see) and drops his own cheap things there - each at least 1.4 times poorer per cell than the friend's middle thing (0.9.8; rounds and magazines for no gun of his go too),
    ///    things, none worth 25k or more, together at most 60 % of what he will take. The squad leaves that stash alone.
    ///  - Then he goes straight to the body (LootingBots' priority corpse) and loots it; while he loots that body he drops cheap stuff for the friend's things
    ///    more readily than usual (MakeRoom).
    ///  - A fight breaks it off; it is tried again later (up to 3 times, for 8 minutes after the death).
    ///  - 0.9.9: nobody goes while the friend's killer (or one of his squad) is still within 120 m of the body, a mate on its way out of the raid goes only
    ///    to a body within 60 m, a badly hurt one (under 45 %) only within 40 m (raid 2026-10-31 00:34: Crimson, beaten off and at 48 %, was handed his
    ///    leader's body 148 m away on his way out - right where the squad that had killed him still was).
    ///  - With "Carry out the whole kit of a fallen squadmate" (MateKit) the mate goes straight to the body and swaps its own finds there for the friend's gear
    ///    and kit; what of the friend's still does not fit it hides in a bush nearby by chance, otherwise it heads for an exit soon after with what it carries.
    /// </summary>
    public static class MateRecovery
    {
        private const float Delay = 6f;
        // Fights after a squadmate's death last minutes (raid 2026-10-26 18:34: 4 min) - the squad still comes for the body afterwards
        private const float GiveUpAfter = 900f;
        private const float MaxRange = 150f;
        private const float MaxOwnItemPrice = 25000f;

        private enum Phase
        {
            Waiting,
            Stash,
            Body,
            Hide,
            Done,
        }

        private class Fallen
        {
            public string BodyId;
            public string EquipmentId;
            public string Name;
            public Vector3 Pos;
            public float At;
            public HashSet<string> Squad;
            public InventoryEquipment Equipment;
            public Phase Phase;
            public string RecoverId;
            public int Attempts;
            public List<Item> Drops;
            public bool Dropping;
            public bool Dropped;
            public float PhaseSince;
            public HashSet<string> Given = new HashSet<string>();
            public string KillerId;
            public string DangerNoted;
            public float SmokedUntil;
            public bool? Hidden;
        }

        private static readonly List<Fallen> _fallen = new List<Fallen>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Recovered { get; private set; }
        public static int Stashes { get; private set; }
        public static int GaveUp { get; private set; }

        public static string Summary => $"fallen squadmates looted by their squad {Recovered} (stashes made for room {Stashes}, given up {GaveUp})";

        public static void Clear()
        {
            _fallen.Clear();
            _next = 0f;
            Recovered = Stashes = GaveUp = 0;
        }

        public static void OnKilled(Player victim)
        {
            if (!Settings.RecoverFallenMate.Value || victim == null)
            {
                return;
            }

            BotOwner bot = victim.AIData?.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav))
            {
                return;
            }

            HashSet<string> squad = LootBrain.SquadIds(bot);
            squad.Remove(victim.ProfileId);
            if (squad.Count == 0)
            {
                return;
            }

            _fallen.Add(new Fallen
            {
                BodyId = victim.ProfileId,
                EquipmentId = victim.Equipment?.Id,
                Name = victim.Profile?.Nickname,
                Pos = victim.Position,
                At = Time.time,
                Squad = squad,
                Equipment = victim.Equipment,
                KillerId = WorldEvents.TryGetKill(victim.ProfileId, out KillEvent k) ? k.KillerId : null,
            });
        }

        /// <summary>The gear (equipment id) of a fallen squadmate somebody is to recover.</summary>
        public static bool IsFallenMate(string equipmentId) => equipmentId != null && _fallen.Any(f => f.EquipmentId == equipmentId);

        private const float KillerNear = 120f;

        /// <summary>The friend's killer, or one of his squad, is still close to the body: nobody walks into them for the loot.</summary>
        private static string BodyDanger(GameWorld world, Fallen f) => BodyDanger(world, f, out _);

        private static string BodyDanger(GameWorld world, Fallen f, out Player near)
        {
            near = null;
            if (f.KillerId == null && WorldEvents.TryGetKill(f.BodyId, out KillEvent kill))
            {
                f.KillerId = kill.KillerId;
            }

            if (f.KillerId == null)
            {
                return null;
            }

            HashSet<string> theirs = new HashSet<string>(SquadTies.MatesOf(f.KillerId)) { f.KillerId };
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p != null && theirs.Contains(p.ProfileId) && !f.Squad.Contains(p.ProfileId) && (p.Position - f.Pos).sqrMagnitude < KillerNear * KillerNear)
                {
                    near = p;
                    return $"{(p.ProfileId == f.KillerId ? "his killer" : "one of his killer's squad")} {p.Profile?.Nickname} is still {Vector3.Distance(p.Position, f.Pos):0} m from it";
                }
            }

            return null;
        }

        /// <summary>This mate should not go for the body now: on its way out and the body is far, or too badly hurt to walk far.</summary>
        private static bool Unfit(BotOwner m, float dist) =>
            (dist > 60f && ExtractPlanner.IsLeaving(m)) || (dist > 40f && m.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized < 0.45f);

        /// <summary>The bot is on its way to (or at) the body of a fallen squadmate it recovers: LootBrain loots it without weighing.</summary>
        public static bool IsRecovering(BotOwner bot, string bodyId) =>
            bot != null && _fallen.Any(f => f.Phase == Phase.Body && f.RecoverId == bot.ProfileId && f.BodyId == bodyId);

        /// <summary>The bot is going through its fallen squadmate's body right now (MakeRoom drops more readily for it).</summary>
        public static bool IsLootingMate(BotOwner bot)
        {
            if (bot == null || _fallen.Count == 0)
            {
                return false;
            }

            // A bot whose player object is already destroyed (the raid is ending) is not null for "?." - Unity's own comparison tells
            Player player = bot.GetPlayer;
            if (player == null)
            {
                return false;
            }

            LootingBrain brain = player.GetComponent<LootingBrain>();
            if (!(brain?.ActiveLoot is EFT.Interactive.Corpse corpse))
            {
                return false;
            }

            // The one sent for it, or any other squadmate that came to that body (a body job)
            return _fallen.Any(f => f.BodyId == corpse.PlayerProfileID && (f.RecoverId == bot.ProfileId || (f.Squad != null && f.Squad.Contains(bot.ProfileId))));
        }

        public static void Tick()
        {
            if (!Settings.RecoverFallenMate.Value || _fallen.Count == 0 || Time.time < _next)
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
            foreach (Fallen f in _fallen)
            {
                if (f.Phase == Phase.Done)
                {
                    continue;
                }

                // Every living squadmate within reach has the body as a job of its own (LootJobs), not only the one sent: if he cannot, another goes when
                // quiet (raid 2026-10-26 18:34: only Spock was given Rhyufer's body; when Spock fell too, Waffle had it as a stranger's body that ran out)
                // 0.9.9: not while his killers are about - the jobs and the recovery wait for them to go (or for the give-up time)
                Player killer = null;
                string danger = now < f.SmokedUntil ? null : BodyDanger(world, f, out killer);

                // A smoke between the body and his killers lets the squad get to him (FireTricks): a squadmate near the body with one throws it
                if (danger != null && killer != null && Settings.SmokeScreens.Value)
                {
                    foreach (Player p in world.AllAlivePlayersList)
                    {
                        BotOwner m = p?.AIData?.BotOwner;
                        if (m != null && f.Squad.Contains(m.ProfileId) && (m.Position - f.Pos).sqrMagnitude < 40f * 40f
                            && FireTricks.Smoke(m, killer.Position, $"to get to {f.Name}'s body with {killer.Profile?.Nickname} {Vector3.Distance(killer.Position, f.Pos):0} m from it"))
                        {
                            f.SmokedUntil = now + 20f;
                            danger = null;
                            break;
                        }
                    }
                }

                if (danger != null)
                {
                    string who = danger.Substring(0, danger.IndexOf(" is still", StringComparison.Ordinal));
                    if (f.DangerNoted != who)
                    {
                        f.DangerNoted = who;
                        Log.Detail("squad", $"{f.Name}'s body waits: {danger}");
                    }

                    if (f.Phase == Phase.Waiting)
                    {
                        if (now - f.At > GiveUpAfter)
                        {
                            End(f, "too long");
                        }

                        continue;
                    }
                }
                else
                {
                    GiveJobs(world, f);
                }

                if (now - f.At > GiveUpAfter || f.Attempts > 3)
                {
                    End(f, "too long");
                    continue;
                }

                if (now - f.At < Delay)
                {
                    continue;
                }

                BotOwner bot = f.RecoverId != null ? world.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == f.RecoverId)?.AIData?.BotOwner : null;
                switch (f.Phase)
                {
                    case Phase.Waiting:
                        Assign(world, f);
                        break;
                    case Phase.Stash:
                        StashStep(f, bot, now);
                        break;
                    case Phase.Body:
                        BodyStep(f, bot, now);
                        break;
                    case Phase.Hide:
                        HideStep(f, bot, now);
                        break;
                }
            }

            _fallen.RemoveAll(f => f.Phase == Phase.Done && now - f.At > 60f);
        }

        private static void GiveJobs(GameWorld world, Fallen f)
        {
            if (!WorldEvents.TryGetKill(f.BodyId, out KillEvent kill))
            {
                return;
            }

            foreach (Player p in world.AllAlivePlayersList)
            {
                BotOwner m = p?.AIData?.BotOwner;
                if (m != null && !m.IsDead && f.Squad.Contains(p.ProfileId) && (m.Position - f.Pos).sqrMagnitude < MaxRange * MaxRange
                    && !Unfit(m, Vector3.Distance(m.Position, f.Pos)) && f.Given.Add(p.ProfileId))
                {
                    LootJobs.Add(m, kill, LootJobs.Kind.Mate);
                }
            }
        }

        private static void Assign(GameWorld world, Fallen f)
        {
            BotOwner best = null;
            float bestDist = MaxRange;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p == null || !f.Squad.Contains(p.ProfileId))
                {
                    continue;
                }

                BotOwner m = p.AIData?.BotOwner;
                if (m == null || m.IsDead || Spectator.TakeOver.IsTaken(m) || !TacticalTasks.BotIsCalm(m) || HitReaction.InRealFight(m, 15f)
                    || LootDanger.Why(m) != null || TacticalTasks.TryGet(m, out _) || _fallen.Any(o => o != f && o.Phase != Phase.Done && o.RecoverId == m.ProfileId))
                {
                    continue;
                }

                float d = Vector3.Distance(m.Position, f.Pos);
                if (d < bestDist && !Unfit(m, d))
                {
                    best = m;
                    bestDist = d;
                }
            }

            if (best == null)
            {
                return;
            }

            f.RecoverId = best.ProfileId;
            f.Attempts++;
            // The whole kit (MateKit): straight to the body. What it replaces of its own is dropped there (LootingBots' swaps, MakeRoom); what of the
            // friend's is still left after it is hidden in a bush by chance - otherwise it carries out what fits and heads for an exit soon (AfterBody)
            if (MateKit.On)
            {
                GoToBody(f, best);
                return;
            }

            f.Drops = StashPlan(best, f, out float dropValue, out float takeValue, out string noStash);
            Vector3? spot = f.Drops != null ? FindStash(best, f.Pos) : null;
            if (f.Drops == null || spot == null)
            {
                if (noStash != null || f.Drops != null)
                {
                    Log.Detail("squad", $"{best.Profile.Nickname} leaves no stash before {f.Name}'s body - {noStash ?? "no hidden spot found within 22 m of it"}");
                }

                GoToBody(f, best);
                return;
            }

            f.Phase = Phase.Stash;
            f.PhaseSince = Time.time;
            f.Dropping = f.Dropped = false;
            SendTo(best, spot.Value, f.Pos);
            Log.Info($"LR Squad: {best.Profile.Nickname} goes to recover {f.Name}'s loot ({bestDist:0} m) - first leaves {f.Drops.Count} cheap thing(s) " +
                     $"(~{dropValue / 1000f:0}k) in a hidden spot to make room for ~{takeValue / 1000f:0}k");
        }

        private static void SendTo(BotOwner bot, Vector3 spot, Vector3 lookAt)
        {
            TacticalTasks.Set(bot, new TacticalTask
            {
                Kind = TaskKind.Recover,
                StandAt = spot,
                LookAt = lookAt,
                Until = Time.time + 60f,
                Speed = 1f,
                Sprint = Vector3.Distance(bot.Position, spot) > 25f,
                SprintBeyond = 0f,
                MovePose = 1f,
            });
        }

        /// <summary>
        /// The whole kit, the body searched: by chance (F12) what of the friend's did not fit - his backpack, rig, guns, armour still on him, the dear things
        /// in his pockets - is carried off to a bush 6-22 m away and hidden there; otherwise the mate heads for an exit soon with what it carries.
        /// </summary>
        private static void AfterBody(Fallen f, BotOwner bot)
        {
            if (f.Hidden == null)
            {
                f.Hidden = MateKit.DrawHidden();
            }

            List<Item> left = f.Hidden.Value ? MateKit.Leftovers(f.Equipment) : null;
            Vector3? spot = left != null && left.Count > 0 ? FindStash(bot, f.Pos) : null;
            if (spot == null)
            {
                string why = !f.Hidden.Value ? "does not stop to hide the rest" : left == null || left.Count == 0 ? "nothing worth hiding is left on him" : "no hidden spot within 22 m";
                MateKit.Noted(false);
                ExtractPlanner.LeaveSoon(bot, $"carries out {f.Name}'s kit ({why})");
                End(f, null);
                return;
            }

            f.Drops = left;
            f.Phase = Phase.Hide;
            f.PhaseSince = Time.time;
            f.Dropping = f.Dropped = false;
            SendTo(bot, spot.Value, f.Pos);
            MateKit.Noted(true);
            Log.Info($"LR Squad: {bot.Profile.Nickname} carries what of {f.Name}'s did not fit ({left.Count} thing(s), ~{left.Sum(LootBrain.Price) / 1000f:0}k) off to a hidden spot " +
                     $"{Vector3.Distance(f.Pos, spot.Value):0} m from his body");
        }

        private static void HideStep(Fallen f, BotOwner bot, float now)
        {
            if (bot == null || bot.IsDead || !TacticalTasks.TryGet(bot, out TacticalTask task) || task.Kind != TaskKind.Recover)
            {
                // Dead, or a fight took it away: the rest stays on the body
                if (!f.Dropping)
                {
                    End(f, null);
                }

                return;
            }

            if (task.Arrived && !f.Dropping)
            {
                f.Dropping = true;
                task.Until = Mathf.Max(task.Until, now + 30f);
                DropAll(bot, f);
            }

            if (f.Dropped || now - f.PhaseSince > 90f)
            {
                TacticalTasks.Finish(bot);
                End(f, null);
            }
        }

        private static void StashStep(Fallen f, BotOwner bot, float now)
        {
            if (bot == null || bot.IsDead)
            {
                Restart(f);
                return;
            }

            if (!TacticalTasks.TryGet(bot, out TacticalTask task) || task.Kind != TaskKind.Recover)
            {
                // A fight or a squad call took it away: later, maybe by someone else
                if (!f.Dropping)
                {
                    Restart(f);
                }

                return;
            }

            if (task.Arrived && !f.Dropping)
            {
                f.Dropping = true;
                task.Until = Mathf.Max(task.Until, now + 20f);
                DropAll(bot, f);
            }

            if (f.Dropped || now - f.PhaseSince > 75f)
            {
                TacticalTasks.Finish(bot);
                GoToBody(f, bot);
            }
        }

        private static void BodyStep(Fallen f, BotOwner bot, float now)
        {
            if (bot == null || bot.IsDead)
            {
                Restart(f);
                return;
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            if (brain != null && f.EquipmentId != null && brain.IsLootIgnored(f.EquipmentId))
            {
                Recovered++;
                Log.Info($"LR Squad: {bot.Profile.Nickname} has gone through {f.Name}'s body (~{LootingBots.External.GetNetLootValue(bot) / 1000f:0}k carried now)");
                if (MateKit.On)
                {
                    AfterBody(f, bot);
                    return;
                }

                End(f, null);
                return;
            }

            bool busy = brain != null && (brain.LootTaskRunning || brain.HasActiveLootable);
            if (!busy && now - f.PhaseSince > 12f && now - f.PhaseSince < 90f && TacticalTasks.NoThreat(bot) && LootDanger.Why(bot) == null)
            {
                // The scan picked something else or nothing: ask again for the body
                f.PhaseSince = now - 4f;
                Prioritize(bot, f);
            }

            if (now - f.PhaseSince > 90f)
            {
                Restart(f);
            }
        }

        private static void GoToBody(Fallen f, BotOwner bot)
        {
            f.Phase = Phase.Body;
            f.PhaseSince = Time.time;
            f.RecoverId = bot.ProfileId;
            Prioritize(bot, f);
            Log.Info($"LR Squad: {bot.Profile.Nickname} goes to {f.Name}'s body to carry out the loot ({Vector3.Distance(bot.Position, f.Pos):0} m)");
        }

        private static void Prioritize(BotOwner bot, Fallen f)
        {
            // The body is a job of the mate until searched: asked for again whenever a try is broken off (LootJobs)
            if (WorldEvents.TryGetKill(f.BodyId, out KillEvent kill))
            {
                LootJobs.Add(bot, kill, LootJobs.Kind.Mate);
            }

            RoomSweep.Release(bot);
            bot.GetPlayer?.GetComponent<LootFinder>()?.EnqueuePriorityCorpse(f.BodyId);
            RoomSweep.ForceScan(bot);
        }

        private static void Restart(Fallen f)
        {
            f.Phase = Phase.Waiting;
            f.RecoverId = null;
            f.Drops = null;
            f.Dropping = f.Dropped = false;
        }

        private static void End(Fallen f, string giveUp)
        {
            if (giveUp != null && f.Phase != Phase.Done)
            {
                GaveUp++;
                Log.Detail("squad", $"nobody recovered {f.Name}'s loot: {giveUp}");
            }

            f.Phase = Phase.Done;
        }

        /// <summary>
        /// The mate's own cheap things to leave behind so that the friend's worthwhile things fit, or null when nothing needs to (or may) go.
        /// </summary>
        private static List<Item> StashPlan(BotOwner bot, Fallen f, out float dropValue, out float takeValue, out string why)
        {
            dropValue = takeValue = 0f;
            why = null;
            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            InventoryEquipment own = bot.GetPlayer?.Equipment;
            if (brain == null || own == null || f.Equipment == null)
            {
                return null;
            }

            // What of the friend's is worth carrying out: what lies in his bag, rig and pockets - and, always, his guns and armour that the mate will not
            // put on himself (his own is as good): those go into the bag (LootRules lets LootingBots pick them up)
            var worth = Contents(f.Equipment).Where(c => c.value / c.cells >= 1500f).ToList();
            var gear = GearForTheBag(own, f.Equipment).ToList();
            worth.AddRange(gear);
            if (worth.Count == 0)
            {
                return null;
            }

            takeValue = worth.Sum(c => c.value);
            int need = Mathf.Min(40, worth.Sum(c => c.cells));
            int free = brain.Stats?.AvailableGridSpaces ?? 0;
            if ((takeValue < 15000f && gear.Count == 0) || free >= need)
            {
                return null;
            }

            // 0.9.8: its own things had to be worth under 60 % of the median per cell of the friend's - but LootingBots puts only things of 7k a cell and
            // more into a PMC's bag, the friend's as much as its own: not one stash in five raids. Now 1.4 times poorer per cell is enough, and rounds and
            // magazines for no gun it carries go like any junk (MakeRoom's rule)
            List<float> perCell = worth.Select(c => c.value / c.cells).OrderBy(v => v).ToList();
            float median = perCell[perCell.Count / 2];
            LootingInventoryController lic = brain.InventoryController;
            List<Item> drops = new List<Item>();
            int freed = 0;
            foreach (var c in Contents(own).Where(c => !MakeRoom.Keep(lic, c.item) && c.value < MaxOwnItemPrice && c.value / c.cells * 1.4f <= median)
                         .OrderBy(c => c.value / c.cells))
            {
                if (dropValue + c.value > takeValue * 0.6f)
                {
                    break;
                }

                drops.Add(c.item);
                freed += c.cells;
                dropValue += c.value;
                if (free + freed >= need)
                {
                    break;
                }
            }

            if (drops.Count == 0)
            {
                why = $"needs {need - free} more cells for ~{takeValue / 1000f:0}k, but nothing of its own is 1.4 times poorer per cell than the friend's things ({median / 1000f:0.#}k a cell)";
                return null;
            }

            return drops;
        }

        private static readonly EquipmentSlot[] GearSlots =
        {
            EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.ArmorVest, EquipmentSlot.Headwear,
        };

        /// <summary>The friend's guns and armour the mate keeps its own instead of (its own is worth as much): they have to fit in the bag.</summary>
        private static IEnumerable<(Item item, float value, int cells)> GearForTheBag(InventoryEquipment own, InventoryEquipment theirs)
        {
            foreach (EquipmentSlot slot in GearSlots)
            {
                Item his = theirs.GetSlot(slot)?.ContainedItem;
                if (his == null)
                {
                    continue;
                }

                Item mine = own.GetSlot(slot)?.ContainedItem;
                float hisValue = LootBrain.Price(his);
                if (mine == null || hisValue > LootBrain.Price(mine) * 1.15f)
                {
                    // Put on (an empty slot, or better than its own): no room needed
                    continue;
                }

                var size = his.CalculateCellSize();
                yield return (his, hisValue, Mathf.Max(1, size.X * size.Y));
            }
        }

        /// <summary>Is the item one of the friend's guns or pieces of armour (for LootRules)?</summary>
        public static bool IsGear(Item item) =>
            item is Weapon || item.GetItemComponent<ArmorComponent>() != null;

        /// <summary>Things lying in the bag, rig and pockets, with their price and size.</summary>
        private static IEnumerable<(Item item, float value, int cells)> Contents(InventoryEquipment eq)
        {
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Backpack, EquipmentSlot.TacticalVest, EquipmentSlot.Pockets })
            {
                Item container = eq.GetSlot(slot)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                foreach (Item child in container.GetAllItems())
                {
                    if (child == container || child.Parent?.Container?.ParentItem != container)
                    {
                        continue;
                    }

                    var size = child.CalculateCellSize();
                    yield return (child, LootBrain.Price(child), Mathf.Max(1, size.X * size.Y));
                }
            }
        }

        private static async void DropAll(BotOwner bot, Fallen f)
        {
            try
            {
                LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
                LootingTransactionController tc = brain?.InventoryController != null
                    ? Traverse.Create(brain.InventoryController).Field("_transactionController").GetValue<LootingTransactionController>()
                    : null;
                if (tc == null)
                {
                    return;
                }

                bool his = f.Phase == Phase.Hide;
                if (his && f.Equipment?.Owner != null)
                {
                    tc.SetRootItemOwner(f.Equipment.Owner);
                }

                int n = 0;
                float value = 0f;
                foreach (Item item in f.Drops)
                {
                    if (bot.IsDead || item.Parent == null)
                    {
                        break;
                    }

                    float v = LootBrain.Price(item);
                    if (!await tc.ThrowItemAsync(item, CancellationToken.None))
                    {
                        break;
                    }

                    n++;
                    value += v;
                    // The stash is the squad's: none of them picks it up again on the way
                    foreach (string id in f.Squad.Append(bot.ProfileId))
                    {
                        Player mate = Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id);
                        mate?.GetComponent<LootingBrain>()?.IgnoreLoot(item.Id);
                    }
                }

                brain.UpdateGridStats();
                if (n == 0 && his)
                {
                    Log.Detail("squad", $"{bot.Profile.Nickname} could not hide {f.Name}'s things - the game refused the throw from his body");
                }

                if (n > 0)
                {
                    Stashes++;
                    Vector3 at = bot.Position;
                    Log.Info(his
                        ? $"LR Squad: {bot.Profile.Nickname} hid {n} of {f.Name}'s thing(s) (~{value / 1000f:0}k) that did not fit in a hidden spot at ({at.x:0}, {at.y:0}, {at.z:0})"
                        : $"LR Squad: {bot.Profile.Nickname} left {n} thing(s) (~{value / 1000f:0}k) in a hidden spot at ({at.x:0}, {at.y:0}, {at.z:0}) to carry out {f.Name}'s loot");
                }
            }
            catch (Exception e)
            {
                Diag.Report("MateRecovery.Drop", e);
            }
            finally
            {
                f.Dropped = true;
            }
        }

        /// <summary>
        /// A reachable spot 6 m to F12's "How far to look for a bush" (35 m) from the body that its surroundings hardly see. A bush first: raid 2026-10-31 05:46 kikirio hid techy's things in the
        /// corner of a building - the walls scored higher than leaves. A spot with foliage round it (2 of 8 sides or more) wins whenever there is one;
        /// only without any bush near a corner or rubble outside does, and a spot under a roof only as the last choice.
        /// </summary>
        private static Vector3? FindStash(BotOwner bot, Vector3 body)
        {
            int mask = LayersMaskController.HighPolyWithTerrainMask | LayersMaskController.Foliage;
            int foliage = LayersMaskController.Foliage;
            NavMeshPath path = new NavMeshPath();
            Vector3? best = null;
            float bestScore = float.MinValue;
            bool bestBush = false;
            float reach = Mathf.Clamp(Settings.MateStashRadius.Value, 10f, 100f);
            for (int i = 0; i < 60; i++)
            {
                float angle = (float)_rng.NextDouble() * Mathf.PI * 2f;
                float r = 6f + (float)_rng.NextDouble() * (reach - 6f);
                Vector3 probe = body + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * r;
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - body.y) > 3f)
                {
                    continue;
                }

                Vector3 spot = hit.position;
                int enclosed = 0;
                int leaves = 0;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * 45f * Mathf.Deg2Rad;
                    if (Physics.Raycast(spot + Vector3.up * 0.6f, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)), out RaycastHit h, 2.2f, mask))
                    {
                        enclosed++;
                        if (((1 << h.collider.gameObject.layer) & foliage) != 0)
                        {
                            leaves++;
                        }
                    }
                }

                if (enclosed < 3)
                {
                    continue;
                }

                int hidden = 0;
                for (int k = 0; k < 6; k++)
                {
                    float a = k * 60f * Mathf.Deg2Rad;
                    Vector3 outside = spot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 18f + Vector3.up * 1.6f;
                    if (Physics.Linecast(outside, spot + Vector3.up * 0.4f, mask))
                    {
                        hidden++;
                    }
                }

                if (hidden < 3 || !NavMesh.CalculatePath(bot.Position, spot, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++)
                {
                    len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                }

                if (len > Mathf.Max(60f, reach * 3f))
                {
                    continue;
                }

                bool bush = leaves >= 2;
                bool roof = Physics.Raycast(spot + Vector3.up * 1.6f, Vector3.up, 6f, LayersMaskController.HighPolyWithTerrainMask);
                float score = enclosed / 8f * 0.5f + hidden / 6f + leaves * 0.6f - len * 0.004f - (roof ? 1.5f : 0f);
                if ((bush && !bestBush) || (bush == bestBush && score > bestScore))
                {
                    bestScore = score;
                    best = spot;
                    bestBush = bush;
                }
            }

            return best;
        }
    }
}

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// How a mate goes through its fallen squadmate's body (F12 -> 8 "Recover the loot of a fallen squadmate"). LootingBots puts on what is better than
    /// the bot's own and otherwise takes a thing into the bag only if it is "worth enough per cell" - guns and armour almost never are (a 40k vest is 4k a
    /// cell), so the friend's rifle was stripped of its parts and his armour left on him. On the friend's body:
    ///  - his guns and armour are always taken: put on when better (LootingBots' own swap), else into the bag (the stash plan made room for them);
    ///  - anything else worth 1000 per cell or more is taken - as much as fits.
    /// </summary>
    public static class LootRules
    {
        public static int GearTaken { get; private set; }

        public static void ClearCounters() => GearTaken = 0;

        public static void Took() => GearTaken++;

        public static bool OnMate(BotOwner bot) => Settings.RecoverFallenMate.Value && bot != null && MateRecovery.IsLootingMate(bot);
    }

    public class MatePickupPatch : SPT.Reflection.Patching.ModulePatch
    {
        protected override System.Reflection.MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.AllowedToPickup));

        [SPT.Reflection.Patching.PatchPostfix]
        protected static void PatchPostfix(Item lootItem, ref bool __result, BotOwner ____botOwner, LootingBrain ____lootingBrain)
        {
            if (lootItem == null)
            {
                return;
            }

            try
            {
                // Already in the bot's own bag (MateKit put the friend's backpack in whole): not to be moved again
                if (__result && MateKit.On && ReferenceEquals(lootItem.Owner, ____botOwner?.GetPlayer?.InventoryController))
                {
                    __result = false;
                    return;
                }

                if (__result)
                {
                    return;
                }

                if ((MateRecovery.IsGear(lootItem) || (MateKit.On && (lootItem is Backpack || lootItem is Headwear))) && ____lootingBrain != null
                    && ____lootingBrain.HasFreeSpace && LootRules.OnMate(____botOwner))
                {
                    __result = true;
                    LootRules.Took();
                    Log.Detail("loot", () => $"{____botOwner.Profile?.Nickname} takes its fallen squadmate's {lootItem.ShortName?.Localized()} into the bag");
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("MatePickupPatch", e);
            }
        }
    }

    public class MateValuePatch : SPT.Reflection.Patching.ModulePatch
    {
        protected override System.Reflection.MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LootingInventoryController), nameof(LootingInventoryController.IsValuableEnough), new[] { typeof(float) });

        [SPT.Reflection.Patching.PatchPostfix]
        protected static void PatchPostfix(float itemPrice, ref bool __result, BotOwner ____botOwner)
        {
            if (__result || itemPrice < 1000f)
            {
                return;
            }

            try
            {
                if (LootRules.OnMate(____botOwner))
                {
                    __result = true;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("MateValuePatch", e);
            }
        }
    }
}
