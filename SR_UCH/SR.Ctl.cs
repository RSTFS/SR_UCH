// 控件与页面能力门面：通用控件（按键录制 / 右键绑键 / 开关 / 滑块 / 下拉）、首页、行页扩展点、表格控件。
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx;
using BepInEx.Bootstrap;

namespace SR_UCH.Tweaks {
public partial class SR {
// 分区：Ctl（功能页能力门面 + 通用控件 + 首页 + 行/页扩展点）
// Ctl 只转发不放逻辑；核心内部直接调 SR，新能力在 Ctl 里补纯转发成员。

    public static partial class Ctl {

        // 调色板与分区部件自注入：Ctl 首次访问时挂到契约层门面。
        static Ctl() { SR.ColorPicker.Wire(); SRApi.Ctl.WireSection(SectionBegin, SectionEnd); }

        public static float Sc(float v) { return SR.Sc(v); }

        // UI 主题令牌：圆角、过渡时长、底色（统一引用，避免散落魔数）。
        public static float ThemeRadius { get { return Sc(6f); } }
        public static float ThemeHoverFade { get { return 0.12f; } }
        public static Color ThemeFooterBg { get { return new Color(0.10f, 0.11f, 0.13f, 0.96f); } }
        public static Color ThemePanelBg { get { return new Color(0.08f, 0.08f, 0.09f, 0.96f); } }
        public static float ControlH() { return SR.ControlH(); }

        // 一排等宽按钮（功能页工具栏用）：返回被点击的下标，-1 = 没有；宽 <=0 时按文字自适应。
        public static int ButtonRow(string[] labels, float[] widths, float height) {
            if (labels == null || labels.Length == 0) return -1;
            int hit = -1;
            float h = height > 0f ? Sc(height) : ControlH();
            GUILayout.BeginHorizontal();
            try {
                for (int i = 0; i < labels.Length; i++) {
                    if (string.IsNullOrEmpty(labels[i])) continue;
                    float w = (widths != null && i < widths.Length && widths[i] > 0f) ? Sc(widths[i]) : 0f;
                    bool clicked = w > 0f
                        ? GUILayout.Button(labels[i], SR.Ctl.Btn, GUILayout.Width(w), GUILayout.Height(h))
                        : GUILayout.Button(labels[i], SR.Ctl.Btn, GUILayout.Height(h));
                    if (clicked) hit = i;
                }
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
            return hit;
        }



        // 表格自适应高度：usedAbove + 表格高 = 底栏之上可用高度；配合 TableAfterDraw 自校准。
    public static float TableAutoFit(float contentBottomY, float currentY, float minH, ref float bias) {
        if (contentBottomY <= 1f) return minH;
        return Mathf.Max(minH, contentBottomY - currentY + bias);
    }
    // 表格底边拖拽改高度（entry 值 0 = 自动高度）。
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
    // 竖向滚动条一体化：几何取自 TableArgs，绘制与输入一次完成。
    public static bool TableScrollbarFor(SRApi.TableArgs a, Event e, ref bool dragging, ref float grabY, ref float scrollY) {
        try {
            if (a == null || a.box.width <= 0f || a.rowsH <= 0f) return false;
            float contentH = a.rowCount * a.rowH;
            if (contentH <= a.rowsH + 0.5f) return false;
            float maxScroll = Mathf.Max(0f, contentH - a.rowsH);
            float sw = Sc(13f);
            // 有钉住列时滚动条放其外侧，否则放数据区右缘。
            float tx = a.pinW > 0f ? a.pinX + a.pinW : a.box.x + a.stripW;
            Rect track = new Rect(tx, a.box.y + a.headerH, sw, a.rowsH);
            Rect thumb;
            // 唯一开关：HideTableScrollbars（默认隐藏且不占位，也不接受点击）。
            bool __show = !HideTableScrollbars;
            a.scrollbarAlpha = __show ? 1f : 0f;
            TableScrollbar(track, contentH, a.rowsH, scrollY, dragging, out thumb, __show, a.scrollbarAlpha);
            if (!__show) return false;
            if (e == null) return false;
            return SRApi.TableScrollbarInput(e, e.mousePosition, track, thumb, maxScroll, ref dragging, ref grabY, ref scrollY);
        } catch (Exception __ex) { Guard.Log("表格滚动条一体化", __ex); return false; }
    }

    public static void TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible = true, float alpha = 1f) {
            SR.TableScrollbar(track, contentH, viewH, scrollY, dragging, out thumb, visible, alpha);
        }



