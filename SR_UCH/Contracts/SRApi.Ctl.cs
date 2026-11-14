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
            // 表格自适应高度助手（Ctl 侧别名；功能层统一从这里取）。
            public static float TableAutoHeight(float usedAbove, float minH) { return SRApi.TableAutoHeight(usedAbove, minH); }
            // 表格尺寸令牌（随 UI 缩放；页面不再写死 26/24/13 等字面量）。
            // 表格滚动几何（纯函数，单测直接覆盖）：行区与纵向滚动上限。
        public static float TableRowsH(float tableH, float headerH) {
            return Mathf.Max(0f, tableH - headerH);
        }
        public static float TableMaxScrollY(int rows, float rowH, float rowsH) {
            float content = Mathf.Max(0, rows) * Mathf.Max(0f, rowH);
            return Mathf.Max(0f, content - Mathf.Max(0f, rowsH));
        }

        // 自绘列表的**唯一**滚动几何：把"表格框"收敛成"屏幕真实可见的行区"。
        // 根因背景：核心所有相位（行矩形/绘制/滚动上限）都用 rowsH 当视口高，
        // 而 rowsH 若直接取 box.height - headerH，box 一旦伸到屏幕外就会虚高 →
        // 行被画到屏幕外、maxY 被压成 0、滚轮彻底失效。
        // visibleBottom 用「右栏高度 − 底栏高度」（与 box/probe 同一坐标系）。
        public static float ScrollRowsH(Rect box, float headerH, float visibleBottom, float rowH) {
            float rowsTop = box.y + headerH;
            float raw = Mathf.Max(rowH, box.height - headerH);
            float avail = Mathf.Max(rowsTop + rowH, visibleBottom) - rowsTop;
            return Mathf.Max(rowH, Mathf.Min(raw, avail));
        }
        // 收敛后行区对应的滚动上限（与 TableMaxScrollY 同一公式，只是把收敛逻辑收在一处）。
        public static float ScrollMaxY(int rows, float rowH, float rowsH) {
            return TableMaxScrollY(rows, rowH, rowsH);
        }

        public static float TableRowH() { return Sc(26f); }
            // UI 主题令牌（功能层统一入口）。
            public static float ThemeRadius { get { return Sc(6f); } }
            public static float ThemeHoverFade { get { return 0.12f; } }
            public static Color ThemeFooterBg { get { return new Color(0.10f, 0.11f, 0.13f, 0.96f); } }
            public static Color ThemePanelBg { get { return new Color(0.08f, 0.08f, 0.09f, 0.96f); } }
            // 按列模板与表头文字算默认列宽。
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
            // 列表高度自适应 / 自校准 / 一体化滚动条（功能层统一入口）。
            public static float TableAutoFit(float contentBottomY, float currentY, float minH, ref float bias) {
                return SRApi.TableAutoFit(contentBottomY, currentY, minH, ref bias);
            }
            public static void TableAfterDraw(float contentBottomY, float drawnBottom, ref float bias) {
                SRApi.TableAfterDraw(contentBottomY, drawnBottom, ref bias);
            }
            public static bool TableScrollbarFor(TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
                return SRApi.TableScrollbarFor(a, e, ref dragging, ref grabY, ref scrollY);
            }
            // 列表可用高度：内容底界(pageBottomY) - 页面起点(pageTopY)，再按用户自定义高度夹取（entry 值 0 = 自动）。
            // 列表自适应高度（核心化）：可用高度与"内容高度"取小 —— 行少时不留空表格体，
            // 行多时才填满并滚动。所有列表都应走这一个出��口，避免各页自己算高度。
            // fillViewport = true：底部贴着页面底的列表（如联机房间列表）始终填满可用高度，
            // 随界面大小自适应；false：按内容自适应，或按 Table Height 配置固定（>0 生效）。
            public static float ListTableHeight(float pageBottomY, float pageTopY, ConfigEntryBase heightEntry,
                                                float minH, int rowCount, float rowH, float headerH, float gripH,
                                                bool fillViewport = false) {
                try {
                    float avail = TableFitHeight(pageBottomY, pageTopY, heightEntry, minH);
                    if (fillViewport) return Mathf.Max(minH, avail);
                    float content = headerH + Mathf.Max(0, rowCount) * rowH + Mathf.Max(0f, gripH);
                    float h = Mathf.Min(avail, content);
                    // 固定高度模式：配置 > 0 时以配置为准（仍不超过可用高度）。
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
            // 表格底边拖拽条：间隙 + 4px 条 + 拖动改高度并持久化（两个列表同一外观与行为）。
            public static void TableHeightGripBar(ConfigEntryBase heightEntry, float autoH, int min, int max) {
                GUILayout.Space(TableGripGap());
                GUILayout.Box(GUIContent.none, SRApi.Ctl.Footer, GUILayout.Height(TableGripH()), GUILayout.ExpandWidth(true));
                Rect grip = GUILayoutUtility.GetLastRect();
                TableHeightGrip(heightEntry, grip, autoH, min, max);
            }
            // 表格底边拖拽改高度并持久化（entry 为 0 表示自动高度）。grip 为可拖区域，autoH 为当前自动高度（设计单位）。
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
            // 统一复选框（绝对矩形，设置页同款外观 + 勾选贴图）：功能层与 EX 一律走这里，
            // 不要再自己拿 CheckOn/CheckOff 拼 "✓" 按钮，否则勾选外观会与设置页不一致。
            private static Func<Rect, bool, string, bool> _checkboxAt;
            public static void SetCheckboxSink(Func<Rect, bool, string, bool> checkboxAt) { _checkboxAt = checkboxAt; }
            // 对外契约 API：外部 DLL 自绘勾选框用；目前无调用方属正常态。
        public static bool Checkbox(Rect r, bool value, string tip) {
                Func<Rect, bool, string, bool> f = _checkboxAt;
                return f != null && f(r, value, tip);
            }
            public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { Action<ConfigEntryBase, bool, float> f = _renderEntryRow; if (f != null) f(entry, isInternal, colWidth); }
            public static ConfigEntryBase FindEntry(string section, string key) { Func<string, string, ConfigEntryBase> f = _findEntry; return f != null ? f(section, key) : null; }
            public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries; return f != null ? f(section) : null; }
            public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) { Action<ConfigEntryBase, float> f = _drawEntryRow; if (f != null) f(entry, colWidth); }
            // 默认宽度版本：功能页不用再各写一份 CfgRow(e){ DrawEntryRow(e, Sc(180)) }。
            public static void DrawEntryRow(ConfigEntryBase entry) { DrawEntryRow(entry, 0f); }
            public static float TextHeight(string text, float width) { Func<string, float, float> f = _textHeight; return f != null ? f(text, width) : 0f; }
            public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep) { Func<Rect, float, float, float, bool, float> f = _drawSlider; return f != null ? f(rect, value, min, max, intStep) : value; }
            public static float DrawSlider(Rect rect, float value, float min, float max) { return DrawSlider(rect, value, min, max, false); }
            // width <= 0 = 自适应：按全部选项 / 当前显示文本的最大宽度 + 箭头内边距，并夹在固定下限与可用宽度 60% 上限之间。
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width) { ComboBoxFn f = _comboBox; return f != null ? f(entry, current, vals, options, ref open, width) : 0; }
            // 省略宽度 = 自适应（等价 width <= 0）。
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open) { return ComboBox(entry, current, vals, options, ref open, -1f); }
        // 筛选下拉的统一实现：候选、标签、当前值 get/set、展开态、默认回退都在一处。
        // 页面只需描述"有哪些候选、怎么读/写当前值"，不再各抄一份"找当前项 + ComboBox + 落值"。
        // 规则：当前值不在候选里 → 写回 fallback（避免运行期状态被改坏后下拉显示空白）。
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
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width, bool bindable) { Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> f = _hotkeyActionButton; return f != null && f(label, tooltip, keyEntry, width, bindable); }
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry) { return HotkeyActionButton(label, tooltip, keyEntry, 0f, false); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width, float height) { Func<string, string, string, Action, float, float, bool> f = _hotkeyButton; return f != null && f(id, label, tooltip, action, width, height); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action) { return HotkeyButton(id, label, tooltip, action, 0f, 0f); }
            // 与 HotkeyButton 相同（右键可绑键），但悬浮提示不追加"右键：绑定/删除快捷键"这一行。
            public delegate bool HotkeyButtonNoHintFn(string id, string label, string tooltip, Action action, float width, float height);
            private static object _hotkeyButtonNoHint;
            public static void SetHotkeyButtonNoHintSink(HotkeyButtonNoHintFn f) { _hotkeyButtonNoHint = f; }
            public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width, float height) {
                HotkeyButtonNoHintFn f = _hotkeyButtonNoHint as HotkeyButtonNoHintFn;
                return f != null && f(id, label, tooltip, action, width, height);
            }
            public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action) { return HotkeyButtonNoHint(id, label, tooltip, action, 0f, 0f); }
            private static GUIStyle Style(string name) { Func<string, GUIStyle> f = _style; return f != null ? f(name) : null; }
            // 自动开关按钮（带勾选框样式 + 右键绑键）：独立程序集用它，避免直接调 SR.Ctl（需求 12）。
            public delegate bool SelfToggleButtonFn(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width, bool bindable);
            private static SelfToggleButtonFn _selfToggleButton;
            public static void SetSelfToggleButtonSink(SelfToggleButtonFn f) { _selfToggleButton = f; }
            public static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
                SelfToggleButtonFn f = _selfToggleButton;
                return f != null && f(label, tooltip, on, keyEntry, width, bindable);
            }

            // 选中态按钮样式（EX 的总开关等自绘按钮用）：与侧栏选中项同款。
            private static Func<GUIStyle> _getSelItem;
            public static void SetSelItemSink(Func<GUIStyle> f) { _getSelItem = f; }

            // ---- 统一行控件：功能页不再各写一份"标签 + 控件"排版 ----
            // 标签宽度按文字实测（与 RestoreLabel 同一套），输入框/开关左边缘在所有页面里对齐。
            private static Action<string, string, ConfigEntryBase, float> _labeledRow;
            public static void SetLabeledRowSink(Action<string, string, ConfigEntryBase, float> f) { _labeledRow = f; }
            public static void LabeledRow(string label, string tip, ConfigEntryBase e, float labelWidth = 0f) {
                Action<string, string, ConfigEntryBase, float> f = _labeledRow;
                if (f != null) f(label, tip, e, labelWidth);
            }

            // 统一入口：Row = 带标签的条目行（按控件类型自动选控件），新页面只用这一个 API。
            public static void Row(string label, string tip, ConfigEntryBase e, float labelWidth = 0f) {
                LabeledRow(label, tip, e, labelWidth);
            }

            // 带标签的下拉行：entry 作为稳定 ID 交给核心管理展开态；选中即写值并收起。
            private static Func<string, string, ConfigEntryBase, string[], string[], float, float, int> _rowCombo;
            public static void SetRowComboSink(Func<string, string, ConfigEntryBase, string[], string[], float, float, int> f) { _rowCombo = f; }
            public static int RowCombo(string label, string tip, ConfigEntryBase e, string[] vals, string[] labels, float width = 160f, float labelWidth = 0f) {
                Func<string, string, ConfigEntryBase, string[], string[], float, float, int> f = _rowCombo;
                return f != null ? f(label, tip, e, vals, labels, width, labelWidth) : -1;
            }

            // 带标签的滑块行：统一 0.1 步进、数值右对齐（以前每个页面各抄一份，格式与宽度容易走样）。
            private static Func<string, string, ConfigEntryBase, float, float, float, string, bool> _rowSlider;
            public static void SetRowSliderSink(Func<string, string, ConfigEntryBase, float, float, float, string, bool> f) { _rowSlider = f; }
            // 返回 true 表示本帧用户拖动并写入了新值
            public static bool RowSlider(string label, string tip, ConfigEntryBase e, float min, float max, string fmt = "0.0", float labelWidth = 0f) {
                Func<string, string, ConfigEntryBase, float, float, float, string, bool> f = _rowSlider;
                return f != null && f(label, tip, e, min, max, labelWidth, fmt);
            }
            public static GUIStyle SelItem { get { Func<GUIStyle> f = _getSelItem; return f != null ? f() : null; } }

            // 勾选样式（自绘行 / 表格钉住列用）：与设置页完全同款。
            private static Func<GUIStyle> _getCheckOn, _getCheckOff;
            public static void SetCheckStylesSink(Func<GUIStyle> on, Func<GUIStyle> off) { _getCheckOn = on; _getCheckOff = off; }
            public static GUIStyle CheckOn { get { Func<GUIStyle> f = _getCheckOn; return f != null ? f() : null; } }
            public static GUIStyle CheckOff { get { Func<GUIStyle> f = _getCheckOff; return f != null ? f() : null; } }

            // 强制中文渲染（独立程序集 X 页用：这些页面不走语言切换）。以前 EX 直接摸 SR.Ctl.ForceZh，
            // 那是实现层；现在收敛到契约层（需求 12 统一控件）。
            private static Func<bool> _getForceZh;
            private static Action<bool> _setForceZh;
            public static void SetForceZhSink(Func<bool> get, Action<bool> set) { _getForceZh = get; _setForceZh = set; }
            public static bool ForceZh {
                get { Func<bool> f = _getForceZh; return f != null && f(); }
                set { Action<bool> f = _setForceZh; if (f != null) f(value); }
            }

            public static GUIStyle Label { get { return Style("Label"); } }
            public static GUIStyle LabelWrap { get { return Style("LabelWrap"); } }
        // 自适应标签：按文本实测宽高绘制（宽度=文本宽+2，高度=CalcHeight），避免固定高度把文字下半截裁掉。
        // 功能页统一用它替代 GUILayout.Label(..., GUILayout.Height(固定值))。
        public static void LabelAuto(string text) { LabelAuto(text, 0f); }
        public static void LabelAuto(string text, float minWidth) {
            try {
                GUIStyle st = Style("Label");
                GUIContent c = new GUIContent(text ?? "");
                // 左右各留 2px（随 UI 缩放）：与按钮/输入框的 FramePadding 同一节奏，文字不会被裁边。
                float w = Mathf.Max(minWidth, st.CalcSize(c).x + Sc(4f));
                // 底部额外 1px 缓冲（统一规则）：CJK / 拉丁字母的下沿常被 CalcHeight 少算一点，
                // 只给样式加 padding 在"显式指定高度"的地方仍会被裁，所以按文字算高时也补 1px。
                float h = Mathf.Max(st.CalcSize(c).y, st.CalcHeight(c, w)) + Sc(1f);
                GUILayout.Label(c, st, GUILayout.Width(w), GUILayout.Height(h));
            } catch {
                try { GUILayout.Label(text ?? ""); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
        }
            public static GUIStyle ChatLabel { get { return Style("ChatLabel"); } }
            public static GUIStyle ChatArea { get { return Style("ChatArea"); } }
            public static GUIStyle SecHeader { get { return Style("SecHeader"); } }
        // 分区标题：宽度收缩到文字实际宽度，悬浮提示只在标题文字上出现（不再整行生效）。
        // 统一按钮：自带悬浮说明（页面里不要再写裸 GUILayout.Button）
        private static Func<string, string, float, float, bool> _button;
        public static void SetButtonSink(Func<string, string, float, float, bool> f) { _button = f; }
        public static bool Button(string label, string tip, float width = 0f, float height = 0f) {
            Func<string, string, float, float, bool> f = _button;
            return f != null && f(label, tip, width, height);
        }

        // 本页标签列宽（按本页实际出现的标签收敛）：自绘行用它对齐，不要自己写死宽度
        private static Func<string, float> _labelCol;
        public static void SetLabelColumnSink(Func<string, float> f) { _labelCol = f; }
        public static float LabelColumnWidth(string label) { Func<string, float> f = _labelCol; return f != null ? f(label) : 0f; }
        // 绘制期取用滚轮量（转发到 SRApi.WheelTake，供页面内控件/表格调用）
        public static float WheelTake(Rect layoutRect) { return SRApi.WheelTake(layoutRect); }
        // 兜底：绘制期取用失败时登记"矩形 + 应用函数"，帧尾若还有滚轮量再命中一次
        private static Action<string, Rect, Func<float, bool>> _wheelFb;
        public static void SetWheelFallbackSink(Action<string, Rect, Func<float, bool>> f) { _wheelFb = f; }
        // 填充页的列表滚动回调：页面注册一次，帧尾若滚轮没被任何控件/钩子消费，
        // 且指针在内容区，就直接交给它（不依赖命中框，保证列表一定能滚）。
        // 带页面标识：只有当前页面的回调会被调用（审查 P1-1：以前会跨页面误滚联机列表）
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

        // 存 object 而非委托：注入点在 SR.Core 的 ManagerUI.Initialize（晚于 MainPlugin 启动期的
        // SRApi.MissingSinks() 自检），用委托字段会被误报为"未注入"（与颜色面板同一处理）。
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
        // 表格搜索高亮：页面每帧传入当前搜索串（空串 = 不高亮）
        private static Action<string> _tableHighlight;
        public static void SetTableHighlightSink(Action<string> f) { _tableHighlight = f; }
        public static void SetTableHighlight(string query) { Action<string> f = _tableHighlight; if (f != null) f(query ?? ""); }

        // ================= 作用域化 GUI 状态（对齐 ImGui 的 BeginDisabled/PushStyleColor） =================
        // 用法：using (SRApi.Ctl.Disabled(cond)) { ... }
        // 以前到处手写 bool old = GUI.enabled; try { } finally { GUI.enabled = old; }，
        // 漏写一次就会让后面整页变灰 —— 作用域写法不可能漏。
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
            // 控件创建不能放在 try 里：Layout 建了 2 个、Repaint 走 catch 建 1 个，
            // 就会抛 "Getting control N's position in a group with only N controls"。
            // 只在 try 里算尺寸，控件无条件建（失败退回一个固定尺寸）。
            GUIContent c = new GUIContent(title ?? "", tip);
            GUIStyle st = SecHeader;
            Vector2 sz;
            try { sz = st.CalcSize(c); } catch { sz = new Vector2(Sc(120f), Sc(20f)); }
            if (sz.x <= 0f) sz.x = Sc(120f);
            if (sz.y <= 0f) sz.y = Sc(20f);
            // 缓冲做进标题自身高度（以前用 GUILayout.Space 追加，紧邻的条目会把这点间距吃掉，
            // 观感上看不到 —— 用户反馈 10）。这里高度 = 文字高 + 2px，缓冲一定占位。
            GUILayout.Space(Sc(2));
            GUILayout.Label(c, st, GUILayout.Width(sz.x), GUILayout.Height(sz.y + Sc(2)));
        }
            public static GUIStyle Btn { get { return Style("Btn"); } }
            public static GUIStyle BtnClip { get { return Style("BtnClip"); } }
            public static GUIStyle SearchBox { get { return Style("SearchBox"); } }

            // GUIContent 是 class（每帧 new 就是每帧一次堆分配）。这里按 (文本|提示) 缓存实例：
            // 文案不变时零分配；语言切换/文案变化时 key 变化，自动重建。上限 512 条，超了整体清空。
            private static readonly Dictionary<string, GUIContent> _contentCache = new Dictionary<string, GUIContent>(64);
            // 悬浮条目文本的兜底规范化：去掉首尾空行、折叠连续空行。
            // 功能页自建的 GUIContent 不经过核心的 ComposeTip，这里再兜一层，
            // 避免"第一行是空行导致悬浮框顶部多出一条空行"（用户反馈 A）。
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

            // ---- 颜色条目：统一调色板（预览 + 24 色网格 + H 变体条 + H/S/V 滑块 + RGB/Hex + 复制/粘贴/还原默认）----
            // 值格式 "#RRGGBB"。展开、拖动、输入等状态全部在核心按 ConfigEntryBase 保存，功能页不持有任何调色板状态；
            // 写盘走 SetValue + MarkConfigDirty（节流落盘），拖动/输入过程中只改"预览色"，由 PreviewColor 供功能页取用。
            public delegate bool ColorEditFn(GUIContent label, ConfigEntryBase entry, float width, float height);
            public delegate bool ColorSwatchFn(Rect rect, ConfigEntryBase entry);
            public delegate void ColorPanelFn(ConfigEntryBase entry);
            public delegate Color? ColorPreviewFn(ConfigEntryBase entry);
            public delegate void ColorResetFn(ConfigEntryBase entry);
            // 存 object 而非委托：注入点在 SR.Ctl 首次被使用（早于任何页面渲染，但晚于 MainPlugin 的缺失通道自检），
            // 用委托字段会被 SRApi.MissingSinks() 误报为"未注入"（与 TableAutoHeight / HotkeyButtonNoHint 同一处理）。
            private static object _colorEdit, _colorSwatch, _colorPanel, _colorPreview, _colorReset;
            private static Func<string, string, string, bool> _sectionBegin;
            private static Action _sectionEnd;
            public static void WireSection(Func<string, string, string, bool> begin, Action end) { _sectionBegin = begin; _sectionEnd = end; }
            public static void WireColorPicker(ColorEditFn edit, ColorSwatchFn swatch, ColorPanelFn panel,
                                               ColorPreviewFn preview, ColorResetFn reset) {
                _colorEdit = edit; _colorSwatch = swatch; _colorPanel = panel; _colorPreview = preview; _colorReset = reset;
            }
            // 颜色条目（标签 + 色块 + 展开的调色板）：点名称还原默认色，点色块展开/收起；返回本帧是否改动了颜色值。
            public static bool ColorEdit(GUIContent label, ConfigEntryBase entry, float width, float height) {
                ColorEditFn f = _colorEdit as ColorEditFn;
                return f != null && f(label, entry, width, height);
            }
            // 只画一个色块按钮（无标签），返回是否被点击；供自绘行使用。
            public static bool ColorSwatch(Rect rect, ConfigEntryBase entry) {
                ColorSwatchFn f = _colorSwatch as ColorSwatchFn;
                return f != null && f(rect, entry);
            }
            // 自绘行在 EndHorizontal 之后调用：该条目展开时画统一调色板，收起时什么都不画。
            public static void ColorPanel(ConfigEntryBase entry) {
                ColorPanelFn f = _colorPanel as ColorPanelFn;
                if (f != null) f(entry);
            }
            // 未落盘的预览色；null = 没有预览（调用方读配置值）。
            public static Color? PreviewColor(ConfigEntryBase entry) {
                ColorPreviewFn f = _colorPreview as ColorPreviewFn;
                if (f == null) return null;
                return f(entry);
            }
            // 把颜色键写回默认值（点名称 / 调色板里的「还原默认」共用同一条路径）。
            public static void ResetColor(ConfigEntryBase entry) {
                ColorResetFn f = _colorReset as ColorResetFn;
                if (f != null) f(entry);
            }
        }
    }
}
