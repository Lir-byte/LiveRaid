using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>
    /// The armoured train's station (Reserve): the box of its exit zone as it stood when the train came, kept in learned/&lt;map&gt;.train.json (1.4.6). The train is a
    /// moving platform with its own navmesh cutting: a walk along its carriage is "off the navmesh" for 50 m, and was learned as six shortcuts of the bots' own
    /// (raid 2026-11-11 05:01); the bots then took those climbs - and were put inside the carriage and at the ditch by the rails, off the mesh (Кайл Беспредельщик stood
    /// 70 s at (162, -5, -152) until the raid ended). No shortcut and no navmesh patch is learned from a walk within 25 m of that box, and one that is in the file
    /// already is not taken.
    /// </summary>
    public static class TrainArea
    {
        private static string _location;
        private static Bounds? _box;

        private static string PathOf(string location) => Path.Combine(Plugin.PluginDir, "learned", location + ".train.json");

        public static void Begin(string location)
        {
            _location = location;
            _box = Load(location);
            TrainInterior.Begin(location);
            ExitSpots.Clear();
        }

        public static void Clear()
        {
            _location = null;
            _box = null;
            TrainInterior.Clear();
        }

        private static Bounds? Load(string location)
        {
            try
            {
                if (string.IsNullOrEmpty(location) || !File.Exists(PathOf(location)))
                {
                    return null;
                }

                string[] v = File.ReadAllText(PathOf(location)).Trim().Split(',');
                if (v.Length < 6)
                {
                    return null;
                }

                float[] f = new float[6];
                for (int i = 0; i < 6; i++)
                {
                    f[i] = float.Parse(v[i], CultureInfo.InvariantCulture);
                }

                return new Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The train has come: its zone is the station.</summary>
        public static void Remember(Bounds zone)
        {
            _box = zone;
            if (string.IsNullOrEmpty(_location))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf(_location)));
                File.WriteAllText(PathOf(_location), string.Join(",", new[] { zone.center.x, zone.center.y, zone.center.z, zone.size.x, zone.size.y, zone.size.z }
                    .Select(x => x.ToString("0.##", CultureInfo.InvariantCulture))));
            }
            catch (Exception e)
            {
                Log.Warn("LR Train: the station box could not be saved: " + e.Message);
            }
        }

        /// <summary>The point is within <paramref name="margin"/> m (all round) of the train's zone.</summary>
        public static bool Contains(Vector3 p, float margin)
        {
            if (!_box.HasValue)
            {
                return false;
            }

            Bounds b = _box.Value;
            b.Expand(margin * 2f);
            return b.Contains(p);
        }
    }
}
