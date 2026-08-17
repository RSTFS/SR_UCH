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
public partial class ModManager : ITweak {

// ==== 分区：Core（核心状态 / 初始化 / 插件扫描）====
// 约定：所有静态字段集中在此（状态仓库）；其余分区文件只放行为方法。
// 新增功能：新建 XxxFeature.cs 实现 ITweak 即可被自动发现；新增分区 = 新建 ModManager.XXX.cs 并在 sr_uch.rsp 注册。

        private static MainPlugin _mp;
        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _blockInputEntry;
        private static ConfigEntry<float> _uiScaleEntry;
        private static ConfigEntry<string> _disabledPluginsEntry;
        private static ConfigEntry<bool> _ignoreModeLimitEntry;
        private static ConfigEntry<bool> _freezeCharEntry;
        private static ConfigEntry<bool> _gcAfterLoadEntry;
        private static ConfigEntry<bool> _allEnabledEntry;
        //附加模块代理（SR_UCH_EX 注册进来；未安装则附加页隐藏）
        private static object _cultivationProxy;
        public static bool CultivationLoaded { get { return _cultivationProxy != null; } }
        //附加模块在初始化时调用，把它的实例交给 SR_UCH 渲染附加页
        public static void RegisterCultivation(object cultivation) {
            _cultivationProxy = cultivation;
            if (!_internalSections.Contains("EX")) _internalSections.Add("EX");
        }
        //本 Mod 总开关（首页设置）：关闭时所有内部功能运行时失效，各功能开关值保持不变。
        //初始默认关闭；改动写入配置自动保存（_allEnabledEntry）
        public static bool AllEnabled = false;
        private static bool _appliedDisabled;
        //keys that require Shift (registered via RegisterShiftKey) - shown as "Shift + X"
        private static readonly HashSet<ConfigEntryBase> _shiftKeys = new HashSet<ConfigEntryBase>();
        //组合键修饰：ConfigEntry<KeyCode> → 所需修饰键（Shift/Ctrl/Alt/无）。
        //所有自定义键位统一走这里：捕捉时按住修饰键一起按 → 存为该键位的修饰键；
        //不再需要单独的"修饰键"下拉框。持久化见 _keyModEntry。
        public enum ComboMod { None, Shift, Ctrl, Alt }
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _keyMods = new Dictionary<ConfigEntryBase, ComboMod>();
        private static readonly Dictionary<ConfigEntryBase, ConfigEntry<string>> _keyModEntries = new Dictionary<ConfigEntryBase, ConfigEntry<string>>();
        private static bool _visible;
        private static float _uiAlpha; //open/close fade
        private static bool _scanned;
        private enum Mode { Internal, Settings, External }
        //internal = SR_UCH (shown as sections), settings = the independent 设置 page,
        //external = every other plugin
        private static ConfigFile _internalConfig;
        private static readonly List<string> _internalSections = new List<string>();
        private static string _selectedInternalSection = "";
        private static Mode _mode = Mode.Internal;
        private static readonly List<PluginEntry> _externalPlugins = new List<PluginEntry>();
        private static string _pluginKey = "";
        private static string _search = "";
        private static Vector2 _scroll;
        private static ConfigEntry<bool> _chatFilterQuickEntry;
        private static bool _chatFilterQuick; //会话内容页：过滤快捷消息（表情/预设消息不显示；配置持久化）
        private static ConfigEntry<bool> _hideChatEntry;
        private static bool _hideChat; //会话内容页：隐藏游戏内聊天窗口（消息气泡/输入框不显示；配置持久化）
        public static bool HideChatWindow { get { return _hideChat; } }
        private static bool _chatShowTime = true; //会话内容页：每条消息前显示具体时间（默认开）
        private static string _chatTextCache; //编辑框内容缓存（每秒重建一次，避免每帧拼接字符串）
        private static float _chatTextTimer;
        private static bool _chatCacheFilter, _chatCacheShowTime; //缓存对应的开关状态（变化立即重建）
        private static string _chatInputText = ""; //会话内容页底部：发送聊天消息的编辑框内容
        //文本测量缓存（控制台打开时避免每帧对所有条目 CalcSize/CalcHeight，减少掉帧）
        private static string _nameWKey = "";
        private static float _nameWCached;
        private static readonly Dictionary<string, float> _heightCache = new Dictionary<string, float>();
        //打开面板/地图时是否冻结自己的角色（EX 页"冻结角色"开关控制；默认关 = 游戏照常运行）
        private static bool _pauseApplied;
        private static float _pauseSavedTs = 1f;
        private static Vector2 _leftScroll;
        //map state: overhead view uses the MAIN camera (exactly like BetterFreeplay)
        private static ConfigEntry<KeyCode> _mapKey;
        private static ConfigEntry<bool> _mapEnabledEntry; //地图总开关（地图页顶部；关闭后 M 键无法打开地图）
        private static bool _mapVisible;

