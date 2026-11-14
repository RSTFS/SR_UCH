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
    public static partial class SRApi {   // 门面主体；其余成员见同目录 SRApi.*.cs（同一个 partial 类）
        private static Action<string> _info, _warn, _error;
        private static Action<string> _notify;
        // 通知元数据通道（可选）：带显式级别 / 动作按钮的通知。未实现时退回普通 Notify。
        public sealed class NotifyArgs {
            public string Text, Tag;
            public int Level;          // 0 普通 / 1 注意 / 2 重要（-1 = 按旧逻辑按文本猜）
            public Action Action;      // 可选动作（通知中心会画一个按钮，点击执行后通知关闭）
        }
        public delegate bool NotifyExFn(NotifyArgs args);
        private static Action<NotifyArgs> _notifyEx;
        // 注入前的日志缓冲（audit 02-P0-1）：SetLogSinks 自己也是门面通道，
        // 在它之前调用的 LogInfo/LogWarn 会静默丢弃，于是"功能不工作但界面安静"。
        // 这里用 32 条环形缓冲 + Debug 兜底，注入后一次性补发。
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
        // 核心启动时注入：把契约层的日志/提示接到实现（无注入时静默丢弃，不影响功能）。
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

        // 关掉管理器：功能页要打开游戏自己的界面（规则书/修改器/积分）时必须先收起本面板，
        // 否则输入被本面板冻结，游戏界面点不动。
        // 覆盖层（画在窗口最上层，不受页面裁剪）：功能的弹出列表用
        // 滚轮区域（功能自绘控件用）：id 稳定、矩形为布局坐标，处理器返回 true = 已消费
        // 诊断：最近一次滚轮被哪一层消费（zone:xxx / hook / hook+off / zone-miss(n/m) / none）
        private static Func<string> _wheelLayer;
        public static void SetWheelLayerSink(Func<string> f) { _wheelLayer = f; }
        public static string WheelLayer { get { Func<string> f = _wheelLayer; return f != null ? f() : ""; } }

        // 绘制期取用滚轮量：控件/表格在自己画的时候用自己的矩形问一句"这格滚轮归我吗"。
        // 返回 0 = 不归我；非 0 = 上滚为正的一格，已从队列取走。
        private static Func<Rect, float> _wheelTake;
        public static void SetWheelTakeSink(Func<Rect, float> f) { _wheelTake = f; }
        public static float WheelTake(Rect layoutRect) { Func<Rect, float> f = _wheelTake; return f != null ? f(layoutRect) : 0f; }
        // 兼容旧调用：新模型不需要注册区域（空实现，保留签名）
        public static void WheelArea(string id, Rect layoutRect, Func<float, bool> handler) { }

        private static Action<Action> _overlay;
        public static void SetOverlaySink(Action<Action> f) { _overlay = f; }
        public static void RegisterOverlay(Action draw) { Action<Action> f = _overlay; if (f != null) f(draw); }
        // 圆角面板（与核心同一套主题色）
        private static Action<Rect, Color, float> _rounded;
        public static void SetRoundedSink(Action<Rect, Color, float> f) { _rounded = f; }
        public static void DrawPanel(Rect r, Color c, float radius) { Action<Rect, Color, float> f = _rounded; if (f != null) f(r, c, radius); }

        private static Action _closeMenu;
        public static void SetCloseMenuSink(Action closeMenu) { _closeMenu = closeMenu; }
        public static void CloseMenu() { Action a = _closeMenu; if (a != null) a(); }
        // ── 滚轮该走哪条路（audit P2④：三条通道的优先级说明）─────────────────────────────
        // 1) RegisterPageWheel（首选，给"自绘固定高度列表"）：在窗口建立滚动区之前回调，
        //    页面自己判断鼠标是否在自己那块矩形里，命中就改滚动值并返回 true。
        //    适用：预设列表这种"页面关掉滚动、列表自己滚"的布局（页面内部的滚轮事件已被滚动区处理过）。
        // 2) SRApi.TableWheelInput（次选）：页面内部调用，把滚轮换算成表格的横向/纵向滚动。
        //    适用：页面本身允许滚动、只需要表格额外响应的场景（联机列表）。
        // 3) RegisterWheelHook + SR.PostGuiWheel（最后）：帧级收口，GUI 画完之后统一分发，
        //    给"不依附任何控件"的功能用（地图缩放 / 自由相机缩放）。
        // 优先级：控件自己（下拉框/滑块）> 1) 页面级钩子 > 2) 表格内部 > 3) 帧级钩子；
        //         任何一层返回 true / 调 MarkWheelUsed() 后，后面的层本帧拿到的是已被消费的事件。
        // 新增列表时：先问"这个列表在页面滚动区内吗"——是→2)，不是（页面关滚动）→1)。
        // 页面级滚轮钩子：页面在窗口建立滚动区【之前】先拿一次滚轮（mouse, wheel；返回 true = 已消费）。
        // 自绘的固定高度列表（表格 / 网格）都靠它滚动 —— 嵌套滚动区在本面板里收不到滚轮事件。
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

        // 打开管理器（从游戏自己的界面回来时自动弹回）。
        private static Action _openMenu;
        public static void SetOpenMenuSink(Action openMenu) { _openMenu = openMenu; }
        public static void OpenMenu() { Action a = _openMenu; if (a != null) a(); }
        internal static void Warn(string msg) { try { Action<string> w = _warn; if (w != null) w(msg); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        private static string Str(object m) { try { return m == null ? "" : m.ToString(); } catch { return "<ToString 失败>"; } }
        public static void LogInfo(object m) { try { Action<string> a = _info; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogWarn(object m) { try { Action<string> a = _warn; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogError(object m) { try { Action<string> a = _error; if (a != null) a(Str(m)); else if (!_sinksReady) PreLog(Str(m)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        // 用户提示（右下角/系统消息）：实现层注入 ShowUserMessage（同时会写日志）。
        // 未注入时进缓冲（注入后补发），不再静默丢弃。
        public static void Notify(string text) { try { Action<string> n = _notify; if (n != null) n(text ?? ""); else if (!_sinksReady) PreLog(Str(text)); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void Notify(string text, params object[] args) {
            if (args == null || args.Length == 0) { Notify(text); return; }
            try { Notify(string.Format(text, args)); } catch { Notify(text); }
        }
    }
}
