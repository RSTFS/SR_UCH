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
public partial class ModManager {

// ==== 分区：Window（主窗口框架：Tick 主循环 / 标题栏 / DrawGUI / 窗口拖动缩放）====

        private static void Tick() {
            //延迟加载后清理：FadeOut 后 1 秒执行 GC.Collect + UnloadUnusedAssets（不阻塞过渡）
            if (_pendingCleanupAt >= 0f && Time.unscaledTime >= _pendingCleanupAt) {
                _pendingCleanupAt = -1f;
                try {
                    _lastCleanedScene = _pendingCleanupScene;
                    System.GC.Collect();
                    Resources.UnloadUnusedAssets();
                } catch { }
            }
            //进度解锁：未达标时锁定项强制复位为 false（每分钟检查一次，避免频繁读存档；
            //内部按 A/B 组分别判断是否已解锁，已解锁的组不再复位）
            _progCheckTimer += Time.unscaledDeltaTime;
            if (_progCheckTimer >= 60f) {
                _progCheckTimer = 0f;
                ForceLockedConfigs();
            }
            //fade in/out for open/close
            _uiAlpha = Mathf.Clamp01(_uiAlpha + (_visible ? 8f : -8f) * Time.unscaledDeltaTime);
            //总开关 / 地图总开关关闭时强制退出已打开的地图（运行时关闭开关的场景）
            if ((!AllEnabled || !MapEnabled) && _mapVisible) {
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
            //自动保存：改动后 1 秒内写盘（点击即存；点底部"保存"按钮也可立即保存）
            if (_dirty && Time.unscaledTime - _lastSave > 1f) {
                _lastSave = Time.unscaledTime;
                _dirty = false;
                if (_dirtyConfig != null) _dirtyConfig.Save();
                _dirtyConfig = null;
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

            Rect winRect = new Rect(_winX, _winY, _winWidth, _winHeight);
            bool over = winRect.Contains(mouse);
            Rect gripRect = new Rect(winRect.xMax - Sc(20), winRect.yMax - Sc(20), Sc(20), Sc(20));
            if (_winCollapsed) {
                winRect.height = Sc(32); //collapsed: title bar only
                gripRect = new Rect(0, 0, 0, 0);
            }

            //key capture: next keypress binds, Esc clears to (未设置), Shift+Esc cancels
            //(restores the previous value), click cancels.
            //组合键：按主键时若同时按住 Shift/Ctrl/Alt，则绑定为组合键（如 Shift+P），
            //修饰键持久化到隐藏配置；显示为 "Shift + P"。所有自定义键位都支持。
            //注意：纯修饰键（Shift/Ctrl/Alt 自身）不作为主键——先按住 Ctrl 再按 P 时，
            //Ctrl 的 KeyDown 事件被跳过，等 P 到达时才绑定 Ctrl+P（避免绑成 Ctrl+Ctrl）。
            if (_capturing != null) {
                if (e.type == EventType.KeyDown) {
                    if (e.keyCode == KeyCode.Escape) {
                        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) {
                            //Shift+Esc = cancel, keep the old binding
                            if (_prevBoxed != null) {
                                try { _capturing.BoxedValue = _prevBoxed; } catch { }
                            }
                        } else {
                            //Esc = clear the binding
                            if (_capturing.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                                try { _capturing.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(KeyCode.None); } catch { }
                            } else {
                                SetValue(_capturing, KeyCode.None);
                            }
                            //清空组合修饰
                            SetKeyComboMod(_capturing, ComboMod.None);
                        }
                        _capturing = null;
                        _dirty = true;
                        e.Use();
                    } else if (e.keyCode != KeyCode.None) {
                        //跳过纯修饰键（它们只是组合键的前缀，不作为主键）
                        if (e.keyCode == KeyCode.LeftShift || e.keyCode == KeyCode.RightShift ||
                            e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl ||
                            e.keyCode == KeyCode.LeftAlt || e.keyCode == KeyCode.RightAlt) {
                            e.Use(); //吃掉修饰键事件，继续等待主键
                            return;
                        }
                        //检测按住的主修饰键（Shift > Ctrl > Alt 优先级）
                        ComboMod mod = ComboMod.None;
                        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mod = ComboMod.Shift;
                        else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mod = ComboMod.Ctrl;
                        else if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mod = ComboMod.Alt;
                        if (_capturing.SettingType == typeof(BepInEx.Configuration.KeyboardShortcut)) {
                            try { _capturing.BoxedValue = new BepInEx.Configuration.KeyboardShortcut(e.keyCode); } catch { }
                            //KeyboardShortcut 自含修饰，无需额外记录
                        } else {
                            SetValue(_capturing, e.keyCode);
                            SetKeyComboMod(_capturing, mod);
                        }
                        _capturing = null;
                        _dirty = true;
                        e.Use();
                    }
                } else if (e.type == EventType.MouseDown) {
                    _capturing = null;
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
            //right: search + entries + footer
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            //搜索标签：宽度按文本测量（英文 "Search" 比中文长）
            float searchLabelW = _label.CalcSize(new GUIContent(T("搜索", "Search"))).x + Sc(10);
            GUILayout.Label(T("搜索", "Search"), _label, GUILayout.Width(searchLabelW), GUILayout.Height(Sc(26)));
            _search = GUILayout.TextField(_search, _searchBox, GUILayout.Width(Sc(220)), GUILayout.Height(Sc(26)));
            GUILayout.FlexibleSpace();
            //本 Mod 总开关（搜索栏右侧）
            if (GUILayout.Button(new GUIContent(AllEnabled ? T("总开关：开", "Master ON") : T("总开关：关", "Master OFF"),
                T("本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变", "Mod master switch: off disables all internal features at runtime")),
                AllEnabled ? _selItem : _btn, GUILayout.Width(Sc(96)), GUILayout.Height(Sc(26)))) {
                AllEnabled = !AllEnabled;
                if (_allEnabledEntry != null) {
                    _allEnabledEntry.Value = AllEnabled; //写入配置
                    try { _allEnabledEntry.ConfigFile.Save(); } catch { } //自动保存
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
                            try { ce.BoxedValue = ce.DefaultValue; } catch { }
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
            _scroll = GUILayout.BeginScrollView(_scroll);
            ConfigFile curConfig = _mode == Mode.Internal
                ? _internalConfig
                : (_mode == Mode.External && CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null);
            if (_mode == Mode.Internal) {
                if (_selectedInternalSection == "首页") {
                    RenderHomePage();
                } else if (_selectedInternalSection == "更多玩家") {
                    RenderMorePlayersConsole();
                } else if (_selectedInternalSection == "模组联机") {
                    RenderModMCConsole();
                } else if (_selectedInternalSection == "尝试计数") {
                    RenderAttemptCounterConsole();
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
                } else {
                    //视野 page: read-only 当前 FOV box on top (order: 当前FOV, 自由相机, FOV滑块, 按键)
                    if (_selectedInternalSection == "视野") {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(T("当前 FOV", "Current FOV"), _label, GUILayout.Width(colWidth), GUILayout.Height(Sc(26)));
                        GUILayout.FlexibleSpace();
                        bool oldEn = GUI.enabled;
                        GUI.enabled = false;
                        GUILayout.TextField(FovAdjust.CurrentFov().ToString("0.0"), _searchBox, GUILayout.Width(Sc(90)), GUILayout.Height(Sc(26)));
                        GUI.enabled = oldEn;
                        GUILayout.EndHorizontal();
                        GUILayout.Space(Sc(2));
                    }
                    bool any = false;
                    bool blDiv = false; //建造增强页内“建造上限”小分区的分隔标题只画一次
                    bool respDiv = false; //重生页内“重生”分区标题
                    bool spawnDiv = false; //重生页内“重生点”分区标题
                    foreach (ConfigEntryBase entry in InternalSectionEntries()) {
                        //重生页：按功能分两个分区（重生 / 重生点）
                        if (entry.Definition.Section == "Respawn") {
                            string rk = entry.Definition.Key;
                            if (!respDiv && (rk == "Enabled" || rk == "Spawn Immunity" || rk == "Delay")) {
                                respDiv = true;
                                GUILayout.Space(Sc(6));
                                GUILayout.Label("— " + T("重生", "Respawn") + " —", _secHeader);
                                GUILayout.Space(Sc(2));
                            }
                            if (!spawnDiv && (rk == "Spawn Points Enabled" || rk == "Set Spawn Key" || rk == "Respawn Key" || rk == "Reset Spawn Keys")) {
                                spawnDiv = true;
                                GUILayout.Space(Sc(6));
                                GUILayout.Label("— " + T("重生点", "Spawn Points") + " —", _secHeader);
                                GUILayout.Space(Sc(2));
                            }
                        }
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
                    GUILayout.Label(dis ? T("（已禁用，重启后仍禁用）", "(disabled, stays disabled after restart)") : T("（已启用）· 按键可点击重绑（支持 Shift/Ctrl/Alt 组合）", "(enabled) · keys can be clicked to rebind (Shift/Ctrl/Alt combos supported)"), _label, GUILayout.Height(Sc(26)));
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
                    WrapLabel(T("补丁卸载/恢复立即生效；关卡内已存在的对象和状态需重新进入关卡才会完全刷新。", "Patches apply immediately; existing objects need a level reload to fully refresh."));
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
            //footer: settings shortcut (bottom-left) + save/reload
            GUILayout.BeginHorizontal(_footer);
            //按钮宽度按文本测量（英文 "Settings"/"Reload" 比中文长，固定宽度会截断）
            float settingsW = _btn.CalcSize(new GUIContent(T("设置", "Settings"))).x + Sc(18);
            float saveW = _btn.CalcSize(new GUIContent(T("保存", "Save"))).x + Sc(18);
            float reloadW = _btn.CalcSize(new GUIContent(T("重新加载", "Reload"))).x + Sc(18);
            if (GUILayout.Button(T("设置", "Settings"), _mode == Mode.Settings ? _selItem : _btn, GUILayout.Width(settingsW))) {
                _mode = Mode.Settings;
                _editText.Clear();
                _editOpen.Clear();
                _capturing = null;
            }
            GUILayout.Space(Sc(8));
            if (GUILayout.Button(T("保存", "Save"), _btn, GUILayout.Width(saveW))) {
                _dirty = false;
                if (_dirtyConfig != null) _dirtyConfig.Save();
                if (curConfig != null) curConfig.Save();
                _dirtyConfig = null;
            }
            if (GUILayout.Button(T("重新加载", "Reload"), _btn, GUILayout.Width(reloadW))) {
                if (curConfig != null) curConfig.Reload();
                _editText.Clear();
                _editOpen.Clear();
            }
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
