using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// Measures how steady the bots' brains are - the thing a player notices as "jerky" or "can't make up its mind".
    /// BigBrain hands a bot every tick to the first layer that wants it, with no hold at all, and SAIN re-decides ten times a second,
    /// so any flickering condition turns into a bot switching back and forth. Every frame this watches each PMC/scav's active brain layer
    /// and SAIN's decision, and counts switches, how long each lasted, and ping-pongs (A -> B -> A with B lasting under 3 s for layers,
    /// under 2 s for SAIN decisions). Raid summary: "LR Brain on &lt;map&gt;"; every ping-pong goes to the trace (category "brain", rate-limited).
    /// </summary>
    public static class BrainWatch
    {
        private const float LayerPingPong = 3f;
        private const float DecisionPingPong = 2f;

        private class Track
        {
            public string Name;
            public string Layer;
            public float LayerSince;
            public string PrevLayer;

            public string Decision;
            public float DecisionSince;
            public string PrevDecision;

            public float FirstSeen;
            public float LastSeen;
            public float NextTraceAt;
        }

        private static readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        private static readonly Dictionary<string, int> _layerPingPongs = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _decisionPingPongs = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _shortLayers = new Dictionary<string, int>();
        private static int _layerSwitches, _layerPings, _decisionSwitches, _decisionPings, _segments, _shortSegments;

        public static void Clear()
        {
            _tracks.Clear();
            _layerPingPongs.Clear();
            _decisionPingPongs.Clear();
            _shortLayers.Clear();
            _layerSwitches = _layerPings = _decisionSwitches = _decisionPings = _segments = _shortSegments = 0;
        }

        public static void Tick()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            float now = Time.time;
            foreach (Player player in world.AllAlivePlayersList.ToArray())
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null || bot.IsDead || bot.BotState != EBotState.Active || Spectator.TakeOver.IsTaken(bot))
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                if (p == null || p.Kind == BotKind.Other)
                {
                    continue;
                }

                string layer;
                try
                {
                    layer = bot.Brain?.ActiveLayerName() ?? "-";
                }
                catch
                {
                    continue;
                }

                if (!_tracks.TryGetValue(bot.ProfileId, out Track t))
                {
                    _tracks[bot.ProfileId] = t = new Track { Name = bot.Profile.Nickname, Layer = layer, LayerSince = now, FirstSeen = now, DecisionSince = now };
                }

                t.LastSeen = now;
                if (layer != t.Layer)
                {
                    OnLayerSwitch(bot, t, layer, now);
                }

                SAIN.Components.BotComponent sain = SainBot.Of(bot);
                string decision = sain?.Decision == null ? "-" : sain.Decision.CurrentCombatDecision + "/" + sain.Decision.CurrentSquadDecision + "/" + sain.Decision.CurrentSelfDecision;
                if (t.Decision == null)
                {
                    t.Decision = decision;
                    t.DecisionSince = now;
                }
                else if (decision != t.Decision)
                {
                    OnDecisionSwitch(t, decision, now);
                }
            }
        }

        private static void OnLayerSwitch(BotOwner bot, Track t, string layer, float now)
        {
            float lasted = now - t.LayerSince;
            _layerSwitches++;
            _segments++;
            if (lasted < 2f)
            {
                _shortSegments++;
                Count(_shortLayers, t.Layer);
            }

            if (layer == t.PrevLayer && lasted < LayerPingPong)
            {
                _layerPings++;
                string pair = Pair(t.PrevLayer, t.Layer);
                Count(_layerPingPongs, pair);
                TraceOnce(t, now, $"{t.Name} layer ping-pong {t.PrevLayer} -> {t.Layer} ({lasted:0.0} s) -> {layer}");
                Bots.Harmonizer.OnPingPong(bot, t.PrevLayer, t.Layer, lasted);
            }

            t.PrevLayer = t.Layer;
            t.Layer = layer;
            t.LayerSince = now;
        }

        private static void OnDecisionSwitch(Track t, string decision, float now)
        {
            float lasted = now - t.DecisionSince;
            _decisionSwitches++;
            if (decision == t.PrevDecision && lasted < DecisionPingPong)
            {
                _decisionPings++;
                Count(_decisionPingPongs, Pair(t.PrevDecision, t.Decision));
                TraceOnce(t, now, $"{t.Name} SAIN decision ping-pong {t.PrevDecision} -> {t.Decision} ({lasted:0.0} s) -> {decision}");
            }

            t.PrevDecision = t.Decision;
            t.Decision = decision;
            t.DecisionSince = now;
        }

        private static void TraceOnce(Track t, float now, string line)
        {
            // At most one line per bot every 5 s: a flickering bot would flood the trace
            if (now < t.NextTraceAt)
            {
                return;
            }

            t.NextTraceAt = now + 5f;
            Log.Detail("brain", line);
        }

        private static string Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + " <-> " + b : b + " <-> " + a;

        private static void Count(Dictionary<string, int> d, string key) => d[key] = (d.TryGetValue(key, out int n) ? n : 0) + 1;

        private static string Top(Dictionary<string, int> d, int n) =>
            d.Count == 0 ? "none" : string.Join("; ", d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Key} x{kv.Value}"));

        /// <summary>Raid summary line (call before Clear).</summary>
        public static string Summary(string location)
        {
            float botMinutes = _tracks.Values.Sum(t => Mathf.Max(0f, t.LastSeen - t.FirstSeen)) / 60f;
            if (botMinutes < 0.5f)
            {
                return null;
            }

            string Rate(int n) => (n / botMinutes).ToString("0.0");
            return $"LR Brain on {location}: {botMinutes:0} bot-minutes; layer switches {Rate(_layerSwitches)}/bot-min, ping-pongs {Rate(_layerPings)}/bot-min, " +
                   $"layers held under 2 s {(_segments > 0 ? 100f * _shortSegments / _segments : 0f):0}%; " +
                   $"SAIN decision changes {Rate(_decisionSwitches)}/bot-min, ping-pongs {Rate(_decisionPings)}/bot-min; smooth decisions {(Settings.SmoothDecisions.Value ? "on" : "off")}, " +
                   $"smooth combat decisions {(Settings.SmoothCombatDecisions.Value ? "on" : "off")}" +
                   $" | top layer ping-pongs: {Top(_layerPingPongs, 5)} | shortest-lived layers: {Top(_shortLayers, 4)} | top SAIN decision ping-pongs: {Top(_decisionPingPongs, 5)}";
        }
    }
}
