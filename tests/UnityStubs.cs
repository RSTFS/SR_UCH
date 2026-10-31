// 单元测试用的 UnityEngine 最小桩（item 5）。
// 游戏目录里的 UnityEngine.*.dll 是 Unity 自己的 netstandard 变体，脱离 Unity 运行时加载会
// TypeLoadException；契约层只用到下面这些"纯数据结构"成员，所以测试工程用这份桩编译，
// 不引用任何游戏/DLL。**本文件不参与插件构建**（只在 build\run-tests.ps1 里被编译）。
using System;

namespace UnityEngine {
    public struct Vector2 {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return new Vector2(0f, 0f); } }
    }

    public struct Rect {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public float xMax { get { return x + width; } }
        public float yMax { get { return y + height; } }
        public bool Contains(Vector2 p) { return p.x >= x && p.x <= xMax && p.y >= y && p.y <= yMax; }
    }

    public struct Color {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }
        public static Color black { get { return new Color(0f, 0f, 0f, 1f); } }
    }

    public struct Color32 {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public static class Mathf {
        public const float Epsilon = 1.401298E-45f;
        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static float Clamp(float v, float min, float max) { return v < min ? min : (v > max ? max : v); }
        public static int Clamp(int v, int min, int max) { return v < min ? min : (v > max ? max : v); }
        public static byte Clamp(byte v, byte min, byte max) { return v < min ? min : (v > max ? max : v); }
        public static float Round(float v) { return (float)Math.Round(v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v); }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Abs(float v) { return v < 0f ? -v : v; }
    }

    public class GUIStyle {
        public Vector2 CalcSize(GUIContent content) { return new Vector2(0f, 0f); }
        public float CalcHeight(GUIContent content, float width) { return 0f; }
        public void Draw(Rect r, GUIContent c, bool hover, bool active, bool on, bool hasKeyboardFocus) { }
    }

    public class GUIContent {
        public string text, tooltip;
        public GUIContent() { }
        public GUIContent(string text) { this.text = text; }
        public GUIContent(string text, string tooltip) { this.text = text; this.tooltip = tooltip; }
        public static GUIContent none { get { return new GUIContent(); } }
    }

    public class GUILayoutOption { }

    public enum EventType { Layout, Repaint, MouseDown, MouseUp, MouseDrag, ScrollWheel, KeyDown, KeyUp, Used, Ignore }

    public class Event {
        public EventType type;
        public int button;
        public bool shift, control, alt;
        public Vector2 mousePosition;
        public Vector2 delta;
        public void Use() { type = EventType.Used; }
        public static Event current { get { return null; } }
    }

    public static class GUILayout {
        public static GUILayoutOption Width(float v) { return null; }
        public static GUILayoutOption Height(float v) { return null; }
        public static GUILayoutOption ExpandWidth(bool v) { return null; }
        public static void Label(string text) { }
        public static void Label(string text, GUIStyle style) { }
        public static void Label(string text, GUIStyle style, GUILayoutOption a) { }
        public static void Label(GUIContent c, GUIStyle style, GUILayoutOption a, GUILayoutOption b) { }
        public static void Space(float px) { }
        public static void Box(GUIContent c) { }
        public static void Box(GUIContent c, GUIStyle style) { }
        public static void Box(GUIContent c, GUIStyle style, GUILayoutOption a, GUILayoutOption b) { }
        public static void Label(GUIContent c, GUIStyle style) { }
        public static void Label(GUIContent c, GUIStyle style, GUILayoutOption a) { }
    }

    public class Object {
        public static void DontDestroyOnLoad(Object o) { }
        public static Object Instantiate(Object o) { return o; }
        public static void Destroy(Object o) { }
        public static Object[] FindObjectsOfType(Type t) { return new Object[0]; }
        public static Object FindObjectOfType(Type t) { return null; }
        public static T[] FindObjectsOfType<T>() where T : Object { return new T[0]; }
        public static T FindObjectOfType<T>() where T : Object { return null; }
    }

    public class Camera : Object { }

    public static class Time {
        public static float unscaledTime { get { return 0f; } }
        public static float unscaledDeltaTime { get { return 0f; } }
        public static int frameCount { get { return 0; } }
    }

    // 契约层的"注入前日志兜底"用 UnityEngine.Debug；测试工程要能编译到它。
    public static class Debug {
        public static void LogWarning(object m) { }
        public static void LogError(object m) { }
        public static void Log(object m) { }
    }

    public static class GUILayoutUtility {
        public static Rect GetRect(float w, float h) { return new Rect(0f, 0f, w, h); }
        public static Rect GetRect(float w, float h, GUIStyle s) { return new Rect(0f, 0f, w, h); }
        public static Rect GetLastRect() { return new Rect(0f, 0f, 0f, 0f); }
    }

    public enum KeyCode { None = 0, Escape = 27, Space = 32, BackQuote = 96, F1 = 282 }
}
