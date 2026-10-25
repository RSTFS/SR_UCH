// 控件与页面能力门面：管理器通用控件（按键录制/右键绑键/开关/滑块/下拉）、首页、Ctl 门面、行页扩展点 RowHooks、表格控件 TableArgs -> TableDraw。
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx;
using BepInEx.Bootstrap;

namespace SR_UCH.Tweaks {
public partial class SR {
// 分区：Ctl（功能页能力门面 + 管理器通用控件 + 首页 + 行/页扩展点）
// Ctl 只转发不放逻辑：功能页与外部模块走 SR.Ctl，核心内部（SR 各分部文件）直接调 SR；新能力在 Ctl 里补纯转发成员。

    public static class Ctl {

        // 颜色调色板自注入：核心在 Ctl 首次被访问时把实现挂到契约层门面。
        // 放在这里而不是 MainPlugin 的注入清单里 —— 新增 Set*Sinks 会要求 MainPlugin 同步改（该文件由他人收尾）。
        static Ctl() { SR.ColorPicker.Wire(); }

        // UI 缩放系数（各页像素尺寸都乘它）
        public static float Sc(float v) { return SR.Sc(v); }

        // 统一控件高度：按钮 / 输入框 / 下拉框 / 复选框同高（默认 26，按 UI 缩放；功能可用 ControlHeight 覆盖）。
        // UI 主题令牌：圆角、过渡时长、底栏/面板底色（各页面统一引用，避免散落魔数）。
        public static float ThemeRadius { get { return Sc(6f); } }
        public static float ThemeHoverFade { get { return 0.12f; } }
        public static Color ThemeFooterBg { get { return new Color(0.10f, 0.11f, 0.13f, 0.96f); } }
        public static Color ThemePanelBg { get { return new Color(0.08f, 0.08f, 0.09f, 0.96f); } }
        public static float ControlH() { return SR.ControlH(); }
        // 表格尺寸令牌：行高/表头高/拖拽条/滚动条宽/最小高度（全部随 UI 缩放，页面不再写死字面量）。
        // 依据列模板与当前语言表头文字计算默认列宽（只增不减的下限），供列表首次布局使用。
        public static float[] TableColumnWidths(SRApi.TableColumn[] cols) {
            if (cols == null) return null;
            float[] w = new float[cols.Length];
            for (int i = 0; i < cols.Length; i++) {
                SRApi.TableColumn c = cols[i];
                if (c == null) { w[i] = Sc(60f); continue; }
                string title = SRApi.T(c.TitleZh, c.TitleEn);
                float tw = 0f;
                try { tw = Label.CalcSize(new GUIContent(title)).x; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                w[i] = Mathf.Max(Sc(c.MinW), tw + Sc(c.PadW));
            }
            return w;
        }
        public static float TableRowH() { return Sc(26f); }
        public static float TableHeaderH() { return Sc(24f); }
        public static float TableGripH() { return Sc(4f); }
        public static float TableGripGap() { return Sc(2f); }
        public static float TableScrollbarW() { return Sc(13f); }
        public static float TableMinH() { return Sc(120f); }

        // 表格自适应高度：页面顶部已用高度 usedAbove + 表格高度 = 底栏之上可用高度。
        // 26 = 窗口标题栏，2 = 内容区内边距，4 = 行区上下留白；均按当前 UI 缩放换算。
        // 竖向滚动条：绘制/配色由核心统一（ImGui Scrollbar 配色）。
        // 列表高度自适应（配 TableAfterDraw 自校准）：窗口/页头/切换页面后一帧内贴底。
    public static float TableAutoFit(float contentBottomY, float currentY, float minH, ref float bias) {
        if (contentBottomY <= 1f) return minH;
        return Mathf.Max(minH, contentBottomY - currentY + bias);
    }
    // 表格底边拖拽改高度：核心统一处理按下/拖动/松手写盘（entry 值 0 = 自动高度）。
    private sealed class HeightDrag { public bool on; public float y0; public float h0; }
    private static readonly Dictionary<ConfigEntryBase, HeightDrag> _heightDrags = new Dictionary<ConfigEntryBase, HeightDrag>();
    public static void TableHeightGrip(ConfigEntryBase entry, Rect grip, float autoH, int min, int max) {
        try {
            if (entry == null) return;
            Event e = Event.current;
            if (e == null) return;
            HeightDrag st;
            if (!_heightDrags.TryGetValue(entry, out st)) { st = new HeightDrag(); _heightDrags[entry] = st; }
            float cur = autoH;
            try { int v = System.Convert.ToInt32(entry.BoxedValue); if (v > 0) cur = v; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) {
                st.on = true; st.y0 = e.mousePosition.y; st.h0 = cur;
                BlockWindowDrag = true; e.Use();
            } else if (e.type == EventType.MouseDrag && st.on) {
                float h = st.h0 + (e.mousePosition.y - st.y0) / Mathf.Max(0.0001f, Sc(1f));
                entry.BoxedValue = Mathf.Clamp(Mathf.RoundToInt(h), min, max);
                BlockWindowDrag = true; e.Use();
            } else if (e.type == EventType.MouseUp && st.on) {
                st.on = false; BlockWindowDrag = false;
                MarkConfigDirty(entry.ConfigFile);
                e.Use();
            }
        } catch (Exception __ex) { Guard.Log("表格高度拖拽", __ex); }
    }

    public static void TableAfterDraw(float contentBottomY, float drawnBottom, ref float bias) {
        try {
            if (contentBottomY <= 1f) return;
            float gap = contentBottomY - drawnBottom;
            if (Mathf.Abs(gap) > 0.5f) bias = Mathf.Clamp(bias + gap, -Sc(160f), Sc(160f));
        } catch (Exception __ex) { Guard.Log("表格高度自校准", __ex); }
    }
    // 竖向滚动条一体化：几何来自 TableArgs（box/stripW/rowsH/rowCount/rowH），绘制+输入一次完成。
    public static bool TableScrollbarFor(SRApi.TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
        try {
            if (a == null || a.box.width <= 0f || a.rowsH <= 0f) return false;
            float contentH = a.rowCount * a.rowH;
            if (contentH <= a.rowsH + 0.5f) return false;
            float maxScroll = Mathf.Max(0f, contentH - a.rowsH);
            float sw = Sc(13f);
            // 有钉住列时滚动条放在钉住列外侧（联机列表的加入列在左、滚动条在最右）；
            // 没有钉住列时放在数据区右缘。两种列表共用同一规则，避免压住钉住列。
            float tx = a.pinW > 0f ? a.pinX + a.pinW : a.box.x + a.stripW;
            Rect track = new Rect(tx, a.box.y + a.headerH, sw, a.rowsH);
            Rect thumb;
            // 滚动条只有“隐藏 / 显示”两态：默认隐藏，不会因为鼠标悬停自动出现。
            // 列表滚动统一走滚轮 / Ctrl+滚轮；若以后要显示，把 __show 改成 true 即可。
            bool __show = false;
            a.scrollbarAlpha = __show ? 1f : 0f;
            TableScrollbar(track, contentH, a.rowsH, scrollY, dragging, out thumb, __show, a.scrollbarAlpha);
            // 不可见时不接受点击/拖动，避免“看不见却能点”的隐形热区。
            if (!__show) return false;
            if (e == null) return false;
            return SRApi.TableScrollbarInput(e, e.mousePosition, track, thumb, maxScroll, ref dragging, ref grabY, ref scrollY);
        } catch (Exception __ex) { Guard.Log("表格滚动条一体化", __ex); return false; }
    }

    public static void TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible = true, float alpha = 1f) {
            SR.TableScrollbar(track, contentH, viewH, scrollY, dragging, out thumb, visible, alpha);
        }

