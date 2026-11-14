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

    public static partial class Ctl {

        // 颜色调色板自注入：核心在 Ctl 首次被访问时把实现挂到契约层门面。
        // 放在这里而不是 MainPlugin 的注入清单里 —— 新增 Set*Sinks 会要求 MainPlugin 同步改（该文件由他人收尾）。
        static Ctl() { SR.ColorPicker.Wire(); SRApi.Ctl.WireSection(SectionBegin, SectionEnd); }

        // UI 缩放系数（各页像素尺寸都乘它）
        public static float Sc(float v) { return SR.Sc(v); }

        // 统一控件高度：按钮 / 输入框 / 下拉框 / 复选框同高（默认 26，按 UI 缩放；功能可用 ControlHeight 覆盖）。
        // UI 主题令牌：圆角、过渡时长、底栏/面板底色（各页面统一引用，避免散落魔数）。
        public static float ThemeRadius { get { return Sc(6f); } }
        public static float ThemeHoverFade { get { return 0.12f; } }
        public static Color ThemeFooterBg { get { return new Color(0.10f, 0.11f, 0.13f, 0.96f); } }
        public static Color ThemePanelBg { get { return new Color(0.08f, 0.08f, 0.09f, 0.96f); } }
        public static float ControlH() { return SR.ControlH(); }








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
            // 唯一开关：SR.Table.HideTableScrollbars（默认 true = 不显示、也不占位）。
            // 隐藏时同样"不接受点击/拖动"——避免看不见却能点的隐形热区；列表统一走滚轮。
            // 要恢复可见滚动条，只改 HideTableScrollbars 一处即可（绘制与命中会一起恢复）。
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







        // 条目标签（可点击恢复默认值；tooltip = 说明 + 后缀追加的默认值，不再带恢复提示）
        public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) { SR.RestoreLabel(content, entry, w, h, hotkey); }
        public static void RenderControl(ConfigEntryBase entry) { SR.RenderControl(entry); }





        // ================= 标签列宽（按页面收敛） =================
        // 旧实现用 EntryNameWidth()：整个 section 里最长的那条配置名，哪怕本页根本没渲染它，
        // 结果"标签比文字宽一大截"。这里改成"本页实际画过的标签"取最大值（上一帧值，两帧收敛）。
        private static float _labelColPrev, _labelColCur;




        // Ctrl+点击滑块后的输入态（同一时刻只编辑一个数值）
        private static ConfigEntryBase _numEdit;
        internal static ConfigEntryBase NumEdit { get { return _numEdit; } set { _numEdit = value; } }
        private static string _numText;
        internal static string NumText { get { return _numText; } set { _numText = value; } }






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
        // 每帧锁存的布尔决策：同一帧里 OnGUI 会跑多趟（Layout → 输入事件 → Repaint），
        // 凡是由输入改变、又决定"建不建控件"的状态（下拉框展开、内联编辑框…），
        // 逐趟读实时值会让 Repaint 比 Layout 多建/少建控件，直接抛
        // "Getting control N's position in a group with only N controls"。统一按帧锁存。
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
        // 统一按钮：带悬浮说明（页面里不要再写裸 GUILayout.Button）
        public static bool Button(string label, string tip, float width = 0f, float height = 0f) {
            GUIContent c = string.IsNullOrEmpty(tip) ? new GUIContent(label) : new GUIContent(label, tip);
            bool clicked;
            if (width > 0f && height > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Width(Sc(width)), GUILayout.Height(Sc(height)));
            else if (width > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Width(Sc(width)));
            else if (height > 0f) clicked = GUILayout.Button(c, SR.Ctl.Btn, GUILayout.Height(Sc(height)));
            else clicked = GUILayout.Button(c, SR.Ctl.Btn);
            // 长按连发：刷新派对盒这类按钮不用狂点（与 HotkeyButton 同一套节流，按 label 分桶）。
            return clicked;
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


        // 绝对矩形复选框（表格钉住列用）：与设置页开关同款 —— CheckOn/CheckOff 样式 + 勾选贴图叠加。
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
        //滚到底（新消息到达时用；不要拿属性返回值去改 y，那是副本，改了不生效）
        public static void ScrollToBottom() { _scroll.y = float.MaxValue; }
        public static ConfigFile InternalConfig { get { return _internalConfig; } }
        // 界面是否英文系 = 内置英文或语言包声明回退英文（外部模块页强制中文时为 false）。
        public static bool LangEn { get { return UseEn; } }
        // 外部模块页豁免：渲染期间置 true（该页始终中文）
        public static bool ForceZh { get { return _forceZh; } set { _forceZh = value; } }
        // 滑块提交为“值变了就写”（ImGui 的 v_last 模式）：取 DrawSlider 返回值，变了就调本方法或 SetValue。
        public static Dictionary<ConfigEntryBase, bool> EditOpen { get { return _editOpen; } }
        // 是否有下拉框处于展开状态：页面滚动区据此让出滚轮（否则滚轮切换下拉项时整页跟着滑）。
        internal static int _comboOpenFrame = -1;
        internal static void NoteComboOpen() { _comboOpenFrame = Time.frameCount; }
        internal static bool AnyComboOpen() {
            try {
                if (_comboOpenFrame == Time.frameCount) return true;   // 任意下拉框本帧展开（含 entry=null 的页面自持下拉）
                foreach (KeyValuePair<ConfigEntryBase, bool> kv in _editOpen) if (kv.Value) return true;
            } catch (Exception __ex) { Guard.Log("下拉框展开状态", __ex); }
            return false;
        }
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
                // 拼接不能无条件加换行（actTip 为空时会出现"第一行空行"，用户反馈 11 同类问题）。
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
            // 按钮：左键永远执行动作；绑定快捷键用右键（快捷键"框"才用左键开始录制）。
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

        // 捕获开始时刻（Time.unscaledTime）：录制若因为事件被游戏吃掉而卡住，超时后自动放弃，
        // 否则 _capturing 一直非空 -> 所有快捷键轮询直接 return（表现为"改了键以后热键全失灵"）。
        internal static float CaptureStartedAt;
        // 供窗口层判断是否正在录键（失焦/切场景时用它决定要不要取消）。
        internal static bool IsCapturingKey { get { return _capturing != null; } }
        // 录制超时（秒）：窗口层用它判断是否自动放弃卡住的录制。
        internal const float CaptureTimeoutSeconds = 5f;

        /// 超时/异常时的兜底取消：清状态并丢弃未提交的录制序列。
        internal static void CancelCapture() {
            try {
                _capturing = null;
                _recSeq.Clear();
                _recHeld.Clear();
                _dirty = true;
            } catch (Exception __ex) { Guard.Log("取消按键录制", __ex); }
        }

        // 进入录制（左键未绑定时与右键都用它，确保 CaptureStartedAt 一定被刷新）
        private static void EnterCapture(ConfigEntry<KeyCode> key) {
            if (key == null) return;
            _prevBoxed = key.BoxedValue;
            _capturing = key;
            _captureStartFrame = Time.frameCount;
            SR.CaptureStartedAt = Time.unscaledTime;
            _recSeq.Clear();
            _recHeld.Clear();
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
                CaptureStartedAt = Time.unscaledTime;  // 超时自愈用（见 SR.Window 捕获分支）
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
            // 注意：以前是 tip = "\n" + tooltip，导致悬浮框第一行永远是一条空行（用户反馈 11）。
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
            // 按钮：左键永远执行动作；绑定用右键。
            if (clicked && evBtn != 0) clicked = false; //右键绝不触发左键动作
            if (key != null && e != null) TryBindByRightClick(key, GUILayoutUtility.GetLastRect());
            return clicked && !capturing;
        }


        // 可折叠分区（功能层经 SRApi.Ctl.SectionBegin/SectionEnd 使用）。
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



        // 与 HotkeyButton 相同（右键可绑键、点击执行），但悬浮提示里不追加“右键：绑定/删除快捷键”。
        public static bool HotkeyButtonNoHint(string id, string label, string tooltip, Action action, float width = 0f, float height = 0f) {
            SR.HotkeyAction(id, label, action);
            ConfigEntry<KeyCode> key = SR.Hotkey(id);
            bool capturing = key != null && _capturing == key;
            // 同 HotkeyButton：不能再以换行开头（否则第一行是空行）。
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
            // 启动自检发现按名绑定的目标失效时，在首页最上方提示；详细清单在 设置 -> 自检。
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

// 分区：RowHooks（功能对通用条目行的扩展点）
// SR.Settings 渲染条目行时不硬编码 section/key，功能在 Initialize/SelfReg 里注册以下扩展点。
// 行可见性、同伴条目（同一行右侧再画一个控件）、行专用滑块范围、下拉框宽度、枚举选项过滤。
// SRApi.RowFilters 是布局过滤而非权限门禁：只决定条目是否单独成行 / 是否在设置页重复渲染。

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    // 字符串条目的下拉候选扩展点：命中即用下拉框渲染该条目。
    // values = 落盘值，displayNames = 界面显示（两者等长）。
    public delegate bool RowComboOptionsFn(ConfigEntryBase entry, out string[] values, out string[] displayNames);
    public delegate void PageRenderFn();

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
        for (int i = 0; i < SRApi.RowComboWidths.Count; i++) {
            try { float w = SRApi.RowComboWidths[i](entry); if (w > 0f) return w; } catch (Exception __ex) { Guard.Log("下拉框宽度", __ex); }
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
        for (int i = 0; i < SRApi.RowEnumFilters.Count; i++) {
            try { if (!SRApi.RowEnumFilters[i](entry, enumName, value)) return false; } catch (Exception __ex) { Guard.Log("枚举过滤", __ex); }
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

    // 选中的子页要跨会话记住：写进内部配置（[UI] Page <段名>），重启后回到上次那一页。
    // 用字面量做段/键名，避免新增私有静态字段（SR 分区的字段登记守卫）。
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

    //当前选中的子页 id（未选中时用上次记住的，其次第一页）, 供页面外框（顶栏/底栏）判断是否属于自己那页
    public static string SelectedPageId(string section) {
        string id;
        if (!string.IsNullOrEmpty(section) && _pageSel.TryGetValue(section, out id)) return id;
        List<PageInfo> list;
        if (!string.IsNullOrEmpty(section) && _pageLists.TryGetValue(section, out list) && list.Count > 0) {
            // 上次记住的页还在才用；页面被移除/改名时回退第一页。
            string saved = PersistedPage(section);
            if (!string.IsNullOrEmpty(saved)) {
                for (int i = 0; i < list.Count; i++) if (list[i].Id == saved) { id = saved; _pageSel[section] = id; return id; }
            }
            return list[0].Id;
        }
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
            PersistPage(section, list[nx].Id);
        } catch (Exception __ex) { Guard.Log("切换子页", __ex); }
    }

    public static bool HasPage(string section) {
        return !string.IsNullOrEmpty(section) && (PageRenderers.ContainsKey(section) || _pageLists.ContainsKey(section));
    }

    //文本框（右键清空）：命中该框的右键会清空内容并吃掉事件。
    // 输入框：左键按在框内时必须禁止拖窗口（用户要求"只有空白处才能拖界面"）。
    // 以前只有部分控件置 BlockWindowDrag，输入框没置，于是点输入框开始输入时会顺手把面板拖走。
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
