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
    public static partial class SRApi {
        public static class Ctl {
            public delegate int ComboBoxFn(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width);
            private static Func<float, float> _sc;
            private static Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> _restoreLabel;
            private static Action<ConfigEntryBase> _renderControl;
            private static Action<ConfigEntryBase, bool, float> _renderEntryRow;
            private static Func<string, string, ConfigEntryBase> _findEntry;
            private static Func<string, List<ConfigEntryBase>> _sectionEntries;
            private static Action<ConfigEntryBase, float> _drawEntryRow;
            private static Func<string, float, float> _textHeight;
            private static Func<Rect, float, float, float, bool, float> _drawSlider;
            private static ComboBoxFn _comboBox;
            private static Func<float> _sidebarWidth, _winWidth;
            private static Action<ConfigEntryBase, object> _setValue;
            private static Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> _hotkeyActionButton;
            private static Func<string, string, string, Action, float, float, bool> _hotkeyButton;
            private static Func<string, GUIStyle> _style;
            private static Action _scrollToBottom, _noteEntryBound;
            private static Action<ConfigFile> _markConfigDirty;
            private static Func<bool> _langEn;
            private static Func<Dictionary<ConfigEntryBase, bool>> _editOpen;
            public static void SetSinks(
                Func<float, float> sc,
                Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> restoreLabel,
                Action<ConfigEntryBase> renderControl,
                Action<ConfigEntryBase, bool, float> renderEntryRow,
                Func<string, string, ConfigEntryBase> findEntry,
                Func<string, List<ConfigEntryBase>> sectionEntries,
                Action<ConfigEntryBase, float> drawEntryRow,
                Func<string, float, float> textHeight,
                Func<Rect, float, float, float, bool, float> drawSlider,
                ComboBoxFn comboBox,
                Func<float> sidebarWidth, Func<float> winWidth,
                Action<ConfigEntryBase, object> setValue,
                Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> hotkeyActionButton,
                Func<string, string, string, Action, float, float, bool> hotkeyButton,
                Func<string, GUIStyle> style,
                Action scrollToBottom, Action noteEntryBound, Action<ConfigFile> markConfigDirty,
                Func<bool> langEn, Func<Dictionary<ConfigEntryBase, bool>> editOpen) {
                _sc = sc; _restoreLabel = restoreLabel; _renderControl = renderControl; _renderEntryRow = renderEntryRow;
                _findEntry = findEntry; _sectionEntries = sectionEntries; _drawEntryRow = drawEntryRow; _textHeight = textHeight;
                _drawSlider = drawSlider; _comboBox = comboBox; _sidebarWidth = sidebarWidth; _winWidth = winWidth;
                _setValue = setValue; _hotkeyActionButton = hotkeyActionButton; _hotkeyButton = hotkeyButton; _style = style;
                _scrollToBottom = scrollToBottom; _noteEntryBound = noteEntryBound; _markConfigDirty = markConfigDirty;
                _langEn = langEn; _editOpen = editOpen;
            }
            public static float Sc(float v) { Func<float, float> f = _sc; return f != null ? f(v) : v; }
            public static float TableAutoHeight(float usedAbove, float minH) { return SRApi.TableAutoHeight(usedAbove, minH); }
            // 表格尺寸令牌（随 UI 缩放）；滚动几何为纯函数，单测直接覆盖。
        public static float TableRowsH(float tableH, float headerH) {
            return Mathf.Max(0f, tableH - headerH);
        }
        public static float TableMaxScrollY(int rows, float rowH, float rowsH) {
            float content = Mathf.Max(0, rows) * Mathf.Max(0f, rowH);
            return Mathf.Max(0f, content - Mathf.Max(0f, rowsH));
        }

        // 自绘列表的唯一滚动几何：把表格框收敛成屏幕真实可见的行区。
        // rowsH 若直接取 box.height - headerH，box 伸到屏幕外时会虚高（行画到屏幕外、maxY 归零、滚轮失效）。
        public static float ScrollRowsH(Rect box, float headerH, float visibleBottom, float rowH) {
            float rowsTop = box.y + headerH;
            float raw = Mathf.Max(rowH, box.height - headerH);
            float avail = Mathf.Max(rowsTop + rowH, visibleBottom) - rowsTop;
            return Mathf.Max(rowH, Mathf.Min(raw, avail));
        }
        // 收敛后行区的滚动上限（与 TableMaxScrollY 同公式，收敛逻辑收在一处）。
        public static float ScrollMaxY(int rows, float rowH, float rowsH) {
            return TableMaxScrollY(rows, rowH, rowsH);
        }

        public static float TableRowH() { return Sc(26f); }
            public static float ThemeRadius { get { return Sc(6f); } }
            public static float ThemeHoverFade { get { return 0.12f; } }
            public static Color ThemeFooterBg { get { return new Color(0.10f, 0.11f, 0.13f, 0.96f); } }
            public static Color ThemePanelBg { get { return new Color(0.08f, 0.08f, 0.09f, 0.96f); } }
            public static float[] TableColumnWidths(TableColumn[] cols) {
                if (cols == null) return null;
                float[] w = new float[cols.Length];
                for (int i = 0; i < cols.Length; i++) {
                    TableColumn c = cols[i];
                    if (c == null) { w[i] = Sc(60f); continue; }
                    float tw = 0f;
                    try { tw = Label.CalcSize(new GUIContent(T(c.TitleZh, c.TitleEn))).x; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    w[i] = Mathf.Max(Sc(c.MinW), tw + Sc(c.PadW));
                }
                return w;
            }
            public static float TableHeaderH() { return Sc(24f); }
            public static float TableGripH() { return Sc(4f); }
            public static float TableGripGap() { return Sc(2f); }
            public static float TableScrollbarW() { return Sc(13f); }
            public static float TableMinH() { return Sc(120f); }
            // 列表高度自适应 / 自校准 / 一体化滚动条。
            public static float TableAutoFit(float contentBottomY, float currentY, float minH, ref float bias) {
                return SRApi.TableAutoFit(contentBottomY, currentY, minH, ref bias);
            }
            public static void TableAfterDraw(float contentBottomY, float drawnBottom, ref float bias) {
                SRApi.TableAfterDraw(contentBottomY, drawnBottom, ref bias);
            }
            public static bool TableScrollbarFor(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
                return SRApi.TableScrollbarFor(a, e, ref dragging, ref grabY, ref scrollY);
            }
            // 列表可用高度：内容底界 - 页面起点，再按自定义高度夹取（entry 值 0 = 自动）。
            // 可用高度与内容高度取小；fillViewport = true 时始终填满，false 按内容或配置固定。
            public static float ListTableHeight(float pageBottomY, float pageTopY, ConfigEntryBase heightEntry,
                                                float minH, int rowCount, float rowH, float headerH, float gripH,
                                                bool fillViewport = false) {
                try {
                    float avail = TableFitHeight(pageBottomY, pageTopY, heightEntry, minH);
                    if (fillViewport) return Mathf.Max(minH, avail);
                    float content = headerH + Mathf.Max(0, rowCount) * rowH + Mathf.Max(0f, gripH);
                    float h = Mathf.Min(avail, content);
                    // 配置 > 0 时以配置为准（不超过可用高度）。
                    try {
                        if (heightEntry != null) {
                            int v = System.Convert.ToInt32(heightEntry.BoxedValue);
                            if (v > 0) h = Mathf.Min(avail, Sc(v));
                        }
                    } catch { /* 读不到配置就按自适应 */ }
                    return Mathf.Max(minH, h);
                } catch { return minH; }
            }

            public static float TableFitHeight(float pageBottomY, float pageTopY, ConfigEntryBase heightEntry, float minH) {
                float used = Mathf.Max(0f, pageBottomY - pageTopY);
                float h = Mathf.Max(minH, SRApi.PageViewportHeight - used);
                if (heightEntry != null) {
                    try { int v = System.Convert.ToInt32(heightEntry.BoxedValue); if (v > 0) h = Mathf.Min(h, Sc(v)); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
                return h;
            }
            public static void TableHeightGripBar(ConfigEntryBase heightEntry, float autoH, int min, int max) {
                GUILayout.Space(TableGripGap());
                GUILayout.Box(GUIContent.none, SRApi.Ctl.Footer, GUILayout.Height(TableGripH()), GUILayout.ExpandWidth(true));
                Rect grip = GUILayoutUtility.GetLastRect();
                TableHeightGrip(heightEntry, grip, autoH, min, max);
            }
            // 底边拖拽改高度并持久化；grip 为可拖区域，autoH 为当前自动高度。
            public delegate void TableHeightGripFn(ConfigEntryBase entry, Rect grip, float autoH, int min, int max);
            private static object _tableHeightGrip;
            public static void SetTableHeightGripSink(TableHeightGripFn f) { _tableHeightGrip = f; }
            public static void TableHeightGrip(ConfigEntryBase entry, Rect grip, float autoH, int min, int max) {
                TableHeightGripFn f = _tableHeightGrip as TableHeightGripFn;
                if (f != null) f(entry, grip, autoH, min, max);
            }
            public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey) { Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> f = _restoreLabel; if (f != null) f(content, entry, w, h, hotkey); }
            public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h) { RestoreLabel(content, entry, w, h, null); }
            public static void RenderControl(ConfigEntryBase entry) { Action<ConfigEntryBase> f = _renderControl; if (f != null) f(entry); }
            // 统一复选框：功能层与 EX 一律走这里，不要自己拼"✓"按钮。
            private static Func<Rect, bool, string, bool> _checkboxAt;
            public static void SetCheckboxSink(Func<Rect, bool, string, bool> checkboxAt) { _checkboxAt = checkboxAt; }
        public static bool Checkbox(Rect r, bool value, string tip) {
                Func<Rect, bool, string, bool> f = _checkboxAt;
                return f != null && f(r, value, tip);
            }
            public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { Action<ConfigEntryBase, bool, float> f = _renderEntryRow; if (f != null) f(entry, isInternal, colWidth); }
            public static ConfigEntryBase FindEntry(string section, string key) { Func<string, string, ConfigEntryBase> f = _findEntry; return f != null ? f(section, key) : null; }
            public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries; return f != null ? f(section) : null; }
            public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) { Action<ConfigEntryBase, float> f = _drawEntryRow; if (f != null) f(entry, colWidth); }
            public static void DrawEntryRow(ConfigEntryBase entry) { DrawEntryRow(entry, 0f); }
            public static float TextHeight(string text, float width) { Func<string, float, float> f = _textHeight; return f != null ? f(text, width) : 0f; }
            public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep) { Func<Rect, float, float, float, bool, float> f = _drawSlider; return f != null ? f(rect, value, min, max, intStep) : value; }
            public static float DrawSlider(Rect rect, float value, float min, float max) { return DrawSlider(rect, value, min, max, false); }
            // width <= 0 = 自适应，夹在固定下限与可用宽度 60% 之间。
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width) { ComboBoxFn f = _comboBox; return f != null ? f(entry, current, vals, options, ref open, width) : 0; }
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open) { return ComboBox(entry, current, vals, options, ref open, -1f); }
        // 筛选下拉统一实现：候选、标签、值读写、展开态、默认回退都在一处；当前值不在候选里时写回 fallback。
        public static void FilterCombo(string tag, string[] vals, string[] labels,
                Func<string> get, Action<string> set, ref bool open, string fallback, float width) {
            try {
                if (vals == null || labels == null || vals.Length == 0 || vals.Length != labels.Length) return;
                string curVal = get != null ? get() : null;
                if (!string.IsNullOrEmpty(curVal) && Array.IndexOf(vals, curVal) < 0) { if (set != null) set(fallback); curVal = fallback; }
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], curVal, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                cur = Mathf.Clamp(cur, 0, labels.Length - 1);
                bool o = open;
                int sel = ComboBox(null, labels[cur], vals, labels, ref o, width);
                open = o;
                if (sel >= 0 && sel < vals.Length && set != null) set(vals[sel]);
            } catch { /* 静默：下拉渲染失败不影响本帧其它控件 */ }
        }

            public static float SidebarWidth() { Func<float> f = _sidebarWidth; return f != null ? f() : 0f; }
            public static float WinWidth { get { Func<float> f = _winWidth; return f != null ? f() : 0f; } }
            public static void SetValue(ConfigEntryBase entry, object value) { Action<ConfigEntryBase, object> f = _setValue; if (f != null) f(entry, value); }
            // 一排等宽按钮（功能页工具栏用）：返回被点击的下标，-1 = 没有。宽度 <=0 时按文字自适应。
            private static Func<string[], float[], float, int> _buttonRow;
            public static void SetButtonRowSink(Func<string[], float[], float, int> f) { _buttonRow = f; }
            public static int ButtonRow(string[] labels, float[] widths, float height) {
                Func<string[], float[], float, int> f = _buttonRow;
                return f != null ? f(labels, widths, height) : -1;
            }
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width, bool bindable) { Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> f = _hotkeyActionButton; return f != null && f(label, tooltip, keyEntry, width, bindable); }
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry) { return HotkeyActionButton(label, tooltip, keyEntry, 0f, false); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width, float height) { Func<string, string, string, Action, float, float, bool> f = _hotkeyButton; return f != null && f(id, label, tooltip, action, width, height); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action) { return HotkeyButton(id, label, tooltip, action, 0f, 0f); }
            public delegate bool HotkeyButtonNoHintFn(string id, string label, string tooltip, Action action, float width, float height);
            private static object _hotkeyButtonNoHint;
            public static void SetHotkeyButtonNoHintSink(HotkeyButtonNoHintFn f) { _hotkeyButtonNoHint = f; }
            public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width, float height) {
                HotkeyButtonNoHintFn f = _hotkeyButtonNoHint as HotkeyButtonNoHintFn;
                return f != null && f(id, label, tooltip, action, width, height);
            }
            public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action) { return HotkeyButtonNoHint(id, label, tooltip, action, 0f, 0f); }
            private static GUIStyle Style(string name) { Func<string, GUIStyle> f = _style; return f != null ? f(name) : null; }
            // 自动开关按钮（勾选框 + 右键绑键），供独立程序集使用。
            public delegate bool SelfToggleButtonFn(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width, bool bindable);
            private static SelfToggleButtonFn _selfToggleButton;
            public static void SetSelfToggleButtonSink(SelfToggleButtonFn f) { _selfToggleButton = f; }
            public static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
                SelfToggleButtonFn f = _selfToggleButton;
                return f != null && f(label, tooltip, on, keyEntry, width, bindable);
            }

            private static Func<GUIStyle> _getSelItem;
            public static void SetSelItemSink(Func<GUIStyle> f) { _getSelItem = f; }

            // ---- 统一行控件：功能页不必各写一份"标签 + 控件"排版；标签宽按文字实测以保证左缘对齐 ----
            private static Action<string, string, ConfigEntryBase, float> _labeledRow;
            public static void SetLabeledRowSink(Action<string, string, ConfigEntryBase, float> f) { _labeledRow = f; }
            public static void LabeledRow(string label, string tip, ConfigEntryBase e, float labelWidth = 0f) {
                Action<string, string, ConfigEntryBase, float> f = _labeledRow;
                if (f != null) f(label, tip, e, labelWidth);
            }

            // 统一入口：按控件类型自动选控件的条目行。
            public static void Row(string label, string tip, ConfigEntryBase e, float labelWidth = 0f) {
                LabeledRow(label, tip, e, labelWidth);
            }

            // 带标签的下拉行：entry 作为稳定 ID 管展开态，选中即写值并收起。
            private static Func<string, string, ConfigEntryBase, string[], string[], float, float, int> _rowCombo;
            public static void SetRowComboSink(Func<string, string, ConfigEntryBase, string[], string[], float, float, int> f) { _rowCombo = f; }
            public static int RowCombo(string label, string tip, ConfigEntryBase e, string[] vals, string[] labels, float width = 160f, float labelWidth = 0f) {
                Func<string, string, ConfigEntryBase, string[], string[], float, float, int> f = _rowCombo;
                return f != null ? f(label, tip, e, vals, labels, width, labelWidth) : -1;
            }

            // 带标签的滑块行：统一 0.1 步进、数值右对齐。
            private static Func<string, string, ConfigEntryBase, float, float, float, string, bool> _rowSlider;
            public static void SetRowSliderSink(Func<string, string, ConfigEntryBase, float, float, float, string, bool> f) { _rowSlider = f; }
            public static bool RowSlider(string label, string tip, ConfigEntryBase e, float min, float max, string fmt = "0.0", float labelWidth = 0f) {
                Func<string, string, ConfigEntryBase, float, float, float, string, bool> f = _rowSlider;
                return f != null && f(label, tip, e, min, max, labelWidth, fmt);
            }
            public static GUIStyle SelItem { get { Func<GUIStyle> f = _getSelItem; return f != null ? f() : null; } }

            private static Func<GUIStyle> _getCheckOn, _getCheckOff;
            public static void SetCheckStylesSink(Func<GUIStyle> on, Func<GUIStyle> off) { _getCheckOn = on; _getCheckOff = off; }
            public static GUIStyle CheckOn { get { Func<GUIStyle> f = _getCheckOn; return f != null ? f() : null; } }
            public static GUIStyle CheckOff { get { Func<GUIStyle> f = _getCheckOff; return f != null ? f() : null; } }

            private static Func<bool> _getForceZh;
            private static Action<bool> _setForceZh;
            public static void SetForceZhSink(Func<bool> get, Action<bool> set) { _getForceZh = get; _setForceZh = set; }
            public static bool ForceZh {
                get { Func<bool> f = _getForceZh; return f != null && f(); }
                set { Action<bool> f = _setForceZh; if (f != null) f(value); }
            }

            public static GUIStyle Label { get { return Style("Label"); } }
            public static GUIStyle LabelWrap { get { return Style("LabelWrap"); } }
        public static void LabelAuto(string text) { LabelAuto(text, 0f); }
        public static void LabelAuto(string text, float minWidth) {
            try {
                GUIStyle st = Style("Label");
                GUIContent c = new GUIContent(text ?? "");
                float w = Mathf.Max(minWidth, st.CalcSize(c).x + Sc(4f));
                // 底部 1px 缓冲：CJK / 拉丁字母下沿常被 CalcHeight 少算。
                float h = Mathf.Max(st.CalcSize(c).y, st.CalcHeight(c, w)) + Sc(1f);
                GUILayout.Label(c, st, GUILayout.Width(w), GUILayout.Height(h));
            } catch {
                try { GUILayout.Label(text ?? ""); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
        }
            public static GUIStyle ChatLabel { get { return Style("ChatLabel"); } }
            public static GUIStyle ChatArea { get { return Style("ChatArea"); } }
            public static GUIStyle SecHeader { get { return Style("SecHeader"); } }
        // 分区标题：宽度收缩到文字实际宽度，悬浮提示只在标题文字上生效。
        private static Func<string, string, float, float, bool> _button;
        public static void SetButtonSink(Func<string, string, float, float, bool> f) { _button = f; }
        public static bool Button(string label, string tip, float width = 0f, float height = 0f) {
            Func<string, string, float, float, bool> f = _button;
            return f != null && f(label, tip, width, height);
        }

        private static Func<string, float> _labelCol;
        public static void SetLabelColumnSink(Func<string, float> f) { _labelCol = f; }
        public static float LabelColumnWidth(string label) { Func<string, float> f = _labelCol; return f != null ? f(label) : 0f; }
        public static float WheelTake(Rect layoutRect) { return SRApi.WheelTake(layoutRect); }
        private static Action<string, Rect, Func<float, bool>> _wheelFb;
        public static void SetWheelFallbackSink(Action<string, Rect, Func<float, bool>> f) { _wheelFb = f; }
        // 填充页列表滚动回调：帧尾滚轮没被消费且指针在内容区时交给它；带页面标识避免跨页误滚。
        private static Action<string, Action<float>> _fillScroll;
        private static readonly System.Collections.Generic.List<string> _fillIds = new System.Collections.Generic.List<string>();
        private static readonly System.Collections.Generic.List<Action<float>> _fillHandlers = new System.Collections.Generic.List<Action<float>>();
        public static void SetFillScrollSink(Action<string, Action<float>> f) { _fillScroll = f; }
        public static void RegisterFillScroll(string pageId, Action<float> scroll) {
            Action<string, Action<float>> f = _fillScroll;
            if (f != null) f(pageId ?? "", scroll);
        }
        public static bool FillScrollAll(string pageId, float wheel) {
            if (string.IsNullOrEmpty(pageId)) return false;
            for (int i = 0; i < _fillHandlers.Count; i++) {
                if (!string.Equals(_fillIds[i], pageId, StringComparison.Ordinal)) continue;
                try { _fillHandlers[i](wheel); return true; } catch { /* 静默：单个页面失败不影响其它 */ }
            }
            return false;
        }
        internal static void AddFillHandler(string id, Action<float> h) {
            if (h == null) return;
            _fillIds.Add(id ?? ""); _fillHandlers.Add(h);
        }

        private static object _wheelPoll;
        public static void SetWheelPollSink(Func<string, Rect, float> f) { _wheelPoll = f; }
        public static float WheelPoll(string key, Rect rect) {
            Func<string, Rect, float> f = _wheelPoll as Func<string, Rect, float>;
            return f != null ? f(key, rect) : 0f;
        }

        public static void WheelFallback(string key, Rect layoutRect, Func<float, bool> apply) {
            Action<string, Rect, Func<float, bool>> f = _wheelFb;
            if (f != null) f(key, layoutRect, apply);
        }
        private static Action<string> _tableHighlight;
        public static void SetTableHighlightSink(Action<string> f) { _tableHighlight = f; }
        public static void SetTableHighlight(string query) { Action<string> f = _tableHighlight; if (f != null) f(query ?? ""); }

        // ================= 作用域化 GUI 状态（用法：using (SRApi.Ctl.Disabled(cond)) { ... }） =================
        // 手写 try/finally 还原 GUI.enabled 漏一次就会让整页变灰。
        public sealed class GuiScope : System.IDisposable {
            private readonly bool _enabled;
            private readonly bool _hasColor;
            private readonly Color _color;
            internal GuiScope(bool enabled, bool hasColor, Color color) {
                _enabled = GUI.enabled; _hasColor = hasColor; _color = GUI.color;
                if (!enabled) GUI.enabled = false;
                if (hasColor) GUI.color = color;
            }
            public void Dispose() { GUI.enabled = _enabled; if (_hasColor) GUI.color = _color; }
        }
        public static System.IDisposable Disabled(bool disabled) { return new GuiScope(!disabled, false, default(Color)); }
        public static System.IDisposable Tint(Color color) { return new GuiScope(true, true, color); }
        public static System.IDisposable Tint(Color color, bool useIt) { return new GuiScope(true, useIt, color); }

        public static void SectionTitle(string title, string tip = null) {
            // 控件创建不能放在 try 里：Layout 与 Repaint 建的数量不一致会抛 group 位置异常，故只在 try 里算尺寸。
            GUIContent c = new GUIContent(title ?? "", tip);
            GUIStyle st = SecHeader;
            Vector2 sz;
            try { sz = st.CalcSize(c); } catch { sz = new Vector2(Sc(120f), Sc(20f)); }
            if (sz.x <= 0f) sz.x = Sc(120f);
            if (sz.y <= 0f) sz.y = Sc(20f);
            // 缓冲做进标题自身高度：用 GUILayout.Space 追加会被紧邻条目吃掉。
            GUILayout.Space(Sc(2));
            GUILayout.Label(c, st, GUILayout.Width(sz.x), GUILayout.Height(sz.y + Sc(2)));
        }
            public static GUIStyle Btn { get { return Style("Btn"); } }
            public static GUIStyle BtnClip { get { return Style("BtnClip"); } }
            public static GUIStyle SearchBox { get { return Style("SearchBox"); } }

            // 按 (文本|提示) 缓存 GUIContent，避免每帧堆分配；上限 512 条超出清空。
            private static readonly Dictionary<string, GUIContent> _contentCache = new Dictionary<string, GUIContent>(64);
            // 悬浮文本兜底规范化：去首尾空行、折叠连续空行，避免悬浮框顶部多空行。
            public static string CleanTip(string tip) {
                if (string.IsNullOrEmpty(tip)) return tip;
                try {
                    string[] lines = tip.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    int a = 0, b = lines.Length - 1;
                    while (a <= b && (lines[a] == null || lines[a].Trim().Length == 0)) a++;
                    while (b >= a && (lines[b] == null || lines[b].Trim().Length == 0)) b--;
                    if (a == 0 && b == lines.Length - 1) return tip;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(tip.Length);
                    for (int i = a; i <= b; i++) {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(lines[i] ?? "");
                    }
                    return sb.ToString();
                } catch { return tip; }
            }

            public static GUIContent Content(string text, string tip) {
                MainThread.Assert("Ctl.Content");
                string key = text ?? "";
                if (!string.IsNullOrEmpty(tip)) key = key + "\u0001" + tip;
                GUIContent c;
                if (_contentCache.TryGetValue(key, out c)) return c;
                tip = CleanTip(tip);
                if (_contentCache.Count >= 512) _contentCache.Clear();
                c = new GUIContent(text ?? "", tip);
                _contentCache[key] = c;
                return c;
            }
            public static GUIContent Content(string text) { return Content(text, null); }
            public static GUIStyle Footer { get { return Style("Footer"); } }
            public static bool LangEn { get { Func<bool> f = _langEn; return f != null && f(); } }
            public static Dictionary<ConfigEntryBase, bool> EditOpen { get { Func<Dictionary<ConfigEntryBase, bool>> f = _editOpen; return f != null ? f() : null; } }
            public static void ScrollToBottom() { Action a = _scrollToBottom; if (a != null) a(); }
            public static void NoteEntryBound() { Action a = _noteEntryBound; if (a != null) a(); }
            public static void MarkConfigDirty(ConfigFile cfg) { Action<ConfigFile> f = _markConfigDirty; if (f != null) f(cfg); }

            // ---- 颜色条目：统一调色板；值格式 "#RRGGBB"，展开 / 拖动状态全部在核心按 entry 保存 ----
            public delegate bool ColorEditFn(GUIContent label, ConfigEntryBase entry, float width, float height);
            public delegate bool ColorSwatchFn(Rect rect, ConfigEntryBase entry);
            public delegate void ColorPanelFn(ConfigEntryBase entry);
            public delegate Color? ColorPreviewFn(ConfigEntryBase entry);
            public delegate void ColorResetFn(ConfigEntryBase entry);
            private static object _colorEdit, _colorSwatch, _colorPanel, _colorPreview, _colorReset;
            private static Func<string, string, string, bool> _sectionBegin;
            private static Action _sectionEnd;
            public static void WireSection(Func<string, string, string, bool> begin, Action end) { _sectionBegin = begin; _sectionEnd = end; }
            public static void WireColorPicker(ColorEditFn edit, ColorSwatchFn swatch, ColorPanelFn panel,
                                               ColorPreviewFn preview, ColorResetFn reset) {
                _colorEdit = edit; _colorSwatch = swatch; _colorPanel = panel; _colorPreview = preview; _colorReset = reset;
            }
            // 颜色条目（标签 + 色块 + 展开调色板）：返回本帧是否改动了颜色。
            public static bool ColorEdit(GUIContent label, ConfigEntryBase entry, float width, float height) {
                ColorEditFn f = _colorEdit as ColorEditFn;
                return f != null && f(label, entry, width, height);
            }
            public static bool ColorSwatch(Rect rect, ConfigEntryBase entry) {
                ColorSwatchFn f = _colorSwatch as ColorSwatchFn;
                return f != null && f(rect, entry);
            }
            public static void ColorPanel(ConfigEntryBase entry) {
                ColorPanelFn f = _colorPanel as ColorPanelFn;
                if (f != null) f(entry);
            }
            public static Color? PreviewColor(ConfigEntryBase entry) {
                ColorPreviewFn f = _colorPreview as ColorPreviewFn;
                if (f == null) return null;
                return f(entry);
            }
            public static void ResetColor(ConfigEntryBase entry) {
                ColorResetFn f = _colorReset as ColorResetFn;
                if (f != null) f(entry);
            }
        }
    }
}
