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
public partial class SR : ITweak {

// ==== 分区：Core（核心状态 / 初始化 / 插件扫描）====
// 约定：静态字段集中在此；新功能实现 ITweak 即被自动发现，新分区需在 sr_uch.rsp 注册。

        private static MainPlugin _mp;
        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _blockInputEntry;
        private static ConfigEntry<float> _uiScaleEntry;
        private static ConfigEntry<string> _disabledPluginsEntry;
        private static ConfigEntry<bool> _allEnabledEntry;

        //对外（附加模块 DLL）暴露的注册接口版本：外部模块初始化时与自己的需求版本比对，不匹配就自行提示/降级。
        //SR 不认识任何具体外部模块，只提供版本号。
        public const int ApiVersion = 1;

        //本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值不变；初始默认关闭，改动自动保存
        public static bool AllEnabled = false;
        private static bool _appliedDisabled;
        //组合键修饰：自定义键位统一走这里，捕捉时按住的修饰键存为该键位的修饰键；
        //持久化格式 "Shift+Ctrl+Alt"（见 SR.KeyBinds 的 ParseComboMod/FormatComboMod）。
        [Flags]
        public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _keyMods = new Dictionary<ConfigEntryBase, ComboMod>();
        private static readonly Dictionary<ConfigEntryBase, ConfigEntry<string>> _keyModEntries = new Dictionary<ConfigEntryBase, ConfigEntry<string>>();
        private static bool _visible;
        private static float _uiAlpha; //open/close fade
        private static bool _scanned;
        private enum Mode { Internal, Settings, External }
        //internal = SR_UCH 栏目，settings = 独立设置页，external = 其它插件
        private static ConfigFile _internalConfig;
        private static readonly List<string> _internalSections = new List<string>();
        //侧栏栏目自注册表（各功能文件在自己的 SelfReg 里声明；见 SR.Nav / SR.NavHide）
        private static readonly List<string> _navOrder = new List<string>();                     //注册先后
        private static readonly Dictionary<string, int> _navRank = new Dictionary<string, int>(); //顺序值（越小越靠前）
        private static readonly HashSet<string> _navHidden = new HashSet<string>();              //有 cfg 段但不单独成栏目
        private static string _selectedInternalSection = "";
        private static Mode _mode = Mode.Internal;
        private static readonly List<PluginEntry> _externalPlugins = new List<PluginEntry>();
        private static string _pluginKey = "";
        private static Vector2 _scroll;
        public static bool HideChatWindow { get { return ChatLog.HideWindow; } }
        //文本测量缓存（控制台打开时避免每帧对所有条目 CalcSize/CalcHeight，减少掉帧）
        private static string _nameWKey = "";
        private static float _nameWCached;
        private static readonly Dictionary<string, float> _heightCache = new Dictionary<string, float>();
        //打开面板/地图时是否冻结自己的角色（外部模块页"冻结角色"开关控制；默认关 = 游戏照常运行）
        private static bool _pauseApplied;
        private static float _pauseSavedTs = 1f;
        private static Vector2 _leftScroll;
        //地图功能（视图/网格/状态/配置）都在 Freeplay.cs；这里只留管理器侧转发
        public static bool MapEnabled { get { return Freeplay.Enabled; } }
        private static bool _winCollapsed; //main window folded to just the title bar
        private static ConfigEntryBase _capturing; //key capture target
        //捕捉起始帧：右键进入捕捉的同一次 MouseDown 不能被"按下即取消捕捉"清掉，
        //否则捕捉在同一事件里开始又立刻结束（右键永远绑不上）。
        private static int _captureStartFrame = -1;
        private static float _hotkeyDiagAt0 = -99f; //诊断日志限速（OnGUI 入口打点）
        private static int _rmbConsumedFrame = -1; //当帧右键绑键：一帧只认一个按钮
        private static object _prevBoxed;
        //快捷键录制态：收集所有按键及其顺序（见 SR.Window 的录制分支 / CommitRecording）
        private static readonly List<KeyCode> _recSeq = new List<KeyCode>();
        private static readonly List<KeyCode> _recHeld = new List<KeyCode>(); //录制中仍按住的键（全部松开即完成录制）
        private static float _recLastAt;
        private static ConfigFile _dirtyConfig;
        private static readonly Dictionary<ConfigEntryBase, string> _editText = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, bool> _editOpen = new Dictionary<ConfigEntryBase, bool>();
        //window geometry
        private static int _winWidth = 720;
        private static int _winHeight = 520;
        private static float _winX = 30f;
        private static float _winY = 30f;
        private static bool _dragActive, _dragMoved, _resizing;
        private static Vector2 _dragOffset, _downPos, _resizeStart, _resizeStartSize;
        private static bool _dirty;
        //缩放同时作用于布局尺寸与字号（不使用矩阵缩放）
        private static float _scaled = 1f;
        private static float Sc(float v) { return v * _scaled; }
        private static UnityEngine.EventSystems.EventSystem _gatedEventSystem;
        private static bool _stylesReady;
        private static GUIStyle _win, _title, _titleLabel, _titleMid, _label, _nameLabel, _labelWrap, _chatLabel, _secHeader, _item, _selItem, _btn, _frame,
            _capture, _checkOn, _checkOff, _popup, _searchBox, _footer, _tooltip,
            _sliderTrack, _sliderFill, _sliderHandle, _sliderHandleHover, _sliderHandleActive;
        private static Font _font;
        private static Font _prevFont;
        private static Texture2D _gripTex;
        private static Texture2D _cursorTex;
        private static readonly List<GUIStyle> _styleList = new List<GUIStyle>();

