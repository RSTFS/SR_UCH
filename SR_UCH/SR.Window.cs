// 主窗口框架。Tick 主循环、标题栏、DrawGUI 的页面派发、窗口拖动缩放、侧栏。
// 对外的每帧驱动入口是里面的 ManagerUI（一个 MonoBehaviour，提供 Update / LateUpdate / OnGUI）。
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

        private static void Tick() {
            //门控快照：每帧刷新一次（所有限制读同一份 -> 同帧一致）
            RefreshGate();
            FlushConfig(); //配置节流落盘（改过的值最多延迟 1 秒写盘）
            //惰性 Bind 出新条目后补一次 Configuration Manager 排序（条目表没变时内部直接返回）
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("同步条目顺序", __ex); }
            //功能每帧钩子（加载后清理 / 联机看门狗...）：功能自己 SR.RegisterTick 注册，
            //这里逐个 try/catch, 单个功能抛异常不再中断整段 Tick（落盘/淡入/地图关闭都会照常跑）。
            RunTickHooks();
            CursorResize = false; //每帧复位；命中检测（SR.Window / 联机页）时重新置位
            CursorMove = false;
            // EventSystem 门控必须**每帧跟随**：原来只在"打开面板那一刻"判定一次，
            //  若之后 InputLocked 变 false（关 UI / 关掉屏蔽输入）而面板仍开着，就再也不会恢复 ->
            //  游戏的 UGUI EventSystem 一直禁用 -> 树屋回合开始、回合末结算都点不动（表现为"卡住"）。
            ApplyEventSystemGate();
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);
            //总开关关闭时请各功能收起自己的 UI（地图窗口 / 自由相机等）。
            //"地图自己的总开关"由地图功能自己判断，核心不认识它。
            if (!GateMaster && AnyUiBlocked()) {
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("总开关关闭时收起功能 UI", __ex); }
            }
 
            //冻结角色（外部模块页控制，默认关）：只冻结自己的角色（见 FreezeLocalCharacter），不暂停全局时间 -> 其他角色/动画照常移动，保留原版观感。
            if (_pauseApplied) {
                Time.timeScale = _pauseSavedTs;
                _pauseApplied = false;
            }
            //（地图滚轮缩放原来在这里读 Input.GetAxis：Update 先于 OnGUI，看不到同一帧界面件吃掉滚轮的记录，
            //  会"在下拉框上滚轮同时缩放地图"-> 已挪到 PostGuiWheel，见 SR.Input.cs）
            //自动保存：拖动滑块/窗口时先不写，松手即保存（避免拖动中每帧写盘）
            if (_dirty && !Input.GetMouseButton(0) && !Input.GetMouseButton(1)) {
                _dirty = false;
                if (_dirtyConfig != null) {
                    var dc = _dirtyConfig;
                    _dirtyConfig = null;
                    //配置写盘失败以前是静默的（表现为改了设置重启就没了）；统一记日志
                    Guard.Try("配置保存", () => dc.Save());
                }
            }
        }

        //录制规则：最多三个键、每键只记一次、必须按住上一个再按下一个
        //（松开任意键即完成录制，所以序列只能是和弦式"按住链"）。
        private static void RecPushKey(KeyCode k) {
            if (k == KeyCode.None) return;
            if (_recSeq.Count >= 3) return;
            if (_recSeq.Contains(k)) return;
            _recSeq.Add(k);
            if (!_recHeld.Contains(k)) _recHeld.Add(k);
        }

        //把已松开的键移出"仍按住"集合（清空即完成录制）
        private static void RecCheckRelease() {
            for (int i = _recHeld.Count - 1; i >= 0; i--) {
                bool held;
                try { held = Input.GetKey(_recHeld[i]); } catch { held = false; }
                if (!held) _recHeld.RemoveAt(i);
            }
        }

        //最后一个键 = 主键；开头连续修饰键 = 组合修饰；其余 = 序列前键。
        //单键 / Shift+1 / Shift+Q+W / Q+W 都用同一条存储格式。
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
                    //外部模组的 KeyboardShortcut 只支持主键+修饰键，序列前键不适用
                    List<KeyCode> mk = new List<KeyCode>();
                    if ((mods & ComboMod.Shift) != 0) mk.Add(KeyCode.LeftShift);
                    if ((mods & ComboMod.Ctrl) != 0) mk.Add(KeyCode.LeftControl);
                    if ((mods & ComboMod.Alt) != 0) mk.Add(KeyCode.LeftAlt);
                    try { cap.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(main, mk.ToArray()); } catch (Exception __ex) { Guard.Log("写入组合键(KeyboardShortcut)", __ex); }
                } else {
                    SetValue(cap, main);
                    SetKeyComboMod(cap, mods);
                    SetKeySeqExtra(cap, extras);          //序列前键合并进同一个隐藏条目
                    // **顺序修复**：把组合串改写为"按下顺序"（显示端据此保序；解析按名字，不依赖顺序）
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

        //录键期间屏蔽游戏自己的表情系统：鼠标中键/侧键在 UCH 里同时被表情系统接收（游戏 InputEvent），
        //绑键时会顺带触发游戏 UI -> 看起来像"界面被抢了一下"。做法与 RemovePlayerPlacements 一致
        //（它给 EmoteSystem.ReceiveEvent 挂前缀，按住自己的键时 return false）。
        //独立嵌套类 + 单独注册：万一将来游戏改名，失败也不会带走 SR 的其它补丁。
        [HarmonyPatch(typeof(EmoteSystem), "ReceiveEvent")]
        private static class EmoteBlockPatch {
            [HarmonyPrefix]
            private static bool Prefix() { return _capturing == null; }
        }

        //页面里正在拖自己的控件（如联机列表的列宽分隔线）时置 true：本帧起不要拖动整个窗口。
        //为什么需要：窗口拖动判定在页面绘制之前执行，页面这帧的 Event.Use() 拦不住它。
        public static bool BlockWindowDrag;

        //右栏内容区（屏幕坐标），每帧刷新；自绘页用它铺满窗口
        public static Rect ContentArea;

        //右栏底部那行（设置/模式）的高度：自绘页（联机列表）要用它给表格留出底部空间，
        //否则表格最后几行会被这行盖住（"最下面的数据被设置按钮挡住"）。
        public static float FooterHeight;

        //页面滚动位置：自绘页（联机列表下拉框）用它抵消"控件变化导致滚动区跳动"
        public static Vector2 PageScroll { get { return _scroll; } set { _scroll = value; } }

        //自绘页要求：本页内容自己适配窗口，不要页面级滚动条。
        //为什么需要：页面滚动条是被页面每帧写回的 PageScroll 拖住的（拖不动），而且它不随界面缩放，
        //在联机列表页会和表格的横向条凑成"两个用不到的滚动条"。页面自己在每帧末置 true，切换栏目时清掉。
        public static bool NoPageScrollBars;

        //界面缩放：字号/控件都乘 Sc()，但窗口本身原来不变 -> 放大后内容被裁（文字显示不全）。
        //这里让窗口跟着缩放比例同步变大/变小（一次性），并把位置夹回屏幕内。
        private static float _lastScale;
        private static void ApplyScaleToWindow(float scale) {
            try {
                if (_lastScale <= 0f) {
                    _lastScale = scale;
                    //首次仍要把读盘得到的窗口位置/尺寸夹回屏幕内，否则分辨率变化后窗口可能点不回来
                    _winWidth = Mathf.Clamp(_winWidth, 400, 1600);
                    _winHeight = Mathf.Clamp(_winHeight, 300, 1000);
                    _winX = Mathf.Clamp(_winX, 0f, Mathf.Max(0f, Screen.width - _winWidth));
                    _winY = Mathf.Clamp(_winY, 0f, Mathf.Max(0f, Screen.height - _winHeight));
                    return;
                }
                if (Mathf.Abs(scale - _lastScale) < 0.001f) return;
                float r = scale / _lastScale;
                _lastScale = scale;
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_winWidth * r), 400, 1600);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_winHeight * r), 300, 1000);
                _winX = Mathf.Clamp(_winX, 0f, Mathf.Max(0f, Screen.width - _winWidth));
                _winY = Mathf.Clamp(_winY, 0f, Mathf.Max(0f, Screen.height - _winHeight));
                try {
                    //写缓存条目（不再每次 Bind）
                    if (_winWidthEntry != null) _winWidthEntry.Value = _winWidth;
                    if (_winHeightEntry != null) _winHeightEntry.Value = _winHeight;
                    if (_winXEntry != null) _winXEntry.Value = Mathf.RoundToInt(_winX);
                    if (_winYEntry != null) _winYEntry.Value = Mathf.RoundToInt(_winY);
                } catch (Exception __ex) { Guard.Log("保存窗口几何(缩放调整)", __ex); }
                _dirty = true;
                _stylesReady = false;   //字号变了：样式/字体重建
            } catch (Exception __ex) { Guard.Log("界面缩放调整窗口", __ex); }
        }

            //标题栏用绝对布局；/ 折叠整个窗口；右端 = 总开关 + 关闭
        private static float DrawTitleBar(float width) {
            float barH = Sc(26);
            Rect barRect = new Rect(0, 0, width, barH);
            GUI.Box(barRect, GUIContent.none, _title);
            float tY = (barH - Sc(26)) / 2f;
            //左上角的折叠三角已按需求删除（不再提供折叠）；标题直接顶到左边
            GUI.Label(new Rect(Sc(8), tY, Sc(110), Sc(26)), "SR＿UCH", _titleLabel);
            //关闭按钮左边：本 Mod 总开关（关闭时所有内部功能运行时失效，各功能开关值保持不变）
            float mw = Sc(96);
            float mx = barRect.width - Sc(32) - Sc(4) - mw;
            //提示标签的宽度必须**止于总开关左边**：原来写的是"窗口宽 - 160"，右端伸进总开关底下，
            //窗口不够宽时提示文字被总开关盖住（左对齐 + 收窄 = 只会从右边裁掉，不再被压住）。
            GUI.Label(new Rect(Sc(124), tY, Mathf.Max(Sc(40), mx - Sc(124) - Sc(6)), Sc(26)),
                T("INS开关界面&悬停条目查看说明", "INS: manager · hover entries for tooltips"), _titleMid);
            if (GUI.Button(new Rect(mx, tY, mw, Sc(26)),
                new GUIContent(AllEnabled ? T("总开关：开", "Master ON") : T("总开关：关", "Master OFF"),
                    T("本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变", "Mod master switch: off disables all internal features at runtime")),
                AllEnabled ? _selItem : _btn)) {
                AllEnabled = !AllEnabled;
                if (_allEnabledEntry != null) {
                    _allEnabledEntry.Value = AllEnabled;
                    Guard.Try("总开关自动保存", () => _allEnabledEntry.ConfigFile.Save()); //失败记日志
                }
            }
            if (GUI.Button(new Rect(barRect.width - Sc(32), tY, Sc(26), Sc(26)), "X", _btn)) CloseMenu();
            return barH;
        }

        private static void DrawGUI() {
            if (!_visible && _uiAlpha <= 0.01f && !AnyUiBlocked()) {
                //面板已经关掉：把自绘页当帧声明的系统光标形状收掉（否则 <->/+ 光标会留在屏幕上）
                RunFrameEndHooks();
                return;
            }
            _scaled = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            if (_visible || _uiAlpha > 0.01f) {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            //布局缩放：所有固定尺寸乘 Sc()，字号随之
            float scale = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            ApplyScaleToWindow(scale);
            _scaled = scale;
            _prevFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            Event e = Event.current;
            Vector2 mouse = e.mousePosition; //无矩阵缩放，无需坐标换算

            Rect winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
            bool over = winRect.Contains(mouse);
            //右下角自定义大小：热区略大于三角，手感更稳（三角本身已按需求改成 24）
            Rect gripRect = new Rect(winRect.xMax - Sc(32), winRect.yMax - Sc(32), Sc(32), Sc(32));
            if (_winCollapsed) {
                winRect.height = Sc(32); //collapsed: title bar only
                gripRect = new Rect(0, 0, 0, 0);
            }

            //外部模块页按钮右键绑键由按钮绘制当帧自判（跨帧登记矩形会被状态栏增减行挤位移 -> 绑错键）；
            //下拉框列表在布局流内下一行绘制，无需帧首吞事件。
            //快捷键录制：单键 / 修饰键+主键 / 多普通键序列；Esc 清空、Shift+Esc 放弃、点别处取消（松开按键即保存）。
            //注意：整个录制块只在非 Layout事件里跑。Input.GetMouseButtonDown 在 Layout / Repaint 阶段同样返回 true
            //（它按帧判定，和 IMGUI 当前在处理哪种事件无关），旧代码在 Layout 阶段对着布局事件 Use() ->
            //IMGUI 布局状态被打断 -> 整个窗口闪一下（这就是鼠标侧键/中键绑键闪屏的根因）。
            if (_capturing != null && e.type != EventType.Layout) {
                //IMGUI 的 MouseDown 对侧键(3/4) 不一定触发，故用 Input.GetMouseButtonDown 独立检测
                bool sideHandled = false;
                int sideBtn = -1;
                try {
                    if (Input.GetMouseButtonDown(3)) sideBtn = 3;      //侧键1 (Mouse4)
                    else if (Input.GetMouseButtonDown(4)) sideBtn = 4; //侧键2 (Mouse5)
                } catch { sideBtn = -1; }
                if (sideBtn >= 0) {
                    RecPushKey(KeyCode.Mouse0 + sideBtn); //鼠标键也进序列（Mouse3 / Mouse4）
                    if (e.type == EventType.MouseDown) e.Use(); //Repaint 阶段不需要（也不能）吞事件
                    sideHandled = true; //同帧跳过下面的取消/结束判定（这里同样不能 return，return 会中断本帧绘制 -> 闪一下）
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
                    //中键/侧键进序列；左键/右键不录（用于取消）。同样不能 return（会中断本帧绘制 -> 闪一下）
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
                //结束条件：录到的键全部松开 -> 立刻保存（所以必须是"按住链"）。
                if (_capturing != null && _recSeq.Count > 0) {
                    RecCheckRelease();
                    if (_recHeld.Count == 0) CommitRecording();
                }
            }
            if (!_winCollapsed && e.type == EventType.MouseDown && gripRect.Contains(mouse) && e.button == 0) {
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
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.x + (mouse.x - _resizeStart.x)), 400, 1600);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.y + (mouse.y - _resizeStart.y)), 300, 1000);
                winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                gripRect = new Rect(winRect.xMax - Sc(32), winRect.yMax - Sc(32), Sc(32), Sc(32));
                if (_winWidthEntry != null) _winWidthEntry.Value = _winWidth;    //写缓存条目（不再每个 MouseDrag 事件 Bind）
                if (_winHeightEntry != null) _winHeightEntry.Value = _winHeight;
                _dirty = true;
                e.Use();
            }
            if (!_resizing) {
                float sbDivX = winRect.x + SidebarWidth();
                bool overSbDiv = Mathf.Abs(mouse.x - sbDivX) < Sc(5);
                if (overSbDiv) CursorResize = true; //悬停在可拖拽的栏目分隔线上：换成左右调整光标
                if (e.type == EventType.MouseDown && e.button == 0 && overSbDiv) {
                    _sidebarResizing = true;
                    e.Use();
                } else if (_sidebarResizing) {
                    if (e.type == EventType.MouseDrag) {
                        _sidebarW = mouse.x - winRect.x; //SidebarWidth 内部会 clamp
                        e.Use();
                    } else if (e.type == EventType.MouseUp) {
                        _sidebarResizing = false;
                        _dragActive = false;
                        _dirty = true;
                        //拖动结束写回栏目宽度设置（0=自动时不动），保证重启后保持
                        try { if (_sidebarWEntry != null && _sidebarW > 0f) _sidebarWEntry.Value = Mathf.RoundToInt(_sidebarW); } catch (Exception __ex) { Guard.Log("保存栏目宽度", __ex); }
                    }
                }
            }
            //松手就解除别拖窗口：即使页面没机会清（比如拖到一半切了栏目）也不会卡住窗口拖动
            if (e.type == EventType.MouseUp) BlockWindowDrag = false;
            if (!_resizing) {
                if (e.type == EventType.MouseDown && over && e.button == 0) _downPos = mouse;
                if (_dragActive && e.type == EventType.MouseDrag) {
                    if (!_dragMoved && (mouse - _downPos).magnitude > 4f) _dragMoved = true;
                    if (_dragMoved) {
                        _winX = mouse.x - _dragOffset.x;
                        _winY = mouse.y - _dragOffset.y;
                        winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                        if (_winXEntry != null) _winXEntry.Value = Mathf.RoundToInt(_winX);   //写缓存条目（不再每个 MouseDrag 事件 Bind）
                        if (_winYEntry != null) _winYEntry.Value = Mathf.RoundToInt(_winY);
                        _dirty = true;
                    }
                }
                //页面里正在拖自己的东西（如联机列表的列宽）时不要拖窗口：
                //本函数在页面绘制之前跑，页面这帧才 e.Use()，来不及阻止 -> 用 BlockWindowDrag 显式告知
                // **必须限定左键**：侧栏排序用的是长按右键拖动，右键不该把窗口一起拖走
                if (e.type == EventType.MouseDrag && e.button == 0 && over && GUIUtility.hotControl == 0 && !_dragActive && !BlockWindowDrag) {
                    _dragActive = true;
                    _dragMoved = false;
                    _dragOffset = mouse - new Vector2(_winX, _winY);
                    _downPos = mouse;
                }
            }

            //淡入淡出 + 轻微缩放入场（布局本身已由 Sc() 缩放）
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _uiAlpha);
            float pop = 1f + (1f - _uiAlpha) * 0.05f;
            //先保存旧矩阵，再设缩放矩阵（顺序反了会让 385/534 的还原变成恢复成缩放矩阵）
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(pop, pop, 1f));
            // **绘制体包进 try/finally**：任何异常都必须还原 GUI.matrix/color/字体，
            //  否则游戏 HUD 会保持缩放或半透明（表现为"开界面后界面卡住"）。
            try {

            GUI.Box(winRect, GUIContent.none, _win);
            // **滚轮盾坐标系修复**：窗口区域内的 rect 是"区域局部坐标"，而鼠标事件是"屏幕坐标"，
            //  直接比较永远不命中（下拉框/滑块的滚轮优先于页面滚动就失效了）。这里记下区域偏移供盾换算。
            SR.AreaOffset = new Vector2(winRect.x + Sc(4), winRect.y + Sc(4));
            GUILayout.BeginArea(new Rect(winRect.x + Sc(4), winRect.y + Sc(4), winRect.width - Sc(8), winRect.height - Sc(8)));
            if (_winCollapsed) {
                DrawTitleBar(winRect.width - Sc(8));
                GUILayout.EndArea();
                GUI.matrix = prevMatrix;
                GUI.color = prevColor;
                GUI.skin.font = SR.Ctl.OrigSkinFont() ?? _prevFont;
                RunFrameEndHooks();
                return;
            }
            float barH = DrawTitleBar(winRect.width - Sc(8));
            GUILayout.Space(barH + Sc(2));
            GUILayout.BeginHorizontal();
            float sbw = SidebarWidth();
            //右栏内容区（屏幕坐标）：自绘页用它把控件铺满窗口（联机列表的表格框要随窗口变化）
            ContentArea = new Rect(winRect.x + Sc(4) + sbw + Sc(2), winRect.y + Sc(4),
                                   winRect.width - Sc(8) - sbw - Sc(2), winRect.height - Sc(8));
            GUILayout.BeginVertical(GUILayout.Width(sbw));
            Vector2 leftBefore = _leftScroll;
            _leftScroll = GUILayout.BeginScrollView(_leftScroll, GUIStyle.none, GUIStyle.none);   // **#4 侧栏滚动条已隐藏**
            if (_leftScroll != leftBefore) WheelFrame = Time.frameCount; //侧栏滚动区吃掉了滚轮
            for (int si = 0; si < _internalSections.Count; si++) {
                string s = _internalSections[si];
                bool sbSel = _mode == Mode.Internal && s == _selectedInternalSection;
                if (GUILayout.Button(ZhSection(s), sbSel ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.Internal;
                    _selectedInternalSection = s;
                    NoPageScrollBars = false;   //换栏目：页面级滚动条要求复位（由自绘页每帧重新声明）
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.Space(Sc(6));
            //外部插件与内部栏目之间画一条分隔线
            GUILayout.Box(GUIContent.none, _footer, GUILayout.Height(Sc(1)), GUILayout.ExpandWidth(true));
            GUILayout.Space(Sc(4));
            foreach (PluginEntry p in _externalPlugins) {
                //被禁用的插件在侧栏标出来（*，R412 由 [x] 改），避免"点了没反应"的错觉
                string label = (IsExternalDisabled(p.guid) ? "* " : "") + p.name;   // **R412**：[x] -> *
                if (GUILayout.Button(new GUIContent(label, p.guid + (IsExternalDisabled(p.guid) ? "\n" + T("已禁用（设置页可重新启用）", "disabled (re-enable in Settings)") : "")),
                        _mode == Mode.External && p.guid == _pluginKey ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.External;
                // 右键这一行即可绑定该插件的"启用/禁用"热键（不新增按钮；SR 系统 -> 组合键/序列都支持）
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
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (_mode == Mode.Settings) {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(T("恢复默认", "Defaults"), _btn, GUILayout.Width(Sc(84)))) {
                    if (_internalConfig != null) {
                        foreach (ConfigEntryBase ce in AllEntries(_internalConfig)) {
                            Guard.Try("恢复默认值 " + ce.Definition.Key, () => ce.BoxedValue = ce.DefaultValue);
                        }
                        Guard.Try("写盘保存配置", () => _internalConfig.Save());
                        // **P1-05**：这才是"全部恢复默认"该做的收尾。原来只清了 _editText / _editOpen，
                        //  于是：① 下拉框里的枚举缓存没清，界面仍显示旧选项（用户以为没生效，反复点）；
                        //  ② 键位被改回默认，但序列型快捷键的 _watchKeys 没置脏 -> 带"序列前键"的
                        //     快捷键会一直触发不了，要等下一次别的注册才恢复。
                        //  这里一次性补齐：编辑框 / 展开态 / 枚举下拉缓存 + 键位变更置脏。
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
            //功能页"外框"部件（滚动区之外）：功能自注册，管理器只在对应栏目被选中时调用
            //（会话内容页的顶部工具行就挂在这里；核心不再写 section == "Chat" 这种硬编码判断）
            //页签必须画在页面外框（顶栏）之前，否则顶栏会压在上面
            if (_mode == Mode.Internal) SR.RenderPageTabs(_selectedInternalSection);
            if (_mode == Mode.Internal) SR.RenderPageHeader(_selectedInternalSection);
            //鼠标悬停在下拉框上滚轮 = 换选项：先于滚动区把滚轮事件吞掉，免得页面跟着滚
            WheelShieldHit = false;   // **每帧先复位（仲裁命中时置 true）**
            // #1 真 bug 修复：这两项必须在盾**之前**取值, 盾命中时会把 e.type 改成 EventType.Used，
            //  之后再读就恒为 false，导致"第二道判定"从来没生效过。
            Event __we = Event.current;
            bool __wasWheel = __we != null && __we.type == EventType.ScrollWheel;
            Vector2 __wheelMouse = __we != null ? __we.mousePosition : Vector2.zero;
            // **帧首**：本帧声明 -> 上一帧声明（ImGui 的 HoveredIdPreviousFrameUsingMouseWheel）
            WheelClaimNewFrame();
            bool __overCtrlPrev = WheelClaimedPrev;
            if (__wasWheel && __overCtrlPrev) {
                __we.Use(); __we.type = EventType.Used; WheelShieldHit = true;
                //顺带记下本帧滚轮量：下拉框在 Repaint 趟取用一次后清掉（滚轮轴在该环境恒为 0，不能依赖它）
                WheelDeltaSet(-(Mathf.Abs(__we.delta.y) > 0.001f ? __we.delta.y : __we.delta.x));   // Unity 的 delta.y向下滚为正，取反以符合 ImGui 约定（向上为正）
                SR.MarkWheelUsed();
            }
            //自绘页（联机列表）要求不要页面级滚动条：把两条都换成 none（内容由页面自己铺满窗口）
            // #2/#5 Ctrl+滚轮：**只切当前栏目的页签**（第一/第二/第三...页），不再调整侧栏。
            if (__wasWheel && !SR.WheelShieldHit
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) {
                // **取用即清**：Ctrl 分支先把待用滚轮量拿走（拿走后下拉框本帧就拿不到了,
                //  Ctrl+滚轮的语义是切页，不该顺带改下拉框的值）。
                float cw = WheelDeltaTake();
                if (Mathf.Abs(cw) < 0.0001f) { try { cw = Input.GetAxis("Mouse ScrollWheel"); } catch { cw = 0f; } }
                if (Mathf.Abs(cw) > 0.0001f && _mode == Mode.Internal) {
                    CyclePage(_selectedInternalSection, cw > 0f ? -1 : 1);   //向上滚 = 前一页
                    if (__we != null) { __we.Use(); __we.type = EventType.Used; }
                    WheelShieldHit = true;   //本帧页面不跟着滚
                    SR.MarkWheelUsed();
                }
            }

            Vector2 pageBefore = _scroll;
            // **#1 用户方案**：鼠标悬浮在下拉框/滑块上时，本帧让滚动区"当作没有可滚动内容",
            //  用 none 样式 + 不显示滚动条，配合盾把事件类型改成 Used，双保险让页面不动。
            bool __noScroll = NoPageScrollBars || __overCtrlPrev || WheelShieldHit;
            _scroll = __noScroll
                ? GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none)
                : GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none);   // **#4 隐藏滚动条（滚轮照旧可用）**
            if (_scroll != pageBefore) WheelFrame = Time.frameCount; //页面滚动区吃掉了滚轮
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                //功能自绘页：各功能在自己的 SelfReg 里 SR.RegisterPage(section, render) 注册
                //（首页在 SR.SelfRegCore、实验/会话内容/快速调整/自由模式/关卡/建造增强/聊天窗口/联机都在各自文件里）。
                //这里原来是一串硬编码 if-else（6 个页面名直调各功能类）-> 已删除：新增功能不必再改 SR.Window。
                if (SR.HasPage(_selectedInternalSection)) {
                    SR.RenderPage(_selectedInternalSection);
                } else {
                    //兜底：纯通用条目列表（没有自绘页的栏目）
                    bool any = false;
                    foreach (ConfigEntryBase entry in InternalSectionEntries()) {
                        any = true;
                        RenderEntryRow(entry, true, colWidth);
                    }
                    if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
                }
            } else if (_mode == Mode.Settings) {
                RenderSettingsEntries(colWidth);   //外部插件清单由 RenderSettingsEntries 在界面分组之后渲染
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
            // 滚轮被下拉框/滑块吃掉 -> 撤销本帧页面滚动（彻底杜绝"控件滚轮带动页面"）。
            //  两道判定：1) 盾（绘制前、上一帧矩形命中）, 正常路径，无抖动；
            //            2) 本帧绘制后的控件矩形命中, 兜底，覆盖"布局刚变/列表刚开合"的漏网情况。
            if (WheelShieldHit || (__wasWheel && __overCtrlPrev)) {
                _scroll = pageBefore;
                WheelFrame = Time.frameCount;   //并让"用 Input 读轴"的缩放路径本帧让步
            }
            //功能页"外框"部件（滚动区之下）：会话内容页的输入行挂在这里
            if (_mode == Mode.Internal) SR.RenderPageFooter(_selectedInternalSection);
            GUILayout.BeginHorizontal(_footer);
            //按钮宽度按文本测量（英文比中文长，固定宽度会截断）
            float settingsW = _btn.CalcSize(new GUIContent(T("设置", "Settings"))).x + Sc(18);
            if (GUILayout.Button(T("设置", "Settings"), _mode == Mode.Settings ? _selItem : _btn, GUILayout.Width(settingsW))) {
                _mode = Mode.Settings;
                NoPageScrollBars = false;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
            }
            // **#1 内部版本号**：紧跟在"设置"按钮右侧（界面底部）
            // P2 原来这里只是 Sc(60) 占位间隔，版本号**从未被读过** -> 用户报问题无法确认是哪一版构建。
            GUILayout.FlexibleSpace();   // **按需求**：底栏不再显示 SR 内部版本号
            GUILayout.Label(ModeLabel(), _label, GUILayout.Width(Sc(160)), GUILayout.Height(Sc(26)));
            GUILayout.EndHorizontal();
            FooterHeight = GUILayoutUtility.GetLastRect().height;   //自绘页据此避让（见 ContentArea 注释）
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

                        // #1 整数对齐 + 尺寸与贴图完全一致（Sc(16) 取整）-> 消除锯齿；
            //颜色按 ImGui 的 ResizeGrip 三态（accent，透明度 0.25 / 0.7 / 0.95）。
            {
                int gs = Mathf.Max(8, Mathf.RoundToInt(Sc(21)));   //与贴图一致
                float galpha = _resizing ? 0.95f : (CursorResize && gripRect.Contains(mouse) ? 0.7f : 0.25f);
                Color oldC = GUI.color;
                GUI.color = new Color(0.30f, 0.49f, 1f, galpha);   //ImGui 主题的 accent_color
                GUI.DrawTexture(new Rect(Mathf.Round(winRect.xMax - gs), Mathf.Round(winRect.yMax - gs), gs, gs), _gripTex);
                GUI.color = oldC;
            }
            //自绘光标（游戏隐藏了系统光标）；悬停栏目分隔线时换成左右调整形状
            Texture2D curTex = CursorResize && _cursorResizeTex != null ? _cursorResizeTex
                : (CursorMove && _cursorMoveTex != null ? _cursorMoveTex : _cursorTex);
            if (curTex != null) GUI.DrawTexture(new Rect(mouse.x - Sc(7), mouse.y - Sc(7), Sc(15), Sc(15)), curTex);
            DrawTooltip(mouse);
            } finally {
                GUI.matrix = prevMatrix; GUI.color = prevColor; GUI.skin.font = SR.Ctl.OrigSkinFont() ?? _prevFont;
                // **G7**：把全局选择色也还原。GUI.skin 是共享皮肤（Configuration Manager 等也在用），
                //  只在 SR 绘制期间用我们的蓝色，绘制结束就还回去，避免污染别的 IMGUI 界面。
                RestoreSkinSelectionColor();
            }
            //自绘页本帧声明的鼠标形状在这里统一应用（并复位）：
            //页面没画（切走栏目）或本帧没声明 -> 自动回到"不强制"，特殊光标不会残留
            RunFrameEndHooks();
            }
            //功能覆盖层最后绘制，保证在最上层（各功能自己判断当前该不该画）
            DrawOverlays();
        }

// 分区：MonoBehaviour（ManagerUI：Update/LateUpdate/OnGUI 驱动入口）

        private class ManagerUI : MonoBehaviour {
            private bool _startup;

            private void OnEnable() {
                Camera.onPreCull += OnPreCullView;
            }

            private void OnDisable() {
                Camera.onPreCull -= OnPreCullView;
            }

            //渲染前应用锁定/地图取景（含透视相机，此处最后生效）。
            private static void OnPreCullView(Camera cam) {
                try {
                    if (cam == null) return;
                    //渲染前统一应用各功能注册的视图（地图取景 / 自由相机）。
                    //是否生效、对哪个相机生效，由功能自己在注册的方法里判断；
                    //一个都没注册时这里立即返回（默认状态下的每帧开销为零）。
                    ApplyCameraViews(cam);
                } catch (Exception __ex) { Guard.Log("地图/取景 onPreCull", __ex); }
            }

            private void Update() {
                //延迟到首帧：其他 tweak 可能在 Initialize 之后才注册配置，提前扫描会漏。
                if (!_startup) {
                    //任一子步骤失败不应永久跳过后续自检：逐句 Guard.Try，最后才置 _startup
                    Guard.Try("启动扫描", () => EnsureScanned());
                    Guard.Try("应用禁用插件", () => ApplyDisabledPlugins());
                    Guard.Try("门控自检", () => GateAudit.Run());
                    Guard.Try("反射自检", () => RefAudit());
                    _startup = true;
                }
                //（视野/地图的快捷键由功能自己用 SR.RegisterTick 注册；核心不再逐个点名）
                //（自由相机的滚轮缩放已挪到 OnGUI 末尾的 SR.PostGuiWheel：那里才能用上滚轮仲裁）
                // P1-06：每帧调度做**异常隔离**。原来这三步裸调，任意一步抛异常就会让同一帧
                //  排在它后面的步骤全部跳过 —— 表现为"某个功能一出错，一堆无关功能集体失灵"，
                //  而日志里只有一条难定位的堆栈。Guard.Try 自带 5 秒去重，不会刷屏。
                Guard.Try("每帧 Tick", () => SR.Tick());
                Guard.Try("面板开关键", () => SR.CheckOpenKey());
                Guard.Try("自动快捷键", () => SR.CheckHotkeys());
                //取景生效点：Camera.onPreCull（渲染前，最后生效）+ ZoomCamera.Update 后缀（游戏移动相机后立刻纠正）。
            }

            private void OnGUI() {
                // **P1-06**：DrawGUI 抛异常时原来会把 PostGuiWheel 一起跳过（滚轮仲裁当场失效），
                //  这里分开包，保证滚轮收口无论如何都能跑到。
                Guard.Try("绘制面板", () => SR.DrawGUI());
                //滚轮仲裁收口：放在 DrawGUI **之后**，这时面板/地图里所有界面件都已经处理过这一帧的滚轮
                //（谁吃掉谁 MarkWheelUsed），再用 Input 读轴的缩放路径就不会和界面抢滚轮了。
                Guard.Try("滚轮收口", () => SR.PostGuiWheel());
                // **R414**：点在下拉框以外的任何地方 = 收起展开的下拉框。
                //  判据是"MouseDown 这一帧没有任何下拉框自己认领"（见 SR.CloseOpenCombos）——
                //  不靠坐标，因为下拉框矩形在滚动区内容坐标系里，与这里的顶层坐标对不上。
                //  放在 DrawGUI 之后：按钮自己的开/关切换已经跑完，不会互相打架。
                //  CloseOpenCombos 已改成"先拷贝键再改值"（Dictionary 边枚举边写会抛 Collection was modified）。
                Event __ce = Event.current;
                if (__ce != null && __ce.type == EventType.MouseDown && __ce.button == 0 && _comboSeenFrame != Time.frameCount) CloseOpenCombos();

            }
        }

	}
}
