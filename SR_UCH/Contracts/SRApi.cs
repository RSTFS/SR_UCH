// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：不得引用 SR 的其它文件，须能单独编译；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {   // 门面主体；其余成员见同目录 SRApi.*.cs（同一个 partial 类）
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
        // 底栏按钮扩展点：核心在「设置」按钮右侧依次绘制。
        public static readonly List<FooterButton> FooterButtons = new List<FooterButton>();
        public static void RegisterFooterButton(string id, string zh, string en, Action onClick) {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < FooterButtons.Count; i++) {
                if (FooterButtons[i].Id == id) { FooterButtons[i].LabelZh = zh; FooterButtons[i].LabelEn = en; FooterButtons[i].OnClick = onClick; return; }
            }
            FooterButtons.Add(new FooterButton(id, zh, en, onClick));
        }

        public static void RegisterPage(string section, Action render) { Action<string, Action> f = _registerPageSimple; if (f != null) f(section, render); }
        public static void RegisterPage(string section, string pageId, string nameZh, string nameEn, int order, Action render) { Action<string, string, string, string, int, Action> f = _registerPageFull; if (f != null) f(section, pageId, nameZh, nameEn, order, render); }

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

        // 迁移规则门面：注册表与 cfg 改写都在核心，只转发。
        // 表格控件门面：TableArgs 是纯数据参数对象，交互函数委托注入；操作列按钮只给描述，布局与命中由核心处理。
        public delegate void TableDragWatchdogFn(Event e, ref float watchdog, ref bool sbDragging, ref int moveCol,
            ref float moveDx, ref int dragCol, ref bool dragH, Action saveWidths, Action saveTableH, Action resetJoinDown);
        public delegate bool TableWheelInputFn(Event e, Rect box, Vector2 mp, float maxX, float maxY,
            ref Vector2 scrollCols, ref Vector2 scrollRows);
        public delegate bool TableScrollbarInputFn(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax,
            ref bool dragging, ref float grabY, ref float scrollY);
        public delegate float TableAutoHeightFn(float usedAbove, float minH);
        // 统一操作列按钮：按 widths 依次画，返回本帧命中并已执行回调的下标；widths 为 null 或 <=0 时按文字自适应。
        public delegate int TableActionButtonsFn(Rect rowRect, TableAction[] actions, float[] widths, ref int hoverIndex);
        private static Action<TableArgs> _tableDraw;
        private static TableDragWatchdogFn _tableDragWatchdog;
        private static TableWheelInputFn _tableWheelInput;
        private static TableScrollbarInputFn _tableScrollbarInput;
        // 存成 object 而非委托字段：注入发生在 MainPlugin 启动期自检之后，委托字段会被误报未注入。
        private static object _tableAutoHeight;
        private static object _tableActionButtons;
        public static void SetTableSinks(Action<TableArgs> tableDraw, TableDragWatchdogFn tableDragWatchdog,
                                         TableWheelInputFn tableWheelInput, TableScrollbarInputFn tableScrollbarInput) {
            _tableDraw = tableDraw; _tableDragWatchdog = tableDragWatchdog;
            _tableWheelInput = tableWheelInput; _tableScrollbarInput = tableScrollbarInput;
        }
        // 表格自适应高度：顶部已用高度 + 表格高 = 底栏之上可用高度；公式由实现层注入。
        public static void SetTableAutoHeightSink(TableAutoHeightFn tableAutoHeight) { _tableAutoHeight = tableAutoHeight; }
        public static float TableAutoHeight(float usedAbove, float minH) {
            TableAutoHeightFn f = _tableAutoHeight as TableAutoHeightFn;
            if (f != null) return f(usedAbove, minH);
            float avail = PageViewportHeight > Ctl.Sc(60f)
                ? PageViewportHeight
                : (ContentArea.height - Ctl.Sc(26f) - Ctl.Sc(2f) - FooterHeight);
            return Mathf.Max(minH, avail - usedAbove - Ctl.Sc(2f));
        }
        // 操作列统一控件：布局 / 命中 / 悬浮由实现层负责；未注入返回 -1；列宽按按钮文字实测。
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
        // 表头拖动换位：独立程序集不再直接调 SR.TableColMove。
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

        public delegate void TableScrollbarFn(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible, float alpha);
        private static object _tableScrollbar;
        public static void SetTableScrollbarSink(TableScrollbarFn f) { _tableScrollbar = f; }
        public static Rect TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, bool visible = true, float alpha = 1f) {
            Rect thumb = default(Rect);
            TableScrollbarFn f = _tableScrollbar as TableScrollbarFn;
            if (f != null) f(track, contentH, viewH, scrollY, dragging, out thumb, visible, alpha);
            return thumb;
        }
        // 列表高度自适应：返回"内容底界 - 当前 Y + 累计偏差"，绘制后用 TableAfterDraw 校准。
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
        // 竖向滚动条一体化：按 TableArgs 自动定位、绘制并处理输入。
        public delegate bool TableScrollbarForFn(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY);
        private static object _tableScrollbarFor;
        public static void SetTableScrollbarForSink(TableScrollbarForFn f) { _tableScrollbarFor = f; }
        public static bool TableScrollbarFor(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
            TableScrollbarForFn f = _tableScrollbarFor as TableScrollbarForFn;
            return f != null && f(a, e, ref dragging, ref grabY, ref scrollY);
        }


        // 行扩展点（与核心同名同签名）。
        public delegate bool RowFilterFn(ConfigEntryBase entry);
        public delegate string RowCompanionFn(ConfigEntryBase entry);
        public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
                // 行级紧凑模式：控件紧贴标签（标签列宽 = 文字宽），不参与整列对齐。
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
        // 主面板是否打开（一类浮窗只能由面板内触发）。
        private static Func<bool> _uiVisible;
        public static void SetUiVisibleSink(Func<bool> f) { _uiVisible = f; }
        public static bool Visible { get { Func<bool> f = _uiVisible; return f != null && f(); } }
        // 二类独立窗口（主面板关闭也要画）的绘制注入：核心不认识具体功能。
        private static readonly List<Action> _standaloneDrawers = new List<Action>();
        public static void RegisterStandaloneDrawer(Action draw) {
            if (draw == null) return;
            if (!_standaloneDrawers.Contains(draw)) _standaloneDrawers.Add(draw);
        }
        public static void RunStandaloneDrawers() {
            for (int i = 0; i < _standaloneDrawers.Count; i++) {
                try { _standaloneDrawers[i](); } catch (Exception) { /* 单个窗口失败不影响其它 */ }
            }
        }
        public static void SetGateSinks(Func<int, bool> gateModeAllows, Func<string> envSceneName, Func<bool> envInTreehouse) {
            _gateModeAllows = gateModeAllows; _envSceneName = envSceneName; _envInTreehouse = envInTreehouse;
        }
        public static bool GateModeAllows(ModeMask allowed) { Func<int, bool> f = _gateModeAllows; return f != null && f((int)allowed); }
        // 功能级模式门控：从 [SrFeature].Modes 读取（0 = 不限）。
        public static bool FeatureAllowed(Type impl) { Func<Type, bool> f = _featureAllowed; return f == null || f(impl); }
        public static void SetFeatureAllowedSink(Func<Type, bool> f) { _featureAllowed = f; }

        // UI 状态 / 注册门面（窗口尺寸、光标样式、页面注册、按键注册）
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
        // 右栏内容区整块矩形，不扣标题栏与底栏；铺满可视区请用 PageViewportHeight / PageContentBottomY。
        public static Rect ContentArea { get { Func<Rect> f = _contentArea; return f != null ? f() : default(Rect); } set { Action<Rect> a = _setContentArea; if (a != null) a(value); } }
        // 页面可视高度（滚动区真实视口，已扣标题 / 页头 / 底栏）：表格高度应以它为准。
        public static float PageViewportHeight;
        // 右栏布局总高（不含标题栏）。
        public static float PageBodyHeight;
        public static float PageUsedTop;
        // 页面内容底界（右栏底部 = 底栏上沿），与 last.yMax 同坐标系。
        public static float PageContentBottom;
        // 同帧对象查询缓存：同类型每帧只查一次；返回数组共享，不要修改。
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
        // 页面内容底界：滚动区顶 + 视口高 = 底栏条上沿。
        public static float PageContentBottomY;
        // 页面滚动区顶：页面内已用高度 = 当前布局 Y - 这个值。
        public static float PageScrollTopY;
        // 轻量诊断浮层：功能每帧写入，按 F9 在窗口左上角显示。
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
        public static bool Typing { get { Func<bool> f = _typing; return f != null && f(); } }

        public static void RegisterTypingProvider(Func<bool> provider) { Action<Func<bool>> f = _typingProvider; if (f != null) f(provider); }
        public static void RegisterInputGateHold(Func<bool> hold) { Action<Func<bool>> f = _inputGateHold; if (f != null) f(hold); }
        public static void RegisterCameraView(Action<Camera> apply) { Action<Action<Camera>> f = _cameraView; if (f != null) f(apply); }
        public static void RegisterPageChrome(string section, Action header, Action footer) { Action<string, Action, Action> f = _pageChrome; if (f != null) f(section, header, footer); }
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) { Action<string, ConfigEntry<KeyCode>, string> f = _registerKey; if (f != null) f(name, entry, mode); }
        // 键位动作核心化：功能声明"哪个键 + 模式 + 做什么"，核心每帧轮询（press / toggle 走边沿，hold 每帧）。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode, Action action) {
            Action<string, ConfigEntry<KeyCode>, string, Action> f = _registerKeyAction;
            if (f != null) f(name, entry, mode, action);
        }
        // 带 impl 的重载：受模式限制的键位交给核心统一判定（不通过则不执行 + 节流提示）。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode, Action action, Type impl) {
            Action<string, ConfigEntry<KeyCode>, string, Action, Type> f = _registerKeyActionGated;
            if (f != null) f(name, entry, mode, action, impl);
        }
        public static void SetKeyActionSink(Action<string, ConfigEntry<KeyCode>, string, Action> f) { _registerKeyAction = f; }
        private static Action<string, ConfigEntry<KeyCode>, string, Action, Type> _registerKeyActionGated;
        public static void SetKeyActionGatedSink(Action<string, ConfigEntry<KeyCode>, string, Action, Type> f) { _registerKeyActionGated = f; }
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyDown; return f != null && f(entry); }
        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyHeld; return f != null && f(entry); }
        public static void RenderSectionEntries(string section, Func<ConfigEntryBase, bool> filter) { Action<string, Func<ConfigEntryBase, bool>> f = _renderSectionEntries; if (f != null) f(section, filter); }
        public static void RenderSectionEntries(string section) { RenderSectionEntries(section, null); }
        public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries2; return f != null ? f(section) : null; }
        public static Color TableLineStrong { get { Func<Color> f = _tableLineStrong; return f != null ? f() : default(Color); } }
        public static void DrawFill(Rect r, Color c) { Action<Rect, Color> f = _drawFill; if (f != null) f(r, c); }
        public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }
        public static void DrawLine(Rect r) { DrawFill(r, new Color(1f, 1f, 1f, 0.22f)); }

        // UI 工具箱门面（IMGUI 控件 / 样式 / 布局），实现在核心。
        // 表格列状态统一持久化，格式为逗号分隔的三条配置。




        // 颜色文本工具（纯 BCL / 引擎实现）："#RRGGBB" 解析与格式化统一走这里。
        public delegate float RowComboWidthHook(ConfigEntryBase entry);
        public delegate bool RowEnumFilterHook(ConfigEntryBase entry, string enumName, int value);
        public static readonly List<RowComboWidthHook> RowComboWidths = new List<RowComboWidthHook>();
        public static readonly List<RowEnumFilterHook> RowEnumFilters = new List<RowEnumFilterHook>();


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
        public static string T(string zh, string en) { Func<string, string, string> f = _t; return f != null ? f(zh, en) : zh; }
        // 本地化注册：栏目 / 键名 / 描述 / 枚举值
        public static void LocSec(string sec, string zh) { Action<string, string> f = _locSec2; if (f != null) f(sec, zh); }
        public static void LocSec(string sec, string zh, string en) { Action<string, string, string> f = _locSec3; if (f != null) f(sec, zh, en); }
        public static void LocKey(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locKey; if (f != null) f(sec, key, zh, en); }
        public static void LocDesc(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locDesc; if (f != null) f(sec, key, zh, en); }
        // 一次登记显示名 + 悬浮说明（漏一行会让中文界面显示英文键名）；动态条目名按语言版本节流重注册。
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

        // 可折叠分区：标题行可点，展开时内容缩进在框内（状态按 id 记住）。
        private static Func<string, string, string, bool> _sectionBegin;
        private static Action _sectionEnd;
        public static void WireSection(Func<string, string, string, bool> begin, Action end) { _sectionBegin = begin; _sectionEnd = end; }
        public static bool SectionBegin(string id, string title, string tip = null) { Func<string, string, string, bool> f = _sectionBegin; return f != null && f(id, title, tip); }
        public static void SectionEnd() { Action f = _sectionEnd; if (f != null) f(); }

        // 每帧签名 / 结果缓存：同一帧同一 key 只算一次。


        public static void LocEnum(string name, string zh) { Action<string, string> f = _locEnum; if (f != null) f(name, zh); }
        public static string CurrentLanguageId { get { Func<string> f = _currentLanguageId; return f != null ? f() : "English"; } }
        public static int LangVer { get { Func<int> f = _langVer; return f != null ? f() : 0; } }
        public static void NotifyT(string zh, string en, params object[] args) {
            Action<string, string, object[]> f = _notifyT;
            if (f != null) f(zh, en, args);
            else Notify(string.Format(T(zh, en), args ?? new object[0]));
        }

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
        // 关机 / 卸载回调：ITweak 没有 Shutdown，功能可把撤销注册挂在这里；回调应幂等。
        public static void OnShutdown(string name, Action cleanup) { Action<string, Action> f = _shutdown; if (f != null) f(name, cleanup); }
        public static void Nav(string sec, int rank) { Action<string, int> f = _nav; if (f != null) f(sec, rank); }
        public static void NavHide(string sec) { Action<string> f = _navHide; if (f != null) f(sec); }
        public static void RegisterTick(string name, Action hook) { Action<string, Action> f = _tick; if (f != null) f(name, hook); }
        public static void RegisterSceneHook(Action hook) { Action<Action> f = _sceneHook; if (f != null) f(hook); }
        public static void RegisterDismissHook(Action hook) { Action<Action> f = _dismissHook; if (f != null) f(hook); }
        public static void RegisterWheelHook(Func<float, bool> hook) { Action<Func<float, bool>> f = _wheelHook; if (f != null) f(hook); }
        public static void HotkeyAction(string id, string label, Action action) { Action<string, string, Action> f = _hotkey; if (f != null) f(id, label, action); }

        // 反射门面：缓存 / RefOverride / 启动自检都在核心。

        internal static Type[] Owners() {
            Type self = typeof(SRApi);
            Type[] nested = self.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            Type[] all = new Type[nested.Length + 1];
            all[0] = self;
            Array.Copy(nested, 0, all, 1, nested.Length);
            return all;
        }
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

        // 故意不注入的通道白名单：当前为空；新增"合法恒为 null"的通道时把 "Owner.Field" 加进来。
        private static readonly HashSet<string> _optionalSinks = new HashSet<string>(StringComparer.Ordinal);

        // 自检：返回尚未注入的通道名（漏注入会让对应功能静默失效），由 MainPlugin 注入完成后调用。
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
                // 注入未发生就查缺失时写 Debug，避免启动期问题不可观测。
                try {
                    UnityEngine.Debug.LogError("[SR] 未注入的 API 通道 (" + miss.Count + "): " + string.Join(", ", miss.ToArray()));
                } catch { /* 兜底失败即放弃 */ }
            }
            return miss.ToArray();
        }
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

        // 统一带日志的 try / catch 包装：同一条 what 5 秒内只记一次。
    }
}
