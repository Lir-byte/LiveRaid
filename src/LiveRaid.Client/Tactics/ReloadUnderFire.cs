using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Caught in a long reload (F12 -> 9 "Reload under fire"). A drum, a 45/60/95-round magazine or a tube to fill takes seconds a close enemy does not give.
    /// What a player does instead, in this order:
    ///  - the enemy at arm's length (3 m): the knife;
    ///  - a loaded pistol or second gun and the enemy within 35 m: switch to it (the reload is dropped, the rifle is reloaded later);
    ///  - nothing else to shoot with: keep reloading on the way to cover - never stand in the open, never push (SAIN keeps choosing its moves; while the
    ///    reload goes on its "stand and shoot" / "rush" / "search" become "seek cover").
    /// Dropping the reload with nothing else to shoot would only leave the bot with an empty gun, so it is not done. Seasoned bots react more often
    /// (novice 35 %, average 60 %, experienced 80 %, pro 90 %); the rest just reload where they are, as SAIN decides.
    /// Independent of the setting, a bot whose hands are reloading is never sent to push or to stand and shoot by LiveRaid (CombatPatches).
    /// </summary>
    public static class ReloadUnderFire
    {
        private class State
        {
            public float ReloadKey = -1f;
            public float CoverUntil;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly Dictionary<string, int> _capacity = new Dictionary<string, int>();
        private static readonly System.Random _rng = new System.Random();
        private static float _next;

        public static int Switched { get; private set; }
        public static int Knifed { get; private set; }
        public static int ToCover { get; private set; }

        public static string Summary => $"caught in a long reload: switched to another gun {Switched}, knife {Knifed}, reloaded on the way to cover {ToCover}";

        public static void Clear()
        {
            _states.Clear();
            _capacity.Clear();
            _next = 0f;
            Switched = Knifed = ToCover = 0;
        }

        /// <summary>The bot reloads with an enemy on it: LiveRaid's combat hooks send it to cover instead of standing or pushing.</summary>
        public static bool WantsCover(BotOwner bot) =>
            bot != null && _states.TryGetValue(bot.ProfileId, out State s) && Time.time < s.CoverUntil && HandsGuard.RealReload(bot);

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
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
                    TickBot(bot, player, p);
                }
                catch (System.Exception e)
                {
                    Diag.Report("ReloadUnderFire", e);
                }
            }
        }

        private static void TickBot(BotOwner bot, Player player, BotProfile p)
        {
            BotWeaponManager wm = bot.WeaponManager;
            Weapon gun = wm?.CurrentWeapon;
            BotReload reload = wm?.Reload;
            if (gun == null || reload == null)
            {
                return;
            }

            if (!reload.Reloading)
            {
                // Remember the gun's magazine size while it has one in (during a magazine swap there is none)
                int max = gun.GetCurrentMagazine()?.MaxCount ?? 0;
                if (max > 0)
                {
                    _capacity[gun.Id] = max;
                }

                if (_states.TryGetValue(bot.ProfileId, out State done))
                {
                    done.ReloadKey = -1f;
                    done.CoverUntil = 0f;
                }

                return;
            }

            if (!Settings.ReloadUnderFire.Value || !HandsGuard.RealReload(bot))
            {
                return;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State();
            }

            // One decision per reload
            if (s.ReloadKey >= 0f)
            {
                return;
            }

            Enemy enemy = SainBot.Of(bot)?.GoalEnemy;
            if (enemy?.EnemyPlayer == null || !enemy.CheckValid())
            {
                return;
            }

            bool onMe = (enemy.IsVisible && enemy.RealDistance <= 30f) || (bot.Memory?.IsUnderFire == true && enemy.RealDistance <= 45f);
            if (!onMe)
            {
                return;
            }

            s.ReloadKey = reload._reloadStartTime;
            if (!LongReload(gun, reload))
            {
                return;
            }

            double chance;
            switch (p.Skill)
            {
                case SkillTier.Novice: chance = 0.35; break;
                case SkillTier.Average: chance = 0.6; break;
                case SkillTier.Experienced: chance = 0.8; break;
                default: chance = 0.9; break;
            }

            if (_rng.NextDouble() > chance)
            {
                return;
            }

            float dist = enemy.RealDistance;
            BotWeaponSelector sel = wm.Selector;
            string enemyName = enemy.EnemyPlayer.Profile?.Nickname;

            // At arm's length: the knife
            if (dist <= 3f && sel.CanChangeToMeleeWeapons && player.Equipment?.GetSlot(EquipmentSlot.Scabbard)?.ContainedItem != null)
            {
                reload.Reloading = false;
                SafeReload.Run(sel.ChangeToMelee);
                Knifed++;
                Log.Detail("weapon", () => $"{p.Name} is caught reloading by {enemyName} at {dist:0} m - knife");
                return;
            }

            // A loaded pistol or second gun
            if (dist <= 35f)
            {
                EquipmentSlot current = sel.EquipmentSlot;
                foreach (EquipmentSlot slot in new[] { EquipmentSlot.Holster, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.FirstPrimaryWeapon })
                {
                    if (slot == current || !MeleeSwitch.CanFire(bot, slot))
                    {
                        continue;
                    }

                    // EFT refuses any change while the reload flag is up; when the switch is refused the reload carries on as before
                    reload.Reloading = false;
                    bool ok = false;
                    SafeReload.Run(() => ok = sel.TryChangeToSlot(slot, false));
                    if (ok)
                    {
                        Switched++;
                        Log.Detail("weapon", () => $"{p.Name} is caught reloading by {enemyName} at {dist:0} m - switches to its {slot}");
                        return;
                    }

                    reload.Reloading = true;
                    s.ReloadKey = reload._reloadStartTime;
                    break;
                }
            }

            // Nothing else to shoot with: reload on the way to cover
            s.CoverUntil = Time.time + 8f;
            ToCover++;
            Log.Detail("weapon", () => $"{p.Name} is caught reloading by {enemyName} at {dist:0} m - reloads on the way to cover");
        }

        /// <summary>A reload worth avoiding in a close fight: a magazine of more than 30 rounds, or 4+ rounds to load one by one.</summary>
        private static bool LongReload(Weapon gun, BotReload reload)
        {
            if (reload._reloadType == BotReload.EReloadType.AmmoReload)
            {
                try
                {
                    return reload.MaxBulletCount - reload.BulletCount >= 4;
                }
                catch
                {
                    return false;
                }
            }

            int cap = _capacity.TryGetValue(gun.Id, out int c) ? c : gun.GetCurrentMagazine()?.MaxCount ?? 0;
            return cap > 30;
        }
    }
}