        //地图总开关：关闭时 M 键无法打开地图窗口，已打开的地图立即关闭（Tick 强制退出）。
        //树屋地图/地图网格等由各自开关控制，不随本开关联动。
        public static bool MapEnabled { get { return _mapEnabledEntry != null && _mapEnabledEntry.Value; } }

        //地图内操作的短暂提示（unscaled 计时，暂停时也显示）
        private static string _mapToast = "";
        private static float _mapToastUntil;
        private static bool _winCollapsed; //main window folded to just the title bar
        //附加"地图传送"模式: open the map, click a spot -> teleport the target there
        private static bool _mapTeleportTarget;
        //实验页统计缓存
        private static string _statTextCache = "";
        private static string _cheatFlagText = "";
        private static float _statsAutoTimer; //读取统计页每秒自动刷新
        private static bool _cacheLangEn; //缓存生成时的语言（切换语言后强制刷新缓存）
        //地图左键拖拽平移视角（世界偏移量，与滚轮缩放解耦）
        private static bool _mapDragActive;
        private static bool _mapDragMoved;
        private static Vector2 _mapDragLastScreen;
        private static Vector3 _mapDragOffset = Vector3.zero;
        private static bool _ctxOpen; //右键菜单已移除，保留字段兼容旧判断
        private static int _activePoint = -1; //selected custom spawn point in the editor
        private static bool _camSaved;
        private static float _progCheckTimer; //进度解锁强制复位检查计时（每分钟一次）
        private static Vector3 _savedCamPos;
        private static Quaternion _savedCamRot;
        private static float _savedOrtho;
        private static float _savedNear;
        private static float _savedFar;
        private static float _savedFov; //perspective: the map view fixes FOV for a stable fit
        private static ConfigEntryBase _capturing; //key capture target
        private static object _prevBoxed;
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
        private static float _lastSave;
        //UI scale applied to layout (方案 B: layout and font both scale, no matrix scale)
        private static float _scaled = 1f;
        private static float Sc(float v) { return v * _scaled; }
        //EventSystem gate
        private static UnityEngine.EventSystems.EventSystem _gatedEventSystem;
        //styles
        private static bool _stylesReady;
        private static GUIStyle _win, _title, _titleLabel, _titleMid, _label, _nameLabel, _labelWrap, _chatLabel, _secHeader, _item, _selItem, _btn, _frame,
            _capture, _checkOn, _checkOff, _popup, _searchBox, _footer, _tooltip,
            _sliderTrack, _sliderFill, _sliderHandle, _sliderHandleHover, _sliderHandleActive;
        private static Font _font;
        private static Font _prevFont;
        private static Texture2D _gripTex;
        private static Texture2D _cursorTex;
        private static readonly List<GUIStyle> _styleList = new List<GUIStyle>();

        //true while the manager window is open (collapsed = not open, so input stays free)
        public static bool UiOpen { get { return _visible && !_winCollapsed; } }
        //true while the map editor is open (FovAdjust skips its view while this is on)
        public static bool MapOpen { get { return _mapVisible; } }
        //freeze game input while the manager is open
        public static bool BlockInput = true;

        //附加模块控制台的"无视模式限制"：勾选后所有"仅自由模式"限制都被无视
        //(视野/地图/重生/附加功能在任何模式下都可用)
        public static bool IgnoreModeLimit = false;

