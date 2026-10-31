// 主窗口框架：Tick 主循环、标题栏、DrawGUI 页面派发、窗口拖动缩放、侧栏。
// 每帧驱动入口是里面的 ManagerUI（MonoBehaviour，提供 Update / LateUpdate / OnGUI）。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Window（主窗口框架：Tick 主循环 / 标题栏 / DrawGUI / 窗口拖动缩放）

        // 诊断浮层（设置页开关控制）：按换行实测高度绘制，内容来自 SRApi.DiagLines。
        private static void DrawDiagOverlay() {
            try {
                if (SR.DiagEnabled && SRApi.DiagLines.Count > 0) {
                    float w = Sc(520f);
                    float __innerW = w - Sc(12f);
                    float __y = Sc(4f);
                    // 先按换行测量总高，避免长行被裁掉。
                    float __total = _label.CalcHeight(new GUIContent("SR DIAG"), __innerW) + Sc(4f);
                    for (int i = 0; i < SRApi.DiagLines.Count; i++)
                        __total += _labelWrap.CalcHeight(new GUIContent(SRApi.DiagLines[i]), __innerW);
                    Rect box = new Rect(Sc(8f), Sc(34f), w, Mathf.Min(__total + Sc(10f), Screen.height * 0.5f));
                    SR.DrawRounded(box, new Color(0f, 0f, 0f, 0.78f), SRApi.Ctl.ThemeRadius);
                    GUI.Label(new Rect(box.x + Sc(6f), box.y + Sc(2f), __innerW, _label.CalcHeight(new GUIContent("SR DIAG"), __innerW)), "SR DIAG", _label);
                    __y += _label.CalcHeight(new GUIContent("SR DIAG"), __innerW) + Sc(2f);
                    for (int i = 0; i < SRApi.DiagLines.Count; i++) {
                        float hh = _labelWrap.CalcHeight(new GUIContent(SRApi.DiagLines[i]), __innerW);
                        GUI.Label(new Rect(box.x + Sc(6f), box.y + Sc(2f) + __y, __innerW, hh), SRApi.DiagLines[i], _labelWrap);
                        __y += hh;
                    }
                }
            } catch (Exception __dg) { Guard.Log("诊断浮层", __dg); }
        }

        // 缩放把手 + 自绘光标（从 DrawGUI 提取，保持原有绘制顺序与状态）。
        private static void DrawResizeGripAndCursor(Rect winRect, Vector2 mouse, Rect gripRect) {
            // 整数对齐 + 尺寸与贴图一致（消除锯齿）；颜色按 ImGui ResizeGrip 三态（accent，0.25 / 0.7 / 0.95）。
            {
                int gs = Mathf.Max(8, Mathf.RoundToInt(Sc(21)));   // 与贴图一致
                float galpha = _resizing ? 0.95f : (CursorResize && gripRect.Contains(mouse) ? 0.7f : 0.25f);
                Color oldC = GUI.color;
                GUI.color = new Color(0.30f, 0.49f, 1f, galpha);   // ImGui 主题的 accent_color
                GUI.DrawTexture(new Rect(Mathf.Round(winRect.xMax - gs), Mathf.Round(winRect.yMax - gs), gs, gs), _gripTex);
                GUI.color = oldC;
            }
            // 自绘光标（游戏隐藏了系统光标）；悬停栏目分隔线时换成左右调整形状
            Texture2D curTex = CursorResize && _cursorResizeTex != null ? _cursorResizeTex
                : (CursorMove && _cursorMoveTex != null ? _cursorMoveTex : _cursorTex);
            if (curTex != null) GUI.DrawTexture(new Rect(mouse.x - Sc(7), mouse.y - Sc(7), Sc(15), Sc(15)), curTex);
        }


        private static void Tick() {
            // 诊断浮层宿主惰性创建：以前只在启动时建一次，若那次失败/时机不对就永远不显示。
            try { if (DiagEnabled) SR_UCH.Tweaks.SrDiagOverlay.Ensure(); } catch (Exception __dg) { Guard.Log("诊断浮层/确保宿主", __dg); }
            // 门控快照：每帧刷新一次（所有限制读同一份 -> 同帧一致）
            RefreshGate();
            FlushConfig(); // 配置节流落盘（最多延迟 1 秒）
            // 惰性 Bind 出新条目后补一次 Configuration Manager 排序（条目表没变时内部直接返回）
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("同步条目顺序", __ex); }
            // 功能每帧钩子（功能自己 SR.RegisterTick 注册）：逐个 try/catch，单个功能抛异常不中断整段 Tick。
            RunTickHooks();
            CursorResize = false; // 每帧复位；命中检测时重新置位
            CursorMove = false;
            // EventSystem 门控必须每帧跟随：只在打开面板时判定一次，之后 InputLocked 变 false 就不再恢复。
            ApplyEventSystemGate();
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);   // 打开/关闭约 0.12s 淡入淡出
            // 总开关关闭时请各功能收起自己的 UI；各功能自己判断各自的子开关。
            if (!GateMaster && AnyUiBlocked()) {
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("总开关关闭时收起功能 UI", __ex); }
            }
 
            // 自动保存：按住鼠标时不写，松手即保存（避免拖动中每帧写盘）
            if (_dirty && !Input.GetMouseButton(0) && !Input.GetMouseButton(1)) {
                _dirty = false;
                if (_dirtyConfig != null) {
                    var dc = _dirtyConfig;
                    _dirtyConfig = null;
                    // 配置写盘失败统一记日志
                    Guard.Try("配置保存", () => dc.Save());
                }
            }
        }

        // 录制规则：最多三个键、每键只记一次，必须按住上一个再按下一个（松开即完成录制）。
        private static void RecPushKey(KeyCode k) {
            if (k == KeyCode.None) return;
            if (_recSeq.Count >= 3) return;
            if (_recSeq.Contains(k)) return;
            _recSeq.Add(k);
            if (!_recHeld.Contains(k)) _recHeld.Add(k);
        }

        // 把已松开的键移出"仍按住"集合（清空即完成录制）
        private static void RecCheckRelease() {
            for (int i = _recHeld.Count - 1; i >= 0; i--) {
                bool held;
                try { held = Input.GetKey(_recHeld[i]); } catch { held = false; }
                if (!held) _recHeld.RemoveAt(i);
            }
        }

        // 最后一个键 = 主键；开头连续修饰键 = 组合修饰；其余 = 序列前键（同一条存储格式，会写盘）。
        private static void CommitRecording() {
            ConfigEntryBase cap = _capturing;
            if (cap == null) { _recSeq.Clear(); return; }
            if (_recSeq.Count == 0) { _capturing = null; _dirty = true; return; }
            KeyCode main = _recSeq[_recSeq.Count - 1];
            ComboMod mods = ComboMod.None;
            List<KeyCode> extras = new List<KeyCode>();
            for (int i = 0; i < _recSeq.Count - 1; i++) {
                KeyCode k = _recSeq[i];
                if (extras.Count == 0 && IsModifierKey(k)) {
                    if (k == KeyCode.LeftShift || k == KeyCode.RightShift) mods |= ComboMod.Shift;
                    else if (k == KeyCode.LeftControl || k == KeyCode.RightControl) mods |= ComboMod.Ctrl;
                    else mods |= ComboMod.Alt;
                } else {
                    extras.Add(k);
                }
            }
            try {
                if (cap.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                    // 外部模组的 KeyboardShortcut 只支持主键 + 修饰键，序列前键不适用
                    List<KeyCode> mk = new List<KeyCode>();
                    if ((mods & ComboMod.Shift) != 0) mk.Add(KeyCode.LeftShift);
                    if ((mods & ComboMod.Ctrl) != 0) mk.Add(KeyCode.LeftControl);
                    if ((mods & ComboMod.Alt) != 0) mk.Add(KeyCode.LeftAlt);
                    try { cap.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(main, mk.ToArray()); } catch (Exception __ex) { Guard.Log("写入组合键(KeyboardShortcut)", __ex); }
                } else {
                    SetValue(cap, main);
                    SetKeyComboMod(cap, mods);
                    SetKeySeqExtra(cap, extras);          // 序列前键合并进同一个隐藏条目
                    // 把组合串改写为按下顺序（显示端据此保序；解析按名字，不依赖顺序）
                    try {
                        System.Text.StringBuilder __sb = new System.Text.StringBuilder();
                        for (int __i = 0; __i < _recSeq.Count - 1; __i++) {
                            KeyCode __k = _recSeq[__i];
                            string __n = (__k == KeyCode.LeftShift || __k == KeyCode.RightShift) ? "Shift"
                                       : (__k == KeyCode.LeftControl || __k == KeyCode.RightControl) ? "Ctrl"
                                       : (__k == KeyCode.LeftAlt || __k == KeyCode.RightAlt) ? "Alt" : __k.ToString();
                            if (__sb.Length > 0) __sb.Append("+");
                            __sb.Append(__n);
                        }
                        SR.SetRawComboString(cap, __sb.ToString());
                    } catch (Exception __ex) { Guard.Log("组合键顺序写入", __ex); }
                }

            } finally {
                _capturing = null;
                _recSeq.Clear();
                _dirty = true;
            }
        }

        // 录键期间屏蔽游戏表情系统：中键 / 侧键同时被 EmoteSystem.ReceiveEvent 接收，绑键会顺带触发游戏 UI。
        // 独立嵌套类 + 单独注册：游戏改名时失败也不会带走 SR 的其它补丁。
        [HarmonyPatch(typeof(EmoteSystem), "ReceiveEvent")]
        private static class EmoteBlockPatch {
            [HarmonyPrefix]
            private static bool Prefix() { return _capturing == null; }
        }

        // 页面正在拖自己的控件时置 true：窗口拖动判定在页面绘制之前执行，页面的 Event.Use() 拦不住。
        public static bool BlockWindowDrag;

        // 右栏内容区（屏幕坐标），每帧刷新；自绘页用它铺满窗口
        public static Rect ContentArea;

        // 右栏底部那行（设置 / 模式）的高度：自绘页据此给表格留出底部空间，否则末行会被盖住。
        public static float FooterHeight;
        private static readonly Dictionary<string, float> _pageFooterH = new Dictionary<string, float>();
        private static float _pageUsedTop;   // 上一帧量到的页头/页签底部 Y（Layout 帧兜底用）   // 各栏目页面外框按钮高度（切换栏目不跳）
        private static int _footerClickFrame = -1;   // 底栏按钮同帧去重

        // 页面滚动位置：自绘页用它抵消控件变化导致的滚动区跳动
        public static Vector2 PageScroll { get { return _scroll; } set { _scroll = value; } }

        // 自绘页要求：本页内容自己适配窗口，不要页面级滚动条（滚动条被 PageScroll 拖住且不随缩放）。
        // 页面自己每帧末置 true，切换栏目时清掉。
        public static bool NoPageScrollBars;

        // 界面缩放：窗口随缩放比例同比放大 / 缩小一次，并把位置夹回屏幕内。
        private static float _lastScale;
        private static void ApplyScaleToWindow(float scale) {
            try {
                if (_lastScale <= 0f) {
                    _lastScale = scale;
                    // 首次也要把读盘得到的窗口位置 / 尺寸夹回屏幕内，否则分辨率变化后窗口可能点不回来
                    _winWidth = Mathf.Clamp(_winWidth, Ui.WinWMin, Ui.WinWMax);
                    _winHeight = Mathf.Clamp(_winHeight, Ui.WinHMin, Ui.WinHMax);
                    _winX = Mathf.Clamp(_winX, 0f, Mathf.Max(0f, Screen.width - _winWidth));
                    _winY = Mathf.Clamp(_winY, 0f, Mathf.Max(0f, Screen.height - _winHeight));
                    return;
                }
                if (Mathf.Abs(scale - _lastScale) < 0.001f) return;
                float r = scale / _lastScale;
                _lastScale = scale;
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_winWidth * r), Ui.WinWMin, Ui.WinWMax);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_winHeight * r), Ui.WinHMin, Ui.WinHMax);
                _winX = Mathf.Clamp(_winX, 0f, Mathf.Max(0f, Screen.width - _winWidth));
                _winY = Mathf.Clamp(_winY, 0f, Mathf.Max(0f, Screen.height - _winHeight));
                try {
                    // 写缓存条目
                    if (_winWidthEntry != null) _winWidthEntry.Value = _winWidth;
                    if (_winHeightEntry != null) _winHeightEntry.Value = _winHeight;
                    if (_winXEntry != null) _winXEntry.Value = Mathf.RoundToInt(_winX);
                    if (_winYEntry != null) _winYEntry.Value = Mathf.RoundToInt(_winY);
                } catch (Exception __ex) { Guard.Log("保存窗口几何(缩放调整)", __ex); }
                _dirty = true;
                _stylesReady = false;   // 字号变了：样式 / 字体重建
            } catch (Exception __ex) { Guard.Log("界面缩放调整窗口", __ex); }
        }

        // 标题栏绝对布局；右端 = 总开关 + 关闭
        private static float DrawTitleBar(float width) {
            float barH = Sc(26);
            Rect barRect = new Rect(0, 0, width, barH);
            GUI.Box(barRect, GUIContent.none, _title);
            float tY = (barH - Sc(26)) / 2f;
            GUI.Label(new Rect(Sc(8), tY, Sc(110), Sc(26)), "SR＿UCH", _titleLabel);
            // 关闭按钮左边：本 Mod 总开关（关闭时内部功能运行时失效，各功能开关值不变，会写盘）
            float mw = Sc(96);
            float mx = barRect.width - Sc(32) - Sc(4) - mw;
            // 提示标签宽度必须止于总开关左边，窗口不够宽时从左对齐方向裁掉，不被总开关压住。
            GUI.Label(new Rect(Sc(124), tY, Mathf.Max(Sc(40), mx - Sc(124) - Sc(6)), Sc(26)),
                TitleHint(), _titleMid);
            if (GUI.Button(new Rect(mx, tY, mw, Sc(26)),
                new GUIContent(AllEnabled ? T("总开关：开", "Master ON") : T("总开关：关", "Master OFF"),
                    T("本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变", "Mod master switch: off disables all internal features at runtime")),
                AllEnabled ? _selItem : _btn)) {
                AllEnabled = !AllEnabled;
                if (_allEnabledEntry != null) {
                    _allEnabledEntry.Value = AllEnabled;
                    Guard.Try("总开关自动保存", () => _allEnabledEntry.ConfigFile.Save()); // 失败记日志
                }
            }
            if (GUI.Button(new Rect(barRect.width - Sc(26), tY, Sc(26), Sc(26)), "X", _btn)) CloseMenu();
            return barH;
        }

        // 标题栏提示：开关键名动态取自当前绑定（以前硬编码 INS，改绑后提示是错的 —— audit F-6）。
        // 标题栏提示：开关键名动态取自当前绑定（以前硬编码 INS，改绑后提示是错的 —— audit F-6）。
        private static string TitleHint() {
            try {
                string key = _openKey != null ? KeyText(_openKey) : "INS";
                if (string.IsNullOrEmpty(key)) key = "INS";
                if (UseEn) return key + ": manager - hover entries for tooltips";
                return key + ": 开关界面 / 悬停条目看说明";
            } catch { return T("INS: 开关界面 / 悬停条目看说明", "INS: manager - hover entries for tooltips"); }
        }

        private static void DrawGUI() {
            if (!_visible) { try { ClearTip(); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
            if (!_visible && _uiAlpha <= 0.01f && !AnyUiBlocked()) {
                // 面板已关：把自绘页声明的系统光标形状收掉，否则 <->/+ 光标会留在屏幕上
                try { ClearTip(); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                RunFrameEndHooks();
                return;
            }
            _scaled = Mathf.Clamp(_uiScaleEntry.Value, Ui.ScaleMin, Ui.ScaleMax);
            if (_visible || _uiAlpha > 0.01f) {
            // 全局 GUI 状态（matrix / color / 字体 / 选择色）必须在**任何副作用之前**快照，
            // 且 try 要从这里就起——以前 try 起在设置 matrix 之后，中间任何异常都会把
            // GUI.matrix / skin.font 留在进程里，污染整个游戏的绘制。
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            try {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            // 布局缩放：所有固定尺寸乘 Sc()，字号随之
            float scale = Mathf.Clamp(_uiScaleEntry.Value, Ui.ScaleMin, Ui.ScaleMax);
            ApplyScaleToWindow(scale);
            _scaled = scale;
            _prevFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            Event e = Event.current;
            Vector2 mouse = e.mousePosition; // 无矩阵缩放，无需坐标换算

            Rect winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
            bool over = winRect.Contains(mouse);
            // 右下角自定义大小：热区略大于三角
            Rect gripRect = new Rect(winRect.xMax - Sc(32), winRect.yMax - Sc(32), Sc(32), Sc(32));
            // 外部模块页按钮右键绑键由按钮绘制当帧自判（跨帧登记矩形会因行数变化而位移，绑错键）。
            // 快捷键录制：单键 / 修饰键 + 主键 / 多普通键序列；整块只在非 Layout 事件里跑（Layout 阶段 Use() 会打断布局）。
            // 捕获自愈：录制期间若事件被别处消费（或行被切走），_capturing 会一直挂着并让所有热键失效。
            if (_capturing != null && Time.unscaledTime - SR.CaptureStartedAt > 12f) {
                Guard.Log("按键录制超时，已自动取消", null);
                SR.CancelCapture();
            }
            if (_capturing != null && e.type != EventType.Layout) {
                // IMGUI 的 MouseDown 对侧键 (3/4) 不一定触发，故用 Input.GetMouseButtonDown 独立检测
                bool sideHandled = false;
                int sideBtn = -1;
                try {
                    if (Input.GetMouseButtonDown(3)) sideBtn = 3;      // 侧键1 (Mouse4)
                    else if (Input.GetMouseButtonDown(4)) sideBtn = 4; // 侧键2 (Mouse5)
                } catch { sideBtn = -1; }
                if (sideBtn >= 0) {
                    RecPushKey(KeyCode.Mouse0 + sideBtn); // 鼠标键也进序列（Mouse3 / Mouse4）
                    if (e.type == EventType.MouseDown) e.Use(); // Repaint 阶段不能吞事件
                    sideHandled = true; // 同帧跳过下面的取消/结束判定（这里 return 会中断本帧绘制）
                }
                if (!sideHandled && e.type == EventType.KeyDown) {
                    if (e.keyCode == KeyCode.Escape) {
                        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) {
                            if (_prevBoxed != null) {
                                try { _capturing.BoxedValue = _prevBoxed; } catch (Exception __ex) { Guard.Log("恢复组合键原值", __ex); }
                            }
                        } else {
                            if (_capturing.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                                try { _capturing.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.None); } catch (Exception __ex) { Guard.Log("清空组合键", __ex); }
                            } else {
                                SetValue(_capturing, KeyCode.None);
                                SetKeyComboMod(_capturing, ComboMod.None);
                                SetKeySeqExtra(_capturing, null);
                            }
                        }
                        _capturing = null;
                        _recSeq.Clear();
                        _recHeld.Clear();
                        _dirty = true;
                        e.Use();
                    } else if (e.keyCode != KeyCode.None) {
                        RecPushKey(e.keyCode);
                        e.Use();
                    }
                } else if (!sideHandled && e.type == EventType.MouseDown) {
                    // 中键 / 侧键进序列；左键 / 右键不录（用于取消）。同样不能 return（会中断本帧绘制）
                    if (e.button >= 2 && e.button <= 6) {
                        RecPushKey(KeyCode.Mouse0 + e.button);
                        e.Use();
                        sideHandled = true;
                    } else if (Time.frameCount != _captureStartFrame) {
                        _capturing = null;
                        _recSeq.Clear();
                        _recHeld.Clear();
                    }
                }
                // 结束条件：录到的键全部松开即保存（所以必须是"按住链"）。
                if (_capturing != null && _recSeq.Count > 0) {
                    RecCheckRelease();
                    if (_recHeld.Count == 0) CommitRecording();
                }
            }
            if (e.type == EventType.MouseDown && gripRect.Contains(mouse) && e.button == 0) {
                _resizing = true;
                _resizeStart = mouse;
                _resizeStartSize = new Vector2(_winWidth, _winHeight);
                e.Use();
            } else if (e.type == EventType.MouseUp) {
                _resizing = false;
                _dragActive = false;
                _dragMoved = false;
            }
            if (_resizing && e.type == EventType.MouseDrag) {
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.x + (mouse.x - _resizeStart.x)), Ui.WinWMin, Ui.WinWMax);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.y + (mouse.y - _resizeStart.y)), Ui.WinHMin, Ui.WinHMax);
                winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                gripRect = new Rect(winRect.xMax - Sc(32), winRect.yMax - Sc(32), Sc(32), Sc(32));
                if (_winWidthEntry != null) _winWidthEntry.Value = _winWidth;    // 写缓存条目
                if (_winHeightEntry != null) _winHeightEntry.Value = _winHeight;
                _dirty = true;
                e.Use();
            }
            if (!_resizing) {
                float sbDivX = winRect.x + SidebarWidth();
                bool overSbDiv = Mathf.Abs(mouse.x - sbDivX) < Sc(5);
                if (overSbDiv) CursorResize = true; // 悬停在可拖拽的栏目分隔线上
                if (e.type == EventType.MouseDown && e.button == 0 && overSbDiv) {
                    _sidebarResizing = true;
                    e.Use();
                } else if (_sidebarResizing) {
                    if (e.type == EventType.MouseDrag) {
                        _sidebarW = mouse.x - winRect.x; // SidebarWidth 内部会 clamp
                        e.Use();
                    } else if (e.type == EventType.MouseUp) {
                        _sidebarResizing = false;
                        _dragActive = false;
                        _dirty = true;
                        // 拖动结束写回栏目宽度设置（0 = 自动时不动），会写盘
                        try { if (_sidebarWEntry != null && _sidebarW > 0f) _sidebarWEntry.Value = Mathf.RoundToInt(_sidebarW); } catch (Exception __ex) { Guard.Log("保存栏目宽度", __ex); }
                    }
                }
            }
            // 松手即解除"别拖窗口"：页面没机会清时也不会卡住窗口拖动
            if (e.type == EventType.MouseUp) BlockWindowDrag = false;
            if (!_resizing) {
                if (e.type == EventType.MouseDown && over && e.button == 0) _downPos = mouse;
                if (_dragActive && e.type == EventType.MouseDrag) {
                    if (!_dragMoved && (mouse - _downPos).magnitude > 4f) _dragMoved = true;
                    if (_dragMoved) {
                        _winX = mouse.x - _dragOffset.x;
                        _winY = mouse.y - _dragOffset.y;
                        winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                        if (_winXEntry != null) _winXEntry.Value = Mathf.RoundToInt(_winX);   // 写缓存条目
                        if (_winYEntry != null) _winYEntry.Value = Mathf.RoundToInt(_winY);
                        _dirty = true;
                    }
                }
                // 页面正在拖自己的东西时不要拖窗口（本函数先于页面绘制执行，页面来不及阻止）。
                // 必须限定左键：侧栏排序用长按右键拖动。
                if (e.type == EventType.MouseDrag && e.button == 0 && over && GUIUtility.hotControl == 0 && !_dragActive && !BlockWindowDrag) {
                    _dragActive = true;
                    _dragMoved = false;
                    _dragOffset = mouse - new Vector2(_winX, _winY);
                    _downPos = mouse;
                }
            }

            // 淡入淡出 + 轻微缩放入场（布局本身已由 Sc() 缩放）
            GUI.color = new Color(1f, 1f, 1f, _uiAlpha);
            float pop = 1f + (1f - _uiAlpha) * 0.05f;
            // 矩阵还原靠上面 try 之外抓的 prevMatrix；这里只负责设新值（顺序不能反）。
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(pop, pop, 1f));

            GUI.Box(winRect, GUIContent.none, _win);
            // 窗口区域内 rect 是区域局部坐标，鼠标事件是屏幕坐标：记下区域偏移供滚轮盾换算。
            SR.AreaOffset = new Vector2(winRect.x + Sc(4), winRect.y + Sc(4));
            GUILayout.BeginArea(new Rect(winRect.x + Sc(4), winRect.y + Sc(4), winRect.width - Sc(8), winRect.height - Sc(8)));
            float barH = DrawTitleBar(winRect.width - Sc(8));
            GUILayout.Space(barH + Sc(2));
            // 底栏是绝对定位的固定高条（区域坐标系）：内容行高度必须扣掉它，右栏/左栏都在它上方结束。
            float __footH = Sc(28f);
            float __areaInnerH = winRect.height - Sc(8);
            float __bodyH = Mathf.Max(Sc(80f), __areaInnerH - (barH + Sc(2)) - __footH - Sc(2f));
            GUILayout.BeginHorizontal();
            float sbw = SidebarWidth();
            // 右栏内容区（屏幕坐标）：自绘页用它把控件铺满窗口
            ContentArea = new Rect(winRect.x + Sc(4) + sbw + Sc(2), winRect.y + Sc(4),
                                   winRect.width - Sc(8) - sbw - Sc(2), winRect.height - Sc(8));
            GUILayout.BeginVertical(GUILayout.Width(sbw), GUILayout.Height(__bodyH));
            Vector2 leftBefore = _leftScroll;
            _leftScroll = GUILayout.BeginScrollView(_leftScroll, GUIStyle.none, GUIStyle.none);   // 侧栏滚动条已隐藏
            if (_leftScroll != leftBefore) WheelFrame = Time.frameCount; // 侧栏滚动区吃掉了滚轮
            for (int si = 0; si < _internalSections.Count; si++) {
                string s = _internalSections[si];
                bool sbSel = _mode == Mode.Internal && s == _selectedInternalSection;
                if (GUILayout.Button(ZhSection(s), sbSel ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.Internal;
                    _selectedInternalSection = s;
                    NoPageScrollBars = false;   // 换栏目：页面级滚动条要求复位（由自绘页每帧重新声明）
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.Space(Sc(6));
            // 外部插件与内部栏目之间的分隔线
            GUILayout.Box(GUIContent.none, _footer, GUILayout.Height(Sc(1)), GUILayout.ExpandWidth(true));
            GUILayout.Space(Sc(4));
            foreach (PluginEntry p in _externalPlugins) {
                // 被禁用的插件在侧栏标出来（*）
                string label = (IsExternalDisabled(p.guid) ? "* " : "") + p.name;
                if (GUILayout.Button(new GUIContent(label, p.guid + (IsExternalDisabled(p.guid) ? "\n" + T("已禁用（设置页可重新启用）", "disabled (re-enable in Settings)") : "")),
                        _mode == Mode.External && p.guid == _pluginKey ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.External;
                // 右键这一行即可绑定该插件的启用 / 禁用热键（组合键 / 序列都支持）
                    _pluginKey = p.guid;
                    NoPageScrollBars = false;
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.Space(Sc(2));
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.Height(__bodyH));
            if (_mode == Mode.Settings) {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(T("恢复默认", "Defaults"), _btn, GUILayout.Width(Sc(84)))) {
                    if (_internalConfig != null) {
                        foreach (ConfigEntryBase ce in AllEntries(_internalConfig)) {
                            Guard.Try("恢复默认值 " + ce.Definition.Key, () => ce.BoxedValue = ce.DefaultValue);
                        }
                        Guard.Try("写盘保存配置", () => _internalConfig.Save());
                        // 恢复默认的收尾：编辑框 / 展开态 / 枚举下拉缓存都要清，并置键位脏
                        // （否则下拉仍显示旧选项，带序列前键的快捷键也一直不触发）。
                        _editText.Clear();
                        _editOpen.Clear();
                        _enumOptions.Clear();
                        Guard.Try("重置键位监听", () => MarkKeyChanged());
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(T("恢复所有 SR＿UCH 配置为默认值", "Reset all SR＿UCH settings to defaults"), _label);
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(4));
            }
            float colWidth = EntryNameWidth();
            // 功能页外框部件（滚动区之外，功能自注册，仅在对应栏目被选中时调用）。
            // 页签必须画在页面外框（顶栏）之前，否则顶栏会压在上面。
            if (_mode == Mode.Internal) SR.RenderPageTabs(_selectedInternalSection);
            if (_mode == Mode.Internal) SR.RenderPageHeader(_selectedInternalSection);
            // 鼠标悬停在下拉框上滚轮 = 换选项：先于滚动区吞掉滚轮事件，免得页面跟着滚
            WheelShieldHit = false;   // 每帧先复位（仲裁命中时置 true）
            // 这两项必须在盾之前取值：盾命中时会把 e.type 改成 Used，之后再读恒为 false。
            Event __we = Event.current;
            bool __wasWheel = __we != null && __we.type == EventType.ScrollWheel;
            Vector2 __wheelMouse = __we != null ? __we.mousePosition : Vector2.zero;
            // 帧首：本帧声明 -> 上一帧声明（ImGui 的 HoveredIdPreviousFrameUsingMouseWheel）
            WheelClaimNewFrame();
            bool __overCtrlPrev = WheelClaimedPrev;
            if (__wasWheel && __overCtrlPrev) {
                __we.Use(); __we.type = EventType.Used; WheelShieldHit = true;
                // 记下本帧滚轮量：下拉框在 Repaint 趟取用一次后清掉（滚轮轴在该环境恒为 0，不能依赖它）
                WheelDeltaSet(-(Mathf.Abs(__we.delta.y) > 0.001f ? __we.delta.y : __we.delta.x));   // Unity 的 delta.y向下滚为正，取反以符合 ImGui 约定（向上为正）
                SR.MarkWheelUsed();
            }
            // Ctrl + 滚轮：只切当前栏目的页签（第一 / 第二 / …页），不调整侧栏。
            if (__wasWheel && !SR.WheelShieldHit
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) {
                // 取用即清：Ctrl + 滚轮语义是切页，拿走后下拉框本帧取不到滚轮量。
                float cw = WheelDeltaTake();
                if (Mathf.Abs(cw) < 0.0001f) { try { cw = Input.GetAxis("Mouse ScrollWheel"); } catch { cw = 0f; } }
                if (Mathf.Abs(cw) > 0.0001f && _mode == Mode.Internal) {
                    CyclePage(_selectedInternalSection, cw > 0f ? -1 : 1);   // 向上滚 = 前一页
                    if (__we != null) { __we.Use(); __we.type = EventType.Used; }
                    WheelShieldHit = true;   // 本帧页面不跟着滚
                    SR.MarkWheelUsed();
                }
            }

            Vector2 pageBefore = _scroll;
            // 真实页面视口 = 内容区高 - 已用页头高 - 底栏保留高：显式限制滚动区高度，
            // 否则滚动区会吃满剩余空间、底栏压住最后几行（预设/联机列表“最后一项显示不全”的根因）。
            float __areaH = __areaInnerH;
            // Layout 阶段 GetLastRect 可能还是 0（布局尚未计算完）：用上一帧量到的页头高度，
            // 否则 Layout 帧会把“视口”算成满高，滚动区把底栏顶出可视区（底栏只剩上边一点）。
            float __usedTop = _pageUsedTop;
            // 零高度探针：Layout 帧也能拿到"当前光标所在的准确 y"，不再依赖 GetLastRect 的上一帧值或兜底常量。
            try {
                Rect __probe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
                if (__probe.y > Sc(1f)) { __usedTop = __probe.y; _pageUsedTop = __probe.y; }
            } catch (Exception __ut) { Guard.Log("页面视口/页头高度", __ut); }
            // 底栏预留 = 底栏行高（上一帧，稳定） + 当前栏目页面外框按钮高（按栏目记住，切栏目不会跳）。
            float __pfReserve = 0f;
            // 底栏现在是固定条，不再需要“用其它栏目最大值兜底”；未知栏目按 0，否则表格会被凭空缩短（底部多出一截空白）。
            try { _pageFooterH.TryGetValue(_selectedInternalSection, out __pfReserve); } catch { __pfReserve = 0f; }
            // 底栏是固定高度条（与顶栏同款）：布局层只预留“条高 + 该栏目页面外框高 + 小余量”。
            float __barH = Sc(26f);
            float __footerReserve = __barH + Mathf.Max(0f, __pfReserve);
            // 视口高度：本帧先用上一帧实测值（首次用“区域高 - 底栏”兜底），
            // 本帧结束再按滚动区实测高度刷新；滚动区本身是弹性高度，页面高度变化不会反过来改变它。
            if (SRApi.PageViewportHeight <= Sc(60f))
                SRApi.PageViewportHeight = Mathf.Max(Sc(120f), __areaH - __footerReserve);
            // 页面内容必须以这条 y 为底界（= 滚动区顶 + 视口高 = 底栏条上沿）：列表高度按它算，随窗口大小自适应。
            SRApi.PageContentBottomY = __usedTop + SRApi.PageViewportHeight;
            SRApi.Diag("mode=" + _mode + " section=" + _selectedInternalSection);
            SRApi.Diag("areaH=" + __areaH.ToString("0.0") + " usedTop=" + __usedTop.ToString("0.0") + " barH=" + __barH.ToString("0.0"));
            SRApi.Diag("viewport=" + SRApi.PageViewportHeight.ToString("0.0") + " footerTop=" + (__areaH - __barH).ToString("0.0") + " scrollY=" + _scroll.y.ToString("0.0"));
            // 页面级滚轮：页面可以在滚动区之前抢走滚轮（自绘固定高度列表靠这个滚动）。
            if (__wasWheel) {
                try {
                    float __pw = -(Mathf.Abs(__we.delta.y) > 0.001f ? __we.delta.y : __we.delta.x);
                    if (SRApi.DispatchPageWheel(__wheelMouse, __pw)) {
                        __we.Use(); __we.type = EventType.Used; WheelShieldHit = true; SR.MarkWheelUsed();
                    }
                } catch (Exception __pwx) { Guard.Log("页面级滚轮", __pwx); }
            }
            // 鼠标悬浮在下拉框 / 滑块上时，本帧让滚动区当作没有可滚动内容（none 样式 + 盾改事件类型）。
            bool __noScroll = NoPageScrollBars || __overCtrlPrev || WheelShieldHit;
            // 设置页的页签画在滚动区**之外**：常驻顶部，不受滚动影响；
            // 「恢复默认」跟着页签排在最右侧（只对通用页有意义，自检页隐藏）。
            if (_mode == Mode.Settings) {
                try { SR.RenderSettingsTabBar(); } catch (Exception __tb) { Guard.Log("设置页签", __tb); }
            }
            _scroll = __noScroll
                ? GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none, GUILayout.ExpandHeight(true))
                : GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none, GUILayout.ExpandHeight(true));   // 弹性占满剩余高度
            if (_scroll != pageBefore) WheelFrame = Time.frameCount; // 页面滚动区吃掉了滚轮
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                // 功能自绘页：各功能在自己的 SelfReg 里 SR.RegisterPage(section, render) 注册，
                // 新增功能不必再改 SR.Window。
                if (SR.HasPage(_selectedInternalSection)) {
                    SR.RenderPage(_selectedInternalSection);
                } else {
                    // 兜底：纯通用条目列表（没有自绘页的栏目）
                    bool any = false;
                    foreach (ConfigEntryBase entry in InternalSectionEntries()) {
                        any = true;
                        RenderEntryRow(entry, true, colWidth);
                    }
                    if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
                }
            } else if (_mode == Mode.Settings) {
                RenderSettingsPage(colWidth);   // 通用 / 自检 两页（自检页含诊断浮层开关）
            } else {
                PluginEntry curExt = CurrentExternalPlugin();
                if (curExt != null) {
                    bool dis = IsExternalDisabled(curExt.guid);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(dis ? T("启用", "Enable") : T("禁用", "Disable"), _btn, GUILayout.Width(Sc(84)), GUILayout.Height(Sc(25)))) ToggleExternalPlugin(curExt);
                    GUILayout.Space(Sc(8));
                    GUILayout.Label(dis ? T("（已禁用，重启后仍禁用）", "(disabled, stays disabled after restart)") : T("（已启用，重启后保持启用）", "(enabled, stays enabled after restart)"), _label, GUILayout.Height(Sc(26)));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(Sc(4));
                }
                string lastSec = null;
                bool any = false;
                foreach (ConfigEntryBase entry in VisibleEntries(curConfig)) {
                    any = true;
                    string sec = entry.Definition.Section;
                    if (string.IsNullOrEmpty(sec)) sec = "(General)";
                    if (sec != lastSec) {
                        lastSec = sec;
                        GUILayout.Label("- " + sec + " -", _secHeader);
                    }
                    RenderEntryRow(entry, false, colWidth);
                }
                if (!any) GUILayout.Label(T("（这个插件没有可显示的配置项）", "(this plugin has no visible settings)"), _label);
            }
            GUILayout.EndScrollView();
            // 实测滚动区高度：它就是页面可用视口（下一帧给表格算高度用）。
            // 滚动区是弹性高度，不受页面内容影响，所以这个值不会形成抖动反馈。
            try {
                Rect __sv = GUILayoutUtility.GetLastRect();
                if (__sv.height > Sc(40f)) SRApi.PageViewportHeight = __sv.height;
            } catch (Exception __vm) { Guard.Log("页面视口实测", __vm); }
            // 滚轮被下拉框 / 滑块吃掉 -> 撤销本帧页面滚动。
            // 两道判定：1) 盾（绘制前、上一帧矩形命中）；2) 本帧绘制后的控件矩形命中。
            if (WheelShieldHit || (__wasWheel && __overCtrlPrev)) {
                _scroll = pageBefore;
                WheelFrame = Time.frameCount;   // 让用 Input 读轴的缩放路径本帧让步
            }
            // 功能页外框部件（紧贴底栏条上方）：量它的高度并按栏目记住，供下一帧预留。
            float __pfTop = 0f;
            try { __pfTop = GUILayoutUtility.GetLastRect().yMax; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (_mode == Mode.Internal) SR.RenderPageFooter(_selectedInternalSection);
            try {
                float __pfBottom = GUILayoutUtility.GetLastRect().yMax;
                float __pfH = Mathf.Max(0f, __pfBottom - __pfTop);
                if (_mode == Mode.Internal && !string.IsNullOrEmpty(_selectedInternalSection)) _pageFooterH[_selectedInternalSection] = __pfH;
            } catch (Exception __pm) { Guard.Log("页面外框高度测量", __pm); }

            // ---------- 底栏（绝对定位：内容行之后不参与 GUILayout，永不因内容溢出而漂移） ----------
            GUILayout.EndVertical();     // 右栏
            GUILayout.EndHorizontal();   // 左栏 + 右栏
            // 底栏矩形按区域高反推：区域坐标系里的最后 __footH 像素。GUILayout 溢出（内容过高）时底栏位置不变，
            // 之前用 GetRect 排版会把整条底栏顶到标题栏、并把右端模式标签挤出可视区。
            float __areaW = winRect.width - Sc(8);
            Rect __footRect = new Rect(Sc(2f), Mathf.Max(0f, __areaInnerH - __footH), Mathf.Max(Sc(60f), __areaW - Sc(4f)), __footH);
            SR.DrawRounded(__footRect, SRApi.Ctl.ThemeFooterBg, SRApi.Ctl.ThemeRadius);
            Rect __footIn = new Rect(__footRect.x + Sc(4f), __footRect.y + Sc(3f), Mathf.Max(Sc(40f), __footRect.width - Sc(8f)), Sc(22f));
            float __bx = __footIn.x;
            // 设置按钮：先算矩形（用于悬停过渡），再在矩形内画按钮。
            string __setLbl = T("设置", "Settings");
            float __setW = _btn.CalcSize(new GUIContent(__setLbl)).x + Sc(18f);
            Rect __setRect = new Rect(__bx, __footIn.y, __setW, Sc(22f));
            bool __setHover = __setRect.Contains(Event.current != null ? Event.current.mousePosition : Vector2.zero);
            Color __setBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.Lerp(__setBg, new Color(0.78f, 0.84f, 0.98f, 1f), __setHover ? SRApi.Ctl.ThemeHoverFade * 2.9f : 0f);
            bool __setClicked = GUI.Button(__setRect, __setLbl, _mode == Mode.Settings ? _selItem : _btn);
            GUI.backgroundColor = __setBg;
            if (__setClicked) {
                _mode = Mode.Settings;
                NoPageScrollBars = false;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
                BlockWindowDrag = true;
            }
            __bx += __setW + Sc(4f);
            // 功能注册的底栏按钮：从设置按钮右侧依次排开，右端留出模式标签的宽度（放不下就不画，宁缺不叠）。
            string __mlText = ModeLabel() ?? "";
            float __mlW = Mathf.Min(__footIn.width * 0.55f, _label.CalcSize(new GUIContent(__mlText)).x + Sc(8f));
            float __bxMax = __footIn.xMax - __mlW - Sc(6f);
            for (int fbi = 0; fbi < SRApi.FooterButtons.Count; fbi++) {
                SRApi.FooterButton fb = SRApi.FooterButtons[fbi];
                if (fb == null || string.IsNullOrEmpty(fb.Id)) continue;
                string fbLabel = T(fb.LabelZh, fb.LabelEn);
                if (string.IsNullOrEmpty(fbLabel)) continue;
                GUIContent fbContent = new GUIContent(fbLabel, T("打开", "Open ") + fbLabel);
                float fbW = _btn.CalcSize(new GUIContent(fbLabel)).x + Sc(16f);
                if (__bx + fbW > __bxMax) break;
                bool fbSel = _mode == Mode.Internal && _selectedInternalSection == fb.Id;
                if (GUI.Button(new Rect(__bx, __footIn.y, fbW, Sc(22f)), fbContent, fbSel ? _selItem : _btn)) {
                    if (_footerClickFrame != Time.frameCount) {
                        _footerClickFrame = Time.frameCount;
                        BlockWindowDrag = true;
                        try { if (fb.OnClick != null) fb.OnClick(); } catch (Exception __ex) { Guard.Log("底栏按钮", __ex); }
                        _mode = Mode.Internal;
                        _selectedInternalSection = fb.Id;
                        NoPageScrollBars = false;
                        _editText.Clear();
                        _editOpen.Clear();
                        _capturing = null;
                    }
                }
                __bx += fbW + Sc(4f);
            }
            // 右小角：模式 · 关卡（始终贴底栏右端，不受左侧按钮数量影响）
            GUI.Label(new Rect(__footIn.xMax - __mlW, __footIn.y, __mlW, Sc(22f)), __mlText, _label);
            FooterHeight = __footH;
            GUILayout.EndArea();

            DrawResizeGripAndCursor(winRect, mouse, gripRect);
            DrawTooltip(mouse);
            } finally {
                GUI.matrix = prevMatrix; GUI.color = prevColor; GUI.skin.font = SR.Ctl.OrigSkinFont() ?? _prevFont;
                // 还原全局选择色：GUI.skin 是共享皮肤（Configuration Manager 也在用），只在 SR 绘制期间改色。
                RestoreSkinSelectionColor();
            }
            // 自绘页本帧声明的鼠标形状在此统一应用并复位：未声明时回到"不强制"，特殊光标不会残留。
            RunFrameEndHooks();
            }
            // 功能覆盖层最后绘制，保证在最上层（各功能自己判断该不该画）
            DrawOverlays();
        }

