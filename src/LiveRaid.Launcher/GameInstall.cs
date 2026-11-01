using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveRaid.Launcher
{
    internal enum Mode
    {
        Unknown,
        Single,
        Online,
        Mixed,
    }

    /// <summary>Branch of the mod: the stable one (main) or the test branch (experimental, "-test" in the DLLs' product version).</summary>
    internal enum Channel
    {
        Stable,
        Test,
    }

    /// <summary>
    /// One SPT game folder and what the launcher does with it. Single player: everything of Project Fika (by name - "fika" in it - in the game root,
    /// BepInEx/plugins, BepInEx/config, BepInEx/patchers and SPT_Runtime/user/mods) is moved as it is into "LiveRaid Launcher/fika-off", with its
    /// settings and its server data, and the single-player LiveRaid DLL goes into BepInEx/plugins/LiveRaid. Online: the same folders go back where
    /// they were and the Online DLL comes in. LiveRaid's own F12 settings are kept per mode. A move that fails undoes every move made before it.
    /// Branches: the stable and the test LiveRaid each have their client DLLs (editions\&lt;Single|Online&gt; and editions\Test\&lt;Single|Online&gt;)
    /// and their server (editions\Server and editions\Test\Server); switching the branch swaps the client and the server's DLL and data, keeping the
    /// mode, and each branch keeps its own F12 settings too. The stable server in the game is always put aside before a test one comes in.
    /// </summary>
    internal sealed class GameInstall
    {
        public const string HomeName = "LiveRaid Launcher";
        private const string LiveRaidCfg = "com.lir.liveraid.cfg";

        private static readonly string[] Parents = { "", @"BepInEx\plugins", @"BepInEx\config", @"BepInEx\patchers", @"SPT_Runtime\user\mods", @"SPT\user\mods" };

        public readonly string Root;

        public GameInstall(string root)
        {
            Root = root;
        }

        public string Home => Path.Combine(Root, HomeName);
        public string Parked => Path.Combine(Home, "fika-off");
        public string LiveDll => Path.Combine(Root, @"BepInEx\plugins\LiveRaid\LiveRaid.Client.dll");
        /// <summary>The server's folder in the game: "SPT_Runtime" in the SPT 4.1 installer's layout, "SPT" in the older 4.x one.</summary>
        public string ServerFolder => Directory.Exists(Path.Combine(Root, "SPT_Runtime")) || !Directory.Exists(Path.Combine(Root, "SPT")) ? "SPT_Runtime" : "SPT";

        public string LiveServerDir => Path.Combine(Root, ServerFolder, @"user\mods\LiveRaid");
        private string Editions(Channel c) => c == Channel.Test ? Path.Combine(Home, "editions", "Test") : Path.Combine(Home, "editions");
        public string EditionDll(Mode m, Channel c) => Path.Combine(Editions(c), m == Mode.Online ? "Online" : "Single", "LiveRaid.Client.dll");
        public string ServerKept(Channel c) => Path.Combine(Editions(c), "Server");
        private string LiveCfg => Path.Combine(Root, @"BepInEx\config", LiveRaidCfg);
        private string ModeCfg(Mode m, Channel c) =>
            Path.Combine(Home, "settings", c == Channel.Test ? "Test" : "", m == Mode.Online ? "Online" : "Single", LiveRaidCfg);
        private string FikaCore(string under) => Path.Combine(under, @"BepInEx\plugins\Fika\Fika.Core.dll");

        public static bool LooksLikeGame(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "EscapeFromTarkov.exe")) && Directory.Exists(Path.Combine(dir, "BepInEx"));

        // ------------------------------------------------------------------ what is there

        public sealed class State
        {
            public bool FikaLive;
            public bool FikaParked;
            public string FikaVersion = "";
            public string Edition = "";
            public string Version = "";
            public bool HasSingle;
            public bool HasOnline;
            public Channel Channel;
            public Channel ServerChannel;
            public string ServerVersion = "";
            public bool HasTest;
            public bool HasStable;
            public string Running = "";
            public bool ServerRunning;
            public bool GameRunning;
            public List<string> LiveItems = new List<string>();
            public List<string> ParkedItems = new List<string>();

            public Mode Mode
            {
                get
                {
                    if (Edition == "")
                    {
                        return FikaLive ? Mode.Online : Mode.Unknown;
                    }

                    if (Edition == "Online")
                    {
                        return FikaLive ? Mode.Online : Mode.Mixed;
                    }

                    return FikaLive ? Mode.Mixed : Mode.Single;
                }
            }
        }

        public State Read()
        {
            State s = new State
            {
                LiveItems = Items(Root),
                ParkedItems = Directory.Exists(Parked) ? Items(Parked) : new List<string>(),
                FikaLive = File.Exists(FikaCore(Root)),
                FikaParked = File.Exists(FikaCore(Parked)),
            };
            string server = Path.Combine(LiveServerDir, "LiveRaid.Server.dll");
            if (File.Exists(server))
            {
                s.ServerVersion = Version(server);
                s.ServerChannel = ChannelOf(s.ServerVersion);
            }
            string fika = s.FikaLive ? FikaCore(Root) : s.FikaParked ? FikaCore(Parked) : null;
            if (fika != null)
            {
                s.FikaVersion = Version(fika);
            }

            if (File.Exists(LiveDll))
            {
                s.Edition = EditionOf(LiveDll);
                s.Version = Version(LiveDll);
                s.Channel = ChannelOf(s.Version);
            }
            else
            {
                s.Channel = s.ServerChannel;
            }

            s.HasSingle = File.Exists(EditionDll(Mode.Single, s.Channel));
            s.HasOnline = File.Exists(EditionDll(Mode.Online, s.Channel));
            Channel other = s.Channel == Channel.Test ? Channel.Stable : Channel.Test;
            bool otherThere = (File.Exists(EditionDll(Mode.Single, other)) || File.Exists(EditionDll(Mode.Online, other))) &&
                              (File.Exists(Path.Combine(ServerKept(other), "LiveRaid.Server.dll")) || (s.ServerVersion != "" && s.ServerChannel == other));
            s.HasTest = s.Channel == Channel.Test || otherThere;
            s.HasStable = s.Channel == Channel.Stable || otherThere;

            List<string> running = Blockers();
            s.Running = string.Join(", ", running.Select(L.Tr));
            s.ServerRunning = running.Any(r => r.StartsWith("сервер", StringComparison.OrdinalIgnoreCase));
            s.GameRunning = running.Any(r => r.StartsWith("игра", StringComparison.OrdinalIgnoreCase));
            return s;
        }

        /// <summary>Single / Online by what the DLL references: only LiveRaid Online is built against Fika.Core.</summary>
        public static string EditionOf(string dll)
        {
            try
            {
                byte[] data = File.ReadAllBytes(dll);
                return Contains(data, Encoding.ASCII.GetBytes("Fika.Core")) ? "Online" : "Single";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>The test branch marks its DLLs "x.y.z-test" (product version); the stable ones carry the bare number.</summary>
        public static Channel ChannelOf(string version) =>
            version.IndexOf("-test", StringComparison.OrdinalIgnoreCase) >= 0 ? Channel.Test : Channel.Stable;

        public static string Version(string dll)
        {
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(dll);
                string s = string.IsNullOrEmpty(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
                return (s ?? "").Split('+')[0];
            }
            catch
            {
                return "";
            }
        }

        private static bool Contains(byte[] data, byte[] what)
        {
            for (int i = 0; i <= data.Length - what.Length; i++)
            {
                int j = 0;
                while (j < what.Length && data[i + j] == what[j])
                {
                    j++;
                }

                if (j == what.Length)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Everything of Fika under a root (relative paths).</summary>
        private static List<string> Items(string under)
        {
            List<string> found = new List<string>();
            foreach (string parent in Parents)
            {
                string dir = Path.Combine(under, parent);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string name = Path.GetFileName(entry);
                    if (name.IndexOf("fika", StringComparison.OrdinalIgnoreCase) >= 0 && !name.Equals(HomeName, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(Path.Combine(parent, name));
                    }
                }
            }

            return found;
        }

        /// <summary>Programs that hold the files the launcher moves.</summary>
        public static List<string> Blockers()
        {
            List<string> found = new List<string>();
            void Check(string process, string what)
            {
                Process[] p = Process.GetProcessesByName(process);
                if (p.Length > 0)
                {
                    found.Add(what);
                }

                foreach (Process x in p)
                {
                    x.Dispose();
                }
            }

            Check("EscapeFromTarkov", "игра (EscapeFromTarkov)");
            Check("SPT.Server", "сервер SPT");
            Check("SPT.Launcher", "лаунчер SPT");
            Check("Fika-Installer", "установщик Fika");
            return found;
        }

        // ------------------------------------------------------------------ switching

        private readonly List<Action> _undo = new List<Action>();

        public void SwitchTo(Mode target, Action<string, int> log)
        {
            Mode from = target == Mode.Online ? Mode.Single : Mode.Online;
            List<string> blockers = Blockers();
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException(L.Tr("сначала закройте: ") + string.Join(", ", blockers.Select(L.Tr)));
            }

            Channel channel = Read().Channel;
            string edition = EditionDll(target, channel);
            if (!File.Exists(edition))
            {
                throw new InvalidOperationException(L.Tr("нет файла редакции ") + Rel(edition) + L.Tr(" - распакуйте архив LiveRaid целиком (в нём обе редакции)"));
            }

            _undo.Clear();
            try
            {
                log(L.Tr("сохраняю настройки LiveRaid режима «") + Title(from) + "»", 0);
                Copy(LiveCfg, ModeCfg(from, channel), log, undoable: false);

                if (target == Mode.Single)
                {
                    List<string> items = Items(Root);
                    log(items.Count == 0 ? L.Tr("Fika в игре не найдена - убирать нечего") : L.Tr("убираю Fika: ") + items.Count + L.Tr(" папок и файлов"), 0);
                    foreach (string rel in items)
                    {
                        Park(Path.Combine(Root, rel), Path.Combine(Parked, rel), log);
                    }
                }
                else
                {
                    List<string> items = Directory.Exists(Parked) ? Items(Parked) : new List<string>();
                    log(items.Count == 0 ? L.Tr("отложенной Fika нет") : L.Tr("возвращаю Fika на место: ") + items.Count + L.Tr(" папок и файлов"), 0);
                    foreach (string rel in items)
                    {
                        Park(Path.Combine(Parked, rel), Path.Combine(Root, rel), log);
                    }

                    if (!File.Exists(FikaCore(Root)))
                    {
                        throw new InvalidOperationException(L.Tr("Fika не найдена ни в игре, ни в отложенных - установите её через Fika-Installer и переключите ещё раз"));
                    }
                }

                log(L.Tr("ставлю ") + (target == Mode.Online ? "LiveRaid Online" : L.Tr("LiveRaid (одиночный)")) + " " + Version(edition), 0);
                Copy(edition, LiveDll, log, undoable: true);

                if (File.Exists(ModeCfg(target, channel)))
                {
                    log(L.Tr("возвращаю настройки LiveRaid режима «") + Title(target) + "»", 0);
                    Copy(ModeCfg(target, channel), LiveCfg, log, undoable: true);
                }
                else
                {
                    log(L.Tr("у режима «") + Title(target) + L.Tr("» ещё нет своих настроек - остаются текущие"), 0);
                }

                File.AppendAllText(Path.Combine(Home, "launcher.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} switched to {target}{Environment.NewLine}");
            }
            catch (Exception e)
            {
                log(L.Tr("ошибка: ") + e.Message, 2);
                if (e is UnauthorizedAccessException || e is IOException)
                {
                    log(L.Tr("похоже, файл занят другой программой (игра, сервер, редактор, антивирус) - закройте её и переключите ещё раз"), 1);
                }
                if (_undo.Count > 0)
                {
                    log(L.Tr("откатываю ") + _undo.Count + L.Tr(" шаг(ов)..."), 1);
                    for (int i = _undo.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            _undo[i]();
                        }
                        catch (Exception u)
                        {
                            log(L.Tr("не удалось откатить шаг: ") + u.Message, 2);
                        }
                    }

                    log(L.Tr("всё вернулось как было"), 1);
                }

                throw;
            }
        }

        /// <summary>
        /// Stable &lt;-&gt; test branch in the mode the game is in: the client DLL of that mode and the server's DLL and data of the branch come in,
        /// each branch's F12 settings are kept apart. A stable server in the game is put aside first (editions\Server) - it is never lost.
        /// </summary>
        public void SwitchChannel(Channel target, Action<string, int> log)
        {
            List<string> blockers = Blockers();
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException(L.Tr("сначала закройте: ") + string.Join(", ", blockers.Select(L.Tr)));
            }

            State s = Read();
            Mode mode = s.Edition == "Online" ? Mode.Online : s.Edition == "Single" ? Mode.Single : s.FikaLive ? Mode.Online : Mode.Single;
            string client = EditionDll(mode, target);
            string server = Path.Combine(ServerKept(target), "LiveRaid.Server.dll");
            if (!File.Exists(client))
            {
                throw new InvalidOperationException(L.Tr("нет ") + Rel(client) + " - " + (target == Channel.Test
                    ? L.Tr("распакуйте архив тестовой ветки (LiveRaid-Test) в папку игры")
                    : L.Tr("распакуйте архив стабильной версии LiveRaid в папку игры")));
            }

            bool serverInGame = s.ServerVersion != "" && s.ServerChannel == target;
            if (!File.Exists(server) && !serverInGame)
            {
                throw new InvalidOperationException(L.Tr("нет сервера ветки «") + Title(target) + "» (" + Rel(server) + ")");
            }

            _undo.Clear();
            try
            {
                log(L.Tr("сохраняю настройки LiveRaid ветки «") + Title(s.Channel) + L.Tr("», режим «") + Title(mode) + "»", 0);
                Copy(LiveCfg, ModeCfg(mode, s.Channel), log, undoable: false);

                if (s.ServerVersion != "" && s.ServerChannel == Channel.Stable && !serverInGame)
                {
                    log(L.Tr("откладываю стабильный сервер ") + s.ServerVersion + L.Tr(" в ") + Rel(ServerKept(Channel.Stable)), 0);
                    CopyServer(LiveServerDir, ServerKept(Channel.Stable));
                }

                log(L.Tr("ставлю клиент ") + (mode == Mode.Online ? "LiveRaid Online " : "LiveRaid ") + Version(client), 0);
                Copy(client, LiveDll, log, undoable: true);
                if (!serverInGame)
                {
                    log(L.Tr("ставлю сервер LiveRaid ") + Version(server), 0);
                    CopyServer(ServerKept(target), LiveServerDir);
                }

                if (File.Exists(ModeCfg(mode, target)))
                {
                    log(L.Tr("возвращаю настройки LiveRaid ветки «") + Title(target) + "»", 0);
                    Copy(ModeCfg(mode, target), LiveCfg, log, undoable: true);
                }
                else
                {
                    log(L.Tr("у ветки «") + Title(target) + L.Tr("» ещё нет своих настроек - остаются текущие"), 0);
                }

                File.AppendAllText(Path.Combine(Home, "launcher.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} switched to the {target} branch ({mode}){Environment.NewLine}");
            }
            catch (Exception e)
            {
                log(L.Tr("ошибка: ") + e.Message, 2);
                if (e is UnauthorizedAccessException || e is IOException)
                {
                    log(L.Tr("похоже, файл занят другой программой (игра, сервер, редактор, антивирус) - закройте её и переключите ещё раз"), 1);
                }

                if (_undo.Count > 0)
                {
                    log(L.Tr("откатываю ") + _undo.Count + L.Tr(" шаг(ов)..."), 1);
                    for (int i = _undo.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            _undo[i]();
                        }
                        catch (Exception u)
                        {
                            log(L.Tr("не удалось откатить шаг: ") + u.Message, 2);
                        }
                    }

                    log(L.Tr("всё вернулось как было"), 1);
                }

                throw;
            }
        }

        /// <summary>The server's DLL and its data\*.json (its settings for the group, host.token and the like stay where they are).</summary>
        private void CopyServer(string from, string to)
        {
            Copy(Path.Combine(from, "LiveRaid.Server.dll"), Path.Combine(to, "LiveRaid.Server.dll"), null, undoable: true);
            string data = Path.Combine(from, "data");
            if (Directory.Exists(data))
            {
                foreach (string f in Directory.GetFiles(data, "*.json"))
                {
                    Copy(f, Path.Combine(to, "data", Path.GetFileName(f)), null, undoable: true);
                }
            }
        }

        public static string Title(Mode m) => m == Mode.Online ? L.Tr("Онлайн") : L.Tr("Одиночный");

        public static string Title(Channel c) => c == Channel.Test ? L.Tr("Тестовая") : L.Tr("Стабильная");

        private string Rel(string path) => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? path.Substring(Root.Length).TrimStart('\\') : path;

        /// <summary>Moves a file or folder; something already at the target is moved aside into fika-old (never deleted).</summary>
        private void Park(string from, string to, Action<string, int> log)
        {
            if (Exists(to))
            {
                string aside = Path.Combine(Home, "fika-old", DateTime.Now.ToString("yyyyMMdd-HHmmss"), Rel(to).Replace(HomeName + @"\fika-off\", ""));
                log(L.Tr("  уже есть ") + Rel(to) + L.Tr(" - старая копия убрана в ") + Rel(aside), 1);
                MoveOne(to, aside);
            }

            log("  " + Rel(from) + "  ->  " + Rel(to), 0);
            MoveOne(from, to);
        }

        private void MoveOne(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }

            _undo.Add(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(from));
                if (Directory.Exists(to))
                {
                    Directory.Move(to, from);
                }
                else
                {
                    File.Move(to, from);
                }
            });
        }

        private void Copy(string from, string to, Action<string, int> log, bool undoable)
        {
            if (!File.Exists(from))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (undoable)
            {
                byte[] old = File.Exists(to) ? File.ReadAllBytes(to) : null;
                _undo.Add(() =>
                {
                    if (old != null)
                    {
                        File.WriteAllBytes(to, old);
                    }
                    else if (File.Exists(to))
                    {
                        File.Delete(to);
                    }
                });
            }

            File.Copy(from, to, true);
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    }
}
