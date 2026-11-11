using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace LiveRaid.Client
{
    /// <summary>
    /// Read by ConfigurationManager (it looks for a tag object of this name and copies its public fields). The fields are changed later on
    /// (developer mode, the host lock) and ConfigurationManager picks them up when its list is rebuilt.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public string Category;
        public string DispName;
        public int? Order;
        public bool? ReadOnly;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public Action<ConfigEntryBase> CustomDrawer;
    }

    /// <summary>
    /// How LiveRaid's F12 looks: Russian names, groups and order; the compact menu (packs that switch a whole group of features and levels that set
    /// several values together) with every single setting only in developer mode; in LiveRaid Online the lock of a player who is not the host
    /// (greyed controls, a veil over them and a banner). The values themselves stay in the usual config entries the rest of LiveRaid reads.
    /// </summary>
    internal static class SettingsMenu
    {
        public enum PackState
        {
            [Description("Вкл.")] On,
            [Description("Выборочно")] Custom,
            [Description("Выкл.")] Off,
        }

        public enum Level
        {
            Off,
            Low,
            Normal,
            High,
            Custom,
        }

        private sealed class Item
        {
            public ConfigEntryBase Entry;
            public ConfigurationManagerAttributes Attr;
            public string Name;
            public string Category;
            public bool Simple;
            public bool Synced;
            public string[] Labels;
            public Combo Owner;
            public Combo Combo;
            public int Index;
        }

        /// <summary>A menu entry that stands for several settings: its value is read from them and, when the player changes it, written into them.</summary>
        private abstract class Combo
        {
            public Item Item;
            public readonly List<Item> Children = new List<Item>();
            public abstract object Derive();
            public abstract void Apply(object value);
            public virtual string[] Labels => null;
            public virtual object[] Values => null;
        }

        private sealed class PackCombo : Combo
        {
            // UseLearnedShortcuts is the one switch no pack and no preset touches: it is on everywhere ("Fixes only" included), see Presets
            private IEnumerable<Item> Switches => Children.Where(c => c.Synced && c.Entry.SettingType == typeof(bool) && c.Entry != Settings.UseLearnedShortcuts);

            public override object Derive()
            {
                List<Item> s = Switches.ToList();
                if (s.All(c => (bool)c.Entry.BoxedValue == (bool)c.Entry.DefaultValue))
                {
                    return PackState.On;
                }

                return s.All(c => !(bool)c.Entry.BoxedValue) ? PackState.Off : PackState.Custom;
            }

            public override void Apply(object value)
            {
                PackState v = (PackState)value;
                if (v == PackState.Custom)
                {
                    // "Pick one by one" means: show me the single settings
                    Settings.DevMode.Value = true;
                    return;
                }

                foreach (Item c in Switches)
                {
                    c.Entry.BoxedValue = v == PackState.On ? c.Entry.DefaultValue : (object)false;
                }
            }

            public override string[] Labels => new[] { Loc.Tr("Вкл."), Loc.Tr("Выборочно"), Loc.Tr("Выкл.") };
            public override object[] Values => new object[] { PackState.On, PackState.Custom, PackState.Off };
        }

        private sealed class LevelCombo : Combo
        {
            public (Level level, string label, object[] values)[] Rows;

            public override object Derive()
            {
                foreach (var row in Rows)
                {
                    bool match = true;
                    for (int i = 0; i < Children.Count && match; i++)
                    {
                        match = row.values[i] == null || Same(Children[i].Entry.BoxedValue, row.values[i]);
                    }

                    if (match)
                    {
                        return row.level;
                    }
                }

                return Level.Custom;
            }

            public override void Apply(object value)
            {
                Level v = (Level)value;
                if (v == Level.Custom)
                {
                    Settings.DevMode.Value = true;
                    return;
                }

                foreach (var row in Rows.Where(r => r.level == v))
                {
                    for (int i = 0; i < Children.Count; i++)
                    {
                        if (row.values[i] != null)
                        {
                            Children[i].Entry.BoxedValue = Convert.ChangeType(row.values[i], Children[i].Entry.SettingType, CultureInfo.InvariantCulture);
                        }
                    }
                }
            }

            public override string[] Labels => Rows.Select(r => Loc.Tr(r.label)).Concat(new[] { Loc.Tr("Выборочно") }).ToArray();
            public override object[] Values => Rows.Select(r => (object)r.level).Concat(new object[] { Level.Custom }).ToArray();
        }

        /// <summary>A switch and its amount as one number: 0 = off.</summary>
        private sealed class AmountCombo : Combo
        {
            public int Min;

            public override object Derive()
            {
                if (!(bool)Children[0].Entry.BoxedValue)
                {
                    return 0;
                }

                return Mathf.RoundToInt(Convert.ToSingle(Children[1].Entry.BoxedValue, CultureInfo.InvariantCulture));
            }

            public override void Apply(object value)
            {
                int n = (int)value;
                Children[0].Entry.BoxedValue = n > 0;
                if (n > 0)
                {
                    Children[1].Entry.BoxedValue = Convert.ChangeType(Mathf.Max(Min, n), Children[1].Entry.SettingType, CultureInfo.InvariantCulture);
                }
            }
        }

        private static readonly List<Item> Items = new List<Item>();
        private static readonly Dictionary<ConfigEntryBase, Item> ByEntry = new Dictionary<ConfigEntryBase, Item>();
        private static readonly List<Combo> Combos = new List<Combo>();
        private static readonly List<string> Categories = new List<string>();
        private static ConfigFile _cfg;
        private static bool _deriving;
        private static int _bulk;
        private static bool _rebuild;
        private static Item _lastLocked;

        /// <summary>LiveRaid Online, a player who is not the host: the shared settings are the host's and cannot be changed here.</summary>
        public static bool Locked { get; private set; }

        /// <summary>The banner's text (LiveRaid Online), empty - no banner.</summary>
        public static string Banner { get; private set; } = "";

        public static string BannerNote = "";

        /// <summary>Every setting shared with the group in LiveRaid Online.</summary>
        public static IEnumerable<ConfigEntryBase> SyncedEntries => Items.Where(i => i.Synced && i.Entry.ConfigFile == _cfg).Select(i => i.Entry);

        public static void Register(ConfigEntryBase entry, ConfigurationManagerAttributes attr, string category, bool simple, bool synced, string[] labels)
        {
            bool hotkey = entry.SettingType == typeof(KeyboardShortcut);
            Item item = new Item
            {
                Entry = entry, Attr = attr, Name = attr.DispName, Category = category, Simple = simple, Synced = synced && !hotkey, Labels = labels,
                Index = Items.Count,
            };
            if (!hotkey)
            {
                attr.CustomDrawer = Draw;
                attr.HideDefaultButton = true;
            }

            Items.Add(item);
            ByEntry[entry] = item;
            if (!Categories.Contains(category))
            {
                Categories.Add(category);
            }
        }

        public static void PackOf(ConfigEntry<PackState> pack, params ConfigEntryBase[] children) => Add(new PackCombo(), pack, children);

        public static void LevelOf(ConfigEntry<Level> level, ConfigEntryBase[] children, params (Level, string, object[])[] rows) =>
            Add(new LevelCombo { Rows = rows }, level, children);

        public static void AmountOf(ConfigEntry<int> amount, ConfigEntry<bool> flag, ConfigEntryBase count, int min) =>
            Add(new AmountCombo { Min = min }, amount, new[] { (ConfigEntryBase)flag, count });

        private static void Add(Combo combo, ConfigEntryBase entry, ConfigEntryBase[] children)
        {
            combo.Item = ByEntry[entry];
            combo.Item.Combo = combo;
            foreach (ConfigEntryBase c in children)
            {
                Item child = ByEntry[c];
                child.Owner = combo;
                child.Category = combo.Item.Category;
                child.Attr.Category = Loc.Tr(combo.Item.Category);
                combo.Children.Add(child);
            }

            Combos.Add(combo);
        }

        /// <summary>After every setting is bound: order, the values of the compact menu, visibility; changes are followed from now on.</summary>
        public static void Finish(ConfigFile cfg)
        {
            _cfg = cfg;

            // Display order: per group the packs and levels each followed by their own settings, then the rest in the order they were bound
            int order = 100000;
            foreach (string category in Categories)
            {
                List<Item> inGroup = Items.Where(i => i.Category == category).ToList();
                List<Item> shown = new List<Item>();
                shown.AddRange(inGroup.Where(i => i.Owner == null && i.Combo == null && i.Simple && i.Index < FirstCombo(inGroup)));
                foreach (Item c in inGroup.Where(i => i.Combo != null))
                {
                    shown.Add(c);
                    shown.AddRange(c.Combo.Children);
                }

                shown.AddRange(inGroup.Where(i => !shown.Contains(i) && i.Owner == null && i.Simple));
                shown.AddRange(inGroup.Where(i => !shown.Contains(i)));
                foreach (Item i in shown)
                {
                    i.Attr.Order = order--;
                    if (i.Owner != null)
                    {
                        i.Attr.DispName = "   · " + i.Name;
                    }
                }
            }

            cfg.SettingChanged += OnSettingChanged;
            Derive();
            Refresh();
        }

        /// <summary>The language changed: names, descriptions' owners and group titles are written again in the new one.</summary>
        public static void Relabel()
        {
            foreach (Item i in Items)
            {
                string name = Loc.NameOf(i.Entry);
                if (name != null)
                {
                    i.Name = name;
                }

                i.Attr.DispName = (i.Owner != null ? "   · " : "") + i.Name;
                i.Attr.Category = Loc.Tr(i.Category);
            }

            // ConfigurationManager copies names, descriptions and group titles into its list when it builds it (on opening F12):
            // a language changed with the window open showed only our own drawn values translated until F12 was opened again
            _rebuild = true;
        }

        private static int FirstCombo(List<Item> group) => group.Where(i => i.Combo != null).Select(i => i.Index).DefaultIfEmpty(int.MaxValue).Min();

        private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                if (!ByEntry.TryGetValue(e.ChangedSetting, out Item item))
                {
                    return;
                }

                if (item.Entry == Settings.DevMode)
                {
                    Refresh();
                    return;
                }

                if (item.Combo != null)
                {
                    if (_deriving)
                    {
                        return;
                    }

                    _bulk++;
                    try
                    {
                        item.Combo.Apply(item.Entry.BoxedValue);
                    }
                    finally
                    {
                        _bulk--;
                    }

                    Derive();
                    return;
                }

                if (_bulk == 0 && item.Owner != null)
                {
                    Derive();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("F12 menu: " + ex);
            }
        }

        /// <summary>Many values are about to change at once (the host's settings arrive): the menu is worked out once afterwards.</summary>
        public static void BeginBulk() => _bulk++;

        public static void EndBulk()
        {
            _bulk = Math.Max(0, _bulk - 1);
            if (_bulk == 0)
            {
                Derive();
            }
        }

        /// <summary>The packs, levels and amounts follow what their settings now are.</summary>
        private static void Derive()
        {
            _deriving = true;
            try
            {
                foreach (Combo c in Combos)
                {
                    object v = c.Derive();
                    if (!Equals(c.Item.Entry.BoxedValue, v))
                    {
                        c.Item.Entry.BoxedValue = v;
                    }
                }
            }
            finally
            {
                _deriving = false;
            }
        }

        public static void SetLock(bool locked, string banner)
        {
            if (Locked == locked && Banner == banner)
            {
                return;
            }

            Locked = locked;
            Banner = banner ?? "";
            Refresh();
        }

        /// <summary>Shown / hidden and read-only as of now; ConfigurationManager rebuilds its list on the next frame.</summary>
        private static void Refresh()
        {
            bool dev = Settings.DevMode?.Value == true;
            _lastLocked = null;
            foreach (Item i in Items)
            {
                bool show = i.Entry == Settings.MenuStatus ? Banner.Length > 0 : dev || (i.Simple && i.Owner == null);
                i.Attr.Browsable = show;
                i.Attr.ReadOnly = Locked && (i.Synced || i.Combo != null);
                if (i.Entry == Settings.MenuStatus)
                {
                    i.Attr.HideSettingName = true;
                }
            }

            if (Locked)
            {
                _lastLocked = Items.Where(i => i.Attr.Browsable == true && i.Entry.ConfigFile == _cfg)
                    .OrderBy(i => Categories.IndexOf(i.Category)).ThenByDescending(i => i.Attr.Order ?? 0).LastOrDefault();
            }

            _rebuild = true;
        }

        private static object _manager;
        private static MethodInfo _buildList;
        private static PropertyInfo _displaying;

        /// <summary>ConfigurationManager's window is open.</summary>
        public static bool WindowOpen
        {
            get
            {
                FindManager();
                try
                {
                    return _displaying != null && (bool)_displaying.GetValue(_manager, null);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void FindManager()
        {
            if (_manager != null)
            {
                return;
            }

            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out BepInEx.PluginInfo info) && info.Instance != null)
            {
                _manager = info.Instance;
                _buildList = _manager.GetType().GetMethod("BuildSettingList", BindingFlags.Instance | BindingFlags.Public);
                _displaying = _manager.GetType().GetProperty("DisplayingWindow", BindingFlags.Instance | BindingFlags.Public);
            }
        }

        /// <summary>Every frame (Plugin.Update).</summary>
        public static void Tick()
        {
            if (!_rebuild)
            {
                return;
            }

            _rebuild = false;
            FindManager();
            try
            {
                _buildList?.Invoke(_manager, null);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("F12 menu could not be refreshed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- drawing

        private const float ResetWidth = 56f;
        private const float NumberWidth = 44f;
        private static float _width = 260f;
        private static GUIStyle _small;
        private static GUIStyle _bannerStyle;
        private static GUIStyle _bannerNote;
        private static GUIStyle _veilText;
        private static Texture2D _veilTex;
        private static Texture2D _bannerTex;
        private static Rect _bannerRect;
        private static int _bannerFrame = -1;

        private static void Styles()
        {
            if (_bannerStyle != null)
            {
                return;
            }

            // Buttons that squeeze into a narrow column: smaller text, little padding, the text is cut rather than pushing the row wider
            _small = new GUIStyle(GUI.skin.button) { fontSize = 12, padding = new RectOffset(3, 3, 3, 3), clipping = TextClipping.Clip };
            _bannerTex = Solid(new Color(0.35f, 0.05f, 0.03f, 0.95f));
            _bannerStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true,
                padding = new RectOffset(8, 8, 8, 4),
            };
            _bannerStyle.normal.background = _bannerTex;
            _bannerStyle.normal.textColor = new Color(1f, 0.82f, 0.35f);
            _bannerNote = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, wordWrap = true };
            _bannerNote.normal.textColor = new Color(0.85f, 0.85f, 0.8f);
            _veilTex = Solid(new Color(0.05f, 0.05f, 0.05f, 0.55f));
            _veilText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
            _veilText.normal.textColor = new Color(1f, 0.78f, 0.3f, 0.9f);
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static void Draw(ConfigEntryBase entry)
        {
            if (!ByEntry.TryGetValue(entry, out Item item))
            {
                return;
            }

            Styles();
            if (entry == Settings.MenuStatus)
            {
                DrawBanner();
                return;
            }

            bool locked = item.Attr.ReadOnly == true;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && !locked;
            try
            {
                // Everything of a row fits into the value column of the window (it was cut off at the right edge of the 650 px window)
                _width = Mathf.Max(150f, Patches.ConfigWindow.ValueWidth - ResetWidth - 6f);
                DrawValue(item);
                bool atDefault = Same(entry.BoxedValue, entry.DefaultValue);
                GUI.enabled = enabled && !locked && !atDefault && item.Combo == null;
                if (GUILayout.Button(item.Combo == null ? Loc.Tr("Сброс") : " ", _small, GUILayout.Width(ResetWidth)) && !locked)
                {
                    entry.BoxedValue = entry.DefaultValue;
                }
            }
            finally
            {
                GUI.enabled = enabled;
            }

            if (item == _lastLocked && Event.current.type == EventType.Repaint && _bannerFrame == Time.frameCount)
            {
                DrawVeil(GUILayoutUtility.GetLastRect());
            }
        }

        private static void DrawValue(Item item)
        {
            ConfigEntryBase entry = item.Entry;
            Type t = entry.SettingType;
            if (item.Combo != null && item.Combo.Values != null)
            {
                Toolbar(entry, item.Combo.Labels, item.Combo.Values);
                return;
            }

            if (t == typeof(bool))
            {
                bool v = (bool)entry.BoxedValue;
                bool n = GUILayout.Toggle(v, v ? Loc.Tr("Вкл.") : Loc.Tr("Выкл."), GUILayout.Width(_width));
                if (n != v)
                {
                    entry.BoxedValue = n;
                }

                return;
            }

            if (t.IsEnum)
            {
                Array values = Enum.GetValues(t);
                string[] labels = item.Labels?.Select(Loc.Tr).ToArray() ?? values.Cast<object>().Select(v => v.ToString()).ToArray();
                Toolbar(entry, labels, values.Cast<object>().ToArray());
                return;
            }

            if ((t == typeof(int) || t == typeof(float)) && entry.Description?.AcceptableValues is AcceptableValueBase range)
            {
                float min = Convert.ToSingle(range.GetType().GetProperty("MinValue")?.GetValue(range, null), CultureInfo.InvariantCulture);
                float max = Convert.ToSingle(range.GetType().GetProperty("MaxValue")?.GetValue(range, null), CultureInfo.InvariantCulture);
                float v = Convert.ToSingle(entry.BoxedValue, CultureInfo.InvariantCulture);
                float n = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(_width - NumberWidth - 4f));
                if (t == typeof(int))
                {
                    int ni = Mathf.RoundToInt(n);
                    if (ni != (int)entry.BoxedValue)
                    {
                        entry.BoxedValue = ni;
                    }

                    GUILayout.Label(item.Combo != null && ni == 0 ? Loc.Tr("выкл.") : ni.ToString(CultureInfo.InvariantCulture), GUILayout.Width(NumberWidth));
                }
                else
                {
                    float step = max - min <= 1.01f ? 0.01f : max - min <= 5.01f ? 0.05f : max - min <= 25f ? 0.5f : 1f;
                    n = Mathf.Round(n / step) * step;
                    if (Mathf.Abs(n - v) > step * 0.5f)
                    {
                        entry.BoxedValue = Mathf.Clamp(n, min, max);
                    }

                    GUILayout.Label(v.ToString(max - min <= 1.01f ? "0.00" : "0.##", CultureInfo.InvariantCulture), GUILayout.Width(NumberWidth));
                }

                return;
            }

            string s = entry.GetSerializedValue();
            string ns = GUILayout.TextField(s, GUILayout.Width(_width));
            if (ns != s)
            {
                try
                {
                    entry.SetSerializedValue(ns);
                }
                catch
                {
                    // half-typed
                }
            }
        }

        private static void Toolbar(ConfigEntryBase entry, string[] labels, object[] values)
        {
            int current = Array.FindIndex(values, v => Equals(v, entry.BoxedValue));
            int chosen = GUILayout.Toolbar(current, labels, _small, GUILayout.Width(_width));
            if (chosen != current && chosen >= 0 && chosen < values.Length)
            {
                entry.BoxedValue = values[chosen];
            }
        }

        private static void DrawBanner()
        {
            GUILayout.BeginVertical();
            GUILayout.Label(Banner, _bannerStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(34));
            if (Event.current.type == EventType.Repaint)
            {
                _bannerRect = GUILayoutUtility.GetLastRect();
                _bannerFrame = Time.frameCount;
            }

            if (BannerNote.Length > 0)
            {
                GUILayout.Label(BannerNote, _bannerNote, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>A dark veil from under the banner down to the last locked row, with the lock written over it again and again.</summary>
        private static void DrawVeil(Rect last)
        {
            float top = _bannerRect.yMax + 2f;
            float bottom = last.yMax + 2f;
            if (bottom <= top + 10f)
            {
                return;
            }

            Rect veil = new Rect(_bannerRect.x, top, _bannerRect.width, bottom - top);
            GUI.DrawTexture(veil, _veilTex);
            for (float y = top + 40f; y < bottom - 20f; y += 320f)
            {
                GUI.Label(new Rect(veil.x, y, veil.width, 40f), Loc.Tr("Настройки контролируются только хостом"), _veilText);
            }
        }

        private static bool Same(object a, object b)
        {
            if (a is float fa && b != null)
            {
                return Mathf.Abs(fa - Convert.ToSingle(b, CultureInfo.InvariantCulture)) < 0.001f;
            }

            if (a is int ia && b is float fb)
            {
                return Mathf.Abs(ia - fb) < 0.001f;
            }

            return Equals(a, b) || (a is int i1 && b != null && b.GetType().IsPrimitive && i1 == Convert.ToInt32(b, CultureInfo.InvariantCulture));
        }
    }
}
