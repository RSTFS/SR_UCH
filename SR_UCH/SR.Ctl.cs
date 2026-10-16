// 控件与页面能力门面：管理器通用控件（按键录制/右键绑键/开关/滑块/下拉）、首页、Ctl 门面、行页扩展点 RowHooks、表格控件 TableArgs -> TableDraw。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx;
using BepInEx.Bootstrap;

namespace SR_UCH.Tweaks {
public partial class SR {
// 分区：Ctl（功能页能力门面 + 管理器通用控件 + 首页 + 行/页扩展点）
// Ctl 只转发不放逻辑：功能页与外部模块走 SR.Ctl，核心内部（SR 各分部文件）直接调 SR；新能力在 Ctl 里补纯转发成员。

    public static class Ctl {

        // UI 缩放系数（各页像素尺寸都乘它）
        public static float Sc(float v) { return SR.Sc(v); }

        // 条目标签（可点击恢复默认值；tooltip = 说明 + 默认值 + 提示）
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
        // 下拉框（自绘：列表紧跟按钮下方）；返回选中下标，-1 = 未选
        public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) { return SR.ComboBox(entry, current, vals, options, ref open, width); }
        public static void WrapLabel(string text) { SR.WrapLabel(text); }
        public static float SidebarWidth() { return SR.SidebarWidth(); }
        // 写配置值（会标记待保存）
        public static void SetValue(ConfigEntryBase entry, object value) { SR.SetValue(entry, value); }

        // 按键按钮（含右键绑键 / 组合键录制）
        public static string RecText() { return SR.RecText(); }
        // 原始皮肤字体（由 SR.Styles 在首次改皮肤前记录），供窗口恢复用。
        public static Font OrigSkinFont() { return SR.OrigSkinFontValue; }
        public static string KeySuffix(ConfigEntry<KeyCode> e) { return SR.KeySuffix(e); }
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

            // 每帧调用：顺序/隐藏列被改动时才落盘。
            public void SaveIfDirty() {
                if (Sig() != _sig) Save();
            }
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

        //操作按钮：左键触发操作。bindable=true 时该按钮支持右键绑定快捷键（其余按钮右键无反应）。
        private static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            string text = label + SR.KeySuffix(keyEntry);
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
            string tip = string.IsNullOrEmpty(tooltip) ? "" : tooltip + "\n";
            tip += T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey");
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            float bw = width > 0f ? width : Sc(150);
            float bh = height > 0f ? height : Sc(30);
            bool clicked = GUILayout.Button(new GUIContent(label + SR.KeySuffix(key), tip),
                capturing ? _capture : _btn, GUILayout.Width(bw), GUILayout.Height(bh));
            if (clicked && evBtn != 0) clicked = false; //右键绝不触发左键动作
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
        private static void DrawTooltip(Vector2 mp) {
            string tip = GUI.tooltip;
            if (tip == null || tip.Length == 0) return;
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
            GUI.Box(new Rect(x, y, _tipW, _tipH + 8), _tipContent, _tooltip);
        }
// 分区：RowHooks（功能对通用条目行的扩展点）
// SR.Settings 渲染条目行时不硬编码 section/key，功能在 Initialize/SelfReg 里注册以下扩展点。
// 行可见性、同伴条目（同一行右侧再画一个控件）、行专用滑块范围、下拉框宽度、枚举选项过滤。
// RowFilters 是布局过滤而非权限门禁：只决定条目是否单独成行 / 是否在设置页重复渲染。

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    public delegate float RowComboWidthFn(ConfigEntryBase entry);
    public delegate bool RowEnumFilterFn(ConfigEntryBase entry, string enumName, int value);
    // 字符串条目的下拉候选扩展点：命中即用下拉框渲染该条目。
    // values = 落盘值，displayNames = 界面显示（两者等长）。
    public delegate bool RowComboOptionsFn(ConfigEntryBase entry, out string[] values, out string[] displayNames);
    public delegate void PageRenderFn();

