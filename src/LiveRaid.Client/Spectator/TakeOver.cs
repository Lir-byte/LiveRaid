using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommonAssets.Scripts.Game;
using Diz.Jobs;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI.Insurance;
using HarmonyLib;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// Playing as a bot after your death. The bot stays a bot for everyone else: its BotOwner keeps the Active state, so SAIN,
    /// QuestingBots and EFT still see a valid enemy / squadmate (an inactive bot is ignored as an enemy by SAIN). Only its "mind" is cut off
    /// the body: the brain agent leaves the AI core controller (no layers, no logic nodes - BigBrain, SAIN, QuestingBots, LootingBots and
    /// LiveRaid layers all live there), <c>BotOwner.UpdateManual/FixedUpdate</c> and SAIN's per-bot tick are skipped by
    /// <c>SpectatorPatches</c> (mover, steering, aiming, shooting, healing), and the QuestingBots / LootingBots components are disabled.
    /// The body then gets what EFT gives your own character at spawn: an <see cref="EftGamePlayerOwner"/> (input, HUD, interactions),
    /// your mouse sensitivity, first-person view with hands and the player camera controller.
    /// Nothing is handed back: the bot is yours until it dies, leaves through an exit or the raid ends.
    /// </summary>
    internal static class TakeOver
    {
        public static Player Current { get; private set; }

        public static BotOwner Bot { get; private set; }

        public static bool FirstPerson { get; private set; }

        /// <summary>Exit the taken bot stands in, and since when (null - none).</summary>
        public static ExfiltrationPoint Exit { get; private set; }

        public static float ExitLeft { get; private set; }

        private static EftGamePlayerOwner _owner;
        private static bool _starting;
        private static ResourceKey _handsBundle;

        private const string BotContainer = "5c0a794586f77461c458f892";
        private const string AlphaContainer = "544a11ac4bdc2d470e8b456a";
        private static float _exitSince;
        private static float _nextExitCheck;
        private static readonly List<Behaviour> _disabled = new List<Behaviour>();
        private static YourPlayerState.BotSettings _saved;

        private static readonly FieldInfo InsuranceField = AccessTools.Field(typeof(GamePlayerOwner), "_insurance");
        private static readonly FieldInfo BackEndField = AccessTools.Field(typeof(BaseLocalGame<EftGamePlayerOwner>), "_backEnd");
        private static readonly FieldInfo SensitivityField = AccessTools.Field(typeof(Player), "GetSensitivity");
        private static readonly FieldInfo AimingSensitivityField = AccessTools.Field(typeof(Player), "GetAimingSensitivity");
        private static readonly FieldInfo CullingField = AccessTools.Field(typeof(LocalPlayer), "botPlayerCulling");

        // Components of other mods that drive a bot on their own (only when its BotState is Active, which it stays)
        private static readonly string[] BotDrivers = { "LootingBrain", "BotObjectiveManager" };

        public static bool IsTaken(BotOwner bot) => bot != null && Bot == bot;

        public static bool IsTaken(Player player) => player != null && Current == player;

        public static bool IsTaken(string profileId) => Current != null && Current.ProfileId == profileId;

        public static void Clear()
        {
            Current = null;
            Bot = null;
            _owner = null;
            Exit = null;
            _starting = false;
            _handsBundle = null;
            _disabled.Clear();
            _saved = null;
        }

        /// <summary>A living bot you can watch (follow, name tag).</summary>
        public static bool IsWatchable(Player player)
        {
            BotOwner bot = player?.AIData?.BotOwner;
            return bot != null && !bot.IsDead && bot.BotState == EBotState.Active && player.HealthController?.IsAlive == true
                   && player != SpectatorSession.Original && !IsTaken(player);
        }

        /// <summary>Why this bot cannot be taken over now (null - it can).</summary>
        public static string Refusal(Player player)
        {
            if (Plugin.DisabledFeatures.Contains("take over"))
            {
                return Loc.T("вселение отключено (ошибка патча, см. лог)", "taking over is off (a patch error, see the log)");
            }

            if (!IsWatchable(player) || player.BtrState != EPlayerBtrState.Outside)
            {
                return Loc.T("этого бота сейчас нельзя взять", "this bot cannot be taken now");
            }

            BotOwner bot = player.AIData.BotOwner;
            if (bot.Profile.Info.Settings.Role == WildSpawnType.marksman || BotProfiles.Get(bot)?.Style == PlayStyle.Camper)
            {
                return Loc.T("в кемперов вселяться нельзя", "campers cannot be taken over");
            }

            return Current != null || _starting ? Loc.T("вы уже вселяетесь", "you are already taking over") : null;
        }

        /// <summary>
        /// Starts the take-over: the mind is cut at once (the bot freezes where it stands), the rest waits for the bot's own
        /// first-person arms to load (a bot is built without them) and then gives you the body.
        /// </summary>
        public static bool Start(Player target, out string error)
        {
            BaseLocalGame<EftGamePlayerOwner> game = SpectatorSession.Game;
            error = game?.PlayerOwner == null ? Loc.T("нет игры", "no game") : Refusal(target);
            if (error != null || SpectatorCamera.Instance == null)
            {
                return false;
            }

            BotOwner bot = target.AIData.BotOwner;
            try
            {
                CutTheMind(bot, target);
                Bot = bot;
                Current = target;
                _starting = true;
                SmoothieGuard.Engage("take-over of " + target.Profile.Nickname);
                SpectatorSession.State = SpectatorState.TakenOver;
                SpectatorCamera.Instance.StartCoroutine(Finish(game, bot, target));
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"LR Spectator: could not take over {target.Profile.Nickname}: {e}");
                error = Loc.T("не получилось взять управление (подробности в логе)", "could not take control (details in the log)");
                Rollback(bot, target);
                return false;
            }
        }

        public static bool Starting => _starting;

        private static IEnumerator Finish(BaseLocalGame<EftGamePlayerOwner> game, BotOwner bot, Player target)
        {
            string name = target.Profile.Nickname;
            // First person only: a third-person body under your controls broke the inventory and interactions
            const bool wantFirst = true;
            Task load = wantFirst ? LoadHands(target, out _handsBundle) : null;
            float until = Time.time + 8f;
            while (load != null && !load.IsCompleted && Time.time < until)
            {
                yield return null;
            }

            _starting = false;
            if (Current != target || target.HealthController?.IsAlive != true || SpectatorSession.State != SpectatorState.TakenOver)
            {
                // Died while the arms loaded, or the raid is ending
                if (Current == target)
                {
                    Rollback(bot, target);
                    SpectatorCamera.Instance?.BeginDying(Loc.T($"{name} погиб", $"{name} died"));
                }

                yield break;
            }

            if (load != null && (load.IsFaulted || !load.IsCompleted))
            {
                Log.Warn($"LR Spectator: {name}'s arms did not load ({(load.IsFaulted ? load.Exception?.GetBaseException().Message : "timeout")}), your own arms are used");
                _handsBundle = null;
            }

            try
            {
                EftGamePlayerOwner mainOwner = game.PlayerOwner;
                _saved = YourPlayerState.Remember(target, SensitivityField, AimingSensitivityField, CullingField);
                MakePlayable(target);

                // The body becomes "yours": first-person weapon setup, sounds, no culling, your mouse settings
                target.IsYourPlayer = true;

                // A bot's weapon animation runs only the "draw down" effector (BotOwner.Create): no recoil, sway, breathing or walk bob.
                // Your character's set of effectors comes with the body
                target.ProceduralWeaponAnimation.Mask = SpectatorSession.Original.ProceduralWeaponAnimation.Mask;
                CopyField(SensitivityField, SpectatorSession.Original, target);
                CopyField(AimingSensitivityField, SpectatorSession.Original, target);
                if (CullingField?.GetValue(target) is BasePlayerCulling culling)
                {
                    culling.Disable();
                }

                // Whatever a body that was yours before (your own, a bot you left) left on the camera: screen effects, a scope camera
                YourPlayerState.ClearCameraEffects(SpectatorSession.Original);
                YourPlayerState.CloseOptic();

                FirstPerson = wantFirst && EnsureHands(target, _handsBundle);
                if (!FirstPerson)
                {
                    throw new InvalidOperationException("no first-person arms could be put on this bot");
                }

                target.PointOfView = EPointOfView.FirstPerson;

                // What EFT does for your own character at spawn (BaseLocalGame owner factory + Spawn)
                InsuranceCompany insurance = InsuranceField?.GetValue(mainOwner) as InsuranceCompany;
                IEftSession session = BackEndField?.GetValue(game) as IEftSession;
                _owner = EftGamePlayerOwner.Create(target, mainOwner.InputTree, insurance, session, game.GameUi, game.GameDateTime, game.Location);
                _owner.OnLeave += OnLeave;
                _owner.vmethod_0();

                if (FirstPerson)
                {
                    CullingGuard.MarkRebuild();
                    PlayerCameraController.Create(target);
                    RefreshHands(target);
                }

                SpectatorCamera.Instance?.Fade();

                // The camera is in the bot's head right away (not where the free camera was)
                Camera cam = CameraManager.Exist ? CameraManager.Instance.Camera : null;
                if (cam != null && target.CameraPosition != null)
                {
                    cam.transform.SetPositionAndRotation(target.CameraPosition.position, target.CameraPosition.rotation);
                }

                target.HealthController.DiedEvent += OnDied;
                TakenBotFire.Watch(target);
                Exit = null;
                Log.Info($"LR Spectator: took over {name} ({BotProfiles.KindOf(bot)}, {target.Profile.Info.Side}, {bot.Profile.Info.Settings.Role}), " +
                         $"{(FirstPerson ? "first person, " + (_handsBundle != null ? "its own arms" : "your arms") : "third person")}; " +
                         "its kills, loot and exit do not count for the profile");
                YourPlayerState.Report("after taking over " + name);
            }
            catch (Exception e)
            {
                Log.Error($"LR Spectator: could not take over {name}: {e}");
                Rollback(bot, target);
                SpectatorCamera.Instance?.BeginDying(Loc.T("Не получилось вселиться (подробности в логе)", "Could not take over (details in the log)"));
            }
        }

        /// <summary>Every frame while a bot is taken: the exit check.</summary>
        public static void Tick()
        {
            if (Current == null || Time.time < _nextExitCheck)
            {
                return;
            }

            _nextExitCheck = Time.time + 0.2f;
            ExfiltrationPoint point = ExitUnderFeet(Current);
            if (point == null)
            {
                Exit = null;
                return;
            }

            if (point != Exit)
            {
                Exit = point;
                _exitSince = Time.time;
                Log.Info($"LR Spectator: {Current.Profile.Nickname} entered exit {point.Settings.Name} ({point.Settings.ExfiltrationTime:0} s)");
            }

            ExitLeft = Mathf.Max(0f, point.Settings.ExfiltrationTime - (Time.time - _exitSince));
            if (ExitLeft <= 0f)
            {
                SpectatorSession.EndRaid($"{Current.Profile.Nickname} left through {point.Settings.Name}");
            }
        }

        /// <summary>Gives the controls back (the bot died or the raid ends). The body is not returned to the AI.</summary>
        public static void Release(bool raidEnding)
        {
            Player player = Current;
            if (player == null)
            {
                return;
            }

            player.HealthController.DiedEvent -= OnDied;
            Try(TakenBotFire.Stop);
            if (_owner != null)
            {
                _owner.OnLeave -= OnLeave;
                Try(() => _owner.vmethod_1());
                Try(() => UnityEngine.Object.Destroy(_owner));
            }

            CullingGuard.MarkRebuild();
            Try(() => PlayerCameraController.Destroy(player));

            // The taken bot's inventory screen (opened and then the bot died, or the controls were cut) leaves the texture streaming on its "icon" settings:
            // IncreaseMemoryBudgetForIcon sets the mip reduction to 4 and the budget +100 and only RestoreMemoryBudget undoes it. Back in the free camera the level
            // then stopped loading textures. Restore it, and re-apply the camera's mip bias.
            RestoreTextureStreaming();

            if (!raidEnding || player.HealthController?.IsAlive != true)
            {
                // A body seen from outside again: full renderers (first person keeps only its shadow) and no "your player" flag on a corpse
                Try(() => player.PointOfView = EPointOfView.ThirdPerson);
                Try(() => player.IsYourPlayer = false);
            }

            Current = null;
            Bot = null;
            _owner = null;
            Exit = null;
            if (!raidEnding)
            {
                // The screen effects, scope camera, sound listener and headphones EFT tied to this body go back to the free camera
                Try(() => YourPlayerState.Untie(player));
                bool alive = player.HealthController?.IsAlive == true;
                YourPlayerState.ReportSoon($"after {player.Profile.Nickname} {(alive ? "was left" : "died")}");
            }
        }

        private static void OnDied(EDamageType damage)
        {
            Diag.Guard("Spectator.TakenBotDied", () =>
            {
                Player player = Current;
                string name = player?.Profile?.Nickname ?? "bot";
                Log.Info($"LR Spectator: taken-over {name} died ({damage})");
                if (Settings.EndRaidWhenTakenBotDies.Value)
                {
                    SpectatorSession.EndRaid($"the taken-over {name} died");
                    return;
                }

                Release(raidEnding: false);
                SpectatorCamera.Instance?.BeginDying(Loc.T($"{name} погиб", $"{name} died"));
            });
        }

        private static void OnLeave()
        {
            // "Leave" from the in-raid menu while playing a bot
            Diag.Guard("Spectator.Leave", () => SpectatorSession.EndRaid("left from the menu"));
        }

        private static void CutTheMind(BotOwner bot, Player player)
        {
            bot.BotsController?.AICoreController?.RemoveNodeController(bot.Brain.Agent);
            Try(() => bot.Mover?.Stop());
            Try(() => Tactics.TacticalTasks.Cancel(bot, "taken over by the player"));
            Try(() => Tactics.ProneLoot.Forget(bot));
            Try(() =>
            {
                if (player.HandsController is Player.FirearmController firearm)
                {
                    firearm.SetTriggerPressed(false);
                }
            });

            _disabled.Clear();
            foreach (MonoBehaviour component in player.gameObject.GetComponents<MonoBehaviour>())
            {
                if (component != null && component.enabled && BotDrivers.Contains(component.GetType().Name))
                {
                    component.StopAllCoroutines();
                    component.enabled = false;
                    _disabled.Add(component);
                }
            }
        }

        /// <summary>You hand the bot back on purpose: it gets its mind and its AI components back and you return to the free camera.</summary>
        public static void Leave()
        {
            Player player = Current;
            BotOwner bot = Bot;
            if (player == null || bot == null || _starting || SpectatorSession.State != SpectatorState.TakenOver)
            {
                return;
            }

            string name = player.Profile.Nickname;
            Log.Info($"LR Spectator: you left {name}, it goes back to its AI");

            // The weapon in its hands was set up for your view (no gun shadow, first-person parts); taken out again it is a bot's weapon
            RefreshHands(player, forAi: true);
            Rollback(bot, player);
            SpectatorCamera.Instance?.Fade();
        }

        /// <summary>The take-over failed half way: give the bot its mind back so it does not stand there forever.</summary>
        private static void Rollback(BotOwner bot, Player player)
        {
            Try(() => Release(raidEnding: false));
            Current = null;
            Bot = null;
            Try(() =>
            {
                AICoreController core = bot.BotsController?.AICoreController;
                AICoreAgentBase agent = bot.Brain?.Agent;
                if (core != null && agent != null)
                {
                    core._listToDel.Remove(agent);
                    core.AddNodeController(agent);
                }
            });
            foreach (Behaviour component in _disabled)
            {
                Try(() => component.enabled = true);
            }

            _disabled.Clear();
            Try(() => player.IsYourPlayer = false);
            Try(() => player.PointOfView = EPointOfView.ThirdPerson);
            YourPlayerState.GiveBack(player, _saved, SensitivityField, AimingSensitivityField, CullingField);
            _saved = null;
            _starting = false;
            if (SpectatorSession.State == SpectatorState.TakenOver)
            {
                SpectatorCamera.Instance?.ResumeFree();
            }
        }

        /// <summary>Starts loading the bot's own first-person arms (its hands customization); null - nothing to load.</summary>
        private static Task LoadHands(Player player, out ResourceKey bundle)
        {
            bundle = null;
            try
            {
                if (!player.Profile.Customization.TryGetValue(EBodyModelPart.Hands, out MongoID hands))
                {
                    return null;
                }

                bundle = Singleton<CustomizationSolver>.Instance.GetBundle(hands);
                if (bundle == null)
                {
                    return null;
                }

                return Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
                    new List<ResourceKey> { bundle }, JobYieldPriority.General, new Progress<InitLevelProgress>(), CancellationToken.None);
            }
            catch (Exception e)
            {
                Log.Warn("LR Spectator: could not start loading the bot's arms: " + e.Message);
                bundle = null;
                return null;
            }
        }

        /// <summary>
        /// Bots are built without first-person arms (ThirdPersonCustomizationFilter keeps body, feet and head), and the camera
        /// controller needs them. The bot's own arms when they loaded, otherwise your character's (loaded for sure).
        /// </summary>
        private static bool EnsureHands(Player player, ResourceKey own)
        {
            PlayerBody body = player.PlayerBody;
            if (body.BodySkins.ContainsKey(EBodyModelPart.Hands))
            {
                return true;
            }

            if (own != null && TrySetHands(body, own))
            {
                return true;
            }

            _handsBundle = null;
            if (SpectatorSession.Original.Profile.Customization.TryGetValue(EBodyModelPart.Hands, out MongoID mine))
            {
                ResourceKey bundle = Singleton<CustomizationSolver>.Instance.GetBundle(mine);
                if (bundle != null && TrySetHands(body, bundle))
                {
                    return true;
                }
            }

            Log.Warn("LR Spectator: no first-person arms for the bot, third person instead");
            return false;
        }

        private static bool TrySetHands(PlayerBody body, ResourceKey bundle)
        {
            try
            {
                body.SetSkin(new KeyValuePair<EBodyModelPart, ResourceKey>(EBodyModelPart.Hands, bundle), body.SkeletonHands);
                body.BodySkins[EBodyModelPart.Hands].SetShadowCastingMode(UnityEngine.Rendering.ShadowCastingMode.Off);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"LR Spectator: arms {bundle} could not be put on: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// A bot carries SPT's technical secure container (5c0a794586f77461c458f892: a big grid stuffed with spare ammo and meds the AI
        /// reloads from). A player has nothing like it: a PMC gets an empty Alpha container, a scav none, like a player scav.
        /// </summary>
        private static void MakePlayable(Player player)
        {
            Slot slot = player.Inventory.Equipment.GetSlot(EquipmentSlot.SecuredContainer);
            Item container = slot?.ContainedItem;
            if (container == null || container.StringTemplateId != BotContainer)
            {
                return;
            }

            int inside = container.GetAllItems().Count() - 1;
            slot.RemoveItemWithoutRestrictions();
            string given = "nothing (scav)";
            if (player.Profile.Info.Side != EPlayerSide.Savage)
            {
                Item alpha = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(true), AlphaContainer, null);
                if (alpha != null && slot.AddWithoutRestrictions(alpha).Succeeded)
                {
                    given = "an empty Alpha container";
                }
            }

            Log.Info($"LR Spectator: {player.Profile.Nickname}'s bot secure container ({inside} spare items) replaced with {given}");
        }

        /// <summary>
        /// The item in hands is set up for whoever held it when it was taken out (first person for you, third person for a bot): take it out
        /// again for the new holder - you after a take-over, its AI after you left it.
        /// </summary>
        private static void RefreshHands(Player player, bool forAi = false)
        {
            Item inHands = player.HandsController?.Item;
            player.SetEmptyHands(result =>
            {
                Diag.Guard("Spectator.RefreshHands", () =>
                {
                    if ((forAi ? Current == player : Current != player) || player.HealthController?.IsAlive != true)
                    {
                        return;
                    }

                    if (inHands is Weapon weapon && player.IsItemCanBeEquipped(weapon))
                    {
                        player.SetInHands(weapon, r => { });
                    }
                    else
                    {
                        player.TrySetLastEquippedWeapon();
                    }
                });
            });
        }

        /// <summary>An open exit of the bot's side whose zone the bot stands in (requirements checked for the bot, like for you).</summary>
        private static ExfiltrationPoint ExitUnderFeet(Player player)
        {
            ExfiltrationController exits = ExfiltrationController.Instance;
            if (exits == null)
            {
                return null;
            }

            IEnumerable<ExfiltrationPoint> points = player.Profile.Info.Side == EPlayerSide.Savage ? exits.ScavExfiltrationPoints : exits.ExfiltrationPoints;
            if (points == null)
            {
                return null;
            }

            Vector3 pos = player.Position + Vector3.up * 0.5f;
            foreach (ExfiltrationPoint point in points)
            {
                if (point == null || !IsOpen(point, player))
                {
                    continue;
                }

                BoxCollider zone = point.GetComponent<BoxCollider>();
                bool inside = zone != null
                    ? (zone.ClosestPoint(pos) - pos).sqrMagnitude < 0.01f
                    : (point.transform.position - player.Position).sqrMagnitude < 9f;
                if (inside)
                {
                    return point;
                }
            }

            return null;
        }

        private static bool IsOpen(ExfiltrationPoint point, Player player)
        {
            switch (point.Status)
            {
                case EExfiltrationStatus.RegularMode:
                case EExfiltrationStatus.Countdown:
                    return true;
                case EExfiltrationStatus.UncompleteRequirements:
                    try
                    {
                        return !point.UnmetRequirements(player).Any();
                    }
                    catch
                    {
                        return false;
                    }

                default:
                    return false;
            }
        }

        private static void CopyField(FieldInfo field, object from, object to)
        {
            object value = field?.GetValue(from);
            if (value != null)
            {
                field.SetValue(to, value);
            }
        }

        /// <summary>Puts the texture streaming back to the game's own settings (see <see cref="Release"/>).</summary>
        public static void RestoreTextureStreaming()
        {
            Try(() => EFT.Settings.Graphics.GraphicsSettingsController.RestoreMemoryBudget());
            Try(() =>
            {
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.method_3();
                }
            });
            Try(() => QualitySettings.streamingMipmapsAddAllCameras = true);
            Try(() => Texture.streamingTextureDiscardUnusedMips = true);
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                string at = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                Log.Detail("debug", $"LR Spectator: {e.GetType().Name} {e.Message} {at}");
            }
        }
    }
}
