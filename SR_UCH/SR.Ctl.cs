using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Ctl（功能页能力门面 + 管理器通用控件 + 首页 + 行/页扩展点）====
// 本文件三部分：SR.Ctl（只转发不放逻辑，供功能页/外部模块调用）、通用控件与首页、行/页扩展点注册表。

// ---- 1) 能力门面：功能页渲染用的「通用控件 / 样式 / 窗口状态」----

    public static class Ctl {

        // UI 缩放系数（各页的像素尺寸都乘它）
        public static float Sc(float v) { return SR.Sc(v); }

        // ---- 通用条目控件 ----
        // 条目标签（可点击恢复默认值；tooltip = 说明 + 默认值 + 提示）
        public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h) { SR.RestoreLabel(content, entry, w, h); }
        public static void RenderControl(ConfigEntryBase entry) { SR.RenderControl(entry); }
        public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { SR.RenderEntryRow(entry, isInternal, colWidth); }
        public static ConfigEntryBase FindEntry(string section, string key) { return SR.FindInternalEntry(section, key); }
        public static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) { return SR.AllEntries(config); }
        // 某 section 的可见条目（功能自绘页拼装通用条目行时用；已应用功能注册的行可见性）
        public static List<ConfigEntryBase> SectionEntries(string section) { return SR.SectionEntries(section); }
        // 条目行（功能自绘页用）与名称列宽
        public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) { SR.RenderEntryRow(entry, true, colWidth); }
        public static float EntryNameWidth() { return SR.EntryNameWidth(); }
        public static GUIStyle SecHeaderStyle { get { return _secHeader; } }
        public static float TextHeight(string text, float width) { return SR.TextHeight(text, width); }
        // 滑块（松左键提交；intStep = 整数步进）
        public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false) { return SR.DrawSlider(rect, value, min, max, intStep); }
        // 下拉框（自绘：列表紧跟按钮下方）；返回选中下标，-1 = 未选
        public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) { return SR.ComboBox(entry, current, vals, options, ref open, width); }
        public static void WrapLabel(string text) { SR.WrapLabel(text); }
        public static float SidebarWidth() { return SR.SidebarWidth(); }
        // 写配置值（会标记待保存）
        public static void SetValue(ConfigEntryBase entry, object value) { SR.SetValue(entry, value); }

        // ---- 按键按钮（含右键绑键 / 组合键录制）----
        public static string RecText() { return SR.RecText(); }
        public static string KeySuffix(ConfigEntry<KeyCode> e) { return SR.KeySuffix(e); }
        public static void RegisterRowHotkey(ConfigEntry<KeyCode> entry) { SR.RegisterRowHotkey(entry); }
        public static void TryBindByRightClick(ConfigEntry<KeyCode> entry, Rect rect) { SR.TryBindByRightClick(entry, rect); }
        public static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.SelfToggleButton(label, tooltip, on, keyEntry, width, bindable);
        }
        public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            return SR.HotkeyActionButton(label, tooltip, keyEntry, width, bindable);
        }
        // 悬浮提示框（在 OnGUI 末尾统一绘制；功能页无需关心）
        public static void DrawTooltip(Vector2 mp) { SR.DrawTooltip(mp); }

        // ---- 样式（深色主题，见 SR.Styles.cs）----
        public static GUIStyle Label { get { return _label; } }
        public static GUIStyle LabelWrap { get { return _labelWrap; } }
        public static GUIStyle NameLabel { get { return _nameLabel; } }
        public static GUIStyle ChatLabel { get { return _chatLabel; } }
        public static GUIStyle SecHeader { get { return _secHeader; } }
        public static GUIStyle Item { get { return _item; } }
        public static GUIStyle SelItem { get { return _selItem; } }
        public static GUIStyle Btn { get { return _btn; } }
        public static GUIStyle Frame { get { return _frame; } }
        public static GUIStyle Capture { get { return _capture; } }
        public static GUIStyle CheckOn { get { return _checkOn; } }
        public static GUIStyle CheckOff { get { return _checkOff; } }
        public static GUIStyle SearchBox { get { return _searchBox; } }
        public static GUIStyle Footer { get { return _footer; } }
        public static GUIStyle TitleMid { get { return _titleMid; } }
        public static GUIStyle Title { get { return _title; } }
        public static GUIStyle TitleLabel { get { return _titleLabel; } }
        public static GUIStyle Win { get { return _win; } }
        public static Texture2D CursorTex { get { return _cursorTex; } }

        //绘制页面之前准备 GUI：扫描外部插件 → 建样式 → 应用 UI 缩放与字体（地图页/其它自绘页共用）
        public static void PrepareGuiSkin() {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            _scaled = Mathf.Clamp(_uiScaleEntry != null ? _uiScaleEntry.Value : 1f, 1f, 1.8f);
            if (_font != null) GUI.skin.font = _font;
        }
        public static float ScaleFactor { get { return _scaled; } }
        //上一帧的字体（自绘页面收尾时恢复）
        public static Font PrevFont { get { return _prevFont; } }
        //是否正在录制按键（录制期间页面不抢输入）
        public static bool IsCapturing { get { return _capturing != null; } }

        public static float WinWidth { get { return _winWidth; } }
        public static Vector2 Scroll { get { return _scroll; } set { _scroll = value; } }
        //滚到底（新消息到达时用；不能写成 Ctl.Scroll.y = ... —— 属性返回的是副本）
        public static void ScrollToBottom() { _scroll.y = float.MaxValue; }
        public static ConfigFile InternalConfig { get { return _internalConfig; } }
        public static bool LangEn { get { return _langEn; } }
        // 外部模块页豁免：渲染期间置 true（该页始终中文）
        public static bool ForceZh { get { return _forceZh; } set { _forceZh = value; } }
        public static bool SliderCommitted { get { return _sliderCommitted; } }
        public static Dictionary<ConfigEntryBase, bool> EditOpen { get { return _editOpen; } }
        public static Dictionary<ConfigEntryBase, string> EditText { get { return _editText; } }
        public static void ClearEditState() { _editText.Clear(); _editOpen.Clear(); }

        // ---- 核心配置项（功能页需要读取；外部模块页的两个开关条目已归外部模块自己持有）----
        public static ConfigEntry<KeyCode> MapKeyEntry { get { return Freeplay.MapKeyEntry; } }
        public static ConfigEntry<bool> MapEnabledEntry { get { return Freeplay.MapEnabledEntry; } }
        public static ConfigEntry<bool> TreehouseMapEntry { get { return Freeplay.TreehouseMapEntry; } }
    }