        //管理器窗口是否真正打开（折叠 = 未打开，输入不受限）
        public static bool UiOpen { get { return _visible && !_winCollapsed; } }
        //地图窗口是否打开（打开时 FovAdjust 跳过自己的视图）
        public static bool MapOpen { get { return Freeplay.Visible; } }
        //freeze game input while the manager is open
        public static bool BlockInput = true;

        //「无视模式限制」「冻结角色」是外部模块的开关（配置/按键/界面都在那边）。
        //SR 侧只保留读取接点：外部模块初始化时调 SetExToggles 注册取值委托；未安装外部模块 = 都关闭（原版行为）。
        private static Func<bool> _ignoreModeLimitProvider;
        private static Func<bool> _freezeCharProvider;
        public static void SetExToggles(Func<bool> ignoreModeLimit, Func<bool> freezeChar) {
            _ignoreModeLimitProvider = ignoreModeLimit;
            _freezeCharProvider = freezeChar;
        }
        //无视模式限制：所有"仅自由模式"的门控（视野/地图/重生/关卡等）都读它
        public static bool IgnoreModeLimit { get { return _ignoreModeLimitProvider != null && _ignoreModeLimitProvider(); } }
        //冻结角色：打开面板/地图时冻结自己的角色
        public static bool PauseGame { get { return _freezeCharProvider != null && _freezeCharProvider(); } }

        //「无视房主限制」同样属于外部模块；SR 只保留读取接点（门控 OverrideHost 读它）。
        private static Func<bool> _hostOverrideProvider;
        public static void SetHostOverrideProvider(Func<bool> provider) { _hostOverrideProvider = provider; }
        //未安装外部模块 = false（保持原版"房主说了算"的行为）
        public static bool HostOverride { get { return _hostOverrideProvider != null && _hostOverrideProvider(); } }

        //树屋地图：允许在树屋大厅使用地图（地图功能主要自由模式；开启后树屋也能用）
        public static bool TreehouseMap { get { return Freeplay.TreehouseAllowed; } }

