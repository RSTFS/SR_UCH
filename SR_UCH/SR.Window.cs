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
            //门控快照：每帧刷新一次（所有限制读同一份 → 同帧一致）
            RefreshGate();
            //加载后清理（配置在 Experiments、实现在 LevelTools）：FadeOut 后 1 秒执行一次
            LevelTools.TickCleanup();
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);
            //总开关/地图总开关关闭时强制退出已打开的地图
            if ((!GateMaster || !MapEnabled) && Freeplay.Visible) {
                Freeplay.Close();
            }
 
            //「冻结角色」（外部模块页控制，默认关）：只冻结自己的角色（见 FreezeLocalCharacter），不暂停全局时间 → 其他角色/动画照常移动，保留原版观感。
            if (_pauseApplied) {
                Time.timeScale = _pauseSavedTs;
                _pauseApplied = false;
            }
            if (Freeplay.Visible) {
                //地图上滚轮缩放：调整自由相机 FOV (视野页"当前 FOV"跟随)
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.0001f) {
                    FovAdjust.SetFov(FovAdjust.FovValue - wheel * 3f);
                }
            }
            //自动保存：拖动滑块/窗口时先不写，松手即保存（避免拖动中每帧写盘）
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

        //录制规则：最多三个键、每键只记一次、必须按住上一个再按下一个
        //（松开任意键即完成录制，所以序列只能是和弦式"按住链"）。
        private static void RecPushKey(KeyCode k) {
            if (k == KeyCode.None) return;
            if (_recSeq.Count >= 3) return;
            if (_recSeq.Contains(k)) return;
            _recSeq.Add(k);
            if (!_recHeld.Contains(k)) _recHeld.Add(k);
            _recLastAt = Time.unscaledTime;
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
                }
                try {
                    string s = "";
                    foreach (KeyCode k in _recSeq) s += (s.Length > 0 ? " → " : "") + k;
                    MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 保存快捷键 " + cap.Definition.Key + " = " + s);
                } catch { }
            } finally {
                _capturing = null;
                _recSeq.Clear();
                _dirty = true;
            }
        }

        //统一绑定入口（个别直接绑定鼠标键的路径使用）
        private static void BindCapturedKey(KeyCode kc, ComboMod mod) {
            ConfigEntryBase cap = _capturing;
            if (cap == null) return;
            try {
                if (cap.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                    //KeyboardShortcut 自带修饰键列表，把按住的修饰键一并写入
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
                _dirty = true;
            }
        }

            //标题栏用绝对布局；▼/▶ 折叠整个窗口；右端 = 总开关 + 关闭
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
            //关闭按钮左边：本 Mod 总开关（关闭时所有内部功能运行时失效，各功能开关值保持不变）
            float mw = Sc(96);
            float mx = barRect.width - Sc(32) - Sc(4) - mw;
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
            if (GUI.Button(new Rect(barRect.width - Sc(32), tY, Sc(26), Sc(26)), "✕", _btn)) CloseMenu();
            return barH;
        }

        private static void DrawGUI() {
            if (!_visible && _uiAlpha <= 0.01f && !Freeplay.Visible) return;
            _scaled = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            if (_visible || _uiAlpha > 0.01f) {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            //布局缩放：所有固定尺寸乘 Sc()，字号随之
            float scale = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            _scaled = scale;
            _prevFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            Event e = Event.current;
            Vector2 mouse = e.mousePosition; //无矩阵缩放，无需坐标换算

            //诊断：确认 OnGUI 是否收到右键按下（无日志 = 右键没到插件）
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

            //外部模块页按钮右键绑键由按钮绘制当帧自判（跨帧登记矩形会被状态栏增减行挤位移 → 绑错键）；
            //下拉框列表在布局流内下一行绘制，无需帧首吞事件。
            //快捷键录制：单键 / 修饰键+主键 / 多普通键序列；Esc 清空、Shift+Esc 放弃、点别处取消（松开按键即保存）。
            if (_capturing != null) {
                //IMGUI 的 MouseDown 对侧键(3/4)不一定触发，故用 Input.GetMouseButtonDown 独立检测
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
                } else if (e.type == EventType.MouseDown) {
                    //中键/侧键进序列；左键/右键不录（用于取消）
                    if (e.button >= 2 && e.button <= 6) {
                        RecPushKey(KeyCode.Mouse0 + e.button);
                        e.Use();
                        return;
                    }
                    if (Time.frameCount != _captureStartFrame) {
                        try { MainPlugin.ModLogger.LogInfo("[HotkeyDiag] 录制被鼠标取消 button=" + e.button); } catch { }
                        _capturing = null;
                        _recSeq.Clear();
                        _recHeld.Clear();
                    }
                }
                //结束条件：录到的键全部松开 → 立刻保存（所以必须是"按住链"）。
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
                _winWidth = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.x + (mouse.x - _resizeStart.x)), 400, 1200);
                _winHeight = Mathf.Clamp(Mathf.RoundToInt(_resizeStartSize.y + (mouse.y - _resizeStart.y)), 300, 1000);
                winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                gripRect = new Rect(winRect.xMax - Sc(20), winRect.yMax - Sc(20), Sc(20), Sc(20));
                _mp.Config.Bind("Settings", "Window Width", 720, "").Value = _winWidth;
                _mp.Config.Bind("Settings", "Window Height", 520, "").Value = _winHeight;
                _dirty = true;
                e.Use();
            }
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
                        //拖动结束写回「栏目宽度」设置（0=自动时不动），保证重启后保持
                        try { if (_sidebarWEntry != null && _sidebarW > 0f) _sidebarWEntry.Value = Mathf.RoundToInt(_sidebarW); } catch (Exception __ex) { Guard.Log("保存栏目宽度", __ex); }
                    }
                }
            }
            if (!_resizing) {
                if (e.type == EventType.MouseDown && over && e.button == 0) _downPos = mouse;
                if (_dragActive && e.type == EventType.MouseDrag) {
                    if (!_dragMoved && (mouse - _downPos).magnitude > 4f) _dragMoved = true;
                    if (_dragMoved) {
                        _winX = mouse.x - _dragOffset.x;
                        _winY = mouse.y - _dragOffset.y;
                        winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
                        _mp.Config.Bind("Settings", "Window X", 30f, "").Value = Mathf.RoundToInt(_winX);
                        _mp.Config.Bind("Settings", "Window Y", 30f, "").Value = Mathf.RoundToInt(_winY);
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

            //淡入淡出 + 轻微缩放入场（布局本身已由 Sc() 缩放）
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _uiAlpha);
            float pop = 1f + (1f - _uiAlpha) * 0.05f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(pop, pop, 1f));
            Matrix4x4 prevMatrix = GUI.matrix;

            GUI.Box(winRect, GUIContent.none, _win);
            GUILayout.BeginArea(new Rect(winRect.x + Sc(4), winRect.y + Sc(4), winRect.width - Sc(8), winRect.height - Sc(8)));
            if (_winCollapsed) {
                DrawTitleBar(winRect.width - Sc(8));
                GUILayout.EndArea();
                GUI.matrix = prevMatrix;
                GUI.color = prevColor;
                if (_prevFont != null) GUI.skin.font = _prevFont;
                return;
            }
            float barH = DrawTitleBar(winRect.width - Sc(8));
            GUILayout.Space(barH + Sc(2));
            GUILayout.BeginHorizontal();
            float sbw = SidebarWidth();
            GUILayout.BeginVertical(GUILayout.Width(sbw));
            _leftScroll = GUILayout.BeginScrollView(_leftScroll);
            foreach (string s in _internalSections) {
                if (GUILayout.Button(ZhSection(s), _mode == Mode.Internal && s == _selectedInternalSection ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                    _mode = Mode.Internal;
                    _selectedInternalSection = s;
                    _editText.Clear();
                    _editOpen.Clear();
                    _capturing = null;
                }
            }
            GUILayout.Space(Sc(6));
            //外部插件与内部栏目之间只画一条分隔线（不再用「外部」标签）
            GUILayout.Box(GUIContent.none, _footer, GUILayout.Height(Sc(1)), GUILayout.ExpandWidth(true));
            GUILayout.Space(Sc(4));
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
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
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
            //会话内容页：顶部工具行画在滚动区外，不随内容滚动
            if (_mode == Mode.Internal && _selectedInternalSection == "Chat") {
                ChatLog.RenderToolbar(); //本功能页在 ChatLog.cs
            }
            _scroll = GUILayout.BeginScrollView(_scroll);
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                if (_selectedInternalSection == "Home") {
                    RenderHomePage();
                } else if (_selectedInternalSection == "Experiments") {
                    Experiments.Render(); //本功能页在 Experiments.cs
                } else if (_selectedInternalSection == "Chat") {
                    ChatLog.Render(); //本功能页在 ChatLog.cs
                } else if (_selectedInternalSection == "Quick Adjust") {
                    QuickAdjust.Render(); //本功能页在 QuickAdjust.cs
                } else if (_selectedInternalSection == "Freeplay") {
                    Freeplay.Render(); //本功能页在 Freeplay.cs（地图/网格/视野/重生）
                } else if (_selectedInternalSection == "Level") {
                    LevelTools.Render(); //本功能页在 Level.cs
                } else if (SR.HasPage(_selectedInternalSection)) {
                    //功能自绘页（如 建造增强：含「建造上限」小分区）
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
                RenderSettingsEntries(colWidth);
            } else {
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
            }
            GUILayout.EndScrollView();
            //会话内容页：输入行画在滚动区外（渲染同在 ChatLog.cs）
            if (_mode == Mode.Internal && _selectedInternalSection == "Chat") {
                ChatLog.RenderInputRow();
            }
            GUILayout.BeginHorizontal(_footer);
            //按钮宽度按文本测量（英文比中文长，固定宽度会截断）
            float settingsW = _btn.CalcSize(new GUIContent(T("设置", "Settings"))).x + Sc(18);
            if (GUILayout.Button(T("设置", "Settings"), _mode == Mode.Settings ? _selItem : _btn, GUILayout.Width(settingsW))) {
                _mode = Mode.Settings;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(60));
            GUILayout.Label(ModeLabel(), _label, GUILayout.Width(Sc(160)), GUILayout.Height(Sc(26)));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.DrawTexture(new Rect(winRect.xMax - Sc(16), winRect.yMax - Sc(16), Sc(16), Sc(16)), _gripTex);
            //自绘光标（游戏隐藏了系统光标）
            GUI.DrawTexture(new Rect(mouse.x - Sc(7), mouse.y - Sc(7), Sc(15), Sc(15)), _cursorTex);
            DrawTooltip(mouse);
            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
            if (_prevFont != null) GUI.skin.font = _prevFont;
            }
            //地图窗口最后绘制，保证在最上层
            if (Freeplay.Visible) Freeplay.DrawMapWindow();
        }



