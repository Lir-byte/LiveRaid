using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// The bots spread over the map (F12 -> 3 "Spread the bots over the map"). Raids 2026-10-30 / 31, quarters of Customs by the time the bots spent in them:
    /// PMCs sat 83 % in one quarter (5-10 min), 91 % in one at the end, two quarters empty for a quarter of an hour - in a lobby of real players the map
    /// is never a crowd in a corner and a desert elsewhere. What decides where a bot goes next is the weight of the quest it picks (QuestingBots'
    /// desirability), so that is where the balance is made - softly, no bot is ever moved by force:
    ///  - the map is a grid of regions (the exploration regions); the load of a region is the bots in and around it (a Gaussian of ~1 region), PMC 1,
    ///    player scav 0.8, a roaming scav 0.4; bosses and followers of bosses stay out of it;
    ///  - the share of the bots a region ought to have is its capacity (the loot weight of its spots, to the power 0.7: the rich places are busier, the
    ///    barren ones are not filled) times this raid's unevenness - a smooth random field, re-rolled and blended every 6 minutes, so the map is
    ///    never "exactly even" and the crowded corner moves from raid to raid;
    ///  - pressure = load / what the region ought to have, less the asking squad's own presence; inside a 1.4x dead band nothing is done; beyond it a quest in
    ///    the region weighs pressure^-k (k from the strength setting), between 0.4 and 2.2 times;
    ///  - each squad obeys it to its own degree (explorers and the greedy-for-distance more, campers, snipers and the quest-driven less): weight^obedience,
    ///    so a part of the map's bots always go where they like.
    /// Also the far point a restless or idle bot is sent to avoids the crowded regions (<see cref="Accept"/>). Airdrops, boss hunts and spawn rushes are events,
    /// not the walk of the map, and are left as they are.
    /// </summary>
    public static class MapBalance
    {
        private const float DeadBand = 0.34f; // ln(1.4)
        private const float MinWeight = 0.4f;
        private const float MaxWeight = 2.2f;
        private const float DriftSeconds = 360f;
        private const float BlendSeconds = 90f;

        private static readonly System.Random _rng = new System.Random();
        private static readonly Dictionary<string, (int, int)> _questRegion = new Dictionary<string, (int, int)>();

        private static bool _built;
        private static float _size;
        private static int _x0, _z0, _w, _h;
        private static float[] _capacity;
        private static float[] _noiseA, _noiseB;
        private static float _blendFrom;
        private static float _nextDrift;
        private static float[] _load;
        private static float[] _smoothLoad;
        private static float[] _smoothTarget;
        private static float _totalLoad;
        private static float _next;
        private static float _nextLog;
        private static float _indexSum;
        private static int _indexN;

        public static int Asked { get; private set; }
        public static int Damped { get; private set; }
        public static int Lifted { get; private set; }
        public static int FarPointsAvoided { get; private set; }

        public static string Summary =>
            !_built ? "map balance not used"
                : $"map balance {(_indexN > 0 ? _indexSum / _indexN : 0f):0.00} of the way to the targeted spread (1 = on target, over {_indexN} looks), " +
                  $"quests weighed {Asked} (held back {Damped}, favoured {Lifted}), far points turned away from crowds {FarPointsAvoided}";

        public static void Clear()
        {
            _built = false;
            _questRegion.Clear();
            _own.Clear();
            _capacity = _noiseA = _noiseB = _load = _smoothLoad = _smoothTarget = null;
            _next = _nextLog = _nextDrift = 0f;
            _totalLoad = 0f;
            _indexSum = 0f;
            _indexN = 0;
            Asked = Damped = Lifted = FarPointsAvoided = 0;
        }

        private static bool On => Settings.Enabled.Value && Settings.MapBalance.Value && Settings.MapBalanceStrength.Value > 0;

        private static int Idx(int rx, int rz) => (rz - _z0) * _w + (rx - _x0);

        private static bool Inside(int rx, int rz) => rx >= _x0 && rx < _x0 + _w && rz >= _z0 && rz < _z0 + _h;

        private static (int, int) RegionOf(Vector3 p) => (Mathf.FloorToInt(p.x / _size), Mathf.FloorToInt(p.z / _size));

        /// <summary>Value noise on a coarse grid (a node every 3 regions), bilinear, one factor per region: a smooth field of luck for this raid.</summary>
        private static float[] RollField()
        {
            float sigma = Mathf.Clamp(Settings.MapBalanceUneven.Value / 100f, 0f, 1.2f);
            int nw = _w / 3 + 2, nh = _h / 3 + 2;
            float[] nodes = new float[nw * nh];
            for (int i = 0; i < nodes.Length; i++)
            {
                // Two uniforms make a roughly bell-shaped draw without the log / cos
                nodes[i] = ((float)_rng.NextDouble() + (float)_rng.NextDouble() + (float)_rng.NextDouble() - 1.5f) * 1.3f;
            }

            float[] field = new float[_w * _h];
            for (int z = 0; z < _h; z++)
            {
                for (int x = 0; x < _w; x++)
                {
                    float fx = x / 3f, fz = z / 3f;
                    int ix = (int)fx, iz = (int)fz;
                    float tx = fx - ix, tz = fz - iz;
                    float a = Mathf.Lerp(nodes[iz * nw + ix], nodes[iz * nw + ix + 1], tx);
                    float b = Mathf.Lerp(nodes[(iz + 1) * nw + ix], nodes[(iz + 1) * nw + ix + 1], tx);
                    field[z * _w + x] = Mathf.Exp(sigma * Mathf.Lerp(a, b, tz));
                }
            }

            return field;
        }

        private static bool Build()
        {
            MapIntel intel = MapIntel.Current;
            // The spots are gathered over several frames: the grid waits for all of them
            if (intel == null || !intel.Ready || intel.Pois.Count < 20)
            {
                return false;
            }

            _size = Mathf.Max(60f, Settings.RoamRegionSize.Value);
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (Poi poi in intel.Pois)
            {
                (int rx, int rz) = RegionOf(poi.Pos);
                minX = Math.Min(minX, rx);
                maxX = Math.Max(maxX, rx);
                minZ = Math.Min(minZ, rz);
                maxZ = Math.Max(maxZ, rz);
            }

            // A margin of one region: bots walk a little beyond the outermost spot
            _x0 = minX - 1;
            _z0 = minZ - 1;
            _w = maxX - minX + 3;
            _h = maxZ - minZ + 3;
            if (_w * _h > 900 || _w < 3 || _h < 3)
            {
                return false;
            }

            float[] raw = new float[_w * _h];
            foreach (Poi poi in intel.Pois)
            {
                (int rx, int rz) = RegionOf(poi.Pos);
                if (Inside(rx, rz))
                {
                    raw[Idx(rx, rz)] += Mathf.Max(0.05f, poi.W);
                }
            }

            _capacity = Smooth(raw);
            for (int i = 0; i < _capacity.Length; i++)
            {
                // Rich places are busier, barren ones are not filled; a floor so that an empty cell is not "forbidden"
                _capacity[i] = Mathf.Pow(Mathf.Max(0f, _capacity[i]), 0.7f);
            }

            float mean = _capacity.Average();
            for (int i = 0; i < _capacity.Length; i++)
            {
                _capacity[i] = Mathf.Max(_capacity[i], 0.04f * mean);
            }

            _noiseA = RollField();
            _noiseB = RollField();
            _blendFrom = -1000f;
            _nextDrift = Time.time + DriftSeconds;
            _load = new float[_w * _h];
            _smoothLoad = new float[_w * _h];
            _smoothTarget = new float[_w * _h];
            _built = true;
            Log.Detail("info", $"LR Balance: {_w} x {_h} regions of {_size:0} m, targeted spread uneven by {Settings.MapBalanceUneven.Value} %, strength {Settings.MapBalanceStrength.Value} %");
            return true;
        }

        /// <summary>sum over cells of value * exp(-d^2 / 2) with d in regions.</summary>
        private static float[] Smooth(float[] src)
        {
            float[] dst = new float[src.Length];
            for (int z = 0; z < _h; z++)
            {
                for (int x = 0; x < _w; x++)
                {
                    float v = src[z * _w + x];
                    if (v <= 0f)
                    {
                        continue;
                    }

                    for (int dz = -3; dz <= 3; dz++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int tx = x + dx, tz = z + dz;
                            if (tx < 0 || tz < 0 || tx >= _w || tz >= _h)
                            {
                                continue;
                            }

                            dst[tz * _w + tx] += v * Mathf.Exp(-0.5f * (dx * dx + dz * dz));
                        }
                    }
                }
            }

            return dst;
        }

        private static float Weight(BotProfile p)
        {
            switch (p.Kind)
            {
                case BotKind.Pmc: return 1f;
                case BotKind.PlayerScav: return 0.8f;
                case BotKind.Scav: return 0.4f;
                default: return 0f;
            }
        }

        public static void Tick()
        {
            if (!On || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 5f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || (!_built && !Build()))
            {
                return;
            }

            float now = Time.time;
            if (now >= _nextDrift)
            {
                // The luck of the map moves on: a new field, blended in over 90 s
                _noiseA = _noiseB;
                _noiseB = RollField();
                _blendFrom = now;
                _nextDrift = now + DriftSeconds;
            }

            Array.Clear(_load, 0, _load.Length);
            _totalLoad = 0f;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotProfile p = bot != null && !bot.IsDead ? BotProfiles.Get(bot) : null;
                if (p == null)
                {
                    continue;
                }

                float w = Weight(p);
                (int rx, int rz) = RegionOf(bot.Position);
                if (w > 0f && Inside(rx, rz))
                {
                    _load[Idx(rx, rz)] += w;
                    _totalLoad += w;
                }
            }

            float[] sm = Smooth(_load);
            Array.Copy(sm, _smoothLoad, sm.Length);

            // What each region ought to hold: capacity times this raid's luck, smoothed like the load, scaled to the load there is
            float blend = Mathf.Clamp01((now - _blendFrom) / BlendSeconds);
            float[] want = new float[_capacity.Length];
            for (int i = 0; i < want.Length; i++)
            {
                want[i] = _capacity[i] * Mathf.Lerp(_noiseA[i], _noiseB[i], blend);
            }

            float[] target = Smooth(want);
            float sumTarget = target.Sum();
            float sumLoad = sm.Sum();
            if (sumTarget <= 0f)
            {
                return;
            }

            for (int i = 0; i < target.Length; i++)
            {
                _smoothTarget[i] = target[i] / sumTarget * sumLoad;
            }

            if (_totalLoad >= 6f)
            {
                // How near the spread is to the targeted one: 1 - half the sum of the differences of the shares
                float diff = 0f;
                for (int i = 0; i < sm.Length; i++)
                {
                    diff += Mathf.Abs(sm[i] / sumLoad - target[i] / sumTarget);
                }

                _indexSum += 1f - 0.5f * diff;
                _indexN++;
            }

            if (now >= _nextLog)
            {
                _nextLog = now + 120f;
                LogShares(sm, sumLoad, target, sumTarget);
            }
        }

        private static void LogShares(float[] sm, float sumLoad, float[] target, float sumTarget)
        {
            if (sumLoad <= 0f)
            {
                return;
            }

            // Quarters of the map by the middle of the grid, bots now / targeted
            float[] now4 = new float[4], tgt4 = new float[4];
            for (int z = 0; z < _h; z++)
            {
                for (int x = 0; x < _w; x++)
                {
                    int q = (x >= _w / 2 ? 1 : 0) + (z >= _h / 2 ? 2 : 0);
                    now4[q] += sm[z * _w + x] / sumLoad;
                    tgt4[q] += target[z * _w + x] / sumTarget;
                }
            }

            Log.Detail("info", $"LR Balance: {_totalLoad:0} bots; quarters (west-south, east-south, west-north, east-north) now " +
                               $"{string.Join("/", now4.Select(v => (v * 100f).ToString("0")))} %, targeted {string.Join("/", tgt4.Select(v => (v * 100f).ToString("0")))} %");
        }

        private struct Own
        {
            public int Rx, Rz;
            public float Weight;
            public float At;
        }

        // The asking squad's weight and region, looked up once in 5 s per bot (the desirability is asked hundreds of times per assignment)
        private static readonly Dictionary<string, Own> _own = new Dictionary<string, Own>();

        private static Own OwnOf(BotOwner bot)
        {
            float now = Time.time;
            if (_own.TryGetValue(bot.ProfileId, out Own o) && now - o.At < 5f)
            {
                return o;
            }

            BotProfile me = BotProfiles.Get(bot);
            float w = me != null ? Weight(me) : 0f;
            int mates = Mathf.Max(1, SquadTies.MatesOf(bot.ProfileId).Count());
            (int mx, int mz) = RegionOf(bot.Position);
            o = new Own { Rx = mx, Rz = mz, Weight = mates * w, At = now };
            _own[bot.ProfileId] = o;
            return o;
        }

        /// <summary>
        /// The pressure of a region for this bot: its load against what it ought to have, with the bot's own squad taken out of it (a squad is not crowded
        /// by itself). 1 = as it ought to be; above - crowded; below - empty.
        /// </summary>
        private static float Pressure(BotOwner bot, int rx, int rz)
        {
            int i = Idx(rx, rz);
            float load = _smoothLoad[i];
            if (bot != null)
            {
                Own me = OwnOf(bot);
                int dx = rx - me.Rx, dz = rz - me.Rz;
                load = Mathf.Max(0f, load - me.Weight * Mathf.Exp(-0.5f * (dx * dx + dz * dz)));
            }

            // One bot's worth of slack, so that with a few bots left nothing swings wildly
            return (load + 0.6f) / (_smoothTarget[i] + 0.6f);
        }

        /// <summary>How readily a bot follows the balance: explorers and the restless yes, campers, snipers and the quest-driven less.</summary>
        private static float Obedience(BotProfile p)
        {
            float o = 0.45f + 0.5f * p.Explore - 0.3f * p.Camp - 0.2f * p.Snipe - 0.15f * p.QuestFocus;
            return Mathf.Clamp(o, 0.2f, 1f);
        }

        private static float WeightOf(float pressure, float obedience)
        {
            // Strength 50 %: a region 3 times as crowded as it ought to be weighs about 0.55, one a third as full about 1.8
            float k = Settings.MapBalanceStrength.Value / 100f * 1.4f;
            float x = Mathf.Log(Mathf.Max(0.05f, pressure));
            float over = Mathf.Abs(x) <= DeadBand ? 0f : x - Mathf.Sign(x) * DeadBand;
            float full = Mathf.Clamp(Mathf.Exp(-k * over), MinWeight, MaxWeight);
            // Obedience is the exponent: a bot that follows it little takes only a part of the push
            return Mathf.Pow(full, obedience);
        }

        /// <summary>QbDesirabilityPatch: the weight for a quest by where it is.</summary>
        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On || !_built || bot == null || p == null || (p.Kind != BotKind.Pmc && p.Kind != BotKind.PlayerScav && !(p.Kind == BotKind.Scav && Settings.ScavsQuest.Value)))
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (name.IndexOf("Airdrop", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("Spawn Rush") || name.StartsWith("Boss Hunter"))
            {
                return 1f;
            }

            if (!_questRegion.TryGetValue(name, out (int, int) region))
            {
                Vector3? at = null;
                if (QuestFactory.TryParseRegion(name, out int qx, out int qz))
                {
                    region = (qx, qz);
                    at = Vector3.zero;
                }
                else
                {
                    at = quest.GetValidObjectives().FirstOrDefault()?.GetFirstStepPosition();
                    if (at.HasValue)
                    {
                        region = RegionOf(at.Value);
                    }
                }

                if (!at.HasValue)
                {
                    return 1f;
                }

                _questRegion[name] = region;
            }

            if (!Inside(region.Item1, region.Item2))
            {
                return 1f;
            }

            // The tasks of EFT's quests are where they are for a reason: only a part of the push
            float w = WeightOf(Pressure(bot, region.Item1, region.Item2), Obedience(p) * (quest.IsEFTQuest ? 0.4f : 1f));
            Asked++;
            if (w < 0.95f)
            {
                Damped++;
            }
            else if (w > 1.05f)
            {
                Lifted++;
            }

            return w;
        }

        /// <summary>A far point for a restless or idle bot: false (try another) with the chance the crowd of its region asks for.</summary>
        public static bool Accept(BotOwner bot, Vector3 point)
        {
            if (!On || !_built || bot == null)
            {
                return true;
            }

            BotProfile p = BotProfiles.Get(bot);
            (int rx, int rz) = RegionOf(point);
            if (p == null || !Inside(rx, rz))
            {
                return true;
            }

            float w = WeightOf(Pressure(bot, rx, rz), Obedience(p));
            // 1 or more: always; 0.4: one try in three
            if (w >= 1f || _rng.NextDouble() < w / 1.2f)
            {
                return true;
            }

            FarPointsAvoided++;
            return false;
        }
    }
}