        // 条目标签（可点击恢复默认值）。
        public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) { SR.RestoreLabel(content, entry, w, h, hotkey); }
        public static void RenderControl(ConfigEntryBase entry) { SR.RenderControl(entry); }



        // ================= 标签列宽（按页面收敛） =================
        // 取本页实际画过的标签最大值（上一帧值，两帧收敛）。
        private static float _labelColPrev, _labelColCur;




        
        private static ConfigEntryBase _numEdit;
        internal static ConfigEntryBase NumEdit { get { return _numEdit; } set { _numEdit = value; } }
        private static string _numText;
        internal static string NumText { get { return _numText; } set { _numText = value; } }






        public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { SR.RenderEntryRow(entry, isInternal, colWidth); }
        public static ConfigEntryBase FindEntry(string section, string key) { return SR.FindInternalEntry(section, key); }
        public static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) { return SR.AllEntries(config); }
        public static List<ConfigEntryBase> SectionEntries(string section) { return SR.SectionEntries(section); }
        public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) {
            if (colWidth <= 0f) colWidth = Sc(180f);   // 默认标签列宽（契约的默认宽度重载走这里）
            SR.RenderEntryRow(entry, true, colWidth);
        }
        public static float EntryNameWidth() { return SR.EntryNameWidth(); }
        public static float TextHeight(string text, float width) { return SR.TextHeight(text, width); }
        public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false) { return SR.DrawSlider(rect, value, min, max, intStep); }
        // 下拉框（自绘）；返回选中下标，-1 = 未选；width <= 0 时按最长选项实测自适应。
        // 由输入改变又决定"建不建控件"的状态必须按帧锁存，否则 Layout 与 Repaint 数量不一致会抛异常。
        private static readonly Dictionary<string, bool> _frameLatch = new Dictionary<string, bool>(32);
        private static int _frameLatchFrame = -1;
        public static bool FrameLatch(string key, Func<bool> live) {
            try {
                if (_frameLatchFrame != Time.frameCount) { _frameLatchFrame = Time.frameCount; _frameLatch.Clear(); }
                if (string.IsNullOrEmpty(key)) return live != null && live();
                bool v;
                if (_frameLatch.TryGetValue(key, out v)) return v;
                v = live != null && live();
                _frameLatch[key] = v;
                return v;
            } catch { return live != null && live(); }
        }

        public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = AutoComboBoxWidth(options, current);
            
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
            
            return Mathf.Clamp(maxText + Sc(FramePadX * 2 + 20f), minW, maxW);
        }
        // 统一按钮：带悬浮说明，页面里不要写裸 GUILayout.Button。
        public static bool Button(string label, string tip, float width = 0f, float height = 0f) {
            GUIContent c = string.IsNullOrEmpty(tip) ? new GUIContent(label) : new GUIContent(label, tip);
            bool clicked;
            if (width > 0f && height > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Width(Sc(width)), GUILayout.Height(Sc(height)));
            else if (width > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Width(Sc(width)));
            else if (height > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Height(Sc(height)));
            else clicked = GUILayout.Button(c, SR.Ctl.Btn);
            // 长按连发（与 HotkeyButton 同一套节流）。
            return clicked;
        }

        public static void WrapLabel(string text) { SR.WrapLabel(text); }
        public static float SidebarWidth() { return SR.SidebarWidth(); }
        public static void SetValue(ConfigEntryBase entry, object value) { SR.SetValue(entry, value); }

        public static string RecText() { return SR.RecText(); }
        // 原始皮肤字体，供窗口恢复用。
        public static Font OrigSkinFont() { return SR.OrigSkinFontValue; }
        public static string KeySuffix(ConfigEntry<KeyCode> e) { return SR.KeySuffix(e); }
        public static string KeyText(ConfigEntry<KeyCode> e) { return SR.KeyText(e); }
        // 动作行用的注入：录制开始/清空/键名。
        public static void BeginCaptureEntry(ConfigEntry<KeyCode> e) { SR.EnterCapturePublic(e); }
        // 供契约注入（在核心初始化时调用一次）。
        internal static void InstallActionRowSinks() {
            try {
                SRApi.Ctl.SetActionRowSink(SR.Ctl.RowsActionRowSink);
                SRApi.Ctl.SetRecTextSink(RecTextPublic);
                SRApi.Ctl.SetKeyTextSink(SR.KeyText);
                SRApi.Ctl.SetBeginCaptureSink(BeginCaptureEntry);
                SRApi.Ctl.SetClearKeySink(ClearKeyEntry);
                SRApi.Ctl.SetCursorTextureSink(delegate { return SR.CursorTexture; });
                SRApi.Ctl.SetRowGateSink(SR.Ctl.RegisterRowGateImpl);
            } catch (Exception __ia) { Guard.Log("注入动作行", __ia); }
        }
        public static string RecTextPublic() { return SR.RecText(); }
        // 条目可用性门（不可用 -> 置灰、不可点）。
        private static readonly Dictionary<ConfigEntryBase, Func<bool>> _rowGates = new Dictionary<ConfigEntryBase, Func<bool>>();
        public static void RegisterRowGateImpl(ConfigEntryBase entry, Func<bool> allowed) {
            if (entry == null) return;
            _rowGates[entry] = allowed;
        }
        internal static bool RowAllowed(ConfigEntryBase entry) {
            Func<bool> f;
            if (entry == null || !_rowGates.TryGetValue(entry, out f) || f == null) return true;
            try { return f(); } catch (Exception __ra) { Guard.Log("条目可用性门", __ra); return true; }
        }

        public static bool RowsActionRowSink(string label, string tip, string button, ConfigEntry<KeyCode> key, float bw, float lw) {
            return SR.Ctl.RowsActionRow(label, tip, button, key, bw, lw);
        }
        public static void ClearKeyEntry(ConfigEntry<KeyCode> e) { try { if (e != null) e.Value = KeyCode.None; } catch (Exception __ce) { Guard.Log("清空快捷键", __ce); } }
        public static void RegisterRowHotkey(ConfigEntry<KeyCode> entry) { SR.RegisterRowHotkey(entry); }
        public static void TryBindByRightClick(ConfigEntry<KeyCode> entry, Rect rect) { SR.TryBindByRightClick(entry, rect); }
        public static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.SelfToggleButton(label, tooltip, on, keyEntry, width, bindable);
        }
        public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.HotkeyActionButton(label, tooltip, keyEntry, width, bindable);
        }
        // 自动快捷键界面件（右键绑定，SR.CheckHotkeys 统一触发）。
        public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            return SR.HotkeyButton(id, label, tooltip, action, width, height);
        }

        
        public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            return SR.HotkeyButtonNoHint(id, label, tooltip, action, width, height);
        }
        public static string HotkeyText(string id) { return SR.HotkeyText(id); }
        public static void DrawTooltip(Vector2 mp) { SR.DrawTooltip(mp); }


        // 绝对矩形复选框。
        public static bool CheckboxAt(Rect r, bool value, string tip) {
            bool clicked;
            try {
                GUIContent c = (value && CheckTex == null) ? new GUIContent("✓", tip) : new GUIContent("", tip);
                clicked = GUI.Button(r, c, value ? _checkOn : _checkOff);
                if (value && CheckTex != null) {
                    float m = r.width * 0.18f;
                    GUI.DrawTexture(new Rect(r.x + m, r.y + m, r.width - m * 2f, r.height - m * 2f), CheckTex);
                }
            } catch { clicked = false; }
            return clicked;
        }


        public static float WinWidth { get { return _winWidth; } }
        // 滚到底；不要拿属性返回值去改 y（那是副本）。
        public static void ScrollToBottom() { _scroll.y = float.MaxValue; }
        public static ConfigFile InternalConfig { get { return _internalConfig; } }
        // 是否按英文输出（外部模块页强制中文时为 false）。
        public static bool LangEn { get { return UseEn; } }
        public static bool ForceZh { get { return _forceZh; } set { _forceZh = value; } }
        public static Dictionary<ConfigEntryBase, bool> EditOpen { get { return _editOpen; } }
        // 下拉框矩形登记（上一帧）：窗口据此在 BeginScrollView 之前截走滚轮。
        private static System.Collections.Generic.List<Rect> _comboRectsCur = new System.Collections.Generic.List<Rect>();
        private static System.Collections.Generic.List<Rect> _comboRectsPrev = new System.Collections.Generic.List<Rect>();
        internal static void ComboFrameBegin() {
            try {
                System.Collections.Generic.List<Rect> tmp = _comboRectsPrev;
                _comboRectsPrev = _comboRectsCur;
                _comboRectsCur = tmp;
                _comboRectsCur.Clear();
            } catch (Exception __cb) { Guard.Log("下拉框矩形/帧切换", __cb); }
        }
        internal static void NoteComboRect(Rect r) {
            try {
                if (r.width > 1f && r.height > 1f && _comboRectsCur.Count < 128) _comboRectsCur.Add(r);
            } catch (Exception __cn) { Guard.Log("下拉框矩形/登记", __cn); }
        }
        private static int _comboRectFrame = -1;   // ComboFrameBegin 每帧一次的锁存
        // 悬停下拉框时截走滚轮并清待处理量，下拉框自己的 WheelPoll 仍能换选项。
        internal static void ComboWheelIntercept() {
            try {
                if (Time.frameCount != _comboRectFrame) { _comboRectFrame = Time.frameCount; ComboFrameBegin(); }
                Event __e = Event.current;
                if (__e != null && __e.type == EventType.ScrollWheel && ComboUnderMouse(__e.mousePosition)) {
                    SR.Wheel.TakeRemaining(false);
                    __e.Use();
                }
            } catch (Exception __ci) { Guard.Log("下拉框滚轮/拦截", __ci); }
        }

        internal static bool ComboUnderMouse(Vector2 mp) {
            try {
                for (int i = 0; i < _comboRectsPrev.Count; i++) if (_comboRectsPrev[i].Contains(mp)) return true;
            } catch (Exception __cu) { Guard.Log("下拉框矩形/命中", __cu); }
            return false;
        }

        internal static int _comboOpenFrame = -1;
        internal static void NoteComboOpen() { _comboOpenFrame = Time.frameCount; }
        internal static bool AnyComboOpen() {
            try {
                if (_comboOpenFrame == Time.frameCount) return true;   // 任意下拉框本帧展开（含 entry=null 的页面自持下拉）
                foreach (KeyValuePair<ConfigEntryBase, bool> kv in _editOpen) if (kv.Value) return true;
            } catch (Exception __ex) { Guard.Log("下拉框展开状态", __ex); }
            return false;
        }
        // 配置落盘节流 + 条目缓存失效门面。
        public static void MarkConfigDirty(ConfigFile cfg) { SR.MarkConfigDirty(cfg); }
        public static void NoteEntryBound() { SR.NoteEntryBound(); }

    }