        //打开面板/地图时冻结自己的角色（EX 页"冻结角色"开关控制；默认关 = 游戏照常运行、自己也能动）
        public static bool PauseGame = false;

        //force-reset every UI state (used when a match starts so a stuck manager/map can
        //never hold up the snapshot-loading handshake for the whole lobby)
        public static void ForceResetUiState() {
            try {
                if (_mapVisible) ExitMapView();
                _mapVisible = false;
                _mapTeleportTarget = false;
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _winCollapsed = false;
                //挑战对局开始：关闭自由相机（挑战模式禁用自由相机/地图，避免残留）
                try { FovAdjust.ForceDisableLock(); } catch { }
                ApplyEventSystemGate(); //re-enable the game's EventSystem
            } catch { }
        }


        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            _openKey = plugin.Config.Bind("设置", "Open Key", KeyCode.Insert, "打开/关闭配置管理器（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            RegisterKey("管理器-开关", _openKey, "press");
            _blockInputEntry = plugin.Config.Bind("设置", "Block Input", true, "打开管理器时冻结游戏输入");
            _uiScaleEntry = plugin.Config.Bind("设置", "UI Scale", 1.3f, "界面整体缩放 (1.0 - 1.8)");
            //界面语言：中文 / English（运行时立即生效；EX 附加页始终中文；默认英文）
            _langEntry = plugin.Config.Bind("设置", "Language", "English", "界面语言：中文 / English（EX 附加页始终中文）");
            _langEn = _langEntry.Value == "English";
            _langEntry.SettingChanged += (s, e) => _langEn = _langEntry.Value == "English";
            _mapKey = plugin.Config.Bind("地图", "Map Key", KeyCode.M, "打开/关闭地图窗口（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            RegisterKey("地图-开关", _mapKey, "press");
            //地图总开关：默认开启；关闭后 M 键无法打开地图，已打开的地图立即关闭。
            //地图网格（实验/Grid Always On）与同步循环等独立功能不受本开关影响。
            _mapEnabledEntry = plugin.Config.Bind("地图", "地图总开关", true, "地图总开关：关闭后无法打开地图窗口（M 键无效），已打开的地图立即关闭。\n「地图网格」「同步循环」等独立功能不受影响。");
            _ignoreModeLimitEntry = plugin.Config.Bind("EX", "Ignore Mode Limit", false, "无视模式限制：附加功能/视野/地图/重生在任何模式下都可用");
            IgnoreModeLimit = _ignoreModeLimitEntry.Value;
            _ignoreModeLimitEntry.SettingChanged += (s, e) => IgnoreModeLimit = _ignoreModeLimitEntry.Value;
            _freezeCharEntry = plugin.Config.Bind("EX", "Freeze Character", false, "冻结角色：打开面板/地图时冻结自己的角色（其他角色照常移动；默认关 = 打开面板/地图时自己也能动）");
            PauseGame = _freezeCharEntry.Value;
            _freezeCharEntry.SettingChanged += (s, e) => PauseGame = _freezeCharEntry.Value;
            //会话内容页：过滤快捷消息（表情/预设消息不显示）——配置持久化
            _chatFilterQuickEntry = plugin.Config.Bind("设置", "过滤快捷消息", false, "会话内容页：过滤快捷消息（表情/预设消息不显示）");
            _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatFilterQuickEntry.SettingChanged += (s, e) => _chatFilterQuick = _chatFilterQuickEntry.Value;
            //会话内容页：隐藏游戏内聊天窗口（消息气泡/输入框不显示；会话内容页仍照常记录）——配置持久化
            _hideChatEntry = plugin.Config.Bind("设置", "隐藏聊天窗口", false, "隐藏游戏内聊天窗口（消息气泡/输入框不显示），会话内容页仍照常记录聊天。默认关闭。");
            _hideChat = _hideChatEntry.Value;
            _hideChatEntry.SettingChanged += (s, e) => _hideChat = _hideChatEntry.Value;
            //性能优化：进关卡/换关卡时回收垃圾（同关卡回合切换不清理，不影响结算速度）
            _gcAfterLoadEntry = plugin.Config.Bind("地图", "加载后清理", false, "进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。");
            _allEnabledEntry = plugin.Config.Bind("设置", "All Enabled", false, "本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值保持不变。\n初始默认关闭；改动自动保存，下次启动保持上次状态。");
            AllEnabled = _allEnabledEntry.Value;
            _allEnabledEntry.SettingChanged += (s, e) => AllEnabled = _allEnabledEntry.Value;
            _disabledPluginsEntry = plugin.Config.Bind("设置", "Disabled Plugins", "", "被禁用的外部插件 GUID（分号分隔，持久化，重启后仍禁用）");
            BlockInput = _blockInputEntry.Value;
            _blockInputEntry.SettingChanged += (s, e) => {
                BlockInput = _blockInputEntry.Value;
                ApplyEventSystemGate();
            };
            _winWidth = Mathf.Clamp(plugin.Config.Bind("设置", "Window Width", 720, "").Value, 400, 1200);
            _winHeight = Mathf.Clamp(plugin.Config.Bind("设置", "Window Height", 520, "").Value, 300, 1000);
            _winX = plugin.Config.Bind("设置", "Window X", 30f, "").Value;
            _winY = plugin.Config.Bind("设置", "Window Y", 30f, "").Value;

            GameObject go = new GameObject("SR_UCHManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ManagerUI>();
            //scene switches: reset EVERY UI state so nothing leaks into the next scene.
            //a manager left open freezes characters (input block), stops GameControl
            //(countdown/power-ups freeze) and disables the EventSystem (no clicks) -
            //this used to happen when joining another player's treehouse mid-session.
            SceneManager.activeSceneChanged += (a, b) => {
                _stylesReady = false;
                _mapBoundsValid = false;
                if (_mapVisible) ExitMapView();
                _mapVisible = false;
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
            Harmony.CreateAndPatchAll(typeof(ModManager));
        }

        //--- scan: split SR_UCH (internal) from every other plugin (external) ---
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
                    } catch { }
                    continue;
                }
                _externalPlugins.Add(pe);
            }
            _externalPlugins.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            if (_internalConfig != null) {
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    string sec = e.Definition.Section;
                    if (string.IsNullOrEmpty(sec)) sec = "(General)";
                    if (sec == "设置") continue; //the 设置 page is independent
                    if (sec == "Saved Lobby Details") continue; //feature removed
                    if (sec == "Treehouse Suicide") continue; //已并入“快速调整”栏目
                    if (!_internalSections.Contains(sec)) _internalSections.Add(sec);
                }
            }
            //the chat log page is a special internal section (not backed by config)
            if (!_internalSections.Contains("会话内容")) _internalSections.Add("会话内容");
            //the map page is a special internal section (not backed by config)
            if (!_internalSections.Contains("地图")) _internalSections.Add("地图");
            //the quick-adjust page is a special internal section (分数折扣/快速切换/快速自杀)
            if (!_internalSections.Contains("快速调整")) _internalSections.Add("快速调整");
            //the home page is a special internal section (not backed by config)
            if (!_internalSections.Contains("首页")) _internalSections.Add("首页");
            //internal sidebar order (EX always stays last)
            string[] order = {
                "首页", "Player Tracker", "Builder Enhancements", "Destroy Blocks", "视野", "快速调整", "地图", "Respawn",
                "尝试计数", "会话内容", "更多玩家", "模组联机", "实验", "EX"
            };
            _internalSections.Sort((a, b) => {
                if (a == "EX") return 1; //该栏目默认一直在最后
                if (b == "EX") return -1;
                int ia = Array.IndexOf(order, a), ib = Array.IndexOf(order, b);
                if (ia < 0) ia = order.Length;
                if (ib < 0) ib = order.Length;
                return ia.CompareTo(ib);
            });
            if (_internalSections.Count > 0) _selectedInternalSection = _internalSections[0];
            if (_externalPlugins.Count > 0) _pluginKey = _externalPlugins[0].guid;
            ApplyDisabledPlugins(); //re-disable plugins from the last session
        }

        private static void CloseMenu() {
            _visible = false;
            _capturing = null;
            if (_dirtyConfig != null) _dirtyConfig.Save();
            _dirtyConfig = null;
            _dirty = false;
            ApplyEventSystemGate();
        }

	}
}