        public static float TableAutoHeight(float usedAbove, float minH) {
            float avail = SRApi.PageViewportHeight > Sc(60f)
                ? SRApi.PageViewportHeight
                : (SR.ContentArea.height - Sc(26f) - Sc(2f) - SR.FooterHeight);
            return Mathf.Max(minH, avail - usedAbove - Sc(2f));
        }

        // 统一操作列按钮：在行矩形里依次画 BtnClip 按钮，返回本帧左键 MouseUp 命中并已执行回调的下标。
        // widths 为 null、长度不足或某项 <=0 时，该按钮宽度 = BtnClip.CalcSize(label).x + 11px（自适应文字）；
        // 多个按钮总宽超出行宽时按可用宽度统一收缩，绝不横向溢出。Enabled=false 的按钮画灰且不响应。
        public static int TableActionButtons(Rect rowRect, SRApi.TableAction[] actions, float[] widths, ref int hoverIndex) {
            hoverIndex = -1;
            if (actions == null || actions.Length == 0) return -1;
            try {
                Event e = Event.current;
                Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
                float padX = Sc(2f);
                float y = rowRect.y + Sc(1f);
                float h = Mathf.Max(Sc(14f), rowRect.height - Sc(2f));
                float gap = Sc(2f);
                int n = actions.Length;
                float[] bw = new float[n];
                int valid = 0;
                float sum = 0f;
                for (int i = 0; i < n; i++) {
                    SRApi.TableAction a = actions[i];
                    if (a == null) continue;
                    float w;
                    if (widths != null && i < widths.Length && widths[i] > 0f) {
                        w = Sc(widths[i]);
                    } else {
                        GUIStyle st = BtnClip;
                        w = (st != null ? st.CalcSize(new GUIContent(a.Label ?? "")).x : Sc(30f)) + Sc(11f);
                    }
                    if (w <= 0f) w = Sc(8f);
                    bw[i] = w;
                    sum += w;
                    valid++;
                }
                if (valid == 0) return -1;
                float gapTotal = gap * Mathf.Max(0, valid - 1);
                // 可绘制宽度 = 行宽减去左侧起始内边距（右侧允许画到行边界，不再额外留 padX）。
                float avail = Mathf.Max(0f, rowRect.width - padX);
                float need = sum + gapTotal;
                if (need > avail) {
                    // 可用宽度不足：按钮和间距一起按比例收缩，保证最后一个按钮不越过行右边界。
                    float scale = need > 0f ? avail / need : 0f;
                    for (int i = 0; i < n; i++) if (bw[i] > 0f) bw[i] *= scale;
                    gap *= scale;
                }
                float x = rowRect.x + padX;
                int hit = -1;
                for (int i = 0; i < n; i++) {
                    SRApi.TableAction a = actions[i];
                    if (a == null) continue;
                    float curW = bw[i];
                    if (curW <= 0f) continue;
                    Rect br = new Rect(x, y, curW, h);
                    x += curW + gap;
                    if (br.xMax > rowRect.xMax) br.width = Mathf.Max(0f, rowRect.xMax - br.x);
                    if (br.width <= 0f) break;
                    bool over = br.Contains(mp);
                    bool enabled = a.Enabled;
                    if (over) hoverIndex = i;
                    if (over && enabled && !string.IsNullOrEmpty(a.Tip)) GUI.tooltip = a.Tip;
                    Color old = GUI.color;
                    try {
                        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
                        BtnClip.Draw(br, new GUIContent(a.Label ?? ""), over && enabled, false, false, false);
                    } finally { GUI.color = old; }
                    if (!enabled || e == null) continue;
                    if (e.type == EventType.MouseDown && e.button == 0 && over) BlockWindowDrag = true;
                    if (e.type == EventType.MouseUp && e.button == 0 && over) {
                        Action cb = a.OnClick;
                        if (cb != null) { try { cb(); } catch (Exception __ex2) { Guard.Log("操作列按钮回调", __ex2); } }
                        hit = i;
                        e.Use();
                    }
                }
                return hit;
            } catch (Exception __ex) {
                Guard.Log("操作列按钮", __ex);
                hoverIndex = -1;
                return -1;
            }
        }

