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
        public static float ControlH() { return SR.ControlH(); }
        // 表格尺寸令牌：行高/表头高/拖拽条/滚动条宽/最小高度（全部随 UI 缩放，页面不再写死字面量）。
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
            try { int v = System.Convert.ToInt32(entry.BoxedValue); if (v > 0) cur = v; } catch { }
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
        private static float _tipW, _tipH;
        private static bool _tipWrap;
        private static GUIContent TipLineContent(string s) { _tipLineContent.text = s; return _tipLineContent; }
        // 关闭界面时清空提示缓存，避免 tooltip 文本跨帧残留（表头提示延迟消失）。
        public static void ClearTip() {
            try { _tipKey = null; _tipLines = new string[0]; _tipContent.text = ""; } catch { }
        }

        // 悬浮提示显示延迟（参考 Dear ImGui 的 tooltip 延迟）与淡入，避免鼠标扫过控件时提示乱闪。
        private const float TipDelay = 0.2f;
        private const float TipFade = 0.12f;
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
            if (tip != _tipKey) {
                _tipKey = tip;
                _tipContent.text = tip;
                _tipLines = tip.Split('\n');
                float limit = Mathf.Min(560f, Screen.width * 0.8f);
                float maxLine = 0f;
                for (int i = 0; i < _tipLines.Length; i++) {
                    float lw = _tooltip.CalcSize(TipLineContent(_tipLines[i])).x;
                    if (lw > maxLine) maxLine = lw;
                }
                _tipWrap = maxLine + 20f > limit;
                _tooltip.wordWrap = _tipWrap;
                if (_tipWrap) { _tipW = limit; _tipH = _tooltip.CalcHeight(_tipContent, _tipW); }
                else { _tipW = maxLine + 18f; _tipH = _tooltip.CalcSize(_tipContent).y; }
            }
            _tooltip.wordWrap = _tipWrap;   //样式是全局共享的，每帧都要设回本条 tooltip 的模式
            float x = Mathf.Clamp(mp.x + 16, 2f, Mathf.Max(2f, Screen.width - _tipW - 6));
            float y = Mathf.Clamp(mp.y + 16, 2f, Mathf.Max(2f, Screen.height - _tipH - 10));
            Color __oldTipColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, __alpha);
            Rect __tipRect = new Rect(x, y, _tipW, _tipH + 8);
            DrawRounded(__tipRect, new Color(0.08f, 0.08f, 0.09f, 0.96f), Sc(6f));
            GUI.Label(new Rect(__tipRect.x + Sc(6f), __tipRect.y + Sc(4f), __tipRect.width - Sc(12f), __tipRect.height - Sc(8f)), _tipContent, _labelWrap);
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

    //统一入口的参数（表格内容/状态由调用方填；绘制结果回填到列表/矩形里）

    // 行点击状态：按下行用于过滤"从行上按下、在行上松开"的点击（拖列宽/滚动条释放不会误触发）。
    private static int _rowDownRow = -1;
    private static int _emptyHintFrame = -1;   // 空提示绘制帧（防重复）
    private static int _rowLastClickRow = -1;
    private static float _rowLastClickTime = -999f;

    //统一入口：表头 + 行命中 + 行单元格 + 钉住列命中（滚动条/输入由调用方按需要另调）
    public static void TableDraw(SRApi.TableArgs a) {
        if (a == null || a.colW == null || a.vis == null) return;
        try {
            // vis 由 order + hidden 自行推导：TableColMove 换位后只更新 order，调用方无需再维护 vis。
            DeriveVisibleCols(a);
            float headerH = a.headerH > 0f ? a.headerH : Sc(24);
            if (a.vertical) headerH = (a.headerH > 0f ? a.headerH : Sc(22)) * Mathf.Max(1, a.vis != null ? a.vis.Count : 0); // 纵向时表头高 = 单格高 × 列数
            float rowH = a.rowH > 0f ? a.rowH : Sc(26);
            // order 是换列算法的必需输入；调用方未赋时这里兜底成恒等序。
            if (a.drawHeader && (a.order == null || a.order.Length != a.colW.Length)) {
                int[] ident = new int[a.colW.Length];
                for (int i = 0; i < ident.Length; i++) ident[i] = i;
                a.order = ident;
            }
            if (a.drawHeader) {
                DrawTableHeader(a.vis, a.pinCol, a.colW, a.scrollX, a.box, a.stripW, headerH, a.mp, a.title, a.moveCol,
                    a.pinW, a.pinX, a.pinTitle, a.headerCells, a.headerCols, a.vertical, a.scrollY, a.headerLeft, out a.pinHeader);
                // 表头下的横线：只在核心自己画表头时画一条（drawHeader=false 时由调用方自己画）。
                DrawLine(new Rect(a.box.x, a.box.y + headerH - 1f, a.stripW, 1f), CurStyle.LineStrong);
            }
            TableRowRects(a.rowCount, rowH, a.scrollY, a.rowsH, a.box.y, headerH, a.rowsClip, a.box.x, a.stripW, a.rowRects, a.rowRows);
            // 表头 Shift+左键：交给页面处理列头专属开关（如联机列表的“质量系数”）。
            // 必须用 MouseDown 判定：IMGUI 到 MouseUp 时修饰键可能已丢。
            try {
                Event __he = Event.current;
                if (__he != null && __he.type == EventType.MouseDown && __he.button == 0 && __he.shift
                    && a.headerShiftClick != null && a.headerCells.Count > 0) {
                    for (int __hk = 0; __hk < a.headerCells.Count && __hk < a.headerCols.Count; __hk++) {
                        if (!a.headerCells[__hk].Contains(__he.mousePosition)) continue;
                        a.headerShiftClick(a.headerCols[__hk]);
                        __he.Use();
                        break;
                    }
                }
            } catch (Exception __ex) { Guard.Log("表头Shift点击派发", __ex); }
            // 行命中：用 rowRects 每帧回填 rowHover（只在行区裁剪内，且不算钉住列区域）。
            try {
                Event __re = Event.current;
                int __hov = -1;
                if (__re != null && a.rowsClip.width > 0f && a.rowsClip.height > 0f && a.rowsClip.Contains(__re.mousePosition)) {
                    Vector2 __rmp = __re.mousePosition;
                    bool __inPin = a.pinW > 0f && __rmp.x >= a.pinX;
                    if (!__inPin) {
                        for (int k = 0; k < a.rowRects.Count && k < a.rowRows.Count; k++) {
                            if (a.rowRects[k].Contains(__rmp)) { __hov = a.rowRows[k]; break; }
                        }
                    }
                }
                a.rowHover = __hov;
            } catch (Exception __ex) { Guard.Log("表格行命中", __ex); }
            // 行选中高亮：rowSelected 指定的行由核心画整行高亮。
            if (a.rowSelected != null && a.rowRects != null && a.rowRects.Count > 0) {
                try {
                    for (int __i = 0; __i < a.rowRects.Count; __i++) {
                        if (!a.rowSelected(a.rowRows[__i])) continue;
                        Color __oc = GUI.color;
                        try { GUI.color = new Color(0.55f, 0.78f, 1f, 0.22f); GUI.DrawTexture(a.rowRects[__i], Px()); }
                        finally { GUI.color = __oc; }
                    }
                } catch (Exception __ex) { Guard.Log("行选中高亮", __ex); }
            }
            //表头分隔线拖列宽 + 悬停换 <-> 光标（与房间列表完全同一实现）
            if (a.headerCells.Count > 0 && !a.vertical) { // 纵向不做列宽/行高拖动
                bool inBox = a.box.Contains(a.mp);
                for (int k = 0; k < a.headerCells.Count; k++) {
                    Rect cell = a.headerCells[k];
                    int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
                    //列头悬停提示（与房间列表一致：悬停在表头格上显示该列说明）
                    if (a.tip != null && inBox && cell.Contains(a.mp)) {
                        string tip = null;
                        try { tip = a.tip(ci); } catch { tip = null; }
                        if (!string.IsNullOrEmpty(tip)) GUI.tooltip = tip;
                    }
                    // 排序方向指示（给了 sortCol 才画）。
                    if (a.sortCol >= 0 && ci == a.sortCol) {
                        SR.Ctl.SecHeaderCenter.Draw(new Rect(cell.xMax - Sc(14), cell.y, Sc(12), cell.height),
                            new GUIContent(a.sortAsc ? "\u25B2" : "\u25BC"), false, false, false, false);
                    }
                    if (!a.resizable) continue;
                    Rect sep = new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
                    if (inBox && sep.Contains(a.mp)) CursorResize = true;
                    TableColResize(Event.current, cell, ci, a.mp, inBox, a.colW,
                        ref a.dragCol, ref a.dragX0, ref a.dragW0, ref a.scrollColsTmp, ref a.scrollColsAtDragTmp);
                }
                //列头拖动换位（与房间列表同一实现；需要调用方给出 order/回调）
                if (a.reorderable && a.order != null) {
                    if (a.allCols.Count != a.colW.Length) { a.allCols.Clear(); for (int i = 0; i < a.colW.Length; i++) a.allCols.Add(i); }
                    // 列序长度与该表列数不一致时按本表列数重建，否则拖动看不出变化。
                    if (a.order != null && a.colW != null && a.colW.Length > 0 && a.order.Length != a.colW.Length) {
                        int[] __ord = new int[a.colW.Length];
                        for (int i = 0; i < __ord.Length; i++) __ord[i] = i;
                        a.order = __ord;
                    }
                    // 拖动中只把“按下时的那一列”交给 TableColMove，保证松开时 moveCol == ci。
                    int __dragK = -1;
                    if (a.moveCol >= 0) {
                        for (int k = 0; k < a.headerCells.Count; k++) {
                            int __c2 = (a.headerCols.Count > k) ? a.headerCols[k] : k;
                            if (__c2 == a.moveCol) { __dragK = k; break; }
                        }
                        if (__dragK < 0 && a.moveCol < a.headerCells.Count) __dragK = a.moveCol;   //兜底：按下时记录的就是本表列下标
                    }
                    // 落点列列表用 headerCols（显示位 -> 数据列），不能用 allCols（自然序）。
                    List<int> __cols = (a.headerCols != null && a.headerCols.Count > 0) ? a.headerCols : a.allCols;
                    for (int k = 0; k < a.headerCells.Count; k++) {
                        if (__dragK >= 0 && k != __dragK) continue; // 拖动中：只处理拖动的那一列
                        int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
                        TableColMove(Event.current, a.headerCells[k], ci, k, __cols, a.mp, inBox, a.headerCells, a.order,
                            a.applyOrder, a.saveOrder, a.click, ref a.moveCol, ref a.moveX0, ref a.moveDx,
                            ref a.scrollColsTmp, ref a.scrollColsAtDragTmp, a.vertical);
                    }
                    // 兜底：拖动中逐格循环未命中且本帧为 MouseUp 时就地完成换位。
                    if (a.moveCol >= 0 && Event.current != null && Event.current.type == EventType.MouseUp && Event.current.button == 0) {
                        int __mv = a.moveCol; int __k = -1;
                        for (int k = 0; k < a.headerCells.Count; k++) { int __c3 = (a.headerCols.Count > k) ? a.headerCols[k] : k; if (__c3 == __mv) { __k = k; break; } }
                        if (__k < 0 && __mv < a.headerCells.Count) __k = __mv;
                        if (__k >= 0) {
                            TableColMove(Event.current, a.headerCells[__k], __mv, __k, __cols, a.mp, a.box.Contains(a.mp), a.headerCells, a.order,
                                a.applyOrder, a.saveOrder, a.click, ref a.moveCol, ref a.moveX0, ref a.moveDx,
                                ref a.scrollColsTmp, ref a.scrollColsAtDragTmp, a.vertical);
                        } else { a.moveCol = -1; a.moveDx = 0f; }
                    }
                }
            }
            // 列头拖拽动画（仅 Repaint）：跟手浮动块 + 插入指示线，纯绘制不改数据。
            if (a.moveCol >= 0 && Event.current != null && Event.current.type == EventType.Repaint) {
                try {
                    int __mk = -1;
                    for (int k = 0; k < a.headerCells.Count; k++) { int __c = (a.headerCols.Count > k) ? a.headerCols[k] : k; if (__c == a.moveCol) { __mk = k; break; } }
                    if (__mk < 0 && a.moveCol < a.headerCells.Count) __mk = a.moveCol;
                    if (__mk >= 0) {
                        Rect __src = a.headerCells[__mk];
                        int __tgt = __mk;
                        for (int j = 0; j < a.headerCells.Count; j++) { if (a.mp.x < a.headerCells[j].xMax) { __tgt = j; break; } __tgt = a.headerCells.Count - 1; }
                        Color __old = GUI.color;
                        try {
                            //插入指示线：仅当落点不同、且落点不在"钉住列"（pinCol）区域时才画，避免末尾多余色块
                            if (__tgt != __mk && !(a.pinCol > 0 && a.headerCells[__tgt].x >= a.pinX)) {
                                GUI.color = new Color(1f, 0.85f, 0.25f, 0.95f);
                                GUI.Box(new Rect(a.headerCells[__tgt].xMax - Sc(1), __src.y, Sc(2), __src.height), GUIContent.none);
                            }
                            //跟手"幽灵"：实心半透明块（与房间列表观感一致）
                            GUI.color = new Color(1f, 1f, 1f, 0.45f);
                            GUI.Box(new Rect(a.mp.x - __src.width * 0.5f, __src.y - Sc(2), __src.width, __src.height), GUIContent.none);
                        } finally { GUI.color = __old; }
                    }
                } catch (Exception __ex) { Guard.Log("SR.Ctl", __ex); }
            }
            TablePinnedRects(a.rowCount, rowH, a.scrollY, a.rowsH, a.box.y, headerH, a.pinX, a.pinW, a.pinRects, a.pinRows);
            //钉住列按钮：由调用方提供内容（房间列表的加入、方块/EX 的操作），本控件负责定位与命中
            if (a.pinW > 0f && a.pinDraw != null) {
                for (int k = 0; k < a.pinRects.Count && k < a.pinRows.Count; k++) {
                    try { a.pinDraw(a.pinRows[k], a.pinRects[k], k); } catch (Exception __pd) { Guard.Log("钉住列按钮", __pd); }
                }
            }
            //空列表提示：调用方给文本，本控件负责居中（三处列表同一观感）
            if (a.rowCount == 0 && a.emptyHint != null && _emptyHintFrame != Time.frameCount) {
                _emptyHintFrame = Time.frameCount;   // 同一帧只画一次（防止页面/核心重复绘制出现两条提示）
                string eh = null;
                try { eh = a.emptyHint(); } catch { eh = null; }
                if (!string.IsNullOrEmpty(eh)) {
                    Rect rc = a.rowsClip;
                    SR.Ctl.LabelClipCenter.Draw(new Rect(rc.x, rc.y + Mathf.Max(0f, (a.rowsH - rowH * 2f) * 0.5f), Mathf.Max(Sc(60), a.stripW), rowH * 2f),
                        new GUIContent(eh), false, false, false, false);
                }
            }
            Color clipSavedColor = GUI.color; // 异常时也要还原（GUI.color 泄漏会污染游戏界面）
            GUI.BeginClip(new Rect(a.box.x, a.box.y + headerH, a.stripW, a.rowsH));
            try {
            // 只遍历可见行：O(可见行数) 而不是 O(总行数)，大列表滚动时显著省 CPU。
            int __firstRow = Mathf.Max(0, Mathf.FloorToInt(a.scrollY / Mathf.Max(1f, rowH)));
            int __lastRow = Mathf.Min(a.rowCount - 1, Mathf.CeilToInt((a.scrollY + a.rowsH) / Mathf.Max(1f, rowH)));
            for (int i = __firstRow; i <= __lastRow; i++) {
                float y = i * rowH - a.scrollY;
                string[] rc = a.rowCells != null ? a.rowCells(i) : null;
                float alpha = a.rowAlpha != null ? a.rowAlpha(i) : 1f;
                // 隔行底：按真实行号取奇偶（行号不随滚动变化，条纹稳定）；行透明时底也跟着淡。
                if ((i & 1) == 1) {
                    Color ab = CurStyle.RowAltBg;
                    if (alpha < 1f) ab.a *= alpha;
                    DrawFill(new Rect(0f, y, a.stripW, rowH), ab);
                }
                // 行悬停反馈：加在隔行底之上、文字之下；默认开启，rowHover 由核心每帧回填。
                float __hy0 = Mathf.Max(y, 0f), __hy1 = Mathf.Min(y + rowH, a.rowsH);
                if (a.hoverHighlight && i == a.rowHover && __hy1 > __hy0) {
                    Color __hc = new Color(1f, 1f, 1f, 0.06f);
                    if (alpha < 1f) __hc.a *= alpha;
                    DrawFill(new Rect(0f, __hy0, a.stripW, __hy1 - __hy0), __hc);
                }
                Color prev = GUI.color;
                if (alpha < 1f) GUI.color = new Color(1f, 1f, 1f, alpha);
                DrawTableCells(a.vis, a.pinCol, a.alignLeftCol, rowH, a.scrollX, y, a.colW, rc);
                GUI.color = prev;
                DrawLine(new Rect(0f, y + rowH - 1f, a.stripW, 1f), CurStyle.LineLight);
            }
            } finally { GUI.EndClip(); GUI.color = clipSavedColor; }
            //右键隐藏列 / Shift+右键恢复全部；表头右端+N一键恢复（与房间列表同一套行为）
            if (a.hideable && a.hidden != null && Event.current != null && a.box.Contains(a.mp)) {
                Event e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 1) {
                    if (e.shift) {
                        if (a.hidden != null && a.hidden.Count > 0) a.hidden.Clear();
            // Shift+右键还原时列宽也一并还原到默认值。
            if (a.defaultColW != null && a.colW != null && a.defaultColW.Length == a.colW.Length) {
                for (int __i = 0; __i < a.colW.Length; __i++) a.colW[__i] = a.defaultColW[__i];
            }
                        if (a.resetAll != null) { try { a.resetAll(); } catch (Exception __exr) { Guard.Log("表格还原全部", __exr); } }
                        e.Use();
                    } else {
                        for (int k = 0; k < a.headerCells.Count; k++) {
                            if (!a.headerCells[k].Contains(a.mp)) continue;
                            int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
                            if (a.colW.Length - a.hidden.Count > 1) a.hidden.Add(ci); //至少留一列
                            e.Use();
                            break;
                        }
                    }
                }
            }
            // 列宽拖动收尾：TableColResize 只处理 MouseDown/MouseDrag，这里清状态并回调落盘。
            if (a.dragCol >= 0 && Event.current != null && Event.current.type == EventType.MouseUp) {
                a.dragCol = -1;
                BlockWindowDrag = false;
                if (a.saveWidths != null) { try { a.saveWidths(); } catch (Exception __sw) { Guard.Log("表格列宽保存", __sw); } }
            }
            // 行点击/双击：左键从同一行按下再松开才派发；已被其它交互消费的事件（type=Used）不会进到这里。
            try {
                Event __ce = Event.current;
                if (__ce != null) {
                    if (__ce.type == EventType.MouseDown && __ce.button == 0) {
                        _rowDownRow = a.rowHover;
                    } else if (__ce.type == EventType.MouseUp && __ce.button == 1) {
                        // 行右键（核心统一派发；钉住操作列区域不算行命中）。
                        int __rr = a.rowHover;
                        if (__rr >= 0 && a.rowRightClick != null) {
                            try { a.rowRightClick(__rr); } catch (Exception __rc2) { Guard.Log("表格行右键", __rc2); }
                            __ce.Use();
                        }
                    } else if (__ce.type == EventType.MouseUp && __ce.button == 0) {
                        int __clicked = (a.rowHover >= 0 && a.rowHover == _rowDownRow) ? a.rowHover : -1;
                        _rowDownRow = -1;
                        if (__clicked >= 0) {
                            if (a.rowClick != null) { try { a.rowClick(__clicked); } catch (Exception __rc) { Guard.Log("表格行点击", __rc); } }
                            float __now = Time.unscaledTime;
                            if (_rowLastClickRow == __clicked && __now - _rowLastClickTime <= 0.35f) {
                                _rowLastClickRow = -1; _rowLastClickTime = -999f;
                                if (a.rowDoubleClick != null) { try { a.rowDoubleClick(__clicked); } catch (Exception __rd) { Guard.Log("表格行双击", __rd); } }
                            } else {
                                _rowLastClickRow = __clicked; _rowLastClickTime = __now;
                            }
                        }
                    }
                }
            } catch (Exception __ex) { Guard.Log("表格行点击派发", __ex); }
        } catch (Exception __ex) { Guard.Log("表格绘制", __ex); }
    }

    // 从 order + hidden 推导可见列（三处表格共用同一规则）：调用方只维护 order/hidden，无需再维护 vis 副本。
    // TableDraw 在绘制前调用；order 与 colW 尺寸不一致时保持调用方给的 vis 不变。
    private static void DeriveVisibleCols(SRApi.TableArgs a) {
        if (a == null || a.order == null || a.colW == null || a.order.Length != a.colW.Length) return;
        if (a.vis == null) a.vis = new List<int>(a.order.Length);
        a.vis.Clear();
        for (int i = 0; i < a.order.Length; i++) {
            int ci = a.order[i];
            if (ci < 0 || ci >= a.colW.Length) continue;
            if (a.hidden != null && a.hidden.Contains(ci)) continue;
            a.vis.Add(ci);
        }
    }

    //表头绘制（联机房间列表同款）：可滚动列 + 钉住右列；输出每格屏幕矩形（命中用）与钉住列表头矩形。
    public static void DrawTableHeader(List<int> vis, int pinCol, float[] colW, float scrollX, Rect box, float stripW, float headerH,
        Vector2 mp, System.Func<int, string> title, int moveCol, float pinW, float pinX, string pinTitle,
        List<Rect> cells, List<int> cellCols, bool vertical, float scrollY, bool headerLeft, out Rect pinHeader) {
        cells.Clear(); cellCols.Clear(); pinHeader = new Rect(0f, 0f, 0f, 0f);
        float __vh = vertical ? (headerH * Mathf.Max(1, vis.Count)) : headerH; // 纵向：裁剪区是总高
        GUI.BeginClip(new Rect(box.x, box.y, stripW, vertical ? __vh : headerH));
        try {
        // 表头底（ImGui TableHeaderBg）：颜色走 CurStyle，可单表覆盖。
        DrawFill(new Rect(0f, 0f, stripW, vertical ? __vh : headerH), CurStyle.HeaderBg);
        float x = -scrollX, y = -scrollY; // 纵向用 y 叠放
        for (int k = 0; k < vis.Count; k++) {
            int ci = vis[k];
            if (ci == pinCol) continue;
            float w = vertical ? stripW : Sc(colW[ci]);
            float h = vertical ? Sc(colW[ci]) : headerH;
            cells.Add(new Rect(box.x + x, box.y + y, w, h));
            cellCols.Add(ci);
            if (x + w > -Sc(4) && x < stripW + Sc(4)) {   //只画看得见的
                Rect __cr = new Rect(vertical ? 0f : x, vertical ? y : 0f, vertical ? stripW : w, h);
                // 列头悬停反馈（裁剪区内坐标：mp 减掉裁剪原点）。
                Vector2 __lmh = new Vector2(mp.x - box.x, mp.y - box.y);
                if (__lmh.x >= 0f && __lmh.x < stripW && __lmh.y >= 0f && __lmh.y < (vertical ? __vh : headerH)
                    && __cr.Contains(__lmh) && moveCol != ci) DrawRect(__cr, 0.06f);
                if (moveCol == ci) DrawRect(__cr, 0.22f);
                (headerLeft ? SR.Ctl.LabelClip : SR.Ctl.SecHeaderCenter).Draw(__cr,
                    new GUIContent(title != null ? title(ci) : ""), false, false, false, false);
                // 横向画竖分隔线、纵向画横分隔线；表头/外框用强线。
                if (vertical) DrawLine(new Rect(0f, y + h - 1f, stripW, 1f), CurStyle.LineStrong);
                else DrawLine(new Rect(x + w - 1f, 0f, 1f, headerH), CurStyle.LineStrong);
            }
            if (vertical) y += h; else x += w;
        }
        if (moveCol >= 0) {   //拖动中：在目标位置画竖线（裁剪空间里 x 要减掉裁剪原点）
            for (int k = 0; k < cells.Count; k++) {
                Rect cc = cells[k];
                if (mp.x < cc.xMax) { DrawRect(new Rect(cc.x - box.x - 1f, 0f, Sc(3), headerH), 0.9f); break; }
            }
        }
        } finally { GUI.EndClip(); }
        if (pinW > 0f) {
            pinHeader = new Rect(pinX, box.y, pinW, headerH);
            GUI.BeginClip(new Rect(pinX, box.y, pinW, headerH));
            try {
            // 钉住列表头：统一表头底上叠一层淡 accent，保留可辨识度。
            DrawFill(new Rect(0f, 0f, pinW, headerH), CurStyle.HeaderBg);
            DrawRect(new Rect(0f, 0f, pinW, headerH), 0.10f);
            SR.Ctl.SecHeaderCenter.Draw(new Rect(0f, 0f, pinW, headerH),
                new GUIContent(pinTitle ?? ""), false, false, false, false);
            } finally { GUI.EndClip(); }
            DrawLine(new Rect(pinX - 1f, box.y, 1f, headerH), CurStyle.LineStrong);
        }
    }

    // 数组版（TableDraw 走这条）：直接索引取值、零分配；越界列按空串处理。
    public static void DrawTableCells(List<int> vis, int skipCol, int alignLeftCol, float rowH, float scrollX, float y,
        float[] colW, string[] cells) {
        float x = -scrollX;
        for (int k = 0; k < vis.Count; k++) {
            int ci = vis[k];
            if (ci == skipCol) continue;
            float w = Sc(colW[ci]);
            Rect r = new Rect(x, y, w, rowH);
            GUIStyle st = ci == alignLeftCol ? SR.Ctl.LabelClip : SR.Ctl.LabelClipCenter;
            string s = (cells != null && ci >= 0 && ci < cells.Length) ? cells[ci] : null;
            st.Draw(r, CellContent(s), false, false, false, false);
            DrawLine(new Rect(r.xMax - 1f, r.y, 1f, r.height), CurStyle.LineLight);   //格内线用弱线
            x += w;
        }
    }

    // 复用同一个 GUIContent（每帧数百次调用）；GUIStyle.Draw 同步绘制、不持有引用，复用安全。
    private static readonly GUIContent _cellContent = new GUIContent();
    private static GUIContent CellContent(string s) {
        _cellContent.text = s != null ? s : "";
        return _cellContent;
    }

    // 拖拽兜底：超过 1.2s 无鼠标事件就复位所有拖拽状态（仅漏收 MouseUp 时触发）。
    // 拖拽状态与落盘回调由调用方以 ref/回调注入，核心不持有功能数据。
    public static void TableDragWatchdog(Event e, ref float watchdog,
        ref bool sbDragging, ref int moveCol, ref float moveDx, ref int dragCol, ref bool dragH,
        System.Action saveWidths, System.Action saveTableH, System.Action resetJoinDown) {
        // 松开的那一帧不做超时复位，让拖动处理先跑；下一帧再兜底。
        if (e != null && e.type == EventType.MouseUp) return;
        if (watchdog > 0f && Time.unscaledTime > watchdog) {
            // 鼠标键仍按住时绝不复位拖动状态，否则松开时 moveCol != ci、换位静默失效。
            if (UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2))
                return;
            sbDragging = false;
            if (moveCol >= 0) { moveCol = -1; moveDx = 0f; }
            if (dragCol >= 0) { dragCol = -1; if (saveWidths != null) saveWidths(); }
            if (dragH) { dragH = false; if (saveTableH != null) saveTableH(); }
            if (resetJoinDown != null) resetJoinDown();
            BlockWindowDrag = false;
            watchdog = 0f;
            return;
        }
        if (e == null) return;
        if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) {
            if (moveCol >= 0 || dragCol >= 0 || sbDragging || dragH) watchdog = Time.unscaledTime + 1.2f;
        } else if (e.type == EventType.MouseUp) {
            watchdog = 0f;
        }
    }

    // 表格滚轮：Shift+滚轮横向；无纵向余量时自动转横向。返回 true = 已消费。
    public static bool TableWheelInput(Event e, Rect box, Vector2 mp, float maxX, float maxY,
        ref Vector2 scrollCols, ref Vector2 scrollRows) {
        if (e == null || e.type != EventType.ScrollWheel || !box.Contains(mp)) return false;
        float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
        // Shift+滚轮横向滚动；无纵向余量时自动转横向。
        if (e.shift && maxX > 0f) {
            scrollCols.x = Mathf.Clamp(scrollCols.x + amt * Sc(60), 0f, maxX);
            e.Use(); MarkWheelUsed(); return true;
        }
        if (maxY > 0f) {
            scrollRows.y = Mathf.Clamp(scrollRows.y + amt * Sc(40), 0f, maxY);
            e.Use(); MarkWheelUsed(); return true;
        }
        if (maxX > 0f) {
            scrollCols.x = Mathf.Clamp(scrollCols.x + amt * Sc(60), 0f, maxX);
            e.Use(); MarkWheelUsed(); return true;
        }
        return false;
    }

    //竖向滚动条的拖拽 / 点轨道跳转 -> 返回 true 表示本事件已处理（状态以 ref 传入）
    // 竖向滚动条统一绘制（ImGui ScrollbarBg / Grab / GrabHovered / GrabActive 配色）。
    public static void TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible = true, float alpha = 1f) {
        thumb = default(Rect);
        try {
            if (track.width <= 0f || track.height <= 0f || contentH <= viewH + 0.5f) return;
            float th = Mathf.Max(Sc(20f), track.height * Mathf.Clamp01(viewH / Mathf.Max(1f, contentH)));
            float maxScroll = Mathf.Max(0f, contentH - viewH);
            float k2 = maxScroll > 0.01f ? Mathf.Clamp01(scrollY / maxScroll) : 0f;
            thumb = new Rect(track.x, track.y + (track.height - th) * k2, track.width, th);
            // visible=false / alpha<=0：只算几何不绘制（默认隐藏时完全不产生绘制调用）。
            if (!visible || alpha <= 0.01f) return;
            float a = Mathf.Clamp01(alpha);
            DrawFill(track, new Color(0.06f, 0.06f, 0.07f, 0.94f * a));
            bool hover = Event.current != null && thumb.Contains(Event.current.mousePosition);
            DrawFill(thumb, dragging ? new Color(0.51f, 0.51f, 0.53f, a)
                : hover ? new Color(0.41f, 0.41f, 0.44f, a)
                : new Color(0.31f, 0.31f, 0.34f, a));
        } catch (Exception __ex) { Guard.Log("表格滚动条绘制", __ex); }
    }

    public static bool TableScrollbarInput(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax,
        ref bool dragging, ref float grabY, ref float scrollY) {
        if (e == null) return false;
        if (dragging) {
            if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp) {
                float thumbH = thumb.height;
                float t = Mathf.Clamp01((mp.y - track.y - grabY) / Mathf.Max(1f, track.height - thumbH));
                scrollY = t * scrollMax;
                BlockWindowDrag = true;
                if (e.type == EventType.MouseUp) { dragging = false; BlockWindowDrag = false; }
                e.Use();
                return true;
            }
            return false;
        }
        if (e.type == EventType.MouseDown && e.button == 0 && track.width > 0f && track.Contains(mp)) {
            float thumbH = thumb.height;
            bool onThumb = thumb.Contains(mp);
            grabY = onThumb ? (mp.y - thumb.y) : thumbH * 0.5f;
            dragging = true;
            BlockWindowDrag = true;
            if (!onThumb) {   //点轨道：直接跳到该位置
                float t = Mathf.Clamp01((mp.y - track.y - grabY) / Mathf.Max(1f, track.height - thumbH));
                scrollY = t * scrollMax;
            }
            e.Use();
            return true;
        }
        return false;
    }

    //列头拖动换位 / 单击（点击排序等）-> 返回 true 表示本事件已处理。状态与回调由调用方注入。
    public static bool TableColMove(Event e, Rect cell, int ci, int k, List<int> cols, Vector2 mp, bool inBox,
        List<Rect> headerCells, int[] order, System.Action<int[]> applyOrder, System.Action save, System.Action<int> click,
        ref int moveCol, ref float moveX0, ref float moveDx, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag, bool vertical = false) {
        if (e == null) return false;
        // 横向：分隔线在右、可拖区在左；纵向：分隔线在下、可拖区在上。
        Rect sep = vertical ? new Rect(cell.x, cell.yMax - Sc(3), cell.width, Sc(7)) : new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
        Rect body = vertical ? new Rect(cell.x, cell.y, cell.width, cell.height - Sc(4)) : new Rect(cell.x, cell.y, cell.width - Sc(4), cell.height);
        if (e.type == EventType.MouseDown && e.button == 0 && inBox && body.Contains(mp) && !sep.Contains(mp)) {
            moveCol = ci; moveX0 = vertical ? mp.y : mp.x; moveDx = 0f; scrollColsAtDrag = scrollCols; // moveX0 存本轴起点
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseDrag && moveCol == ci) {
            moveDx = (vertical ? mp.y : mp.x) - moveX0; // 离开列表也继续跟手
            scrollCols = scrollColsAtDrag;
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseUp && moveCol == ci) {
            // 换列需要 order 才能算 from/to；order 为空时清干净拖动状态再返回，避免 moveCol 一直挂着。
            if (order == null || order.Length == 0) { moveCol = -1; moveDx = 0f; BlockWindowDrag = false; e.Use(); return true; }
            bool dragged = Mathf.Abs(moveDx) > Sc(4);  //1~3px 手抖不算拖动
            int target = k;
            if (dragged && headerCells.Count > 0) {
                for (int j = 0; j < headerCells.Count; j++) {
                    if (vertical ? (mp.y < headerCells[j].yMax) : (mp.x < headerCells[j].xMax)) { target = j; break; }
                    target = headerCells.Count - 1;
                }
            }
            if (dragged && target != k && target >= 0 && target < cols.Count) {
                int from = -1, to = -1;
                for (int i = 0; i < order.Length; i++) {
                    if (order[i] == ci) from = i;
                    if (order[i] == cols[target]) to = i;
                }
                if (from >= 0 && to >= 0 && from != to) {
                    int moved = order[from];
                    List<int> l = new List<int>(order);
                    l.RemoveAt(from);
                    l.Insert(to, moved);
                    if (applyOrder != null) applyOrder(l.ToArray());
                    if (save != null) save();
                }
            } else if (!dragged && cell.Contains(mp)) {
                if (click != null) click(ci);
            }
            moveCol = -1; moveDx = 0f; BlockWindowDrag = false; e.Use(); return true;
        }
        return false;
    }

    //列宽拖动（表头分隔线上按住左键左右拖）-> 返回 true 表示本事件已处理。状态由调用方以 ref 传入。
    public static bool TableColResize(Event e, Rect cell, int ci, Vector2 mp, bool inBox, float[] colW,
        ref int dragCol, ref float dragX0, ref float dragW0, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag) {
        if (e == null) return false;
        Rect sep = new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
        if (e.type == EventType.MouseDown && e.button == 0 && inBox && sep.Contains(mp)) {
            dragCol = ci; dragX0 = mp.x; dragW0 = colW[ci]; scrollColsAtDrag = scrollCols;
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseDrag && dragCol == ci) {
            //位移是屏幕像素，colW 是未缩放设计值 -> 先除掉缩放系数
            colW[ci] = Mathf.Clamp(dragW0 + (mp.x - dragX0) / Mathf.Max(0.0001f, Sc(1f)), 28f, 600f);
            scrollCols = scrollColsAtDrag;
            BlockWindowDrag = true; e.Use(); return true;
        }
        return false;
    }

    //钉住列（右列，如联机的加入列）的命中矩形：填充 pinnedRects / pinnedRows
    public static void TablePinnedRects(int rowCount, float rowH, float scrollY, float rowsH, float boxY, float headerH,
        float pinX, float pinW, List<Rect> pinnedRects, List<int> pinnedRows) {
        pinnedRects.Clear(); pinnedRows.Clear();
        if (pinW <= 0f) return;
        for (int i = 0; i < rowCount; i++) {
            float y = i * rowH - scrollY;
            if (y > rowsH || y + rowH < 0f) continue;
            float ry = boxY + headerH + y;
            float hy0 = Mathf.Max(ry, boxY + headerH);
            float hy1 = Mathf.Min(ry + rowH, boxY + headerH + rowsH);
            pinnedRects.Add(new Rect(pinX + Sc(2), hy0, pinW - Sc(4), Mathf.Max(0f, hy1 - hy0)));
            pinnedRows.Add(i);
        }
    }

    //行区命中矩形（与绘制共用同一裁剪规则）：填充 rowRects / rowRows（一一对应）
    public static void TableRowRects(int rowCount, float rowH, float scrollY, float rowsH, float boxY, float headerH,
        Rect rowsClip, float boxX, float stripW, List<Rect> rowRects, List<int> rowRows) {
        rowRects.Clear(); rowRows.Clear();
        int first = Mathf.Max(0, Mathf.FloorToInt(scrollY / Mathf.Max(1f, rowH)));
        int last = Mathf.Min(rowCount - 1, Mathf.CeilToInt((scrollY + rowsH) / Mathf.Max(1f, rowH)));
        for (int i = first; i <= last; i++) {
            float y = i * rowH - scrollY;
            float ry = boxY + headerH + y;
            float hy0 = Mathf.Max(ry, rowsClip.y);
            float hy1 = Mathf.Min(ry + rowH, rowsClip.yMax);
            rowRects.Add(new Rect(boxX, hy0, stripW, Mathf.Max(0f, hy1 - hy0)));
            rowRows.Add(i);
        }
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

    // 表格外观配置：绘制一律读 _tableStyle（本文件的 CurStyle 直接读它）；配色只能改下面 TableStyle 的字段默认值。
    public sealed class TableStyle {
        // 表格配色（取自 Dear ImGui 表格主题）：表头底 / 强线（表头、外框）/ 弱线（格内）/ 隔行底。
        public Color HeaderBg = new Color(0.19f, 0.19f, 0.20f, 1f);
        public Color LineStrong = new Color(0.31f, 0.31f, 0.35f, 1f);
        public Color LineLight = new Color(0.23f, 0.23f, 0.25f, 1f);
        public Color RowAltBg = new Color(1f, 1f, 1f, 0.06f);
    }
    private static readonly TableStyle _tableStyle = new TableStyle();
    public static TableStyle DefaultTableStyle { get { return _tableStyle; } }
    private static TableStyle CurStyle { get { return _tableStyle; } }

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

    // ================= 颜色选择器（统一调色板）=================
    // 功能层只用 SRApi.Ctl.ColorEdit / ColorSwatch / ColorPanel / PreviewColor / ResetColor。
    // 版式对齐 ImGui ColorPicker3：预览行 → 饱和度×明度方块（H 固定）+ 竖向色相条 → 24 色常用色网格
    // → H/S/V 三行 → RGB 三行 → Hex 行 → 复制/粘贴/还原默认。
    // 直接拖动方块 / 色相条时只改“预览色”（Dirty），鼠标松开或回车才写盘；文本输入回车 / 失焦提交；
    // 切条目、收起、关窗、切页仍由 Close / Tick 兜底提交。展开、拖动、输入状态全部按 ConfigEntryBase 保存在这里。
    internal static class ColorPicker {
        // 常用色：8 档灰阶 + 16 常见色（12 列 × 2 行，对应 ImGui 的 palette 区）。
        private static readonly string[] Palette = new string[] {
            "#000000", "#1A1A1A", "#333333", "#4D4D4D", "#666666", "#808080", "#B3B3B3", "#FFFFFF",
            "#E53935", "#FB8C00", "#FDD835", "#43A047",
            "#00ACC1", "#1E88E5", "#3949AB", "#5E35B1",
            "#8E24AA", "#D81B60", "#F06292", "#FF7043",
            "#26A69A", "#7CB342", "#FFB300", "#8D6E63"
        };
        private const int PaletteCols = 12;   // 每行色块数
        private const int SvTexSize = 96;     // S/V 方块贴图分辨率（只随 H 变化，避免逐像素逐帧绘制）
        private static Texture2D _svTex;
        private static float _svTexHue = -1f;
        private static Color32[] _svPixels;

        // 一个颜色键的可变状态。
        private sealed class State {
            public bool Open;          // 调色板是否展开
            public float H, S = 1f, V = 1f;
            public bool Dirty;         // 预览已变、配置未写（拖动中不写盘）
            public bool Sliding;       // 滑块 / 方块 / 色相条正在拖动（松手才落盘）
            public bool TextPending;   // hex/RGB 输入框里有未提交的文本
            public string HexEdit = "#FFFFFF";
            public string[] Rgb = new string[] { "255", "255", "255" };
            public string Focus;       // 上一帧的输入焦点（失焦即提交）
            public int Frame = -1;     // 调色板最近绘制的帧号（离开页面/关窗兜底落盘）
            public int Drag;           // 0=无 1=S/V 方块 2=色相条
        }

        private static readonly Dictionary<ConfigEntryBase, State> _states = new Dictionary<ConfigEntryBase, State>();
        private static ConfigEntryBase _open;   // 当前展开的条目（同一时间只展开一个）
        private static bool _tickHooked;
        private const string HexField = "SR_ColorHex";
        private static readonly string[] RgbFields = new string[] { "SR_ColorR", "SR_ColorG", "SR_ColorB" };

        // 契约层门面注入：由 Ctl 的静态构造器调用一次。
        internal static void Wire() {
            SRApi.Ctl.WireColorPicker(Edit, Swatch, Panel, Preview, Reset);
        }

        // 每帧兜底落盘：首次使用时挂上（此时 SR 已初始化，RegisterTick 一定可用）。
        private static void EnsureTick() {
            if (_tickHooked) return;
            _tickHooked = true;
            try { RegisterTick("颜色调色板兜底落盘", Tick); } catch { }
        }

        private static State Get(ConfigEntryBase e) {
            State s;
            if (e != null && _states.TryGetValue(e, out s)) return s;
            s = new State();
            if (e != null) { Load(s, ReadColor(e)); _states[e] = s; }
            return s;
        }

        // ------------------------- 对外三件套 -------------------------

        // 颜色条目：标签（点名称还原默认色）+ 色块（点开展开/收起）+ 展开后的调色板。
        public static bool Edit(GUIContent label, ConfigEntryBase e, float width, float height) {
            if (e == null) return false;
            EnsureTick();
            bool changed = false;
            float h = height > 0f ? height : Ctl.ControlH();
            float w = width > 0f ? width : Sc(150f);
            GUILayout.BeginHorizontal();
            try {
                if (label != null && !string.IsNullOrEmpty(label.text)) {
                    if (GUILayout.Button(label, Ctl.Label, GUILayout.Width(w), GUILayout.Height(h))) { Reset(e); changed = true; }
                    GUILayout.Space(Sc(6f));
                }
                Rect sr = GUILayoutUtility.GetRect(Sc(64f), h);
                if (Swatch(sr, e)) changed = true;
                GUILayout.FlexibleSpace();
            } catch (Exception __ex) { Guard.Log("颜色条目", __ex); }
            GUILayout.EndHorizontal();
            Panel(e);
            return changed;
        }

        // 只画一个色块按钮（无标签），返回是否被点击；供自绘行使用。
        public static bool Swatch(Rect r, ConfigEntryBase e) {
            if (e == null) return false;
            EnsureTick();
            try {
                State s = Get(e);
                s.Frame = Time.frameCount;
                Color c = s.Dirty ? Quant(PickerColor(s)) : ReadColor(e);
                DrawFill(r, c);
                Ring(r, new Color(1f, 1f, 1f, 0.35f));
                string tip = T("点击打开/收起调色板", "click to open/close the color picker");
                if (!GUI.Button(r, new GUIContent("", tip), GUIStyle.none)) return false;
                if (s.Open) Close(e, s); else Open(e, s);
                return true;
            } catch (Exception __ex) { Guard.Log("颜色色块", __ex); return false; }
        }

        // 自绘行在 EndHorizontal 之后调用：该条目展开时画统一调色板，收起时什么都不画。
        public static void Panel(ConfigEntryBase e) {
            if (e == null) return;
            State s;
            if (!_states.TryGetValue(e, out s) || !s.Open) return;
            try { DrawPanel(e, s); } catch (Exception __ex) { Guard.Log("颜色调色板", __ex); }
        }

        // 未落盘的预览色（null = 没有预览，调用方读配置值）。
        public static Color? Preview(ConfigEntryBase e) {
            try {
                State s;
                if (e == null || !_states.TryGetValue(e, out s) || !s.Dirty) return null;
                return Quant(PickerColor(s));
            } catch { return null; }
        }

        // 写回该颜色键的默认值（点名称 / 调色板里的「还原默认」共用）。
        public static void Reset(ConfigEntryBase e) {
            if (e == null) return;
            try {
                string hex = DefaultHex(e);
                SetValue(e, hex);
                State s;
                if (_states.TryGetValue(e, out s)) {
                    Load(s, SRApi.ColorHex.ParseHex(hex, Color.white));
                }
            } catch (Exception __ex) { Guard.Log("颜色还原默认", __ex); }
        }

        // ------------------------- 状态与写盘 -------------------------

        private static Color PickerColor(State s) {
            return Color.HSVToRGB(Mathf.Clamp01(s.H), Mathf.Clamp01(s.S), Mathf.Clamp01(s.V));
        }

        // 预览/写盘统一走 Color32 量化：预览、hex 标签与实际文字颜色完全一致。
        private static Color Quant(Color c) {
            return SRApi.ColorHex.FromColor32(SRApi.ColorHex.ToColor32(c));
        }

        // RGB→HSV；keepHueIfGray 时 V=0 或 S=0 保留原色相（ImGui 行为，拖到底不丢 hue）。
        private static void PickFrom(State s, Color c, bool keepHueIfGray) {
            try {
                c = Quant(c);
                float h, sat, v;
                Color.RGBToHSV(c, out h, out sat, out v);
                if (!keepHueIfGray || (sat > 0.0001f && v > 0.0001f)) s.H = Mathf.Repeat(h, 1f);
                s.S = Mathf.Clamp01(sat);
                s.V = Mathf.Clamp01(v);
            } catch { }
        }

        private static void Load(State s, Color c) {
            PickFrom(s, c, false);
            s.Dirty = false;
            s.Sliding = false;
            s.Drag = 0;
            s.TextPending = false;
            s.Focus = null;
            SyncText(s, true);
        }

        private static void Open(ConfigEntryBase e, State s) {
            if (_open != null && !ReferenceEquals(_open, e)) {
                State prev;
                if (_states.TryGetValue(_open, out prev)) Close(_open, prev);
            }
            _open = e;
            s.Open = true;
            Load(s, ReadColor(e));
        }

        // 收起：先把拖动/输入中的预览写回，避免预览色留在界面上。
        private static void Close(ConfigEntryBase e, State s) {
            try {
                // 顺序与 ImGui 一致：先提交预览，再提交文本（文本是最后一次编辑，必须最后生效）。
                Commit(e, s);
                if (s.TextPending) FlushText(e, s);
            } catch { }
            s.Open = false;
            s.Dirty = false;
            s.Sliding = false;
            s.Drag = 0;
            s.TextPending = false;
            s.Focus = null;
            if (ReferenceEquals(_open, e)) _open = null;
        }

        // 立即落盘（只在色板/方块/色相条/滑块/文本改动后调用；拖动过程中不写配置）。
        private static void Commit(ConfigEntryBase e, State s) {
            try {
                if (!s.Dirty || e == null) return;
                SetValue(e, SRApi.ColorHex.ToHex(PickerColor(s)));
                s.Dirty = false;
                s.Sliding = false;
                s.Drag = 0;
            } catch (Exception __ex) { Guard.Log("颜色写入", __ex); }
        }

        // 应用一个颜色：commit=true 时同时写配置（色板 / 粘贴）。
        private static void Apply(ConfigEntryBase e, State s, Color c, bool keepHueIfGray, bool commit) {
            PickFrom(s, c, keepHueIfGray);
            s.TextPending = false;
            s.Dirty = true;
            SyncText(s, true);   // 选色后文本必须跟着预览走，避免旧输入在失焦时把新颜色覆盖回去
            if (commit) Commit(e, s);
        }

        // 兜底落盘：切页 / 关窗后调色板不再绘制，把预览与输入框里的值写回。
        private static void Tick() {
            try {
                State s;
                if (_open == null || !_states.TryGetValue(_open, out s)) return;
                if (s.Frame >= 0 && Time.frameCount - s.Frame <= 8) return;
                Commit(_open, s);
                if (s.TextPending) FlushText(_open, s);
            } catch { }
        }

        private static string DefaultHex(ConfigEntryBase e) {
            try {
                Color c;
                if (e != null && SRApi.ColorHex.ParseHex(e.DefaultValue as string, out c)) return SRApi.ColorHex.ToHex(c);
            } catch { }
            return "#FFFFFF";
        }

        private static Color ReadColor(ConfigEntryBase e) {
            try { return SRApi.ColorHex.ParseHex(e != null ? e.BoxedValue as string : null, Color.white); }
            catch { return Color.white; }
        }

        // 文本同步：hex/RGB 跟着当前 HSV 走；force=false 时不覆盖正在输入的框。
        private static void SyncText(State s, bool force) {
            try {
                Color32 c = SRApi.ColorHex.ToColor32(PickerColor(s));
                if (force || !IsFocused(HexField)) s.HexEdit = SRApi.ColorHex.ToHex(PickerColor(s));
                for (int i = 0; i < 3; i++) {
                    if (force || !IsFocused(RgbFields[i])) s.Rgb[i] = Comp(c, i).ToString(CultureInfo.InvariantCulture);
                }
            } catch { }
        }

        // ------------------------- 绘制 -------------------------

        // 面板宽度：默认 340 设计单位；窗口变窄时按可用宽度收缩（低于 240 时优先不撑破页面）。
        private static float PanelWidth() {
            float avail = AvailWidth();
            float pw = Mathf.Min(Sc(340f), avail);
            return Mathf.Max(Sc(120f), pw);
        }

        // 内容区可用宽度：优先 ContentArea，其次窗口宽度扣侧栏；下限 120，避免除零。
        private static float AvailWidth() {
            float w = 0f;
            try { w = ContentArea.width; } catch { w = 0f; }
            if (w <= 0f) { try { w = Ctl.WinWidth > 0f ? Ctl.WinWidth - SidebarWidth() : 0f; } catch { w = 0f; } }
            if (w <= 0f) w = Sc(300f);
            return Mathf.Max(Sc(120f), w - Sc(12f));
        }

        // 文本测量：标签列按当前语言的实测宽度计算，不再写死 34 / 14。
        private static float TextW(string text, GUIStyle st) {
            try { return st != null ? st.CalcSize(new GUIContent(text ?? "")).x : Sc(24f); }
            catch { return Sc(24f); }
        }

        // 同一列取最大值 + 8px：用于 H/S/V、RGB、Hex 的标签列。
        private static float LabelCol(params string[] texts) {
            float w = 0f;
            if (texts != null) {
                for (int i = 0; i < texts.Length; i++) w = Mathf.Max(w, TextW(texts[i], Ctl.Label));
            }
            return w + Sc(8f);
        }

        // 按钮宽度：按文字实测宽度 + 内边距，英文/其它语言不截断。
        private static float BtnW(string text) {
            return TextW(text, Ctl.Btn) + Sc(12f);
        }

        private static void HoverTip(Rect r, string tip) {
            try {
                Event ev = Event.current;
                if (ev != null && r.Contains(ev.mousePosition)) GUI.tooltip = tip;
            } catch { }
        }

        private static void DrawPanel(ConfigEntryBase e, State s) {
            s.Frame = Time.frameCount;
            Event ev = Event.current;
            bool enter = ev != null && ev.type == EventType.KeyDown
                && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);
            bool enterText = enter && (IsFocused(HexField) || IsRgbFocused());
            float pw = PanelWidth();

            GUILayout.BeginVertical(GUILayout.Width(pw));
            try {
                try { GUILayout.Label(T("调色板", "Palette"), Ctl.SecHeader); } catch { }
                DrawHeader(e, s, pw);
                GUILayout.Space(Sc(6f));
                DrawSvBlock(e, s, pw);
                GUILayout.Space(Sc(6f));
                DrawPaletteGrid(e, s, pw);
                GUILayout.Space(Sc(4f));

                // H/S/V 与 Hex 共用同一标签列：取当前语言里最宽的文本 + 8px。
                float labelW = LabelCol(T("色相", "Hue"), T("饱和", "Sat"), T("明度", "Val"), "Hex");
                DrawHsvRow(e, s, T("色相", "Hue"), 0, 0f, 360f, pw, labelW);
                DrawHsvRow(e, s, T("饱和", "Sat"), 1, 0f, 100f, pw, labelW);
                DrawHsvRow(e, s, T("明度", "Val"), 2, 0f, 100f, pw, labelW);
                GUILayout.Space(Sc(4f));
                DrawRgbRow(e, s, pw);
                DrawHexRow(e, s, pw, labelW);
                GUILayout.Space(Sc(2f));
                DrawActions(e, s, pw);
            } finally { GUILayout.EndVertical(); }

            // 回车提交（不吞其它按键）；失焦提交；方块/色相条/HSV 滑块松手提交。
            if (enterText) ApplyText(e, s);
            else if (enter && s.Dirty) Commit(e, s);
            HandleFocus(e, s);
            if (s.Dirty && s.Sliding && ev != null
                && (ev.type == EventType.MouseUp || (ev.type == EventType.Repaint && !Input.GetMouseButton(0)))) {
                Commit(e, s);
                BlockWindowDrag = false;
            }
        }

        // 预览色块 + #RRGGBB + 收起：ImGui ColorPicker 的顶部预览行。
        private static void DrawHeader(ConfigEntryBase e, State s, float pw) {
            float h = Ctl.ControlH();
            float collapseW = BtnW(T("收起", "Collapse"));
            float hexW = TextW("#FFFFFF", Ctl.Label) + Sc(8f);
            float previewW = Mathf.Clamp(pw - hexW - collapseW - Sc(16f), Sc(44f), Sc(84f));
            GUILayout.BeginHorizontal();
            try {
                Rect pv = GUILayoutUtility.GetRect(previewW, h);
                PreviewBox(pv, Quant(PickerColor(s)));
                GUILayout.Label(SRApi.ColorHex.ToHex(PickerColor(s)), Ctl.Label, GUILayout.Width(hexW), GUILayout.Height(h));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(T("收起", "Collapse"), Ctl.Btn, GUILayout.Width(collapseW), GUILayout.Height(h))) Close(e, s);
            } catch (Exception __ex) { Guard.Log("调色板顶栏", __ex); }
            GUILayout.EndHorizontal();
        }

        // 色板主体：左边饱和度×明度方块，右边竖向色相条（ImGui ColorPicker3 布局）。
        private static void DrawSvBlock(ConfigEntryBase e, State s, float pw) {
            float hueW = Sc(24f), gap = Sc(8f);
            float side = Mathf.Min(Sc(150f), Mathf.Max(Sc(72f), pw - hueW - gap - Sc(8f)));
            Rect sq = default(Rect), hb = default(Rect);
            GUILayout.BeginHorizontal();
            try {
                sq = GUILayoutUtility.GetRect(side, side);
                GUILayout.Space(gap);
                hb = GUILayoutUtility.GetRect(hueW, side);
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
            DrawSvSquare(e, s, sq);
            DrawHueBar(e, s, hb);
        }

        // 饱和度（X）× 明度（Y）二维方块：H 固定；贴图只随 H 变化，拖动时每秒最多重建几十次小图。
        private static void DrawSvSquare(ConfigEntryBase e, State s, Rect r) {
            if (r.width <= 0f || r.height <= 0f) return;
            DrawFill(r, Color.black);
            if (Event.current != null && Event.current.type == EventType.Repaint) {
                Texture2D tex = null;
                try { tex = SvTexture(s.H); } catch { }
                if (tex != null) {
                    Color old = GUI.color;
                    try { GUI.color = Color.white; GUI.DrawTexture(r, tex); } finally { GUI.color = old; }
                }
            }
            Ring(r, new Color(0f, 0f, 0f, 0.75f));

            float mx = r.x + Mathf.Clamp01(s.S) * r.width;
            float my = r.y + (1f - Mathf.Clamp01(s.V)) * r.height;
            float d = Sc(7f);
            Rect m = new Rect(
                Mathf.Clamp(mx - d / 2f, r.x, Mathf.Max(r.x, r.xMax - d)),
                Mathf.Clamp(my - d / 2f, r.y, Mathf.Max(r.y, r.yMax - d)), d, d);
            DrawFill(m, Color.white);
            Ring(m, Color.black);
            HandleSvInput(e, s, r);
            HoverTip(r, T("拖动选择饱和度与明度；松开写入配置", "drag to pick saturation/value; release to apply"));
        }

        // 竖向色相条：顶部 0°、底部 360°，点击/拖动立即更新预览，松开落盘。
        private static void DrawHueBar(ConfigEntryBase e, State s, Rect r) {
            if (r.width <= 0f || r.height <= 0f) return;
            int n = 96;
            float seg = r.height / n;
            for (int i = 0; i < n; i++) {
                DrawFill(new Rect(r.x, r.y + i * seg, r.width, seg + 1f), Color.HSVToRGB(i / (float)n, 1f, 1f));
            }
            Ring(r, new Color(0f, 0f, 0f, 0.75f));

            float hy = r.y + Mathf.Repeat(s.H, 1f) * r.height;
            float markerY = Mathf.Clamp(hy - Sc(1f), r.y, Mathf.Max(r.y, r.yMax - Sc(2f)));
            DrawFill(new Rect(r.x, markerY, r.width, Sc(2f)), Color.white);
            DrawFill(new Rect(r.x, markerY, r.width, Sc(1f)), new Color(0f, 0f, 0f, 0.85f));
            HandleHueInput(e, s, r);
            HoverTip(r, T("拖动选择色相；松开写入配置", "drag to pick hue; release to apply"));
        }

        // S/V 方块命中：按下、拖动只改预览；松开写盘。
        private static void HandleSvInput(ConfigEntryBase e, State s, Rect r) {
            Event ev = Event.current;
            if (ev == null || r.width <= 0f || r.height <= 0f) return;
            Vector2 mp = ev.mousePosition;
            if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(mp)) {
                s.Drag = 1;
            } else if (ev.type == EventType.MouseUp && s.Drag == 1) {
                SetSvFromRect(s, r, mp);
                s.Dirty = true;
                s.TextPending = false;
                SyncText(s, true);
                Commit(e, s);
                BlockWindowDrag = false;
                ev.Use();
                return;
            } else if (!(s.Drag == 1 && ev.type == EventType.MouseDrag && ev.button == 0)) {
                return;
            }
            SetSvFromRect(s, r, mp);
            s.Dirty = true;
            s.Sliding = true;
            s.TextPending = false;
            SyncText(s, true);
            BlockWindowDrag = true;
            ev.Use();
        }

        private static void SetSvFromRect(State s, Rect r, Vector2 mp) {
            s.S = Mathf.Clamp01((mp.x - r.x) / Mathf.Max(1f, r.width));
            s.V = Mathf.Clamp01(1f - (mp.y - r.y) / Mathf.Max(1f, r.height));
        }

        // 色相条命中：按下、拖动只改预览；松开写盘。
        private static void HandleHueInput(ConfigEntryBase e, State s, Rect r) {
            Event ev = Event.current;
            if (ev == null || r.height <= 0f) return;
            Vector2 mp = ev.mousePosition;
            if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(mp)) {
                s.Drag = 2;
            } else if (ev.type == EventType.MouseUp && s.Drag == 2) {
                SetHueFromRect(s, r, mp);
                s.Dirty = true;
                s.TextPending = false;
                SyncText(s, true);
                Commit(e, s);
                BlockWindowDrag = false;
                ev.Use();
                return;
            } else if (!(s.Drag == 2 && ev.type == EventType.MouseDrag && ev.button == 0)) {
                return;
            }
            SetHueFromRect(s, r, mp);
            s.Dirty = true;
            s.Sliding = true;
            s.TextPending = false;
            SyncText(s, true);
            BlockWindowDrag = true;
            ev.Use();
        }

        private static void SetHueFromRect(State s, Rect r, Vector2 mp) {
            float t = Mathf.Clamp01((mp.y - r.y) / Mathf.Max(1f, r.height));
            s.H = Mathf.Repeat(t, 1f);
        }

        // 24 色网格，点击即应用；当前色用黑白双描边高亮。
        private static void DrawPaletteGrid(ConfigEntryBase e, State s, float pw) {
            float gap = Sc(3f);
            float cw = Mathf.Clamp((pw - (PaletteCols - 1) * gap) / PaletteCols, Sc(10f), Sc(28f));
            float ch = Mathf.Clamp(cw * 0.85f, Sc(16f), Sc(22f));
            int rows = (Palette.Length + PaletteCols - 1) / PaletteCols;
            Rect box = GUILayoutUtility.GetRect(PaletteCols * cw + (PaletteCols - 1) * gap, rows * ch + (rows - 1) * gap);
            Color cur = Quant(PickerColor(s));
            for (int i = 0; i < Palette.Length; i++) {
                int col = i % PaletteCols, row = i / PaletteCols;
                Rect cr = new Rect(box.x + col * (cw + gap), box.y + row * (ch + gap), cw, ch);
                Color pc = SRApi.ColorHex.ParseHex(Palette[i], Color.white);
                if (DrawCell(cr, pc, SameColor(pc, cur), Palette[i])) Apply(e, s, pc, true, true);
            }
        }

        // 单个色板格子：填充 + 外黑描边 + 选中白内框 + 悬停 hex 提示 + 点击判定。
        private static bool DrawCell(Rect r, Color c, bool selected, string tip) {
            DrawFill(r, c);
            Ring(r, new Color(0f, 0f, 0f, selected ? 0.9f : 0.4f));
            if (selected) {
                Ring(new Rect(r.x + 1f, r.y + 1f, Mathf.Max(1f, r.width - 2f), Mathf.Max(1f, r.height - 2f)), new Color(1f, 1f, 1f, 0.95f));
            }
            return GUI.Button(r, new GUIContent("", tip), GUIStyle.none);
        }

        // H/S/V 一行：自适应标签列 + 滑块（整数步进）+ 读数；拖动只改预览，松手由 DrawPanel 落盘。
        private static void DrawHsvRow(ConfigEntryBase e, State s, string label, int which, float min, float max, float pw, float labelW) {
            float rowH = Ctl.ControlH();
            float numW = TextW("360", Ctl.Label) + Sc(8f);
            float sliderW = Mathf.Max(Sc(48f), pw - labelW - numW - Sc(10f));
            float cur = which == 0 ? s.H * 360f : (which == 1 ? s.S * 100f : s.V * 100f);
            float nv = cur;
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(label, Ctl.Label, GUILayout.Width(labelW), GUILayout.Height(rowH));
                Rect sl = GUILayoutUtility.GetRect(sliderW, rowH);
                nv = Ctl.DrawSlider(sl, cur, min, max, true);
                GUILayout.Label(Mathf.RoundToInt(nv).ToString(CultureInfo.InvariantCulture), Ctl.Label, GUILayout.Width(numW), GUILayout.Height(rowH));
            } catch (Exception __ex) { Guard.Log("调色板滑块", __ex); }
            GUILayout.EndHorizontal();
            if (Mathf.Abs(nv - cur) < 0.0001f) return;
            if (which == 0) s.H = Mathf.Clamp01(nv / 360f);
            else if (which == 1) s.S = Mathf.Clamp01(nv / 100f);
            else s.V = Mathf.Clamp01(nv / 100f);
            s.Dirty = true;
            s.Sliding = true;        // 滑块拖动中：松手才写配置
            s.TextPending = false;   // 滑块是最后动作：之前没提交的文本以预览为准
            SyncText(s, false);
        }

        // R/G/B 整数输入（0-255）：三格都合法即时预览，回车/失焦提交；非法格红框。
        private static void DrawRgbRow(ConfigEntryBase e, State s, float pw) {
            float rowH = Ctl.ControlH();
            float labelW = LabelCol("R", "G", "B");
            float gap = Sc(6f);
            float fieldW = Mathf.Max(Sc(30f), (pw - 3f * labelW - 2f * gap - Sc(36f)) / 3f);
            GUILayout.BeginHorizontal();
            try {
                for (int i = 0; i < 3; i++) {
                    if (i > 0) GUILayout.Space(gap);
                    GUILayout.Label(i == 0 ? "R" : (i == 1 ? "G" : "B"), Ctl.Label, GUILayout.Width(labelW), GUILayout.Height(rowH));
                    GUI.SetNextControlName(RgbFields[i]);
                    string before = s.Rgb[i] ?? "";
                    string after = GUILayout.TextField(before, 3, Ctl.SearchBox, GUILayout.Width(fieldW), GUILayout.Height(rowH));
                    Rect fr = GUILayoutUtility.GetLastRect();
                    if (after != before) {
                        s.Rgb[i] = after;
                        s.TextPending = true;
                        ApplyRgbPreview(s);
                    }
                    int probe;
                    if (!TryParseComp(s.Rgb[i], out probe)) Ring(fr, new Color(0.9f, 0.25f, 0.2f, 0.9f));
                }
            } catch (Exception __ex) { Guard.Log("调色板 RGB", __ex); }
            GUILayout.EndHorizontal();
        }

        // 三格都合法才更新预览（非法格保持上一次的有效值）。
        private static void ApplyRgbPreview(State s) {
            int r, g, b;
            if (!TryParseComp(s.Rgb[0], out r)) return;
            if (!TryParseComp(s.Rgb[1], out g)) return;
            if (!TryParseComp(s.Rgb[2], out b)) return;
            PickFrom(s, new Color(r / 255f, g / 255f, b / 255f, 1f), true);
            s.Dirty = true;
            SyncText(s, false);
        }

        // Hex 输入：#RRGGBB；内容非法时实时红框，回车/失焦回退为当前色。
        private static void DrawHexRow(ConfigEntryBase e, State s, float pw, float labelW) {
            float rowH = Ctl.ControlH();
            float hintW = Mathf.Max(Sc(56f), TextW(T("回车应用", "Enter to apply"), Ctl.Label) + Sc(8f));
            float fieldW = Mathf.Max(Sc(48f), pw - labelW - hintW - Sc(12f));
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label("Hex", Ctl.Label, GUILayout.Width(labelW), GUILayout.Height(rowH));
                GUI.SetNextControlName(HexField);
                s.HexEdit = GUILayout.TextField(s.HexEdit ?? "", 7, Ctl.SearchBox, GUILayout.Width(fieldW), GUILayout.Height(rowH));
                Rect fr = GUILayoutUtility.GetLastRect();
                Color probe;
                if (!SRApi.ColorHex.ParseHex(s.HexEdit, out probe)) Ring(fr, new Color(0.9f, 0.25f, 0.2f, 0.9f));
                GUILayout.Label(T("回车应用", "Enter to apply"), Ctl.Label, GUILayout.Width(hintW), GUILayout.Height(rowH));
            } catch (Exception __ex) { Guard.Log("调色板 Hex", __ex); }
            GUILayout.EndHorizontal();
            if (!string.Equals(s.HexEdit, SRApi.ColorHex.ToHex(PickerColor(s)))) s.TextPending = true;
        }

        // 复制 / 粘贴 / 还原默认（剪贴板走 GUIUtility.systemCopyBuffer）。
        private static void DrawActions(ConfigEntryBase e, State s, float pw) {
            float h = Ctl.ControlH();
            string copy = T("复制", "Copy"), paste = T("粘贴", "Paste"), reset = T("还原默认", "Reset");
            float gap = Sc(6f);
            float copyW = BtnW(copy), pasteW = BtnW(paste), resetW = BtnW(reset);
            float total = copyW + pasteW + resetW + 2f * gap;
            float budget = Mathf.Max(Sc(60f), pw - Sc(24f));
            if (total > budget && total > 0f) {
                float k = budget / total;
                copyW *= k; pasteW *= k; resetW *= k; gap *= k;
            }
            GUILayout.BeginHorizontal();
            try {
                if (GUILayout.Button(new GUIContent(copy, T("把当前颜色复制到系统剪贴板", "copy the current color to the system clipboard")),
                    Ctl.Btn, GUILayout.Width(copyW), GUILayout.Height(h))) {
                    try { GUIUtility.systemCopyBuffer = SRApi.ColorHex.ToHex(PickerColor(s)); } catch { }
                }
                GUILayout.Space(gap);
                if (GUILayout.Button(new GUIContent(paste, T("从剪贴板读取 #RRGGBB；格式非法则忽略", "paste #RRGGBB from the clipboard; invalid text is ignored")),
                    Ctl.Btn, GUILayout.Width(pasteW), GUILayout.Height(h))) {
                    Color c;
                    if (SRApi.ColorHex.ParseHex(GUIUtility.systemCopyBuffer, out c)) { Apply(e, s, c, true, true); SyncText(s, true); }
                }
                GUILayout.Space(gap);
                if (GUILayout.Button(new GUIContent(reset, T("写回该颜色键的默认值", "write back the default value of this color key")),
                    Ctl.Btn, GUILayout.Width(resetW), GUILayout.Height(h))) Reset(e);
                GUILayout.FlexibleSpace();
            } catch (Exception __ex) { Guard.Log("调色板操作", __ex); }
            GUILayout.EndHorizontal();
        }

        // 当前 H 对应的 S/V 方块贴图：像素 (x,y) = HSV(H, x/(w-1), y/(h-1))。
        // 只随 H 变化；拖动色相条时逐帧重建 96×96 小图，开销远低于逐格 DrawFill。
        private static Texture2D SvTexture(float h) {
            h = Mathf.Repeat(h, 1f);
            if (_svTex != null && Mathf.Abs(_svTexHue - h) < 0.0005f) return _svTex;
            try {
                if (_svTex == null) {
                    _svTex = new Texture2D(SvTexSize, SvTexSize, TextureFormat.RGBA32, false);
                    _svTex.hideFlags = HideFlags.HideAndDontSave;
                    _svTex.wrapMode = TextureWrapMode.Clamp;
                    _svTex.filterMode = FilterMode.Bilinear;
                }
                if (_svPixels == null || _svPixels.Length != SvTexSize * SvTexSize) _svPixels = new Color32[SvTexSize * SvTexSize];
                Color32[] px = _svPixels;
                float denom = SvTexSize - 1f;
                for (int y = 0; y < SvTexSize; y++) {
                    float v = y / denom;
                    for (int x = 0; x < SvTexSize; x++) {
                        px[y * SvTexSize + x] = SRApi.ColorHex.ToColor32(Color.HSVToRGB(h, x / denom, v));
                    }
                }
                _svTex.SetPixels32(px);
                _svTex.Apply(false, false);
                _svTexHue = h;
                return _svTex;
            } catch { return _svTex; }
        }

        // ------------------------- 输入 / 焦点 -------------------------

        // 回车：hex 优先（合法则应用，非法回退文本），RGB 三格合法则应用；随后落盘。
        private static void ApplyText(ConfigEntryBase e, State s) {
            try {
                Color c;
                if (IsFocused(HexField)) {
                    if (SRApi.ColorHex.ParseHex(s.HexEdit, out c)) { PickFrom(s, c, true); s.Dirty = true; }
                    Commit(e, s);
                    s.HexEdit = SRApi.ColorHex.ToHex(PickerColor(s));
                } else if (IsRgbFocused()) {
                    int r, g, b;
                    if (TryParseComp(s.Rgb[0], out r) && TryParseComp(s.Rgb[1], out g) && TryParseComp(s.Rgb[2], out b)) {
                        PickFrom(s, new Color(r / 255f, g / 255f, b / 255f, 1f), true);
                        s.Dirty = true;
                    }
                    Commit(e, s);
                }
                s.TextPending = false;
                SyncText(s, true);
            } catch (Exception __ex) { Guard.Log("调色板输入", __ex); }
        }

        // 失焦提交：hex/RGB 输入框失去焦点即校验并落盘（与 ImGui 的输入框一致）。
        private static void HandleFocus(ConfigEntryBase e, State s) {
            try {
                string cur = null;
                try { cur = GUI.GetNameOfFocusedControl(); } catch { cur = null; }
                if (string.IsNullOrEmpty(cur)) cur = null;
                string prev = s.Focus;
                if (string.Equals(prev, cur)) return;
                s.Focus = cur;
                if (prev == null) return;
                if (prev == HexField) {
                    Color c;
                    if (SRApi.ColorHex.ParseHex(s.HexEdit, out c)) { PickFrom(s, c, true); s.Dirty = true; }
                    Commit(e, s);
                    s.HexEdit = SRApi.ColorHex.ToHex(PickerColor(s));
                    s.TextPending = false;
                    SyncText(s, true);
                } else if (IsRgbName(prev)) {
                    int r, g, b;
                    if (TryParseComp(s.Rgb[0], out r) && TryParseComp(s.Rgb[1], out g) && TryParseComp(s.Rgb[2], out b)) {
                        PickFrom(s, new Color(r / 255f, g / 255f, b / 255f, 1f), true);
                        s.Dirty = true;
                    }
                    Commit(e, s);
                    s.TextPending = false;
                    SyncText(s, true);
                }
            } catch (Exception __ex) { Guard.Log("调色板失焦", __ex); }
        }

        // 不依赖 GUI 焦点地解析文本并写盘（关窗时兜底）：hex 优先，其次 RGB 三格。
        // 写完把 H/S/V 与预览标记一起对齐，避免下一次 Commit 用旧预览把文本值覆盖回去。
        private static void FlushText(ConfigEntryBase e, State s) {
            try {
                s.TextPending = false;
                if (e == null) return;
                Color c;
                if (SRApi.ColorHex.ParseHex(s.HexEdit, out c)) {
                    SetValue(e, SRApi.ColorHex.ToHex(c));
                    PickFrom(s, c, true);
                    s.Dirty = false;
                    return;
                }
                int r, g, b;
                if (TryParseComp(s.Rgb[0], out r) && TryParseComp(s.Rgb[1], out g) && TryParseComp(s.Rgb[2], out b)) {
                    Color rgb = new Color(r / 255f, g / 255f, b / 255f, 1f);
                    SetValue(e, SRApi.ColorHex.ToHex(rgb));
                    PickFrom(s, rgb, true);
                    s.Dirty = false;
                }
            } catch { }
        }

        // 单个分量（0-255）：空串/非数字/越界都算非法。
        private static bool TryParseComp(string str, out int v) {
            v = 0;
            try {
                if (string.IsNullOrEmpty(str)) return false;
                string t = str.Trim();
                if (t.Length == 0 || t.Length > 3) return false;
                int n;
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return false;
                if (n < 0 || n > 255) return false;
                v = n;
                return true;
            } catch { return false; }
        }

        private static int Comp(Color32 c, int i) { return i == 0 ? c.r : (i == 1 ? c.g : c.b); }

        private static bool SameColor(Color a, Color b) {
            Color32 x = SRApi.ColorHex.ToColor32(a), y = SRApi.ColorHex.ToColor32(b);
            return x.r == y.r && x.g == y.g && x.b == y.b;
        }

        private static bool IsRgbName(string name) {
            if (name == null) return false;
            for (int i = 0; i < RgbFields.Length; i++) if (RgbFields[i] == name) return true;
            return false;
        }

        private static bool IsRgbFocused() {
            for (int i = 0; i < RgbFields.Length; i++) if (IsFocused(RgbFields[i])) return true;
            return false;
        }

        private static bool IsFocused(string name) {
            try { return !string.IsNullOrEmpty(name) && GUI.GetNameOfFocusedControl() == name; }
            catch { return false; }
        }

        // ------------------------- 绘制原语 -------------------------

        // 1px 描边（白贴图着色）：色板选中框、色块边界、输入框错误提示共用。
        private static void Ring(Rect r, Color c) {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            DrawFill(new Rect(r.x, r.y, r.width, 1f), c);
            DrawFill(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            DrawFill(new Rect(r.x, r.y, 1f, r.height), c);
            DrawFill(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }

        // 预览色块：外黑内白双描边，浅色/深色色块上边界都清楚。
        private static void PreviewBox(Rect r, Color c) {
            DrawFill(r, c);
            Ring(r, new Color(0f, 0f, 0f, 0.9f));
            Ring(new Rect(r.x + 1f, r.y + 1f, Mathf.Max(1f, r.width - 2f), Mathf.Max(1f, r.height - 2f)), new Color(1f, 1f, 1f, 0.85f));
        }
    }


	}
}
