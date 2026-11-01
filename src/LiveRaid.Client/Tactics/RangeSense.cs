using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Fight at the range of the gun (F12 -> 9 "Fight at the range of the gun"). A shotgun, a pistol or an SMG against an enemy far beyond its reach, or a
    /// bolt-action rifle against one at arm's length, is no answer - and SAIN does not see it: its "move to engage" stops to shoot the moment the enemy is in
    /// view, and its "rush" turns into a strafing dogfight once he is in line of sight, so such a bot walked about aiming at someone it could not hurt.
    /// A player, in this order:
    ///  - takes the other gun if it reaches (a loaded one, clearly better at this distance) - and keeps it while the distance stays (EFT's own "back to the
    ///    main weapon" is refused for 15 s unless that gun is as good here or the one in the hands runs dry); seasoned bots do it almost always;
    ///  - otherwise, by character, health and the numbers: runs in closer at a sprint until the gun reaches (DisengageLayer, mode "close in"), goes round
    ///    the enemy out of his view (Maneuvers, when he is out of sight), keeps out of his view (SightCover), or leaves him be (BreakContact).
    /// How far a gun works - one range per type of weapon: the top of the effective distances the game gives the guns of that type (the weapon
    /// templates' bEffDist, the "effective range" the wiki shows; SPT 4.1.6: shotguns 70-100 m, pistols 50-70, SMGs 100-500, carbines 400, assault rifles
    /// 300-900, marksman rifles 100-1000, bolt-action rifles 1000, machine guns 650-800), plus a margin (F12, 100 m by default) for the types listed in F12
    /// (pistols, bolt-action, assault and marksman rifles). Within that range any gun works fully, right up to arm's length; beyond it the gun does not reach
    /// (CombatSense.Reach, the same number). The one limit up close: a bolt-action rifle under 20 m and a marksman rifle under 15 m - too slow to swing and
    /// work, a player takes the pistol or the other gun; with nothing else SAIN fights on with the rifle. Launchers and flares have no range limit.
    /// </summary>
    public static class RangeSense
    {
        private const float HoldSeconds = 15f;

        private enum Plan
        {
            Close,
            Flank,
            Cover,
            Leave,
        }

        private class State
        {
            public string EnemyId;
            public float NextDecision;
            public float SwitchRollUntil;
            public bool SwitchRollOk;
            public float SwitchedAt = -100f;
            public float HoldUntil;
            public float RefuseNoteAt;

            // Running in closer
            public bool Closing;
            public string CloseEnemyId;
            public string CloseEnemyName;
            public Vector3 Target;
            public float CloseUntil;
            public float CloseStartHp;
            public Vector3 LastPos;
            public float LastMoveAt;
            public bool Bold;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly System.Random _rng = new System.Random();
        private static readonly EquipmentSlot[] GunSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
        private static float _next;

        public static int Switched { get; private set; }
        public static int SwitchedClose { get; private set; }
        public static int Closed { get; private set; }
        public static int Reached { get; private set; }
        public static int Flanked { get; private set; }
        public static int Covered { get; private set; }
        public static int Left { get; private set; }
        public static int Refused { get; private set; }

        public static string Summary =>
            $"guns out of their range: took a gun that reaches {Switched} (a long gun too slow up close {SwitchedClose}), kept it against a change back {Refused} times, " +
            $"ran in closer {Closed} (got within reach {Reached}), went round {Flanked}, kept out of view {Covered}, left the enemy be {Left}";

        public static void Clear()
        {
            _states.Clear();
            _next = 0f;
            Switched = SwitchedClose = Closed = Reached = Flanked = Covered = Left = Refused = 0;
        }

        /// <summary>
        /// How well this gun works at this distance, 0..1: 1 within its range (all the way to arm's length), under 0.5 beyond it (0 at 1.5 x the range);
        /// a bolt-action or marksman rifle closer than its comfortable distance is under 0.5 as well.
        /// </summary>
        public static float Effect(Weapon gun, float distance)
        {
            if (gun == null)
            {
                return 1f;
            }

            float range = EffectiveRange(gun);
            float far = range <= 0f || distance <= range ? 1f : Mathf.Max(0f, 0.45f - 0.45f * (distance - range) / (0.5f * range));

            float comfort = CloseComfort(gun.WeapClass);
            float near = comfort > 0f && distance < comfort ? Mathf.Lerp(0.3f, 0.45f, distance / comfort) : 1f;
            return Mathf.Min(far, near);
        }

        /// <summary>The effective range of this gun's type plus the F12 margin for the types it is set for, in metres; 0 = no limit.</summary>
        public static float EffectiveRange(Weapon gun)
        {
            string weapClass = gun?.WeapClass;
            float range = ClassRange(weapClass);
            if (range <= 0f)
            {
                return 0f;
            }

            return range + (MarginClasses().Contains(weapClass ?? "") ? Mathf.Max(0f, Settings.GunRangeMargin.Value) : 0f);
        }

        /// <summary>The top of the game's effective distances (bEffDist) for the guns of each type, SPT 4.1.6 item database; 0 = no limit.</summary>
        public static float ClassRange(string weapClass)
        {
            switch (weapClass)
            {
                case "shotgun": return 100f;        // 70-100
                case "pistol": return 70f;          // 50-70
                case "smg": return 500f;            // 100-500 (MPX, STM-9 500; MP5, PP-19 100-200)
                case "assaultCarbine": return 400f; // 400
                case "assaultRifle": return 900f;   // 300-900
                case "marksmanRifle": return 1000f; // 100-1000
                case "sniperRifle": return 1000f;   // 1000
                case "machinegun": return 800f;     // 650-800
                default: return 0f;                 // grenade launchers, flare pistols, rocket launchers
            }
        }

        /// <summary>The closest distance a rifle is comfortable at (bolt-action 20 m, marksman 15 m); 0 = any distance, right up to arm's length.</summary>
        public static float CloseComfort(string weapClass)
        {
            switch (weapClass)
            {
                case "sniperRifle": return 20f;
                case "marksmanRifle": return 15f;
                default: return 0f;
            }
        }

        /// <summary>The reach by class LiveRaid used before (0.8.0-0.8.1), 0 = no limit: with "Fight at the range of the gun" off.</summary>
        public static float OldReach(string weapClass)
        {
            switch (weapClass)
            {
                case "shotgun": return 50f;
                case "pistol": return 35f;
                case "smg": return 70f;
                default: return 0f;
            }
        }

        private static string _marginText;
        private static HashSet<string> _marginClasses = new HashSet<string>();

        /// <summary>The weapon types the margin is for (F12 text, separated by commas or spaces; case does not matter).</summary>
        private static HashSet<string> MarginClasses()
        {
            string text = Settings.GunRangeMarginClasses.Value ?? "";
            if (text != _marginText)
            {
                _marginText = text;
                _marginClasses = new HashSet<string>(text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()),
                                                     StringComparer.OrdinalIgnoreCase);
            }

            return _marginClasses;
        }

        private static Weapon GunIn(BotOwner bot, EquipmentSlot slot) => bot?.GetPlayer?.Equipment?.GetSlot(slot)?.ContainedItem as Weapon;

        private static State Get(BotOwner bot)
        {
            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            return s;
        }

        public static void Tick()
        {
            if (!Settings.GunRange.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList.ToArray())
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

                try
                {
                    TickBot(bot, p);
                }
                catch (Exception e)
                {
                    Diag.Report("RangeSense", e);
                }
            }
        }

        private static void TickBot(BotOwner bot, BotProfile p)
        {
            State s = Get(bot);
            if (s.Closing)
            {
                // DisengageLayer runs it and asks Closing() for the end; asked here too, in case a higher layer (heal, back off) has the bot meanwhile
                Closing(bot);
                return;
            }

            Enemy e = SainBot.Of(bot)?.GoalEnemy;
            if (e?.EnemyPlayer == null)
            {
                s.EnemyId = null;
                return;
            }

            float d = e.RealDistance;
            bool engaged = e.IsVisible || e.TimeSinceSeen < 5f || (bot.Memory?.IsUnderFire == true && d < 150f);
            if (d > 320f || !engaged)
            {
                return;
            }

            BotWeaponManager wm = bot.WeaponManager;
            Weapon cur = wm?.CurrentWeapon;
            if (cur == null || wm.IsMelee || !(bot.GetPlayer?.HandsController?.Item is Weapon) || HandsGuard.RealReload(bot)
                || bot.Medecine?.Using == true || SafeReload.Busy || SafeReload.OutOfRounds(bot))
            {
                return;
            }

            float eff = Effect(cur, d);
            if (eff >= 0.5f)
            {
                return;
            }

            if (s.EnemyId != e.EnemyProfileId)
            {
                s.EnemyId = e.EnemyProfileId;
                s.SwitchRollUntil = 0f;
            }

            if (TrySwitch(bot, p, s, e, cur, d, eff))
            {
                return;
            }

            // Too close for a rifle with nothing else to take: SAIN fights on with it (there is no "closer" to go, and no fight is refused up close)
            if (d < CloseComfort(cur.WeapClass))
            {
                return;
            }

            if (Time.time >= s.NextDecision)
            {
                Decide(bot, p, s, e, cur, d, false);
            }
        }

        // ---------------- the other gun ----------------

        private static bool TrySwitch(BotOwner bot, BotProfile p, State s, Enemy e, Weapon cur, float d, float eff)
        {
            float now = Time.time;
            BotWeaponSelector sel = bot.WeaponManager.Selector;
            if (sel == null || now - s.SwitchedAt < 6f)
            {
                return false;
            }

            EquipmentSlot? best = null;
            Weapon bestGun = null;
            float bestEff = Mathf.Max(0.5f, eff + 0.25f);
            foreach (EquipmentSlot slot in GunSlots)
            {
                if (slot == sel.EquipmentSlot || !MeleeSwitch.CanFire(bot, slot))
                {
                    continue;
                }

                Weapon w = GunIn(bot, slot);
                float we = w != null ? Effect(w, d) : 0f;
                if (w != null && we >= bestEff)
                {
                    best = slot;
                    bestGun = w;
                    bestEff = we;
                }
            }

            if (best == null)
            {
                return false;
            }

            // A seasoned player always does it; a novice does not always think of it (one roll per enemy every 20 s)
            if (now > s.SwitchRollUntil)
            {
                double chance;
                switch (p.Skill)
                {
                    case SkillTier.Novice: chance = 0.6; break;
                    case SkillTier.Average: chance = 0.8; break;
                    case SkillTier.Experienced: chance = 0.92; break;
                    default: chance = 0.97; break;
                }

                s.SwitchRollUntil = now + 20f;
                s.SwitchRollOk = _rng.NextDouble() < chance;
            }

            if (!s.SwitchRollOk)
            {
                return false;
            }

            EquipmentSlot target = best.Value;
            bool ok = false;
            SafeReload.Run(() => ok = sel.TryChangeToSlot(target, false));
            if (!ok)
            {
                s.SwitchRollUntil = now + 5f;
                s.SwitchRollOk = false;
                return false;
            }

            bool tooClose = d < CloseComfort(cur.WeapClass);
            s.SwitchedAt = now;
            s.HoldUntil = now + HoldSeconds;
            Switched++;
            if (tooClose)
            {
                SwitchedClose++;
            }

            Log.Info($"LR Weapon: {p.Name} takes its {bestGun.ShortName.Localized()} ({bestGun.WeapClass}) for {e.EnemyPlayer.Profile?.Nickname} {d:0} m away - " +
                     $"its {cur.ShortName.Localized()} ({cur.WeapClass}) {(tooClose ? "is too slow that close" : "does not reach that far")}");
            return true;
        }

        /// <summary>
        /// EFT / SAIN wants another gun while the bot keeps the one it took for this distance: refused when the other gun is clearly worse at it
        /// (EFT's TryChangeToMain puts the shotgun back in the hands the moment the enemy is "far"). A dry gun may always go.
        /// </summary>
        public static bool RefusesChange(BotOwner bot, EquipmentSlot? target, string what)
        {
            if (!Settings.GunRange.Value || bot == null || !_states.TryGetValue(bot.ProfileId, out State s) || Time.time >= s.HoldUntil)
            {
                return false;
            }

            if (bot.WeaponManager == null || !bot.WeaponManager.HaveBullets || !(bot.GetPlayer?.HandsController?.Item is Weapon cur) || bot.WeaponManager.IsMelee)
            {
                return false;
            }

            Enemy e = SainBot.Of(bot)?.GoalEnemy;
            if (e == null)
            {
                return false;
            }

            if (target.HasValue && target.Value == bot.WeaponManager.Selector?.EquipmentSlot)
            {
                return false;
            }

            float d = e.RealDistance;
            Weapon other = target.HasValue ? GunIn(bot, target.Value) : null;
            float otherEff = other != null ? Effect(other, d) : 0f;
            if (target.HasValue && other == null)
            {
                // Not a firearm slot (knife, grenade): not ours to judge
                return false;
            }

            if (otherEff >= Effect(cur, d) - 0.1f)
            {
                return false;
            }

            Refused++;
            if (Time.time >= s.RefuseNoteAt)
            {
                s.RefuseNoteAt = Time.time + 10f;
                Log.Detail("weapon", () => $"{bot.Profile?.Nickname} keeps its {cur.ShortName.Localized()} for {e.EnemyPlayer?.Profile?.Nickname} {d:0} m away ({what} refused)");
            }

            return true;
        }

        /// <summary>Holding the gun it took for this distance: EFT's "change for the distance" waits.</summary>
        public static bool Holds(BotOwner bot) =>
            Settings.GunRange.Value && bot != null && _states.TryGetValue(bot.ProfileId, out State s) && Time.time < s.HoldUntil;

        // ---------------- no gun that reaches ----------------

        /// <summary>
        /// SightCover found the bot back in a shooter's view beyond the reach of its gun (or hit there): decide now, and not "keep out of view" again.
        /// True when the bot now runs in, flanks or leaves.
        /// </summary>
        public static bool React(BotOwner bot, Enemy shooter)
        {
            BotProfile p = BotProfiles.Get(bot);
            Weapon cur = bot?.WeaponManager?.CurrentWeapon;
            if (p == null || cur == null || shooter == null)
            {
                return false;
            }

            State s = Get(bot);
            return !s.Closing && Decide(bot, p, s, shooter, cur, shooter.RealDistance, true);
        }

        private static bool Decide(BotOwner bot, BotProfile p, State s, Enemy e, Weapon cur, float d, bool noCover)
        {
            float now = Time.time;
            CombatStance stance = CombatSense.StanceOf(bot);
            if (stance == CombatStance.Retreat || stance == CombatStance.BreakContact || stance == CombatStance.Reposition)
            {
                s.NextDecision = now + 3f;
                return false;
            }

            if (Maneuvers.IsFlanking(bot) || (TacticalTasks.TryGet(bot, out TacticalTask task) && task.Kind != TaskKind.Assist)
                || bot.Medecine?.FirstAid?.Have2Do == true)
            {
                s.NextDecision = now + 3f;
                return false;
            }

            s.NextDecision = now + 12f + (float)_rng.NextDouble() * 6f;
            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            SAIN.Components.BotComponent sain = SainBot.Of(bot);
            int enemies = Math.Max(1, sain?.EnemyController?.KnownEnemies?.Count(x => x != null && x.EnemyKnown && x.RealDistance < 150f && (x.IsVisible || x.TimeSinceSeen < 20f)) ?? 1);
            HashSet<string> squad = LootBrain.SquadIds(bot);
            int allies = Math.Max(1, Singleton<GameWorld>.Instance.AllAlivePlayersList.Count(pl => squad.Contains(pl.ProfileId) && (pl.Position - bot.Position).sqrMagnitude < 70f * 70f));
            bool bold = p.Style == PlayStyle.Rusher || p.Style == PlayStyle.Hunter || p.Aggression >= 0.6f;
            bool timid = p.Style == PlayStyle.Rat || p.Style == PlayStyle.Camper || (CombatSense.TryGetState(bot, out CombatSense.State cs) && cs.HasMorale && cs.Morale < -0.2f);
            float reach = CombatSense.Reach(bot);
            bool inView = e.IsVisible || e.TimeSinceSeen < 3f;

            float wClose = (0.3f + 0.6f * p.Aggression + (bold ? 0.3f : 0f)) * (hp < 0.5f ? 0.25f : hp < 0.7f ? 0.6f : 1f)
                           * (enemies > allies ? 0.5f : 1f) * (d > reach * 2.4f ? 0.35f : 1f) * (timid ? 0.3f : 1f);
            float wFlank = inView || d < 18f || d > 120f || hp < 0.6f ? 0f
                : (0.35f + (p.Style == PlayStyle.Hunter || p.Style == PlayStyle.Explorer ? 0.15f : 0f)) * (timid ? 0.4f : 1f);
            float wCover = noCover || !inView ? 0f : 0.12f + (timid ? 0.35f : 0f);
            float wLeave = 0.05f + 0.5f * (1f - hp) + (enemies > allies + 1 ? 0.3f : 0f) + (timid ? 0.2f : 0f);

            Plan plan = Pick(wClose, wFlank, wCover, wLeave);
            string enemyName = e.EnemyPlayer?.Profile?.Nickname ?? "an enemy";
            string why = $"{enemyName} {d:0} m away, beyond the ~{reach:0} m its {cur.ShortName.Localized()} ({cur.WeapClass}) reaches, {enemies} vs {allies}, hp {hp:P0}";

            // What it chose may not be possible here: the next best that is
            foreach (Plan option in Order(plan))
            {
                switch (option)
                {
                    case Plan.Close:
                        if (wClose > 0f && StartClose(bot, p, s, e, d, reach, hp, bold, why))
                        {
                            return true;
                        }

                        break;

                    case Plan.Flank:
                        if (wFlank > 0f && Maneuvers.TrySoloFlank(bot, e))
                        {
                            Flanked++;
                            Log.Info($"LR Combat: {p.Name} ({p.Style}) is out-gunned at range ({why}) - goes round him out of his view");
                            return true;
                        }

                        break;

                    case Plan.Cover:
                        if (wCover > 0f)
                        {
                            // SightCover takes the bot out of his view; back in it, SightCover calls React
                            Covered++;
                            Log.Detail("combat", $"{p.Name} ({p.Style}) is out-gunned at range ({why}) - keeps out of his view");
                            return false;
                        }

                        break;

                    case Plan.Leave:
                        if (CombatSense.BreakOff(bot, e, $"out-gunned at range: {why}"))
                        {
                            Left++;
                            return true;
                        }

                        break;
                }
            }

            return false;
        }

        private static Plan Pick(float close, float flank, float cover, float leave)
        {
            float total = close + flank + cover + leave;
            double roll = _rng.NextDouble() * total;
            if ((roll -= close) < 0)
            {
                return Plan.Close;
            }

            if ((roll -= flank) < 0)
            {
                return Plan.Flank;
            }

            return roll - cover < 0 ? Plan.Cover : Plan.Leave;
        }

        /// <summary>The chosen plan first, then the others from the boldest to the most careful.</summary>
        private static IEnumerable<Plan> Order(Plan first)
        {
            yield return first;
            foreach (Plan p in new[] { Plan.Close, Plan.Flank, Plan.Cover, Plan.Leave })
            {
                if (p != first)
                {
                    yield return p;
                }
            }
        }

        // ---------------- running in closer ----------------

        private static bool StartClose(BotOwner bot, BotProfile p, State s, Enemy e, float d, float reach, float hp, bool bold, string why)
        {
            Vector3? at = e.IsVisible && e.EnemyPlayer != null ? e.EnemyPlayer.Position : e.LastKnownPosition;
            if (at == null || !NavMesh.SamplePosition(at.Value, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                return false;
            }

            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(bot.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                return false;
            }

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            // A way round half the map is not "closing in"
            if (length > Mathf.Min(1.8f * d + 15f, 230f))
            {
                return false;
            }

            float now = Time.time;
            s.Closing = true;
            s.CloseEnemyId = e.EnemyProfileId;
            s.CloseEnemyName = e.EnemyPlayer?.Profile?.Nickname ?? "an enemy";
            s.Target = hit.position;
            s.CloseUntil = now + Mathf.Clamp(length / 4.5f + 4f, 8f, 40f);
            s.CloseStartHp = hp;
            s.LastPos = bot.Position;
            s.LastMoveAt = now;
            s.Bold = bold;
            Closed++;
            Log.Info($"LR Combat: {p.Name} ({p.Style}) is out-gunned at range ({why}) - runs in closer, {length:0} m on foot");
            SquadBrain.Say(bot, EPhraseTrigger.Gogogo, 0.4f);
            return true;
        }

        /// <summary>Asked by DisengageLayer: the bot is running in on its enemy. Ends the run (false) when it is over.</summary>
        public static bool Closing(BotOwner bot)
        {
            if (bot == null || !_states.TryGetValue(bot.ProfileId, out State s) || !s.Closing)
            {
                return false;
            }

            string end = CloseEnd(bot, s, out bool reached);
            if (end == null)
            {
                return true;
            }

            s.Closing = false;
            s.NextDecision = Time.time + 10f;
            if (reached)
            {
                Reached++;

                // Within reach a bold one keeps pressing (SAIN's rush at close range is a real assault); the rest fight as SAIN decides
                if (s.Bold)
                {
                    CombatSense.Nudge(bot, CombatStance.Push, $"got within reach of {s.CloseEnemyName}, presses on");
                }
            }

            Log.Detail("combat", $"{bot.Profile.Nickname} stops running in on {s.CloseEnemyName}: {end}");
            return false;
        }

        public static Vector3? CloseTarget(BotOwner bot) =>
            bot != null && _states.TryGetValue(bot.ProfileId, out State s) && s.Closing ? s.Target : (Vector3?)null;

        /// <summary>Why the run is over, or null while it goes on.</summary>
        private static string CloseEnd(BotOwner bot, State s, out bool reached)
        {
            reached = false;
            float now = Time.time;
            if (!Settings.GunRange.Value || bot.IsDead)
            {
                return "switched off";
            }

            CombatStance stance = CombatSense.StanceOf(bot);
            if (stance == CombatStance.Retreat || stance == CombatStance.BreakContact)
            {
                return "the fight turned, falls back";
            }

            Enemy e = SainBot.Of(bot)?.GoalEnemy;
            if (e == null)
            {
                return "no enemy any more";
            }

            if (e.EnemyProfileId != s.CloseEnemyId && (e.IsVisible || e.TimeSinceSeen < 3f))
            {
                return $"another enemy ({e.EnemyPlayer?.Profile?.Nickname})";
            }

            Weapon cur = bot.WeaponManager?.CurrentWeapon;
            float d = e.RealDistance;
            float range = EffectiveRange(cur);
            if (cur == null || range <= 0f || d <= 0.9f * range)
            {
                reached = true;
                return $"within reach ({d:0} m)";
            }

            if (d < 10f)
            {
                reached = true;
                return "the enemy is close";
            }

            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            if (hp < 0.35f || hp < s.CloseStartHp - 0.3f)
            {
                return $"hurt on the way (hp {hp:P0})";
            }

            if (bot.Medecine?.Using == true)
            {
                return "patching up";
            }

            if (now > s.CloseUntil)
            {
                return "took too long";
            }

            Vector3 pos = bot.Position;
            if ((pos - s.LastPos).sqrMagnitude > 1f)
            {
                s.LastPos = pos;
                s.LastMoveAt = now;
            }
            else if (now - s.LastMoveAt > 3f)
            {
                CombatSense.GiveUpMove(bot, "running in closer, no way");
                return "stuck on the way";
            }

            // Where he is now: seen - his position, otherwise where he was last known (squadmates' sightings count)
            Vector3? at = e.IsVisible && e.EnemyPlayer != null ? e.EnemyPlayer.Position : e.LastKnownPosition;
            if (at.HasValue && (at.Value - s.Target).sqrMagnitude > 4f && NavMesh.SamplePosition(at.Value, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                s.Target = hit.position;
            }

            return null;
        }
    }

    /// <summary>Sprints at the enemy along the way on foot; DisengageLayer hands the bot back to SAIN once its gun reaches him.</summary>
    public class CloseInLogic : CustomLogic
    {
        private float _nextOrder;

        public CloseInLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Stop()
        {
            BotOwner.Sprint(false);
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Vector3? target = RangeSense.CloseTarget(BotOwner);
                if (target == null)
                {
                    return;
                }

                BotOwner.SetPose(1f);
                BotOwner.SetTargetMoveSpeed(1f);
                BotOwner.Sprint(true);
                BotOwner.Steering.LookToMovingDirection();
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 1f;
                    BotOwner.GoToPoint(target.Value, false, -1f, false, false);
                }
            }
            catch (Exception e)
            {
                Diag.Report("CloseInLogic", e);
            }
        }
    }
}
