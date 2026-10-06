// 控件与页面能力门面。管理器里的通用控件（按键录制、右键绑键、开关、滑块、下拉）、首页，
// 给功能页用的能力门面 Ctl、通用条目行扩展点 RowHooks，
// 以及固定表头表格 TableArgs -> TableDraw（列宽拖拽 / 换序 / 隐藏 / 点击回调 / 滚轮）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx;
using BepInEx.Bootstrap;

namespace SR_UCH.Tweaks {
public partial class SR {
// 分区：Ctl（功能页能力门面 + 管理器通用控件 + 首页 + 行/页扩展点）
// 本文件三部分：SR.Ctl（**只转发不放逻辑**，供功能页/外部模块调用）、通用控件与首页、行/页扩展点注册表。
// Ctl 是功能页与外部模块的规范入口 —— 全仓大量调用点走 SR.Ctl 这层门面，而 SR 上的同名成员是核心内部实现。所以：
//   功能页/外部模块走 SR.Ctl，核心内部（SR 各分部文件）直接调 SR；给功能页加新能力时在 Ctl 里补一个纯转发成员。
// **历史教训**：曾把 facade 成员误插进 Ctl 本体，造成 GroupBegin 无限递归，而且编译器不报错。
//   所以新逻辑一律写在 SR，Ctl 只加转发。

    public static class Ctl {

        // UI 缩放系数（各页的像素尺寸都乘它）
        public static float Sc(float v) { return SR.Sc(v); }

        // 通用条目控件
        // 条目标签（可点击恢复默认值；tooltip = 说明 + 默认值 + 提示）
        public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) { SR.RestoreLabel(content, entry, w, h, hotkey); }
        public static void RenderControl(ConfigEntryBase entry) { SR.RenderControl(entry); }
        public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { SR.RenderEntryRow(entry, isInternal, colWidth); }
        public static ConfigEntryBase FindEntry(string section, string key) { return SR.FindInternalEntry(section, key); }
        public static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) { return SR.AllEntries(config); }
        // 某 section 的可见条目（功能自绘页拼装通用条目行时用；已应用功能注册的行可见性）
        public static List<ConfigEntryBase> SectionEntries(string section) { return SR.SectionEntries(section); }
        // 条目行（功能自绘页用）与名称列宽
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
        // **全局泄漏修复**：把"原始字体"暴露给窗口恢复用（原字体由 SR.Styles 在第一次改皮肤前记录）
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
        //表格列偏好（列顺序 + 隐藏列）的统一持久化组件：三处表格共用，格式 "o=0,1,2;h=1,3"
        public class TablePrefs {
            private readonly BepInEx.Configuration.ConfigEntry<string> _entry;
            private readonly int _count;
            private long _sig; //上次落盘的签名（用于 SaveIfDirty，避免每帧写盘；R411 起为 64 位哈希）
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
                    //旧格式（老版本只存纯列顺序，如 "0,1,2,3,4,5"，没有 o=/h= 前缀）：当成列顺序读，避免升级后自定义列序被丢
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
                    // **R411**：配置里读到"全部隐藏"时一次性修掉（至少留一列）。
                    if (Hidden.Count >= _count) {
                        List<int> __hs = new List<int>(Hidden);
                        Hidden.Clear();
                        for (int __k = 1; __k < __hs.Count; __k++) Hidden.Add(__hs[__k]);
                    }
                } catch (Exception __ex) { Guard.Log("表格列偏好读取", __ex); }
            }

            //把配置里存的列宽套到调用方的列宽数组上（页面先把默认值填好，再调这里覆盖）
            // 只套用**一次**：页面每帧渲染（IMGUI 一帧 Layout+Repaint 至少两趟），若每帧都套用，
            //  用户拖动改过的列宽会在下一趟被 _savedW（落盘前还是旧值）覆盖回去 -> 列宽拖动永远存不下来。
            //  （ResetAll 走 _defaults 直接写回，不依赖这里；想重新套用可置 Rebind() 后的新实例。）
            public void BindWidths(float[] colW) {
                if (colW == null) return;
                //首次绑定前页面刚填好默认值 -> 留一份作"还原全部"的基准
                if (_defaults == null || _defaults.Length != colW.Length) _defaults = (float[])colW.Clone();
                _widths = colW;
                if (_applied || _savedW == null) return;
                for (int i = 0; i < _savedW.Length && i < colW.Length; i++) if (_savedW[i] > 0f) colW[i] = _savedW[i];
                _applied = true;
            }

            // **R411**：64 位 FNV 折叠（零分配）。原来每帧建 StringBuilder + ToString 只为和上一帧比一比
            //  （EX 表在 Repaint 每帧调一次 SaveIfDirty）。哈希碰撞的后果只是"这一帧没落盘"，可接受。
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
                    //列宽（像素，整数；页面每次都会用默认值初始化，所以整份写出即可）
                    if (_widths != null && _widths.Length > 0) {
                        sb.Append(";w=");
                        for (int i = 0; i < _widths.Length; i++) {
                            if (i > 0) sb.Append(',');
                            sb.Append(i).Append(':').Append(Mathf.RoundToInt(Mathf.Max(0f, _widths[i])));
                        }
                    }
                    _entry.Value = sb.ToString();
                    // **回写 _savedW**：页面每帧先用默认值填好列宽数组，再靠 BindWidths 用 _savedW 覆盖。
                    //  这里不同步的话，下一帧列宽会被"上一次读到的旧值"覆盖回去（表现为拖完一松手就弹回），
                    //  且 Sig() 随之变回旧值 -> 下一帧又判定"脏了"再写一次 -> 每帧写盘、值在两数间反复横跳。
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

            //每帧调用：顺序/隐藏列被用户改动（如右键隐藏）时才落盘
            // **R411**："至少留一列"的兜底从这里挪到 Load()（配置读到全隐藏时一次性修）。
            //  原来每帧这里都要 new 一份 List 只为删掉一个元素，而真正挡住"全隐藏"的是
            //  TableDraw 加隐藏列前的判据，两处并存语义还不一样强 -> 只留一处。
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
        //界面是否英文系= 内置英文 **或** 外部语言包声明回退英文（外部模块页强制中文时仍为 false）。
        //不能写 _langEn：装了语言包时它恒为 false，但 FallbackId="English" 的语言包下未收录词条
        //应回退英文, 与 T() / ZhKey / EnumNameZh 等出口保持一致（统一走 UseEn）。
        public static bool LangEn { get { return UseEn; } }
        // 外部模块页豁免：渲染期间置 true（该页始终中文）
        public static bool ForceZh { get { return _forceZh; } set { _forceZh = value; } }
        // **R416**：滑块提交改为「值变了就写」（ImGui 的 v_last 模式）。
        // 外部模块用滑块时：拿到 DrawSlider 的返回值，变了就调本方法（或直接 SetValue）。
        // 旧的 SliderCommitWasMine / SliderCommitted 跨帧布尔接口已删除（R413-R415 三轮都因它失效）。
        public static Dictionary<ConfigEntryBase, bool> EditOpen { get { return _editOpen; } }
        public static Dictionary<ConfigEntryBase, string> EditText { get { return _editText; } }
        //配置落盘节流 + 条目缓存失效：功能（含将来独立编译的 DLL）通过门面调用，不必用 SR 的 internal 成员
        public static void MarkConfigDirty(ConfigFile cfg) { SR.MarkConfigDirty(cfg); }
        public static void NoteEntryBound() { SR.NoteEntryBound(); }

    }

