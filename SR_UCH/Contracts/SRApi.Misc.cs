// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {

        // 槽位名常量：Slots.Provide/Require/Retract 以前是裸字符串，拼错只会静默失效；统一从这里取。
        public static class SlotKeys {
            public const string NotifyHook = "sr.notify.hook";           // Func<string,string,bool> 通知治理钩子
            public const string NotifyHub2 = "sr.notify.hub2";           // NotifyExFn 带元数据/动作的通知钩子
            public const string SelfTestToken = "sr.selftest.token";     // 自检往返用的临时对象
            public const string LevelReloadBusy = "level.reloadBusy";    // 关卡重载锁（Level 提供，预设等待消费）
            public const string PresetsReloadBusy = "presets.reloadBusy";// 预设注入后等待游戏消费
        }
        public sealed class FooterButton {
            public string Id, LabelZh, LabelEn; public Action OnClick;
            public FooterButton(string id, string zh, string en, Action onClick) { Id = id; LabelZh = zh; LabelEn = en; OnClick = onClick; }
        }
        public static class Migration {
            private static Action<string, string[]> _remove;
            private static Action<string, string, string> _move;
            public static void SetSinks(Action<string, string[]> remove, Action<string, string, string> move) { _remove = remove; _move = move; }
            // 从 cfg 清理指定段下的一批死键（幂等）
            public static void Remove(string section, params string[] keys) { Action<string, string[]> f = _remove; if (f != null) f(section, keys); }
            // 把某个键从一段搬到另一段
            public static void Move(string fromSection, string toSection, string key) { Action<string, string, string> f = _move; if (f != null) f(fromSection, toSection, key); }
        }
        public static class Env {
            public static string SceneName { get { Func<string> f = _envSceneName; return f != null ? f() : ""; } }
            public static bool InTreehouse { get { Func<bool> f = _envInTreehouse; return f != null && f(); } }
        }
        public static class ColorHex {
            public static bool ParseHex(string hex, out Color c) {
                c = Color.white;
                try {
                    if (string.IsNullOrEmpty(hex)) return false;
                    string t = hex.Trim();
                    if (t.Length > 0 && t[0] == '#') t = t.Substring(1);
                    if (t.Length != 6) return false;
                    int v;
                    if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
                    c = FromColor32(new Color32((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF), 255));
                    return true;
                } catch { return false; }
            }
            public static Color ParseHex(string hex, Color fallback) { Color c; return ParseHex(hex, out c) ? c : fallback; }
            public static string ToHex(Color c) {
                Color32 q = ToColor32(c);
                return "#" + q.r.ToString("X2", CultureInfo.InvariantCulture)
                    + q.g.ToString("X2", CultureInfo.InvariantCulture)
                    + q.b.ToString("X2", CultureInfo.InvariantCulture);
            }
            // 唯一的 Color→Color32 归一化入口：预览、色板、实际文字颜色都走这里，避免多套取整逻辑。
            public static Color32 ToColor32(Color c) {
                int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
                int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
                int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
                return new Color32((byte)r, (byte)g, (byte)b, 255);
            }
            public static Color FromColor32(Color32 c) { return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f); }
        }
        public static class Cache {
            private static readonly Dictionary<string, object> _vals = new Dictionary<string, object>();
            private static readonly Dictionary<string, int> _frames = new Dictionary<string, int>();
            public static T FrameSig<T>(string key, Func<T> compute) {
                if (string.IsNullOrEmpty(key) || compute == null) return compute != null ? compute() : default(T);
                int f = Time.frameCount;
                int hit;
                object v;
                if (_frames.TryGetValue(key, out hit) && hit == f && _vals.TryGetValue(key, out v) && v is T) return (T)v;
                T nv = compute();
                _frames[key] = f;
                _vals[key] = nv;
                return nv;
            }
            public static void Clear() { _vals.Clear(); _frames.Clear(); }
        }
        public static class Ref {
            internal static Func<Type, string, string, FieldInfo> FieldFn;
            internal static Func<Type, string, string, MethodInfo> MethodFn;
            internal static Func<Type, string, Type[], string, MethodInfo> MethodArgsFn;
            internal static Action<string, string> OverrideFn;
            public static void SetSinks(Func<Type, string, string, FieldInfo> field,
                                        Func<Type, string, string, MethodInfo> method,
                                        Func<Type, string, Type[], string, MethodInfo> methodArgs,
                                        Action<string, string> over) {
                FieldFn = field; MethodFn = method; MethodArgsFn = methodArgs; OverrideFn = over;
            }
            public static FieldInfo Field(Type owner, string name, string feature) { Func<Type, string, string, FieldInfo> f = FieldFn; return f != null ? f(owner, name, feature) : null; }
            public static MethodInfo Method(Type owner, string name, string feature) { Func<Type, string, string, MethodInfo> f = MethodFn; return f != null ? f(owner, name, feature) : null; }
            public static MethodInfo Method(Type owner, string name, Type[] parameters, string feature) { Func<Type, string, Type[], string, MethodInfo> f = MethodArgsFn; return f != null ? f(owner, name, parameters, feature) : null; }
            public static void Override(string member, string feature) { Action<string, string> f = OverrideFn; if (f != null) f(member, feature); }
        }
        // 网络魔数注册表：各功能把 MsgGameRuleSet 的 magic 号登记进来，自检断言唯一，
        // 避免以后再加通道时撞号（现在散在核心/混乱牌组的常量里，只能靠注释互相提醒）。
        public static class NetMagic {
            private static readonly Dictionary<string, int> _byName = new Dictionary<string, int>();
            private static readonly Dictionary<int, string> _byValue = new Dictionary<int, string>();
            private static readonly List<string> _conflicts = new List<string>();
            public static bool Register(string name, int value) {
                try {
                    if (string.IsNullOrEmpty(name)) return false;
                    string owner;
                    if (_byValue.TryGetValue(value, out owner)) {
                        if (owner != name) _conflicts.Add(name + "=" + value + " 与 " + owner + " 冲突");
                        return false;
                    }
                    _byName[name] = value; _byValue[value] = name;
                    return true;
                } catch (Exception) { return false; }
            }
            public static string[] Conflicts() {
                try { return _conflicts.ToArray(); } catch (Exception) { return new string[0]; }
            }
            public static int Registered { get { try { return _byValue.Count; } catch (Exception) { return 0; } } }
        }

        // 注入状态登记：MissingSinks() 只能反射 delegate 字段，覆盖不到"非委托状态"通道
        // （例如 Storage 的根目录注入）。核心可把这类状态登记成 (名称, 就绪判定)，自检统一检查。
        public static class Sinks {
            private static readonly Dictionary<string, Func<bool>> _states = new Dictionary<string, Func<bool>>();
            private static readonly List<string> _order = new List<string>();
            public static void State(string name, Func<bool> ready) {
                try {
                    if (string.IsNullOrEmpty(name) || ready == null) return;
                    if (!_states.ContainsKey(name)) _order.Add(name);
                    _states[name] = ready;
                } catch (Exception) { return; }
            }
            // 未就绪的状态通道名（空数组 = 全部就绪）。
            public static string[] MissingStates() {
                List<string> miss = new List<string>();
                try {
                    for (int i = 0; i < _order.Count; i++) {
                        string n = _order[i];
                        Func<bool> f;
                        if (!_states.TryGetValue(n, out f) || f == null) { miss.Add(n); continue; }
                        bool ok = false;
                        try { ok = f(); } catch (Exception) { ok = false; }
                        if (!ok) miss.Add(n);
                    }
                } catch (Exception) { return miss.ToArray(); }
                return miss.ToArray();
            }
            public static int StateCount { get { return _order.Count; } }
        }

        public static class Guard {
            private static readonly Dictionary<string, int> _lastWarn = new Dictionary<string, int>();
            // 最近异常环形缓冲（16 条）：自检页「最近异常」卡与支持包用；不参与去重/计数逻辑。
            private const int RecentCap = 16;
            private static readonly string[] _recent = new string[RecentCap];
            private static int _recentNext, _recentTotal;
            public static string LastWhat, LastError;
            public static int WarnCount;
            public static int RecentTotal { get { return _recentTotal; } }
            // 最近记录（最新在前）；无记录返回空数组。
            public static string[] Recent() {
                int n = _recentTotal < RecentCap ? _recentTotal : RecentCap;
                string[] r = new string[n];
                for (int i = 0; i < n; i++) r[i] = _recent[(_recentNext - 1 - i + RecentCap * 2) % RecentCap];
                return r;
            }

            public static void Try(string what, Action action) {
                try { action(); } catch (Exception ex) { Warn(what, ex); }
            }
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); } catch (Exception ex) { Warn(what, ex); return fallback; }
            }
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            private static void Warn(string what, Exception ex) {
                try {
                    int now = Environment.TickCount;
                    int last;
                    if (what != null && _lastWarn.TryGetValue(what, out last) && unchecked((uint)(now - last)) < 5000u) return;
                    // 去重表只增不减：异常风暴时（what 动态拼接）加容量上限，超过即整体清空（去重语义可容忍重置）。
                    if (what != null && _lastWarn.Count > 512) _lastWarn.Clear();
                    if (what != null) _lastWarn[what] = now;
                    LastWhat = what;
                    LastError = ex != null ? (ex.GetType().Name + ": " + ex.Message) : "未知错误";
                    WarnCount++;
                    _recent[_recentNext] = what + " — " + LastError;
                    _recentNext = (_recentNext + 1) % RecentCap;
                    _recentTotal++;
                    SRApi.Warn("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch (Exception __ex) {
                    try { LastWhat = what; LastError = "Warn 自身失败: " + __ex.GetType().Name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
            }
        }
    }
}
