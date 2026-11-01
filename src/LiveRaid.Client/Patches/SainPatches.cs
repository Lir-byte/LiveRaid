using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Tactics;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Models.Preset.Personalities;
using SAIN.Preset.Shared.Models.Preset.Personalities;
using SAIN.Preset.Shared.Personalities.BasePersonality;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Info;
using SPT.Reflection.Patching;
using UnityEngine;
using BotProfile = LiveRaid.Client.Bots.BotProfile;

namespace LiveRaid.Client.Patches
{
    /// <summary>SAIN personality follows the bot's LiveRaid character (camper -> SnappingTurtle, rat -> Rat, novice -> Timmy...).</summary>
    public class SainPersonalityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PersonalityDictionary), nameof(PersonalityDictionary.GetPersonality));

        [PatchPrefix]
        protected static bool PatchPrefix(PersonalityDictionary __instance, SAINBotInfoClass infoClass, ref PersonalitySettingsClass settings, ref EPersonality __result)
        {
            try
            {
                return RunPrefix(__instance, infoClass, ref settings, ref __result);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainPersonalityPatch", e);
                return true;
            }
        }

        private static bool RunPrefix(PersonalityDictionary __instance, SAINBotInfoClass infoClass, ref PersonalitySettingsClass settings, ref EPersonality __result)
        {
            if (!Settings.Personalities.Value)
            {
                return true;
            }

            BotOwner bot = infoClass?.BotOwner;
            BotProfile profile = bot != null ? BotProfiles.Get(bot) : null;
            if (profile == null || profile.Kind == BotKind.Other)
            {
                return true;
            }

            if (!__instance.TryGetValue(profile.SainPersonality, out PersonalitySettingsClass chosen) || chosen == null)
            {
                return true;
            }

            settings = chosen;
            __result = profile.SainPersonality;
            return false;
        }
    }

    /// <summary>
    /// SAIN sets how long a bot waits before searching and how long it remembers an enemy.
    /// Characters change both: rushers push sooner, rats and campers wait, hunters remember for longer.
    /// </summary>
    public class SainSearchTimePatch : ModulePatch
    {
        private static readonly MethodInfo SetTimeBeforeSearch = AccessTools.PropertySetter(typeof(SAINBotInfoClass), nameof(SAINBotInfoClass.TimeBeforeSearch));
        private static readonly MethodInfo SetForgetEnemyTime = AccessTools.PropertySetter(typeof(SAINBotInfoClass), nameof(SAINBotInfoClass.ForgetEnemyTime));

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SAINBotInfoClass), nameof(SAINBotInfoClass.CalcTimeBeforeSearch));

        [PatchPostfix]
        protected static void PatchPostfix(SAINBotInfoClass __instance)
        {
            try
            {
                RunPostfix(__instance);
            }
            catch (System.Exception e)
            {
                Diag.Report("SainSearchTimePatch", e);
            }
        }

        private static void RunPostfix(SAINBotInfoClass __instance)
        {
            BotOwner bot = __instance.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other)
            {
                return;
            }

            float searchMult;
            switch (p.Style)
            {
                case PlayStyle.Rusher: searchMult = 0.6f; break;
                case PlayStyle.Hunter: searchMult = 0.75f; break;
                case PlayStyle.Rat:
                case PlayStyle.Camper:
                case PlayStyle.Sniper: searchMult = 1.5f; break;
                default: searchMult = 1f; break;
            }

            // The SAIN personality already carries the character's patience (Rat 240 s, turtle and Timmy 90 s, Normal 60 s):
            // the style multiplier is not applied a second time on top of it (a rat waited up to 6 minutes for silence)
            if (p.SainPersonality == EPersonality.Rat || p.SainPersonality == EPersonality.SnappingTurtle
                || p.SainPersonality == EPersonality.Timmy || p.SainPersonality == EPersonality.Coward)
            {
                searchMult = System.Math.Min(searchMult, 1f);
            }

            float search = __instance.TimeBeforeSearch * searchMult;

            // SAIN starts a search only after the enemy has not been heard for this long, so every shot nearby restarts the wait.
            // Nobody holds a corner for silence longer than a few minutes: past the ceiling the bot goes to look
            float ceiling = Settings.MaxSearchWait.Value;
            if (ceiling > 0f)
            {
                search = System.Math.Min(search, ceiling * (0.8f + 0.4f * UnityEngine.Random.value));
            }
            float forget = (__instance.ForgetEnemyTime - __instance.TimeBeforeSearch) * Settings.MemoryMultiplier.Value * p.Memory + search;

            SetTimeBeforeSearch?.Invoke(__instance, new object[] { search });
            SetForgetEnemyTime?.Invoke(__instance, new object[] { forget });
            bot.Settings.FileSettings.Mind.TIME_TO_FORGOR_ABOUT_ENEMY_SEC = forget;
        }
    }

    /// <summary>
    /// SAIN keeps everyone it ever heard of (a shot, a squadmate's report) as a known enemy, however far and however unseen.
    /// On a busy map that kept a third of all bots standing in SAIN's combat layers for minutes over fights 100-250 m away,
    /// never questing or looting, and our own reaction to gunfire (which waits for calm bots) never got its turn.
    /// Here such an enemy - far, not seen by this bot in the last minute, not shooting at it - is not "known" to SAIN;
    /// the fight still reaches the bot through GunfireResponse (hold, approach, flank, leave... by character).
    /// Once the enemy comes closer, is seen, or shoots at the bot, SAIN has it again at once.
    /// </summary>
    public class SainDistantEnemyPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<EnemyKnownChecker, Enemy> EnemyField =
            AccessTools.FieldRefAccess<EnemyKnownChecker, Enemy>("Enemy");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EnemyKnownChecker), "ShallKnowEnemy");

        [PatchPostfix]
        protected static void PatchPostfix(EnemyKnownChecker __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            try
            {
                if (IsSomeoneElsesFight(EnemyField(__instance)))
                {
                    __result = false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainDistantEnemyPatch", e);
            }
        }

        private struct Latch
        {
            public bool Distant;
            public float Since;
        }

        // Per bot and enemy: the last answer and since when. Without it an enemy walking about the threshold (100 m) was "known" and
        // "unknown" on alternate checks, and the bot jumped between SAIN's combat layer and its quest every second
        private static readonly Dictionary<string, Latch> _latches = new Dictionary<string, Latch>();
        private const float LatchSeconds = 8f;

        public static void Clear()
        {
            _latches.Clear();
            _floorLatches.Clear();
        }

        private static readonly Dictionary<string, (bool apart, float since)> _floorLatches = new Dictionary<string, (bool, float)>();

        /// <summary>
        /// The enemy is on another storey (3+ m of height between the bot and where it believes he is, near enough for walls and ceilings to matter), is not in sight,
        /// has not shot at the bot or hit it: the fight above or below is not the bot's business. The answer holds for 6 s (the height wobbles on stairs).
        /// </summary>
        public static bool FloorApart(Enemy enemy)
        {
            BotOwner bot = enemy?.BotOwner;
            if (bot == null)
            {
                return false;
            }

            BotProfile p = BotProfiles.Get(bot);
            if (p == null || p.Kind == BotKind.Other)
            {
                return false;
            }

            // Real contact is always its fight
            if (enemy.IsVisible || enemy.Status.ShotAtMeRecently || bot.Memory?.IsUnderFire == true || HitReaction.SinceHit(bot) < 10f || (enemy.Seen && enemy.TimeSinceSeen < 8f))
            {
                _floorLatches.Remove(bot.ProfileId + enemy.EnemyProfileId);
                return false;
            }

            string key = bot.ProfileId + enemy.EnemyProfileId;
            if (_floorLatches.TryGetValue(key, out var latch) && Time.time - latch.since < 6f)
            {
                return latch.apart;
            }

            Vector3 at = enemy.LastKnownPosition ?? enemy.EnemyPosition;
            Vector3 d = at - bot.Position;
            float dy = Mathf.Abs(d.y);
            d.y = 0f;
            float flat = d.magnitude;
            bool apart = (dy >= 3.2f && flat < 45f) || (dy >= 6f && flat < 100f);
            _floorLatches[key] = (apart, Time.time);
            return apart;
        }

        public static bool IsSomeoneElsesFight(Enemy enemy)
        {
            if (enemy == null)
            {
                return false;
            }

            // A fight that went cold (nobody seen or shooting for the bot's patience) is over for the bot, near or far
            if (EnemyLapse.IsLapsed(enemy))
            {
                return true;
            }

            // A fight over or under the bot's head is not its fight until it comes down the stairs, shows itself or shoots at the bot
            if (Settings.FloorAware.Value && FloorApart(enemy))
            {
                return true;
            }

            // A looter, a rat, a quiet squad leaves a far AI scav alone that is not shooting at it (PlayerSense)
            if (PlayerSense.LeavesScavAlone(enemy.BotOwner, enemy))
            {
                return true;
            }

            float range = Settings.DistantFightRange.Value;
            if (range <= 0f)
            {
                return false;
            }

            BotOwner bot = enemy.BotOwner;
            BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
            if (p == null || p.Kind == BotKind.Other)
            {
                return false;
            }

            string key = bot.ProfileId + enemy.EnemyProfileId;
            bool hasLatch = _latches.TryGetValue(key, out Latch latch);

            // Shooting at the bot or seen lately: always its fight, at once
            if (enemy.Status.ShotAtMeRecently || (enemy.Seen && enemy.TimeSinceSeen < 60f) || bot.Memory?.IsUnderFire == true)
            {
                _latches[key] = new Latch { Distant = false, Since = Time.time };
                return false;
            }

            float dist = enemy.RealDistance;
            bool distant;
            if (!hasLatch)
            {
                distant = dist >= range;
            }
            else if (Time.time - latch.Since < LatchSeconds)
            {
                distant = latch.Distant;
            }
            else
            {
                // A band around the threshold: once someone else's fight it stays so until clearly closer, and the other way round
                distant = latch.Distant ? dist >= range * 0.85f : dist > range * 1.25f;
            }

            if (!hasLatch || distant != latch.Distant)
            {
                _latches[key] = new Latch { Distant = distant, Since = Time.time };
            }

            return distant;
        }
    }

    /// <summary>
    /// SAIN's search creeps: a "stealthy" search outdoors walks at half speed crouched (speed 0.5, pose 0.7), indoors at a third, "sneaky"
    /// personalities at a quarter (0.25 / 0.6), and at every search point it stands 1-4 s. A bot that "hunts" this way crossed a block in a
    /// minute and a half and looked like it was patrolling the same building for the whole raid (vargrasen, raid 14:21, 4-5 m per 5 s).
    /// Online a player who goes to look for someone walks normally, turns the corners with the gun up and looks around for a moment.
    /// </summary>
    public class SearchPacePatch : ModulePatch
    {
        public static int Quickened { get; private set; }
        public static string Summary => $"(creeping made a normal walk {Quickened} times, waits at points shortened {SearchWaitPatch.Shortened})";

        public static void Clear()
        {
            Quickened = 0;
            SearchWaitPatch.Shortened = 0;
        }

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAIN.Classes.Bot.Search.SearchClass), "GetSpeedAndPose");

        [PatchPostfix]
        protected static void PatchPostfix(SAIN.Classes.Bot.Search.SearchClass __instance, ref float speed, ref float pose)
        {
            try
            {
                if (!Settings.SearchFullPace.Value || (speed >= 1f && pose >= 1f))
                {
                    return;
                }

                BotOwner bot = __instance.BotOwner;
                BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind == BotKind.Other)
                {
                    return;
                }

                speed = 1f;
                pose = 1f;
                Quickened++;
            }
            catch (System.Exception e)
            {
                Diag.Report("SearchPacePatch", e);
            }
        }
    }

    /// <summary>The pause at each search point: a look round, not a stand (SAIN: 3 s x personality x 0.25-1.25).</summary>
    public class SearchWaitPatch : ModulePatch
    {
        public static int Shortened;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAIN.Classes.Bot.Search.SearchClass), "WaitAtPoint");

        [PatchPrefix]
        protected static void PatchPrefix(SAIN.Classes.Bot.Search.SearchClass __instance, ref float ____waitAtPointTimer)
        {
            try
            {
                if (!Settings.SearchFullPace.Value || ____waitAtPointTimer >= 0f)
                {
                    return;
                }

                BotOwner bot = __instance.BotOwner;
                BotProfile p = bot != null ? BotProfiles.Get(bot) : null;
                if (p == null || p.Kind == BotKind.Other)
                {
                    return;
                }

                // The original starts its timer only when it is not running yet: start a short one here instead
                float wait = 0.3f + UnityEngine.Random.value * 0.7f;
                ____waitAtPointTimer = Time.time + wait;
                __instance.Bot.Mover.ActivePath?.Pause(wait);
                Shortened++;
            }
            catch (System.Exception e)
            {
                Diag.Report("SearchWaitPatch", e);
            }
        }
    }

    /// <summary>
    /// No close-quarters mode against a far enemy (F12 -> 9 "No dogfight with far enemies"). SAIN starts its dogfight (stand, strafe, shoot - no cover,
    /// no moving off) when the path to the enemy is under 10 m. But the path length is left at 0 when no path could be built (the bot or the enemy off the
    /// navmesh, on a roof, an island): raid 2026-10-31 15:59, U_Mad_Bro stood 70 s on one spot "dogfighting" enemies 90-115 m away until he was shot,
    /// and 27 of 46 dogfight samples had the enemy beyond 40 m. A path cannot be shorter than the straight line to the same point: when it is, it is not
    /// a path, and the enemy is not close.
    /// </summary>
    public class SainDogFightPatch : ModulePatch
    {
        public static int Refused { get; private set; }

        private static readonly Dictionary<string, float> _noteAt = new Dictionary<string, float>();

        public static void Clear()
        {
            Refused = 0;
            _noteAt.Clear();
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SAIN.SAINComponent.Classes.Decision.DogFightDecisionClass), "ShallDogfightEnemy");

        [PatchPostfix]
        protected static void PatchPostfix(Enemy enemy, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            try
            {
                if (PathLies(enemy))
                {
                    __result = false;
                    Refused++;
                    string key = enemy.BotOwner?.ProfileId ?? "";
                    if (!_noteAt.TryGetValue(key, out float at) || Time.time >= at)
                    {
                        _noteAt[key] = Time.time + 30f;
                        Log.Detail("combat", () => $"{enemy.BotOwner?.Profile?.Nickname}: no dogfight with {enemy.EnemyPlayer?.Profile?.Nickname} - " +
                                                   $"{enemy.RealDistance:0} m away, SAIN's path to him says {enemy.Path.PathLength:0} m (no path)");
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainDogFightPatch", e);
            }
        }

        /// <summary>The path SAIN measured to where the bot believes the enemy is is shorter than the straight line there - no path was built.</summary>
        internal static bool PathLies(Enemy enemy)
        {
            if (!Settings.Enabled.Value || !Settings.DogFightSanity.Value || enemy?.Path == null)
            {
                return false;
            }

            BotOwner bot = enemy.BotOwner;
            Vector3? at = enemy.LastKnownPosition;
            if (bot == null || at == null)
            {
                return false;
            }

            return (at.Value - bot.Position).magnitude > enemy.Path.PathLength + 3f;
        }
    }

    /// <summary>The dogfight target SAIN already has is let go when its "path" is no path (else the old target kept the dogfight on - see above).</summary>
    public class SainDogFightClearPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SAIN.SAINComponent.Classes.Decision.DogFightDecisionClass), "shallClearDogfightTarget");

        [PatchPostfix]
        protected static void PatchPostfix(Enemy enemy, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            try
            {
                if (SainDogFightPatch.PathLies(enemy))
                {
                    __result = true;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SainDogFightClearPatch", e);
            }
        }
    }
}
