// 主窗口框架：Tick 主循环、标题栏、DrawGUI 派发、拖动缩放、侧栏；入口为内嵌 ManagerUI。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Window（主窗口框架：Tick / 标题栏 / DrawGUI / 拖动缩放）


        private static void DrawResizeGripAndCursor(Rect winRect, Vector2 mouse, Rect gripRect) {
            // 整数对齐、贴图与尺寸一致以消锯齿；颜色三态同 ImGui ResizeGrip。
            {
                int gs = Mathf.Max(8, Mathf.RoundToInt(Sc(21)));   // 与贴图一致
                float galpha = _resizing ? 0.95f : (CursorResize && gripRect.Contains(mouse) ? 0.7f : 0.25f);
                Color oldC = GUI.color;
                GUI.color = new Color(0.30f, 0.49f, 1f, galpha);   // ImGui 主题的 accent_color
                GUI.DrawTexture(new Rect(Mathf.Round(winRect.xMax - gs), Mathf.Round(winRect.yMax - gs), gs, gs), _gripTex);
                GUI.color = oldC;
            }
            // 自绘光标（游戏隐藏了系统光标）；悬停栏目分隔线时换成左右箭头。
            Texture2D curTex = CursorResize && _cursorResizeTex != null ? _cursorResizeTex
                : (CursorMove && _cursorMoveTex != null ? _cursorMoveTex : _cursorTex);
            if (curTex != null) GUI.DrawTexture(new Rect(mouse.x - Sc(7), mouse.y - Sc(7), Sc(15), Sc(15)), curTex);
        }


        private static void Tick() {
            // 诊断浮层宿主惰性创建；门控快照每帧刷一次，保证同帧内判断一致。
            RefreshGate();
            FlushConfig(); // 配置节流落盘（最多延迟 1 秒）
            // 惰性 Bind 出条目后补一次 CM 排序（条目表未变时内部直接返回）。
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("同步条目顺序", __ex); }
            // 功能每帧钩子：逐个 try/catch，单功能异常不中断整段 Tick。
            RunTickHooks();
            CursorResize = false; // 每帧复位；命中检测时重新置位
            CursorMove = false;
            // 兜底：鼠标全松开时清 BlockWindowDrag，否则指针移出后松手会漏复位，窗口拖拽被永久阻塞。
            try {
                if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1)) BlockWindowDrag = false;
            } catch (Exception __bwd) { Guard.Log("复位窗口拖拽标记", __bwd); }
            // EventSystem 门控必须每帧跟随，否则 InputLocked 变 false 后不再恢复。
            ApplyEventSystemGate();
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);   // 打开/关闭约 0.12s 淡入淡出
            // 总开关关闭时各功能收起自己的 UI；子开关由各功能自判。
            if (!GateMaster && AnyUiBlocked()) {
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("总开关关闭时收起功能 UI", __ex); }
            }
 
            // 自动保存：按住鼠标不写，松手即存，避免拖动中每帧写盘。
            if (_dirty && !Input.GetMouseButton(0) && !Input.GetMouseButton(1)) {
                _dirty = false;
                // 逐个保存，成功才移除；失败留下次再试。
                if (SR.DirtyConfigs.Count > 0) {
                    var __all = new System.Collections.Generic.List<ConfigFile>(SR.DirtyConfigs);
                    for (int __i = 0; __i < __all.Count; __i++) {
                        ConfigFile __dc = __all[__i];
                        bool __ok = false;
                        Guard.Try("配置保存", () =>
                        {
                            __dc.Save();
                            __ok = true;
                        });
                        if (__ok) SR.DirtyConfigs.Remove(__dc);
                    }
                }
            }
        }

        // 录制：最多三键，需按住上一个再按下一个，松开即完成。
        private static void RecPushKey(KeyCode k) {
            if (k == KeyCode.None) return;
            if (_recSeq.Count >= 3) return;
            if (_recSeq.Contains(k)) return;
            _recSeq.Add(k);
            if (!_recHeld.Contains(k)) _recHeld.Add(k);
        }

        private static void RecCheckRelease() {
            for (int i = _recHeld.Count - 1; i >= 0; i--) {
                bool held;
                try { held = Input.GetKey(_recHeld[i]); } catch { held = false; }
                if (!held) _recHeld.RemoveAt(i);
            }
        }

        // 末键为主键，开头连续修饰键为组合修饰，其余为序列前键（会写盘）。
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
                    // 外部模组 KeyboardShortcut 只支持主键 + 修饰键。
                    List<KeyCode> mk = new List<KeyCode>();
                    if ((mods & ComboMod.Shift) != 0) mk.Add(KeyCode.LeftShift);
                    if ((mods & ComboMod.Ctrl) != 0) mk.Add(KeyCode.LeftControl);
                    if ((mods & ComboMod.Alt) != 0) mk.Add(KeyCode.LeftAlt);
                    try { cap.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(main, mk.ToArray()); } catch (Exception __ex) { Guard.Log("写入组合键(KeyboardShortcut)", __ex); }
                } else {
                    SetValue(cap, main);
                    SetKeyComboMod(cap, mods);
                    SetKeySeqExtra(cap, extras);          // 序列前键合并进同一个隐藏条目
                    // 组合串按按下顺序改写，便于显示；解析按名字，与顺序无关。
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

        // 录键期间屏蔽表情系统：中/侧键会被 EmoteSystem 一并接收；独立嵌套类注册，游戏改名时不影响其它补丁。
        [HarmonyPatch(typeof(EmoteSystem), "ReceiveEvent")]
        private static class EmoteBlockPatch {
            [HarmonyPrefix]
            private static bool Prefix() { return _capturing == null; }
        }

        // 页面拖动自己的控件时置 true；窗口拖动判定早于页面绘制。
        public static bool BlockWindowDrag;
        private static bool _downOnControl;   // 本次手势的起点是否落在控件上（拖滑块不该拖窗口）

        // 右栏内容区（屏幕坐标），每帧刷新。
        public static Rect ContentArea;

        // 右栏底栏高度；自绘页据此预留底部空间，否则末行被盖住。
        public static float FooterHeight;
        private static readonly Dictionary<string, float> _pageFooterH = new Dictionary<string, float>();
        private static float _pageUsedTop;   // 上一帧量到的页头/页签底部 Y（Layout 帧兜底用）   // 各栏目页面外框按钮高度（切换栏目不跳）
        private static int _footerClickFrame = -1;   // 底栏按钮同帧去重

        // 页面滚动位置；自绘页用它抵消滚动区跳动。
        public static Vector2 PageScroll { get { return _scroll; } set { _scroll = value; } }

        // 自绘页声明不要页面级滚动条（内容自己适配窗口）；每帧末置 true，切栏目清掉。
        public static bool NoPageScrollBars;
        // 上一帧页面声明的关滚动；渲染后由核心收走清零，切页不继承。
        private static bool _noScrollDeclared;
        private static int _noScrollLatchFrame = -1;
        private static bool _noScrollLatch;
        // 同一帧 OnGUI 跑多趟，各趟若重读页面声明会让滚动组进出不配对并抛异常，故首趟锁存。
        private static bool ScrollViewLatchedForThisFrame() {
            if (_noScrollLatchFrame != Time.frameCount) {
                _noScrollLatchFrame = Time.frameCount;
                _noScrollLatch = _noScrollDeclared;
            }
            return _noScrollLatch;
        }
        private static string _lastPageSig;   // 上一次的"栏目|页签"，变化时收起下拉框

        // 界面缩放：窗口按比例放大/缩小一次，并把位置夹回屏幕内。
        private static float _lastScale;
        private static void ApplyScaleToWindow(float scale) {
            try {
                if (_lastScale <= 0f) {
                    _lastScale = scale;
                    // 首次也须夹回屏幕内，否则分辨率变化后窗口可能点不回来。
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
                    if (_winWidthEntry != null) _winWidthEntry.Value = _winWidth;
                    if (_winHeightEntry != null) _winHeightEntry.Value = _winHeight;
                    if (_winXEntry != null) _winXEntry.Value = Mathf.RoundToInt(_winX);
                    if (_winYEntry != null) _winYEntry.Value = Mathf.RoundToInt(_winY);
                } catch (Exception __ex) { Guard.Log("保存窗口几何(缩放调整)", __ex); }
                _dirty = true;
                _stylesReady = false;   // 字号变了：样式 / 字体重建
            } catch (Exception __ex) { Guard.Log("界面缩放调整窗口", __ex); }
        }

        private static float DrawTitleBar(float width) {
            float barH = Sc(26);
            Rect barRect = new Rect(0, 0, width, barH);
            GUI.Box(barRect, GUIContent.none, _title);
            float tY = (barH - Sc(26)) / 2f;
            GUI.Label(new Rect(Sc(8), tY, Sc(110), Sc(26)), "SR＿UCH", _titleLabel);
            // 关闭按钮左侧：本 Mod 总开关；关闭后功能运行时失效，开关值不变。
            float mw = Sc(96);
            float mx = barRect.width - Sc(32) - Sc(4) - mw;
            // 提示标签宽度止于总开关左侧，窄窗口时不压住总开关。
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

        // 标题栏提示：开关键名取自当前绑定，改绑后提示同步。
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
                // 面板已关：收掉自绘页的光标形状，否则光标留在屏幕上。
                try { ClearTip(); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                RunFrameEndHooks();
                SR.Ctl.EndLabelColumnFrame();
                return;
            }
            _scaled = Mathf.Clamp(_uiScaleEntry.Value, Ui.ScaleMin, Ui.ScaleMax);
            if (_visible || _uiAlpha > 0.01f) {
            // 全局 GUI 状态（matrix / color / 字体 / 选择色）须在任何副作用前快照，否则异常会污染全局绘制。
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            try {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            float scale = Mathf.Clamp(_uiScaleEntry.Value, Ui.ScaleMin, Ui.ScaleMax);
            ApplyScaleToWindow(scale);
            _scaled = scale;
            _prevFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            Event e = Event.current;
            Vector2 mouse = e.mousePosition; // 无矩阵缩放，无需坐标换算
            SR.Wheel.FrameStart(e);          // 滚轮仲裁：只更新本趟事件信息（区域表跨趟持久）
            Rect winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
            bool over = winRect.Contains(mouse);
            Rect gripRect = new Rect(winRect.xMax - Sc(32), winRect.yMax - Sc(32), Sc(32), Sc(32));
            // 外部模块页右键绑键由按钮当帧自判，跨帧登记的矩形会位移导致绑错。
            // 录制只在非 Layout 事件里跑；捕获超时自愈，否则 _capturing 挂住会让所有热键失效。
            if (_capturing != null && Time.unscaledTime - SR.CaptureStartedAt > SR.CaptureTimeoutSeconds) {
                // CancelCapture 原子清状态，后续提交分支不会再写半截序列。
                Guard.Log("按键录制超时，已自动取消", null);
                SR.CancelCapture();
            }
            if (_capturing != null && e.type != EventType.Layout) {
                // IMGUI 对侧键 (3/4) 的 MouseDown 不一定触发，改用 Input 独立检测。
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
                    // 中键/侧键进序列，左右键用于取消；不能 return，否则中断本帧绘制。
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
                // 录到的键全部松开即保存，所以必须是按住链。
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
                // 命中区限制在顶栏之下、底栏之上，否则窗口外也会判成命中间隔线。
                float sbTopY = winRect.y + Sc(26f);   // 顶栏固定 26（与设置页签行同高）
                float sbBotY = winRect.yMax - SR.FooterHeight;
                bool inSbBand = mouse.y >= sbTopY && mouse.y <= sbBotY;
                bool overSbDiv = inSbBand && Mathf.Abs(mouse.x - sbDivX) < Sc(5);
                if (overSbDiv) CursorResize = true; // 悬停在可拖拽的栏目分隔线上
                if (e.type == EventType.MouseDown && e.button == 0 && overSbDiv) {
                    SidebarResizing = true;
                    e.Use();
                } else if (SidebarResizing) {
                    if (e.type == EventType.MouseDrag) {
                        SidebarW = mouse.x - winRect.x; // SidebarWidth 内部会 clamp
                        e.Use();
                    } else if (e.type == EventType.MouseUp) {
                        SidebarResizing = false;
                        _dragActive = false;
                        _dirty = true;
                        // 拖动结束写回栏目宽度设置（0 表示自动时不写）。
                        try { if (SidebarWEntry != null && SidebarW > 0f) SidebarWEntry.Value = Mathf.RoundToInt(SidebarW); } catch (Exception __ex) { Guard.Log("保存栏目宽度", __ex); }
                    }
                }
            }
            // 松手即清 BlockWindowDrag，页面没机会清时也不卡住窗口拖动。
            if (e.type == EventType.MouseUp) BlockWindowDrag = false;
            if (!_resizing) {
                if (e.type == EventType.MouseDown && over && e.button == 0
                    && !(SR.FloatWindow.IsOpen && SR.FloatWindow.Rect.Contains(mouse))) {
                    _downPos = mouse;
                    // 记下本次按下是否落在控件上：BlockWindowDrag 只在 MouseDown 帧为真，后续拖拽帧会变 false。
                    _downOnControl = BlockWindowDrag;
                }
                if (e.type == EventType.MouseUp) _downOnControl = false;
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
                // 只判左键（侧栏排序用右键）；不用 hotControl==0：滚动视图按下即拿走它，空白处就拖不动窗口。
                // 独立窗口（一类浮窗 / 二类地图）上方的拖动归它们自己：否则拖浮窗会连 SR 一起拖。
                bool overStandalone = SR.FloatWindow.IsOpen && SR.FloatWindow.Rect.Contains(mouse);
                if (e.type == EventType.MouseDrag && e.button == 0 && over && !_dragActive && !BlockWindowDrag
                    && !_downOnControl && !overStandalone) {
                    _dragActive = true;
                    _dragMoved = false;
                    _dragOffset = mouse - new Vector2(_winX, _winY);
                    _downPos = mouse;
                }
            }

            GUI.color = new Color(1f, 1f, 1f, _uiAlpha);
            float pop = 1f + (1f - _uiAlpha) * 0.05f;
            // 这里只设新矩阵；还原靠 try 外抓取的 prevMatrix，顺序不能反。
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(pop, pop, 1f));

            GUI.Box(winRect, GUIContent.none, _win);
            // 区域局部坐标与屏幕坐标的偏移，供滚轮换算。
            SR.AreaOffset = new Vector2(winRect.x + Sc(4), winRect.y + Sc(4));
            GUILayout.BeginArea(new Rect(winRect.x + Sc(4), winRect.y + Sc(4), winRect.width - Sc(8), winRect.height - Sc(8)));
            float barH = 0f;
            // 标题栏异常不能跳到 finally，否则布局组配对失衡。
            try { barH = DrawTitleBar(winRect.width - Sc(8)); }
            catch (Exception __tb) { Guard.Log("标题栏渲染", __tb); }
            GUILayout.Space(barH + Sc(2));
            // 底栏是绝对定位固定高条：内容高度须扣掉它，左右栏在其上方结束。
            float __footH = Sc(28f);
            float __areaInnerH = winRect.height - Sc(8);
            float __bodyH = Mathf.Max(Sc(80f), __areaInnerH - (barH + Sc(2)) - __footH - Sc(2f));
            GUILayout.BeginHorizontal();
            float sbw = SidebarWidth();
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
                    _scroll = Vector2.zero;
                    NoPageScrollBars = false;   // 换栏目：页面级滚动条要求复位（由自绘页每帧重新声明）
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.Space(Sc(6));
            GUILayout.Box(GUIContent.none, _footer, GUILayout.Height(Sc(1)), GUILayout.ExpandWidth(true));
            GUILayout.Space(Sc(4));
            foreach (PluginEntry p in _externalPlugins) {
                // 被禁用的插件在侧栏标 *。
                string label = (IsExternalDisabled(p.guid) ? "* " : "") + p.name;
                if (GUILayout.Button(new GUIContent(label, p.guid + (IsExternalDisabled(p.guid) ? "\n" + T("已禁用（设置页可重新启用）", "disabled (re-enable in Settings)") : "")),
                        _mode == Mode.External && p.guid == _pluginKey ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.External;
                    _scroll = Vector2.zero;
                // 右键该行可绑定插件启用 / 禁用热键。
                    _pluginKey = p.guid;
                    NoPageScrollBars = false;
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.EndScrollView();
            // 侧栏滚轮：指针在侧栏时剩余滚轮量归侧栏，Unity 滚动视图收不到事件。
            try {
                if (SR.Wheel.HasPending) {
                    Vector2 mpSb = SR.Wheel.MouseScreen;   // 事件趟的屏幕坐标
                    if (mpSb.x < ContentArea.x) {
                        // TakeRemaining(false) 是丢弃语义，侧栏必须用 true，否则永远滚不动。
                        float wSb = SR.Wheel.TakeRemaining(true);
                        if (Mathf.Abs(wSb) > 0.01f) _leftScroll.y = Mathf.Max(0f, _leftScroll.y - wSb * Sc(64f));
                    }
                }
            } catch (Exception __sb) { Guard.Log("侧栏滚轮", __sb); }
            GUILayout.EndVertical();
            GUILayout.Space(Sc(2));
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.Height(__bodyH));
            float colWidth = EntryNameWidth();
            // 功能页外框部件：滚动区之外，功能自注册，仅选中栏目时调用；页签须画在顶栏之前。
            if (_mode == Mode.Internal) SR.RenderPageTabs(_selectedInternalSection);
            if (_mode == Mode.Internal) SR.RenderPageHeader(_selectedInternalSection);
            // ================= 滚轮：唯一入口（仲裁器） =================
            // FrameStart 已记成待处理量并 Use()；控件用 WheelTake 取用，剩下归页面滚动，帧尾结算。
            Event __we = Event.current;
            bool __wasWheel = SR.Wheel.HasPending || (__we != null && __we.type == EventType.ScrollWheel);
            // 换栏目/页签时收起下拉框：跨页残留会一直声明滚轮归它，页面滚动失效。
            try {
                string __sig = (_mode == Mode.Settings ? "settings" : (_selectedInternalSection ?? ""))
                    + "|" + (_mode == Mode.Settings ? "" : (SR.SelectedPageId(_selectedInternalSection) ?? ""));
                if (__sig != _lastPageSig) {
                    _lastPageSig = __sig;
                    SR.CloseOpenCombos();
                    SR.Ctl.ResetLabelColumn();   // 换页：标签列宽重新按本页标签收敛
                    _noScrollDeclared = false;   // 旧页的"禁止页面滚动"声明不能带到新页（会吞掉换页后的第一格滚轮）
                    SR.HighlightQuery = "";      // 换页清掉表格搜索高亮（否则旧查询串会染到别的列表）
                    _scroll = Vector2.zero;     // 换页回到顶部：新页内容更短时不会停在空白处，看起来像"滚不动"
                }
            } catch (Exception __cs) { Guard.Log("换页收起下拉框", __cs); }

            Vector2 pageBefore = _scroll;
            // 视口 = 内容区高 − 页头高 − 底栏保留高；不限制会让滚动区吃满、底栏压住末行。
            float __areaH = __areaInnerH;
            // Layout 阶段 GetLastRect 可能为 0，改用上一帧页头高，否则视口算成满高。
            float __usedTop = _pageUsedTop;
            // 零高度探针：Layout 帧也能拿到当前光标的准确 y。
            try {
                Rect __probe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
                if (__probe.y > Sc(1f)) { __usedTop = __probe.y; _pageUsedTop = __probe.y; }
            } catch (Exception __ut) { Guard.Log("页面视口/页头高度", __ut); }
            // 底栏预留 = 底栏行高（上一帧）+ 该栏目外框按钮高（按栏目缓存，切栏目不跳）。
            float __pfReserve = 0f;
            // 未知栏目按 0 预留；用其它栏目值兜底会让表格底部多出空白。
            try { _pageFooterH.TryGetValue(_selectedInternalSection, out __pfReserve); } catch { __pfReserve = 0f; }
            // 布局层只预留条高 + 该栏目外框高 + 小余量。
            float __barH = Sc(26f);
            float __footerReserve = __barH + Mathf.Max(0f, __pfReserve);
            // 视口高度先用上一帧实测值（首次兜底），帧末再按滚动区实测刷新。
            if (SRApi.PageViewportHeight <= Sc(60f))
                SRApi.PageViewportHeight = Mathf.Max(Sc(120f), __areaH - __footerReserve);
            SRApi.PageBodyHeight = __bodyH;                 // 右栏可用高度（布局单位）
            SRApi.PageContentBottom = barH + Sc(2f) + __bodyH;
            SRApi.PageUsedTop = __usedTop;                  // 页面内容起点（页头之后）
            // 页面内容底界（滚动区顶 + 视口高 = 底栏上沿），列表高度按它算。
            SRApi.PageContentBottomY = __usedTop + SRApi.PageViewportHeight;
            SRApi.Diag("mode=" + _mode + " section=" + _selectedInternalSection);
            SRApi.Diag("areaH=" + __areaH.ToString("0.0") + " usedTop=" + __usedTop.ToString("0.0") + " barH=" + __barH.ToString("0.0"));
            SRApi.Diag("viewport=" + SRApi.PageViewportHeight.ToString("0.0") + " footerTop=" + (__areaH - __barH).ToString("0.0") + " scrollY=" + _scroll.y.ToString("0.0"));
            Ctl.ComboWheelIntercept();   // 下拉框上的滚轮：在 ScrollView 之前截走，页面就不会被滚
            bool __noScroll = ScrollViewLatchedForThisFrame();

            // 填充页（预设 / 联机）不建滚动区：列表高度已算到底栏上沿，再套会裁掉内容却按可见计算。
            bool __useScrollView = !__noScroll;
            Vector2 __scrollBeforeWheel = _scroll;
            if (__useScrollView) {
                _scroll = GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none, GUILayout.ExpandHeight(true));
            } else {
                _scroll = Vector2.zero;   // 填充页没有页面滚动
            }
            if (_scroll != pageBefore) WheelFrame = Time.frameCount; // 页面滚动区吃掉了滚轮
            // 下拉框展开时滚轮只给下拉列表，撤销页面滚动量，否则整页跟着滑。
            if (Event.current != null && Event.current.type == EventType.ScrollWheel
                && (Ctl.AnyComboOpen() || SR.ComboWheelConsumedThisFrame))
                _scroll = __scrollBeforeWheel;
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                // 功能自绘页：各功能用 SR.RegisterPage(section, render) 注册，无需改本文件。
                if (SR.HasPage(_selectedInternalSection)) {
                    // 页面异常须就地吞掉并记日志，否则跳到外层 finally 会让布局组配对失衡。
                    try { SR.RenderPage(_selectedInternalSection); }
                    catch (Exception __pg) { Guard.Log("页面渲染 " + _selectedInternalSection, __pg); }
                } else {
                    // 兜底：无自绘页的栏目走通用条目列表。
                    bool any = false;
                    foreach (ConfigEntryBase entry in InternalSectionEntries()) {
                        any = true;
                        try { RenderEntryRow(entry, true, colWidth); }
                        catch (Exception __er) { Guard.Log("条目行渲染", __er); }
                    }
                    if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
                }
            } else if (_mode == Mode.Settings) {
                try { RenderSettingsPage(colWidth); }   // 通用 / 自检 两页（自检页含诊断浮层开关）
                catch (Exception __sp) { Guard.Log("设置页渲染", __sp); }
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
            if (__useScrollView) {
                GUILayout.EndScrollView();
                // 实测滚动区高度即可用视口，供下一帧算表格高度。
                try {
                    Rect __sv = GUILayoutUtility.GetLastRect();
                    float __vpMax = Mathf.Max(Sc(120f), __areaH - __usedTop - __footerReserve);
                    if (__sv.height > Sc(40f)) SRApi.PageViewportHeight = Mathf.Min(__sv.height, __vpMax);
                    else if (SRApi.PageViewportHeight > __vpMax) SRApi.PageViewportHeight = __vpMax;
                } catch (Exception __vm) { Guard.Log("页面视口实测", __vm); }
            } else {
                // 无滚动区时视口按区域高 − 页头 − 底栏预留直接算。
                SRApi.PageViewportHeight = Mathf.Max(Sc(120f), __areaH - __usedTop - __footerReserve);
            }
            // 滚轮结算（帧尾）：控件没取走的才滚页面。
            try {
                if (SR.Wheel.HasPending && !SR.WheelTakenThisFrame) {   // 控件已通过即时输入消费：不许再滚页面
                    if (SR.Wheel.CtrlHeld) {
                        // Ctrl+滚轮切页签。
                        if (_mode == Mode.Internal) CyclePage(_selectedInternalSection, SR.Wheel.Value > 0f ? -1 : 1);
                        SR.Wheel.TakeRemaining(false);
                    } else {
                        // 顺序：控件 → 页面级钩子（列表自己滚） → 页面滚动。
                        bool __done = false;
                        if (SR.Wheel.TryFallback()) __done = true;
                        if (!__done) {
                            float __w0 = SR.Wheel.Value;
                            Vector2 __mp0 = SR.Wheel.MouseScreen;
                            try {
                                if (SRApi.DispatchPageWheel(__mp0, __w0) || SRApi.DispatchPageWheel(__mp0 + SR.AreaOffset, __w0)) {
                                    SR.Wheel.TakeRemaining(false);   // 已由列表消费，清掉待处理量
                                    __done = true;
                                }
                            } catch (Exception __dh) { Guard.Log("滚轮/页面钩子", __dh); }
                        }
                        if (!__done && _noScrollDeclared) {
                            float __wf = SR.Wheel.Value;
                            if (SRApi.Ctl.FillScrollAll(_selectedInternalSection, __wf)) {
                                SR.Wheel.TakeRemaining(false);
                                __done = true;
                            }
                        }
                        if (!__done) {
                        bool allow = !_noScrollDeclared;
                        float w = SR.Wheel.TakeRemaining(allow);
                        if (Mathf.Abs(w) > 0.01f) {
                            if (SR.Wheel.ShiftHeld) _scroll.x = Mathf.Max(0f, _scroll.x - w * Sc(64f));
                            else _scroll.y = Mathf.Max(0f, _scroll.y - w * Sc(64f));
                            try { Event evp = Event.current; if (evp != null) evp.Use(); } catch (Exception __ep) { Guard.Log("滚轮/页面消费", __ep); }
                        }
                        }
                    }
                }
            } catch (Exception __ws) { Guard.Log("滚轮结算", __ws); }

            // 量外框按钮高度并按栏目记住，供下一帧预留。
            float __pfTop = 0f;
            try { __pfTop = GUILayoutUtility.GetLastRect().yMax; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (_mode == Mode.Internal) SR.RenderPageFooter(_selectedInternalSection);
            try {
                float __pfBottom = GUILayoutUtility.GetLastRect().yMax;
                float __pfH = Mathf.Max(0f, __pfBottom - __pfTop);
                if (_mode == Mode.Internal && !string.IsNullOrEmpty(_selectedInternalSection)) _pageFooterH[_selectedInternalSection] = __pfH;
            } catch (Exception __pm) { Guard.Log("页面外框高度测量", __pm); }

            // ---------- 底栏：绝对定位，不参与 GUILayout，不因内容溢出漂移 ----------
            GUILayout.EndVertical();     // 右栏
            GUILayout.EndHorizontal();   // 左栏 + 右栏
            // 底栏矩形按区域高反推，GUILayout 溢出时位置不变。
            float __areaW = winRect.width - Sc(8);
            Rect __footRect = new Rect(Sc(2f), Mathf.Max(0f, __areaInnerH - __footH), Mathf.Max(Sc(60f), __areaW - Sc(4f)), __footH);
            SR.DrawRounded(__footRect, SRApi.Ctl.ThemeFooterBg, SRApi.Ctl.ThemeRadius);
            Rect __footIn = new Rect(__footRect.x + Sc(4f), __footRect.y + Sc(3f), Mathf.Max(Sc(40f), __footRect.width - Sc(8f)), Sc(22f));
            float __bx = __footIn.x;
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
                _scroll = Vector2.zero;   // 换页必须复位滚动位置，否则新页继承旧偏移
                NoPageScrollBars = false;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
                BlockWindowDrag = true;
            }
            __bx += __setW + Sc(4f);
            // 功能底栏按钮自设置按钮右侧排开，右端避让模式标签，放不下不画。
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
                        _scroll = Vector2.zero;
                        NoPageScrollBars = false;
                        _editText.Clear();
                        _editOpen.Clear();
                        _capturing = null;
                    }
                }
                __bx += fbW + Sc(4f);
            }
            GUI.Label(new Rect(__footIn.xMax - __mlW, __footIn.y, __mlW, Sc(22f)), __mlText, _label);
            FooterHeight = __footH;
            GUILayout.EndArea();

            DrawResizeGripAndCursor(winRect, mouse, gripRect);
            // 悬浮说明只在指针位于窗口内时绘制，统一在此门控一次。
            if (over) DrawTooltip(mouse);
            else { try { GUI.tooltip = ""; } catch { /* 静默：清提示失败不影响绘制 */ } }
            // 手势级标记必须在本趟末尾补记：控件在页面绘制时才置 BlockWindowDrag，判定却在函数开头。
            try {
                if (e.type == EventType.MouseDown && e.button == 0 && over) _downOnControl = BlockWindowDrag;
            } catch (Exception __dg) { Guard.Log("窗口拖拽/按下标记", __dg); }
            } finally {
                GUI.matrix = prevMatrix; GUI.color = prevColor; GUI.skin.font = SR.Ctl.OrigSkinFont() ?? _prevFont;
                // 还原全局选择色：GUI.skin 是共享皮肤，只在 SR 绘制期间改色。
                RestoreSkinSelectionColor();
                // 收走本帧页面声明并清零，下帧只认仍在声明的页面。
                _noScrollDeclared = NoPageScrollBars;
                NoPageScrollBars = false;
            }
            // 统一应用并复位页面声明的鼠标形状，未声明时不强制。
            RunFrameEndHooks();
            SR.Ctl.EndLabelColumnFrame();   // 标签列宽本帧收敛（下一帧生效）
            }
            DrawOverlays();
            // 一类浮窗（调色板 / 重命名）：只在主面板打开时画，随面板一起收起。
            try { SR.DrawAttachedWindows(); } catch (Exception __aw) { Guard.Log("绘制随面板浮窗", __aw); }

        }

