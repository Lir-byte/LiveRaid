using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SPT.Reflection.Patching;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// The packrat only ever comes alone (F12 -> 6 "Packrat chance, %"). The server cannot know which PMCs will be alone: the client's request for one PMC
    /// brings the role's preset limit of them (15) into the client's pool of profiles (<c>ABotProfileCreator._simpleProfiles</c>), and QuestingBots forms its
    /// groups from that pool, one random profile at a time. Raids of 2026-11-02: the server gave the kit to a "lone" bot every time, and the packrat never
    /// came - its profile stayed in the pool, or went into a group of three.
    ///  - the server tags the first PMC of each side in an armed raid (Packrat.Refresh brings their ids);
    ///  - <see cref="QbGroupPlanPatch"/>: when QuestingBots is about to generate a group, and the packrat's profile is waiting in the pool, one of the first
    ///    groups (a natural group of one, else the 2nd-4th group) is made a group of ONE of the profile's side and difficulty;
    ///  - <see cref="PoolProfilePatch"/>: a lone group takes the tagged profile out of the pool; a group of two or more never gets one - it is put back and
    ///    another profile is drawn.
    /// </summary>
    public static class PackratPool
    {
        private static readonly FieldInfo PoolField = AccessTools.Field(typeof(ABotProfileCreator), "_simpleProfiles");
        private static readonly ConditionalWeakTable<object, StrongBox<int>> Sizes = new ConditionalWeakTable<object, StrongBox<int>>();
        private static int _groups;
        private static int _forceAt = -1;
        private static bool _planned;

        public static bool Active { get; internal set; }
        public static int Forced { get; private set; }
        public static int Given { get; private set; }
        public static int Kept { get; private set; }

        public static string Summary => $"packrat pool: {(Packrat.Armed ? "armed" : "not armed")}, lone groups made for it {Forced}, given to a lone group {Given}, kept out of groups {Kept}";

        public static void Clear()
        {
            _groups = 0;
            _forceAt = -1;
            _planned = false;
            Forced = Given = Kept = 0;
        }

        private static bool IsPmc(WildSpawnType type) => type == WildSpawnType.pmcBEAR || type == WildSpawnType.pmcUSEC;

        private static List<Profile> Pool()
        {
            object creator = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner?._botCreator;
            return creator is ABotProfileCreator ? PoolField?.GetValue(creator) as List<Profile> : null;
        }

        /// <summary>QuestingBots is about to generate a group of <paramref name="bots"/> PMCs.</summary>
        public static void Plan(ref WildSpawnType type, ref BotDifficulty difficulty, ref int bots)
        {
            if (!IsPmc(type) || !Packrat.Wanted)
            {
                return;
            }

            int index = _groups++;
            Packrat.Refresh(index == 0);
            if (!Packrat.Armed || Packrat.Assigned != null || _planned)
            {
                return;
            }

            Profile tagged = Pool()?.FirstOrDefault(p => p != null && Packrat.IsTagged(p.Id));
            if (tagged?.Info?.Settings == null)
            {
                return;
            }

            if (_forceAt < 0)
            {
                _forceAt = UnityEngine.Random.Range(1, 4);
            }

            bool natural = bots == 1;
            if (!natural && index < _forceAt)
            {
                return;
            }

            if (!natural)
            {
                Forced++;
            }

            bots = 1;
            type = tagged.Info.Settings.Role;
            difficulty = tagged.Info.Settings.BotDifficulty;
            _planned = true;
            Log.Info($"LR Packrat: PMC group no. {index + 1} is made a group of one for the raid's packrat ({tagged.Nickname}, {type} {difficulty}{(natural ? ", it was one anyway" : "")})");
        }

        /// <summary>QuestingBots asked for <paramref name="count"/> profiles for this request.</summary>
        public static void NoteSize(object profileData, int count)
        {
            if (profileData != null)
            {
                Sizes.Remove(profileData);
                Sizes.Add(profileData, new StrongBox<int>(count));
            }
        }

        /// <summary>A profile was drawn from the pool for <paramref name="data"/>: the packrat's goes to a lone group only.</summary>
        public static void Drawn(ABotProfileCreator creator, BotCreationData data, bool withDelete, ref Profile result)
        {
            if (!withDelete || !Packrat.Wanted || data?._profileData == null || !data._profileData.TryGetRole(out WildSpawnType role, out _) || !IsPmc(role))
            {
                return;
            }

            List<Profile> pool = PoolField?.GetValue(creator) as List<Profile>;
            if (pool == null)
            {
                return;
            }

            Packrat.Refresh();
            int size = Sizes.TryGetValue(data._profileData, out StrongBox<int> box) ? box.Value : -1;
            bool lone = size == 1;
            bool free = Packrat.Armed && Packrat.Assigned == null;

            if (result != null && Packrat.IsTagged(result.Id))
            {
                if (lone && free)
                {
                    Packrat.Assign(result.Id);
                    Given++;
                    Log.Info($"LR Packrat: the packrat's profile {result.Nickname} goes to a lone PMC");
                    return;
                }

                // Not for a group: back into the pool (QuestingBots' draw may or may not have taken it out), another one drawn
                if (!pool.Contains(result))
                {
                    pool.Add(result);
                }

                List<Profile> others = pool.Where(p => p != null && !Packrat.IsTagged(p.Id)).ToList();
                Profile other = others.Count > 0 ? data.ChooseProfile(others, false) : null;
                if (other != null)
                {
                    pool.Remove(other);
                }

                Kept++;
                result = other;
                return;
            }

            if (result != null && lone && free)
            {
                List<Profile> tagged = pool.Where(p => p != null && Packrat.IsTagged(p.Id)).ToList();
                Profile mine = tagged.Count > 0 ? data.ChooseProfile(tagged, false) : null;
                if (mine != null)
                {
                    pool.Remove(mine);
                    if (!pool.Contains(result))
                    {
                        pool.Add(result);
                    }

                    result = mine;
                    Packrat.Assign(mine.Id);
                    Given++;
                    Log.Info($"LR Packrat: the packrat's profile {mine.Nickname} goes to a lone PMC");
                }
            }
        }
    }

    /// <summary>QuestingBots: a group of PMCs is about to be generated.</summary>
    public class QbGroupPlanPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            Type generator = AccessTools.TypeByName("QuestingBots.Components.Spawning.BotGenerator");
            return generator == null ? null : AccessTools.Method(generator, "GenerateBotGroup");
        }

        [PatchPrefix]
        protected static void PatchPrefix(ref WildSpawnType spawnType, ref BotDifficulty botdifficulty, ref int bots)
        {
            try
            {
                PackratPool.Plan(ref spawnType, ref botdifficulty, ref bots);
            }
            catch (Exception e)
            {
                Diag.Report("QbGroupPlanPatch", e);
            }
        }
    }

    /// <summary>EFT: profiles for a request are asked for (how many is not kept anywhere else).</summary>
    public class BotCreateSizePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotCreationData), nameof(BotCreationData.Create));

        [PatchPrefix]
        protected static void PatchPrefix(IGetProfileData profileData, int count)
        {
            try
            {
                PackratPool.NoteSize(profileData, count);
            }
            catch (Exception e)
            {
                Diag.Report("BotCreateSizePatch", e);
            }
        }
    }

    /// <summary>EFT / QuestingBots: a profile is drawn from the client's pool for a bot (runs after QuestingBots' own draw).</summary>
    public class PoolProfilePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(ABotProfileCreator).GetMethod(nameof(ABotProfileCreator.GetNewProfile), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(BotCreationData), typeof(bool) }, null);

        [PatchPostfix]
        protected static void PatchPostfix(ABotProfileCreator __instance, ref Profile __result, BotCreationData data, bool withDelete)
        {
            try
            {
                PackratPool.Drawn(__instance, data, withDelete, ref __result);
            }
            catch (Exception e)
            {
                Diag.Report("PoolProfilePatch", e);
            }
        }
    }
}