        //force-reset every UI state (used when a match starts so a stuck manager/map can
        //never hold up the snapshot-loading handshake for the whole lobby)
        public static void ForceResetUiState() {
            try {
                Freeplay.Close();
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _winCollapsed = false;
                //对局开始：关闭自由相机（避免残留；每次进对局恢复默认视角）
                try { FovAdjust.ForceDisableLock(); } catch (Exception __ex) { Guard.Log("对局开始时关闭自由相机", __ex); }
                ApplyEventSystemGate(); //re-enable the game's EventSystem
            } catch (Exception __ex) { Guard.Log("对局开始处理", __ex); }
        }

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            //界面文案自注册（各功能文件的 section/键名/悬浮说明；本处只派发 SR 分部文件，
            //    实现了 ITweak 的功能在自己的 Initialize 里注册）
            SelfRegCore();
            //**必须在任何 Config.Bind 之前**把旧 cfg 的中文 section/key 改写成英文；
            //改写后要让 ConfigFile 重新读盘，否则 BepInEx 内存里还是旧（中文）键，
            //新建的英文条目会拿默认值，而且下次写盘会把文件又写回中文 —— 等于迁移白做。
            try {
                if (ConfigMigration.Migrate(plugin.Config.ConfigFilePath)) {
                    plugin.Config.Reload();
                    MainPlugin.ModLogger.LogInfo("[T1] 已迁移旧版 cfg（section/key → 英文）并重新读盘");
                }
            } catch (Exception __ex) { Guard.Log("T1 cfg 迁移/重读", __ex); }
            _openKey = plugin.Config.Bind("Settings", "Open Key", KeyCode.Insert, "打开/关闭配置管理器（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            RegisterKey("管理器-开关", _openKey, "press");
            _blockInputEntry = plugin.Config.Bind("Settings", "Block Input", true, "打开管理器时冻结游戏输入");
            _uiScaleEntry = plugin.Config.Bind("Settings", "UI Scale", 1.3f, "界面整体缩放 (1.0 - 1.8)");
            //界面语言：中文 / English（运行时立即生效；外部模块页面始终中文；默认英文）
            _langEntry = plugin.Config.Bind("Settings", "Language", "English", "界面语言：中文 / English（外部模块页面始终中文）");
            _langEn = _langEntry.Value == "English";
            _langEntry.SettingChanged += (s, e) => _langEn = _langEntry.Value == "English";
            //地图总开关：默认关闭；开启后 M 键可打开地图，关闭时已打开的地图立即关闭。
            //地图网格（实验/Grid Always On）等独立功能不受本开关影响。
            //外部模块页的两个开关（无视模式限制 / 冻结角色）已随功能下放到外部模块（Bind 也在那边）
            _allEnabledEntry = plugin.Config.Bind("Settings", "All Enabled", false, "本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变。\n初始默认关闭；改动自动保存，下次启动保持上次状态。");
            //会话内容页的 3 个开关（Filter Quick Msgs / Hide Chat Window / Show Time）
            //已随功能下放到 ChatLog.cs（Bind 也在那边，第 8 条）
            //设置页「栏目宽度」：调节左侧栏目栏宽度（0 = 自动按文字宽度）。
            //也可直接在窗口里拖动侧栏右缘调整（拖动结果会写回本项，见 SR.Window）。
            _sidebarWEntry = plugin.Config.Bind("Settings", "Sidebar Width", 0, "左侧栏目栏宽度（像素，0 = 自动）。也可在窗口里直接拖动侧栏右缘调整。");
            _sidebarW = _sidebarWEntry.Value;
            _sidebarWEntry.SettingChanged += (s, e) => _sidebarW = _sidebarWEntry.Value;
            //「加载后清理」已按 T5 迁到 Experiments.cs（section 同时改为 Experiments，界面位置不变）
            _allEnabledEntry = plugin.Config.Bind("Settings", "All Enabled", false, "本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变。\n初始默认关闭；改动自动保存，下次启动保持上次状态。");
            AllEnabled = _allEnabledEntry.Value;
            _allEnabledEntry.SettingChanged += (s, e) => AllEnabled = _allEnabledEntry.Value;
            //外部插件默认启用（不再默认禁用）；用户在「外部」栏手动禁用的 GUID 记入
            //本禁用列表（deny-list），重启后保持禁用；不在列表内的插件默认启用。
            _disabledPluginsEntry = plugin.Config.Bind("Settings", "Disabled Plugins", "", "被禁用的外部插件 GUID（分号分隔；不在列表内的外部插件默认启用）");
            BlockInput = _blockInputEntry.Value;
            _blockInputEntry.SettingChanged += (s, e) => {
                BlockInput = _blockInputEntry.Value;
                ApplyEventSystemGate();
            };
            _winWidth = Mathf.Clamp(plugin.Config.Bind("Settings", "Window Width", 720, "").Value, 400, 1200);
            _winHeight = Mathf.Clamp(plugin.Config.Bind("Settings", "Window Height", 520, "").Value, 300, 1000);
            _winX = plugin.Config.Bind("Settings", "Window X", 30f, "").Value;
            _winY = plugin.Config.Bind("Settings", "Window Y", 30f, "").Value;

            GameObject go = new GameObject("SR_UCHManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ManagerUI>();
            //scene switches: reset EVERY UI state so nothing leaks into the next scene.
            //a manager left open freezes characters (input block), stops GameControl
            //(countdown/power-ups freeze) and disables the EventSystem (no clicks) -
            //this used to happen when joining another player's treehouse mid-session.
            SceneManager.activeSceneChanged += (a, b) => {
                _stylesReady = false;
                Freeplay.InvalidateBounds();
                Freeplay.Close();
                if (_visible) {
                    _visible = false;
                    _capturing = null;
                    _editText.Clear();
                    _editOpen.Clear();
                    _winCollapsed = false;
                    ApplyEventSystemGate(); //re-enable the game's EventSystem
                }
            };
            //NOTE: EnsureScanned/ApplyDisabledPlugins are NOT called here - other tweaks
            //may initialize after us, so the scan runs on the manager's first Update frame
            Harmony.CreateAndPatchAll(typeof(SR));
        }

        //--- scan: split SR_UCH (internal) from every other plugin (external) ---
        //侧栏栏目自注册 —— 每个功能声明自己的栏目与顺序（SR.Core 不再硬编码 order 数组，
        //也不再写 `sec == "xxx"` 的跳过分支）：
        //    SR.Nav("Player Tracker", 20);   // 出现在侧栏（顺序值越小越靠前）
        //    SR.NavHide("Camera");           // 有 cfg 段但条目并入别的页面，不单独成栏目
        public static void Nav(string sec, int rank) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Remove(sec);
            if (!_navRank.ContainsKey(sec)) _navOrder.Add(sec);
            _navRank[sec] = rank;
        }

