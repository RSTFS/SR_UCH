using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Window（主窗口框架：Tick 主循环 / 标题栏 / DrawGUI / 窗口拖动缩放）====

        private static void Tick() {
            //门控快照：每帧刷新一次（所有限制读同一份 → 同帧一致；进度解锁走缓存不读存档）
            RefreshGate();
            //延迟加载后清理：FadeOut 后 1 秒执行 GC.Collect + UnloadUnusedAssets（不阻塞过渡）
            if (_pendingCleanupAt >= 0f && Time.unscaledTime >= _pendingCleanupAt) {
                _pendingCleanupAt = -1f;
                try {
                    _lastCleanedScene = _pendingCleanupScene;
                    System.GC.Collect();
                    Resources.UnloadUnusedAssets();
                } catch (Exception __ex) { Guard.Log("加载后清理(GC)", __ex); }
            }
            //（已移除）进度解锁的“每分钟强制复位”：改为纯运行时门控，见 SR.Progression.cs 顶部说明。
            //fade in/out for open/close
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);
            //总开关 / 地图总开关关闭时强制退出已打开的地图（运行时关闭开关的场景）
            if ((!GateMaster || !MapEnabled) && _mapVisible) {
                ExitMapView();
                _mapVisible = false;
            }
            //map open/close animation
            if (_visible || _mapVisible) ApplyEventSystemGate();
            //「冻结角色」：EX 页选择框控制（默认关 = 打开面板/地图时游戏照常运行）。
            //开启后打开面板/地图只冻结**自己**的角色（见 FreezeLocalCharacter），
            //不暂停全局时间：其他玩家角色/动画照常移动，保留游戏原版观感。
            //（旧语义"暂停游戏"的 timeScale=0 已移除——那会连其他角色一起定住。）
            if (_pauseApplied) {
                Time.timeScale = _pauseSavedTs;
                _pauseApplied = false;
            }
            if (_mapVisible) {
                //wheel zoom on the map: adjusts the free-camera FOV (视野页"当前 FOV"跟随)
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.0001f) {
                    FovAdjust.SetFov(FovAdjust.FovValue - wheel * 3f);
                }
            }
            //自动保存：修改配置后立即写盘（拖动滑块/窗口时先不写，松手即保存，避免拖动过程每帧写盘）
            if (_dirty && !Input.GetMouseButton(0) && !Input.GetMouseButton(1)) {
                _dirty = false;
                if (_dirtyConfig != null) {
                    var dc = _dirtyConfig;
                    _dirtyConfig = null;
                    //配置写盘失败以前是静默的（表现为“改了设置重启就没了”）；统一记日志
                    Guard.Try("配置保存", () => dc.Save());
                }
            }
        }

        //录制：每按下一个键记一个。规则：
        //  · **最多三个键**（再多按不再记）；
        //  · 每个键最多出现一次（Q→Q→W 不允许，只能 Q→W）；
        //  · **必须按住上一个再按下一个**：因为"松开任意键即完成录制"（见结束条件），
        //    序列只可能由"按住链"构成（和弦式），松手就定型。
        private static void RecPushKey(KeyCode k) {
            if (k == KeyCode.None) return;
            if (_recSeq.Count >= 3) return;   //最多三个键
            if (_recSeq.Contains(k)) return;  //同键只记一次
            _recSeq.Add(k);
            if (!_recHeld.Contains(k)) _recHeld.Add(k);
            _recLastAt = Time.unscaledTime;
        }

        //松手检测：把已经松开的键从"仍按住"集合里去掉（全部松开 → 立刻完成录制并保存）
        private static void RecCheckRelease() {
            for (int i = _recHeld.Count - 1; i >= 0; i--) {
                bool held;
                try { held = Input.GetKey(_recHeld[i]); } catch { held = false; }
                if (!held) _recHeld.RemoveAt(i);
            }
        }

        //结束录制并保存：最后一个键 = 主键；开头连续的修饰键 = 组合修饰；中间其余键 = 序列前键。
        //这样"单键 / Shift+1 / Shift+Q+W / Q+W"都是同一条存储格式。
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
                    //外部模组的 KeyboardShortcut：只支持 主键+修饰键，序列前键不适用（保留主键与修饰）
                    List<KeyCode> mk = new List<KeyCode>();
                    if ((mods & ComboMod.Shift) != 0) mk.Add(KeyCode.LeftShift);
                    if ((mods & ComboMod.Ctrl) != 0) mk.Add(KeyCode.LeftControl);
                    if ((mods & ComboMod.Alt) != 0) mk.Add(KeyCode.LeftAlt);
                    try { cap.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(main, mk.ToArray()); } catch (Exception __ex) { Guard.Log("写入组合键(KeyboardShortcut)", __ex); }
                } else {
                    SetValue(cap, main);
                    SetKeyComboMod(cap, mods);            //先写修饰键
                    SetKeySeqExtra(cap, extras);          //再写序列前键（合并进同一个隐藏条目）
                }
                try {
                    string s = "";
                    foreach (KeyCode k in _recSeq) s += (s.Length > 0 ? " → " : "") + k;
                    MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 保存快捷键 " + cap.Definition.Key + " = " + s);
                } catch { }
            } finally {
                _capturing = null;
                _pendingModKey = KeyCode.None;
                _recSeq.Clear();
                _dirty = true;
            }
        }

        //统一绑定入口（保留给个别直接绑定鼠标键的旧路径用）
        private static void BindCapturedKey(KeyCode kc, ComboMod mod) {
            ConfigEntryBase cap = _capturing;
            if (cap == null) return;
            try {
                if (cap.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                    //BepInEx 的 KeyboardShortcut 自带"修饰键列表"，这里把按住的修饰键一并写进去（支持 Ctrl+Alt+X）
                    List<KeyCode> mods = new List<KeyCode>();
                    if ((mod & ComboMod.Shift) != 0) mods.Add(KeyCode.LeftShift);
                    if ((mod & ComboMod.Ctrl) != 0) mods.Add(KeyCode.LeftControl);
                    if ((mod & ComboMod.Alt) != 0) mods.Add(KeyCode.LeftAlt);
                    try { cap.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(kc, mods.ToArray()); } catch (Exception __ex) { Guard.Log("写入组合键(KeyboardShortcut)", __ex); }
                } else {
                    SetValue(cap, kc);
                    SetKeyComboMod(cap, mod);
                }
                try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 绑定 " + cap.Definition.Key + " = " + kc + " 修饰=" + mod); } catch { }
            } finally {
                _capturing = null;
                _pendingModKey = KeyCode.None;
                _dirty = true;
            }
        }

        //main window title bar (absolute layout; the ▼/▶ triangle folds the whole window)
        private static float DrawTitleBar(float width) {
            float barH = Sc(26);
            Rect barRect = new Rect(0, 0, width, barH);
            GUI.Box(barRect, GUIContent.none, _title);
            float tY = (barH - Sc(26)) / 2f;
            if (GUI.Button(new Rect(Sc(4), tY, Sc(26), Sc(26)), _winCollapsed ? "▶" : "▼", _btn)) {
                _winCollapsed = !_winCollapsed;
            }
            GUI.Label(new Rect(Sc(34), tY, Sc(90), Sc(26)), "SR＿UCH", _titleLabel);
            GUI.Label(new Rect(Sc(124), tY, barRect.width - Sc(160), Sc(26)), T("INS开关界面&悬停条目查看说明", "INS: manager · hover entries for tooltips"), _titleMid);
            if (GUI.Button(new Rect(barRect.width - Sc(32), tY, Sc(26), Sc(26)), "✕", _btn)) CloseMenu();
            return barH;
        }

        private static void DrawGUI() {
            if (!_visible && _uiAlpha <= 0.01f && !_mapVisible) return;
            _scaled = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            if (_visible || _uiAlpha > 0.01f) {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            //layout is scaled by multiplying every fixed size by Sc(); the font size follows
            float scale = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            _scaled = scale;
            _prevFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            Event e = Event.current;
            Vector2 mouse = e.mousePosition; //no matrix scale, so no conversion needed

            //诊断（临时）：确认 SR 的 OnGUI 是否收到右键按下（没有这条日志 = 右键根本没到插件）
            if (e.type == EventType.MouseDown && e.button == 1 && Time.realtimeSinceStartup - _hotkeyDiagAt0 > 0.5f) {
                _hotkeyDiagAt0 = Time.realtimeSinceStartup;
                try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] OnGUI 收到右键 pos=" + e.mousePosition + " 窗口=(" + _winX + "," + _winY + "," + _winWidth + "," + _winHeight + ") 模式=" + _mode + " 栏目=" + _selectedInternalSection); } catch { }
            }

            Rect winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
            bool over = winRect.Contains(mouse);
            Rect gripRect = new Rect(winRect.xMax - Sc(20), winRect.yMax - Sc(20), Sc(20), Sc(20));
            if (_winCollapsed) {
                winRect.height = Sc(32); //collapsed: title bar only
                gripRect = new Rect(0, 0, 0, 0);
            }

            //EX 按钮右键绑键：改为"HOTKEY 按钮画完当帧自判"（见 SR.Pages.TryBindByRightClick），
            //不再跨帧登记矩形（跨帧矩形会被上方状态栏的增减行挤位移 → 右键绑错键）。
            //下拉框展开中的点击处理：必须在页面内容之前拦截，否则点击会被浮层下面的控件先吃掉。
            //（_combo* 是上一帧渲染时记录的，本帧此刻仍有效；本帧稍后 BeginScrollView 前才清空重记。）
            //下拉框展开列表改为"布局流内下一行"绘制（见 SR.Settings.DrawComboListInline），
            //不再需要帧首帧首命中检测/吞事件，也就没有穿透问题。
            //=== 快捷键录制态 ===
            //进入这个状态后**收集所有按键及其顺序**，保存为一整条快捷键：
            //  · 单个键       → 单键
            //  · 修饰键+主键  → 组合键（如 Shift → 1，等价于旧的 Shift+1）
            //  · 多个普通键   → 序列键（如 9 → 0）
            //结束方式：Enter 立即保存 / 静默 0.8 秒自动保存（松手即生效）/ Backspace 删掉最后一个 /
            //          Esc 清空绑定、Shift+Esc 放弃改动 / 左键点击别处取消。
            if (_capturing != null) {
                //鼠标侧键检测（IMGUI 的 MouseDown 事件对侧键 button 3/4 不一定触发，
                //用 Input.GetMouseButtonDown 独立检测侧键1(button3=Mouse4)、侧键2(button4=Mouse5)）
                int sideBtn = -1;
                try {
                    if (Input.GetMouseButtonDown(3)) sideBtn = 3;      //侧键1 (Mouse4)
                    else if (Input.GetMouseButtonDown(4)) sideBtn = 4; //侧键2 (Mouse5)
                } catch { sideBtn = -1; }
                if (sideBtn >= 0) {
                    RecPushKey(KeyCode.Mouse0 + sideBtn); //鼠标键也进序列（Mouse3 / Mouse4）
                    if (Event.current != null) Event.current.Use();
                    return;
                }
                if (e.type == EventType.KeyDown) {
                    if (e.keyCode == KeyCode.Escape) {
                        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) {
                            //Shift+Esc = 放弃这次改动，恢复原值
                            if (_prevBoxed != null) {
                                try { _capturing.BoxedValue = _prevBoxed; } catch (Exception __ex) { Guard.Log("恢复组合键原值", __ex); }
                            }
                        } else {
                            //Esc = 清空绑定
                            if (_capturing.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                                try { _capturing.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.None); } catch (Exception __ex) { Guard.Log("清空组合键", __ex); }
                            } else {
                                SetValue(_capturing, KeyCode.None);
                                SetKeyComboMod(_capturing, ComboMod.None);
                                SetKeySeqExtra(_capturing, null);
                            }
                        }
                        _capturing = null;
                        _pendingModKey = KeyCode.None;
                        _recSeq.Clear();
                        _recHeld.Clear();
                        _dirty = true;
                        e.Use();
                    } else if (e.keyCode != KeyCode.None) {
                        //所有其它键都作为录制内容（不再有"Enter 保存"这类保留键；Esc 仍用于清空/放弃）
                        RecPushKey(e.keyCode);
                        e.Use();
                    }
                } else if (e.type == EventType.MouseDown) {
                    //鼠标中键(2)及侧键(3+)作为序列中的一个键；左键(0)/右键(1)不录（用于取消）
                    if (e.button >= 2 && e.button <= 6) {
                        RecPushKey(KeyCode.Mouse0 + e.button);
                        e.Use();
                        return;
                    }
                    if (Time.frameCount != _captureStartFrame) {
                        try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 录制被鼠标取消 button=" + e.button); } catch { }
                        _capturing = null;
                        _pendingModKey = KeyCode.None;
                        _recSeq.Clear();
                        _recHeld.Clear();
                    }
                }
                //录制结束条件（用户要求：**松开按键即完成保存**，不等待、不需要 Enter）：
                //  · 录到的键全部松开 → 立刻保存；
                //  · 因此按键必须是"按住链"：先按住第一个，再按第二个（最多三个），最后松手完成。
                if (_capturing != null && _recSeq.Count > 0) {
                    RecCheckRelease();
                    if (_recHeld.Count == 0) CommitRecording();
                }
            }
            //resize grip (hidden while collapsed)
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
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.x + (mouse.x - _resizeStart.x)), 400, 1200);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.y + (mouse.y - _resizeStart.y)), 300, 1000);
                winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                gripRect = new Rect(winRect.xMax - Sc(20), winRect.yMax - Sc(20), Sc(20), Sc(20));
                _mp.Config.Bind("设置", "Window Width", 720, "").Value = _winWidth;
                _mp.Config.Bind("设置", "Window Height", 520, "").Value = _winHeight;
                _dirty = true;
                e.Use();
            }
            //侧栏右缘：鼠标按住拖动可调侧栏宽度
            if (!_resizing) {
                float sbDivX = winRect.x + SidebarWidth();
                if (e.type == EventType.MouseDown && e.button == 0 && Mathf.Abs(mouse.x - sbDivX) < Sc(5)) {
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
                        //拖动结束把结果写回「栏目宽度」设置，保证重启后保持（0=自动时不动）
                        try { if (_sidebarWEntry != null && _sidebarW > 0f) _sidebarWEntry.Value = Mathf.RoundToInt(_sidebarW); } catch (Exception __ex) { Guard.Log("保存栏目宽度", __ex); }
                    }
                }
            }
            //drag by empty space
            if (!_resizing) {
                if (e.type == EventType.MouseDown && over && e.button == 0) _downPos = mouse;
                if (_dragActive && e.type == EventType.MouseDrag) {
                    if (!_dragMoved && (mouse - _downPos).magnitude > 4f) _dragMoved = true;
                    if (_dragMoved) {
                        _winX = mouse.x - _dragOffset.x;
                        _winY = mouse.y - _dragOffset.y;
                        winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                        _mp.Config.Bind("设置", "Window X", 30f, "").Value = Mathf.RoundToInt(_winX);
                        _mp.Config.Bind("设置", "Window Y", 30f, "").Value = Mathf.RoundToInt(_winY);
                        _dirty = true;
                    }
                }
                if (e.type == EventType.MouseDrag && over && GUIUtility.hotControl == 0 && !_dragActive) {
                    _dragActive = true;
                    _dragMoved = false;
                    _dragOffset = mouse - new Vector2(_winX, _winY);
                    _downPos = mouse;
                }
            }

            //fade in/out scale-in effect (layout itself is already scaled by Sc())
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _uiAlpha);
            float pop = 1f + (1f - _uiAlpha) * 0.05f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(pop, pop, 1f));
            Matrix4x4 prevMatrix = GUI.matrix;

            GUI.Box(winRect, GUIContent.none, _win);
            GUILayout.BeginArea(new Rect(winRect.x + Sc(4), winRect.y + Sc(4), winRect.width - Sc(8), winRect.height - Sc(8)));
            //collapsed: only the title bar remains (▼/▶ toggles it); no fake cursor
            if (_winCollapsed) {
                DrawTitleBar(winRect.width - Sc(8));
                GUILayout.EndArea();
                GUI.matrix = prevMatrix;
                GUI.color = prevColor;
                if (_prevFont != null) GUI.skin.font = _prevFont;
                return;
            }
            //title bar (absolute layout so the text is never clipped)
            float barH = DrawTitleBar(winRect.width - Sc(8));
            GUILayout.Space(barH + Sc(2));
            GUILayout.BeginHorizontal();
            //left: SR＿UCH sections, then external plugins (width adapts to content)
            float sbw = SidebarWidth();
            GUILayout.BeginVertical(GUILayout.Width(sbw));
            GUILayout.Label(T("内部", "Internal"), _secHeader);
            _leftScroll = GUILayout.BeginScrollView(_leftScroll);
            foreach (string s in _internalSections) {
                if (s == "EX" && !ExRef.Loaded) continue; //未安装附加模块：隐藏该栏目
                if (GUILayout.Button(ZhSection(s), _mode == Mode.Internal && s == _selectedInternalSection ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.Internal;
                    _selectedInternalSection = s;
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.Space(Sc(8));
            GUILayout.Label(T("外部", "External"), _secHeader);
            foreach (PluginEntry p in _externalPlugins) {
                if (GUILayout.Button(new GUIContent(p.name, p.guid), _mode == Mode.External && p.guid == _pluginKey ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.External;
                    _pluginKey = p.guid;
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.Space(Sc(2));
            //right: entries + footer（搜索已移除：条目过滤对中文界面无意义，直接删掉省一行）
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            //本 Mod 总开关（右上角）
            if (GUILayout.Button(new GUIContent(AllEnabled ? T("总开关：开", "Master ON") : T("总开关：关", "Master OFF"),
                T("本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变", "Mod master switch: off disables all internal features at runtime")),
                AllEnabled ? _selItem : _btn, GUILayout.Width(Sc(96)), GUILayout.Height(Sc(26)))) {
                AllEnabled = !AllEnabled;
                if (_allEnabledEntry != null) {
                    _allEnabledEntry.Value = AllEnabled; //写入配置
                    Guard.Try("总开关自动保存", () => _allEnabledEntry.ConfigFile.Save()); //自动保存（失败记日志）
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //设置 page: restore-defaults button on top
            if (_mode == Mode.Settings) {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(T("恢复默认", "Defaults"), _btn, GUILayout.Width(Sc(84)))) {
                    if (_internalConfig != null) {
                        foreach (ConfigEntryBase ce in AllEntries(_internalConfig)) {
                            Guard.Try("恢复默认值 " + ce.Definition.Key, () => ce.BoxedValue = ce.DefaultValue);
                        }
                        _internalConfig.Save();
                        _editText.Clear();
                        _editOpen.Clear();
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(T("恢复所有 SR＿UCH 配置为默认值", "Reset all SR＿UCH settings to defaults"), _label);
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(4));
            }
            float colWidth = EntryNameWidth();
            //会话内容页：顶部工具行固定在滚动区外，不随内容滚动
            if (_mode == Mode.Internal && _selectedInternalSection == "会话内容") {
                RenderChatLogToolbar();
            }
            _scroll = GUILayout.BeginScrollView(_scroll);
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                if (_selectedInternalSection == "首页") {
                    RenderHomePage();
                } else if (_selectedInternalSection == "EX") {
                    RenderCultivationConsole();
                } else if (_selectedInternalSection == "实验") {
                    RenderExperimentsConsole();
                } else if (_selectedInternalSection == "会话内容") {
                    RenderChatLog();
                } else if (_selectedInternalSection == "快速调整") {
                    RenderQuickAdjustConsole();
                } else if (_selectedInternalSection == "地图") {
                    RenderMapPage();
                } else if (_selectedInternalSection == "关卡") {
                    RenderLevelPage();
                } else {
                    //（已删除）原“视野”分区分支与“Respawn”分区分支：永远不可达——
                    //  SR.Core 已把「视野」「Respawn」两个 config section 从侧栏栏目 continue 掉，
                    //  它们的条目由地图页（RenderMapPage，界面显示为“自由模式”）渲染
                    //  （提示词第 7 节“死代码”）。同时删掉只服务于它们的 respDiv/spawnDiv。
                    bool any = false;
                    bool blDiv = false; //建造增强页内“建造上限”小分区的分隔标题只画一次
                    foreach (ConfigEntryBase entry in InternalSectionEntries()) {
                        //建造增强: the toggle keys are merged into the override rows below
                        if (entry.Definition.Section == "Builder Enhancements" && IsBuilderToggleKey(entry.Definition.Key)) continue;
                        //建造增强页内的“建造上限”分区：先画分隔标题 + 当前生效值，再画这两行
                        if (entry.Definition.Section == "Builder Enhancements" &&
                            (entry.Definition.Key == "解除建造上限" || entry.Definition.Key == "上限数值")) {
                            if (!blDiv) {
                                blDiv = true;
                                GUILayout.Space(Sc(6));
                                GUILayout.Label(new GUIContent("— " + T("建造上限", "Build Limit") + " —",
                                    T("建造上限（BuildUnlimiter）：解除树屋保存/发布界面的关卡满度限制，超满的关卡也能正常发布/上传。",
                                      "Build Limit (BuildUnlimiter): lifts the treehouse save/publish fullness cap so over-full levels can be published.")),
                                    _secHeader);
                                GUILayout.Space(Sc(2));
                                //当前生效值（只读，实时跟随开关/数值）
                                GUILayout.BeginHorizontal();
                                GUILayout.Label(new GUIContent(T("当前上限", "Current limit"),
                                    T("当前生效的满度上限；游戏原版为 500", "The current effective fullness cap; vanilla is 500")),
                                    _label, GUILayout.Width(colWidth), GUILayout.Height(Sc(26)));
                                GUILayout.FlexibleSpace();
                                bool oldEn2 = GUI.enabled;
                                GUI.enabled = false;
                                GUILayout.TextField(T("当前上限：" + BuildUnlimiter.CurrentLimit(), "Current: " + BuildUnlimiter.CurrentLimit()),
                                    _searchBox, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(26)));
                                GUI.enabled = oldEn2;
                                GUILayout.EndHorizontal();
                                GUILayout.Space(Sc(2));
                            }
                        }
                        any = true;
                        RenderEntryRow(entry, true, colWidth);
                    }
                    if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
                }
            } else if (_mode == Mode.Settings) {
                RenderSettingsEntries(colWidth);
            } else {
                //external plugin: enable/disable toggle on top (keys are editable below)
                PluginEntry curExt = CurrentExternalPlugin();
                if (curExt != null) {
                    bool dis = IsExternalDisabled(curExt.guid);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(dis ? T("启用插件", "Enable plugin") : T("禁用插件", "Disable plugin"), _btn, GUILayout.Width(Sc(84)))) ToggleExternalPlugin(curExt);
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
                        GUILayout.Label("— " + sec + " —", _secHeader);
                    }
                    RenderEntryRow(entry, false, colWidth);
                }
                if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
                //external plugin note (inside the scroll area so it is always reachable)
                if (_mode == Mode.External && CurrentExternalPlugin() != null) {
                    GUILayout.Space(Sc(4));
                    WrapLabel(T("补丁卸载/恢复立即生效", "Patch unload/restore applies immediately"));
                }
            }
            GUILayout.EndScrollView();
            //会话内容页：输入行固定在滚动区外（窗口下部，不随消息滚动），稍微上移留出间距；支持 Enter 发送
            if (_mode == Mode.Internal && _selectedInternalSection == "会话内容") {
                GUILayout.Space(Sc(10));
                GUILayout.BeginHorizontal();
                //Enter/小键盘 Enter 发送：必须在 TextField 绘制**之前**拦截——IMGUI 单行 TextField
                //会消费 KeyDown Return 事件（提交并失焦），之后检测 Event.current 已失效；这里在
                //控件处理前捕获并 Use()，发送后焦点保持，可连续发送。空文本不发送。
                if (Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                    && _chatInputText.Length > 0) {
                    ChatLog.SendChatText(_chatInputText);
                    _chatInputText = "";
                    Event.current.Use();
                }
                GUI.SetNextControlName("SRUCH_ChatInput");
                _chatInputText = GUILayout.TextField(_chatInputText, _searchBox, GUILayout.Height(Sc(26)));
                if (GUILayout.Button(new GUIContent(T("发送", "Send"), T("把输入内容作为聊天消息发到游戏聊天里", "Send the text as a chat message")), _btn, GUILayout.Width(Sc(56)), GUILayout.Height(Sc(26)))) {
                    ChatLog.SendChatText(_chatInputText);
                    _chatInputText = "";
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(4));
            }
            //footer: settings shortcut (bottom-left) + current page label
            GUILayout.BeginHorizontal(_footer);
            //按钮宽度按文本测量（英文比中文长，固定宽度会截断）
            float settingsW = _btn.CalcSize(new GUIContent(T("设置", "Settings"))).x + Sc(18);
            if (GUILayout.Button(T("设置", "Settings"), _mode == Mode.Settings ? _selItem : _btn, GUILayout.Width(settingsW))) {
                _mode = Mode.Settings;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
            }
            //「保存」与「重读配置」都已删除：
            //  · 保存没有作用——BepInEx 的 ConfigFile 默认 SaveOnConfigSet=true，改任何一项都会立即写盘，
            //    本 mod 另有"松开鼠标即保存"的自动写盘（见 Tick）；
            //  · 重读配置（config.Reload）只有"在游戏外手改 .cfg 后同步"这一种用途，属边缘场景，按用户要求一并删除。
            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(60));
            GUILayout.Label(ModeLabel(), _label, GUILayout.Width(Sc(160)), GUILayout.Height(Sc(26)));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.DrawTexture(new Rect(winRect.xMax - Sc(16), winRect.yMax - Sc(16), Sc(16), Sc(16)), _gripTex);
            //fake mouse cursor (the game hides the system cursor)
            GUI.DrawTexture(new Rect(mouse.x - Sc(7), mouse.y - Sc(7), Sc(15), Sc(15)), _cursorTex);
            DrawTooltip(mouse);
            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
            if (_prevFont != null) GUI.skin.font = _prevFont;
            } //end main window
            //map view is drawn last so it sits on top (fade in/out animation)
            if (_mapVisible) DrawMapWindow();
        }

	}
}
