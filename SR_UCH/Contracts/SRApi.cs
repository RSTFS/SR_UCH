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

        // 组合键修饰符（与核心 ComboMod 数值一致，下面枚举体直接从核心源码镜像）
        [Flags] public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static Func<ConfigEntryBase, int> _keyComboMod;
        private static Action<ConfigEntryBase, int> _setDefaultCombo;
        private static Func<string, int, GUIStyle, GUILayoutOption[], string> _clearableTextField;
        private static Action<string, Action> _registerPageSimple;
        private static Action<string, string, string, string, int, Action> _registerPageFull;
        public static string MsgMasterOff { get { return T("本 Mod 总开关已关闭", "The mod's master switch is off"); } }
        public static string MsgNotLobby { get { return T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player"); } }
        public static void SetMiscSinks(Func<ConfigEntryBase, int> keyComboMod, Action<ConfigEntryBase, int> setDefaultCombo,
                                        Func<string, int, GUIStyle, GUILayoutOption[], string> clearableTextField,
                                        Action<string, Action> registerPageSimple,
                                        Action<string, string, string, string, int, Action> registerPageFull) {
            _keyComboMod = keyComboMod; _setDefaultCombo = setDefaultCombo; _clearableTextField = clearableTextField;
            _registerPageSimple = registerPageSimple; _registerPageFull = registerPageFull;
        }
        public static ComboMod KeyComboMod(ConfigEntryBase entry) { Func<ConfigEntryBase, int> f = _keyComboMod; return f != null ? (ComboMod)f(entry) : ComboMod.None; }
        public static void SetDefaultCombo(ConfigEntryBase entry, ComboMod mod) { Action<ConfigEntryBase, int> f = _setDefaultCombo; if (f != null) f(entry, (int)mod); }
        public static string ClearableTextField(string text, GUIStyle style, params GUILayoutOption[] options) { Func<string, int, GUIStyle, GUILayoutOption[], string> f = _clearableTextField; return f != null ? f(text, -1, style, options) : text; }
        public static string ClearableTextField(string text, int maxLength, GUIStyle style, params GUILayoutOption[] options) { Func<string, int, GUIStyle, GUILayoutOption[], string> f = _clearableTextField; return f != null ? f(text, maxLength, style, options) : text; }
        // 底栏按钮扩展点：核心在「设置」按钮右侧依次绘制；功能可注册自己的入口。
        public static readonly List<FooterButton> FooterButtons = new List<FooterButton>();
        // 对外契约 API：目前没有第三方使用，零调用是正常态（不是死代码）。
        public static void RegisterFooterButton(string id, string zh, string en, Action onClick) {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < FooterButtons.Count; i++) {
                if (FooterButtons[i].Id == id) { FooterButtons[i].LabelZh = zh; FooterButtons[i].LabelEn = en; FooterButtons[i].OnClick = onClick; return; }
            }
            FooterButtons.Add(new FooterButton(id, zh, en, onClick));
        }

        public static void RegisterPage(string section, Action render) { Action<string, Action> f = _registerPageSimple; if (f != null) f(section, render); }
        public static void RegisterPage(string section, string pageId, string nameZh, string nameEn, int order, Action render) { Action<string, string, string, string, int, Action> f = _registerPageFull; if (f != null) f(section, pageId, nameZh, nameEn, order, render); }

        // P2：外部 DLL 所需的其余 API（委托注入，行为不变）
        private static Func<bool> _allEnabled, _ignoreModeLimit;
        private static Action<bool> _setAllEnabled;
        private static Func<string, bool> _hasPage;
        private static Action<string, string> _notifyTag;
        private static Func<string, object[], string> _cat;
        private static Func<string, string> _enumNameZh;
        private static Func<float> _controlHeight;
        private static Action<float> _setControlHeight;
        private static Action<Func<bool>> _modeOverride, _hostOverride, _pauseProvider;
        private static Action<string, Action> _tabPrefix;
        public static void SetExSinks(Func<bool> allEnabled, Action<bool> setAllEnabled, Func<bool> ignoreModeLimit,
                                      Func<string, bool> hasPage, Action<string, string> notifyTag,
                                      Func<string, object[], string> cat, Func<string, string> enumNameZh,
                                      Func<float> controlHeight, Action<float> setControlHeight,
                                      Action<Func<bool>> modeOverride, Action<Func<bool>> hostOverride,
                                      Action<Func<bool>> pauseProvider, Action<string, Action> tabPrefix) {
            _allEnabled = allEnabled; _setAllEnabled = setAllEnabled; _ignoreModeLimit = ignoreModeLimit;
            _hasPage = hasPage; _notifyTag = notifyTag; _cat = cat; _enumNameZh = enumNameZh;
            _controlHeight = controlHeight; _setControlHeight = setControlHeight;
            _modeOverride = modeOverride; _hostOverride = hostOverride; _pauseProvider = pauseProvider; _tabPrefix = tabPrefix;
        }
        public static bool AllEnabled { get { Func<bool> f = _allEnabled; return f != null && f(); } set { Action<bool> a = _setAllEnabled; if (a != null) a(value); } }
        public static bool IgnoreModeLimit { get { Func<bool> f = _ignoreModeLimit; return f != null && f(); } }
        public static bool HasPage(string section) { Func<string, bool> f = _hasPage; return f != null && f(section); }
        public static void NotifyTag(string tag, string text) { Action<string, string> f = _notifyTag; if (f != null) f(tag, text); }
        public static string Cat(string text, object[] args) { Func<string, object[], string> f = _cat; return f != null ? f(text, args) : text; }
        public static string EnumNameZh(string n) { Func<string, string> f = _enumNameZh; return f != null ? f(n) : n; }
        public static float ControlHeight { get { Func<float> f = _controlHeight; return f != null ? f() : 0f; } set { Action<float> a = _setControlHeight; if (a != null) a(value); } }
        public static void RegisterModeOverride(Func<bool> f2) { Action<Func<bool>> f = _modeOverride; if (f != null) f(f2); }
        public static void RegisterHostOverride(Func<bool> f2) { Action<Func<bool>> f = _hostOverride; if (f != null) f(f2); }
        public static void RegisterPauseProvider(Func<bool> f2) { Action<Func<bool>> f = _pauseProvider; if (f != null) f(f2); }
        public static void RegisterTabPrefix(string section, Action draw) { Action<string, Action> f = _tabPrefix; if (f != null) f(section, draw); }

        // 迁移规则门面：规则注册表与 cfg 改写都在核心，契约层只转发。
        // 表格控件门面：TableArgs 是纯数据参数对象（与核心同字段），4 个交互函数委托注入。
        // 操作列按钮描述：功能页只给文字 / 提示 / 是否可用 / 回调，布局与命中由核心统一处理。
        public delegate void TableDragWatchdogFn(Event e, ref float watchdog, ref bool sbDragging, ref int moveCol,
            ref float moveDx, ref int dragCol, ref bool dragH, Action saveWidths, Action saveTableH, Action resetJoinDown);
        public delegate bool TableWheelInputFn(Event e, Rect box, Vector2 mp, float maxX, float maxY,
            ref Vector2 scrollCols, ref Vector2 scrollRows);
        public delegate bool TableScrollbarInputFn(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax,
            ref bool dragging, ref float grabY, ref float scrollY);
        public delegate float TableAutoHeightFn(float usedAbove, float minH);
        // 统一操作列按钮：给定行矩形按 widths 依次画按钮，返回本帧左键 MouseUp 命中并已执行回调的下标。
        // widths 可为 null（或某项 <=0）：该按钮宽度按文字自适应，总宽超出行宽时自动收缩。
        public delegate int TableActionButtonsFn(Rect rowRect, TableAction[] actions, float[] widths, ref int hoverIndex);
        private static Action<TableArgs> _tableDraw;
        private static TableDragWatchdogFn _tableDragWatchdog;
        private static TableWheelInputFn _tableWheelInput;
        private static TableScrollbarInputFn _tableScrollbarInput;
        // 存成 object 而非委托字段：本注入发生在 SR.Initialize（早于功能渲染），
        // 但晚于 MainPlugin 启动期的 SRApi.MissingSinks() 自检；用委托字段会被误报为"未注入"。
        private static object _tableAutoHeight;
        private static object _tableActionButtons;
        public static void SetTableSinks(Action<TableArgs> tableDraw, TableDragWatchdogFn tableDragWatchdog,
                                         TableWheelInputFn tableWheelInput, TableScrollbarInputFn tableScrollbarInput) {
            _tableDraw = tableDraw; _tableDragWatchdog = tableDragWatchdog;
            _tableWheelInput = tableWheelInput; _tableScrollbarInput = tableScrollbarInput;
        }
        // 表格自适应高度助手：页面顶部已用高度 + 表格高度 = 底栏之上可用高度。
        // 公式（标题栏 26 + 内边距 2 + 底栏 + 行区上下留白 4）由实现层注入；未注入时按同一语义兜底。
        public static void SetTableAutoHeightSink(TableAutoHeightFn tableAutoHeight) { _tableAutoHeight = tableAutoHeight; }
        public static float TableAutoHeight(float usedAbove, float minH) {
            TableAutoHeightFn f = _tableAutoHeight as TableAutoHeightFn;
            if (f != null) return f(usedAbove, minH);
            float avail = PageViewportHeight > Ctl.Sc(60f)
                ? PageViewportHeight
                : (ContentArea.height - Ctl.Sc(26f) - Ctl.Sc(2f) - FooterHeight);
            return Mathf.Max(minH, avail - usedAbove - Ctl.Sc(2f));
        }
        // 操作列按钮统一控件：布局 / 命中 / 悬浮由实现层负责；未注入时返回 -1（不响应）。
        // 操作列自适应宽度（核心计算）：按当前语言按钮文字实测，表格方直接用作操作列默认宽。
        private static Func<TableAction[], float> _tableActionColW;
        public static void SetTableActionColumnWidthSink(Func<TableAction[], float> f) { _tableActionColW = f; }
        public static float TableActionColumnWidth(TableAction[] actions) { Func<TableAction[], float> f = _tableActionColW; return f != null ? f(actions) : 0f; }

        public static void SetTableActionButtonsSink(TableActionButtonsFn tableActionButtons) { _tableActionButtons = tableActionButtons; }
        public static int TableActionButtons(Rect rowRect, TableAction[] actions, float[] widths, ref int hoverIndex) {
            TableActionButtonsFn f = _tableActionButtons as TableActionButtonsFn;
            if (f != null) return f(rowRect, actions, widths, ref hoverIndex);
            hoverIndex = -1;
            return -1;
        }
        public static void TableDraw(TableArgs a) { Action<TableArgs> f = _tableDraw; if (f != null) f(a); }
        // 表头拖动换位（核心化）：独立程序集不再直接调 SR.TableColMove。
        public delegate bool TableColMoveFn(Event e, Rect cell, int ci, int k, List<int> cols, Vector2 mp, bool inBox,
            List<Rect> headerCells, int[] order, Action<int[]> applyOrder, Action save, Action<int> click,
            ref int moveCol, ref float moveX0, ref float moveDx, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag, bool vertical);
        private static TableColMoveFn _tableColMove;
        public static void SetTableColMoveSink(TableColMoveFn f) { _tableColMove = f; }
        public static bool TableColMove(Event e, Rect cell, int ci, int k, List<int> cols, Vector2 mp, bool inBox,
            List<Rect> headerCells, int[] order, Action<int[]> applyOrder, Action save, Action<int> click,
            ref int moveCol, ref float moveX0, ref float moveDx, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag, bool vertical = false) {
            TableColMoveFn f = _tableColMove;
            return f != null && f(e, cell, ci, k, cols, mp, inBox, headerCells, order, applyOrder, save, click,
                ref moveCol, ref moveX0, ref moveDx, ref scrollCols, ref scrollColsAtDrag, vertical);
        }

        public static void TableDragWatchdog(Event e, ref float watchdog, ref bool sbDragging, ref int moveCol,
            ref float moveDx, ref int dragCol, ref bool dragH, Action saveWidths, Action saveTableH, Action resetJoinDown) {
            TableDragWatchdogFn f = _tableDragWatchdog;
            if (f != null) f(e, ref watchdog, ref sbDragging, ref moveCol, ref moveDx, ref dragCol, ref dragH, saveWidths, saveTableH, resetJoinDown);
        }
        public static bool TableWheelInput(Event e, Rect box, Vector2 mp, float maxX, float maxY, ref Vector2 scrollCols, ref Vector2 scrollRows) {
            TableWheelInputFn f = _tableWheelInput;
            return f != null && f(e, box, mp, maxX, maxY, ref scrollCols, ref scrollRows);
        }
        public static bool TableScrollbarInput(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax, ref bool dragging, ref float grabY, ref float scrollY) {
            TableScrollbarInputFn f = _tableScrollbarInput;
            return f != null && f(e, mp, track, thumb, scrollMax, ref dragging, ref grabY, ref scrollY);
        }

        // 竖向滚动条统一绘制（ImGui 配色）：返回滑块矩形，调用方把它传给 TableScrollbarInput。
        public delegate void TableScrollbarFn(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible, float alpha);
        private static object _tableScrollbar;
        public static void SetTableScrollbarSink(TableScrollbarFn f) { _tableScrollbar = f; }
        public static Rect TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, bool visible = true, float alpha = 1f) {
            Rect thumb = default(Rect);
            TableScrollbarFn f = _tableScrollbar as TableScrollbarFn;
            if (f != null) f(track, contentH, viewH, scrollY, dragging, out thumb, visible, alpha);
            return thumb;
        }
        // 列表高度自适应：返回“内容底界 - 当前布局 Y + 历史偏差”，并在绘制后用 TableAfterDraw 自校准。
        public delegate float TableAutoFitFn(float contentBottomY, float currentY, float minH, ref float bias);
        private static object _tableAutoFit;
        public static void SetTableAutoFitSink(TableAutoFitFn f) { _tableAutoFit = f; }
        public static float TableAutoFit(float contentBottomY, float currentY, float minH, ref float bias) {
            TableAutoFitFn f = _tableAutoFit as TableAutoFitFn;
            return f != null ? f(contentBottomY, currentY, minH, ref bias) : minH;
        }
        public delegate void TableAfterDrawFn(float contentBottomY, float drawnBottom, ref float bias);
        private static object _tableAfterDraw;
        public static void SetTableAfterDrawSink(TableAfterDrawFn f) { _tableAfterDraw = f; }
        public static void TableAfterDraw(float contentBottomY, float drawnBottom, ref float bias) {
            TableAfterDrawFn f = _tableAfterDraw as TableAfterDrawFn;
            if (f != null) f(contentBottomY, drawnBottom, ref bias);
        }
        // 竖向滚动条一体化：按 TableArgs 自动定位（数据区右侧、钉住列之前），绘制并处理输入；返回事件是否被处理。
        public delegate bool TableScrollbarForFn(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY);
        private static object _tableScrollbarFor;
        public static void SetTableScrollbarForSink(TableScrollbarForFn f) { _tableScrollbarFor = f; }
        public static bool TableScrollbarFor(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
            TableScrollbarForFn f = _tableScrollbarFor as TableScrollbarForFn;
            return f != null && f(a, e, ref dragging, ref grabY, ref scrollY);
        }


        // 行扩展点（与核心同名同签名）：功能向设置页条目行注册过滤/附加控件/滑块范围。
        public delegate bool RowFilterFn(ConfigEntryBase entry);
        public delegate string RowCompanionFn(ConfigEntryBase entry);
        public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
                // 行级"紧凑模式"：命中即让控件紧贴标签（标签列宽 = 文字宽），不参与整列对齐。
        // 用于会话 / 建造这类"每行标签长度差异大、控件离标签太远"的页面。
        public static readonly List<Func<ConfigEntryBase, bool>> RowCompact = new List<Func<ConfigEntryBase, bool>>();