// 分区：Controls（通用控件：按键录制 / 右键绑键 / 开关按钮 + 首页）

        // 按钮矩形登记（右键绑键用），命中检测须在同一 GUI 组内。

        private static string RecText() {
            if (_recSeq.Count == 0) return T("请按键...", "press keys...");
            string s = "";
            foreach (KeyCode k in _recSeq) s += (s.Length > 0 ? " -> " : "") + KeyDisplayName(k);
            return s;
        }


        
        private static void RegisterRowHotkey(ConfigEntry<KeyCode> entry) {
            if (entry == null) return;
            if (Event.current == null) return;
            Rect r = GUILayoutUtility.GetLastRect();
            r.width += Sc(44);
            TryBindByRightClick(entry, r);
        }

        // 右键绑键当场判定：跨帧存矩形会因布局浮动而绑错按钮。
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
            
        }

        // 自身状态开关按钮：左键切换并显示绑定键；bindable 时支持右键绑键。
        private static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            
            string text = label + (on ? T("开", "ON") : T("关", "OFF")) + SR.KeySuffix(keyEntry);
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30))); // 控件统一 30px
            
            if (clicked && evBtn != 0) clicked = false;
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发切换
        }

        // 快捷键文本放进悬浮提示，避免绑定后按钮被截断。
        private static string KeyTip(ConfigEntry<KeyCode> key) {
            try {
                if (key == null || key.Value == KeyCode.None) return "";
                return "\n" + T("按键：", "Key: ") + SR.KeySuffix(key).Trim().Trim('[', ']');
            } catch { return ""; }
        }

        // 操作按钮：左键触发，bindable 时支持右键绑键。
        private static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            string text = label;
            if (keyEntry != null) {
                string actTip = string.IsNullOrEmpty(tooltip) ? label : label + "\n" + tooltip;
                actTip += KeyTip(keyEntry);
                
                if (bindable) {
                    string hintTxt = T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey");
                    tooltip = string.IsNullOrEmpty(actTip) ? hintTxt : actTip + "\n" + hintTxt;
                } else {
                    tooltip = actTip;
                }
                tooltip = SRApi.Ctl.CleanTip(tooltip);
            }
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30))); // 控件统一 30px
            // 左键执行动作，绑定用右键。
            if (clicked && keyEntry != null && !capturing && false) {
                EnterCapture(keyEntry);
                clicked = false;
            }
            if (clicked && evBtn != 0) clicked = false;
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发操作
        }

        // 捕获开始时刻：录制卡住时超时自动放弃，否则所有快捷键轮询会直接 return。
        internal static float CaptureStartedAt;
        
        internal static bool IsCapturingKey { get { return _capturing != null; } }
        internal const float CaptureTimeoutSeconds = 5f;

        /// 超时 / 异常时兜底取消：清状态并丢弃未提交的序列。
        internal static void CancelCapture() {
            try {
                _capturing = null;
                _recSeq.Clear();
                _recHeld.Clear();
                _dirty = true;
            } catch (Exception __ex) { Guard.Log("取消按键录制", __ex); }
        }

        // 进入录制（左键未绑定与右键都走它）。
        internal static void EnterCaptureInternal(ConfigEntry<KeyCode> key) { EnterCapture(key); }

        private static void EnterCapture(ConfigEntry<KeyCode> key) {
            if (key == null) return;
            _prevBoxed = key.BoxedValue;
            _capturing = key;
            _captureStartFrame = Time.frameCount;
            SR.CaptureStartedAt = Time.unscaledTime;
            _recSeq.Clear();
            _recHeld.Clear();
        }

        // 右键：进入捕捉；捕捉中再右键删除快捷键。
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
                CaptureStartedAt = Time.unscaledTime;  // 超时自愈用（见 SR.Window 捕获分支）
                _recSeq.Clear();
                _recHeld.Clear();
            }
        }

        // 登记 action -> 画按钮 -> 右键绑键 -> 返回左键点击。
        public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            SR.HotkeyAction(id, label, action);
            ConfigEntry<KeyCode> key = SR.Hotkey(id);
            bool capturing = key != null && _capturing == key;
            // 快捷键框只显示按键；动作名进提示，提示不能以换行开头。
            string tip = tooltip ?? "";
            string hint = T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey");
            tip = tip.Length > 0 ? tip + "\n" + hint : hint;
            tip = SRApi.Ctl.CleanTip(tip);
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


        // 可折叠分区。
        private static readonly HashSet<string> _secClosed = new HashSet<string>();

        internal static bool SectionBegin(string id, string title, string tip) {
            GUILayout.BeginVertical(GUI.skin.box);
            bool open = !_secClosed.Contains(id);
            GUIContent c = string.IsNullOrEmpty(tip) ? new GUIContent((open ? "\u25BC " : "\u25B6 ") + title)
                                                      : new GUIContent((open ? "\u25BC " : "\u25B6 ") + title, tip);
            if (GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Height(Sc(24)))) {
                if (open) _secClosed.Add(id); else _secClosed.Remove(id);
                open = !open;
            }
            return open;
        }

        internal static void SectionEnd() { GUILayout.EndVertical(); }



        // 与 HotkeyButton 相同，但提示里不追加绑键说明。
        public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            SR.HotkeyAction(id, label, action);
            ConfigEntry<KeyCode> key = SR.Hotkey(id);
            bool capturing = key != null && _capturing == key;
            string tip = SRApi.Ctl.CleanTip(tooltip ?? "");
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
            // 自检发现问题时在首页提示；详细清单在 设置 -> 自检。
            if (SR.AuditHasIssues) {
                Color __pc = GUI.color;
                try {
                    GUI.color = new Color(1f, 0.78f, 0.35f, 1f);
                    WrapLabelTight(T(" 有 " + SR.AuditIssueCount + " 项按名绑定的目标失效，对应功能不会生效 - 详见 设置 -> 自检。",
                        " " + SR.AuditIssueCount + " name-bound target(s) are broken; those features will not work - see Settings > Self-check."));
                } finally { GUI.color = __pc; }
                GUILayout.Space(Sc(2));
            }
            WrapLabel(T("Ultimate Chicken Horse 模组整合增强包。", "An enhancement pack for Ultimate Chicken Horse."));
            WrapLabel(T("本 mod 参考了 BetterFreeplay，BuildingPlus，BuildUnlimiter，UCH Freeplay Spawn Setter，UCH Tweaks，UCH-PlayerTracker-Mod，UltimateBuilder，向这些 mod 的制作者表示感谢。", "This mod references BetterFreeplay, BuildingPlus, BuildUnlimiter, UCH Freeplay Spawn Setter, UCH Tweaks, UCH-PlayerTracker-Mod and UltimateBuilder. Thanks to their authors."));
            GUILayout.Space(Sc(2));

            GUILayout.Label(T("- 开源地址 -", "- Source -"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("https://github.com/RSTFS/SR_UCH", _btn, GUILayout.Height(Sc(30)))) {
                try { Application.OpenURL("https://github.com/RSTFS/SR_UCH"); } catch (Exception __ex) { Guard.Log("打开开源地址", __ex); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));

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
            GUILayout.Space(Sc(2));

            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(2));
        }