// ==== 分区：MonoBehaviour（ManagerUI：Update/LateUpdate/OnGUI 驱动入口）====

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
                if (cam == null) return;
                //地图与自由相机都未激活：跳过相机查找（默认状态下的每帧开销）
                if (!Freeplay.Visible && !FovAdjust.LockView) return;
                Camera gc = FovAdjust.GameCamera();
                if (gc == null || cam != gc) return; //only the game camera, never UI cameras
                if (Freeplay.Visible) {
                    Freeplay.ApplyMapViewOnCamera(cam);
                    return;
                }
                FovAdjust.ApplyToCamera(cam);
            }

            private void Update() {
                //延迟到首帧：其他 tweak 可能在 Initialize 之后才注册配置，提前扫描会漏。
                if (!_startup) {
                    _startup = true;
                    EnsureScanned();
                    ApplyDisabledPlugins();
                    GateAudit.Run(); //启动自检（列出补丁目标 + 标出每帧方法）
                }
                FovAdjust.CheckKey(); //view hotkey works in every scene (no ZoomCamera needed)
                FovAdjust.TickInput(); //wheel zoom, once per frame
                SR.Tick();
                SR.CheckOpenKey();
                Freeplay.CheckMapKey();
                SR.CheckToggleKeys(); //外部模块页开关行的快捷键
                //取景生效点：Camera.onPreCull（渲染前，最后生效）+ ZoomCamera.Update 后缀（游戏移动相机后立刻纠正）。
            }

            private void OnGUI() {
                SR.DrawGUI();
            }
        }

	}
}