// 分区：Controls（管理器通用控件：按键录制/右键绑键/开关按钮 + 首页）
// 功能页已下放到各自模块文件，这里只留全页面共用控件；功能页通过 SR.Ctl（SR.Ctl.cs）调用。

        //按钮矩形登记（右键绑键用）记录"当前 GUI 组（滚动视图内容）内坐标"；命中检测须放在同一组内（见 SR.Window），坐标系一致、无需转换。

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
            //不吞事件（旧代码这里是 ev.Use()）：在 Layout/Repaint 期间吞掉 MouseDown 会让 IMGUI 把该事件
            //当作已处理，界面会闪一下；_rmbConsumedFrame 已能保证"一次右键只绑一个控件"。
        }

        //自身状态开关按钮：左键切换 on/off，按钮显示当前绑定键；右键绑键用绘制后的 GetLastRect 取矩形
        //（鼠标事件期间 GetRect 取到的矩形不可靠，会让个别按钮绑不上）；width=0 用默认宽。
        private static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            //键后缀统一走 SR.KeySuffix：未绑定 ""、已绑定 " [Shift+K]"、录制中紧凑的 " [...]" / " [1]"
            //（旧写法拼 RecText 会显示"请按键..."而把窄按钮撑成两行）
            string text = label + (on ? T("开", "ON") : T("关", "OFF")) + SR.KeySuffix(keyEntry);
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30)));   //EX 控件统一 30px（与编辑框/下拉框一致）
            //GUI.Button 对右键也会返回 true -> 必须按"触发按键"过滤：右键绝不触发左键动作
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
            //同 SelfToggleButton：键后缀统一走 SR.KeySuffix（录制中为紧凑的 [...] / [1]）
            string text = label + SR.KeySuffix(keyEntry);
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30)));   //EX 控件统一 30px（同上）
            if (clicked && evBtn != 0) clicked = false;
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发操作
        }

        //右键处理：未捕捉 -> 进入捕捉（等待按键设为快捷键）；捕捉中再右键 -> 删除快捷键。
        //（删除也可在捕捉中直接按 Esc，由 SR 的按键捕捉机制处理。）
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
            //录制中 KeySuffix 已返回 " [...]" / " [1]"，故不用再拼 RecText（旧写法会变成 "[1]1"）
            bool clicked = GUILayout.Button(new GUIContent(label + SR.KeySuffix(key), tip),
                capturing ? _capture : _btn, GUILayout.Width(bw), GUILayout.Height(bh));
            if (clicked && evBtn != 0) clicked = false; //右键绝不触发左键动作
            if (key != null && e != null) TryBindByRightClick(key, GUILayoutUtility.GetLastRect());
            return clicked && !capturing;
        }

        private static void RenderHomePage() {
            GUILayout.Label(T("- 欢迎使用 SR＿UCH -", "- Welcome to SR＿UCH -"), _secHeader);
            //地基可见性：自检发现按名绑定的目标失效时，在最上方显著提示。
            //详细清单在 设置页 -> 自检块。平时不占位置。
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
            // **G9 修复**：原文案写侧栏：长按右键栏目并上下拖动 = 调整栏目顺序，但该功能早已移除
            //（SR.Settings.cs 那个 HandleSidebarOrderDrag 当时就是段无调用点的死代码，_ordPrev 也从未被写入，后来已删）->
            //  用户照做完全没有反应。改为指向真实的入口（设置页自定义栏目拖动列头）。
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

        //紧凑版换行标签：高度用**实测值**（不裁切），靠"负间距"把条目贴紧, 既不会显示不全，也不留空行。
        //用哪个成组的多条提示 -> Tight；单独段落 -> WrapLabel。
        private static void WrapLabelTight(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
            GUILayout.Space(-Sc(5));   // **贴紧**：抵消样式自带的行距（不会裁切文字）
        }

        //悬浮提示框：最后绘制以保证在最上层。
        //不再按固定 380px 强行折行（中文/英文混排会被切得很难看）：
        //先按最宽的一行决定宽度（只在 '\n' 处换行），只有整行真的超过屏幕 80% 时才回退到自动折行。
        // **R411**：测量结果按 tip 原文缓存。同一次悬浮里 tip 几乎不变，而原来每帧都要
        //  Split（数组 + N 个子串）+ N 次 CalcSize + CalcHeight，一悬浮就是一帧一堆分配。
        //  （窗口尺寸变化时 limit 会旧一档，tip 一换就重算，可忽略。）
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
// SR.Settings 渲染条目行时不再硬编码任何 section/key，功能在自己的 Initialize/SelfReg 里注册：
// 行可见性、同伴条目（同一行右侧再画一个控件）、行专用滑块范围、下拉框宽度、枚举选项过滤。
// RowFilters 是**布局过滤**：只决定条目是否单独成行 / 是否在设置页重复渲染，
//   **不是权限门禁** —— 不做灰显、不写拒绝原因（现有 5 个过滤器全是布局用途）。
//   想做"没权限就灰显 + tooltip 说明原因"，得另起一套机制，别往这里塞。

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    public delegate float RowComboWidthFn(ConfigEntryBase entry);
    public delegate bool RowEnumFilterFn(ConfigEntryBase entry, string enumName, int value);
    public delegate void PageRenderFn();

    public static readonly List<RowFilterFn> RowFilters = new List<RowFilterFn>();
    public static readonly List<RowCompanionFn> RowCompanions = new List<RowCompanionFn>();
    public static readonly List<RowCompanionFn> RowExtras = new List<RowCompanionFn>(); //本行右侧"再渲染一个控件"的条目（如 缩放+键位、文字大小+开关）
    public static readonly List<RowSliderFn> RowSliders = new List<RowSliderFn>();
    public static readonly List<RowComboWidthFn> RowComboWidths = new List<RowComboWidthFn>();
    public static readonly List<RowEnumFilterFn> RowEnumFilters = new List<RowEnumFilterFn>();
    // **R412**：页面块扩展点：功能可把自己的 UI 块插进**别人的页面**的指定锚点
    //（例：EX 把派对盒块插进关卡页"派对盒炸弹"上面）。锚点名由宿主页面定义。
    public static readonly Dictionary<string, List<Action>> PageBlocks = new Dictionary<string, List<Action>>();

    /// <summary>把一个 UI 块挂到某栏目页面上的某个锚点（宿主页面在对应位置调 RenderPageBlocks）。</summary>
    public static void RegisterPageBlock(string section, string anchor, Action draw) {
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(anchor) || draw == null) return;
        string k = section + "|" + anchor;
        List<Action> list;
        if (!PageBlocks.TryGetValue(k, out list)) { list = new List<Action>(); PageBlocks[k] = list; }
        if (!list.Contains(draw)) list.Add(draw);
    }

    internal static void RenderPageBlocks(string section, string anchor) {
        List<Action> list;
        if (!PageBlocks.TryGetValue(section + "|" + anchor, out list)) return;
        for (int i = 0; i < list.Count; i++) {
            try { list[i](); } catch (Exception __ex) { Guard.Log("页面块(" + section + "/" + anchor + ")", __ex); }
        }
    }

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

    // 下拉框宽度（0 = 用默认宽度）
    public static float RowComboWidth(ConfigEntryBase entry) {
        for (int i = 0; i < RowComboWidths.Count; i++) {
            try { float w = RowComboWidths[i](entry); if (w > 0f) return w; } catch (Exception __ex) { Guard.Log("下拉框宽度", __ex); }
        }
        return 0f;
    }

    // 枚举选项是否保留（任一过滤器返回 false 即从下拉框里过滤掉）
    public static bool RowEnumAllowed(ConfigEntryBase entry, string enumName, int value) {
        for (int i = 0; i < RowEnumFilters.Count; i++) {
            try { if (!RowEnumFilters[i](entry, enumName, value)) return false; } catch (Exception __ex) { Guard.Log("枚举过滤", __ex); }
        }
        return true;
    }

    // 功能自绘页：功能用通用条目行拼自己的页面（如 建造增强 的建造上限小分区）
    // 注册后 SR.Window 优先调用它，不再走"纯通用条目列表"兜底分支。
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

    // 多页用法（核心化：功能只需注册，不要自己画页签）,
    //   1) 单页：SR.RegisterPage("段名", Render);
    //   2) 多页：第一页照旧用单页注册（或也用下面的 6 参形式），其余页用
    //        SR.RegisterPage("段名", "页id", "中文名", "English", 顺序, RenderXxx);
    // 注册 ≥2 页后，核心自动在页面顶部画页签（RenderPageTabs）、记录选中页（_pageSel）、
    //         并在 SR.Window 分派时只渲染当前选中页（RenderPage）。
    //   3) 某页要自己的顶栏/底栏：SR.RegisterPageChrome("段名", 顶栏, 底栏)，配合 SR.Ctl.SelectedPageId("段名") 判断。
    //   参考实现：EX 三页（目标/其它/高级）、实验两页（实验区一 / 实验区二）。
    // **要点**：多页注册会清掉该段的单页渲染器（见本方法末尾 PageRenderers.Remove），所以不要混用。
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

    // **#5 Ctrl+滚轮切页**：在当前栏目的页列表里前后移动一位（循环，顺序 = 注册时的 order）
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
        // **#5 竖向排列**：格子沿 Y 叠放（每项一行）。默认 false -> 对现有横向表格零影响。
        public bool vertical;
        public float[] defaultColW;   // **#1 默认列宽（Shift+右键时核心会还原成它）**
        public System.Func<int, bool> rowSelected;   // **某行是否"选中"（核心据此画整行高亮）**
        public bool headerLeft;                      // **#2 表头文字靠左（默认居中）**
        // **#1 默认列宽**：Shift+右键（resetAll）时核心会把 colW 还原成这一份，再调 resetAll
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
        // **把"钉住列按钮"与"空列表提示"也纳入本控件**：三处列表只提供内容，不再各自实现
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
            // 由 order + hidden **自行推导 vis**：TableColMove 换位后只调 applyOrder 更新 order，
            //  而画表头用的是 vis, 原来"谁更新 vis"全靠调用方自觉（联机页写了 _visCache = null 才正常，
            //  其它表都没写）-> 表现就是"拖了完全没变化"。这里由核心统一推导，调用方不必再维护。
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
            if (a.vertical) headerH = (a.headerH > 0f ? a.headerH : Sc(22)) * Mathf.Max(1, a.vis != null ? a.vis.Count : 0);   // **#5**
            float rowH = a.rowH > 0f ? a.rowH : Sc(26);
            // order 是换列算法的必需输入，但它在文档里是"可选的显示顺序" -> 调用方很容易漏。
            //  这里兜底成恒等序：即使调用方没赋 order，拖动换列也能工作（不再是静默失效）。
            if (a.drawHeader && (a.order == null || a.order.Length != a.colW.Length)) {
                // **#2 缓存复用**：原来每帧 new（房间列表列多、刷新频繁时会白白产生垃圾）
                if (_idOrder == null || _idOrder.Length != a.colW.Length) {
                    _idOrder = new int[a.colW.Length];
                    for (int i = 0; i < _idOrder.Length; i++) _idOrder[i] = i;
                }
                a.order = _idOrder;
            }
            if (a.drawHeader) {
                DrawTableHeader(a.vis, a.pinCol, a.colW, a.scrollX, a.box, a.stripW, headerH, a.mp, a.title, a.moveCol,
                    a.pinW, a.pinX, a.pinTitle, a.headerCells, a.headerCols, a.vertical, a.scrollY, a.headerLeft, out a.pinHeader);
                //表头下的横线：只在核心自己画表头时画（调用方自己画表头的，如联机页，会自己画一条，
                //否则会出现两条宽度不同的线叠在一起, #3）
                DrawLine(new Rect(a.box.x, a.box.y + headerH - 1f, a.stripW, 1f), CurStyle.LineStrong);
            }
            // 这里**不能**再无条件画一条：drawHeader=true 时上面刚画过（同一位置叠两次会更深），
            //  drawHeader=false 时调用方（联机页）自己画, 两种情况都只需一条。
            TableRowRects(a.rowCount, rowH, a.scrollY, a.rowsH, a.box.y, headerH, a.rowsClip, a.box.x, a.stripW, a.rowRects, a.rowRows);
            // **行选中高亮**：核心原来没有"选中行"的概念（联机页靠行透明度区分），
            //  XML 列表这类"点一行选中一行"的表格因此完全没有选中表现。这里统一支持。
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
            if (a.headerCells.Count > 0 && !a.vertical) {   // **#5 纵向不做列宽/行高拖动**
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
                    //排序方向指示（调用方给了 sortCol 才画）： 升序 /  降序
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
                    // **#2 列序规范化**：Order 长度与该表列数不一致时（例如 EX 误用/串用了房间列表的 11 列列序），
                    //    按本表列数重建, 否则拖动虽然生效，但视觉上"看不出变化"（只映射了前 N 列的排列）。
                    //    重建后的第一次 applyOrder 落盘即把持久化数据修正为本表列数。
                    if (a.order != null && a.colW != null && a.colW.Length > 0 && a.order.Length != a.colW.Length) {
                        int[] __ord = new int[a.colW.Length];
                        for (int i = 0; i < __ord.Length; i++) __ord[i] = i;
                        a.order = __ord;
                    }
                    // **#2 拖动进行中**：只把"当初按下的那一列"交给 TableColMove，
                    //  这样松开时 moveCol == ci 必然成立（否则 headerCols 映射一旦在拖动期间漂移，
                    //  松开事件就会落到别的 ci 上 -> moveCol != ci -> 换位静默失效，正是 EX 暴露的问题）。
                    int __dragK = -1;
                    if (a.moveCol >= 0) {
                        for (int k = 0; k < a.headerCells.Count; k++) {
                            int __c2 = (a.headerCols.Count > k) ? a.headerCols[k] : k;
                            if (__c2 == a.moveCol) { __dragK = k; break; }
                        }
                        if (__dragK < 0 && a.moveCol < a.headerCells.Count) __dragK = a.moveCol;   //兜底：按下时记录的就是本表列下标
                    }
                    // 落点列列表必须用 headerCols（显示位 -> 数据列），不能用 allCols（自然序）：
                    //  有列被隐藏、或钉住列不在最后时 headerCols[k] != k，用 allCols 会"移动错列"。
                    //  （EX 页早就这么做并留了注释，核心这里漏了, 现在统一。）
                    List<int> __cols = (a.headerCols != null && a.headerCols.Count > 0) ? a.headerCols : a.allCols;
                    for (int k = 0; k < a.headerCells.Count; k++) {
                        if (__dragK >= 0 && k != __dragK) continue;   // **#2 拖动中**：只处理拖动的那一列
                        int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
                        TableColMove(Event.current, a.headerCells[k], ci, k, __cols, a.mp, inBox, a.headerCells, a.order,
                            a.applyOrder, a.saveOrder, a.click, ref a.moveCol, ref a.moveX0, ref a.moveDx,
                            ref a.scrollColsTmp, ref a.scrollColsAtDragTmp, a.vertical);
                    }
                    // **#2 兜底**：逐格循环没命中也不能让换位丢失, 只要仍处于拖动状态且这一帧是 MouseUp，就地完成换位。
                    //  （命中时循环内已把 moveCol 清成 -1，故不会重复处理。）
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
            // #4 列头拖拽动画（仅 Repaint）：跟手浮动块 + 目标位置插入指示线。
            //  纯绘制，不改数据；GUI.color 用 try/finally 复位（防泄漏污染其它 UI）。
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
                    // **用 rowsClip 的坐标**：原来写死 x=0（且 rc 取了没用），空提示会跑到屏幕最左边而不是表格里
                    SR.Ctl.LabelClipCenter.Draw(new Rect(rc.x, rc.y + Mathf.Max(0f, (a.rowsH - rowH * 2f) * 0.5f), Mathf.Max(Sc(60), a.stripW), rowH * 2f),
                        new GUIContent(eh), false, false, false, false);
                }
            }
            Color clipSavedColor = GUI.color; // 异常时也要还原（GUI.color 泄漏会污染游戏界面）
            GUI.BeginClip(new Rect(a.box.x, a.box.y + headerH, a.stripW, a.rowsH));
            try {
            // **R411**：行悬停用的"裁剪区内坐标"（原点 = 裁剪框左上；mp 是进裁剪前抓的屏幕坐标）
            Vector2 __lm = new Vector2(a.mp.x - a.box.x, a.mp.y - a.box.y - headerH);
            for (int i = 0; i < a.rowCount; i++) {
                float y = i * rowH - a.scrollY;
                if (y > a.rowsH || y + rowH < 0f) continue;
                string[] rc = a.rowCells != null ? a.rowCells(i) : null;
                float alpha = a.rowAlpha != null ? a.rowAlpha(i) : 1f;
                // 隔行底（ImGui 的 TableRowBgAlt）：按**真实行号** i 取奇偶，而不是"可见序号",
                //  行号不随滚动变化，条纹才稳定（用可见序号会一滚就抖）。行号只有这里知道，
                //  所以接线必须在核心，不能丢给各功能页。行透明时（如不可加入的暗行）底也跟着淡。
                if ((i & 1) == 1) {
                    Color ab = CurStyle.RowAltBg;
                    if (alpha < 1f) ab.a *= alpha;
                    DrawFill(new Rect(0f, y, a.stripW, rowH), ab);
                }
                // **R411**：行悬停反馈（ImGui 表格 hovered 行亮一档）。加在隔行底之上、文字之下。
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
            // #1 列宽也一并还原到默认值（原来只还原隐藏列 -> 列宽拉歪后 Shift+右键回不来）
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
            //（原+N恢复按钮已按需求删除：还原统一走 Shift+右键 = resetAll）
            // **列宽拖动的收尾**：TableColResize 只处理 MouseDown/MouseDrag，没人清 MouseUp。
            //  原来 dragCol 要等 1.2 秒兜底才复位，且期间列宽改了也**从不落盘**（联机页靠自己那段
            //  MouseUp 处理才正常，EX/方块表没有 -> 拖完松手就弹回）。这里补上：清状态 + 回调落盘。
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
        float __vh = vertical ? (headerH * Mathf.Max(1, vis.Count)) : headerH;   // **#5 纵向**：裁剪区是"总高"
        GUI.BeginClip(new Rect(box.x, box.y, stripW, vertical ? __vh : headerH));
        try {
        // 表头底（ImGui 的 TableHeaderBg）：原来表头**只有文字、没有底**，与行的区分全靠线。
        //  铺在最前（裁剪坐标系内，原点已是 box.x/box.y），颜色走 CurStyle -> 单表可覆盖。
        DrawFill(new Rect(0f, 0f, stripW, vertical ? __vh : headerH), CurStyle.HeaderBg);
        float x = -scrollX, y = -scrollY;   // **#5 纵向用 y 叠放**
        for (int k = 0; k < vis.Count; k++) {
            int ci = vis[k];
            if (ci == pinCol) continue;
            float w = vertical ? stripW : Sc(colW[ci]);
            float h = vertical ? Sc(colW[ci]) : headerH;
            cells.Add(new Rect(box.x + x, box.y + y, w, h));
            cellCols.Add(ci);
            if (x + w > -Sc(4) && x < stripW + Sc(4)) {   //只画看得见的
                Rect __cr = new Rect(vertical ? 0f : x, vertical ? y : 0f, vertical ? stripW : w, h);
                // **R411**：列头悬停反馈（ImGui 的 TableHeader 有 hovered 态）。
                //  裁剪区里的绘制坐标以裁剪框左上为原点，而 mp 是进裁剪前抓的屏幕坐标 -> 先减掉原点。
                Vector2 __lmh = new Vector2(mp.x - box.x, mp.y - box.y);
                if (__lmh.x >= 0f && __lmh.x < stripW && __lmh.y >= 0f && __lmh.y < (vertical ? __vh : headerH)
                    && __cr.Contains(__lmh) && moveCol != ci) DrawRect(__cr, 0.06f);
                if (moveCol == ci) DrawRect(__cr, 0.22f);
                (headerLeft ? SR.Ctl.LabelClip : SR.Ctl.SecHeaderCenter).Draw(__cr,
                    new GUIContent(title != null ? title(ci) : ""), false, false, false, false);
                //横向画竖分隔线； #5 纵向画横分隔线。表头/外框用**强线**（ImGui TableBorderStrong）。
                if (vertical) DrawLine(new Rect(0f, y + h - 1f, stripW, 1f), CurStyle.LineStrong);
                else DrawLine(new Rect(x + w - 1f, 0f, 1f, headerH), CurStyle.LineStrong);
            }
            if (vertical) y += h; else x += w;   // **#5**
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
            //钉住列表头：先铺统一表头底，再叠一层极淡 accent, 保留"这一列被钉住"的可辨识度
            //（原来只有 accent 0.10；加了表头底之后若不再叠，它会与普通表头长得一样）。
            DrawFill(new Rect(0f, 0f, pinW, headerH), CurStyle.HeaderBg);
            DrawRect(new Rect(0f, 0f, pinW, headerH), 0.10f);
            SR.Ctl.SecHeaderCenter.Draw(new Rect(0f, 0f, pinW, headerH),
                new GUIContent(pinTitle ?? ""), false, false, false, false);
            } finally { GUI.EndClip(); }
            DrawLine(new Rect(pinX - 1f, box.y, 1f, headerH), CurStyle.LineStrong);
        }
    }

    // 数组版（推荐）：TableDraw 走这一条, 原来每行都要 new 一个 Func 闭包来包 rc[ci]，
    //  一张 100 行的表 × IMGUI 每帧两趟 = 每帧 200 个委托 + 200 个闭包对象，纯 GC 垃圾。
    //  直接传数组后这里只是索引取值，零分配；越界的列按空串处理（不再抛异常打断整表绘制）。
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

    //复用同一个 GUIContent：单元格文本每帧数百次，原来每次都 new 一个（含内部字符串拷贝）。
    //GUIStyle.Draw 是同步绘制，不持有该引用，复用安全。
    private static readonly GUIContent _cellContent = new GUIContent();
    private static GUIContent CellContent(string s) {
        _cellContent.text = s != null ? s : "";
        return _cellContent;
    }

    //拖拽兜底：超过 1.2s 没鼠标事件就复位所有拖拽状态（只有真漏收 MouseUp 才触发）。
    //联机页的拖拽状态与落盘回调以 ref/回调注入，核心不持有功能数据。
    public static void TableDragWatchdog(Event e, ref float watchdog,
        ref bool sbDragging, ref int moveCol, ref float moveDx, ref int dragCol, ref bool dragH,
        System.Action saveWidths, System.Action saveTableH, System.Action resetJoinDown) {
        // #2 治本（第二刀）：**松开的那一帧不做超时复位**。
        //  第一刀只挡了"鼠标仍按住"，但 MouseUp 帧按键已抬起 -> 若距上次事件超过 1.2 秒，
        //  看门狗会抢在核心换位块之前把 moveCol 清成 -1 -> 松开判定失败（只有"按下"日志）。
        //  有 MouseUp 在飞行时跳过复位，让拖动处理先跑；下一帧再照常兜底。
        if (e != null && e.type == EventType.MouseUp) return;
        if (watchdog > 0f && Time.unscaledTime > watchdog) {
            // #2 治本：**鼠标键仍按住时绝不复位**拖动状态。
            //  原逻辑只按"1.2 秒没收到鼠标事件"就复位 moveCol/dragCol：玩家按下后停顿再松开时，
            //  拖动状态已被清掉 -> 松开时 moveCol != ci，列头换位静默失效（列宽拖动同理会受影响）。
            //  按住期间直接返回，等真正松开（不再有按键）后再由下一帧完成复位。
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

    //表格滚轮：Ctrl=横向；没有纵向余量时自动转横向。返回 true = 已消费（并会标记滚轮已用）
    public static bool TableWheelInput(Event e, Rect box, Vector2 mp, float maxX, float maxY,
        ref Vector2 scrollCols, ref Vector2 scrollRows) {
        if (e == null || e.type != EventType.ScrollWheel || !box.Contains(mp)) return false;
        float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
        // #1 Shift+滚轮 也可横向滚动（原来只认 Ctrl；同时保留"无纵向余量时自动转横向"）
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
        // **#5 轴感知**：横向时"分隔线在右、可拖区在左"；纵向时"分隔线在下、可拖区在上"
        Rect sep = vertical ? new Rect(cell.x, cell.yMax - Sc(3), cell.width, Sc(7)) : new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
        Rect body = vertical ? new Rect(cell.x, cell.y, cell.width, cell.height - Sc(4)) : new Rect(cell.x, cell.y, cell.width - Sc(4), cell.height);
        if (e.type == EventType.MouseDown && e.button == 0 && inBox && body.Contains(mp) && !sep.Contains(mp)) {
            moveCol = ci; moveX0 = vertical ? mp.y : mp.x; moveDx = 0f; scrollColsAtDrag = scrollCols;   // **#5 moveX0 存"本轴起点"**
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseDrag && moveCol == ci) {
            moveDx = (vertical ? mp.y : mp.x) - moveX0;   // **#5 离开列表也继续跟手**
            scrollCols = scrollColsAtDrag;
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseUp && moveCol == ci) {
            // 换列需要 order 才能算 from/to；order 为空时**必须**先把拖动状态清干净再返回，
            //  否则 moveCol 会一直挂着（原来这里直接 order.Length -> NRE，被 TableDraw 的
            //  try/catch 吞掉，表现为"拖了没反应且没有任何提示"）。
            if (order == null || order.Length == 0) { moveCol = -1; moveDx = 0f; BlockWindowDrag = false; e.Use(); return true; }
            bool dragged = Mathf.Abs(moveDx) > Sc(4);  //1~3px 手抖不算拖动
            int target = k;
            if (dragged && headerCells.Count > 0) {
                for (int j = 0; j < headerCells.Count; j++) {
                    if (vertical ? (mp.y < headerCells[j].yMax) : (mp.x < headerCells[j].xMax)) { target = j; break; }   // **#5**
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

    //表格通用绘制原语（从联机房间列表抽到核心，供所有表格共用）
    private static int[] _idOrder;   // #2 恒等序缓存（避免每帧分配；属 SR，不属 TableArgs）
    // **R411**：纯白填充直接用 Unity 内置 whiteTexture（全白、常驻、不用自己建/销毁）。
    //  原来自建 1x1：既没挂 DontDestroyOnLoad（换场景被回收后靠伪 null 重建）也不在 EnsureStyles
    //  的销毁清单里，与其它运行时贴图两套生命周期策略。DrawTexture 把整张贴图拉伸进目标矩形，
    //  全白纹理 1x1 还是 4x4 视觉完全一致 -> 直接替换，行为零差异。
    public static Texture2D Px() { return Texture2D.whiteTexture; }
    // 默认色保持不变（1,1,1,0.22）：本方法对外，功能页自绘的线也在用它，不能悄悄换色。
    //  表格内部要按 ImGui 的"强线/弱线"分开，所以另开一个带颜色的重载。
    public static void DrawLine(Rect r) { DrawLine(r, new Color(1f, 1f, 1f, 0.22f)); }

    public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }

    // 纯色填充（仅 Repaint 趟画）：表头底与隔行底用。
    //  alpha <= 0 直接跳过, 这样"关掉表头底/隔行底"只需把颜色 alpha 设为 0，不必改绘制代码。
    internal static void DrawFill(Rect r, Color c) {
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

    //表格外观配置：绘制一律读 _tableStyle（私有字段，本文件内的 CurStyle 也是直接读它）。
    //  **E4**：原注释写"改 SR.DefaultTableStyle 即可全局调"是**误导** —— _tableStyle 是 readonly
    //  且没有 setter，外部拿到的只是一个只读视图，改不了。要调配色就改下面 TableStyle 里的字段默认值。
    //  将来若要"按表不同外观"，给 TableArgs 加 style 字段、TableDraw 取它即可（getter 保留给扩展方读）。
    public sealed class TableStyle {
        // 表格配色（默认值逐字取自 Dear ImGui 的表格主题，见 _UI_PORT_IMGUI.md 一）：
        //  表头底 TableHeaderBg (0.19,0.19,0.20)；强线（表头/外框）TableBorderStrong (0.31,0.31,0.35)；
        //  弱线（格内）TableBorderLight (0.23,0.23,0.25)；隔行底 TableRowBgAlt（白 6%）。
        //  原来这些颜色是**硬编码在绘制里**的（表头根本没有底、两种线共用一个白色 0.22），
        //  所以没法调、也没法按表区分。现在统一走这里（全局一套；目前没有单表覆盖入口，
        //  需要的话给 TableArgs 加个 style 字段、TableDraw 取它即可）。
        //  想回到"无表头底 / 无隔行"的老观感：把 HeaderBg / RowAltBg 的 alpha 设为 0 即可。
        public Color HeaderBg = new Color(0.19f, 0.19f, 0.20f, 1f);
        public Color LineStrong = new Color(0.31f, 0.31f, 0.35f, 1f);
        public Color LineLight = new Color(0.23f, 0.23f, 0.25f, 1f);
        public Color RowAltBg = new Color(1f, 1f, 1f, 0.06f);
    }
    private static readonly TableStyle _tableStyle = new TableStyle();
    public static TableStyle DefaultTableStyle { get { return _tableStyle; } }
    private static TableStyle CurStyle { get { return _tableStyle; } }

    //页签行（多子页栏目）：要在页面外框（RenderPageHeader）之前调用，页签才会永远在最上面。
    //页签行左侧的"前缀"绘制点（每个栏目一个）。
    //用途：有些模块需要在**与页签同一行**的最左侧放一个自己的控件（如 EX 的总开关）,
    //核心不关心它画什么，只负责在最左侧留位，这样"开关 + 页签"是同一行而不是两行。
    private static readonly Dictionary<string, Action> _tabPrefix = new Dictionary<string, Action>();
    //控件高度（设计像素，0 = 默认 26）：页面渲染开始处设置、结束处复位

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

    // 功能页"外框"部件：滚动区之外的自绘内容（顶部工具行 / 底部输入行）
    // 会话内容页的这两块必须画在滚动区外（不随内容滚动），但布局位置属于管理器 ->
    // 由功能自己注册，管理器在对应栏目被选中时调用；核心不再写 section == "Chat" 这种硬编码判断。
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
