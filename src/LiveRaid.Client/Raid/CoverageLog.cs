using System;
using System.Globalization;
using System.IO;
using System.Text;
using Comfort.Common;
using EFT;
using LiveRaid.Client.Bots;
using UnityEngine;

namespace LiveRaid.Client.Raid
{
    /// <summary>Every 10 s writes where each bot is, to measure how much of the map bots actually cover.</summary>
    public static class CoverageLog
    {
        private static string _file;
        private static float _next;
        private static readonly StringBuilder _buffer = new StringBuilder();

        public static void Begin(string locationId)
        {
            _next = 0;
            _buffer.Clear();
            if (!Settings.CoverageLog.Value)
            {
                _file = null;
                return;
            }

            string dir = Path.Combine(Plugin.PluginDir, "coverage");
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, $"{locationId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(_file, "time,bot,role,kind,style,x,y,z\n");
        }

        public static void Tick()
        {
            if (_file == null || Time.time < _next)
            {
                return;
            }

            _next = Time.time + 10f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            int t = (int)Time.time;
            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                if (bot == null)
                {
                    continue;
                }

                BotProfile p = BotProfiles.Get(bot);
                Vector3 pos = bot.Position;
                bool tuned = p != null && p.Kind != BotKind.Other;
                _buffer.Append(t).Append(',').Append(bot.Profile.Nickname.Replace(',', ' ')).Append(',').Append(bot.Profile.Info.Settings.Role).Append(',')
                    .Append(tuned ? p.Kind.ToString() : "Other").Append(',').Append(tuned ? p.Style.ToString() : "-").Append(',')
                    .Append(pos.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pos.y.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pos.z.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
            }

            try
            {
                File.AppendAllText(_file, _buffer.ToString());
            }
            catch (Exception e)
            {
                Log.Warn("coverage log write failed: " + e.Message);
                _file = null;
            }

            _buffer.Clear();
        }
    }
}