    public static readonly List<RowFilterFn> RowFilters = new List<RowFilterFn>();
    public static readonly List<RowCompanionFn> RowCompanions = new List<RowCompanionFn>();
    public static readonly List<RowCompanionFn> RowExtras = new List<RowCompanionFn>(); //本行右侧"再渲染一个控件"的条目（如 缩放+键位、文字大小+开关）
    public static readonly List<RowSliderFn> RowSliders = new List<RowSliderFn>();
    public static readonly List<RowComboWidthFn> RowComboWidths = new List<RowComboWidthFn>();
    public static readonly List<RowEnumFilterFn> RowEnumFilters = new List<RowEnumFilterFn>();
    public static readonly List<RowComboOptionsFn> RowComboOptions = new List<RowComboOptionsFn>();

    // 该条目当前是否显示（任一过滤器返回 false 即不显示）
    public static bool RowVisible(ConfigEntryBase entry) {
        for (int i = 0; i < RowFilters.Count; i++) {
            try { if (!RowFilters[i](entry)) return false; } catch (Exception __ex) { Guard.Log("行可见性过滤", __ex); }
        }
        return true;
    }

    // 同伴条目 key（同一行右侧再渲染一个控件），null = 无
    public static string RowCompanion(ConfigEntryBase entry) {
        for (int i = 0; i < RowCompanions.Count; i++) {
            try { string k = RowCompanions[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("同伴条目", __ex); }
        }
        return null;
    }

    // 同伴条目 key（同一行右侧"再渲染一个控件"），null = 无
    public static string RowExtra(ConfigEntryBase entry) {
        for (int i = 0; i < RowExtras.Count; i++) {
            try { string k = RowExtras[i](entry); if (k != null) return k; } catch (Exception __ex) { Guard.Log("行附加控件", __ex); }
        }
        return null;
    }

    // 行专用滑块范围（命中即用滑块渲染这个数值条目；fmt = 数值显示格式）
    public static bool RowSliderRange(ConfigEntryBase entry, out float min, out float max, out string fmt) {
        min = 0f; max = 0f; fmt = "0.0";
        for (int i = 0; i < RowSliders.Count; i++) {
            try { if (RowSliders[i](entry, out min, out max, out fmt)) return true; } catch (Exception __ex) { Guard.Log("行滑块范围", __ex); }
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
    public sealed class TableArgs {
        public Rect box;                 //表格外框（屏幕坐标）
        public Rect rowsClip;            //行区裁剪矩形（命中和绘制共用）
        public float headerH, rowH, rowsH, stripW;
        public float[] colW;
        public List<int> vis;            //可见列顺序
        public int rowCount, pinCol = -1, alignLeftCol = -1, moveCol = -1;
        public float scrollX, scrollY;
        public Vector2 mp;
        public float pinW, pinX;
        public string pinTitle;
        public System.Func<int, string> title;          //列标题(列下标)
        public System.Func<int, string> tip;            //列头悬停提示(可选)
        public System.Func<int, string[]> rowCells;     //整行文本（行号 -> 各列文本）
        public System.Func<int, float> rowAlpha;       //行透明度（可选；不可用的行压暗）
        public bool drawHeader = true;                 //是否画表头（表头另外画时置 false）
        // 竖向排列：格子沿 Y 叠放（每项一行），默认 false。
        public bool vertical;
        public float[] defaultColW; // 默认列宽（Shift+右键还原时核心会还原成它）
        public System.Func<int, bool> rowSelected; // 某行是否“选中”（核心据此画整行高亮）
        public bool headerLeft; // 表头文字靠左（默认居中）
        public bool resizable;                         //表头分隔线可拖动改列宽（与房间列表同款）
        public int dragCol = -1;                       //列宽拖动状态（由参数对象自己持有，跨帧保持）
        public float dragX0, dragW0;
        public Vector2 scrollColsTmp, scrollColsAtDragTmp;
        public bool reorderable;                       //表头可拖动换列位
        public int[] order;                            //显示顺序（数据列下标）
        public System.Action<int[]> applyOrder;        //换位回调（调用方保存新顺序）
        public System.Action saveOrder;                //换位后落盘（可选）
        public System.Action saveWidths;               //列宽拖动松开时落盘（可选；核心自己处理 MouseUp 清状态）
        public System.Action<int> click;               //单击列头（可选）
        public System.Action resetAll;                 //Shift+右键=还原全部（可选）
        // 钉住列按钮与空列表提示也由本控件负责绘制：三处列表只提供内容。
        public System.Action<int, Rect, int> pinDraw;  //(数据行下标, 钉住列绝对矩形, 可见序号)
        public System.Func<string> emptyHint;          //无行时的居中提示（返回 null/空 = 不画）
        public float moveX0, moveDx;
        public List<int> allCols = new List<int>();
        public System.Collections.Generic.HashSet<int> hidden; //被隐藏的列（右键表头隐藏，Shift+右键恢复）
        public bool hideable;                                  //是否允许右键隐藏列 / 显示+N恢复按钮
        public int sortCol = -1;                               //当前排序列（仅用于给列头画标记）
        public bool sortAsc = true;
        public List<Rect> headerCells = new List<Rect>();
        public List<int> headerCols = new List<int>();
        public List<Rect> rowRects = new List<Rect>();
        public List<int> rowRows = new List<int>();
        public List<Rect> pinRects = new List<Rect>();
        public List<int> pinRows = new List<int>();
        public Rect pinHeader;
    }

    //统一入口：表头 + 行命中 + 行单元格 + 钉住列命中（滚动条/输入由调用方按需要另调）
    public static void TableDraw(TableArgs a) {
        if (a == null || a.colW == null || a.vis == null) return;
        try {
            // vis 由 order + hidden 自行推导：TableColMove 换位后只更新 order，调用方无需再维护 vis。
            if (a.order != null && a.colW != null && a.order.Length == a.colW.Length) {
                a.vis.Clear();
                for (int i = 0; i < a.order.Length; i++) {
                    int ci = a.order[i];
                    if (ci < 0 || ci >= a.colW.Length) continue;
                    if (a.hidden != null && a.hidden.Contains(ci)) continue;
                    a.vis.Add(ci);
                }
            }
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
            if (a.rowCount == 0 && a.emptyHint != null) {
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
            // 行悬停用的裁剪区内坐标（原点 = 裁剪框左上；mp 是进裁剪前抓的屏幕坐标）。
            Vector2 __lm = new Vector2(a.mp.x - a.box.x, a.mp.y - a.box.y - headerH);
            for (int i = 0; i < a.rowCount; i++) {
                float y = i * rowH - a.scrollY;
                if (y > a.rowsH || y + rowH < 0f) continue;
                string[] rc = a.rowCells != null ? a.rowCells(i) : null;
                float alpha = a.rowAlpha != null ? a.rowAlpha(i) : 1f;
                // 隔行底：按真实行号取奇偶（行号不随滚动变化，条纹稳定）；行透明时底也跟着淡。
                if ((i & 1) == 1) {
                    Color ab = CurStyle.RowAltBg;
                    if (alpha < 1f) ab.a *= alpha;
                    DrawFill(new Rect(0f, y, a.stripW, rowH), ab);
                }
                // 行悬停反馈：加在隔行底之上、文字之下。
                float __hy0 = Mathf.Max(y, 0f), __hy1 = Mathf.Min(y + rowH, a.rowsH);
                if (__hy1 > __hy0 && __lm.x >= 0f && __lm.x < a.stripW && __lm.y >= __hy0 && __lm.y < __hy1) {
                    Color __hc = new Color(1f, 1f, 1f, 0.05f);
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
        } catch (Exception __ex) { Guard.Log("表格绘制", __ex); }
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
        for (int i = 0; i < rowCount; i++) {
            float y = i * rowH - scrollY;
            if (y > rowsH || y + rowH < 0f) continue;
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

	}
}
