using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;

namespace LiveRaid.Client
{
    public enum PresetChoice
    {
        Custom,
        Recommended,
        LightPc,
        Quiet,
        Busy,
        FixesOnly,
    }

    /// <summary>
    /// Ready-made sets of settings (F12, the top of the compact menu), so that nobody has to read two hundred settings to start:
    ///  - Recommended: every pack on, every population and performance setting at the author's value;
    ///  - Weak PC: far bots simplified and put to sleep, no AI-versus-AI at a distance, a smaller lobby, no detailed logs;
    ///  - Quiet raids: fewer bots and PMCs, a lower crowd cap;
    ///  - Busy raids: more bots and PMCs, a bigger lobby, more reserves;
    ///  - Fixes only: all of LiveRaid's behaviour off, only the repairs of EFT's own bugs and healing stay.
    /// Each preset is a list of (setting, value); the chosen preset shows only while the settings still match it, otherwise "Custom".
    /// In LiveRaid Online only the host can choose (the others' shared settings are locked).
    /// </summary>
    internal static class Presets
    {
        private static readonly List<(PresetChoice preset, List<(ConfigEntryBase entry, object value)> rows)> Table =
            new List<(PresetChoice, List<(ConfigEntryBase, object)>)>();

        private static bool _busy;

        public static void Setup(ConfigFile cfg, ConfigEntry<SettingsMenu.PackState>[] packs, ConfigEntry<SettingsMenu.PackState> heal, ConfigEntry<SettingsMenu.PackState> fixes)
        {
            List<(ConfigEntryBase, object)> recommended = packs.Select(p => ((ConfigEntryBase)p, (object)SettingsMenu.PackState.On)).ToList();
            foreach (ConfigEntryBase e in Governed())
            {
                recommended.Add((e, e.DefaultValue));
            }

            Table.Add((PresetChoice.FixesOnly, packs.Select(p => ((ConfigEntryBase)p, (object)(p == heal || p == fixes ? SettingsMenu.PackState.On : SettingsMenu.PackState.Off))).ToList()));
            Table.Add((PresetChoice.LightPc, new List<(ConfigEntryBase, object)>
            {
                (Settings.SimplifyDistantBots, true), (Settings.QbSleeping, true), (Settings.FarBotsFullSenses, false),
                (Settings.PopulationDensity, 75), (Settings.PopulationStrength, 100), (Settings.CrowdCap, 6), (Settings.BiggerLobby, false),
                (Settings.CoverageLog, false), (Settings.EncounterLog, false), (Settings.LogNetwork, false),
            }));
            Table.Add((PresetChoice.Quiet, new List<(ConfigEntryBase, object)>
            {
                (Settings.PopulationDensity, 70), (Settings.PopulationStrength, 100), (Settings.BiggerLobby, false), (Settings.PmcReserve, 1), (Settings.CrowdCap, 5),
            }));
            Table.Add((PresetChoice.Busy, new List<(ConfigEntryBase, object)>
            {
                (Settings.PopulationDensity, 130), (Settings.BiggerLobby, true), (Settings.BiggerLobbyCount, 7), (Settings.PmcReserve, 5), (Settings.CrowdCap, 10),
            }));
            Table.Add((PresetChoice.Recommended, recommended));

            Settings.Preset.SettingChanged += (s, e) => OnChosen();
            cfg.SettingChanged += (s, e) =>
            {
                if (!_busy)
                {
                    Derive();
                }
            };
            Derive();
        }

        private static IEnumerable<ConfigEntryBase> Governed() => new ConfigEntryBase[]
        {
            Settings.PopulationBalance, Settings.PopulationDensity, Settings.PopulationStrength, Settings.CrowdCap, Settings.BiggerLobby, Settings.BiggerLobbyCount,
            Settings.PmcReserve, Settings.LivelyMap, Settings.PScavReplace, Settings.SimplifyDistantBots, Settings.QbSleeping, Settings.FarBotsFullSenses,
            Settings.CoverageLog, Settings.EncounterLog, Settings.LogNetwork,
        };

        private static void OnChosen()
        {
            if (_busy)
            {
                return;
            }

            PresetChoice choice = Settings.Preset.Value;
            if (choice == PresetChoice.Custom)
            {
                Settings.DevMode.Value = true;
                Derive();
                return;
            }

            if (SettingsMenu.Locked)
            {
                Derive();
                return;
            }

            _busy = true;
            try
            {
                foreach ((PresetChoice preset, List<(ConfigEntryBase entry, object value)> rows) in Table)
                {
                    if (preset != choice)
                    {
                        continue;
                    }

                    foreach ((ConfigEntryBase entry, object value) in rows)
                    {
                        object v = entry.SettingType.IsEnum ? value : Convert.ChangeType(value, entry.SettingType, CultureInfo.InvariantCulture);
                        if (!Equals(entry.BoxedValue, v))
                        {
                            entry.BoxedValue = v;
                        }
                    }
                }
            }
            finally
            {
                _busy = false;
            }

            Derive();
            Log.Info($"LR Presets: the \"{choice}\" preset applied");
        }

        /// <summary>The preset whose every value is in place now, else Custom.</summary>
        private static void Derive()
        {
            PresetChoice found = PresetChoice.Custom;
            foreach ((PresetChoice preset, List<(ConfigEntryBase entry, object value)> rows) in Table)
            {
                if (rows.All(r => Same(r.entry.BoxedValue, r.value)))
                {
                    found = preset;
                    break;
                }
            }

            if (Settings.Preset.Value != found)
            {
                _busy = true;
                try
                {
                    Settings.Preset.Value = found;
                }
                finally
                {
                    _busy = false;
                }
            }
        }

        private static bool Same(object a, object b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }

            if (a.GetType().IsEnum)
            {
                return a.Equals(b);
            }

            return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(Convert.ChangeType(b, a.GetType(), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
    }
}