public static readonly List<RowFilterFn> RowFilters = new List<RowFilterFn>();
        public static readonly List<RowCompanionFn> RowCompanions = new List<RowCompanionFn>();
        public static readonly List<RowCompanionFn> RowExtras = new List<RowCompanionFn>();
        public static readonly List<RowSliderFn> RowSliders = new List<RowSliderFn>();

        // 对局模式门控（与核心 SR.ModeMask 按位一致）与场景环境查询
        [Flags] public enum ModeMask { None = 0, Freeplay = 1, Party = 2, Creative = 4, Challenge = 8 }
        private static Func<int, bool> _gateModeAllows;
        private static Func<Type, bool> _featureAllowed;
        private static Func<string> _envSceneName;
        private static Func<bool> _envInTreehouse;
        public static void SetGateSinks(Func<int, bool> gateModeAllows, Func<string> envSceneName, Func<bool> envInTreehouse) {
            _gateModeAllows = gateModeAllows; _envSceneName = envSceneName; _envInTreehouse = envInTreehouse;
        }
        // 当前对局模式是否落在 allowed 位集合内
        public static bool GateModeAllows(ModeMask allowed) { Func<int, bool> f = _gateModeAllows; return f != null && f((int)allowed); }
        // 功能级模式门控：从 [SrFeature].Modes 元数据读取（0 = 不限），由核心判定当前模式是否放行。
        // 功能不再各自写 ModeMask 常量 + GateModeAllows，只声明一次元数据。
        public static bool FeatureAllowed(Type impl) { Func<Type, bool> f = _featureAllowed; return f == null || f(impl); }
        public static void SetFeatureAllowedSink(Func<Type, bool> f) { _featureAllowed = f; }
        // 场景环境（只读）

        // UI 状态/注册门面（窗口尺寸、光标样式、页面注册、按键注册）
        private static Func<Rect> _contentArea; private static Action<Rect> _setContentArea;
        private static Func<float> _footerHeight; private static Action<float> _setFooterHeight;
        private static Func<Vector2> _pageScroll; private static Action<Vector2> _setPageScroll;
        private static Func<bool> _noPageScrollBars, _cursorResize, _cursorMove;
        private static Action<bool> _setNoPageScrollBars, _setCursorResize, _setCursorMove;
        private static Action<Func<bool>> _typingProvider, _inputGateHold;
        private static Action<Action<Camera>> _cameraView;
        private static Action<string, Action, Action> _pageChrome;
        private static Action<string, ConfigEntry<KeyCode>, string> _registerKey;
        private static Action<string, ConfigEntry<KeyCode>, string, Action> _registerKeyAction;
        private static Func<ConfigEntry<KeyCode>, bool> _comboKeyDown, _comboKeyHeld;
        private static Action<string, Func<ConfigEntryBase, bool>> _renderSectionEntries;
        private static Func<string, List<ConfigEntryBase>> _sectionEntries2;
        private static Func<Color> _tableLineStrong;
        private static Action<Rect, Color> _drawFill;
        public static void SetUiSinks(
            Func<Rect> contentArea, Action<Rect> setContentArea, Func<float> footerHeight, Action<float> setFooterHeight,
            Func<Vector2> pageScroll, Action<Vector2> setPageScroll, Func<bool> noPageScrollBars, Action<bool> setNoPageScrollBars,
            Func<bool> cursorResize, Action<bool> setCursorResize, Func<bool> cursorMove, Action<bool> setCursorMove,
            Action<Func<bool>> typingProvider, Action<Func<bool>> inputGateHold, Action<Action<Camera>> cameraView,
            Action<string, Action, Action> pageChrome, Action<string, ConfigEntry<KeyCode>, string> registerKey,
            Func<ConfigEntry<KeyCode>, bool> comboKeyDown, Func<ConfigEntry<KeyCode>, bool> comboKeyHeld,
            Action<string, Func<ConfigEntryBase, bool>> renderSectionEntries, Func<string, List<ConfigEntryBase>> sectionEntries,
            Func<Color> tableLineStrong, Action<Rect, Color> drawFill) {
            _contentArea = contentArea; _setContentArea = setContentArea; _footerHeight = footerHeight; _setFooterHeight = setFooterHeight;
            _pageScroll = pageScroll; _setPageScroll = setPageScroll; _noPageScrollBars = noPageScrollBars; _setNoPageScrollBars = setNoPageScrollBars;
            _cursorResize = cursorResize; _setCursorResize = setCursorResize; _cursorMove = cursorMove; _setCursorMove = setCursorMove;
            _typingProvider = typingProvider; _inputGateHold = inputGateHold; _cameraView = cameraView; _pageChrome = pageChrome;
            _registerKey = registerKey; _comboKeyDown = comboKeyDown; _comboKeyHeld = comboKeyHeld;
            _renderSectionEntries = renderSectionEntries; _sectionEntries2 = sectionEntries;
            _tableLineStrong = tableLineStrong; _drawFill = drawFill;
        }
        // 注意：ContentArea 是「右栏内容区」的整块矩形，**不扣标题栏与底栏**；自绘页要铺满可视区请用
        // PageViewportHeight / PageContentBottomY（audit F-8）。
        public static Rect ContentArea { get { Func<Rect> f = _contentArea; return f != null ? f() : default(Rect); } set { Action<Rect> a = _setContentArea; if (a != null) a(value); } }
        // 页面可视高度（滚动区真实视口，已扣除窗口标题/页头/底栏）：功能页的表格高度应以它为准。
        public static float PageViewportHeight;
        // 右栏在布局坐标里的总高（不含标题栏），以及页面内容起点（页头之后的 y）。
        // 页面用这两个值算"我还能用多少高度"，全程同一坐标系，不会像屏幕换算那样算溢出。
        public static float PageBodyHeight;
        public static float PageUsedTop;
        // 页面内容在布局坐标里的底界（右栏底部 = 底栏上沿）：页面用它算"还能用多少高度"，
        // 与 last.yMax 同坐标系，算出来的表高不可能越过底栏。
        public static float PageContentBottom;
        // 同帧对象查询缓存：一帧内同类型只真正 FindObjectsOfType 一次（多个钩子同帧查询合并）。
        // 调用方仍应保留自己的节流；返回数组是共享的，不要修改其内容。
        private static readonly System.Collections.Generic.Dictionary<Type, object> _frameObjCache = new System.Collections.Generic.Dictionary<Type, object>();
        private static int _frameObjFrame = -1;
        public static T[] FrameObjects<T>() where T : UnityEngine.Object {
            int f = Time.frameCount;
            if (f != _frameObjFrame) { _frameObjFrame = f; _frameObjCache.Clear(); }
            object hit;
            if (_frameObjCache.TryGetValue(typeof(T), out hit)) return (T[])hit;
            T[] a = UnityEngine.Object.FindObjectsOfType<T>();
            _frameObjCache[typeof(T)] = a;
            return a;
        }
        // 页面内容底界（布局坐标）：滚动区顶 + 视口高 = 底栏条上沿。表格高度按它算可精确贴底。
        public static float PageContentBottomY;
        // 页面滚动区顶（布局坐标）：页面内“已用高度”一律 = 当前布局Y - 这个值。
        public static float PageScrollTopY;
        // 轻量诊断浮层：功能每帧可把状态写进来，按 F9 在窗口左上角显示（空则不显示）。
        public static readonly System.Collections.Generic.List<string> DiagLines = new System.Collections.Generic.List<string>();
        public static void Diag(string line) {
            try { if (!string.IsNullOrEmpty(line) && DiagLines.Count < 24) DiagLines.Add(line); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
        public static float FooterHeight { get { Func<float> f = _footerHeight; return f != null ? f() : 0f; } set { Action<float> a = _setFooterHeight; if (a != null) a(value); } }
        public static Vector2 PageScroll { get { Func<Vector2> f = _pageScroll; return f != null ? f() : default(Vector2); } set { Action<Vector2> a = _setPageScroll; if (a != null) a(value); } }
        public static bool NoPageScrollBars { get { Func<bool> f = _noPageScrollBars; return f != null && f(); } set { Action<bool> a = _setNoPageScrollBars; if (a != null) a(value); } }
        public static bool CursorResize { get { Func<bool> f = _cursorResize; return f != null && f(); } set { Action<bool> a = _setCursorResize; if (a != null) a(value); } }
        public static bool CursorMove { get { Func<bool> f = _cursorMove; return f != null && f(); } set { Action<bool> a = _setCursorMove; if (a != null) a(value); } }
        private static Func<bool> _typing;
        public static void SetTypingSink(Func<bool> f) { _typing = f; }
        // 正在输入文字（聊天/文本框）：键位轮询一律让路。面板打开本身不再拦功能快捷键。
        public static bool Typing { get { Func<bool> f = _typing; return f != null && f(); } }

        public static void RegisterTypingProvider(Func<bool> provider) { Action<Func<bool>> f = _typingProvider; if (f != null) f(provider); }
        public static void RegisterInputGateHold(Func<bool> hold) { Action<Func<bool>> f = _inputGateHold; if (f != null) f(hold); }
        public static void RegisterCameraView(Action<Camera> apply) { Action<Action<Camera>> f = _cameraView; if (f != null) f(apply); }
        public static void RegisterPageChrome(string section, Action header, Action footer) { Action<string, Action, Action> f = _pageChrome; if (f != null) f(section, header, footer); }
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) { Action<string, ConfigEntry<KeyCode>, string> f = _registerKey; if (f != null) f(name, entry, mode); }
        // 键位动作核心化：功能只声明"哪个键 + 模式 + 做什么"，由核心每帧统一轮询触发
        // （press = 按下边沿一次；toggle = 边沿一次，切换由功能自己写；hold = 按住期间每帧）。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode, Action action) {
            Action<string, ConfigEntry<KeyCode>, string, Action> f = _registerKeyAction;
            if (f != null) f(name, entry, mode, action);
        }
        public static void SetKeyActionSink(Action<string, ConfigEntry<KeyCode>, string, Action> f) { _registerKeyAction = f; }
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyDown; return f != null && f(entry); }
        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyHeld; return f != null && f(entry); }
        public static void RenderSectionEntries(string section, Func<ConfigEntryBase, bool> filter) { Action<string, Func<ConfigEntryBase, bool>> f = _renderSectionEntries; if (f != null) f(section, filter); }
        public static void RenderSectionEntries(string section) { RenderSectionEntries(section, null); }
        public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries2; return f != null ? f(section) : null; }
        public static Color TableLineStrong { get { Func<Color> f = _tableLineStrong; return f != null ? f() : default(Color); } }
        public static void DrawFill(Rect r, Color c) { Action<Rect, Color> f = _drawFill; if (f != null) f(r, c); }
        public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }
        public static void DrawLine(Rect r) { DrawFill(r, new Color(1f, 1f, 1f, 0.22f)); }

        // UI 工具箱门面（IMGUI 控件/样式/布局）：实现在核心，契约层只转发。
        // 表格列状态（列宽 / 列序 / 隐藏列）统一持久化：格式与旧实现完全一致（逗号分隔的三条配置）。
    // 页面只需持有实例并把 Order/Hidden/Widths 当作自己的工作数组即可。

    // 通用列表数据管线：过滤 / 排序结果按“版本戳”缓存，每帧最多重算一次。
        // 页面只需提供源数据 + 过滤/排序委托 + 一个版本戳（通常用 Time.frameCount 或配置脏标记）。

        // 列模板：列表只声明"标题 / 提示 / 宽度策略 / 对齐"，核心据此生成表头与默认列宽。


        // 颜色文本工具（纯 BCL/引擎实现，无实现层依赖）：功能层解析/格式化 "#RRGGBB" 统一走这里，
        // 不要在功能页里重复写一套 hex 解析。往返不变：hex → Color → hex 经过同一套 Color32 量化。
        // 行级扩展点注册表（契约层持有实例）：功能/独立程序集在这里 Add，核心遍历。
        public delegate float RowComboWidthHook(ConfigEntryBase entry);
        public delegate bool RowEnumFilterHook(ConfigEntryBase entry, string enumName, int value);
        public static readonly List<RowComboWidthHook> RowComboWidths = new List<RowComboWidthHook>();
        public static readonly List<RowEnumFilterHook> RowEnumFilters = new List<RowEnumFilterHook>();


        // 本地化门面：语言引擎（语言包 / 词条表 / 缓存）留在核心，契约层只转发，行为不变。
        private static Func<string, string, string> _t;
        private static Action<string, string> _locSec2;
        private static Action<string, string, string> _locSec3;
        private static Action<string, string, string, string> _locKey, _locDesc;
        private static Action<string, string> _locEnum;
        private static Func<string> _currentLanguageId;
        private static Func<int> _langVer;
        private static Action<string, string, object[]> _notifyT;
        public static void SetLangSinks(Func<string, string, string> t,
                                        Action<string, string> locSec2, Action<string, string, string> locSec3,
                                        Action<string, string, string, string> locKey,
                                        Action<string, string, string, string> locDesc,
                                        Action<string, string> locEnum,
                                        Func<string> currentLanguageId, Func<int> langVer,
                                        Action<string, string, object[]> notifyT) {
            _t = t; _locSec2 = locSec2; _locSec3 = locSec3; _locKey = locKey; _locDesc = locDesc;
            _locEnum = locEnum; _currentLanguageId = currentLanguageId; _langVer = langVer; _notifyT = notifyT;
        }
        // 取译文：zh 为源码中文原文，en 为英文回退
        public static string T(string zh, string en) { Func<string, string, string> f = _t; return f != null ? f(zh, en) : zh; }
        // 本地化注册：栏目 / 键名 / 描述 / 枚举值
        public static void LocSec(string sec, string zh) { Action<string, string> f = _locSec2; if (f != null) f(sec, zh); }
        public static void LocSec(string sec, string zh, string en) { Action<string, string, string> f = _locSec3; if (f != null) f(sec, zh, en); }
        public static void LocKey(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locKey; if (f != null) f(sec, key, zh, en); }
        public static void LocDesc(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locDesc; if (f != null) f(sec, key, zh, en); }
        // 一次登记「显示名 + 悬浮说明」：功能层原来每条都要写两行 LocKey/LocDesc，
        // 漏一行就会在中文界面显示英文键名（牌池扩到 17 张时漏过 4 张）。descZh 为空时只登记显示名。
        // 动态条目名：会随游戏语言/内容变化的名字（如游戏自己的规则名）。核心按语言版本节流重注册，
        // 功能不用自己维护"变了才 LocKey"的缓存。
        private static Action<ConfigEntryBase, string, string> _locDynamic;
        public static void SetLocDynamicSink(Action<ConfigEntryBase, string, string> f) { _locDynamic = f; }
        public static void LocDynamic(ConfigEntryBase entry, string zh, string en) {
            Action<ConfigEntryBase, string, string> f = _locDynamic;
            if (f != null) f(entry, zh, en);
        }
        public static void LocPair(string sec, string key, string zh, string en, string descZh = null, string descEn = null) {
            LocKey(sec, key, zh, en);
            if (!string.IsNullOrEmpty(descZh) || !string.IsNullOrEmpty(descEn)) LocDesc(sec, key, descZh ?? zh, descEn ?? en);
        }

        // 可折叠分区：标题行可点，展开时后续内容缩进在框内（按 id 记住本次会话的折叠状态）。
        private static Func<string, string, string, bool> _sectionBegin;
        private static Action _sectionEnd;
        public static void WireSection(Func<string, string, string, bool> begin, Action end) { _sectionBegin = begin; _sectionEnd = end; }
        public static bool SectionBegin(string id, string title, string tip = null) { Func<string, string, string, bool> f = _sectionBegin; return f != null && f(id, title, tip); }
        public static void SectionEnd() { Action f = _sectionEnd; if (f != null) f(); }

        // 每帧签名/结果缓存：同一帧内同一 key 只算一次。
        // 预设页与联机页原来各自手写 *_Signature() 字段缓存，忘记失效就会"改了筛选列表不动"；
        // 用这个助手可以按"帧"天然失效，不必再手动维护版本号。


        public static void LocEnum(string name, string zh) { Action<string, string> f = _locEnum; if (f != null) f(name, zh); }
        public static string CurrentLanguageId { get { Func<string> f = _currentLanguageId; return f != null ? f() : "English"; } }
        public static int LangVer { get { Func<int> f = _langVer; return f != null ? f() : 0; } }
        public static void NotifyT(string zh, string en, params object[] args) {
            Action<string, string, object[]> f = _notifyT;
            if (f != null) f(zh, en, args);
            else Notify(string.Format(T(zh, en), args ?? new object[0]));
        }

        // 门控/UI 状态门面：状态在核心，契约层只读（provider 注入）。
        public const int ApiVersion = 3;   //与核心 SR.ApiVersion 同步
        private static Func<bool> _hasServer, _gateMaster, _gateHost, _canControl, _canDoHost, _uiOpen, _blockInput, _blockWindowDrag;
        private static Func<int> _wheelFrame;
        private static Func<float> _takeWheel;
        private static Action _markWheelUsed, _forceResetUiState, _flushConfig;
        private static Func<string, string> _selectedPageId;
        public static void SetStateSinks(
            Func<bool> hasServer, Func<bool> gateMaster, Func<bool> gateHost, Func<bool> canControl,
            Func<bool> canDoHost, Func<bool> uiOpen, Func<bool> blockInput, Func<bool> blockWindowDrag,
            Func<int> wheelFrame, Func<float> takeWheel, Action markWheelUsed,
            Action forceResetUiState, Action flushConfig, Func<string, string> selectedPageId,
            Action<bool> setBlockWindowDrag, Action<int> setWheelFrame) {
            _hasServer = hasServer; _gateMaster = gateMaster; _gateHost = gateHost; _canControl = canControl;
            _canDoHost = canDoHost; _uiOpen = uiOpen; _blockInput = blockInput; _blockWindowDrag = blockWindowDrag;
            _wheelFrame = wheelFrame; _takeWheel = takeWheel; _markWheelUsed = markWheelUsed;
            _forceResetUiState = forceResetUiState; _flushConfig = flushConfig; _selectedPageId = selectedPageId;
            _setBlockWindowDrag = setBlockWindowDrag; _setWheelFrame = setWheelFrame;
        }
        public static bool HasServer { get { return _hasServer != null && _hasServer(); } }
        public static bool GateMaster { get { return _gateMaster != null && _gateMaster(); } }
        public static bool GateHost { get { return _gateHost != null && _gateHost(); } }
        public static bool CanControlLocally { get { return _canControl != null && _canControl(); } }
        public static bool CanDoHost() { return _canDoHost != null && _canDoHost(); }
        public static bool UiOpen { get { return _uiOpen != null && _uiOpen(); } }
        public static bool BlockInput { get { return _blockInput != null && _blockInput(); } }
        private static Action<bool> _setBlockWindowDrag;
        public static bool BlockWindowDrag { get { return _blockWindowDrag != null && _blockWindowDrag(); } set { Action<bool> a = _setBlockWindowDrag; if (a != null) a(value); } }
        private static Action<int> _setWheelFrame;
        public static int WheelFrame { get { return _wheelFrame != null ? _wheelFrame() : -1; } set { Action<int> a = _setWheelFrame; if (a != null) a(value); } }
        public static float TakeWheelEvent() { return _takeWheel != null ? _takeWheel() : 0f; }
        public static void MarkWheelUsed() { Action a = _markWheelUsed; if (a != null) a(); }
        public static void ForceResetUiState() { Action a = _forceResetUiState; if (a != null) a(); }
        public static void FlushConfig() { Action a = _flushConfig; if (a != null) a(); }
        public static string SelectedPageId(string section) { Func<string, string> f = _selectedPageId; return f != null ? f(section) : null; }

        // 扩展点注册门面：注册表在核心，契约层只转发（功能层不再依赖 SR.*）。
        private static Action<string, int> _nav;
        private static Action<string> _navHide;
        private static Action<string, Action> _tick;
        private static Action<Action> _sceneHook, _dismissHook;
        private static Action<Func<float, bool>> _wheelHook;
        private static Action<string, string, Action> _hotkey;
        private static Action<string, Action> _shutdown;
        public static void SetExtSinks(Action<string, int> nav, Action<string> navHide, Action<string, Action> tick,
                                       Action<Action> sceneHook, Action<Action> dismissHook,
                                       Action<Func<float, bool>> wheelHook, Action<string, string, Action> hotkey,
                                       Action<string, Action> shutdown) {
            _nav = nav; _navHide = navHide; _tick = tick; _sceneHook = sceneHook; _dismissHook = dismissHook; _wheelHook = wheelHook; _hotkey = hotkey;
            _shutdown = shutdown;
        }
        // 关机/卸载回调（R2）：ITweak 没有 Shutdown，功能可选地把"撤销注册"挂在这里。
        // 进程退出或模组被销毁时由宿主反向调用；回调应幂等，失败只记日志。
        public static void OnShutdown(string name, Action cleanup) { Action<string, Action> f = _shutdown; if (f != null) f(name, cleanup); }
        //侧栏栏目与顺序（rank 越小越靠前）
        public static void Nav(string sec, int rank) { Action<string, int> f = _nav; if (f != null) f(sec, rank); }
        //隐藏某个配置段对应的栏目
        public static void NavHide(string sec) { Action<string> f = _navHide; if (f != null) f(sec); }
        //每帧回调（name 仅用于自检/日志）
        public static void RegisterTick(string name, Action hook) { Action<string, Action> f = _tick; if (f != null) f(name, hook); }
        //场景切换通知
        public static void RegisterSceneHook(Action hook) { Action<Action> f = _sceneHook; if (f != null) f(hook); }
        //收起功能 UI 的通知
        public static void RegisterDismissHook(Action hook) { Action<Action> f = _dismissHook; if (f != null) f(hook); }
        //滚轮钩子（返回 true = 已消费）
        public static void RegisterWheelHook(Func<float, bool> hook) { Action<Func<float, bool>> f = _wheelHook; if (f != null) f(hook); }
        //注册一个可绑定的快捷键动作
        public static void HotkeyAction(string id, string label, Action action) { Action<string, string, Action> f = _hotkey; if (f != null) f(id, label, action); }

        // 反射门面：实际实现（缓存 / 登记 RefOverride / 启动自检）在核心，契约层只转发，行为不变。

        // 自检用：SRApi 自身 + 全部嵌套类型（含以后新增的门面分组，避免硬编码列表漏统计）。
        internal static Type[] Owners() {
            Type self = typeof(SRApi);
            Type[] nested = self.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            Type[] all = new Type[nested.Length + 1];
            all[0] = self;
            Array.Copy(nested, 0, all, 1, nested.Length);
            return all;
        }
        // 注入点总数（= 声明为委托类型的静态字段数）
        public static int SinkTotal() {
            int n = 0;
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) if (typeof(Delegate).IsAssignableFrom(fs[j].FieldType)) n++;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return n;
        }

        // 故意不注入的通道白名单：当前为空（所有 delegate 字段都会被 MainPlugin 注入，自检里是满额）。
        // 将来若新增"合法恒为 null"的通道，把 "Owner.Field" 形式的全名加进来即可（历史注释提到的
        // Ref.TypeFn 早已不存在，注释已同步删除）。
        private static readonly HashSet<string> _optionalSinks = new HashSet<string>(StringComparer.Ordinal);

        // 自检：反射遍历全部注入点，返回尚未注入的 API 通道名（漏注入会让对应功能静默失效）。
        // 运行时由 MainPlugin 在注入完成后调用。
        public static string[] MissingSinks() {
            List<string> miss = new List<string>();
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) {
                        FieldInfo f = fs[j];
                        if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        if (f.GetValue(null) != null) continue;
                        string name = owners[i].Name + "." + f.Name;
                        if (_optionalSinks.Contains(name)) continue;   // 白名单：确认"不注入是正常状态"
                        miss.Add(name);
                    }
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (miss.Count > 0 && !_sinksReady) {
                // 注入还没发生就查缺失：写 Debug，避免"启动期问题不可观测"（audit 02-P0-1）。
                try {
                    UnityEngine.Debug.LogError("[SR] 未注入的 API 通道 (" + miss.Count + "): " + string.Join(", ", miss.ToArray()));
                } catch { /* 兜底失败即放弃 */ }
            }
            return miss.ToArray();
        }
        // 已注入的 API 通道总数（自检用）
        public static int SinkCount() {
            int n = 0;
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) if (typeof(Delegate).IsAssignableFrom(fs[j].FieldType) && fs[j].GetValue(null) != null) n++;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return n;
        }

        // 统一带日志的 try / catch 包装：同一条 what 5 秒内只记一次，每帧路径上也安全。
    }
}