        public static void NavHide(string sec) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Add(sec);
            _navOrder.Remove(sec);
            _navRank.Remove(sec);
        }

        private static void EnsureScanned() {
            if (_scanned) return;
            _scanned = true;
            foreach (var kv in Chainloader.PluginInfos) {
                PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                PluginEntry pe = new PluginEntry {
                    guid = kv.Key,
                    name = info.Metadata != null && info.Metadata.Name != null ? info.Metadata.Name : kv.Key,
                    config = info.Instance.Config,
                    instance = info.Instance
                };
                if (kv.Key == "com.gamingbeast.SR_UCH") {
                    _internalConfig = pe.config;
                    //回填组合键持久化：各功能 Initialize 早于本扫描，运行时组合修饰
                    //可能已设置但未落盘（当时 _internalConfig 为 null）——现在补写。
                    try {
                        foreach (var kme in new Dictionary<ConfigEntryBase, ComboMod>(_keyMods)) {
                            SetKeyComboMod(kme.Key, kme.Value);
                        }
                    } catch (Exception __ex) { Guard.Log("重新应用按键修饰键", __ex); }
                    continue;
                }
                _externalPlugins.Add(pe);
            }
            _externalPlugins.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            //侧栏栏目由各功能文件自注册（SR.Nav / SR.NavHide），这里只做「按顺序值排序 + 兜底」
            List<string> registered = new List<string>();
            foreach (string s in _navOrder) {
                if (!_navHidden.Contains(s)) registered.Add(s);
            }
            registered.Sort((a, b) => {
                int r = _navRank[a].CompareTo(_navRank[b]);
                if (r != 0) return r;
                return _navOrder.IndexOf(a).CompareTo(_navOrder.IndexOf(b)); //同顺序值：按注册先后
            });
            foreach (string s in registered) {
                if (!_internalSections.Contains(s)) _internalSections.Add(s);
            }
            //兜底：既没注册也没 NavHide 的 cfg 段（例如附加模块自己新加的段、老 cfg 残留段）排在注册项之后
            if (_internalConfig != null) {
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    string sec = e.Definition.Section;
                    if (string.IsNullOrEmpty(sec)) sec = "(General)";
                    if (_navHidden.Contains(sec) || _navRank.ContainsKey(sec)) continue;
                    if (!_internalSections.Contains(sec)) _internalSections.Add(sec);
                }
            }
            if (_internalSections.Count > 0) _selectedInternalSection = _internalSections[0];
            if (_externalPlugins.Count > 0) _pluginKey = _externalPlugins[0].guid;
            ApplyDisabledPlugins(); //re-disable plugins from the last session
        }

        private static void CloseMenu() {
            _visible = false;
            _capturing = null;
            var dc = _dirtyConfig;
            _dirtyConfig = null;
            _dirty = false;
            //关闭菜单时保存配置：失败记日志，不再静默/抛异常
            Guard.Try("关闭菜单时保存配置", () => { if (dc != null) dc.Save(); });
            ApplyEventSystemGate();
        }

        //「本 Mod 总开关」在设置页不重复成行（搜索栏右侧已有专用按钮）
        private static bool AllEnabledRowHidden(ConfigEntryBase e) {
            return !(e.Definition.Section == "Settings" && e.Definition.Key == "All Enabled");
        }

        //管理器自己的界面文案与侧栏注册
        private static void SelfRegCore() {
            SR.RowFilters.Add(AllEnabledRowHidden); //设置页不重复显示「本 Mod 总开关」（搜索栏右侧有专用按钮）
            SR.LocSec("Settings", "设置", null);
            SR.NavHide("Settings"); //T7：设置页是独立顶层页（不占内部侧栏栏目）
            SR.NavHide("Saved Lobby Details"); //T7：已删除的功能段（老 cfg 残留也不显示）
            SR.LocSec("Home", "首页", null);
            SR.Nav("Home", 10); //T7：本功能自己的侧栏栏目（顺序 10）
            SR.LocSec("Freeplay", "自由模式", null);
            SR.LocKey("Freeplay", "Map Key", "地图按键", null);
            SR.LocDesc("Freeplay", "Map Key", "打开/关闭地图窗口（俯视图，可看全图/设重生点；仅自由模式可用，实验栏可解锁树屋；挑战模式对局内禁用）", "Open/close the map window (top view: full level / spawn points; freeplay only unless unlocked in Experiments; disabled in Challenge matches)");
            SR.LocKey("Settings", "UI Scale", "界面缩放", null);
            SR.LocDesc("Settings", "UI Scale", "界面整体缩放大小（1.0 = 100%，1.3 默认）", "UI zoom (1.0 = 100%, 1.3 default)");
            SR.LocKey("Settings", "Disabled Plugins", "禁用的外部插件", null);
            SR.LocDesc("Settings", "Disabled Plugins", "被禁用的外部插件（GUID 列表，分号分隔；不在列表内的外部插件默认启用）", "Disabled external plugins (GUID list, semicolon-separated; plugins not on the list are enabled by default)");
            SR.LocKey("Settings", "Open Key", "打开键", null);
            SR.LocDesc("Settings", "Open Key", "打开/关闭配置管理器（默认 Insert）", "Open/close the config manager (default Insert)");
            SR.LocKey("Settings", "Block Input", "冻结输入", null);
            SR.LocDesc("Settings", "Block Input", "打开管理器时冻结游戏输入（防止误操作角色）", "Freeze game input while the manager is open (prevents accidental character control)");
            SR.LocKey("Settings", "Window Width", "窗口宽度", null);
            SR.LocDesc("Settings", "Window Width", "管理器窗口宽度（400 - 1200）", "Manager window width (400 - 1200)");
            SR.LocKey("Settings", "Window Height", "窗口高度", null);
            SR.LocDesc("Settings", "Window Height", "管理器窗口高度（300 - 1000）", "Manager window height (300 - 1000)");
            SR.LocKey("Settings", "Window X", "窗口X", null);
            SR.LocDesc("Settings", "Window X", "管理器窗口 X 坐标（屏幕左上角为原点）", "Manager window X (origin at top-left of screen)");
            SR.LocKey("Settings", "Window Y", "窗口Y", null);
            SR.LocDesc("Settings", "Window Y", "管理器窗口 Y 坐标（屏幕左上角为原点）", "Manager window Y (origin at top-left of screen)");
            SR.LocKey("Settings", "Sidebar Width", "栏目宽度", null);
            SR.LocDesc("Settings", "Sidebar Width", "左侧栏目栏宽度（像素，0 = 自动；100-320）。也可在窗口里直接拖动侧栏右缘调整。", "Left sidebar width in pixels (0 = auto; 100-320). You can also drag the sidebar's right edge.");
            SR.LocKey("Freeplay", "Treehouse Map", "树屋地图", null);
            SR.LocDesc("Freeplay", "Treehouse Map", "树屋地图：在树屋大厅也能打开地图（M 键开，左键拖拽平移，T 传送到鼠标位置）", "Treehouse map: open the map in the treehouse lobby (M to open, drag to pan, T to teleport to cursor)");
            SR.LocKey("Freeplay", "Map Enabled", null, "Map Master Switch");
            SR.LocDesc("Freeplay", "Map Enabled", "地图总开关：关闭后无法打开地图窗口（M 键无效），已打开的地图立即关闭。\n「地图网格」「树屋地图」等独立功能不受影响。", "Map master switch: OFF disables opening the map (M key does nothing); an open map closes immediately.\nIndependent features like Map grid / Treehouse map are not affected.");
            SR.LocKey("Settings", "Language", "界面语言", null);
        }



