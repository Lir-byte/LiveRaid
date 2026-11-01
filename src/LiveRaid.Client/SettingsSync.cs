using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace LiveRaid.Client
{
    /// <summary>
    /// LiveRaid Online: one set of settings for the group, owned by the host - the player whose machine runs the SPT server everybody plays on.
    /// The host's game sends its settings to the server whenever one changes; every other player takes them at start, while F12 is open and at the
    /// start of every raid (whoever hosts that raid runs the bots with them), and sees F12 locked. Who is the host: the server keeps a host key
    /// in host.token in its own folder - a game installed in the same SPT folder reads it, and a game on another machine (a dedicated or headless
    /// server) gets the same power from the key's text put once into the "Host key" of its cfg. A headless client is never the host.
    /// Single player: nothing here runs.
    /// </summary>
    internal static class SettingsSync
    {
        public enum Mode
        {
            Off,
            Host,
            Client,
            NoServer,
        }

        private const string GetRoute = "/liveraid/settings/get";
        private const string SetRoute = "/liveraid/settings/set";

        public static Mode Current { get; private set; } = Mode.Off;

        private static ConfigFile _cfg;
        private static ConfigEntry<string> _hostKey;

        // What to say in the locked F12 to someone who wants to be the host (server on another machine)
        private static string _hint = "";
        private static string _token;
        private static int _rev = -1;
        private static DateTime _received;
        private static bool _dirty;
        private static float _dirtyAt;
        private static float _nextPoll;
        private static volatile bool _busy;
        private static volatile string _incoming;
        private static readonly object Gate = new object();

        public static void Init(ConfigFile cfg)
        {
#if !LIVERAID_ONLINE
            return;
#else
            _cfg = cfg;

            // For a server on another machine (dedicated, headless): the text of the server's host.token, put in by hand once (the key stays the same across restarts)
            _hostKey = cfg.Bind("0. Online", "Host key", "",
                new ConfigDescription("Ключ хоста: текст файла host.token из папки сервера (user/mods/LiveRaid). Нужен, только если сервер SPT стоит на другом ПК, а вы хотите менять общие настройки", null,
                    new ConfigurationManagerAttributes { Browsable = false }));
            string json = Fetch();
            if (json == null)
            {
                Current = Mode.NoServer;
                SettingsMenu.BannerNote = "";
                SettingsMenu.SetLock(false, Loc.Tr("Сервер не отдал общие настройки (на нём нет LiveRaid 0.9+) - каждый играет со своими"));
                Log.Warn("LiveRaid Online: the SPT server has no shared settings (LiveRaid server 0.9 or newer needed) - this machine keeps its own");
                return;
            }

            JObject o = Parse(json);
            string server = (string)o?["server"] ?? "";
            bool headless = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.headless");
            string given = (_hostKey.Value ?? "").Trim();
            string fromFile = ReadToken();
            _token = given.Length > 0 && given == server ? given : fromFile != null && fromFile == server ? fromFile : null;
            _hint = headless ? "" : given.Length > 0
                ? Loc.Tr("Ключ в поле Host key не подходит к этому серверу - возьмите заново текст файла host.token из папки сервера (user/mods/LiveRaid)")
                : Loc.Tr("Сервер на другом ПК, а хотите менять настройки? Вставьте текст файла host.token из папки сервера (user/mods/LiveRaid) в поле Host key в BepInEx/config/com.lir.liveraid.cfg (секция 0. Online) и перезапустите игру");
            if (!headless && _token != null)
            {
                Current = Mode.Host;
                SettingsMenu.BannerNote = Loc.Tr("Ваши значения получают все игроки группы при каждом изменении и в начале каждого рейда");
                SettingsMenu.SetLock(false, Loc.Tr("Вы - хост: эти настройки действуют у всех игроков"));
                cfg.SettingChanged += OnChanged;
                _dirty = true;
                _dirtyAt = -10f;
                Log.Info("LiveRaid Online: this machine is the host (" + (_token == given ? "the Host key of its cfg fits the SPT server" : "it runs the SPT server") + ") - its F12 settings are the group's");
            }
            else
            {
                Current = Mode.Client;
                SettingsMenu.SetLock(true, Loc.Tr("НАСТРОЙКИ КОНТРОЛИРУЮТСЯ ТОЛЬКО ХОСТОМ"));
                Apply(o);
                Log.Info("LiveRaid Online: the SPT server runs on another machine - its host's settings are used here, F12 is locked" +
                         (headless ? " (a headless client is never the host)" : given.Length > 0 ? " (the Host key in the cfg does not fit this server)" : " (to be the host: put the text of the server's host.token into the Host key of the cfg, section '0. Online')"));
            }
#endif
        }

        /// <summary>At the start of a raid: the raid uses the host's settings as they are right now (also when this machine hosts the raid itself).</summary>
        public static void PullNow()
        {
            if (Current == Mode.Host)
            {
                PushNow();
                return;
            }

            if (Current != Mode.Client)
            {
                return;
            }

            string json = Fetch();
            if (json != null)
            {
                Apply(Parse(json));
            }
        }

        /// <summary>Every frame (Plugin.Update).</summary>
        public static void Tick()
        {
            if (Current == Mode.Host)
            {
                if (_dirty && !_busy && Time.realtimeSinceStartup - _dirtyAt > 1.5f)
                {
                    _dirty = false;
                    string body = Snapshot();
                    _busy = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            Post(body);
                        }
                        finally
                        {
                            _busy = false;
                        }
                    });
                }

                return;
            }

            if (Current != Mode.Client)
            {
                return;
            }

            string incoming = _incoming;
            if (incoming != null)
            {
                _incoming = null;
                Apply(Parse(incoming));
            }

            if (!_busy && Time.realtimeSinceStartup >= _nextPoll && SettingsMenu.WindowOpen)
            {
                _nextPoll = Time.realtimeSinceStartup + 3f;
                _busy = true;
                Task.Run(() =>
                {
                    try
                    {
                        _incoming = Fetch();
                    }
                    finally
                    {
                        _busy = false;
                    }
                });
            }

            if (Time.frameCount % 30 != 0)
            {
                return;
            }

            if (_rev >= 0)
            {
                int ago = (int)(DateTime.Now - _received).TotalSeconds;
                SettingsMenu.BannerNote = (Loc.Ru ? "Действуют значения хоста (версия " + _rev + ", получены " + (ago < 5 ? "только что" : ago + " с назад") + ")" : "The host's values apply (version " + _rev + ", received " + (ago < 5 ? "just now" : ago + " s ago") + ")") +
                                          (_hint.Length > 0 ? "\n" + _hint : "");
            }
            else
            {
                SettingsMenu.BannerNote = Loc.Tr("Хост ещё не сохранил свои настройки - пока действуют ваши") + (_hint.Length > 0 ? "\n" + _hint : "");
            }
        }

        private static void OnChanged(object sender, SettingChangedEventArgs e)
        {
            if (SettingsMenu.SyncedEntries.Contains(e.ChangedSetting))
            {
                _dirty = true;
                _dirtyAt = Time.realtimeSinceStartup;
            }
        }

        private static void PushNow()
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            Post(Snapshot());
        }

        private static string Key(ConfigEntryBase e) => e.Definition.Section + "\n" + e.Definition.Key;

        private static string Snapshot()
        {
            Dictionary<string, string> values = SettingsMenu.SyncedEntries.ToDictionary(Key, e => e.GetSerializedValue());
            return JsonConvert.SerializeObject(new Dictionary<string, object> { ["token"] = _token, ["by"] = Environment.MachineName, ["values"] = values });
        }

        private static void Post(string body)
        {
            try
            {
                string answer = RequestHandler.PostJson(SetRoute, body);
                JObject o = Parse(answer);
                if ((bool?)o?["ok"] != true)
                {
                    Log.Warn("LiveRaid Online: the server did not take the host's settings (" + (answer ?? "no answer") + ")");
                }
            }
            catch (Exception e)
            {
                Log.Warn("LiveRaid Online: the host's settings could not be sent to the server: " + e.Message);
            }
        }

        private static string Fetch()
        {
            try
            {
                string json = RequestHandler.GetJson(GetRoute);
                return string.IsNullOrEmpty(json) || !json.TrimStart().StartsWith("{") || !json.Contains("\"server\"") ? null : json;
            }
            catch
            {
                return null;
            }
        }

        private static JObject Parse(string json)
        {
            try
            {
                return string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        private static void Apply(JObject o)
        {
            int rev = (int?)o?["rev"] ?? -1;
            if (!(o?["values"] is JObject values) || rev <= 0 || rev == _rev)
            {
                return;
            }

            lock (Gate)
            {
                int changed = 0;
                bool save = _cfg.SaveOnConfigSet;
                _cfg.SaveOnConfigSet = false;
                SettingsMenu.BeginBulk();
                try
                {
                    foreach (ConfigEntryBase entry in SettingsMenu.SyncedEntries.ToList())
                    {
                        string v = (string)values[Key(entry)];
                        if (v != null && v != entry.GetSerializedValue())
                        {
                            try
                            {
                                entry.SetSerializedValue(v);
                                changed++;
                            }
                            catch (Exception e)
                            {
                                Log.Warn($"LiveRaid Online: the host's value of '{entry.Definition.Key}' could not be read ({v}): {e.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    SettingsMenu.EndBulk();
                    _cfg.SaveOnConfigSet = save;
                }

                if (changed > 0)
                {
                    _cfg.Save();
                }

                _rev = rev;
                _received = DateTime.Now;
                Log.Info($"LiveRaid Online: the host's settings taken (version {rev}, {changed} changed)");
            }
        }

        /// <summary>host.token of the LiveRaid server mod in this game's own SPT folder (the server writes it at every start).</summary>
        private static string ReadToken()
        {
            string root = BepInEx.Paths.GameRootPath;
            foreach (string dir in new[] { "SPT_Runtime", "SPT", "" })
            {
                try
                {
                    string path = Path.Combine(root, dir, "user", "mods", "LiveRaid", "host.token");
                    if (File.Exists(path))
                    {
                        return File.ReadAllText(path).Trim();
                    }
                }
                catch
                {
                    // look further
                }
            }

            return null;
        }
    }
}
