using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRaid.Client.Patches
{
    /// <summary>
    /// The F12 window of ConfigurationManager (the one SPT ships, 18.4) is always 650 px wide and cannot be resized: the value column is ~275 px, too
    /// narrow for LiveRaid's choice bars. Here the window opens wider (55 % of the screen, 650-1150 px) and can be resized by dragging its bottom-right
    /// corner (a grip is drawn there); the size is kept between sessions. It helps every mod's settings, not only LiveRaid's.
    /// </summary>
    internal static class ConfigWindow
    {
        public static readonly Type Manager = AccessTools.TypeByName("ConfigurationManager.ConfigurationManager");
        private static readonly PropertyInfo RectProp = Manager == null ? null : AccessTools.Property(Manager, "SettingWindowRect");
        private static readonly PropertyInfo LeftProp = Manager == null ? null : AccessTools.Property(Manager, "LeftColumnWidth");
        private static readonly PropertyInfo RightProp = Manager == null ? null : AccessTools.Property(Manager, "RightColumnWidth");

        private const float Grip = 18f;
        private const float MinW = 560f;
        private const float MinH = 360f;

        /// <summary>The window size chosen by dragging ("w x h", empty = automatic). Not shown in F12.</summary>
        public static ConfigEntry<string> SavedSize;

        private static object _instance;
        private static Vector2 _want;
        private static bool _resizing;
        private static Vector2 _startMouse;
        private static Vector2 _startSize;
        private static int _gripId;
        private static Texture2D _gripTex;

        public static void Bind(ConfigFile cfg)
        {
            SavedSize = cfg.Bind("0. Compact menu", "F12 window size", "",
                new ConfigDescription("Размер окна F12, выбранный перетаскиванием угла (пусто - автоматически)", null,
                    new ConfigurationManagerAttributes { Browsable = false }));
        }

        /// <summary>Width of the value column of a setting row (what LiveRaid's controls may take), from the live window.</summary>
        public static float ValueWidth
        {
            get
            {
                try
                {
                    if (_instance != null && RightProp != null)
                    {
                        // ConfigurationManager keeps 115 px for its own Reset button and margins; LiveRaid draws its own button in the value column
                        return (int)RightProp.GetValue(_instance, null) + 52f;
                    }
                }
                catch
                {
                    // fall through
                }

                return 320f;
            }
        }

        private static Rect Get(object cm) => (Rect)RectProp.GetValue(cm, null);

        private static void Set(object cm, Rect r)
        {
            RectProp.SetValue(cm, r, null);
            int left = Mathf.RoundToInt(r.width / 2.5f);
            LeftProp?.SetValue(cm, left, null);
            RightProp?.SetValue(cm, (int)r.width - left - 115, null);
        }

        private static Vector2 Clamp(Vector2 size) =>
            new Vector2(Mathf.Clamp(size.x, MinW, Mathf.Max(MinW, Screen.width - 20f)), Mathf.Clamp(size.y, MinH, Mathf.Max(MinH, Screen.height - 20f)));

        private static Vector2 Saved()
        {
            string[] p = (SavedSize?.Value ?? "").Split('x');
            if (p.Length == 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)
                && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float h) && w > 0 && h > 0)
            {
                return Clamp(new Vector2(w, h));
            }

            float auto = Mathf.Clamp(Screen.width * 0.55f, 650f, 1150f);
            return Clamp(new Vector2(auto, Screen.height < 560 ? Screen.height : Screen.height - 100));
        }

        /// <summary>The window is (re)opened: the remembered or the wide automatic size, centred.</summary>
        public static void Opened(object cm)
        {
            _instance = cm;
            Vector2 size = Saved();
            Set(cm, new Rect(Mathf.Round((Screen.width - size.x) / 2f), Mathf.Round((Screen.height - size.y) / 2f), size.x, size.y));
            _want = size;
        }

        /// <summary>Before the window is drawn: a new size from the grip goes in (here, not inside the window - the window would hand back its old size).</summary>
        public static void BeforeGui(object cm)
        {
            _instance = cm;
            if (_want.x <= 0f)
            {
                return;
            }

            Rect r = Get(cm);
            if (Mathf.Abs(r.width - _want.x) > 0.5f || Mathf.Abs(r.height - _want.y) > 0.5f)
            {
                Set(cm, new Rect(r.x, r.y, _want.x, _want.y));
            }
        }

        /// <summary>Inside the window, before its contents: the grip takes the mouse before the window's drag does.</summary>
        public static void GripInput(object cm)
        {
            Rect r = Get(cm);
            Rect grip = new Rect(r.width - Grip - 2f, r.height - Grip - 2f, Grip, Grip);
            _gripId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            switch (e.GetTypeForControl(_gripId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && grip.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = _gripId;
                        _resizing = true;
                        _startMouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
                        _startSize = r.size;
                        e.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == _gripId && _resizing)
                    {
                        _want = Clamp(_startSize + (GUIUtility.GUIToScreenPoint(e.mousePosition) - _startMouse));
                        e.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == _gripId && _resizing)
                    {
                        GUIUtility.hotControl = 0;
                        _resizing = false;
                        if (SavedSize != null)
                        {
                            SavedSize.Value = Mathf.Round(_want.x).ToString(CultureInfo.InvariantCulture) + "x" + Mathf.Round(_want.y).ToString(CultureInfo.InvariantCulture);
                        }

                        e.Use();
                    }

                    break;
            }
        }

        /// <summary>Inside the window, after its contents: the grip itself (three diagonal strokes), on top of everything.</summary>
        public static void GripDraw(object cm)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (_gripTex == null)
            {
                _gripTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _gripTex.SetPixel(0, 0, Color.white);
                _gripTex.Apply();
            }

            Rect r = Get(cm);
            Color old = GUI.color;
            GUI.color = _resizing ? new Color(1f, 0.75f, 0.3f, 1f) : new Color(0.85f, 0.8f, 0.65f, 0.8f);
            float x0 = r.width - 4f, y0 = r.height - 4f;
            for (int line = 0; line < 3; line++)
            {
                float len = 5f + line * 5f;
                for (int i = 0; i < len; i++)
                {
                    GUI.DrawTexture(new Rect(x0 - len + i, y0 - i - 1f, 2f, 2f), _gripTex);
                }
            }

            GUI.color = old;
        }
    }

    public class CmWindowRectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(ConfigWindow.Manager, "CalculateWindowRect");

        [PatchPostfix]
        protected static void PatchPostfix(object __instance)
        {
            try
            {
                ConfigWindow.Opened(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("F12 window", e);
            }
        }
    }

    public class CmOnGuiPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(ConfigWindow.Manager, "OnGUI");

        [PatchPrefix]
        protected static void PatchPrefix(object __instance)
        {
            try
            {
                ConfigWindow.BeforeGui(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("F12 window", e);
            }
        }
    }

    public class CmSettingsWindowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(ConfigWindow.Manager, "SettingsWindow");

        [PatchPrefix]
        protected static void PatchPrefix(object __instance)
        {
            try
            {
                ConfigWindow.GripInput(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("F12 window", e);
            }
        }

        [PatchPostfix]
        protected static void PatchPostfix(object __instance)
        {
            try
            {
                ConfigWindow.GripDraw(__instance);
            }
            catch (Exception e)
            {
                Diag.Report("F12 window", e);
            }
        }
    }
}
