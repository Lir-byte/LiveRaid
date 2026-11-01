using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Patches;
using LiveRaid.Client.Tactics;
using Newtonsoft.Json;
using QuestingBots.Helpers;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Measures what a raid feels like: how many bots there are, how many hostile groups the player actually lays eyes on, how soon the
    /// first one shows up, how often bots see the player, how much the bots shoot at each other and how they react to it.
    /// One CSV of events and one summary JSON per raid; <c>tools/encounters.py</c> aggregates them over many raids.
    /// </summary>
    public static class EncounterLog
    {
        private const float SightingRange = 150f;

        private static string _dir;
        private static string _stamp;
        private static string _location;
        private static float _startTime;

        // Raid time seen on the last tick: at GameWorld.Dispose the raid no longer counts as started, so End() cannot ask for it
        private static float _lastElapsed;
        private static float _nextTick;
        private static float _nextPopulation;
        private static readonly StringBuilder _rows = new StringBuilder();

        private static readonly Dictionary<object, float> _groupSeen = new Dictionary<object, float>();
        private static readonly HashSet<object> _groupsEverSeen = new HashSet<object>();
        private static readonly Dictionary<string, float> _botSawYou = new Dictionary<string, float>();

        private static int _pmcGroupSightings, _scavSightings, _bossSightings, _botSawYouEvents;
        private static float _firstSighting = -1f, _firstContact = -1f;
        private static int _humanKills, _humanDeaths, _botVsBotKills, _populationMax, _pmcAtMinute2 = -1;
        private static int _minPmcSeen = int.MaxValue;
        private static bool _written;

        // The pulse of the raid (liveliness): measured from you, or from the loose camera of the observer / after death
        private static Vector3 _focus;
        private static string _focusId;
        private static bool _hasFocus;
        private static readonly int[] _shotsThisMinute = new int[3];
        private static readonly int[] _shotsTotal = new int[3];
        private static int _pulseMinutes, _minutesWithShots500;
        private static float _lastNoise = -1f, _longestSilence, _longestSilenceAt;
        private static readonly List<int> _contacts10 = new List<int>();
        private static float _firstPeopleContact = -1f;
        private static float _minPeopleShare = -1f;
        private static int _minutesUnderTarget, _peopleMinutes;
        private static readonly Dictionary<string, (Vector3 pos, float time)> _lastPos = new Dictionary<string, (Vector3, float)>();
        private static readonly float[] _moved = new float[3];
        private static readonly float[] _movedTime = new float[3];
        private static float _nextSpeedSample;
        private static readonly float[] ShotRings = { 150f, 300f, 500f };

        public static void Begin(string location)
        {
            if (!Settings.EncounterLog.Value)
            {
                _dir = null;
                return;
            }

            _dir = Path.Combine(Plugin.PluginDir, "encounters");
            Directory.CreateDirectory(_dir);
            _location = location;
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _rows.Clear();
            _groupSeen.Clear();
            _groupsEverSeen.Clear();
            _botSawYou.Clear();
            _pmcGroupSightings = _scavSightings = _bossSightings = _botSawYouEvents = 0;
            _firstSighting = _firstContact = -1f;
            _humanKills = _humanDeaths = _botVsBotKills = _populationMax = 0;
            _pmcAtMinute2 = -1;
            _minPmcSeen = int.MaxValue;
            _written = false;
            _hasFocus = false;
            _focusId = null;
            Array.Clear(_shotsThisMinute, 0, 3);
            Array.Clear(_shotsTotal, 0, 3);
            _pulseMinutes = _minutesWithShots500 = 0;
            _lastNoise = -1f;
            _longestSilence = _longestSilenceAt = 0f;
            _contacts10.Clear();
            _firstPeopleContact = -1f;
            _minPeopleShare = -1f;
            _minutesUnderTarget = _peopleMinutes = 0;
            _lastPos.Clear();
            Array.Clear(_moved, 0, 3);
            Array.Clear(_movedTime, 0, 3);
            _nextSpeedSample = 0f;
            _startTime = -1f;
            _lastElapsed = 0f;
            _nextTick = _nextPopulation = 0;
            _rows.AppendLine("time,event,detail,x,z");
        }

        private static float Elapsed => RaidHelpers.HasRaidStarted() ? RaidHelpers.GetRaidElapsedSeconds() : 0f;

        private static void Add(string evt, string detail, Vector3 pos)
        {
            _rows.Append(Elapsed.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(evt).Append(',')
                .Append(detail.Replace(',', ';')).Append(',')
                .Append(pos.x.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(pos.z.ToString("0", CultureInfo.InvariantCulture)).AppendLine();
        }

        public static void Tick()
        {
            if (_dir == null || Time.time < _nextTick)
            {
                return;
            }

            _nextTick = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            // Measured from you (in LiveRaid Online: from a living player of the group when you are dead or on a headless host)
            Player human = Net.MainHuman();
            if (world == null || human == null || !RaidHelpers.HasRaidStarted())
            {
                return;
            }

            if (_startTime < 0)
            {
                _startTime = Time.time;
            }

            _lastElapsed = Mathf.Max(_lastElapsed, Elapsed);

            Vector3 eye = human.Position + Vector3.up * 1.6f;
            float now = Time.time;

            // Where the raid is listened to from: you, or the loose camera (observer, after death)
            bool loose = Spectator.SpectatorCamera.ListenerOverride(out Vector3 cam);
            _focus = loose ? cam : human.Position;
            _focusId = loose ? null : human.ProfileId;
            _hasFocus = true;
            Vector3 focusEye = loose ? cam : eye;
            if (_lastNoise < 0f)
            {
                _lastNoise = now;
            }

            bool speedSample = now >= _nextSpeedSample;
            if (speedSample)
            {
                _nextSpeedSample = now + 10f;
            }

            int pmc = 0, pscav = 0, scav = 0, other = 0, near150 = 0, near300 = 0;

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                switch (p?.Kind ?? BotKind.Other)
                {
                    case BotKind.Pmc: pmc++; break;
                    case BotKind.PlayerScav: pscav++; break;
                    case BotKind.Scav: scav++; break;
                    default: other++; break;
                }

                float d = Vector3.Distance(bot.Position, human.Position);
                if (d < 150f) near150++;
                if (d < 300f) near300++;

                // A hostile bot in sight of the listening point breaks the silence
                if (p != null && p.Kind != BotKind.Other)
                {
                    float df = loose ? Vector3.Distance(bot.Position, cam) : d;
                    if (df < SightingRange && !Physics.Linecast(focusEye, bot.Position + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask))
                    {
                        _lastNoise = now;
                    }

                    if (speedSample)
                    {
                        SampleSpeed(bot, p);
                    }
                }

                // Does the player see this bot (hostile group within view range, clear line)?
                if (d < SightingRange && !Physics.Linecast(eye, bot.Position + Vector3.up * 1.4f, LayersMaskController.HighPolyWithTerrainMask))
                {
                    object group = bot.BotsGroup;
                    if (group != null && (!_groupSeen.TryGetValue(group, out float last) || now - last > 180f))
                    {
                        bool firstTimeForGroup = _groupsEverSeen.Add(group);
                        _groupSeen[group] = now;
                        if (_firstSighting < 0)
                        {
                            _firstSighting = Elapsed;
                        }

                        switch (p?.Kind ?? BotKind.Other)
                        {
                            case BotKind.Pmc:
                            case BotKind.PlayerScav:
                                if (firstTimeForGroup)
                                {
                                    _pmcGroupSightings++;
                                }

                                break;
                            case BotKind.Scav: _scavSightings++; break;
                            default: _bossSightings++; break;
                        }

                        Add("sighting", $"{p?.Kind}:{p?.Style}:{d:0}m:{(firstTimeForGroup ? "new" : "again")}", bot.Position);
                        if (p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav))
                        {
                            Contact();
                        }
                    }
                }

                // Does this bot see the player?
                SAIN.Components.BotComponent sain = LiveRaid.Client.Bots.SainBot.Of(bot);
                if (sain?.GoalEnemy != null && Net.IsHuman(sain.GoalEnemy.EnemyPlayer) && sain.GoalEnemy.IsVisible
                    && (!_botSawYou.TryGetValue(bot.ProfileId, out float t) || now - t > 120f))
                {
                    _botSawYou[bot.ProfileId] = now;
                    _botSawYouEvents++;
                    if (_firstContact < 0)
                    {
                        _firstContact = Elapsed;
                    }

                    Add("bot_sees_you", $"{p?.Kind}:{p?.Style}:{d:0}m", bot.Position);
                    if (p != null && (p.Kind == BotKind.Pmc || p.Kind == BotKind.PlayerScav))
                    {
                        Contact();
                    }
                }
            }

            int hostile = pmc + pscav;
            _populationMax = Math.Max(_populationMax, pmc + pscav + scav);
            _minPmcSeen = Math.Min(_minPmcSeen, pmc);
            if (Elapsed >= 120f && _pmcAtMinute2 < 0)
            {
                _pmcAtMinute2 = pmc;
            }

            float silence = now - _lastNoise;
            if (silence > _longestSilence)
            {
                _longestSilence = silence;
                _longestSilenceAt = Elapsed;
            }

            if (now >= _nextPopulation)
            {
                _nextPopulation = now + 60f;
                int people = Bots.PScavReplacement.People(world);
                int lobby = Bots.PScavReplacement.Lobby();
                Add("population", $"pmc={pmc};pscav={pscav};scav={scav};other={other};within150={near150};within300={near300};people={people};lobby={lobby}", human.Position);
                Add("pulse", $"shots150={_shotsThisMinute[0]};shots300={_shotsThisMinute[1]};shots500={_shotsThisMinute[2]};silence={silence:0};contacts={_contacts10.Sum()}", _focus);
                if (Elapsed > 30f)
                {
                    _pulseMinutes++;
                    if (_shotsThisMinute[2] > 0)
                    {
                        _minutesWithShots500++;
                    }
                }

                Array.Clear(_shotsThisMinute, 0, 3);

                // People against the lobby: from minute 3 until the last 10 minutes
                if (lobby > 0 && Elapsed > 180f && RaidHelpers.GetRemainingRaidTimeSeconds() > 600f)
                {
                    float share = people / (float)lobby;
                    _minPeopleShare = _minPeopleShare < 0f ? share : Mathf.Min(_minPeopleShare, share);
                    _peopleMinutes++;
                    if (share * 100f < Settings.PeopleShare.Value - 0.5f)
                    {
                        _minutesUnderTarget++;
                    }
                }

                Flush();
            }
        }

        /// <summary>A shot somewhere on the map (WorldEvents.RecordShot), counted by distance from the listening point. Your own shots do not count.</summary>
        public static void OnShot(Vector3 position, string shooterId)
        {
            if (_dir == null || !_hasFocus || shooterId == _focusId)
            {
                return;
            }

            float d = Vector3.Distance(position, _focus);
            for (int i = 0; i < ShotRings.Length; i++)
            {
                if (d < ShotRings[i])
                {
                    _shotsThisMinute[i]++;
                    _shotsTotal[i]++;
                }
            }

            if (d < 300f)
            {
                _lastNoise = Time.time;
            }
        }

        private static void Contact()
        {
            int slot = (int)(Elapsed / 600f);
            while (_contacts10.Count <= slot)
            {
                _contacts10.Add(0);
            }

            _contacts10[slot]++;
            if (_firstPeopleContact < 0f)
            {
                _firstPeopleContact = Elapsed;
            }
        }

        /// <summary>How far a bot got since the last sample (every 10 s), counted while it is calm: the pace of the raid outside fights.</summary>
        private static void SampleSpeed(BotOwner bot, BotProfile p)
        {
            int k = p.Kind == BotKind.Pmc ? 0 : p.Kind == BotKind.PlayerScav ? 1 : 2;
            Vector3 pos = bot.Position;
            float now = Time.time;
            if (_lastPos.TryGetValue(bot.ProfileId, out var last) && BotMind.IsCalm(bot))
            {
                float dt = now - last.time;
                Vector2 step = new Vector2(pos.x - last.pos.x, pos.z - last.pos.z);
                // A route jump or a teleport is no walk
                if (dt > 5f && dt < 20f && step.magnitude < 9f * dt)
                {
                    _moved[k] += step.magnitude;
                    _movedTime[k] += dt;
                }
            }

            _lastPos[bot.ProfileId] = (pos, now);
        }

        /// <summary>Calm metres per minute of PMCs (0), player scavs (1), AI scavs (2) this raid; -1 when nobody was measured.</summary>
        public static float Pace(int kind) => _movedTime[kind] < 60f ? -1f : _moved[kind] / _movedTime[kind] * 60f;

        public static void OnKill(KillEvent kill, Player killer)
        {
            if (_dir == null)
            {
                return;
            }

            if (kill.VictimHuman)
            {
                _humanDeaths++;
                Add("you_died", $"killer={kill.KillerId}", kill.Position);
            }
            else if (Net.IsHuman(killer))
            {
                _humanKills++;
                Add("you_killed", kill.VictimId, kill.Position);
            }
            else
            {
                _botVsBotKills++;
                Add("bot_kill", $"{kill.KillerId}>{kill.VictimId}", kill.Position);
            }
        }

        private static void Flush()
        {
            if (_dir == null || _rows.Length == 0)
            {
                return;
            }

            try
            {
                File.AppendAllText(Path.Combine(_dir, $"{_location}_{_stamp}.csv"), _rows.ToString());
                _rows.Clear();
            }
            catch (Exception e)
            {
                Log.Warn("encounter log write failed: " + e.Message);
                _dir = null;
            }
        }

        public static void End()
        {
            if (_dir == null || _written)
            {
                return;
            }

            _written = true;
            Flush();
            float duration = Mathf.Max(Elapsed, _lastElapsed);
            var summary = new Dictionary<string, object>
            {
                ["map"] = _location,
                ["stamp"] = _stamp,
                ["raid_seconds"] = Math.Round(duration),
                ["pmc_at_start_planned"] = QbPmcReservePatch.InitialCount,
                ["pmc_alive_at_2min"] = _pmcAtMinute2,
                ["max_population"] = _populationMax,
                ["pmc_groups_seen_by_you"] = _pmcGroupSightings,
                ["scav_sightings"] = _scavSightings,
                ["boss_or_other_sightings"] = _bossSightings,
                ["first_sighting_seconds"] = Math.Round(_firstSighting),
                ["first_bot_saw_you_seconds"] = Math.Round(_firstContact),
                ["bots_saw_you_events"] = _botSawYouEvents,
                ["you_killed"] = _humanKills,
                ["you_died"] = _humanDeaths,
                ["bot_vs_bot_kills"] = _botVsBotKills,
                ["gunfire_fights_heard"] = GunfireResponse.HeardFights,
                ["gunfire_responses"] = GunfireResponse.Responses,
                ["gunfire_reactions"] = GunfireResponse.ReactionCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                ["stuck_reports"] = RaidMonitor.StuckReports,
                ["shots_per_min_150"] = PerMinute(_shotsTotal[0]),
                ["shots_per_min_300"] = PerMinute(_shotsTotal[1]),
                ["shots_per_min_500"] = PerMinute(_shotsTotal[2]),
                ["minutes_with_shots_500_share"] = _pulseMinutes == 0 ? -1 : Math.Round(_minutesWithShots500 / (double)_pulseMinutes, 2),
                ["longest_silence_seconds"] = Math.Round(_longestSilence),
                ["longest_silence_ended_at"] = Math.Round(_longestSilenceAt),
                ["contacts_per_10min"] = _contacts10.ToArray(),
                ["contacts"] = _contacts10.Sum(),
                ["first_people_contact_seconds"] = Math.Round(_firstPeopleContact),
                ["lobby_people"] = Bots.PScavReplacement.Lobby(),
                ["people_min_share_before_last10"] = Math.Round(_minPeopleShare, 2),
                ["minutes_under_people_target"] = _minutesUnderTarget,
                ["people_minutes_measured"] = _peopleMinutes,
                ["people_target_share"] = Settings.PeopleShare.Value / 100.0,
                ["pmc_m_per_min_calm"] = Math.Round(Pace(0)),
                ["pscav_m_per_min_calm"] = Math.Round(Pace(1)),
                ["scav_m_per_min_calm"] = Math.Round(Pace(2)),
                ["pscav_replacements"] = Bots.PScavReplacement.Scavs,
                ["liveraid_errors"] = Diag.ErrorCount,
            };

            try
            {
                File.WriteAllText(Path.Combine(_dir, $"{_location}_{_stamp}.summary.json"), JsonConvert.SerializeObject(summary, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log.Warn("encounter summary write failed: " + e.Message);
            }

            Log.Info($"LR Encounters on {_location}: {Math.Round(duration / 60f, 1)} min raid, {_pmcGroupSightings} PMC groups seen by you, " +
                     $"first sighting at {FormatTime(_firstSighting)}, first bot to see you at {FormatTime(_firstContact)}, PMCs alive at 2 min: {_pmcAtMinute2} " +
                     $"(planned {QbPmcReservePatch.InitialCount}), gunfire: {GunfireResponse.HeardFights} fights heard / {GunfireResponse.Responses} responses, " +
                     $"stuck reports {RaidMonitor.StuckReports}, you killed {_humanKills}");
            Log.Info($"LR Pulse on {_location}: first contact with people at {FormatTime(_firstPeopleContact)}, contacts {_contacts10.Sum()} " +
                     $"(by 10 min: {string.Join("/", _contacts10)}), shots a minute within 150/300/500 m {PerMinute(_shotsTotal[0])}/{PerMinute(_shotsTotal[1])}/{PerMinute(_shotsTotal[2])}, " +
                     $"minutes with shots within 500 m {(_pulseMinutes == 0 ? "-" : $"{100 * _minutesWithShots500 / _pulseMinutes} %")}, longest silence {FormatTime(_longestSilence)} (ended at {FormatTime(_longestSilenceAt)}), " +
                     $"people at least {(_minPeopleShare < 0 ? "-" : $"{_minPeopleShare * 100f:0} %")} of the lobby before the last 10 min (under {Settings.PeopleShare.Value} % for {_minutesUnderTarget} of {_peopleMinutes} min), " +
                     $"calm pace PMC/player scav/scav {Math.Round(Pace(0))}/{Math.Round(Pace(1))}/{Math.Round(Pace(2))} m/min");
        }

        private static double PerMinute(int shots)
        {
            float minutes = Mathf.Max(Elapsed, _lastElapsed) / 60f;
            return minutes < 1f ? -1 : Math.Round(shots / minutes, 1);
        }

        private static string FormatTime(float seconds) => seconds < 0 ? "never" : $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
    }
}