        // 条目标签（可点击恢复默认值；tooltip = 说明 + 后缀追加的默认值，不再带恢复提示）
        public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) { SR.RestoreLabel(content, entry, w, h, hotkey); }
        public static void RenderControl(ConfigEntryBase entry) { SR.RenderControl(entry); }
        public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { SR.RenderEntryRow(entry, isInternal, colWidth); }
        public static ConfigEntryBase FindEntry(string section, string key) { return SR.FindInternalEntry(section, key); }
        public static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) { return SR.AllEntries(config); }
        // 某 section 的可见条目（已应用功能注册的行可见性过滤）。
        public static List<ConfigEntryBase> SectionEntries(string section) { return SR.SectionEntries(section); }
        // 条目行与名称列宽
        public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) { SR.RenderEntryRow(entry, true, colWidth); }
        public static float EntryNameWidth() { return SR.EntryNameWidth(); }
        public static float TextHeight(string text, float width) { return SR.TextHeight(text, width); }
        // 滑块（松左键提交；intStep = 整数步进）
        public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false) { return SR.DrawSlider(rect, value, min, max, intStep); }
        // 下拉框（自绘：列表紧跟按钮下方）；返回选中下标，-1 = 未选。
        // width <= 0 = 宽度自动：取全部选项 + 当前显示文本的最大 CalcSize，加箭头/内边距；
        // 下限 80、上限为可用宽度的 60%（优先内容区，其次窗口宽度），长选项不截断也不会撑破页面。
        public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = AutoComboBoxWidth(options, current);
            // 高度由 SR.ComboBox 统一取 ControlH()（与按钮/输入框同高），这里只管宽度与命中。
            return SR.ComboBox(entry, current, vals, options, ref open, width);
        }

        private static float AutoComboBoxWidth(string[] options, string current) {
            float maxText = 0f;
            try {
                GUIStyle st = BtnClip;
                if (st != null) {
                    if (options != null) {
                        for (int i = 0; i < options.Length; i++) {
                            maxText = Mathf.Max(maxText, st.CalcSize(new GUIContent(options[i] ?? "")).x);
                        }
                    }
                    maxText = Mathf.Max(maxText, st.CalcSize(new GUIContent(current ?? "")).x);
                }
            } catch { maxText = 0f; }
            if (maxText <= 0f) maxText = Sc(60f);
            float avail = 0f;
            try { avail = SRApi.ContentArea.width; } catch { avail = 0f; }
            if (avail <= 0f) { try { avail = SRApi.Ctl.WinWidth; } catch { avail = 0f; } }
            if (avail <= 0f) avail = Screen.width;
            float minW = Sc(80f);
            float maxW = Mathf.Max(minW, avail * 0.6f);
            // 左右内边距（与按钮同一套 FramePadding）+ 箭头区，宽度随缩放走。
            return Mathf.Clamp(maxText + Sc(FramePadX * 2 + 20f), minW, maxW);
        }
        public static void WrapLabel(string text) { SR.WrapLabel(text); }
        public static float SidebarWidth() { return SR.SidebarWidth(); }
        // 写配置值（会标记待保存）
        public static void SetValue(ConfigEntryBase entry, object value) { SR.SetValue(entry, value); }

        // 按键按钮（含右键绑键 / 组合键录制）
        public static string RecText() { return SR.RecText(); }
        // 原始皮肤字体（由 SR.Styles 在首次改皮肤前记录），供窗口恢复用。
        public static Font OrigSkinFont() { return SR.OrigSkinFontValue; }
        public static string KeySuffix(ConfigEntry<KeyCode> e) { return SR.KeySuffix(e); }
        // 快捷键框文本：只显示按键本身
        public static string KeyText(ConfigEntry<KeyCode> e) { return SR.KeyText(e); }
        public static void RegisterRowHotkey(ConfigEntry<KeyCode> entry) { SR.RegisterRowHotkey(entry); }
        public static void TryBindByRightClick(ConfigEntry<KeyCode> entry, Rect rect) { SR.TryBindByRightClick(entry, rect); }
        public static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.SelfToggleButton(label, tooltip, on, keyEntry, width, bindable);
        }
        public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.HotkeyActionButton(label, tooltip, keyEntry, width, bindable);
        }
        // 自动快捷键界面件（右键绑定，SR.CheckHotkeys 统一触发）
        // 按钮：右键绑键 + 点击执行；开关行统一用 RestoreLabel（标签右键绑键）+ RenderControl（复选框只管开关）
        public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            return SR.HotkeyButton(id, label, tooltip, action, width, height);
        }

        // 右键仍可绑键，只是不在悬浮提示里写明（返回树屋这类“不需要提示绑定”的按钮用）。
        public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            return SR.HotkeyButtonNoHint(id, label, tooltip, action, width, height);
        }
        public static string HotkeyText(string id) { return SR.HotkeyText(id); }
        // 悬浮提示框（在 OnGUI 末尾统一绘制；功能页无需关心）
        public static void DrawTooltip(Vector2 mp) { SR.DrawTooltip(mp); }

        // 样式（深色主题，见 SR.Styles.cs）
        public static GUIStyle Label { get { return _label; } }
        public static GUIStyle LabelClip { get { return _labelClip; } }
        public static GUIStyle LabelClipCenter { get { return _labelClipCenter; } }
        public static GUIStyle SecHeaderCenter { get { return _secHeaderCenter; } }
        public static GUIStyle LabelWrap { get { return _labelWrap; } }
        public static GUIStyle ChatLabel { get { return _chatLabel; } }
        public static GUIStyle ChatArea { get { return _chatArea; } }
        public static GUIStyle SecHeader { get { return _secHeader; } }
        public static GUIStyle SelItem { get { return _selItem; } }
        public static GUIStyle Btn { get { return _btn; } }
        public static GUIStyle BtnClip { get { return _btnClip; } }
        // 表格列偏好的统一持久化组件（三处表格共用）：格式 "o=0,1,2;h=1,3;w=..."
        public class TablePrefs {
            private readonly BepInEx.Configuration.ConfigEntry<string> _entry;
            private readonly int _count;
            private long _sig; // 上次落盘的签名（SaveIfDirty 用，避免每帧写盘）
            public int[] Order;
            public readonly System.Collections.Generic.HashSet<int> Hidden = new System.Collections.Generic.HashSet<int>();
            private float[] _savedW;   //配置里读到的列宽（下标->像素）
            private bool _applied;     //是否已把 _savedW 套用过一次（见 BindWidths）
            private float[] _widths;   //调用方当前使用的列宽数组（由 BindWidths 绑定）
            private float[] _defaults; //首次绑定时的默认列宽（供"还原全部"用）

            public TablePrefs(BepInEx.Configuration.ConfigEntry<string> entry, int count) {
                _entry = entry; _count = Mathf.Max(1, count);
                Order = new int[_count];
                for (int i = 0; i < _count; i++) Order[i] = i;
                Load();
                _sig = Sig();
            }

            public void Load() {
                try {
                    if (_entry == null || string.IsNullOrEmpty(_entry.Value)) return;
                    // 旧格式（只存纯列顺序、无 o=/h= 前缀）按列顺序读。
                    if (_entry.Value.IndexOf('=') < 0) {
                        List<int> legacy = new List<int>();
                        foreach (string raw in _entry.Value.Split(',')) {
                            int v;
                            if (int.TryParse(raw.Trim(), out v) && v >= 0 && v < _count && !legacy.Contains(v)) legacy.Add(v);
                        }
                        if (legacy.Count > 0) {
                            for (int k = 0; k < _count; k++) if (!legacy.Contains(k)) legacy.Add(k);
                            Order = legacy.ToArray();
                        }
                        return;
                    }
                    foreach (string part in _entry.Value.Split(';')) {
                        string p = part.Trim();
                        if (p.StartsWith("o=")) {
                            List<int> ord = new List<int>();
                            foreach (string raw in p.Substring(2).Split(',')) {
                                int v;
                                if (int.TryParse(raw.Trim(), out v) && v >= 0 && v < _count && !ord.Contains(v)) ord.Add(v);
                            }
                            for (int k = 0; k < _count; k++) if (!ord.Contains(k)) ord.Add(k);
                            Order = ord.ToArray();
                        } else if (p.StartsWith("w=")) {
                            foreach (string pair in p.Substring(2).Split(',')) {
                                string[] kv = pair.Split(':');
                                int idx; float wv;
                                if (kv.Length != 2 || !int.TryParse(kv[0].Trim(), out idx) || !float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out wv)) continue;
                                if (idx < 0 || idx >= _count) continue;
                                if (_savedW == null) _savedW = new float[_count];
                                _savedW[idx] = wv;
                            }
                        } else if (p.StartsWith("h=")) {
                            Hidden.Clear();
                            foreach (string raw in p.Substring(2).Split(',')) {
                                int v;
                                if (int.TryParse(raw.Trim(), out v) && v >= 0 && v < _count) Hidden.Add(v);
                            }
                        }
                    }
                    // 读到“全部隐藏”时一次性修掉（至少留一列）。
                    if (Hidden.Count >= _count) {
                        List<int> __hs = new List<int>(Hidden);
                        Hidden.Clear();
                        for (int __k = 1; __k < __hs.Count; __k++) Hidden.Add(__hs[__k]);
                    }
                } catch (Exception __ex) { Guard.Log("表格列偏好读取", __ex); }
            }

            // 把配置里存的列宽套到调用方的列宽数组上（页面先填默认值，再调这里覆盖）；只套用一次。
            public void BindWidths(float[] colW) {
                if (colW == null) return;
                // 首次绑定前页面刚填好默认值，留一份作“还原全部”的基准。
                if (_defaults == null || _defaults.Length != colW.Length) _defaults = (float[])colW.Clone();
                _widths = colW;
                if (_applied || _savedW == null) return;
                for (int i = 0; i < _savedW.Length && i < colW.Length; i++) if (_savedW[i] > 0f) colW[i] = _savedW[i];
                _applied = true;
            }

            // 64 位 FNV 折叠签名（零分配），供 SaveIfDirty 判脏。
            private long Sig() {
                unchecked {
                    ulong h = 1469598103934665603UL;
                    h = (h ^ 0x9E3779B97F4A7C15UL) * 1099511628211UL;
                    for (int i = 0; i < Order.Length; i++) { h = (h ^ (uint)Order[i]) * 1099511628211UL; h = (h ^ 0x2DUL) * 1099511628211UL; }
                    h = (h ^ 0x7CUL) * 1099511628211UL;
                    foreach (int hh in Hidden) { h = (h ^ (uint)hh) * 1099511628211UL; h = (h ^ 0x2DUL) * 1099511628211UL; }
                    h = (h ^ 0x7CUL) * 1099511628211UL;
                    if (_widths != null) for (int i = 0; i < _widths.Length; i++) { h = (h ^ (uint)Mathf.RoundToInt(_widths[i])) * 1099511628211UL; h = (h ^ 0x2DUL) * 1099511628211UL; }
                    return (long)h;
                }
            }

            public void Save() {
                try {
                    if (_entry == null) return;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("o=");
                    for (int i = 0; i < Order.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Order[i]); }
                    sb.Append(";h=");
                    bool first = true;
                    foreach (int h in Hidden) { if (!first) sb.Append(','); first = false; sb.Append(h); }
                    // 列宽（像素，整数；页面每次都用默认值初始化，整份写出即可）
                    if (_widths != null && _widths.Length > 0) {
                        sb.Append(";w=");
                        for (int i = 0; i < _widths.Length; i++) {
                            if (i > 0) sb.Append(',');
                            sb.Append(i).Append(':').Append(Mathf.RoundToInt(Mathf.Max(0f, _widths[i])));
                        }
                    }
                    _entry.Value = sb.ToString();
                    // 回写 _savedW：与 _widths 同步，否则下一帧会用旧值覆盖回去并反复判脏写盘。
                    if (_widths != null && _widths.Length > 0) {
                        if (_savedW == null || _savedW.Length != _widths.Length) _savedW = new float[_widths.Length];
                        for (int i = 0; i < _widths.Length; i++) _savedW[i] = _widths[i];
                    }
                    _sig = Sig();
                } catch (Exception __ex) { Guard.Log("表格列偏好保存", __ex); }
            }

            // 每帧调用：顺序/隐藏列被改动时才落盘。
            //还原全部：列序回默认、取消隐藏、列宽回默认（对应 Shift+右键）
            public void ResetAll() {
                try {
                    for (int i = 0; i < Order.Length; i++) Order[i] = i;
                    Hidden.Clear();
                    _savedW = null;
                    if (_widths != null && _defaults != null) {
                        int n = Mathf.Min(_defaults.Length, _widths.Length);
                        for (int i = 0; i < n; i++) _widths[i] = _defaults[i];
                    }
                    Save();
                } catch (Exception __ex) { Guard.Log("表格列偏好还原", __ex); }
            }

            public void SaveIfDirty() {
                if (Sig() != _sig) Save();
            }
        }

        // 绝对矩形复选框（表格钉住列用）：与设置页开关同款 —— CheckOn/CheckOff 样式 + 勾选贴图叠加。
        public static bool CheckboxAt(Rect r, bool value, string tip) {
            bool clicked;
            try {
                GUIContent c = (value && _checkTex == null) ? new GUIContent("✓", tip) : new GUIContent("", tip);
                clicked = GUI.Button(r, c, value ? _checkOn : _checkOff);
                if (value && _checkTex != null) {
                    float m = r.width * 0.18f;
                    GUI.DrawTexture(new Rect(r.x + m, r.y + m, r.width - m * 2f, r.height - m * 2f), _checkTex);
                }
            } catch { clicked = false; }
            return clicked;
        }

        public static GUIStyle CheckOn { get { return _checkOn; } }
        public static GUIStyle CheckOff { get { return _checkOff; } }
        public static GUIStyle SearchBox { get { return _searchBox; } }
        public static GUIStyle Footer { get { return _footer; } }

        public static float WinWidth { get { return _winWidth; } }
        //滚到底（新消息到达时用；不要拿属性返回值去改 y，那是副本，改了不生效）
        public static void ScrollToBottom() { _scroll.y = float.MaxValue; }
        public static ConfigFile InternalConfig { get { return _internalConfig; } }
        // 界面是否英文系 = 内置英文或语言包声明回退英文（外部模块页强制中文时为 false）。
        public static bool LangEn { get { return UseEn; } }
        // 外部模块页豁免：渲染期间置 true（该页始终中文）
        public static bool ForceZh { get { return _forceZh; } set { _forceZh = value; } }
        // 滑块提交为“值变了就写”（ImGui 的 v_last 模式）：取 DrawSlider 返回值，变了就调本方法或 SetValue。
        public static Dictionary<ConfigEntryBase, bool> EditOpen { get { return _editOpen; } }
        public static Dictionary<ConfigEntryBase, string> EditText { get { return _editText; } }
        // 配置落盘节流 + 条目缓存失效的门面（外部 DLL 无需访问 SR 的 internal 成员）。
        public static void MarkConfigDirty(ConfigFile cfg) { SR.MarkConfigDirty(cfg); }
        public static void NoteEntryBound() { SR.NoteEntryBound(); }

    }

