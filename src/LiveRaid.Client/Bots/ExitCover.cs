using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using QuestingBots.Helpers;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// "Under cover" (F12 -> 8 "Slip out through an exit someone opened"): as online, when somebody opens an exit that is open only for a while (the hermetic door
    /// after its lever, D-2 after the power, a train, any exit that was shut at the start of the raid), many players on the map seize the moment and leave
    /// through it while it is open. Here: each PMC and player scav (squad leaders and loners - the squad follows) that is free (no fight, no treatment, not leaving
    /// already, in the raid for a while) rolls once for each such opening; the chance is a little above its usual (12 % by default), falls with the way to the exit
    /// (none beyond 700 m), is higher for rats, loot-minded and quiet characters, lower for rushers and hunters. A winner is given the exit as its plan
    /// (SwitchExtract), the squad with it.
    /// </summary>
    public static class ExitCover
    {
        private const float MaxWay = 700f;
        private const float MinAliveSeconds = 150f;

        private class Seen
        {
            public bool Open;
            public float OpenedAt;
            public readonly HashSet<string> Rolled = new HashSet<string>();
        }

        private static readonly Dictionary<ExfiltrationPoint, Seen> _seen = new Dictionary<ExfiltrationPoint, Seen>();
        private static readonly System.Random _rng = new System.Random();
        private static readonly NavMeshPath _path = new NavMeshPath();
        private static float _next;
        private static bool _baseline;

        public static int Openings { get; private set; }
        public static int Rolls { get; private set; }
        public static int Slipped { get; private set; }

        public static string Summary => $"exits opened for a while {Openings}, bots that rolled for slipping out {Rolls}, slipped out {Slipped}";

        public static void Clear()
        {
            _seen.Clear();
            _baseline = false;
            _next = 0f;
            Openings = Rolls = Slipped = 0;
        }

        private static bool IsOpen(ExfiltrationPoint e) => e.Status == EExfiltrationStatus.RegularMode || e.Status == EExfiltrationStatus.Countdown
                                                              || (e.Status == EExfiltrationStatus.AwaitsManualActivation && SwitchExtract.IsTrainExit(e));

        public static void Tick()
        {
            if (!Settings.Enabled.Value || !Settings.ExitCover.Value || !Settings.SwitchExits.Value || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            ExfiltrationPoint[] pmc = ExfiltrationController.Instance?.ExfiltrationPoints;
            ScavExfiltrationPoint[] scav = ExfiltrationController.Instance?.ScavExfiltrationPoints;
            if (pmc == null)
            {
                return;
            }

            List<ExfiltrationPoint> all = pmc.Concat(scav ?? new ScavExfiltrationPoint[0]).Where(e => e != null && e.Status != EExfiltrationStatus.NotPresent).Distinct().ToList();
            List<ExfiltrationPoint> justOpened = new List<ExfiltrationPoint>();
            foreach (ExfiltrationPoint e in all)
            {
                bool open = IsOpen(e);
                if (!_seen.TryGetValue(e, out Seen s))
                {
                    _seen[e] = s = new Seen { Open = open, OpenedAt = open ? -1f : 0f };
                    continue;
                }

                if (open && !s.Open)
                {
                    s.OpenedAt = Time.time;
                    s.Rolled.Clear();
                    Openings++;
                    justOpened.Add(e);
                }

                s.Open = open;
            }

            // The first look is the start of the raid: exits already open then are the usual ones, not a moment to seize
            if (!_baseline)
            {
                _baseline = true;
                return;
            }

            foreach (ExfiltrationPoint e in justOpened)
            {
                if (SwitchExtract.IsTrainExit(e))
                {
                    BoxCollider zone = e.GetComponent<BoxCollider>();
                    if (zone != null)
                    {
                        Raid.TrainArea.Remember(zone.bounds);
                    }

                    SwitchExtract.SwitchExit info = SwitchExtract.Exits().FirstOrDefault(x => x.Exit == e);
                    Log.Info($"LR Train: the armoured train has arrived - its zone at ({e.transform.position.x:0}, {e.transform.position.y:0}, {e.transform.position.z:0}), " +
                             $"box {(zone != null ? $"{zone.bounds.size.x:0.#} x {zone.bounds.size.y:0.#} x {zone.bounds.size.z:0.#}, centre ({zone.bounds.center.x:0}, {zone.bounds.center.y:0}, {zone.bounds.center.z:0})" : "none")}, " +
                             $"stand ({(info?.Stand.x ?? 0f):0}, {(info?.Stand.y ?? 0f):0}, {(info?.Stand.z ?? 0f):0}), boarding time {e.Requirements?.FirstOrDefault(r => r != null && r.Requirement == ERequirementState.Train)?.Count:0} s");
                }
                else
                {
                    Log.Info($"LR Extract: exit {e.Settings.Name.Localized()} has just opened for a while - the bots that are free may slip out through it");
                }
            }

            foreach (KeyValuePair<ExfiltrationPoint, Seen> kv in _seen)
            {
                // Within four minutes of the opening, and while it is open
                if (!kv.Value.Open || kv.Value.OpenedAt < 0f || Time.time - kv.Value.OpenedAt > (SwitchExtract.IsTrainExit(kv.Key) ? 420f : 240f))
                {
                    continue;
                }

                Offer(world, kv.Key, kv.Value);
            }
        }

        private static void Offer(GameWorld world, ExfiltrationPoint exit, Seen seen)
        {
            SwitchExtract.SwitchExit sw = SwitchExtract.Exits().FirstOrDefault(x => x.Exit == exit);
            if (sw == null)
            {
                return;
            }

            float remaining = RaidHelpers.GetRaidTimeRemainingFraction();
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || seen.Rolled.Contains(bot.ProfileId) || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav) || !(p.Kind == BotKind.PlayerScav ? sw.ForScav : sw.ForPmc)
                    || p.ActivatedAt < 0 || Time.time - p.ActivatedAt < MinAliveSeconds || remaining < 0.1f
                    || Tactics.TacticalTasks.SquadLeaderOf(bot) != null
                    || SwitchExtract.HasPlan(bot) || ExtractPlanner.IsLeaving(bot)
                    || Tactics.HitReaction.InRealFight(bot, 20f) || bot.Medecine?.Using == true)
                {
                    continue;
                }

                // Busy right now (a fight, a treatment) is not a no: it is looked at again on the next pass, while the exit is open
                seen.Rolled.Add(bot.ProfileId);
                if (sw.Direct && !ExitRules.Allows(bot, sw.Exit, out _))
                {
                    continue;
                }

                bool train = sw.IsTrain;
                float maxWay = train ? 1500f : MaxWay;
                if (!SwitchExtract.TryReach(bot.Position, sw.Stand, _path, out _, out _, out float way) || way > maxWay)
                {
                    continue;
                }

                Rolls++;
                // The train is an event of the raid, not a chance to slip out: most free squads within reach go (F12 70 %), a little less far off and for fighters
                float chance = train
                    ? Settings.TrainChance.Value / 100f * Mathf.Clamp(1.2f - way / maxWay, 0.35f, 1f) * Mathf.Clamp(StyleFactor(p), 0.8f, 1.1f)
                    : Settings.ExitCoverChance.Value / 100f * Mathf.Clamp(1.1f - way / MaxWay, 0.25f, 1f) * StyleFactor(p);
                if (_rng.NextDouble() >= chance)
                {
                    continue;
                }

                Slipped++;
                string why = train ? $"runs for the armoured train ({way:0} m, chance {chance:P0})" : $"slips out while {sw.Name.Localized()} is open ({way:0} m, chance {chance:P0})";
                SwitchExtract.Assign(bot, sw, why);
                int squad = 0;
                List<BotOwner> followers = bot.Boss?.Followers;
                if (followers != null)
                {
                    foreach (BotOwner f in followers)
                    {
                        if (f != null && !f.IsDead && !SwitchExtract.HasPlan(f) && (!sw.Direct || ExitRules.Allows(f, sw.Exit, out _)))
                        {
                            SwitchExtract.Assign(f, sw, why);
                            squad++;
                        }
                    }
                }

                Log.Info($"LR Extract: {p.Name} ({p.Kind} {p.Style}) {(squad > 0 ? $"and its squad of {squad + 1} " : "")}{why}");
            }
        }

        /// <summary>Rats, looters and quiet characters like to leave "under the noise"; rushers and hunters stay for the fight.</summary>
        private static float StyleFactor(BotProfile p)
        {
            switch (p.Style)
            {
                case PlayStyle.Rat:
                case PlayStyle.Looter:
                case PlayStyle.Camper: return 1.3f;
                case PlayStyle.Quester:
                case PlayStyle.Explorer: return 1.0f;
                case PlayStyle.Sniper: return 0.8f;
                default: return 0.6f;
            }
        }
    }
}
