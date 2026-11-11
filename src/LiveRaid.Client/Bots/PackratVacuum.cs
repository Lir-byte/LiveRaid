using System.Collections.Generic;
using System.Linq;
using EFT;
using LiveRaid.Client.Raid;
using LiveRaid.Client.Tactics;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The packrat as a vacuum (F12 -> 6 "Packrat is a vacuum", 1.3.0). Raids 2026-11-09 (6 packrats at a 100 % chance): it looted at 0.75-0.93 jobs a minute against 0.6-0.7 of
    /// an ordinary bot (not a vacuum), spent its first minutes walking to exploration steps 435-480 m away "looting only on the way", and three of the four that lived 4+ minutes
    /// were killed in 4-10 minutes - one "lost its nerve and left the fight" ten times and went on meeting the same enemy. Now:
    ///  - the packrat's quests are weighed by how NEAR and how RICH they are (<see cref="Multiplier"/>): the next building, not the far end of the map; the region is cleaned out
    ///    before the next (RoomSweep already makes the packrat go through every room and floor at 97 %);
    ///  - it has no "loot only on the way" drive to a quest step (QuestDrive), so nothing it passes is skipped;
    ///  - a flight from a fight is a real one (<see cref="OnFlight"/>): the second flight within 3 minutes, or a flight at under 60 % health, sends it to an exit
    ///    (ExtractPlanner.LeaveSoon) - a packrat that stays alive is worth more than one that dies in the same street.
    /// 1.4.2, raids 2026-11-10 (Reserve, 16:35 and 17:22): the flight did not work. The "slip away" was a tactical task, and those end the moment there is a fight
    /// ("ends Relocate after 0 s: fight" every single time); the run itself was the common 45-90 m break of 25 s, after which the packrat was "calm" and went back to
    /// the loot it had been at - where the enemy still was (SchmidttyB met PreyToLive four times in one yard). It was given fight roles (Assault, Flank), and
    /// QuestingBots' "investigate the noise" walked Chad_Thundercock to the shots that killed him. Now:
    ///  - the run is the break itself, and for the packrat it is 90-170 m and up to 55 s (<see cref="BreakDistances"/>, CombatSense);
    ///  - the place it ran from is shunned for 4 minutes (<see cref="Shuns"/>): quests there are all but unwanted, loot there is left alone - it goes round;
    ///  - no fight role (FightRoles), no walking to noises (QbNoInvestigatePatch).
    /// Its weapon stays as poor as the server gave it: the player's rule.
    /// </summary>
    public static class PackratVacuum
    {
        private static readonly Dictionary<string, List<float>> _flights = new Dictionary<string, List<float>>();
        private static readonly System.Random _rng = new System.Random();

        public static int Weighed { get; private set; }
        public static int Slips { get; private set; }
        public static int Leaves { get; private set; }
        public static int ShunnedLoot { get; private set; }
        public static int ShunnedQuests { get; private set; }
        public static int NoiseWalksRefused { get; internal set; }

        private const float ShunRadius = 50f;
        private const float ShunSeconds = 150f;
        private static readonly Dictionary<string, List<(Vector3 at, float when)>> _scares = new Dictionary<string, List<(Vector3, float)>>();

        /// <summary>How far the packrat runs when it breaks off (the common break is 45-90 m).</summary>
        public static readonly float[] BreakDistances = { 70f, 100f, 130f };
        public const float BreakSeconds = 40f;

        private static readonly Dictionary<string, (Vector3 dir, float at)> _lastFlight = new Dictionary<string, (Vector3, float)>();

        /// <summary>
        /// Where the people are, as far as the packrat can know (1.4.5): the other PMCs, player scavs and humans within 300 m, and the kills of the last two minutes
        /// within 300 m (a fight is a crowd). Its flights run away from them; before, the flight ran only away from the enemy it met, and the next enemy stood on
        /// the other side - it ran back and forth between the lobby's fights (Strix, 28 of 55 samples in the break layer, three flights in four minutes).
        /// </summary>
        public static List<Vector3> Crowd(BotOwner bot)
        {
            List<Vector3> crowd = new List<Vector3>();
            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return crowd;
            }

            Vector3 here = bot.Position;
            foreach (Player pl in world.AllAlivePlayersList)
            {
                if (pl == null || pl.ProfileId == bot.ProfileId || pl.HealthController?.IsAlive != true || SquadTies.AreMates(bot.ProfileId, pl.ProfileId))
                {
                    continue;
                }

                BotOwner other = pl.AIData?.BotOwner;
                bool people = other == null ? !pl.IsAI : BotProfiles.KindOf(other) == BotKind.Pmc || BotProfiles.KindOf(other) == BotKind.PlayerScav;
                if (people && (pl.Position - here).sqrMagnitude < 300f * 300f)
                {
                    crowd.Add(pl.Position);
                }
            }

            float now = Time.time;
            foreach (KillEvent k in WorldEvents.Kills)
            {
                if (now - k.Time < 120f && (k.Position - here).sqrMagnitude < 300f * 300f)
                {
                    crowd.Add(k.Position);
                }
            }

            return crowd;
        }

        /// <summary>
        /// The worth of a place to run to for the packrat, added to CombatSense's own score: the farther it is from the nearest of the crowd (and farther than where the
        /// packrat stands now), the better; and the same way as its last flight (within 4 minutes) is better than the opposite one.
        /// </summary>
        public static float FlightScore(BotOwner bot, Vector3 to, List<Vector3> crowd)
        {
            float score = 0f;
            Vector3 from = bot.Position;
            if (crowd != null && crowd.Count > 0)
            {
                float nowNear = crowd.Min(p => Vector3.Distance(p, from));
                float toNear = crowd.Min(p => Vector3.Distance(p, to));
                score += (Mathf.Min(toNear, 250f) - Mathf.Min(nowNear, 250f)) * 0.03f + Mathf.Min(toNear, 200f) * 0.01f;
            }

            if (_lastFlight.TryGetValue(bot.ProfileId, out var last) && Time.time - last.at < 240f)
            {
                Vector3 dir = to - from;
                dir.y = 0f;
                if (dir.sqrMagnitude > 1f)
                {
                    score += 1.2f * Vector3.Dot(dir.normalized, last.dir);
                }
            }

            return score;
        }

        /// <summary>The packrat has chosen where to run (CombatSense): its way is remembered for the next flight.</summary>
        public static void NoteFlight(BotOwner bot, Vector3 to, int crowdCount, float nearBefore, float nearAfter)
        {
            Vector3 dir = to - bot.Position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1f)
            {
                return;
            }

            bool again = _lastFlight.TryGetValue(bot.ProfileId, out var last) && Time.time - last.at < 240f && Vector3.Dot(dir.normalized, last.dir) > 0.3f;
            _lastFlight[bot.ProfileId] = (dir.normalized, Time.time);
            Log.Detail("info", () => $"LR Packrat: {bot.Profile?.Nickname} runs for ({to.x:0}, {to.z:0}), {Vector3.Distance(bot.Position, to):0} m, away from the crowd " +
                                     $"({crowdCount} known within 300 m, the nearest {nearBefore:0} -> {nearAfter:0} m){(again ? ", the way of its last flight" : "")}");
        }

        public static string Summary => $"packrat vacuum: quests weighed by nearness and richness {Weighed}, flights {Slips}, sent to an exit after a flight {Leaves}, " +
                                        $"loot left alone where it had run from {ShunnedLoot}, quests there held back {ShunnedQuests} times, walks to a noise refused {NoiseWalksRefused} ticks";

        public static void Clear()
        {
            _flights.Clear();
            _scares.Clear();
            _lastFlight.Clear();
            Weighed = Slips = Leaves = ShunnedLoot = ShunnedQuests = NoiseWalksRefused = 0;
        }

        /// <summary>The packrat ran from an enemy near this place within the last 4 minutes: it keeps away from it.</summary>
        public static bool Shuns(BotOwner bot, Vector3 pos, float radius = ShunRadius)
        {
            if (bot == null || !_scares.TryGetValue(bot.ProfileId, out var list) || list.Count == 0)
            {
                return false;
            }

            float now = Time.time;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (now - list[i].when > ShunSeconds)
                {
                    list.RemoveAt(i);
                    continue;
                }

                float dx = list[i].at.x - pos.x, dz = list[i].at.z - pos.z;
                if (dx * dx + dz * dz < radius * radius && Mathf.Abs(list[i].at.y - pos.y) < 12f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>LootingBots is about to start a loot job: not one in the place the packrat has just run from (the thing is left out for it).</summary>
        public static bool LeavesLootAlone(BotOwner bot, LootingBots.Components.LootingBrain brain)
        {
            if (bot == null || brain == null || brain.ActiveLoot == null || !On(BotProfiles.Get(bot)) || !Shuns(bot, brain.ActiveLoot.transform.position))
            {
                return false;
            }

            ShunnedLoot++;
            string id = brain.ActiveLootId;
            Log.Detail("loot", () => $"{bot.Profile?.Nickname} (the packrat) leaves {brain.ActiveLoot.name} alone - it ran from an enemy there a moment ago");
            if (!string.IsNullOrEmpty(id))
            {
                brain.IgnoreLoot(id);
            }

            return true;
        }

        public static bool On(BotProfile p) => p != null && p.Packrat && Settings.Enabled.Value && Settings.PackratVacuum.Value;

        /// <summary>QuestingBots' desirability of a quest for the packrat: near and rich ones first.</summary>
        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On(p) || bot == null || quest == null)
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (!name.StartsWith(QuestFactory.ExplorePrefix) || !QuestFactory.QuestPois.TryGetValue(name, out List<Poi> pois) || pois.Count == 0)
            {
                return 1f;
            }

            Vector3 centre = Vector3.zero;
            float loot = 0f;
            foreach (Poi poi in pois)
            {
                centre += poi.Pos;
                if (poi.Kind != PoiKind.Open)
                {
                    loot += poi.W;
                }
            }

            centre /= pois.Count;
            if (Shuns(bot, centre, ShunRadius + 20f))
            {
                ShunnedQuests++;
                return 0.08f;
            }

            float dx = centre.x - bot.Position.x, dz = centre.z - bot.Position.z;
            float away = Mathf.Sqrt(dx * dx + dz * dz);
            float near = Mathf.Exp(-away / 130f);
            float richness = Mathf.Clamp(0.6f + 0.35f * loot / pois.Count, 0.6f, 2f);
            Weighed++;
            return Mathf.Clamp(Mathf.Lerp(0.15f, 2.4f, near) * richness, 0.1f, 3.5f);
        }

        /// <summary>The packrat has just left a fight (CombatSense): slip away from the enemy, or - second time in 3 minutes, or wounded - head for an exit.</summary>
        public static void OnFlight(BotOwner bot, BotProfile p, float health, Vector3 enemyAt)
        {
            if (!On(p) || bot == null)
            {
                return;
            }

            float now = Time.time;
            if (!_flights.TryGetValue(bot.ProfileId, out List<float> list))
            {
                _flights[bot.ProfileId] = list = new List<float>();
            }

            list.RemoveAll(t => now - t > 180f);
            list.Add(now);
            if (!_scares.TryGetValue(bot.ProfileId, out var scares))
            {
                _scares[bot.ProfileId] = scares = new List<(Vector3, float)>();
            }

            scares.Add((enemyAt, now));
            // Where it stood too, when the enemy was far: the loot it was at is in his sight
            if ((bot.Position - enemyAt).sqrMagnitude > 40f * 40f)
            {
                scares.Add((bot.Position, now));
            }

            // 1.3.4: the second flight sends it home only when there is something to carry home or it has been in long enough. Raid 2026-11-10 11:43 (Customs):
            // Jiggle_Peek met an enemy twice by 3:25, was sent out at 5:10 with 14 things looted, walked 4 minutes to the paid car at the dorms and was shot there
            // at 11:51 - a packrat that had hardly begun. With an all but empty bag early in the raid it slips farther away instead and goes on looting there
            float carried = 0f;
            bool full = false;
            try
            {
                carried = Mathf.Max(0f, LootingBots.External.GetNetLootValue(bot));
                full = LootingBots.External.CheckIfInventoryFull(bot);
            }
            catch
            {
                // LootingBots has nothing on this bot yet
            }

            float alive = p.ActivatedAt >= 0f ? now - p.ActivatedAt : 0f;
            bool second = list.Count >= 2;
            bool worthLeaving = carried >= 150000f || full || alive >= 600f || list.Count >= 4;
            if (health < 0.6f || (second && worthLeaving))
            {
                Leaves++;
                ExtractPlanner.LeaveSoon(bot, health < 0.6f ? "the packrat is hurt"
                    : $"the packrat met an enemy a second time within 3 minutes (carries ~{carried / 1000f:0}k{(full ? ", the bag is full" : "")}, {alive / 60f:0} min in)");
                return;
            }

            // The run itself is the break (CombatSense: 90-170 m for the packrat) - a tactical task here was ended by the fight in the same second
            Slips++;
            Log.Info($"LR Packrat: {p.Name} runs from the fight and keeps away from that place for {ShunSeconds / 60f:0} min" +
                     (second ? $" - the second time in 3 minutes, but with ~{carried / 1000f:0}k after {alive / 60f:0.0} min it is too early to go home" : ""));
        }
    }
}