// 分区：Controls（管理器通用控件：按键录制/右键绑键/开关按钮 + 首页）
// 功能页已在各自模块文件，这里只留全页面共用控件；功能页通过 SR.Ctl 调用。

        // 按钮矩形登记（右键绑键用）：坐标取当前 GUI 组（滚动视图内容）内坐标，命中检测须在同一组内。

        //录制中显示已录到的按键顺序（按钮/键位行共用）：如 "Shift -> 9 -> 0"
        private static string RecText() {
            if (_recSeq.Count == 0) return T("请按键...", "press keys...");
            string s = "";
            foreach (KeyCode k in _recSeq) s += (s.Length > 0 ? " -> " : "") + KeyDisplayName(k);
            return s;
        }

        //快捷键后缀统一见 SR.KeySuffix（录制中返回紧凑的 " [...]" / " [已录到的键]"）

        //把"开关行"登记为右键绑键命中区：用刚画完的标签按钮矩形向右扩展，覆盖右侧的选择框。
        private static void RegisterRowHotkey(ConfigEntry<KeyCode> entry) {
            if (entry == null) return;
            if (Event.current == null) return;
            Rect r = GUILayoutUtility.GetLastRect();
            r.width += Sc(44);
            TryBindByRightClick(entry, r);
        }

        //右键绑键当场判定（同帧、同 GUI 组坐标系）：跨帧存矩形会因上方内容每帧增减导致布局浮动、绑错按钮。
        private static void TryBindByRightClick(ConfigEntry<KeyCode> entry, Rect rect) {
            if (entry == null) return;
            if (_capturing != null) return;
            if (_rmbConsumedFrame == Time.frameCount) return; //一次右键只认一个按钮
            Event ev = Event.current;
            if (ev == null) return;
            bool down = (ev.type == EventType.MouseDown && ev.button == 1) || Input.GetMouseButtonDown(1);
            if (!down) return;
            if (!rect.Contains(ev.mousePosition)) return;
            _rmbConsumedFrame = Time.frameCount;
            HandleHotkeyRightClick(entry, false);
            // 不吞事件：_rmbConsumedFrame 已保证一次右键只绑一个控件，吞事件会让界面闪一下。
        }

        // 自身状态开关按钮：左键切换 on/off 且显示当前绑定键；bindable 时支持右键绑键（用绘制后的 GetLastRect 取矩形）。
        private static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            // 键后缀统一走 SR.KeySuffix：未绑定 ""、已绑定 " [Shift+K]"、录制中 " [...]"。
            string text = label + (on ? T("开", "ON") : T("关", "OFF")) + SR.KeySuffix(keyEntry);
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30))); // 控件统一 30px
            // 右键也会让 GUI.Button 返回 true，须按触发按键过滤。
            if (clicked && evBtn != 0) clicked = false;
            //只有 bindable 的按钮才支持右键绑键；其余按钮右键什么也不会发生
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发切换
        }

        // 快捷键文本：统一放进悬浮提示（按钮文字不再拼 [键]，否则绑定后按钮被截断）。
        private static string KeyTip(ConfigEntry<KeyCode> key) {
            try {
                if (key == null || key.Value == KeyCode.None) return "";
                return "\n" + T("按键：", "Key: ") + SR.KeySuffix(key).Trim().Trim('[', ']');
            } catch { return ""; }
        }

        //操作按钮：左键触发操作。bindable=true 时该按钮支持右键绑定快捷键（其余按钮右键无反应）。
        private static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            // 按钮文字只显示动作名；按键进悬浮提示（绑定后不会把按钮文字挤爆）。
            string text = label;
            if (keyEntry != null) {
                string actTip = string.IsNullOrEmpty(tooltip) ? label : label + "\n" + tooltip;
                actTip += KeyTip(keyEntry);
                tooltip = bindable ? actTip + "\n" + T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey") : actTip;
            }
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30))); // 控件统一 30px
            if (clicked && evBtn != 0) clicked = false;
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发操作
        }

        // 右键处理：未捕捉 -> 进入捕捉；捕捉中再右键 -> 删除快捷键（Esc 也可清空）。
        private static void HandleHotkeyRightClick(ConfigEntry<KeyCode> keyEntry, bool capturing) {
            if (keyEntry == null) return;
            if (capturing) {
                try { keyEntry.BoxedValue = KeyCode.None; } catch (Exception __ex) { Guard.Log("清空按键绑定", __ex); }
                SR.MarkKeyChanged();   //清空键位：让序列监听的 watch 列表失效
                _capturing = null;
                _dirty = true;
            } else {
                _prevBoxed = keyEntry.BoxedValue;
                _capturing = keyEntry;
                _captureStartFrame = Time.frameCount; //右键进入捕捉：本次 MouseDown 不能被"按下即取消"清掉
                _recSeq.Clear();
                _recHeld.Clear();
            }
        }

        // 自动快捷键界面件：功能自绘按钮时用它（开关行统一走 RestoreLabel + RenderControl）
        // 登记 action（绑定的键由 SR.CheckHotkeys 触发）-> 画按钮（带 [键] 文本）-> 右键绑键 -> 返回左键点击。
        public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            SR.HotkeyAction(id, label, action);
            ConfigEntry<KeyCode> key = SR.Hotkey(id);
            bool capturing = key != null && _capturing == key;
            // 快捷键框只显示按键本身；动作名放进悬浮提示。
            string tip = "";
            if (!string.IsNullOrEmpty(tooltip)) tip += "\n" + tooltip;
            tip += "\n" + T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey");
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            float bw = width > 0f ? width : Sc(150);
            float bh = height > 0f ? height : Sc(30);
            bool clicked = GUILayout.Button(new GUIContent(label, tip + KeyTip(key)),
                capturing ? _capture : _btn, GUILayout.Width(bw), GUILayout.Height(bh));
            if (clicked && evBtn != 0) clicked = false; //右键绝不触发左键动作
            if (key != null && e != null) TryBindByRightClick(key, GUILayoutUtility.GetLastRect());
            return clicked && !capturing;
        }

        // 与 HotkeyButton 相同（右键可绑键、点击执行），但悬浮提示里不追加“右键：绑定/删除快捷键”。
        public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            SR.HotkeyAction(id, label, action);
            ConfigEntry<KeyCode> key = SR.Hotkey(id);
            bool capturing = key != null && _capturing == key;
            string tip = "";
            if (!string.IsNullOrEmpty(tooltip)) tip += "\n" + tooltip;
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            float bw = width > 0f ? width : Sc(150);
            float bh = height > 0f ? height : Sc(30);
            bool clicked = GUILayout.Button(new GUIContent(label, tip + KeyTip(key)),
                capturing ? _capture : _btn, GUILayout.Width(bw), GUILayout.Height(bh));
            if (clicked && evBtn != 0) clicked = false;
            if (key != null && e != null) TryBindByRightClick(key, GUILayoutUtility.GetLastRect());
            return clicked && !capturing;
        }

        private static void RenderHomePage() {
            GUILayout.Label(T("- 欢迎使用 SR＿UCH -", "- Welcome to SR＿UCH -"), _secHeader);
            // 启动自检发现按名绑定的目标失效时，在首页最上方提示；详细清单在 设置 -> 自检。
            if (SR.AuditHasIssues) {
                Color __pc = GUI.color;
                try {
                    GUI.color = new Color(1f, 0.78f, 0.35f, 1f);
                    WrapLabelTight(T(" 有 " + SR.AuditIssueCount + " 项按名绑定的目标失效，对应功能不会生效 - 详见 设置 -> 自检。",
                        " " + SR.AuditIssueCount + " name-bound target(s) are broken; those features will not work - see Settings > Self-check."));
                } finally { GUI.color = __pc; }
                GUILayout.Space(Sc(4));
            }
            WrapLabel(T("Ultimate Chicken Horse 模组整合增强包。", "An enhancement pack for Ultimate Chicken Horse."));
            WrapLabel(T("本 mod 参考了 BetterFreeplay，BuildingPlus，BuildUnlimiter，UCH Freeplay Spawn Setter，UCH Tweaks，UCH-PlayerTracker-Mod，UltimateBuilder，向这些 mod 的制作者表示感谢。", "This mod references BetterFreeplay, BuildingPlus, BuildUnlimiter, UCH Freeplay Spawn Setter, UCH Tweaks, UCH-PlayerTracker-Mod and UltimateBuilder. Thanks to their authors."));
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("- 开源地址 -", "- Source -"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("https://github.com/RSTFS/SR_UCH", _btn, GUILayout.Height(Sc(30)))) {
                try { Application.OpenURL("https://github.com/RSTFS/SR_UCH"); } catch (Exception __ex) { Guard.Log("打开开源地址", __ex); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            GUILayout.Space(Sc(8));
            GUILayout.Label(T("- 操作提示 -", "- Tips -"), _secHeader);
            WrapLabelTight(T("表格：拖分隔线调列宽，拖表头换列序，右键表头隐藏列；Shift+右键表格 = 还原。",
                "Tables: drag a separator to resize, drag a header to reorder, right-click a header to hide a column; Shift+right-click the table to reset."));
            WrapLabelTight(T("滚轮：Shift+滚轮横向滚动；下拉框/滑块上滚轮只调值，不滚页面。",
                "Wheel: Shift+wheel scrolls horizontally; over a dropdown/slider the wheel changes its value instead of scrolling."));
            WrapLabelTight(T("按键：点键位框后按键录制，支持组合键与 9->0 序列；Esc 清空，Shift+Esc 恢复原值。",
                "Keys: click the key box and press keys to record; combos and sequences like 9->0 work; Esc clears, Shift+Esc restores the previous value."));
            WrapLabelTight(T("条目：点名称恢复默认值，右键名称绑定快捷键。", "Entries: click a name to restore its default, right-click a name to bind a hotkey."));
            WrapLabelTight(T("侧栏：在设置页的自定义栏目里拖动列头，即可调整栏目顺序。",
                "Sidebar: reorder sections by dragging a header under Settings - Custom sections."));

            GUILayout.Label(T("- 请共同维护游戏体验 -", "- Keep the game fun for everyone -"), _secHeader);
            WrapLabel(T("请不要使用本模组破坏别人的游戏体验。", "Please do not use this mod to ruin other players' experience."));
            GUILayout.Space(Sc(4));

            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(4));
        }

        //换行标签：按实际可用宽度测量高度，保证最后一行文字不被裁切
        private static void WrapLabel(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
        }

        // 紧凑版换行标签：高度用实测值，靠负间距把条目贴紧。
        private static void WrapLabelTight(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
            GUILayout.Space(-Sc(5)); // 贴紧：抵消样式自带的行距
        }

        // 悬浮提示框：最后绘制以保证在最上层；按最宽一行定宽，仅当整行超过屏幕 80% 才自动折行。
        // 测量结果按 tip 原文缓存（窗口尺寸变化时可能沿用旧 limit，tip 一变即重算）。
        private static string _tipKey;
        private static string[] _tipLines = new string[0];
        private static readonly GUIContent _tipContent = new GUIContent();
        private static readonly GUIContent _tipLineContent = new GUIContent();
        private static float _tipW, _tipH, _tipInnerW;
        private static GUIContent TipLineContent(string s) { _tipLineContent.text = s; return _tipLineContent; }
        // 关闭界面时清空提示缓存，避免 tooltip 文本跨帧残留（表头提示延迟消失）。
        public static void ClearTip() {
            try { _tipKey = null; _tipLines = new string[0]; _tipContent.text = ""; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        // 悬浮提示显示延迟（参考 Dear ImGui 的 tooltip 延迟）与淡入，避免鼠标扫过控件时提示乱闪。
        private const float TipDelay = 0.2f;
        private const float TipFade = 0.12f;
        // 悬浮文本专用样式：零内边距，高度由整块 CalcHeight 得出（不再逐行累加，避免虚高）。
        private static GUIStyle _tipText;
        private static GUIStyle TipTextStyle() {
            if (_tipText == null) {
                _tipText = new GUIStyle(_labelWrap);
                _tipText.padding = new RectOffset(0, 0, 0, 0);
                _tipText.margin = new RectOffset(0, 0, 0, 0);
                _tipText.wordWrap = true;
            }
            return _tipText;
        }
        private static string _tipShownKey;
        private static float _tipShownAt = -999f;

        private static void DrawTooltip(Vector2 mp) {
            string tip = GUI.tooltip;
            // 注意：Layout 帧里 GUI.tooltip 往往为空，不能因此重置计时，
            // 否则延迟永远走不完 → 所有悬浮说明看起来"消失"。空值时保持状态直接返回。
            if (string.IsNullOrEmpty(tip)) return;
            if (tip != _tipShownKey) { _tipShownKey = tip; _tipShownAt = Time.unscaledTime; }
            // 不做延迟：悬停即显示（淡入仍保留一帧内的轻微过渡）。
            float __alpha = 1f;
            // 测量与绘制统一用 _labelWrap（同一个字体/内边距），否则宽高会与绘制不一致导致裁字。
            if (tip != _tipKey) {
                _tipKey = tip;
                _tipContent.text = tip;
                _tipLines = tip.Split('\n');
                GUIStyle st = TipTextStyle();
                float limit = Mathf.Min(Sc(600f), Screen.width * 0.62f);
                float maxLine = 0f;
                for (int i = 0; i < _tipLines.Length; i++) {
                    float lw = st.CalcSize(new GUIContent(_tipLines[i])).x;
                    if (lw > maxLine) maxLine = lw;
                }
                float innerW = Mathf.Max(Sc(40f), Mathf.Min(limit, maxLine + 2f));
                // 整块测量（含换行），样式零内边距 → 高度就是文字实际需要的高度，不多不少。
                float h = st.CalcHeight(_tipContent, innerW);
                _tipInnerW = innerW;
                _tipW = innerW + Sc(12f);
                _tipH = h + Sc(8f);
            }
            float x = Mathf.Clamp(mp.x + 16f, 2f, Mathf.Max(2f, Screen.width - _tipW - 6f));
            float y = Mathf.Clamp(mp.y + 16f, 2f, Mathf.Max(2f, Screen.height - _tipH - 10f));
            Color __oldTipColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, __alpha);
            Rect __tipRect = new Rect(x, y, _tipW, _tipH);
            DrawRounded(__tipRect, SRApi.Ctl.ThemePanelBg, SRApi.Ctl.ThemeRadius);
            GUI.Label(new Rect(__tipRect.x + Sc(6f), __tipRect.y + Sc(4f), _tipInnerW, _tipH - Sc(8f)), _tipContent, TipTextStyle());
            GUI.color = __oldTipColor;
        }
// 分区：RowHooks（功能对通用条目行的扩展点）
// SR.Settings 渲染条目行时不硬编码 section/key，功能在 Initialize/SelfReg 里注册以下扩展点。
// 行可见性、同伴条目（同一行右侧再画一个控件）、行专用滑块范围、下拉框宽度、枚举选项过滤。
// SRApi.RowFilters 是布局过滤而非权限门禁：只决定条目是否单独成行 / 是否在设置页重复渲染。

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    public delegate float RowComboWidthFn(ConfigEntryBase entry);
    public delegate bool RowEnumFilterFn(ConfigEntryBase entry, string enumName, int value);
    // 字符串条目的下拉候选扩展点：命中即用下拉框渲染该条目。
    // values = 落盘值，displayNames = 界面显示（两者等长）。
    public delegate bool RowComboOptionsFn(ConfigEntryBase entry, out string[] values, out string[] displayNames);
    public delegate void PageRenderFn();

    public static readonly List<RowComboWidthFn> RowComboWidths = new List<RowComboWidthFn>();
    public static readonly List<RowEnumFilterFn> RowEnumFilters = new List<RowEnumFilterFn>();
    public static readonly List<RowComboOptionsFn> RowComboOptions = new List<RowComboOptionsFn>();

    // 该条目当前是否显示（任一过滤器返回 false 即不显示）
    public static bool RowVisible(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowFilters.Count; i++) {
            try { if (!SRApi.RowFilters[i](entry)) return false; } catch (Exception __ex) { Guard.Log("行可见性过滤", __ex); }
        }
        return true;
    }

    // 同伴条目 key（同一行右侧再渲染一个控件），null = 无
    public static string RowCompanion(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowCompanions.Count; i++) {
            try { string k = SRApi.RowCompanions[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("同伴条目", __ex); }
        }
        return null;
    }

    // 同伴条目 key（同一行右侧"再渲染一个控件"），null = 无
    public static string RowExtra(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowExtras.Count; i++) {
            try { string k = SRApi.RowExtras[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("行附加控件", __ex); }
        }
        return null;
    }

    // 行专用滑块范围（命中即用滑块渲染这个数值条目；fmt = 数值显示格式）
    public static bool RowSliderRange(ConfigEntryBase entry, out float min, out float max, out string fmt) {
        min = 0f; max = 0f; fmt = "0.0";
        for (int i = 0; i < SRApi.RowSliders.Count; i++) {
            try { if (SRApi.RowSliders[i](entry, out min, out max, out fmt)) return true; } catch (Exception __ex) { Guard.Log("行滑块范围", __ex); }
        }
        return false;
    }

    // 行滑块的浮点步进（可选）：float 条目按 step 步进。
    public delegate bool RowSliderStepFn(ConfigEntryBase entry, out float step);
    public static readonly List<RowSliderStepFn> RowSliderSteps = new List<RowSliderStepFn>();

    public static bool RowSliderStep(ConfigEntryBase entry, out float step) {
        step = 0f;
        for (int i = 0; i < RowSliderSteps.Count; i++) {
            try {
                float s;
                if (!RowSliderSteps[i](entry, out s)) continue;
                if (s <= 0.0001f) continue;      //非法步进（0/负）当没命中
                step = s; return true;
            } catch (Exception __ex) { Guard.Log("行滑块步进", __ex); }
        }
        return false;
    }

    // 下拉框宽度（0 = 用默认宽度）
    public static float RowComboWidth(ConfigEntryBase entry) {
        for (int i = 0; i < RowComboWidths.Count; i++) {
            try { float w = RowComboWidths[i](entry); if (w > 0f) return w; } catch (Exception __ex) { Guard.Log("下拉框宽度", __ex); }
        }
        return 0f;
    }

    // 行下拉候选（命中即用下拉框渲染这个字符串条目）；候选不合法（空/长度不一致）视作没命中
    public static bool RowComboOptionList(ConfigEntryBase entry, out string[] values, out string[] displayNames) {
        values = null; displayNames = null;
        for (int i = 0; i < RowComboOptions.Count; i++) {
            try {
                string[] v, n;
                if (!RowComboOptions[i](entry, out v, out n)) continue;
                if (v == null || n == null || v.Length == 0 || v.Length != n.Length) continue;
                values = v; displayNames = n; return true;
            } catch (Exception __ex) { Guard.Log("行下拉候选", __ex); }
        }
        return false;
    }

    // 枚举选项是否保留（任一过滤器返回 false 即从下拉框里过滤掉）
    public static bool RowEnumAllowed(ConfigEntryBase entry, string enumName, int value) {
        for (int i = 0; i < RowEnumFilters.Count; i++) {
            try { if (!RowEnumFilters[i](entry, enumName, value)) return false; } catch (Exception __ex) { Guard.Log("枚举过滤", __ex); }
        }
        return true;
    }

    // 功能自绘页：功能用通用条目行拼自己的页面；注册后 SR.Window 优先调用它。
    public static readonly Dictionary<string, PageRenderFn> PageRenderers = new Dictionary<string, PageRenderFn>();

    public static void RegisterPage(string section, PageRenderFn render) {
        if (string.IsNullOrEmpty(section) || render == null) return;
        PageRenderers[section] = render;
        _pageLists.Remove(section); //单页注册覆盖多页（同一 section 不应混用两种注册）
        OnNavChanged(section);      //晚注册（首次扫描之后）：立刻重建侧栏，否则页面存在但栏目不显示
    }

    // 一个栏目下的多个子页（顶部页签切换）：如会话= 对话窗口 / 对话内容
    private sealed class PageInfo {
        public string Id;      //稳定 id（选中态与页面外框判断用）
        public string Zh;      //中文显示名（运行时按当前语言翻译，切语言立即生效）
        public string En;      //英文显示名
        public int Order;      //页签顺序
        public PageRenderFn Render;
    }
    private static readonly Dictionary<string, List<PageInfo>> _pageLists = new Dictionary<string, List<PageInfo>>();
    private static readonly Dictionary<string, string> _pageSel = new Dictionary<string, string>();

    // 多页注册：单页走 SR.RegisterPage(section, render)；多页用 6 参重载逐页注册（段名 + 页id + 中英文名 + 顺序 + 渲染）。
    // 注册 ≥2 页后核心自动画页签并只渲染当前页；多页注册会清掉该段的单页渲染器，两种注册不可混用。
    public static void RegisterPage(string section, string pageId, string nameZh, string nameEn, int order, PageRenderFn render) {
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(pageId) || render == null) return;
        List<PageInfo> list;
        if (!_pageLists.TryGetValue(section, out list)) { list = new List<PageInfo>(); _pageLists[section] = list; }
        for (int i = 0; i < list.Count; i++) {
            if (list[i].Id == pageId) {
                list[i].Zh = nameZh; list[i].En = nameEn; list[i].Order = order; list[i].Render = render;
                list.Sort((a, b) => a.Order.CompareTo(b.Order));
                return;
            }
        }
        list.Add(new PageInfo { Id = pageId, Zh = string.IsNullOrEmpty(nameZh) ? pageId : nameZh, En = nameEn, Order = order, Render = render });
        list.Sort((a, b) => a.Order.CompareTo(b.Order));
        PageRenderers.Remove(section);
    }

    //当前选中的子页 id（未选中时默认第一页）, 供页面外框（顶栏/底栏）判断是否属于自己那页
    public static string SelectedPageId(string section) {
        string id;
        if (!string.IsNullOrEmpty(section) && _pageSel.TryGetValue(section, out id)) return id;
        List<PageInfo> list;
        if (!string.IsNullOrEmpty(section) && _pageLists.TryGetValue(section, out list) && list.Count > 0) return list[0].Id;
        return null;
    }

    // Ctrl+滚轮切页：在栏目页列表里循环前后移动一位（顺序 = 注册的 order）。
    public static void CyclePage(string section, int dir) {
        try {
            List<PageInfo> list;
            if (string.IsNullOrEmpty(section) || !_pageLists.TryGetValue(section, out list) || list.Count <= 1) return;
            string cur = SelectedPageId(section);
            int idx = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].Id == cur) { idx = i; break; }
            int nx = ((idx + (dir >= 0 ? 1 : -1)) % list.Count + list.Count) % list.Count;
            _pageSel[section] = list[nx].Id;
        } catch (Exception __ex) { Guard.Log("切换子页", __ex); }
    }

    public static bool HasPage(string section) {
        return !string.IsNullOrEmpty(section) && (PageRenderers.ContainsKey(section) || _pageLists.ContainsKey(section));
    }

    //文本框（右键清空）：命中该框的右键会清空内容并吃掉事件。
    public static string ClearableTextField(string text, GUIStyle style, params GUILayoutOption[] options) {
        string r = GUILayout.TextField(text, style, options);
        Rect rr = GUILayoutUtility.GetLastRect();
        Event e = Event.current;
        if (e != null && e.type == EventType.MouseDown && e.button == 1 && rr.Contains(e.mousePosition)) { r = ""; e.Use(); }
        return r;
    }

    public static string ClearableTextField(string text, int maxLength, GUIStyle style, params GUILayoutOption[] options) {
        string r = GUILayout.TextField(text, maxLength, style, options);
        Rect rr = GUILayoutUtility.GetLastRect();
        Event e = Event.current;
        if (e != null && e.type == EventType.MouseDown && e.button == 1 && rr.Contains(e.mousePosition)) { r = ""; e.Use(); }
        return r;
    }


    // 表格通用绘制原语（三处列表共用）。
    // 纯白填充用 Unity 内置 whiteTexture（常驻，无需自建/销毁）。
    public static Texture2D Px() { return Texture2D.whiteTexture; }
    // 圆角矩形：中心/边用纯色填充，四角用运行时生成的圆形 alpha 贴图（缓存，不逐帧分配）。
    private static Texture2D _roundTex;
    private static Texture2D RoundTex() {
        if (_roundTex != null) return _roundTex;
        const int N = 32;
        _roundTex = new Texture2D(N, N, TextureFormat.ARGB32, false);
        _roundTex.hideFlags = HideFlags.HideAndDontSave;
        _roundTex.wrapMode = TextureWrapMode.Clamp;
        Color[] px = new Color[N * N];
        for (int y = 0; y < N; y++) {
            for (int x = 0; x < N; x++) {
                float dx = (x + 0.5f) / N * 2f - 1f;
                float dy = (y + 0.5f) / N * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - d) * N * 0.5f);
                px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
        }
        _roundTex.SetPixels(px);
        _roundTex.Apply();
        return _roundTex;
    }

    public static void DrawRounded(Rect r, Color c, float radius) {
        try {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            float rad = Mathf.Clamp(radius, 0f, Mathf.Min(r.width, r.height) * 0.5f);
            if (rad <= 0.5f) { DrawFill(r, c); return; }
            Texture2D tex = RoundTex();
            Color old = GUI.color;
            GUI.color = c;
            // 中间 + 四条边（纯色）
            DrawFill(new Rect(r.x + rad, r.y, r.width - rad * 2f, r.height), c);
            DrawFill(new Rect(r.x, r.y + rad, rad, r.height - rad * 2f), c);
            DrawFill(new Rect(r.xMax - rad, r.y + rad, rad, r.height - rad * 2f), c);
            // 四角（圆形的 1/4，按 UV 取向）
            DrawCorner(tex, new Rect(r.x, r.y, rad, rad), new Rect(0f, 0.5f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.xMax - rad, r.y, rad, rad), new Rect(0.5f, 0.5f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.x, r.yMax - rad, rad, rad), new Rect(0f, 0f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.xMax - rad, r.yMax - rad, rad, rad), new Rect(0.5f, 0f, 0.5f, 0.5f));
            GUI.color = old;
        } catch (Exception __ex) { Guard.Log("圆角绘制", __ex); }
    }

    private static void DrawCorner(Texture2D tex, Rect dst, Rect uv) {
        GUI.DrawTextureWithTexCoords(dst, tex, uv);
    }
    // 默认色 (1,1,1,0.22) 保持不变：本方法对外，功能页自绘的线也在用。
    public static void DrawLine(Rect r) { DrawLine(r, new Color(1f, 1f, 1f, 0.22f)); }

    public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }

    // 纯色填充（仅 Repaint 趟画），供表头底与隔行底使用。
    // alpha <= 0 直接跳过：关闭表头底/隔行底只需把颜色 alpha 设为 0。
    public static void DrawFill(Rect r, Color c) {
        if (c.a <= 0f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        try {
        GUI.color = c;
        GUI.DrawTexture(r, Px());
        } finally { GUI.color = old; }
    }

    //表格配色的只读视图：供功能页自绘"表外但属于表"的元素对齐（如联机页钉住列的分隔线）。
    public static Color TableLineStrong { get { return CurStyle.LineStrong; } }
    public static void DrawRect(Rect r, float a) {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        try {
        GUI.color = new Color(0.55f, 0.78f, 1f, a);
        GUI.DrawTexture(r, Px());
        } finally { GUI.color = old; }
    }


    //页签行（多子页栏目）：要在页面外框（RenderPageHeader）之前调用，页签才会永远在最上面。
    // 页签行左侧的“前缀”绘制点（每个栏目一个）：模块可在与页签同一行最左侧放自己的控件。
    private static readonly Dictionary<string, Action> _tabPrefix = new Dictionary<string, Action>();

    public static void RegisterTabPrefix(string section, Action draw) {
        if (!string.IsNullOrEmpty(section) && draw != null) _tabPrefix[section] = draw;
    }

    public static void RenderPageTabs(string section) {
        List<PageInfo> list;
        if (string.IsNullOrEmpty(section) || !_pageLists.TryGetValue(section, out list) || list.Count <= 1) return;
        try {
            string sel = SelectedPageId(section);
            GUILayout.BeginHorizontal();
            Action pre;
            if (_tabPrefix.TryGetValue(section, out pre)) {
                try { pre(); } catch (Exception __pe) { Guard.Log("页签前缀(" + section + ")", __pe); }
                GUILayout.Space(Sc(8));
            }
            for (int i = 0; i < list.Count; i++) {
                bool on = list[i].Id == sel;
                string nm = string.IsNullOrEmpty(list[i].En) ? list[i].Zh : T(list[i].Zh, list[i].En);
                if (GUILayout.Button(nm, on ? _selItem : _item, GUILayout.Height(Sc(26)))) _pageSel[section] = list[i].Id;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
        } catch (Exception __ex) { Guard.Log("页签(" + section + ")", __ex); }
    }

    public static void RenderPage(string section) {
        List<PageInfo> list;
        if (!string.IsNullOrEmpty(section) && _pageLists.TryGetValue(section, out list) && list.Count > 0) {
            try {
                string sel = SelectedPageId(section);
                if (list.Count <= 1) _pageSel[section] = sel;
                for (int i = 0; i < list.Count; i++) {
                    if (list[i].Id == sel) { list[i].Render(); break; }
                }
            } catch (Exception __ex) { Guard.Log("功能页渲染(" + section + ")", __ex); }
            return;
        }
        PageRenderFn f;
        if (!PageRenderers.TryGetValue(section, out f)) return;
        try { f(); } catch (Exception __ex) { Guard.Log("功能页渲染(" + section + ")", __ex); }
    }

    // 功能页“外框”：滚动区之外的自绘内容（顶部工具行 / 底部输入行），由功能注册、管理器在栏目选中时调用。
    private static readonly Dictionary<string, Action> _pageHeaders = new Dictionary<string, Action>();
    private static readonly Dictionary<string, Action> _pageFooters = new Dictionary<string, Action>();

    public static void RegisterPageChrome(string section, Action header, Action footer) {
        if (string.IsNullOrEmpty(section)) return;
        if (header != null) _pageHeaders[section] = header;
        if (footer != null) _pageFooters[section] = footer;
    }

    public static void RenderPageHeader(string section) {
        Action f;
        if (string.IsNullOrEmpty(section) || !_pageHeaders.TryGetValue(section, out f)) return;
        try { f(); } catch (Exception __ex) { Guard.Log("功能页顶栏(" + section + ")", __ex); }
    }

    public static void RenderPageFooter(string section) {
        Action f;
        if (string.IsNullOrEmpty(section) || !_pageFooters.TryGetValue(section, out f)) return;
        try { f(); } catch (Exception __ex) { Guard.Log("功能页底栏(" + section + ")", __ex); }
    }

    // 统一控件高度：按钮 / 输入框 / 下拉框 / 复选框同高（默认 26，按 UI 缩放；功能可用 ControlHeight 覆盖）。
    public static float ControlH() {
        try { return Sc(ControlHeight > 0f ? ControlHeight : 26f); } catch { return Sc(26f); }
    }

    }
}
