// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots；第三方功能只依赖本文件。
// 本文件不得引用 SR 其它文件，须能单独编译；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {   // 门面主体；其余成员见同目录 SRApi.*.cs（同一 partial 类）
        private static Action<string> _info, _warn, _error;
        private static Action<string> _notify;
        // 带显式级别 / 动作按钮的通知；未实现时退回普通 Notify。
        public sealed class NotifyArgs {
            public string Text, Tag;
            public int Level;          // 0 普通 / 1 注意 / 2 重要（-1 = 按文本猜）
            public Action Action;      // 可选动作：通知中心画按钮，点击执行后关闭
        }
        public delegate bool NotifyExFn(NotifyArgs args);
        private static Action<NotifyArgs> _notifyEx;
        // 注入前的日志缓冲：32 条环形 + Debug 兜底，注入后一次性补发。
        private static bool _sinksReady;
        private static readonly string[] _preLog = new string[32];
        private static int _preLogNext, _preLogCount;
        private static void PreLog(string msg) {
            try {
                _preLog[_preLogNext] = msg;
                _preLogNext = (_preLogNext + 1) % _preLog.Length;
                if (_preLogCount < _preLog.Length) _preLogCount++;
                UnityEngine.Debug.LogWarning("[SR]" + msg);
            } catch { /* 兜底路径，失败就只能丢 */ }
        }
        // 核心启动时注入日志实现；注入前调用先进环形缓冲，注入后补发。
        public static void SetLogSinks(Action<string> info, Action<string> warn, Action<string> error) {
            _info = info; _warn = warn; _error = error;
            _sinksReady = true;
            try {
                for (int i = 0; i < _preLogCount; i++) {
                    int idx = (_preLogNext - _preLogCount + i + _preLog.Length) % _preLog.Length;
                    string line = _preLog[idx];
                    if (string.IsNullOrEmpty(line)) continue;
                    try { if (_warn != null) _warn(line); } catch { /* 单条失败不影响其它 */ }
                }
            } catch { /* 补发失败不影响注入 */ }
            _preLogCount = 0;
        }
        public static void SetNotifySink(Action<string> notify) { _notify = notify; }
        public static void SetNotifyExSink(Action<NotifyArgs> notifyEx) { _notifyEx = notifyEx; }
        public static void NotifyEx(NotifyArgs args) {
            try {
                Action<NotifyArgs> n = _notifyEx;
                if (n != null) { n(args); return; }
                if (_notify != null && args != null) _notify(args.Text ?? "");
            } catch (Exception __ne) { SRApi.LogWarn("[NotifyEx] " + __ne.GetType().Name); }
        }
        public static void NotifyEx(string text, int level, string tag, Action action) {
            NotifyEx(new NotifyArgs { Text = text, Tag = tag, Level = level, Action = action });
        }

        // 诊断：最近一次滚轮被哪一层消费（zone / hook / none 等）。
        private static Func<string> _wheelLayer;
        public static void SetWheelLayerSink(Func<string> f) { _wheelLayer = f; }
        public static string WheelLayer { get { Func<string> f = _wheelLayer; return f != null ? f() : ""; } }

        // 控件/表格绘制时用自身矩形询问滚轮归属。
        // 返回 0 = 不归我；非 0 = 上滚为正的格数，已从队列取走。
        private static Func<Rect, float> _wheelTake;
        public static void SetWheelTakeSink(Func<Rect, float> f) { _wheelTake = f; }
        public static float WheelTake(Rect layoutRect) { Func<Rect, float> f = _wheelTake; return f != null ? f(layoutRect) : 0f; }
        // 兼容旧调用：空实现，仅保留签名。
        public static void WheelArea(string id, Rect layoutRect, Func<float, bool> handler) { }

        // 覆盖层：画在窗口最上层、不受页面裁剪，供功能弹出列表使用。
        private static Action<Action> _overlay;
        public static void SetOverlaySink(Action<Action> f) { _overlay = f; }
        public static void RegisterOverlay(Action draw) { Action<Action> f = _overlay; if (f != null) f(draw); }
        // 圆角面板（与核心同一主题色）
        private static Action<Rect, Color, float> _rounded;
        public static void SetRoundedSink(Action<Rect, Color, float> f) { _rounded = f; }
        public static void DrawPanel(Rect r, Color c, float radius) { Action<Rect, Color, float> f = _rounded; if (f != null) f(r, c, radius); }

        // 关掉管理器：功能页打开游戏自身界面前须先收起本面板，否则它点不动。
        private static Action _closeMenu;
        public static void SetCloseMenuSink(Action closeMenu) { _closeMenu = closeMenu; }
        public static void CloseMenu() { Action a = _closeMenu; if (a != null) a(); }
        // 分区：滚轮通道（优先级与选型）
        // 优先级：控件自身 > 页面级钩子 > 表格内部 > 帧级钩子；任一层消费后本帧后续层收不到。
        // 选型：列表在页面滚动区内用 TableWheelInput，页面已关滚动用 RegisterPageWheel。
        // 页面级钩子：窗口建滚动区前先取一次滚轮（返回 true = 已消费）；嵌套滚动区收不到滚轮事件。
        private static readonly List<Func<Vector2, float, bool>> _pageWheel = new List<Func<Vector2, float, bool>>();
        public static void RegisterPageWheel(Func<Vector2, float, bool> hook) {
            if (hook != null && !_pageWheel.Contains(hook)) _pageWheel.Add(hook);
        }
        public static bool DispatchPageWheel(Vector2 mouse, float wheel) {
            for (int i = 0; i < _pageWheel.Count; i++) {
                try { if (_pageWheel[i](mouse, wheel)) return true; }
                catch { /* 静默：单个页面的滚轮钩子出错不影响其它页面 */ }
            }
            return false;
        }

        // 打开管理器（从游戏界面返回时会自动弹回）。
        private static Action _openMenu;
        public static void SetOpenMenuSink(Action openMenu) { _openMenu = openMenu; }
        public static void OpenMenu() { Action a = _openMenu; if (a != null) a(); }
        internal static void Warn(string msg) { try { Action<string> w = _warn; if (w != null) w(msg); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        private static string Str(object m) { try { return m == null ? "" : m.ToString(); } catch { return "<ToString 失败>"; } }
        public static void LogInfo(object m) { try { Action<string> a = _info; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogWarn(object m) { try { Action<string> a = _warn; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogError(object m) { try { Action<string> a = _error; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        // 用户提示：实现层注入 ShowUserMessage（同时写日志）。
        // 未注入时进环形缓冲，注入后补发。
        public static void Notify(string text) { try { Action<string> n = _notify; if (n != null) n(text ?? ""); else if (!_sinksReady) PreLog(Str(text)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void Notify(string text, params object[] args) {
            if (args == null || args.Length == 0) { Notify(text); return; }
            try { Notify(string.Format(text, args)); } catch { Notify(text); }
        }
    }
}