// 分区：MonoBehaviour（ManagerUI：Update/LateUpdate/OnGUI 驱动入口）

        private class ManagerUI : MonoBehaviour {
            // 失焦时取消键位录制：IMGUI 事件断流会让录制态一直挂着。
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

            private static void OnPreCullView(Camera cam) {
                try {
                    if (cam == null) return;
                    // 应用功能注册的视图；相机选择由功能自判，无注册时零开销。
                    ApplyCameraViews(cam);
                } catch (Exception __ex) { Guard.Log("地图/取景 onPreCull", __ex); }
            }

            // 静态委托复用，避免每帧 new 闭包造成 GC 抖动。
            private static readonly System.Diagnostics.Stopwatch _perfSw = new System.Diagnostics.Stopwatch();
            private static void DoTick() {
                _perfSw.Restart();
                try { SR.Tick(); } finally {
                    float ms = (float)_perfSw.Elapsed.TotalMilliseconds;
                    SR.PerfTickAvgMs += (ms - SR.PerfTickAvgMs) * 0.1f;
                    if (ms > SR.PerfTickMaxMs) SR.PerfTickMaxMs = ms;
                }
            }
            private static void DoDrawGui() {
                _perfSw.Restart();
                try { SR.DrawGUI(); } finally {
                    float ms = (float)_perfSw.Elapsed.TotalMilliseconds;
                    SR.PerfGuiAvgMs += (ms - SR.PerfGuiAvgMs) * 0.1f;
                    if (ms > SR.PerfGuiMaxMs) SR.PerfGuiMaxMs = ms;
                }
            }
            private static readonly Action _actTick = DoTick;
            private static readonly Action _actOpenKey = SR.CheckOpenKey;
            private static readonly Action _actHotkeys = SR.CheckHotkeys;
            private static readonly Action _actDrawGui = DoDrawGui;
            private static readonly Action _actDrawStandalone = delegate { SR.DrawStandaloneWindows(); };
            private static readonly Action _actPostWheel = SR.PostGuiWheel;

            private void Update() {
                // 延迟到首帧扫描：其它 tweak 可能在 Initialize 之后才注册配置。
                if (!_startup) {
                    // 子步骤逐个 Guard.Try，最后才置 _startup，失败不跳过后续自检。
                    Guard.Try("启动扫描", () => EnsureScanned());
                    Guard.Try("应用禁用插件", () => ApplyDisabledPlugins());
                    Guard.Try("门控自检", () => GateAudit.Run());
                    Guard.Try("反射自检", () => RefAudit());
                    _startup = true;
                }
                // 视野 / 地图快捷键由功能用 SR.RegisterTick 注册；每帧调度做异常隔离，Guard.Try 自带 5 秒去重。
                Guard.Try("每帧 Tick", _actTick);
                Guard.Try("面板开关键", _actOpenKey);
                Guard.Try("自动快捷键", _actHotkeys);
            }

            private void OnGUI() {
                // 先记按键：界面吞输入时 ComboKeyDown 仍能命中。
                try { SR.RecordGuiKeyEvent(Event.current); } catch (Exception __gk) { SR.Guard.Log("OnGUI 记录按键", __gk); }
                // 分开包：DrawGUI 抛异常时滚轮收口仍要执行。
                Guard.Try("绘制面板", _actDrawGui);
                // 二类独立窗口（地图）：主面板关闭时也要能用。
                Guard.Try("绘制独立窗口", _actDrawStandalone);
                // 滚轮收口必须在 DrawGUI 之后，否则 Input 读轴缩放会抢滚轮。
                Guard.Try("滚轮收口", _actPostWheel);
                // 点在下拉框外即收起：判据是 MouseDown 帧无下拉框认领，不靠坐标；放在 DrawGUI 之后。
                Event __ce = Event.current;
                if (__ce != null && __ce.type == EventType.MouseDown && __ce.button == 0 && ComboSeenFrame != Time.frameCount) CloseOpenCombos();

            }
        }

    }

}