// ==== 分区：Controls（管理器通用控件：按键录制/右键绑键/开关按钮 + 首页）====
// 功能页已下放到各自模块文件，这里只留全页面共用控件；功能页通过 SR.Ctl（SR.Ctl.cs）调用。

        //按钮矩形登记（右键绑键用）记录"当前 GUI 组（滚动视图内容）内坐标"；命中检测须放在同一组内（见 SR.Window），坐标系一致、无需转换。

        //录制中显示已录到的按键顺序（按钮/键位行共用）：如 "Shift → 9 → 0"
        private static string RecText() {
            if (_recSeq.Count == 0) return T("请按键…", "press keys…");
            string s = "";
            foreach (KeyCode k in _recSeq) s += (s.Length > 0 ? " → " : "") + KeyDisplayName(k);
            return s;
        }

        //快捷键后缀：未绑定时返回空串，绑定时返回 " [Shift + K]"（组合键也能显示）
        private static string KeySuffix(ConfigEntry<KeyCode> e) {
            if (e == null || e.Value == KeyCode.None) return "";
            return " [" + ComboKeyDisplay(e, e.Value) + "]";
        }

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
            try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 右键命中(当帧) " + entry.Definition.Key + " 鼠标=" + ev.mousePosition + " 矩形=" + rect); } catch { }
            ev.Use();
        }

        //自身状态开关按钮：左键切换 on/off，按钮显示当前绑定键；右键绑键用绘制后的 GetLastRect 取矩形
        //（鼠标事件期间 GetRect 取到的矩形不可靠，会让个别按钮绑不上）；width=0 用默认宽。
        private static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry, float width = 0f, bool bindable = false) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            string keyTxt = "";
            if (keyEntry != null && keyEntry.Value != KeyCode.None) keyTxt = " [" + ComboKeyDisplay(keyEntry, keyEntry.Value) + "]"; //含修饰键：Shift/Ctrl/Alt + 主键
            string text = label + (on ? T("开", "ON") : T("关", "OFF")) + keyTxt
                + (capturing ? " " + RecText() : "");
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30)));
            //GUI.Button 对右键也会返回 true → 必须按"触发按键"过滤：右键绝不触发左键动作
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
            string keyTxt = "";
            if (keyEntry != null && keyEntry.Value != KeyCode.None) keyTxt = " [" + ComboKeyDisplay(keyEntry, keyEntry.Value) + "]"; //含修饰键：Shift/Ctrl/Alt + 主键
            string text = label + keyTxt + (capturing ? " " + RecText() : "");
            float w = width > 0f ? width : Sc(150);
            Event e = Event.current;
            int evBtn = e != null ? e.button : 0;
            bool clicked = GUILayout.Button(new GUIContent(text, tooltip), capturing ? _capture : _btn,
                GUILayout.Width(w), GUILayout.Height(Sc(30)));
            if (clicked && evBtn != 0) clicked = false;
            if (bindable && keyEntry != null && e != null) {
                TryBindByRightClick(keyEntry, GUILayoutUtility.GetLastRect()); //当帧判定（不跨帧存矩形）
            }
            return clicked && !capturing; //左键（非捕捉）触发操作
        }

        //右键处理：未捕捉 → 进入捕捉（等待按键设为快捷键）；捕捉中再右键 → 删除快捷键。
        //（删除也可在捕捉中直接按 Esc，由 SR 的按键捕捉机制处理。）
        private static void HandleHotkeyRightClick(ConfigEntry<KeyCode> keyEntry, bool capturing) {
            if (keyEntry == null) return;
            if (capturing) {
                try { keyEntry.BoxedValue = KeyCode.None; } catch (Exception __ex) { Guard.Log("清空按键绑定", __ex); }
                _capturing = null;
                _dirty = true;
            } else {
                _prevBoxed = keyEntry.BoxedValue;
                _capturing = keyEntry;
                _captureStartFrame = Time.frameCount; //右键进入捕捉：本次 MouseDown 不能被"按下即取消"清掉
                _recSeq.Clear();
                _recHeld.Clear();
                _recLastAt = Time.unscaledTime;
                try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 右键命中按钮 → 进入捕捉: " + keyEntry.Definition.Key); } catch { }
            }
        }

        private static void RenderHomePage() {
            GUILayout.Label(T("— 欢迎使用 SR＿UCH —", "— Welcome to SR＿UCH —"), _secHeader);
            WrapLabel(T("Ultimate Chicken Horse 模组整合增强包。", "An enhancement pack for Ultimate Chicken Horse."));
            WrapLabel(T("本 mod 参考了 BetterFreeplay，BetterNight，BuildingPlus，BuildUnlimiter，UCH Freeplay Spawn Setter，UCH Tweaks，UCH-PlayerTracker-Mod，UltimateBuilder，向这些 mod 的制作者表示感谢。", "This mod references BetterFreeplay, BetterNight, BuildingPlus, BuildUnlimiter, UCH Freeplay Spawn Setter, UCH Tweaks, UCH-PlayerTracker-Mod and UltimateBuilder. Thanks to their authors."));
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("— 开源地址 —", "— Source —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("https://github.com/RSTFS/SR_UCH", _btn, GUILayout.Height(Sc(30)))) {
                try { Application.OpenURL("https://github.com/RSTFS/SR_UCH"); } catch (Exception __ex) { Guard.Log("打开开源地址", __ex); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            GUILayout.Space(Sc(8));
            GUILayout.Label(T("— 使用提示 —", "— Tips —"), _secHeader);
            WrapLabel(T("· 修改配置即时保存。", "· Config changes are saved immediately."));
            WrapLabel(T("· 自定义按键：点按键按钮后，直接按一个键设为单键；按住 Shift/Ctrl/Alt 再按主键设为组合键（如 Shift+P）。Esc 清空，Shift+Esc 取消。", "· Custom keys: click the key button, then press a key for single-key; hold Shift/Ctrl/Alt while pressing a key for a combo (e.g. Shift+P). Esc clears, Shift+Esc cancels."));
            WrapLabel(T("· 通用条目页：点击条目前的名称即可恢复默认值。", "· Generic entries: click the name to restore its default."));
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("— 请共同维护游戏体验 —", "— Keep the game fun for everyone —"), _secHeader);
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

        //悬浮提示框：最后绘制以保证在最上层。
        private static void DrawTooltip(Vector2 mp) {
            string tip = GUI.tooltip;
            if (tip == null || tip.Length == 0) return;
            GUIContent content = new GUIContent(tip);
            float maxW = Mathf.Min(380f, Screen.width * 0.5f);
            float h = _tooltip.CalcHeight(content, maxW);
            float w = Mathf.Min(_tooltip.CalcSize(content).x + 16f, maxW + 16f);
            float x = Mathf.Clamp(mp.x + 16, 2f, Screen.width - w - 6);
            float y = Mathf.Clamp(mp.y + 16, 2f, Screen.height - h - 10);
            GUI.Box(new Rect(x, y, w, h + 8), content, _tooltip);
        }



// ==== 分区：RowHooks（功能对「通用条目行」的扩展点）====
// SR.Settings 渲染通用条目行时不再硬编码任何 section/key；功能文件在自己的 Initialize/SelfReg 里注册：
//   · 行可见性（如 DestroyBlocks 的「追踪玩家」只在列表模式=普通时显示）
//   · 同伴条目（同一行右侧再渲染一个控件，如 建造增强 的「覆盖 + 快捷键」）
//   · 行专用滑块范围（如 视野 FOV 1-32、外部模块的时间流速 0-2）
//   · 下拉框宽度（如 外部模块页窄下拉框）
//   · 枚举选项过滤（如 关卡下拉框过滤 空白/随机/原型）
// ⚠ RowFilters 是**布局过滤**：只决定条目「是否单独成行 / 是否在设置页重复渲染」，
//   **不是权限门禁，不做灰显、不写拒绝原因**（当前的 5 个过滤器全是布局用途）。
//   需要"没权限就灰显 + tooltip 说明原因"时不要往这里加，那是另一套机制。

    public delegate bool RowFilterFn(ConfigEntryBase entry);
    public delegate string RowCompanionFn(ConfigEntryBase entry);
    public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
    public delegate float RowComboWidthFn(ConfigEntryBase entry);
    public delegate bool RowEnumFilterFn(ConfigEntryBase entry, string enumName, int value);
    public delegate void PageRenderFn();

    public static readonly List<RowFilterFn> RowFilters = new List<RowFilterFn>();
    public static readonly List<RowCompanionFn> RowCompanions = new List<RowCompanionFn>();
    public static readonly List<RowSliderFn> RowSliders = new List<RowSliderFn>();
    public static readonly List<RowComboWidthFn> RowComboWidths = new List<RowComboWidthFn>();
    public static readonly List<RowEnumFilterFn> RowEnumFilters = new List<RowEnumFilterFn>();

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

    // ---- 功能自绘页：功能用通用条目行拼自己的页面（如 建造增强 的「建造上限」小分区）----
    // 注册后 SR.Window 优先调用它，不再走"纯通用条目列表"兜底分支。
    public static readonly Dictionary<string, PageRenderFn> PageRenderers = new Dictionary<string, PageRenderFn>();

    public static void RegisterPage(string section, PageRenderFn render) {
        if (string.IsNullOrEmpty(section) || render == null) return;
        PageRenderers[section] = render;
    }

    public static bool HasPage(string section) {
        return !string.IsNullOrEmpty(section) && PageRenderers.ContainsKey(section);
    }

    public static void RenderPage(string section) {
        PageRenderFn f;
        if (!PageRenderers.TryGetValue(section, out f)) return;
        try { f(); } catch (Exception __ex) { Guard.Log("功能页渲染(" + section + ")", __ex); }
    }

	}
}

