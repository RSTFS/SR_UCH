// 颜色选择器（统一调色板）：功能层只用 SRApi.Ctl.ColorEdit / ColorSwatch / ColorPanel / PreviewColor / ResetColor。
// 布局对齐 ImGui ColorPicker3；展开 / 拖动 / 输入状态全部按 ConfigEntryBase 保存在这里，核心只负责转发。
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：ColorPicker（统一调色板控件）

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
            try { RegisterTick("颜色调色板兜底落盘", Tick); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        private static string DefaultHex(ConfigEntryBase e) {
            try {
                Color c;
                if (e != null && SRApi.ColorHex.ParseHex(e.DefaultValue as string, out c)) return SRApi.ColorHex.ToHex(c);
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                try { GUILayout.Label(T("调色板", "Palette"), Ctl.SecHeader); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                try { tex = SvTexture(s.H); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            // 不能对归一化色相用 Mathf.Repeat：拖到底（t=1，或拖出条外被夹到 1）会被折成 0，
            // 表现为滑块从底部瞬移到顶部。上限留一个极小余量，视觉仍是 360°（= 红），但不会回绕。
            float t = Mathf.Clamp01((mp.y - r.y) / Mathf.Max(1f, r.height));
            s.H = Mathf.Min(t, 0.99999f);
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
                    try { GUIUtility.systemCopyBuffer = SRApi.ColorHex.ToHex(PickerColor(s)); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