// ==== 分区：Guard（可观测性：统一带日志的 try/catch 包装）====
// 关键设计：同一条 what 5 秒内只记一次，故每帧路径上的 catch（相机、方块高亮等）也能安全接入而不刷屏日志。
// 反射探测型 catch（探测可选游戏成员，成员不存在是常态）不接入——那是预期分支，不是错误。
        public static class Guard {
            // 执行一个动作；异常时记 Warning（带 what 标识），不抛出。
            public static void Try(string what, Action action) {
                try { action(); }
                catch (Exception ex) { Warn(what, ex); }
            }

            // 执行一个取值动作；异常时记 Warning 并返回 fallback。
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); }
                catch (Exception ex) { Warn(what, ex); return fallback; }
            }

            //供把 `catch { }` 改造成 `catch (Exception __ex) { Guard.Log("...", __ex); }` 用（不改动原有 try 结构）。
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            //去重时间窗（秒）
            private const float RepeatWindowSeconds = 5f;
            private static readonly Dictionary<string, float> _lastWarn = new Dictionary<string, float>();

            private static void Warn(string what, Exception ex) {
                try {
                    float now = Time.unscaledTime;
                    float last;
                    if (what != null && _lastWarn.TryGetValue(what, out last) && now - last < RepeatWindowSeconds) return;
                    if (what != null) _lastWarn[what] = now;
                    MainPlugin.ModLogger.LogWarning("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch { }
            }
        }

	}
}