// 分区：RowHooks（功能对通用条目行的扩展点；RowFilters 是布局过滤而非权限门禁）

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    // 字符串条目的下拉候选：values 落盘值，displayNames 显示名。
    public delegate bool RowComboOptionsFn(ConfigEntryBase entry, out string[] values, out string[] displayNames);
    public delegate void PageRenderFn();

    public static readonly List<RowComboOptionsFn> RowComboOptions = new List<RowComboOptionsFn>();

    public static bool RowVisible(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowFilters.Count; i++) {
            try { if (!SRApi.RowFilters[i](entry)) return false; } catch (Exception __ex) { Guard.Log("行可见性过滤", __ex); }
        }
        return true;
    }

    // 同伴条目 key（同行右侧再渲染一个控件），null = 无
    public static string RowCompanion(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowCompanions.Count; i++) {
            try { string k = SRApi.RowCompanions[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("同伴条目", __ex); }
        }
        return null;
    }

    public static string RowExtra(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowExtras.Count; i++) {
            try { string k = SRApi.RowExtras[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("行附加控件", __ex); }
        }
        return null;
    }

    // 行专用滑块范围（fmt = 显示格式）
    public static bool RowSliderRange(ConfigEntryBase entry, out float min, out float max, out string fmt) {
        min = 0f; max = 0f; fmt = "0.0";
        for (int i = 0; i < SRApi.RowSliders.Count; i++) {
            try { if (SRApi.RowSliders[i](entry, out min, out max, out fmt)) return true; } catch (Exception __ex) { Guard.Log("行滑块范围", __ex); }
        }
        return false;
    }

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

    public static float RowComboWidth(ConfigEntryBase entry) {
        for (int i = 0; i < SRApi.RowComboWidths.Count; i++) {
            try { float w = SRApi.RowComboWidths[i](entry); if (w > 0f) return w; } catch (Exception __ex) { Guard.Log("下拉框宽度", __ex); }
        }
        return 0f;
    }

    // 行下拉候选；不合法（空 / 长度不一致）视作没命中
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

    public static bool RowEnumAllowed(ConfigEntryBase entry, string enumName, int value) {
        for (int i = 0; i < SRApi.RowEnumFilters.Count; i++) {
            try { if (!SRApi.RowEnumFilters[i](entry, enumName, value)) return false; } catch (Exception __ex) { Guard.Log("枚举过滤", __ex); }
        }
        return true;
    }

    // 功能自绘页：注册后 SR.Window 优先调用。
    public static readonly Dictionary<string, PageRenderFn> PageRenderers = new Dictionary<string, PageRenderFn>();

    public static void RegisterPage(string section, PageRenderFn render) {
        if (string.IsNullOrEmpty(section) || render == null) return;
        PageRenderers[section] = render;
        _pageLists.Remove(section); //单页注册覆盖多页（同一 section 不应混用两种注册）
        OnNavChanged(section);      //晚注册（首次扫描之后）：立刻重建侧栏，否则页面存在但栏目不显示
    }

    private sealed class PageInfo {
        public string Id;      //稳定 id（选中态与页面外框判断用）
        public string Zh;      //中文显示名（运行时按当前语言翻译，切语言立即生效）
        public string En;      //英文显示名
        public int Order;      //页签顺序
        public PageRenderFn Render;
    }
    private static readonly Dictionary<string, List<PageInfo>> _pageLists = new Dictionary<string, List<PageInfo>>();
    private static readonly Dictionary<string, string> _pageSel = new Dictionary<string, string>();

    // 多页注册：≥2 页后自动画页签并只渲染当前页；会清掉单页渲染器，两种注册不可混用。
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

    // 选中的子页跨会话记住（内部配置 [UI] Page <段名>）。
    private static string PersistedPage(string section) {
        try {
            if (_internalConfig == null || string.IsNullOrEmpty(section)) return null;
            return _internalConfig.Bind("UI", "Page " + section, "", "上次打开的子页（内部状态，勿手动修改）").Value;
        } catch (Exception __ex) { Guard.Log("读取记忆子页", __ex); return null; }
    }

    private static void PersistPage(string section, string id) {
        try {
            if (_internalConfig == null || string.IsNullOrEmpty(section)) return;
            ConfigEntryBase e = _internalConfig.Bind("UI", "Page " + section, "", "上次打开的子页（内部状态，勿手动修改）");
            string cur = e.BoxedValue as string ?? "";
            if (cur != (id ?? "")) e.BoxedValue = id ?? "";
        } catch (Exception __ex) { Guard.Log("记忆子页", __ex); }
    }

    // 当前选中的子页 id（未选中时用上次记住的，其次第一页）。
    public static string SelectedPageId(string section) {
        string id;
        if (!string.IsNullOrEmpty(section) && _pageSel.TryGetValue(section, out id)) return id;
        List<PageInfo> list;
        if (!string.IsNullOrEmpty(section) && _pageLists.TryGetValue(section, out list) && list.Count > 0) {
            
            string saved = PersistedPage(section);
            if (!string.IsNullOrEmpty(saved)) {
                for (int i = 0; i < list.Count; i++) if (list[i].Id == saved) { id = saved; _pageSel[section] = id; return id; }
            }
            return list[0].Id;
        }
        return null;
    }

    // Ctrl+滚轮切页：在栏目页列表里循环移动一位。
    public static void CyclePage(string section, int dir) {
        try {
            List<PageInfo> list;
            if (string.IsNullOrEmpty(section) || !_pageLists.TryGetValue(section, out list) || list.Count <= 1) return;
            string cur = SelectedPageId(section);
            int idx = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].Id == cur) { idx = i; break; }
            int nx = ((idx + (dir >= 0 ? 1 : -1)) % list.Count + list.Count) % list.Count;
            _pageSel[section] = list[nx].Id;
            PersistPage(section, list[nx].Id);
        } catch (Exception __ex) { Guard.Log("切换子页", __ex); }
    }

    public static bool HasPage(string section) {
        return !string.IsNullOrEmpty(section) && (PageRenderers.ContainsKey(section) || _pageLists.ContainsKey(section));
    }

    // 输入框内按下必须禁止拖窗口，否则输入时会拖走面板。
    private static void BlockDragOnField(Rect rr, Event e) {
        try {
            if (e != null && e.type == EventType.MouseDown && e.button == 0 && rr.Contains(e.mousePosition)) BlockWindowDrag = true;
        } catch (Exception __bd) { Guard.Log("输入框/禁止拖窗口", __bd); }
    }

    public static string ClearableTextField(string text, GUIStyle style, params GUILayoutOption[] options) {
        string r = GUILayout.TextField(text, style, options);
        Rect rr = GUILayoutUtility.GetLastRect();
        Event e = Event.current;
        BlockDragOnField(rr, e);
        if (e != null && e.type == EventType.MouseDown && e.button == 1 && rr.Contains(e.mousePosition)) { r = ""; e.Use(); }
        return r;
    }

    public static string ClearableTextField(string text, int maxLength, GUIStyle style, params GUILayoutOption[] options) {
        string r = GUILayout.TextField(text, maxLength, style, options);
        Rect rr = GUILayoutUtility.GetLastRect();
        Event e = Event.current;
        BlockDragOnField(rr, e);
        if (e != null && e.type == EventType.MouseDown && e.button == 1 && rr.Contains(e.mousePosition)) { r = ""; e.Use(); }
        return r;
    }




    // 页签行：须在 RenderPageHeader 之前调用；每栏目有一个左侧前缀绘制点。
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
                if (GUILayout.Button(nm, on ? _selItem : _item, GUILayout.Height(Sc(26)))) { _pageSel[section] = list[i].Id; PersistPage(section, list[i].Id); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
        } catch (Exception __ex) { Guard.Log("页签(" + section + ")", __ex); }
    }

    public static void RenderPage(string section) {
        List<PageInfo> list;
        if (!string.IsNullOrEmpty(section) && _pageLists.TryGetValue(section, out list) && list.Count > 0) {
            try {
                string sel = SelectedPageId(section);
                if (list.Count <= 1) { _pageSel[section] = sel; PersistPage(section, sel); }
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

    // 功能页外框：滚动区之外的自绘内容。
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

    // 统一控件高度（默认 26，功能可用 ControlHeight 覆盖）。
    public static float ControlH() {
        try { return Sc(ControlHeight > 0f ? ControlHeight : 26f); } catch { return Sc(26f); }
    }

    }
}
