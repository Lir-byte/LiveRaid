using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using DrakiaXYZ.BigBrain.Internal;
using EFT;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The fighters of the arena event shoot back (F12 -> 9 "Arena fighters fight back", 1.3.1; made exact in 1.3.2). Raid 2026-11-09 20:16: five arena fighters (Hardbass, Mikhalych, Kulyok, Garry Potter,
    /// Lobster - role arenaFighterEvent, "Other" to LiveRaid) were hit by PMCs 18 times and answered 0 times (one shot at a player scav all raid). In 61 samples with an enemy in view
    /// within 30 m their layer was PatrolFollower / PatrolAssault - never a fight layer - while SAIN's decision said StandAndShoot; LiveRaid's "aims at X for 6 s without a shot" watch fired
    /// 16 times on them with "nothing wrong found with the gun". The reason is in SAIN's BigBrainHandler: for the role arenaFighterEvent (brain "PMC") it only REMOVES the vanilla fight layers
    /// (ToggleVanillaLayersForRaiders) and adds SAIN's own layers to the PMC roles and to the "ArenaFighter" brain, not to this one - so the bot has no layer that shoots.
    ///
    /// 1.3.1 put the layers back with BigBrain's RestoreLayers(layers, brains, roles). That was too wide: BigBrain's restore deletes EVERY exclusion that has one of the brains AND one of the
    /// roles in common, and when the sets are not identical it does not split the exclusion first - SAIN's exclusion for "PMC" + all PMC roles went away with the arena role in it, and in the
    /// raid of 22:04 every PMC (PogChamp, Gazelist, Agent_47, Shadowbanned...) ran the vanilla AdvAssaultTarget / AssaultHaveEnemy next to SAIN, a squad flipped Looting &lt;-&gt; AdvAssaultTarget
    /// around one corpse and loot jobs were "cut short from outside" every few seconds. Now the exclusion list is cut by hand: from each exclusion of these layers only the cell
    /// (brain "PMC" x role arenaFighterEvent) is carved out, everything else stays excluded.
    /// </summary>
    public static class ArenaFightFix
    {
        // The vanilla layers SAIN removes (BigBrainHandler._commonVanillaLayersToRemove + "AssaultEnemyFar")
        private static readonly HashSet<string> Layers = new HashSet<string>
        {
            "AssaultEnemyFar", "GroupForce", "AdvAssaultTarget", "Simple Target", "Pmc", "AssaultHaveEnemy", "PushAndSup", "Pursuit", "Kill logic",
        };

        private const string Brain = "PMC";
        private const WildSpawnType Role = WildSpawnType.arenaFighterEvent;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static bool Applied { get; private set; }
        public static int Carved { get; private set; }
        public static bool PmcsStillExcluded { get; private set; } = true;

        public static string Summary => Applied
            ? $"arena fighters: {Carved} exclusion(s) of vanilla fight layers carved for them alone, PMCs still without them: {PmcsStillExcluded}"
            : "arena fighters: not touched";

        private static List<BrainManager.ExcludeLayerInfo> ExclusionList()
        {
            object manager = typeof(BrainManager).GetProperty("Instance", Any)?.GetValue(null);
            return manager == null ? null : typeof(BrainManager).GetField("ExcludeLayers", Any)?.GetValue(manager) as List<BrainManager.ExcludeLayerInfo>;
        }

        private static readonly FieldInfo BrainsField = typeof(AbstractLayerInfo).GetField("brainNames", Any);
        private static readonly FieldInfo RolesField = typeof(AbstractLayerInfo).GetField("roles", Any);

        private static bool Excluded(List<BrainManager.ExcludeLayerInfo> list, string layer, string brain, WildSpawnType role)
        {
            return list.Any(e => e.excludeLayerName == layer && ((List<string>)BrainsField.GetValue(e)).Contains(brain) && ((List<WildSpawnType>)RolesField.GetValue(e)).Contains(role));
        }

        /// <summary>The cell (Brain x Role) is cut out of the rectangle brains x roles: what is left is (other brains x all roles) and (Brain x other roles).</summary>
        private static List<KeyValuePair<List<string>, List<WildSpawnType>>> CutCell(List<string> brains, List<WildSpawnType> roles)
        {
            List<KeyValuePair<List<string>, List<WildSpawnType>>> pieces = new List<KeyValuePair<List<string>, List<WildSpawnType>>>();
            List<string> otherBrains = brains.Where(b => b != Brain).ToList();
            if (otherBrains.Count > 0)
            {
                pieces.Add(new KeyValuePair<List<string>, List<WildSpawnType>>(otherBrains, new List<WildSpawnType>(roles)));
            }

            List<WildSpawnType> otherRoles = roles.Where(r => r != Role).ToList();
            if (otherRoles.Count > 0)
            {
                pieces.Add(new KeyValuePair<List<string>, List<WildSpawnType>>(new List<string> { Brain }, otherRoles));
            }

            return pieces;
        }

        /// <summary>Cuts the cell (brain "PMC" x role arenaFighterEvent) out of every exclusion of the vanilla fight layers; returns how many exclusions were cut.</summary>
        private static int Carve(List<BrainManager.ExcludeLayerInfo> list)
        {
            int carved = 0;
            foreach (BrainManager.ExcludeLayerInfo e in list.ToArray())
            {
                if (!Layers.Contains(e.excludeLayerName))
                {
                    continue;
                }

                List<string> brains = (List<string>)BrainsField.GetValue(e);
                List<WildSpawnType> roles = (List<WildSpawnType>)RolesField.GetValue(e);
                if (brains == null || roles == null || !brains.Contains(Brain) || !roles.Contains(Role))
                {
                    continue;
                }

                list.Remove(e);
                foreach (KeyValuePair<List<string>, List<WildSpawnType>> piece in CutCell(brains, roles))
                {
                    list.Add(new BrainManager.ExcludeLayerInfo(e.excludeLayerName, piece.Key, piece.Value));
                }

                carved++;
            }

            return carved;
        }

        public static void Apply()
        {
            if (!Settings.Enabled.Value || !Settings.ArenaFightFix.Value)
            {
                return;
            }

            try
            {
                List<BrainManager.ExcludeLayerInfo> list = ExclusionList();
                if (list == null || BrainsField == null || RolesField == null)
                {
                    Log.Info("LR Arena: BigBrain's exclusion list was not found - the arena fighters are left as SAIN made them");
                    return;
                }

                int carved = Carve(list);
                Carved += carved;

                // A check that the carving did not touch the PMCs: AdvAssaultTarget is still excluded for brain PMC and the role pmcBEAR
                PmcsStillExcluded = Excluded(list, "AdvAssaultTarget", Brain, WildSpawnType.pmcBEAR);

                if (!Applied || carved > 0)
                {
                    Log.Info($"LR Arena: {carved} exclusion(s) of vanilla fight layers carved for the arena fighters alone (SAIN had taken the layers away and given them none of its own); " +
                             $"the PMCs keep SAIN's layers only: {PmcsStillExcluded}");
                }

                Applied = true;
            }
            catch (Exception e)
            {
                Diag.Report("ArenaFightFix", e);
            }
        }

        /// <summary>Outside the game (tools/bindtest; BigBrain itself cannot start there): the cut is checked on the rectangles SAIN makes - every cell stays excluded except (PMC, arena fighter).</summary>
        public static string SelfTest()
        {
            WildSpawnType[] all = Enum.GetValues(typeof(WildSpawnType)).Cast<WildSpawnType>().ToArray();
            string[] brainNames = { "PMC", "PmcBear", "PmcUsec", "Assault", "ArenaFighter" };
            List<KeyValuePair<List<string>, List<WildSpawnType>>> sain = new List<KeyValuePair<List<string>, List<WildSpawnType>>>
            {
                new KeyValuePair<List<string>, List<WildSpawnType>>(new List<string> { "PMC", "PmcBear", "PmcUsec" }, all.ToList()),
                new KeyValuePair<List<string>, List<WildSpawnType>>(new List<string> { "PMC" }, new List<WildSpawnType> { WildSpawnType.pmcBEAR, WildSpawnType.pmcUSEC }),
                new KeyValuePair<List<string>, List<WildSpawnType>>(new List<string> { "PMC" }, new List<WildSpawnType> { WildSpawnType.arenaFighterEvent }),
                new KeyValuePair<List<string>, List<WildSpawnType>>(new List<string> { "Assault" }, all.ToList()),
            };

            int cut = 0, bad = 0, cells = 0;
            List<KeyValuePair<List<string>, List<WildSpawnType>>> after = new List<KeyValuePair<List<string>, List<WildSpawnType>>>();
            foreach (KeyValuePair<List<string>, List<WildSpawnType>> r in sain)
            {
                if (r.Key.Contains(Brain) && r.Value.Contains(Role))
                {
                    cut++;
                    after.AddRange(CutCell(r.Key, r.Value));
                }
                else
                {
                    after.Add(r);
                }
            }

            foreach (string b in brainNames)
            {
                foreach (WildSpawnType role in all)
                {
                    bool before = sain.Any(r => r.Key.Contains(b) && r.Value.Contains(role));
                    bool now = after.Any(r => r.Key.Contains(b) && r.Value.Contains(role));
                    bool expected = before && !(b == Brain && role == Role);
                    cells++;
                    if (now != expected)
                    {
                        bad++;
                    }
                }
            }

            return (bad == 0 && cut > 0 ? "ARENA OK" : "ARENA BAD") + $": {cut} of {sain.Count} rectangles cut, {bad} wrong of {cells} cells";
        }
    }
}
