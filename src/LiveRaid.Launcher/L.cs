using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LiveRaid.Launcher
{
    /// <summary>
    /// The language of the launcher: Russian or English. Windows' interface language decides at the first start (Russian - Russian, everything else - English);
    /// the "RU / EN" button switches and the choice is kept in "LiveRaid Launcher\language.txt". The code keeps the Russian text where it was written
    /// and <see cref="Tr"/> turns each Russian piece into English from the table; the fixed texts of the window are translated by <see cref="Localize"/>.
    /// </summary>
    internal static class L
    {
        public static bool Ru { get; private set; } = Detect();

        private static readonly Dictionary<object, string> Originals = new Dictionary<object, string>();

        private static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiveRaid Launcher", "language.txt");

        private static bool Detect()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return File.ReadAllText(FilePath).Trim().StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // no file - the system language
            }

            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        }

        public static void Set(bool ru)
        {
            Ru = ru;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, ru ? "ru" : "en");
            }
            catch
            {
                // the choice is just not kept
            }
        }

        public static string Tr(string ru)
        {
            if (Ru || string.IsNullOrEmpty(ru))
            {
                return ru;
            }

            return En.TryGetValue(ru, out string en) ? en : ru;
        }

        /// <summary>Translate the fixed texts of the window (titles, captions, buttons, tooltips, bullets); the original Russian is kept to go back.</summary>
        public static void Localize(DependencyObject root)
        {
            if (root is Window w)
            {
                w.Title = Pick(w, w.Title);
            }

            if (root is TextBlock t && string.IsNullOrEmpty(t.Name))
            {
                t.Text = Pick(t, t.Text);
            }

            if (root is Button b)
            {
                if (b.Content is string s)
                {
                    b.Content = Pick(b, s);
                }

                if (b.ToolTip is string tip)
                {
                    b.ToolTip = Pick(Tuple.Create(b, "tip"), tip);
                }
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject d)
                {
                    Localize(d);
                }
            }
        }

        private static string Pick(object key, string current)
        {
            if (!Originals.TryGetValue(key, out string original))
            {
                original = current;
                Originals[key] = original;
            }

            return Tr(original);
        }

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            // window (XAML)
            ["LiveRaid — пульт режимов"] = "LiveRaid — mode switch",
            ["ПУЛЬТ РЕЖИМОВ"] = "MODE SWITCH",
            ["ИГРА"] = "GAME",
            ["СЕРВЕР SPT"] = "SPT SERVER",
            ["LIVERAID В ИГРЕ"] = "LIVERAID IN THE GAME",
            ["Свернуть"] = "Minimize",
            ["Закрыть"] = "Close",
            ["РЕЖИМ 01"] = "MODE 01",
            ["ОДИНОЧНЫЙ"] = "SINGLE PLAYER",
            ["LiveRaid для SPT без Fika"] = "LiveRaid for SPT without Fika",
            ["· вы один против всей карты ботов"] = "· you alone against a whole map of bots",
            ["· свободная камера после смерти, режим наблюдателя, вселение в бота"] = "· a free camera after death, observer mode, taking over a bot",
            ["· вся Fika с настройками убрана в «LiveRaid Launcher\\fika-off» и ждёт"] = "· all of Fika with its settings is put away in \"LiveRaid Launcher\\fika-off\" and waits",
            ["НАЖМИТЕ, ЧТОБЫ ПЕРЕКЛЮЧИТЬСЯ"] = "CLICK TO SWITCH",
            ["АКТИВЕН"] = "ACTIVE",
            ["СОЛО"] = "SOLO",
            ["ПЕРЕВЕДИТЕ ТУМБЛЕР"] = "FLIP THE SWITCH",
            ["РЕЖИМ 02"] = "MODE 02",
            ["ОНЛАЙН"] = "ONLINE",
            ["· кооп с друзьями: боты живут у хоста"] = "· co-op with friends: the bots live on the host",
            ["· настройки LiveRaid задаёт хост - у остальных F12 заблокирован"] = "· the host sets LiveRaid's settings - F12 is locked for the others",
            ["· Fika возвращается на место со всеми своими настройками и данными"] = "· Fika comes back with all its settings and data",
            ["ВЕТКА МОДА"] = "MOD BRANCH",
            ["СТАБИЛЬНАЯ"] = "STABLE",
            ["ТЕСТОВАЯ"] = "TEST",
            ["ЖУРНАЛ ОПЕРАЦИЙ"] = "OPERATION LOG",
            ["ЗАПУСТИТЬ СЕРВЕР SPT"] = "START SPT SERVER",
            ["ЛАУНЧЕР SPT"] = "SPT LAUNCHER",
            ["ПАПКА ИГРЫ"] = "GAME FOLDER",
            ["ВЫБРАТЬ ИГРУ…"] = "PICK THE GAME…",
            // code
            ["  уже есть "] = "  already there: ",
            [" - мод выключится; переключите режим, чтобы всё сошлось"] = " - the mod will switch itself off; switch the mode to make it agree",
            [" - распакуйте архив LiveRaid 0.9+ в папку игры целиком"] = " - unpack the whole LiveRaid 0.9+ archive into the game folder",
            [" - распакуйте архив LiveRaid целиком (в нём обе редакции)"] = " - unpack the whole LiveRaid archive (it has both editions)",
            [" - старая копия убрана в "] = " - the old copy is put aside in ",
            [" в "] = " in ",
            [" в игре"] = " in the game",
            [" отложена"] = " put aside",
            [" папок и файлов"] = " folders and files",
            [" при "] = " with ",
            [" шаг(ов)..."] = " step(s)...",
            [") - переключите ветку ещё раз, чтобы всё сошлось"] = ") - switch the branch once more to make it agree",
            [", ветка «"] = ", branch «",
            ["Fika в игре не найдена - убирать нечего"] = "No Fika in the game - nothing to put away",
            ["Fika не найдена ни в игре, ни в отложенных - установите её через Fika-Installer и переключите ещё раз"] = "Fika is neither in the game nor put aside - install it with Fika-Installer and switch again",
            ["Fika нет"] = "no Fika",
            ["LiveRaid (одиночный)"] = "LiveRaid (single player)",
            ["LiveRaid не установлен"] = "LiveRaid is not installed",
            ["» ещё нет своих настроек - остаются текущие"] = "» has no settings of its own yet - the current ones stay",
            ["» уже в игре"] = "» is already in the game",
            ["» уже включён"] = "» is already on",
            ["», режим «"] = "», mode «",
            ["». Можно запускать сервер и игру"] = "». You can start the server and the game",
            ["Где EscapeFromTarkov.exe вашей SPT?"] = "Where is EscapeFromTarkov.exe of your SPT?",
            ["ЗАКРОЙТЕ: "] = "CLOSE: ",
            ["ИДЁТ ПЕРЕКЛЮЧЕНИЕ…"] = "SWITCHING…",
            ["НЕСОВПАДЕНИЕ: "] = "MISMATCH: ",
            ["Одиночный"] = "Single player",
            ["Одиночный "] = "Single player ",
            ["Онлайн"] = "Online",
            ["ПУЛЬТ РЕЖИМОВ  ·  "] = "MODE SWITCH  ·  ",
            ["РЕЖИМ НЕ СОШЁЛСЯ - ПЕРЕВЕДИТЕ"] = "THE MODE DOES NOT AGREE - SWITCH",
            ["Стабильная"] = "Stable",
            ["Тестовая"] = "Test",
            ["в «LiveRaid Launcher\\editions» нет "] = "\"LiveRaid Launcher\\editions\" lacks ",
            ["в игре "] = "in the game ",
            ["в игре стабильная версия. Тестовая ветка установлена и ставится кнопкой «ТЕСТОВАЯ» (стабильная при этом сохраняется)"] = "the stable version is in the game. The test branch is installed and goes in with the \"TEST\" button (the stable one is kept)",
            ["в игре стабильная версия. Тестовой ветки нет - распакуйте архив LiveRaid-Test в папку игры"] = "the stable version is in the game. There is no test branch - unpack the LiveRaid-Test archive into the game folder",
            ["в игре тестовая ветка (новые поведения ботов). Стабильная версия отложена и возвращается кнопкой «СТАБИЛЬНАЯ»; у каждой ветки свои настройки F12"] = "the test branch is in the game (new bot behaviours). The stable version is put aside and comes back with the \"STABLE\" button; each branch has its own F12 settings",
            ["в этой папке нет BepInEx - это не SPT"] = "there is no BepInEx in this folder - it is not SPT",
            ["ветка «"] = "branch «",
            ["ветка не переключена"] = "the branch was not switched",
            ["внимание: режим не сошёлся - LiveRaid в игре выключится; сначала переключите режим"] = "warning: the mode does not agree - LiveRaid will be off in the game; switch the mode first",
            ["возвращаю Fika на место: "] = "putting Fika back: ",
            ["возвращаю настройки LiveRaid ветки «"] = "restoring LiveRaid's settings of the branch «",
            ["возвращаю настройки LiveRaid режима «"] = "restoring LiveRaid's settings of the mode «",
            ["всё вернулось как было"] = "everything is back as it was",
            ["готово: ветка «"] = "done: branch «",
            ["готово: режим «"] = "done: mode «",
            ["закрыта"] = "closed",
            ["запущен "] = "started ",
            ["запущена"] = "running",
            ["игра"] = "game",
            ["игра (EscapeFromTarkov)"] = "game (EscapeFromTarkov)",
            ["игра не найдена рядом с лаунчером - нажмите «ВЫБРАТЬ ИГРУ…» и укажите EscapeFromTarkov.exe"] = "the game is not next to the launcher - press \"PICK THE GAME…\" and point to EscapeFromTarkov.exe",
            ["игра: "] = "game: ",
            ["клиент LiveRaid и сервер из разных веток (сервер "] = "the LiveRaid client and the server are of different branches (server ",
            ["лаунчер SPT"] = "SPT launcher",
            ["не найден "] = "not found: ",
            ["не удалось запустить "] = "could not start ",
            ["не удалось откатить шаг: "] = "could not undo a step: ",
            ["не удалось прочитать папку игры: "] = "could not read the game folder: ",
            ["нет"] = "no",
            ["нет "] = "no ",
            ["нет сервера ветки «"] = "no server of the branch «",
            ["нет файла редакции "] = "no edition file ",
            ["обеих редакций"] = "both editions",
            ["одиночной редакции"] = "the single player edition",
            ["одиночный режим ("] = "single player mode (",
            ["онлайн-режим ("] = "online mode (",
            ["остановлен"] = "stopped",
            ["откатываю "] = "undoing ",
            ["откладываю стабильный сервер "] = "putting the stable server aside: ",
            ["отложена "] = "put aside ",
            ["отложенной Fika нет"] = "no Fika put aside",
            ["ошибка: "] = "error: ",
            ["похоже, файл занят другой программой (игра, сервер, редактор, антивирус) - закройте её и переключите ещё раз"] = "looks like a file is held by another program (the game, the server, an editor, an antivirus) - close it and switch again",
            ["работает"] = "running",
            ["распакуйте архив стабильной версии LiveRaid в папку игры"] = "unpack the stable LiveRaid archive into the game folder",
            ["распакуйте архив тестовой ветки (LiveRaid-Test) в папку игры"] = "unpack the test branch archive (LiveRaid-Test) into the game folder",
            ["редакции Online"] = "the Online edition",
            ["режим «"] = "mode «",
            ["режим не переключён"] = "the mode was not switched",
            ["сейчас: "] = "now: ",
            ["сервер"] = "server",
            ["сервер SPT"] = "SPT server",
            ["сначала закройте: "] = "close first: ",
            ["сначала укажите папку игры («ВЫБРАТЬ ИГРУ…»)"] = "first point to the game folder (\"PICK THE GAME…\")",
            ["сохраняю настройки LiveRaid ветки «"] = "saving LiveRaid's settings of the branch «",
            ["сохраняю настройки LiveRaid режима «"] = "saving LiveRaid's settings of the mode «",
            ["ставлю "] = "installing ",
            ["ставлю клиент "] = "installing the client ",
            ["ставлю сервер LiveRaid "] = "installing the LiveRaid server ",
            ["у ветки «"] = "the branch «",
            ["у режима «"] = "the mode «",
            ["убираю Fika: "] = "putting Fika away: ",
            ["установщик Fika"] = "Fika installer",
            ["── ветка: «"] = "── branch: «",
            ["── переключение: «"] = "── switching: «",
            ["▸ НАЖМИТЕ, ЧТОБЫ ПЕРЕКЛЮЧИТЬСЯ"] = "▸ CLICK TO SWITCH",
            ["● НЕ СОШЛОСЬ: Fika и редакция LiveRaid разные"] = "● MISMATCH: Fika and the LiveRaid edition differ",
            ["● СЕЙЧАС В ИГРЕ"] = "● IN THE GAME NOW",
            ["Режим «"] = "Mode «",
        };

        /// <summary>The build check: every Russian piece wrapped in L.Tr(...) has its English; returns the ones that have not.</summary>
        public static IEnumerable<string> Missing(IEnumerable<string> russian)
        {
            foreach (string r in russian)
            {
                if (!En.ContainsKey(r))
                {
                    yield return r;
                }
            }
        }
    }
}
