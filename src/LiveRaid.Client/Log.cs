using System;

namespace LiveRaid.Client
{
    /// <summary>How much LiveRaid writes. Normal = the usual "LR ..." lines; Detailed adds a per-raid trace file; Full also mirrors it into the BepInEx log.</summary>
    public enum LogDetail
    {
        Normal,
        Detailed,
        Full,
    }

    internal static class Log
    {
        public static LogDetail Level => Settings.LogLevel?.Value ?? LogDetail.Normal;

        /// <summary>True when detail lines are recorded at all; check it before building an expensive message.</summary>
        public static bool IsDetailed => Level >= LogDetail.Detailed;

        public static void Info(string msg)
        {
            Plugin.Log?.LogInfo(msg);
            Trace.Write("info", msg);
        }

        public static void Warn(string msg)
        {
            Plugin.Log?.LogWarning(msg);
            Trace.Write("WARN", msg);
        }

        public static void Error(string msg)
        {
            Plugin.Log?.LogError(msg);
            Trace.Write("ERROR", msg);
        }

        /// <summary>A decision or state change worth knowing when analysing a raid: goes to the raid trace (Detailed) and the BepInEx log (Full).</summary>
        public static void Detail(string category, string msg)
        {
            LogDetail level = Level;
            if (level < LogDetail.Detailed)
            {
                return;
            }

            Trace.Write(category, msg);
            if (level >= LogDetail.Full)
            {
                Plugin.Log?.LogInfo($"[LR:{category}] {msg}");
            }
        }

        /// <summary>Old-style debug line; same as a detail line of category "debug".</summary>
        public static void Debug(string msg) => Detail("debug", msg);

        public static void Detail(string category, Func<string> msg)
        {
            if (IsDetailed)
            {
                Detail(category, msg());
            }
        }
    }
}
