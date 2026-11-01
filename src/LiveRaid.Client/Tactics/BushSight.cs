using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRaid.Client.Bots;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;
using Unity.Collections;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Bushes between two bots (F12 -> 9 "Bots see and shoot through bushes at each other"). SAIN casts three rays per body part: line of sight
    /// (terrain, walls and foliage), vision (foliage and grass) and the shot (terrain, walls, grass and foliage) - so a bush hides a man completely and
    /// no round goes through it, and grass stops a shot too. That keeps bots from "seeing" the player through leaves, but between two bots it made
    /// stand-offs a player would not have: raid 2026-10-31 15:59, Hackerman and AshleyShcaefferBMW stood 3-30 m apart by the river for 28 s before the
    /// first shot, each knowing where the other was. In Tarkov a round goes through leaves, and a man moving behind a bush close by is seen through it.
    /// For a bot looking at another bot (not at you), here:
    ///  - up to 25 m a bush does not hide the other (SAIN's own time to notice still applies), grass hides nothing up to 10 m;
    ///  - a shot goes through leaves up to 25 m, and up to 60 m at a man seen in the last 3 s (fire into the bush he went into); through grass up to 30 m.
    /// Done by taking the foliage / grass layers out of those rays' masks when SAIN builds them.
    /// </summary>
    public class BushSightPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<VisionRaycastJob, List<Enemy>> EnemiesField =
            AccessTools.FieldRefAccess<VisionRaycastJob, List<Enemy>>("_enemies");

        public const float SeeThrough = 25f;
        public const float SeeThroughGrass = 10f;
        public const float ShootThrough = 25f;
        public const float ShootThroughSeen = 60f;
        public const float ShootThroughGrass = 30f;

        public static int Opened { get; private set; }

        public static string Summary => $"bot-to-bot sight checks through bushes or grass {Opened}";

        public static void Clear() => Opened = 0;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(VisionRaycastJob), "CreateCommands");

        [PatchPostfix]
        protected static void PatchPostfix(VisionRaycastJob __instance, NativeArray<RaycastCommand> raycastCommands, int enemyCount)
        {
            if (!Settings.Enabled.Value || !Settings.BushSight.Value)
            {
                return;
            }

            try
            {
                Open(EnemiesField(__instance), raycastCommands, enemyCount);
            }
            catch (System.Exception e)
            {
                Diag.Report("BushSightPatch", e);
            }
        }

        private static void Open(List<Enemy> enemies, NativeArray<RaycastCommand> commands, int enemyCount)
        {
            if (enemies == null)
            {
                return;
            }

            int foliage = LayersMaskController.Foliage;
            int grass = LayersMaskController.Grass;
            int c = 0;
            for (int i = 0; i < enemyCount && i < enemies.Count; i++)
            {
                Enemy enemy = enemies[i];
                int parts = enemy.Vision.EnemyParts.PartsArray.Length;
                int first = c;
                c += parts * 3;
                if (!enemy.IsAI || enemy.BotOwner == null || c > commands.Length)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(enemy.BotOwner);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                float d = enemy.RealDistance;
                bool seenJustNow = enemy.Seen && enemy.TimeSinceSeen < 3f;
                int sightCut = (d <= SeeThrough ? foliage : 0) | (d <= SeeThroughGrass ? grass : 0);
                int shotCut = (d <= ShootThrough || (seenJustNow && d <= ShootThroughSeen) ? foliage : 0) | (d <= ShootThroughGrass ? grass : 0);
                if (sightCut == 0 && shotCut == 0)
                {
                    continue;
                }

                for (int k = first; k < c; k += 3)
                {
                    // Line of sight, vision, shot - in that order (VisionRaycastJob.CreateCommands)
                    Cut(commands, k, sightCut);
                    Cut(commands, k + 1, sightCut);
                    Cut(commands, k + 2, shotCut);
                }

                Opened++;
            }
        }

        private static void Cut(NativeArray<RaycastCommand> commands, int index, int layers)
        {
            if (layers == 0)
            {
                return;
            }

            RaycastCommand cmd = commands[index];
            QueryParameters q = cmd.queryParameters;
            if ((q.layerMask & layers) == 0)
            {
                return;
            }

            q.layerMask &= ~layers;
            commands[index] = new RaycastCommand(cmd.from, cmd.direction, q, cmd.distance);
        }
    }
}