// 分区：MonoBehaviour（ManagerUI：Update/LateUpdate/OnGUI 驱动入口）

        private class ManagerUI : MonoBehaviour {
            // 失焦（Alt-Tab / 切窗口）时取消键位录制：IMGUI 事件会断流，录制态会一直挂着。
            private void OnApplicationFocus(bool focused) {
                try { if (!focused && SR.IsCapturingKey) SR.CancelCapture(); }
                catch (Exception __fx) { SR.Guard.Log("失焦取消按键录制", __fx); }
            }

            private bool _startup;

            private void OnEnable() {
                Camera.onPreCull += OnPreCullView;
            }

            private void OnDisable() {
                Camera.onPreCull -= OnPreCullView;
            }

            // 渲染前应用锁定 / 地图取景（含透视相机，此处最后生效）。
            private static void OnPreCullView(Camera cam) {
                try {
                    if (cam == null) return;
                    // 应用各功能注册的视图（是否生效、对哪个相机生效由功能自己判断；无注册时零开销）。
                    ApplyCameraViews(cam);
                } catch (Exception __ex) { Guard.Log("地图/取景 onPreCull", __ex); }
            }

            private void Update() {
                // 延迟到首帧：其它 tweak 可能在 Initialize 之后才注册配置，提前扫描会漏。
                if (!_startup) {
                    // 逐句 Guard.Try，最后才置 _startup：任一子步骤失败不跳过后续自检
                    Guard.Try("启动扫描", () => EnsureScanned());
                    Guard.Try("应用禁用插件", () => ApplyDisabledPlugins());
                    Guard.Try("门控自检", () => GateAudit.Run());
                    Guard.Try("反射自检", () => RefAudit());
                    _startup = true;
                }
                // 视野 / 地图的快捷键由功能自己用 SR.RegisterTick 注册；滚轮缩放走 OnGUI 末尾的 SR.PostGuiWheel。
                // 每帧调度做异常隔离：任一步抛异常不跳过后续步骤；Guard.Try 自带 5 秒去重。
                Guard.Try("每帧 Tick", () => SR.Tick());
                Guard.Try("面板开关键", () => SR.CheckOpenKey());
                Guard.Try("自动快捷键", () => SR.CheckHotkeys());
                // 取景生效点：Camera.onPreCull（渲染前，最后生效）+ ZoomCamera.Update 后缀。
            }

            private void OnGUI() {
                // 分开包：DrawGUI 抛异常时滚轮收口仍要执行。
                Guard.Try("绘制面板", () => SR.DrawGUI());
                // 滚轮仲裁收口：必须在 DrawGUI 之后（界面件已处理完本帧滚轮，Input 读轴的缩放才不会抢滚轮）。
                Guard.Try("滚轮收口", () => SR.PostGuiWheel());
                // 点在下拉框以外 = 收起展开的下拉框：判据是"MouseDown 这一帧无下拉框认领"，不靠坐标
                // （下拉框矩形在滚动区内容坐标系里）。放在 DrawGUI 之后，按钮自身切换已跑完。
                Event __ce = Event.current;
                if (__ce != null && __ce.type == EventType.MouseDown && __ce.button == 0 && _comboSeenFrame != Time.frameCount) CloseOpenCombos();

            }
        }

    }

    // 诊断浮层宿主：独立 MonoBehaviour，**面板关闭时也显示**，只有设置里的开关能关掉它。
    // 样式贴近 HUD：圆角深色面板 + 标题行 + 等宽小字内容。写屏后清空 DiagLines（唯一清空点）。
    internal sealed class SrDiagOverlay : MonoBehaviour {
        private static SrDiagOverlay _host;
        private static GUIStyle _diagTitle;
        private static GUIStyle _diagBody;

        internal static void Ensure() {
            if (_host != null) return;
            try {
                GameObject go = new GameObject("SR_UCH.DiagOverlay");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _host = go.AddComponent<SrDiagOverlay>();
            } catch (Exception __ex) { SR.Guard.Log("诊断浮层/创建", __ex); }
        }

        private void OnGUI() {
            try {
                if (!SR.DiagEnabled) { SRApi.DiagLines.Clear(); return; }
                List<string> lines = SRApi.DiagLines;
                if (lines == null || lines.Count == 0) return;

                if (_diagTitle == null) {
                    _diagTitle = new GUIStyle(GUI.skin.label);
                    _diagTitle.fontSize = Mathf.Max(11, Mathf.RoundToInt(12f * SRApi.Ctl.Sc(1f)));
                    _diagTitle.normal.textColor = new Color(1f, 0.86f, 0.42f, 1f);
                    _diagBody = new GUIStyle(GUI.skin.label);
                    _diagBody.fontSize = Mathf.Max(10, Mathf.RoundToInt(11f * SRApi.Ctl.Sc(1f)));
                    _diagBody.wordWrap = true;
                    _diagBody.normal.textColor = new Color(0.92f, 0.92f, 0.95f, 1f);
                }

                float w = Mathf.Min(Screen.width - 20f, Mathf.Max(260f, 460f * SRApi.Ctl.Sc(1f)));
                float innerW = w - 12f;
                float titleH = 16f;
                float total = titleH + 6f;
                for (int i = 0; i < lines.Count; i++) total += _diagBody.CalcHeight(new GUIContent(lines[i]), innerW) + 1f;
                float maxH = Screen.height * 0.6f;
                float boxH = Mathf.Min(total + 10f, maxH);

                Rect box = new Rect(8f, 8f, w, boxH);
                // HUD 同款：半透明深色圆角面板。
                SR.DrawRounded(box, new Color(0.06f, 0.06f, 0.07f, 0.82f), SRApi.Ctl.ThemeRadius);
                GUI.Label(new Rect(box.x + 6f, box.y + 4f, innerW, titleH), "SR DIAG", _diagTitle);

                float cy = box.y + 4f + titleH + 4f;
                float bottom = box.yMax - 4f;
                for (int i = 0; i < lines.Count; i++) {
                    string s = lines[i];
                    float hh = _diagBody.CalcHeight(new GUIContent(s), innerW);
                    if (cy + hh > bottom) break;   // 超出面板高度就截断（不画到窗口外）
                    GUI.Label(new Rect(box.x + 6f, cy, innerW, hh), s, _diagBody);
                    cy += hh + 1f;
                }
                lines.Clear();   // 写完清空：下一帧重新由功能写入
            } catch (Exception __ex) { SR.Guard.Log("诊断浮层/绘制", __ex); }
        }
    }
}
