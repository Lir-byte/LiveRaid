using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// Smoothie (com.tominocz.smoothie) has three experimental culling features that decide what is drawn from ONE camera - EFT's "FPS Camera" of the raid:
    /// "Hide objects covered by nearer ones" (a depth pyramid of the camera's picture switches off renderers behind nearer ones, and lamps and shadows with
    /// them) and "Skip LOD work for objects you cannot see / too far to draw". A take-over makes EFT destroy that camera and build a new one (and the free
    /// camera after the body's death flies where nobody walks), so what Smoothie hid or skipped for the old camera was left as it was: houses, objects and
    /// textures of the map did not come back after the taken bot died - the "textures stopped loading" look (the user installed Smoothie in October;
    /// LiveRaid's own occlusion rebinding in YourPlayerState had not helped since).
    /// From the first take-over of a raid until the raid ends these features are switched off in Smoothie's own settings (in memory only: the file is not
    /// written, the user's values are put back at the raid's end), and everything they hid is shown again. F12 can turn this off.
    /// </summary>
    internal static class SmoothieGuard
    {
        private const string Guid = "com.tominocz.smoothie";
        private static readonly string[] KeyParts = { "covered by nearer", "Skip LOD work" };

        private static readonly List<(ConfigEntryBase entry, object was)> _off = new List<(ConfigEntryBase, object)>();
        private static ConfigFile _file;
        private static bool _saveWas;
        private static Assembly _assembly;

        public static bool Engaged => _off.Count > 0;

        private static bool? _installed;

        /// <summary>Smoothie is in BepInEx/plugins (a folder or a DLL named Smoothie...); the F12 setting exists only then.</summary>
        public static bool Installed()
        {
            if (_installed.HasValue)
            {
                return _installed.Value;
            }

            bool found = false;
            try
            {
                string plugins = BepInEx.Paths.PluginPath;
                found = System.IO.Directory.GetDirectories(plugins).Any(d => System.IO.Path.GetFileName(d).StartsWith("Smoothie", StringComparison.OrdinalIgnoreCase))
                        || System.IO.Directory.GetFiles(plugins, "Smoothie*.dll", System.IO.SearchOption.AllDirectories).Length > 0;
            }
            catch (Exception)
            {
                // cannot look: no setting shown
            }

            _installed = found;
            return found;
        }

        private static BaseUnityPlugin Plugin() =>
            Chainloader.PluginInfos != null && Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info) ? info.Instance : null;

        /// <summary>The first take-over of the raid: Smoothie's culling off, what it hid shown.</summary>
        public static void Engage(string why)
        {
            if (Engaged || Settings.SmoothieGuard == null || !Settings.SmoothieGuard.Value)
            {
                return;
            }

            try
            {
                BaseUnityPlugin smoothie = Plugin();
                if (smoothie == null)
                {
                    return;
                }

                _assembly = smoothie.GetType().Assembly;
                _file = smoothie.Config;
                _saveWas = _file.SaveOnConfigSet;
                _file.SaveOnConfigSet = false;
                foreach (ConfigDefinition definition in _file.Keys.ToList())
                {
                    ConfigEntryBase entry = _file[definition];
                    if (entry.SettingType == typeof(bool) && KeyParts.Any(p => definition.Key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) && (bool)entry.BoxedValue)
                    {
                        _off.Add((entry, entry.BoxedValue));
                        entry.BoxedValue = false;
                    }
                }

                ShowAll();
                Log.Info($"LR Smoothie: {_off.Count} of its culling features switched off for the rest of the raid ({why}): " +
                         string.Join(", ", _off.Select(o => "\"" + o.entry.Definition.Key + "\"")) + " - they cannot follow a camera that is rebuilt; your values come back when the raid ends");
            }
            catch (Exception e)
            {
                Log.Warn("LR Smoothie: could not switch its culling off: " + e.Message);
            }
        }

        /// <summary>The raid is over: Smoothie's settings are as the user had them.</summary>
        public static void Release()
        {
            if (!Engaged)
            {
                return;
            }

            try
            {
                foreach ((ConfigEntryBase entry, object was) in _off)
                {
                    entry.BoxedValue = was;
                }

                Log.Info($"LR Smoothie: its {_off.Count} culling features are back as you had them");
            }
            catch (Exception e)
            {
                Log.Warn("LR Smoothie: could not put its settings back: " + e.Message);
            }
            finally
            {
                if (_file != null)
                {
                    _file.SaveOnConfigSet = _saveWas;
                }

                _off.Clear();
                _file = null;
            }
        }

        /// <summary>Everything Smoothie hid (covered objects, lamps, LOD-skipped objects) is drawn again.</summary>
        private static void ShowAll()
        {
            foreach (string name in new[] { "Smoothie.DepthCulling", "Smoothie.LodCulling" })
            {
                try
                {
                    Type type = _assembly?.GetType(name);
                    MethodInfo method = type == null ? null : AccessTools.Method(type, "ShowAll");
                    method?.Invoke(null, null);
                }
                catch (Exception e)
                {
                    Log.Detail("debug", $"LR Smoothie: {name}.ShowAll: {e.GetBaseException().Message}");
                }
            }
        }

        /// <summary>For the state line in the log.</summary>
        public static string Text()
        {
            try
            {
                if (Plugin() == null)
                {
                    return "no Smoothie";
                }

                string status = "";
                Type type = (_assembly ?? Plugin().GetType().Assembly).GetType("Smoothie.DepthCulling");
                FieldInfo field = type == null ? null : AccessTools.Field(type, "Status");
                if (field != null)
                {
                    status = field.GetValue(null) as string ?? "";
                }

                return $"Smoothie culling {(Engaged ? $"OFF by LiveRaid ({_off.Count})" : "as set")}{(status.Length > 0 ? ", depth: " + status : "")}";
            }
            catch (Exception e)
            {
                return "Smoothie: ? " + e.Message;
            }
        }
    }
}
