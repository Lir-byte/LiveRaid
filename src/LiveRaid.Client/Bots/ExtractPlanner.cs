using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using QuestingBots.BotLogic.BotMonitor.Monitors;
using QuestingBots.Components;
using QuestingBots.Controllers;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Sends squads home at their own personal moment. Followers go with their leader, so only leaders and solos are checked.
    /// QuestingBots still extracts bots near the end of the raid.
    /// </summary>
    public static class ExtractPlanner
    {
        private const float MinAliveSeconds = 300f;
        private static readonly HashSet<string> _sent = new HashSet<string>();
        private static float _nextCheck;
        private static bool _listed;

        public static void Clear()
        {
            _sent.Clear();
            _leaveSoon.Clear();
            _initialSize.Clear();
            _nextWeigh.Clear();
            EarlyLeavers = 0;
            ExtractClock.Clear();
            _nextCheck = 0;
            _listed = false;
        }

        public static void Tick()
        {
            if (!Settings.PersonalExtract.Value || Time.time < _nextCheck)
            {
                return;
            }

            _nextCheck = Time.time + 5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            if (!_listed && Settings.SwitchExits.Value)
            {
                // Once per raid, early: which switch exits this map has (logged), so the choice later is quick
                _listed = true;
                SwitchExtract.Exits();
            }

            float remaining = RaidHelpers.GetRaidTimeRemainingFraction();
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || _sent.Contains(bot.ProfileId) || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                if (Tactics.TacticalTasks.SquadLeaderOf(bot) != null)
                {
                    continue;
                }

                BotProfile profile = BotProfiles.Get(bot);
                if (profile == null || profile.Kind == BotKind.Other || profile.Kind == BotKind.Scav)
                {
                    continue;
                }

                string early = null;
                bool urgent = false;
                if (_leaveSoon.TryGetValue(bot.ProfileId, out (float at, string why) soon) && Time.time >= soon.at)
                {
                    // Asked to go now (MateKit: carries out a fallen squadmate's kit) - once the fight, if any, is over
                    if (Tactics.HitReaction.InRealFight(bot, 30f))
                    {
                        continue;
                    }

                    early = soon.why;
                    urgent = true;
                }
                else if (remaining > profile.ExtractAtRemaining)
                {
                    // Not its hour yet - unless the raid has gone so that it leaves anyway
                    if (!Settings.EarlyExit.Value || profile.ActivatedAt < 0 || Time.time - profile.ActivatedAt < MinAliveSeconds || remaining < 0.08f
                        || !EarlyLeave(bot, profile, remaining, out early, out urgent))
                    {
                        continue;
                    }
                }

                if (profile.ActivatedAt < 0 || Time.time - profile.ActivatedAt < MinAliveSeconds)
                {
                    continue;
                }

                // A fallen squadmate or its own kill still lies unsearched nearby: those first (raid 2026-10-26 18:34: Waffle's hour came two seconds
                // before he went to Spock's body, and the game's exfil layer walked him away from it)
                if (!urgent && PutsOff(bot, profile))
                {
                    continue;
                }

                // Hardly anyone leaves in the first 20 minutes (ExtractClock): a refused personal hour moves later
                if (!ExtractClock.MayLeave(profile, urgent, early == null, out string clock))
                {
                    continue;
                }

                if (clock != null && early != null)
                {
                    early += "; " + clock;
                }
                else if (clock != null)
                {
                    Log.Info($"LR Extract: {profile.Name} ({profile.Style}) - its hour has come: {clock}");
                }

                BotObjectiveManager objectives = bot.GetObjectiveManager();
                BotExtractMonitor monitor = objectives?.BotMonitor?.GetMonitor<BotExtractMonitor>();
                if (monitor == null || monitor.IsTryingToExtract)
                {
                    _sent.Add(bot.ProfileId);
                    continue;
                }

                _sent.Add(bot.ProfileId);
                if (TrySwitchExit(bot, profile, remaining))
                {
                    if (early != null)
                    {
                        EarlyLeavers++;
                        Log.Info($"LR Extract: {profile.Name} leaves early: {early}");
                    }

                    continue;
                }

                if (monitor.TryInstructBotToExtract())
                {
                    Log.Info($"LR Extract: {profile.Name} ({profile.Style}) heads for an exfil with {remaining:P0} of the raid left{(early != null ? " - EARLY: " + early : "")}");
                    if (early != null)
                    {
                        EarlyLeavers++;
                    }
                }
            }
        }

        private const float MaxDeferral = 240f;
        private static readonly Dictionary<string, float> _deferFrom = new Dictionary<string, float>();
        private static int _deferred, _deferredDone;

        public static void ClearDeferrals()
        {
            _deferFrom.Clear();
            _deferred = _deferredDone = 0;
        }

        public static string DeferSummary => $"leaving put off for bodies {_deferred} times ({_deferredDone} gave up waiting)";

        /// <summary>Its time to leave has come, but a body that matters (a squadmate's, its own kill) lies unsearched within reach: not yet - for up to 4 minutes.</summary>
        private static bool PutsOff(BotOwner bot, BotProfile p)
        {
            if (!Settings.BodiesBeforeLeaving.Value)
            {
                return false;
            }

            string body = Tactics.LootJobs.Unfinished(bot);
            if (body == null)
            {
                return false;
            }

            float now = Time.time;
            if (!_deferFrom.TryGetValue(bot.ProfileId, out float from))
            {
                _deferFrom[bot.ProfileId] = from = now;
                _deferred++;
                Log.Info($"LR Extract: {p.Name} ({p.Style}) - its time to leave, but {body} first");
            }

            if (now - from < MaxDeferral)
            {
                return true;
            }

            if (now - from < MaxDeferral + 6f)
            {
                _deferredDone++;
                Log.Info($"LR Extract: {p.Name} has waited {MaxDeferral:0} s to go through {body} - leaves now");
            }

            return false;
        }

        // Squad size when first seen (to tell how many have fallen), and when each leader was last weighed
        private static readonly Dictionary<string, int> _initialSize = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> _nextWeigh = new Dictionary<string, float>();
        private static readonly System.Random _rng = new System.Random();

        public static int EarlyLeavers { get; private set; }

        /// <summary>
        /// A player does not wait for his appointed hour when the raid has gone badly or well enough: hurt with no meds left, a bag full of valuables, half the squad
        /// dead, a long raid with something to show for it. Every ~20 s each squad leader and solo weighs it up (health of the squad, meds, what is carried, a full bag,
        /// losses, how far into the raid) and with the weight above a threshold a chance to head for an exit now. Never in or right after a fight.
        /// </summary>
        private static bool EarlyLeave(BotOwner bot, BotProfile p, float remaining, out string why, out bool urgent)
        {
            why = null;
            urgent = false;
            float now = Time.time;
            if (_nextWeigh.TryGetValue(bot.ProfileId, out float next) && now < next)
            {
                return false;
            }

            _nextWeigh[bot.ProfileId] = now + 18f + (float)_rng.NextDouble() * 6f;
            if (Tactics.HitReaction.InRealFight(bot, 45f) || bot.Medecine?.Using == true || !Tactics.TacticalTasks.NoThreat(bot))
            {
                return false;
            }

            List<BotOwner> squad = new List<BotOwner> { bot };
            List<BotOwner> followers = bot.Boss?.Followers;
            if (followers != null)
            {
                foreach (BotOwner f in followers)
                {
                    if (f != null && !f.IsDead)
                    {
                        squad.Add(f);
                    }
                }
            }

            // 0.9.9: squadmates QuestingBots moved into a group of their own are the squad all the same (raid 2026-10-31 00:34: CharlieMike_EFT
            // left early "with the squad down to 1 of 2" while Crimson was alive, only split off by QuestingBots), and a spawn squad that has lost
            // men is smaller than it began - also for the one who was a follower when it was weighed first
            int spawnSquad = 0;
            GameWorld world = Singleton<GameWorld>.Instance;
            foreach (string id in SquadTies.MatesOf(bot.ProfileId))
            {
                spawnSquad++;
                BotOwner m = id == bot.ProfileId ? null : world?.AllAlivePlayersList.FirstOrDefault(pl => pl.ProfileId == id)?.AIData?.BotOwner;
                if (m != null && !m.IsDead && !squad.Contains(m))
                {
                    squad.Add(m);
                }
            }

            if (!_initialSize.TryGetValue(bot.ProfileId, out int initial) || squad.Count > initial || spawnSquad > initial)
            {
                _initialSize[bot.ProfileId] = initial = Mathf.Max(squad.Count, spawnSquad);
            }

            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            float hpSum = 0f;
            float carried = 0f;
            int bagsFull = 0;
            foreach (BotOwner m in squad)
            {
                hpSum += m.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
                carried += Mathf.Max(0f, LootingBots.External.GetNetLootValue(m));
                if (LootingBots.External.CheckIfInventoryFull(m))
                {
                    bagsFull++;
                }
            }

            float hpAvg = hpSum / squad.Count;
            BotFirstAid aid = bot.Medecine?.FirstAid;
            bool hurtNoMeds = aid != null && aid.Damaged && !aid.HaveSmth2Use;
            float progress = 1f - remaining;
            float urge = 0f;
            var reasons = new List<string>();
            if (hurtNoMeds && hp < 0.35f)
            {
                urgent = true;
                urge += 0.9f;
                reasons.Add($"badly hurt ({hp:P0}) with nothing to treat it");
            }
            else if (hurtNoMeds && hp < 0.55f)
            {
                urge += 0.5f;
                reasons.Add($"hurt ({hp:P0}) with no meds");
            }
            else if (hpAvg < 0.4f)
            {
                urge += 0.45f;
                reasons.Add($"the squad is in a bad way ({hpAvg:P0})");
            }

            // A leg it can only walk on (broken or blacked out, no splint, no painkiller): the rest of the raid at a limp
            if (Settings.TreatLegs.Value && LegCare.Limps(bot.GetPlayer) && !LegCare.Plan(bot, out _, out _))
            {
                urge += 0.3f;
                reasons.Add("limps on a broken leg with nothing for it");
            }

            if (Tactics.SafeReload.OutOfRounds(bot))
            {
                urgent = true;
                urge += 0.9f;
                reasons.Add("no rounds left for any of its guns");
            }

            if (carried > 20000f)
            {
                float v = Mathf.Log10(1f + carried / 80000f) * 0.35f * Mathf.Clamp(p.LootGreed, 0.5f, 1.3f);
                urge += v;
                if (carried > 250000f)
                {
                    reasons.Add($"carries ~{carried / 1000f:0}k");
                }
            }

            // Overloaded: a player who can hardly run any more heads out (Load)
            float kg = Settings.WeightMatters.Value ? squad.Max(Load.Kg) : 0f;
            if (kg >= Load.Overloaded)
            {
                urge += 0.3f;
                reasons.Add($"overloaded ({kg:0} kg)");
            }
            else if (kg >= Load.Heavy)
            {
                urge += 0.15f;
            }

            if (bagsFull * 2 >= squad.Count)
            {
                urge += 0.35f;
                reasons.Add("the bags are full");
            }

            if (initial >= 2 && squad.Count * 2 <= initial)
            {
                urgent = true;
                urge += 0.4f;
                reasons.Add($"the squad is down to {squad.Count} of {initial}");
            }
            else if (initial == 1 && p.Kind == BotKind.Pmc && carried > 120000f && progress > 0.45f)
            {
                urge += 0.15f;
            }

            // Beaten off: it fell back or broke off a fight in the last 5 minutes and is hurt, or it is alone now after losing squadmates - a player
            // patches up and heads for the exit (raid 2026-10-31 00:34: Crimson broke off a fight with a squad of three, lost his leader, hp 30 %,
            // and roamed on for ten minutes until his hour came)
            if (Tactics.CombatSense.LostFightRecently(bot, 300f, out _) && (hp < 0.6f || squad.Count < initial))
            {
                urge += hp < 0.4f ? 0.6f : 0.45f;
                reasons.Add($"has just lost a fight (hp {hp:P0}{(squad.Count < initial ? ", " + (squad.Count == 1 ? "alone now" : $"{squad.Count} of {initial} left") : "")})");
            }

            urge += 0.3f * progress;

            // The raid's moods: shaken by a close call, a hoarder with a rich bag (RaidMood)
            urge += Tactics.RaidMood.ExtractUrge(bot, reasons);

            // A cautious player leaves earlier, a greedy or reckless one stays for more
            urge += p.Style == PlayStyle.Rat ? 0.12f : p.Style == PlayStyle.Looter ? 0f : p.Aggression > 0.7f ? -0.1f : 0f;

            float chance = Mathf.Clamp01((urge - 0.8f) / 0.7f) * 0.45f;
            if (chance <= 0.01f || _rng.NextDouble() > chance)
            {
                return false;
            }

            why = string.Join(", ", reasons) + $" (urge {urge:0.00}, chance {chance:P0} per check, {progress:P0} of the raid gone)";
            return true;
        }

        private static readonly Dictionary<string, (float at, string why)> _leaveSoon = new Dictionary<string, (float, string)>();

        /// <summary>The bot (its squad leader, when it follows one) heads for an exit some seconds from now, whatever its hour.</summary>
        public static void LeaveSoon(BotOwner bot, string why)
        {
            if (bot == null || _sent.Contains(bot.ProfileId))
            {
                return;
            }

            float at = Time.time + 10f + (float)_rng.NextDouble() * 15f;
            BotOwner leader = Tactics.TacticalTasks.SquadLeaderOf(bot);
            foreach (BotOwner b in new[] { bot, leader })
            {
                if (b != null && !_leaveSoon.ContainsKey(b.ProfileId))
                {
                    _leaveSoon[b.ProfileId] = (at, why);
                }
            }

            Log.Info($"LR Extract: {bot.Profile.Nickname} {(leader != null ? "and its squad head" : "heads")} for an exit in a moment - {why}");
        }

        /// <summary>The bot is on its way out of the raid (sent by LiveRaid, or QuestingBots' extract is under way).</summary>
        public static bool IsLeaving(BotOwner bot) =>
            bot != null && (_sent.Contains(bot.ProfileId) || bot.GetObjectiveManager()?.BotMonitor?.GetMonitor<BotExtractMonitor>()?.IsTryingToExtract == true);

        /// <summary>The ordinary way out: QuestingBots hands the squad to the game's exfil (nearest open exit without requirements).</summary>
        public static void ExtractTheUsualWay(BotOwner bot)
        {
            BotExtractMonitor monitor = bot.GetObjectiveManager()?.BotMonitor?.GetMonitor<BotExtractMonitor>();
            if (monitor != null && !monitor.IsTryingToExtract)
            {
                monitor.TryInstructBotToExtract();
            }
        }

        /// <summary>
        /// Some squads leave through an exit a switch opens (players like those: often closer, quieter). The more of a detour it is
        /// compared with the nearest ordinary exit, the less likely; explorers and questers like it more, rats and campers less.
        /// </summary>
        private static bool TrySwitchExit(BotOwner bot, BotProfile p, float remaining)
        {
            if (!Settings.SwitchExits.Value || p.Kind != BotKind.Pmc)
            {
                return false;
            }

            SwitchExtract.SwitchExit exit = SwitchExtract.Choose(bot, out float way);
            if (exit == null)
            {
                return false;
            }

            float usual = NearestOrdinaryExit(bot.Position);
            float chance;
            switch (p.Style)
            {
                case PlayStyle.Explorer:
                case PlayStyle.Quester:
                case PlayStyle.Hunter: chance = 0.55f; break;
                case PlayStyle.Rusher: chance = 0.45f; break;
                case PlayStyle.Looter: chance = 0.4f; break;
                default: chance = 0.3f; break;
            }

            if (exit.Open)
            {
                chance += 0.25f;
            }

            float detour = usual < float.MaxValue ? way / Mathf.Max(30f, usual) : 1f;
            chance *= detour <= 1.2f ? 1.6f : detour <= 2f ? 1f : detour <= 3f ? 0.5f : 0.15f;
            if (UnityEngine.Random.value > chance)
            {
                return false;
            }

            string why = $"{way:0} m way vs {(usual < float.MaxValue ? usual.ToString("0") : "-")} m to the nearest ordinary exit";
            SwitchExtract.Assign(bot, exit, why);
            int squad = 0;
            List<BotOwner> followers = bot.Boss?.Followers;
            if (followers != null)
            {
                foreach (BotOwner f in followers)
                {
                    if (f != null && !f.IsDead)
                    {
                        SwitchExtract.Assign(f, exit, why);
                        _sent.Add(f.ProfileId);
                        squad++;
                    }
                }
            }

            Log.Info($"LR Extract: {p.Name} ({p.Style}){(squad > 0 ? $" and {squad} squadmates" : "")} leave through switch exit {exit.Name.Localized()} " +
                     $"with {remaining:P0} of the raid left ({(exit.Open ? "already open" : exit.Chain.Count + " switch(es) to pull")}; {why})");
            return true;
        }

        private static float NearestOrdinaryExit(Vector3 from)
        {
            float best = float.MaxValue;
            ExfiltrationPoint[] exits = ExfiltrationController.Instance?.ExfiltrationPoints;
            if (exits == null)
            {
                return best;
            }

            foreach (ExfiltrationPoint e in exits)
            {
                if (e == null || e.Status == EExfiltrationStatus.NotPresent || e.Requirements.Any(r => r.Requirement != ERequirementState.None))
                {
                    continue;
                }

                best = Mathf.Min(best, Vector3.Distance(from, e.transform.position));
            }

            return best;
        }
    }
}
