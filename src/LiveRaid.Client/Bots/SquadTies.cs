using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// A squad stays a squad for the whole raid: no bot ever takes a member of the squad it spawned with for an enemy (F12 -> 9 "Squadmates are never
    /// enemies", on).
    ///
    /// Raid 2026-10-27 00:42: DeadW0Lf shot his leader Sianyde dead from 4 m. QuestingBots had "separated" Sianyde from the squad a minute before (121 s
    /// "not able-bodied" - low health): a new bot group of his own, which lists the old members as allies - but the old group does not list him, and
    /// EFT's BotsGroup.IsPlayerEnemy never looks at allies: an AI of another group is an enemy if the settings say so, and for PMCs they always do.
    /// From then on Sianyde was "another PMC squad" to DeadW0Lf; LiveRaid's "notice an enemy right next to you" added him to the group's enemies and SAIN
    /// opened fire. Seeing him would have done the same a little later (EFT adds every seen enemy-by-rule).
    ///
    /// Here the squad is taken at activation (the bot group a bot spawns in) and kept by profile, whatever QuestingBots or EFT do to the groups later:
    ///  - BotsGroup.AddEnemy and BotMemory.AddEnemy refuse a squadmate (hits, sight, sound, squad reports, LiveRaid's own code - every road in);
    ///  - BotsGroup.IsPlayerEnemy says "no" for a squadmate (after QuestingBots' and LiveRaid's own hostility rules);
    ///  - once a second: a squadmate found among a group's enemies or SAIN's enemies is taken off, and a squadmate in another group becomes an ally
    ///    of this one (EFT's own "friendly" checks see it then).
    /// </summary>
    public static class SquadTies
    {
        private static readonly Dictionary<string, int> _squadOf = new Dictionary<string, int>();
        private static readonly Dictionary<BotsGroup, int> _groupSquad = new Dictionary<BotsGroup, int>();
        private static readonly Dictionary<int, string> _squadNames = new Dictionary<int, string>();
        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static int _nextSquad = 1;
        private static float _next;

        public static int Refused { get; private set; }
        public static int Removed { get; private set; }
        public static int AlliesMade { get; private set; }

        public static string Summary => $"squadmates kept friends: enemy entries refused {Refused}, taken off {Removed}, split squads made allies again {AlliesMade}";

        public static void Clear()
        {
            _squadOf.Clear();
            _groupSquad.Clear();
            _squadNames.Clear();
            _noted.Clear();
            _nextSquad = 1;
            _next = 0f;
            Refused = Removed = AlliesMade = 0;
        }

        /// <summary>At activation: the bot belongs to the squad of the group it spawned in, for good.</summary>
        public static void Register(BotOwner bot)
        {
            if (bot?.ProfileId == null || _squadOf.ContainsKey(bot.ProfileId))
            {
                return;
            }

            BotsGroup group = bot.BotsGroup;
            if (group == null)
            {
                return;
            }

            if (!_groupSquad.TryGetValue(group, out int squad))
            {
                // A group made later (QuestingBots splitting a squad) whose members already have a squad keeps theirs
                squad = 0;
                for (int i = 0; i < group.MembersCount; i++)
                {
                    BotOwner m = group.Member(i);
                    if (m != null && m != bot && _squadOf.TryGetValue(m.ProfileId, out int s))
                    {
                        squad = s;
                        break;
                    }
                }

                if (squad == 0)
                {
                    squad = _nextSquad++;
                }

                _groupSquad[group] = squad;
            }

            _squadOf[bot.ProfileId] = squad;
            _squadNames[squad] = _squadNames.TryGetValue(squad, out string names) ? names + ", " + bot.Profile?.Nickname : bot.Profile?.Nickname;
        }

        public static bool Enabled => Settings.SquadmatesNeverEnemies.Value;

        /// <summary>Both are bots of the squad one of them spawned with.</summary>
        public static bool AreMates(string a, string b) =>
            a != null && b != null && a != b && _squadOf.TryGetValue(a, out int sa) && _squadOf.TryGetValue(b, out int sb) && sa == sb;

        /// <summary>The bot's spawn squad, or a number of its own when it has none on record.</summary>
        public static int SquadOf(string profileId) =>
            profileId != null && _squadOf.TryGetValue(profileId, out int s) ? s : -(profileId?.GetHashCode() & 0x3fffffff ?? 1) - 1;

        public static bool AreMates(IPlayer a, IPlayer b) => a != null && b != null && AreMates(a.ProfileId, b.ProfileId);

        /// <summary>Profile ids of the bot's spawn squad (itself included), alive or not.</summary>
        public static IEnumerable<string> MatesOf(string profileId)
        {
            if (profileId == null || !_squadOf.TryGetValue(profileId, out int squad))
            {
                yield break;
            }

            foreach (KeyValuePair<string, int> kv in _squadOf)
            {
                if (kv.Value == squad)
                {
                    yield return kv.Key;
                }
            }
        }

        /// <summary>The person is a squadmate of a member of this group (or of the bot the group was made for).</summary>
        public static bool MateOfGroup(BotsGroup group, IPlayer person, out BotOwner member)
        {
            member = null;
            if (!Enabled || group == null || person == null || !person.IsAI || !_squadOf.ContainsKey(person.ProfileId))
            {
                return false;
            }

            BotOwner initial = group._initialBot;
            if (initial != null && AreMates(initial.ProfileId, person.ProfileId))
            {
                member = initial;
                return true;
            }

            for (int i = 0; i < group.MembersCount; i++)
            {
                BotOwner m = group.Member(i);
                if (m != null && AreMates(m.ProfileId, person.ProfileId))
                {
                    member = m;
                    return true;
                }
            }

            return false;
        }

        internal static void NoteRefusal(BotOwner member, IPlayer mate, string how)
        {
            Refused++;
            string key = member?.ProfileId + "|" + mate?.ProfileId + "|" + how;
            if (_noted.Add(key))
            {
                string line = $"LR Squad: {member?.Profile?.Nickname} would have taken its squadmate {mate?.Profile?.Nickname} for an enemy ({how}) - refused, they are one squad";
                if (_noted.Count <= 30)
                {
                    Log.Info(line);
                }
                else
                {
                    Log.Detail("squad", () => line);
                }
            }
        }

        /// <summary>Once a second: squadmates taken off every enemy list they got onto, split squads made allies.</summary>
        public static void Tick()
        {
            if (!Enabled || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player p in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = p?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
                {
                    continue;
                }

                // Bots activated before LiveRaid's hook ran (or a raid joined late) still get their squad
                Register(bot);
                try
                {
                    Sweep(bot);
                }
                catch (System.Exception e)
                {
                    Diag.Report("SquadTies", e);
                }
            }
        }

        private static readonly List<IPlayer> _toRemove = new List<IPlayer>();

        private static void Sweep(BotOwner bot)
        {
            if (!_squadOf.ContainsKey(bot.ProfileId))
            {
                return;
            }

            BotsGroup group = bot.BotsGroup;
            if (group != null)
            {
                _toRemove.Clear();
                foreach (IPlayer enemy in group.Enemies.Keys)
                {
                    if (enemy != null && AreMates(bot.ProfileId, enemy.ProfileId))
                    {
                        _toRemove.Add(enemy);
                    }
                }

                foreach (IPlayer mate in _toRemove)
                {
                    group.RemoveEnemy(mate);
                    Removed++;
                    Log.Info($"LR Squad: {bot.Profile?.Nickname}'s group had its squadmate {mate.Profile?.Nickname} down as an enemy - taken off");
                }
            }

            if (bot.Memory?.GoalEnemy?.Person != null && AreMates(bot.ProfileId, bot.Memory.GoalEnemy.Person.ProfileId))
            {
                bot.Memory.DeleteInfoAboutEnemy(bot.Memory.GoalEnemy.Person);
                Removed++;
            }

            var sain = SainBot.Of(bot);
            if (sain != null)
            {
                foreach (string id in sain.EnemyController.Enemies.Keys.ToList())
                {
                    if (AreMates(bot.ProfileId, id))
                    {
                        sain.EnemyController.RemoveEnemy(id);
                        Removed++;
                        Log.Detail("squad", () => $"{bot.Profile?.Nickname}: SAIN had a squadmate down as an enemy - taken off");
                    }
                }
            }

            // A squadmate QuestingBots moved into a group of his own: an ally of this group again
            if (group != null)
            {
                foreach (string id in MatesOf(bot.ProfileId))
                {
                    if (id == bot.ProfileId)
                    {
                        continue;
                    }

                    Player mate = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(id);
                    BotOwner mateBot = mate?.AIData?.BotOwner;
                    if (mateBot != null && mateBot.BotsGroup != group && !group.Allies.Contains(mate))
                    {
                        group.AddAlly(mate);
                        AlliesMade++;
                        Log.Detail("squad", () => $"{bot.Profile?.Nickname}: squadmate {mate.Profile?.Nickname} is in a group of his own now - an ally all the same");
                    }
                }
            }
        }
    }

    /// <summary>A bot group never takes a squadmate of its members for an enemy (SquadTies).</summary>
    public class SquadTiesAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(BotsGroup __instance, IPlayer person, EBotEnemyCause cause, ref bool __result)
        {
            try
            {
                if (SquadTies.MateOfGroup(__instance, person, out BotOwner member))
                {
                    SquadTies.NoteRefusal(member, person, "group: " + cause);
                    __result = false;
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SquadTiesAddEnemyPatch", e);
            }

            return true;
        }
    }

    /// <summary>A bot's own memory never takes a squadmate for an enemy (SquadTies).</summary>
    public class SquadTiesMemoryPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMemory), nameof(BotMemory.AddEnemy));

        [PatchPrefix]
        protected static bool PatchPrefix(BotMemory __instance, IPlayer enemy)
        {
            try
            {
                BotOwner owner = __instance?._owner;
                if (SquadTies.Enabled && owner != null && enemy != null && SquadTies.AreMates(owner.ProfileId, enemy.ProfileId))
                {
                    SquadTies.NoteRefusal(owner, enemy, "own memory");
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SquadTiesMemoryPatch", e);
            }

            return true;
        }
    }

    /// <summary>A squadmate is never "an enemy by the rules" - checked after every other hostility rule (SquadTies).</summary>
    public class SquadTiesIsEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        protected static void PatchPostfix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            try
            {
                if (SquadTies.MateOfGroup(__instance, player, out _))
                {
                    __result = false;
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SquadTiesIsEnemyPatch", e);
            }
        }
    }
}
