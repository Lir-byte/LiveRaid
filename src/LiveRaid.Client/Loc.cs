using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace LiveRaid.Client
{
    public enum LangChoice
    {
        Auto,
        English,
        Russian,
    }

    /// <summary>
    /// The language of what the player reads (F12 menu, free camera hints, launcher texts are separate): English or Russian. "Auto" follows the game's own
    /// interface language (EFT's LocalizationManager culture, "ru" = Russian; the system language while the game has not loaded its settings yet),
    /// every other game language gets English. The setting "Language" in F12 forces one of them.
    /// The code keeps the Russian text where it was written and an English one beside it: <see cref="T"/> picks, <see cref="Tr"/> translates the
    /// small fixed phrases of the menu from the table below, the settings' own English names and descriptions are in <see cref="LocEn"/>.
    /// The names and descriptions already shown are rewritten when the language changes (<see cref="Poll"/>, once a second), so a game that
    /// reads its language late still shows the right one when F12 opens.
    /// </summary>
    internal static class Loc
    {
        private sealed class Text
        {
            public ConfigEntryBase Entry;
            public string RuName, RuDesc, EnName, EnDesc;
            public bool HasEn;
        }

        private static readonly Dictionary<ConfigEntryBase, Text> Texts = new Dictionary<ConfigEntryBase, Text>();
        private static readonly FieldInfo DescriptionField = typeof(ConfigDescription).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool _ru;
        private static bool _known;
        private static float _next;

        /// <summary>The text is Russian now.</summary>
        public static bool Ru
        {
            get
            {
                if (!_known)
                {
                    _ru = Compute();
                    _known = true;
                }

                return _ru;
            }
        }

        /// <summary>The Russian text or the English one.</summary>
        public static string T(string ru, string en) => Ru ? ru : en;

        /// <summary>A fixed phrase of the menu: Russian as written, English from the table (the phrase itself when it is not in it).</summary>
        public static string Tr(string ru)
        {
            if (Ru || string.IsNullOrEmpty(ru))
            {
                return ru;
            }

            return Phrases.TryGetValue(ru, out string en) ? en : ru;
        }

        private static bool Compute()
        {
            LangChoice choice = Settings.Language != null ? Settings.Language.Value : LangChoice.Auto;
            if (choice == LangChoice.Russian)
            {
                return true;
            }

            if (choice == LangChoice.English)
            {
                return false;
            }

            try
            {
                string culture = EFT.LocalizationManager.Instance?.Culture;
                if (!string.IsNullOrEmpty(culture))
                {
                    return culture.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // the game's localisation is not up yet
            }

            try
            {
                return Application.systemLanguage == SystemLanguage.Russian;
            }
            catch
            {
                return false; // outside the game (the build check)
            }
        }

        /// <summary>Once a second: has the language changed (the game loaded its settings, the player chose one)? Then every shown text is rewritten.</summary>
        public static void Poll()
        {
            if (Time.unscaledTime < _next)
            {
                return;
            }

            _next = Time.unscaledTime + 1f;
            bool now = Compute();
            if (_known && now == _ru)
            {
                return;
            }

            _ru = now;
            _known = true;
            Relabel();
        }

        /// <summary>Forget the cached language (the setting changed) and rewrite the texts.</summary>
        public static void Changed()
        {
            _next = 0f;
            _known = false;
            _ru = Compute();
            _known = true;
            Relabel();
        }

        /// <summary>The Bind call of a setting: its Russian texts and the English ones from <see cref="LocEn"/> are kept to switch between.</summary>
        public static void Track(ConfigEntryBase entry, string section, string key, string ruName, string ruDesc)
        {
            Text t = new Text { Entry = entry, RuName = ruName, RuDesc = ruDesc, EnName = key, EnDesc = ruDesc };
            if (LocEn.Entries.TryGetValue(section + "|" + key, out (string name, string desc) en))
            {
                t.EnName = en.name;
                t.EnDesc = en.desc;
                t.HasEn = true;
            }

            Texts[entry] = t;
        }

        /// <summary>Keys of the settings that were bound without an English text (SelfTest and the build check use it).</summary>
        public static IEnumerable<string> MissingEnglish()
        {
            foreach (Text t in Texts.Values)
            {
                if (!t.HasEn)
                {
                    yield return t.RuName;
                }
            }
        }

        /// <summary>Number of settings bound and, for the build check, how the two languages read for one of them.</summary>
        public static string Sample(ConfigEntryBase entry, bool russian) =>
            Texts.TryGetValue(entry, out Text t) ? (russian ? t.RuName + " | " + t.RuDesc : t.EnName + " | " + t.EnDesc) : null;

        public static int Count => Texts.Count;

        public static string NameOf(ConfigEntryBase entry) => Texts.TryGetValue(entry, out Text t) ? (Ru ? t.RuName : t.EnName) : null;

        public static string DescriptionOf(ConfigEntryBase entry) => Texts.TryGetValue(entry, out Text t) ? (Ru ? t.RuDesc : t.EnDesc) : null;

        private static void Relabel()
        {
            foreach (Text t in Texts.Values)
            {
                SetDescription(t.Entry, Ru ? t.RuDesc : t.EnDesc);
            }

            SettingsMenu.Relabel();
        }

        private static void SetDescription(ConfigEntryBase entry, string text)
        {
            try
            {
                if (DescriptionField != null && entry?.Description != null)
                {
                    DescriptionField.SetValue(entry.Description, text);
                }
            }
            catch
            {
                // the description stays as it was
            }
        }

        /// <summary>Fixed phrases of the menu: Russian (as written in the code) -> English.</summary>
        private static readonly Dictionary<string, string> Phrases = new Dictionary<string, string>
        {
            // groups of F12
            ["1. Основное"] = "1. Main",
            ["2. Ключи и двери"] = "2. Keys and doors",
            ["3. Жизнь на карте"] = "3. Life on the map",
            ["4. Характеры и эвакуация"] = "4. Characters and extraction",
            ["5. Спавн и лобби"] = "5. Spawns and lobby",
            ["6. Лут"] = "6. Loot",
            ["7. Отряды"] = "7. Squads",
            ["8. Бой"] = "8. Combat",
            ["9. Лечение, оружие и патроны"] = "9. Healing, weapons and ammo",
            ["10. Квесты и маршруты"] = "10. Quests and routes",
            ["11. Производительность"] = "11. Performance",
            ["12. После смерти"] = "12. After death",
            // controls
            ["Вкл."] = "On",
            ["Выкл."] = "Off",
            ["выкл."] = "off",
            ["Выборочно"] = "Custom",
            ["Рекомендуемый"] = "Recommended",
            ["Слабый ПК"] = "Weak PC",
            ["Тихие рейды"] = "Quiet raids",
            ["Людные рейды"] = "Busy raids",
            ["Только исправления"] = "Fixes only",
            ["Сброс"] = "Reset",
            // levels
            ["Обычный"] = "Usual",
            ["Подробный"] = "Detailed",
            ["Полный"] = "Full",
            ["Мало"] = "Few",
            ["Как онлайн"] = "Like online",
            ["Много"] = "Many",
            ["Нет"] = "None",
            ["Редко"] = "Rarely",
            ["Обычно"] = "Usual",
            ["Часто"] = "Often",
            ["Никогда"] = "Never",
            ["Быстро"] = "Fast",
            ["Долго"] = "Long",
            // Online lock
            ["Настройки контролируются только хостом"] = "Settings are controlled by the host only",
            ["Сервер не отдал общие настройки (на нём нет LiveRaid 0.9+) - каждый играет со своими"] = "The server gave no shared settings (it has no LiveRaid 0.9+) - everyone plays with their own",
            ["Ваши значения получают все игроки группы при каждом изменении и в начале каждого рейда"] = "Your values go to every player of the group at each change and at the start of every raid",
            ["Вы - хост: эти настройки действуют у всех игроков"] = "You are the host: these settings apply to every player",
            ["НАСТРОЙКИ КОНТРОЛИРУЮТСЯ ТОЛЬКО ХОСТОМ"] = "SETTINGS ARE CONTROLLED BY THE HOST ONLY",
            ["Хост ещё не сохранил свои настройки - пока действуют ваши"] = "The host has not saved its settings yet - yours apply for now",
            ["Сервер на другом ПК, а хотите менять настройки? Вставьте текст файла host.token из папки сервера (user/mods/LiveRaid) в поле Host key в BepInEx/config/com.lir.liveraid.cfg (секция 0. Online) и перезапустите игру"] =
                "The server is on another machine and you want to change the settings? Put the text of the host.token file from the server folder (user/mods/LiveRaid) into the Host key field in BepInEx/config/com.lir.liveraid.cfg (section 0. Online) and restart the game",
            ["Ключ в поле Host key не подходит к этому серверу - возьмите заново текст файла host.token из папки сервера (user/mods/LiveRaid)"] =
                "The key in the Host key field does not fit this server - take the text of the host.token file from the server folder (user/mods/LiveRaid) again",
        };
    }
}
