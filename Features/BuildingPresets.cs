// 布局预设库（sr.buildingpresets）：把当前方块布局存成命名预设，支持列表管理 / 加载 / 导入导出。
// 存储：BepInEx/config/SR_UCH/presets/<模式>/<安全文件名>.xml + 同目录 index.json（展示元数据缓存）。
// 约束：功能层只允许走 SRApi / Slots 契约与游戏程序集，绝不引用 SR.* 实现层（拆分构建下本文件单独编成
//       SR_UCH_BuildingPresets.dll，只引用核心 SR_UCH.dll）。因此这里不能直接写 SR_UCH.Tweaks.Level 的类型。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.buildingpresets", "BuildingPresets", "1.0", Provides = new string[] { "presets.reloadBusy" })]
    public partial class BuildingPresets : ITweak {
        // 配置段名：导航栏目名 / LocSec / Config.Bind 三处必须完全一致。
        private const string Section = "Building Presets";
        private const string IndexFileName = "index.json";
        private const int IndexVersion = 4;
        // index.json 每条记录的 schema 版本：缺失 = 旧格式。
        private const int IndexSchema = 1;
        // 排序方式：0=时间（新→旧），1=名称（A→Z）。
        private const int SortModeMin = 0;
        private const int SortModeMax = 1;
        // 表格列索引（与 TableArgs.colW 顺序一致）：名称 / 关卡 / 模式 / 数据 / 时间 / 操作（操作列钉在右侧）。
        private const int ColName = 0;
        private const int ColLevel = 1;
        private const int ColMode = 2;
        private const int ColMods = 3;
        private const int ColTime = 4;
        private const int ColAction = 5;
        private const int ColCount = 6;
        // 列宽（设计单位）默认值与允许范围；顺序与 Col* 一致。
        // 操作列的实际默认宽由 DefaultActionColW() 按当前语言按钮文字实时计算（DefColW[ColAction] 仅作兜底）。
        // 名称 / 关卡 / 模式 / 数据 / 时间 / 操作。数据与时间宽度翻倍、关卡 ×1.5（用户要求）。
        private static readonly float[] DefColW = new float[] { 150f, 84f, 64f, 76f, 96f, 200f };
        private const float ColWidthMin = 40f;
        private const float ColWidthMax = 600f;
        private const string ColWidthsKey = "Preset Widths";
        private const string ColOrderKey = "Preset Order";
        private const string ColHiddenKey = "Preset Hidden";
        // 操作列按钮种类：0=加载 1=导出 2=删除
        private const int ActLoad = 0;
        private const int ActExport = 1;
        private const int ActDelete = 2;
        // 从剪贴板导入的上限：方块数 / XML 字符数。
        private const int MaxImportBlocks = 3000;
        private const int MaxImportChars = 2000000;
        // 游戏本地关卡快照：7z 压缩文件的大小 / 解压后 XML 大小上限
        private const int MaxSnapshotBytes = 4 * 1024 * 1024;
        private const int MaxLocalXmlBytes = 8 * 1024 * 1024;
        // 二次确认窗口（秒）
        private const float ConfirmWindow = 5f;
        // 重载锁自愈阈值（秒）
        private const float ReloadLockStale = SrTimeouts.ReloadLockStaleSeconds;   // 单一真值：契约层 SrTimeouts
        // 等游戏消费 levelPortalXml 的硬上限（秒）
        private const float PresetConsumeTimeout = SrTimeouts.PresetConsumeTimeoutSeconds;   // 单一真值：契约层 SrTimeouts
        private static ConfigEntry<int> _maxPresets;
        private static ConfigEntry<bool> _skipTreehouse;   // 跳过树屋流程（联机直接替换关卡，风险自负）
        private static bool _actionHitThisFrame;   // 本帧是否命中操作按钮（空白处取消二次确认）
        private static ConfigEntry<int> _sortModeEntry;
        private static ConfigEntry<string> _colWidthsEntry;
        private static ConfigEntry<string> _colOrderEntry;
        private static ConfigEntry<string> _colHiddenEntry;
        private static ConfigEntry<int> _tableHEntry;
        // 运行期快照：配置变化回调里即时更新，供每帧渲染读取
        private static int _sortMode;
        // 存储根目录缓存
        private static string _rootDir;

        private static bool _loaded;
        private static readonly List<PresetInfo> _presets = new List<PresetInfo>();

        // UI 状态：保存名称输入框、状态文案、二次确认、重命名编辑、搜索词、路径导入输入框。
        private static string _newName = "";
        private static string _status = "";
        private static string _pendingKey;
        private static float _pendingAt = -1f;
        private static string _renameKey;
        private static string _renameText;
        private static string _search = "";
        private static string _importPath = "";
        // 关卡筛选（运行期状态，不落配置）："" = 全部；否则为预设 XML 里的 levelSceneName。
        private static string _levelFilter = "";
        private static bool _levelFilterOpen;
        // 模式筛选（运行期状态，不落配置）："" = 全部；否则为预设目录名（GameState.GameMode 的 ToString）。
        private static string _modeFilter = "";
        private static bool _modeFilterOpen;
        // 类型筛选（运行期状态，不落配置）："" = 全部，preset = 预设，local = 游戏本地保存关卡。
        // 默认「预设」：打开页面先只显示预设库，需要时再切到全部/本地。
        private static string _typeFilter = "preset";
        private static bool _typeFilterOpen;
        // 加载模式（运行期状态，不落配置）："" = 默认（跟随预设自身保存的模式，保持原有行为）；
        // 否则为 GameState.GameMode 的 ToString，加载/重载时强制使用该模式。
        private static string _loadMode = "";
        private static bool _loadModeOpen;

        // 标准表格（核心 TableDraw）状态：列宽 / 列序 / 隐藏列 / 可见列 / 命中矩形缓存 / 滚动 / 拖拽。
        private static readonly float[] _colW = new float[ColCount];
        private static float[] _drawW;   // 绘制用列宽副本（拉伸列只改它）
        // 初值必须与 ColCount 同长（曾经写成 5 项：DeriveVisibleCols 会因长度不符直接 return，表格一列不画）。
        // EnsureCols 里还会用 NormalizeOrder 归一再兜一层，但声明处就该是对的。
        private static int[] _order = new int[] { 0, 1, 2, 3, 4, 5 };
        private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
        private static SRApi.TableState _tableCols;   // 列宽/列序/隐藏列（核心统一持久化）
        private static bool _colsLoaded;
        private static int _hiddenBeforeTable;
        private static readonly List<int> _vis = new List<int>(ColCount);
        private static readonly List<Rect> _headerCells = new List<Rect>();
        private static readonly List<int> _headerCols = new List<int>();
        private static readonly List<Rect> _rowRects = new List<Rect>();
        private static readonly List<int> _rowRows = new List<int>();
        private static readonly List<Rect> _pinRects = new List<Rect>();
        private static readonly List<int> _pinRows = new List<int>();

        private static PresetInfo _actPreset;
        private static readonly SRApi.TableAction[] _actActions = new SRApi.TableAction[] {
            new SRApi.TableAction { OnClick = DoActLoad },
            new SRApi.TableAction { OnClick = DoActExport },
            new SRApi.TableAction { OnClick = DoActDelete }
        };
        // 本地关卡（游戏存档）只提供「加载」，不提供重命名/导出/删除，避免误动游戏存档。
        private static readonly SRApi.TableAction[] _actLocalActions = new SRApi.TableAction[] {
            new SRApi.TableAction { OnClick = DoActLoad }
        };
        private static readonly string[] _cells = new string[ColCount];
        private static SRApi.TableArgs _args;
        private static Vector2 _scrollRows;
        private static Vector2 _scrollCols;   // 诊断：每帧只记一次滚轮事件
        private static Rect _tableBox;     // 表格在屏幕上的矩形（页面级滚轮钩子按它判断是否滚表格）
        private static float _rowsHCache;   // 表格真实行区高度（滚轮钩子兜底用）
        private static float _maxScrollX;
        private static float _maxScrollY;
        private static bool _sbDragging;
        private static float _sbGrabY;
        private static bool _sbHandled;    // 本帧核心滚动条是否已处理鼠标事件
        private static bool _dragH;
        private static float _dragWatchdog;

        // ============ 列表数据管线缓存（完美列表）============
        // 可见行：用"数据 + 筛选 + 排序"签名驱动，签名不变时跨帧复用；
        // 以前按 frameCount 每帧 Invalidate，什么都不变也要做一次 O(n log n) 排序。
        private static int _visSig = int.MinValue;
        private static int _sigFrame = -1, _sigCache;
        // 筛选下拉：按 _presets 的 Level/Mode 签名缓存，避免每帧建 List + HashSet + 排序 + ToArray。
        private static int _levelOptSig = int.MinValue, _modeOptSig = int.MinValue;
        private static List<string> _levelOpts = new List<string>(), _modeOpts = new List<string>();
        private static string[] _levelOptVals = new string[0], _levelOptLabels = new string[0];
        private static string[] _modeOptVals = new string[0], _modeOptLabels = new string[0];
        // 单元格文本：key 用预设的稳定标识字符串（不用 PresetInfo 引用，避免扫描重建后旧对象被字典强引用累积）。
        // 值带显示签名，上限 256，超了整体清空（预设上限 100）。
        private sealed class CellCacheEntry {
            public int Sig;
            public readonly string[] Cells = new string[ColCount];
        }
        private static readonly Dictionary<string, CellCacheEntry> _presetCellCache = new Dictionary<string, CellCacheEntry>(64);
        // 筛选签名上一帧值：变化后复位滚动与二次确认，避免"滚到一半改搜索"或误触旧行的确认态。
        private static int _filterSigLast = int.MinValue;
        // v2：标签宽度缓存（按语言签名）。FormLabelWidth 每帧被 7 处调用，原实现每次新建
        // 2 个 GUIContent 并做 2 次 CalcSize —— 一帧 14 次分配 + 字形测量。
        private static int _formLabelSig = int.MinValue;
        private static float _formLabelW = 90f;
        // v2：状态栏反馈的显示起点。_status 由 10 处赋值且永不清除，上屏后会一直挂着
        // "已删除预设：X"；这里只控制【显示】，不改任何写入方。
        private static string _statusShown;
        private static float _statusShownAt = -1000f;
        // v2：两个筛选下拉每帧各遍历一次 _presets 算签名，合并为每帧一次遍历。
        private static int _optSigFrame = -1, _optSigLevel, _optSigMode;
        // 关卡列显示模式：false = 关卡名称（本地化），true = 关卡代码（快照码/场景名）；运行期状态，不落配置。
        private static bool _levelShowCode;

        // 加载预设时的重载锁
        private static string _reloadBusyReason;   // 锁的来源
        private static float _otherBusySince = -1f;   // 对方重载锁开始等待的时刻（-1 = 没在等）
        private static bool _reloadBusy;
        private static float _reloadBusyAt = -1f;

        // 一条预设的展示元数据。磁盘上的 XML 文件是权威数据；index.json 只是名称/关卡/方块数/时间/有效性缓存。
        private sealed class PresetInfo {
            public string Name;
            public string Mode;      // 模式目录名（GameState.GameMode 的 ToString）
            public string File;      // 安全文件名（含 .xml）
            public string FilePath;  // 扫描时补全的绝对路径
            public string Level = ""; // XML 根节点 levelSceneName（读不到留空）
            public string LevelName = ""; // 展示用关卡名（保存时按游戏本地化/自定义关卡名记下；旧索引为空则按 Level 现解析）
            public string LevelCode = ""; // 展示用关卡代码（自定义关卡取 snapshotInfo.code/快照码；内置关卡回退场景名）
            public int Blocks = -1;  // -1 = 索引里没有，需要从 XML 现算
            public DateTime TimeUtc = DateTime.MinValue;
            public bool Ok;          // XML 可解析且方块数 > 0（存 index.json；缺字段按 false，扫描时自愈）
        // 修改器策略："" = 预设自带（默认）；"current" = 用游戏当前值；"off" = 全部还原默认。
            public string ModsMode = "";
            // 加载该预设时是否一并应用 游戏规则 / 积分设置 / 模块概率（保存时写入 sidecar）。
            public bool ApplyRules, ApplyPoints, ApplyBlocks;
        public Dictionary<string, string> ModsVals;   // 旧索引（custom）遗留值；新数据不再写入
            // 来源：false = 本功能 presets 目录里的预设；true = 游戏本地保存的自定义关卡（.snapshot，只读）。
            public bool IsLocal;
            public string LocalRelative = ""; // 本地条目在 snapshots 目录里的相对文件名（key 用 "local:"+相对路径）

            public string Key { get { return IsLocal ? "local:" + LocalRelative : Mode + "/" + File; } }
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _maxPresets = plugin.Config.Bind(Section, "Max Presets", 100,
                    new ConfigDescription("预设数量上限（1-100，默认 100）：列表最多保留多少条，达到上限后必须先删除旧预设才能保存新布局。",
                        new AcceptableValueRange<int>(1, 100)));
                // 旧版本默认值是 50，且历史范围到 500：启动时迁移/夹取到新范围（50 视作旧默认值改成 100）。
                try {
                    if (_maxPresets.Value == 50) _maxPresets.Value = 100;
                    else if (_maxPresets.Value < 1) _maxPresets.Value = 1;
                    else if (_maxPresets.Value > 100) _maxPresets.Value = 100;
                } catch (Exception __mxp) { SRApi.Guard.Log("布局预设/裁剪数量上限", __mxp); }
                _sortModeEntry = plugin.Config.Bind(Section, "Sort Mode", 0,
                    new ConfigDescription("列表排序方式：0=时间（新→旧），1=名称（A→Z）。",
                        new AcceptableValueRange<int>(SortModeMin, SortModeMax)));
                _sortMode = _sortModeEntry.Value;
                _sortModeEntry.SettingChanged += delegate { _sortMode = _sortModeEntry.Value; };
                _skipTreehouse = plugin.Config.Bind(Section, "Skip Treehouse", false,
                    "跳过树屋流程：勾选后联机替换关卡不再先回树屋，而是直接替换场景（房客的 SelectedLevel 可能不同步，风险自负）。");
                // 旧配置可能残留已弃用的 2/3：夹回 0（时间）。
                try { if (_sortModeEntry.Value < SortModeMin || _sortModeEntry.Value > SortModeMax) _sortModeEntry.Value = SortModeMin; }
                catch (Exception __cx) { SRApi.Guard.Log("布局预设/裁剪排序值", __cx); }
                // 列表列宽 / 列序（内部状态，由列表表头拖动维护；不在页面上渲染编辑框）。
                _colWidthsEntry = plugin.Config.Bind(Section, ColWidthsKey, "",
                    "内部状态：预设列表列宽（逗号分隔的设计单位，由表头拖动自动写入）。");
                _colOrderEntry = plugin.Config.Bind(Section, ColOrderKey, "",
                    "内部状态：预设列表列顺序（逗号分隔的列索引，由表头拖动自动写入）。");
                _colHiddenEntry = plugin.Config.Bind(Section, ColHiddenKey, "",
                    "内部状态：预设列表隐藏列（逗号分隔的列索引，右键表头隐藏后自动写入）。");
            // 注意：不要用 AcceptableValueRange(120,1000)——它会把默认值 0 夹成 120，等于强制固定高度。
            _tableHEntry = plugin.Config.Bind(Section, "Table Height", 0,
                new ConfigDescription(SRApi.T("预设列表高度（0 = 自动；拖动表格底边可直接修改并保存）", "Preset list height (0 = auto; drag the table bottom edge to change and save)")));
            // 旧版本的默认值被夹成 120，一次性复位为 0（自动）。
            if (_tableHEntry.Value == 120) _tableHEntry.Value = 0;
            // 一次性迁移：以前"拖拽会写 Table Height 但从不读取"，旧值没有任何可见效果；
            // 现在它真正生效，若不迁移，老用户升级后列表会突然变矮。用独立标记键保证只做一次。
            try {
                ConfigEntry<bool> __migH = plugin.Config.Bind(Section, "Table Height Migrated", false,
                    "内部状态：是否已把旧版无效的表格高度复位（勿手动修改）。");
                if (!__migH.Value) {
                    __migH.Value = true;
                    if (_tableHEntry.Value > 0) { _tableHEntry.Value = 0; SRApi.LogInfo("[布局预设] 旧的表格高度值已复位为自动（此前该值从未生效）"); }
                }
            } catch (Exception __mh) { SRApi.Guard.Log("布局预设/表格高度迁移", __mh); }
                // 互斥槽位：关卡重载（Level 功能）动手前会 Require 这个槽位；双方都忙时互相拦下，避免两套重载并发。
                try { Slots.Provide("presets.reloadBusy", (Func<bool>)(() => _reloadBusy)); }
                catch (Exception __sx) { SRApi.Guard.Log("布局预设/登记重载互斥槽位", __sx); }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设 初始化", __ex);
            }
        }

        // 栏目注册：单页（保存 / 导入导出 / 搜索排序筛选 + 预设列表同页，分区用 SectionTitle）。
        private static void SelfReg() {
            SRApi.LocSec(Section, "布局预设", "Layout Presets");
            SRApi.Nav(Section, 45); // 关卡(40) 之后、构建工具(50) 之前
            SRApi.RegisterPage(Section, Render);
            // 页面关掉了滚动，列表要自己吃滚轮：只有页面级钩子能在滚动区建立之前拿到事件。
            SRApi.RegisterPageWheel(OnPresetWheel);
            // 重载锁自愈：TickReloadLock 写好了却从未注册，等于"超时兜底"根本不存在，
            // 失败路径漏放锁只能等用户再点一次加载（audit 10-P0-2）。
            // 重载锁自愈 + levelPortalXml 兜底清理：两个 tick 都必须注册，否则兜底逻辑等于不存在。
            try { SRApi.RegisterTick("布局预设-锁自愈", TickReloadLock); }
            catch (Exception __tl) { SRApi.Guard.Log("布局预设/注册锁自愈", __tl); }
            try { SRApi.RegisterTick("布局预设-XML兜底", TickXmlGuard); }
            catch (Exception __tx) { SRApi.Guard.Log("布局预设/注册XML兜底", __tx); }
            // 淘汰旧键：Show Timestamps（功能已删）与 Enabled（总开关已删除，UI 不可达）。
            try { SRApi.Migration.Remove(Section, "Show Timestamps"); }
            catch (Exception __mx) { SRApi.Guard.Log("布局预设/注册淘汰键", __mx); }
            try { SRApi.Migration.Remove(Section, "Enabled"); }
            catch (Exception __me) { SRApi.Guard.Log("布局预设/淘汰总开关键", __me); }
            SRApi.LocKey(Section, "Max Presets", "预设数量上限", "Max presets");
            SRApi.LocDesc(Section, "Max Presets",
                "预设数量上限\n列表最多保留多少条预设\n范围 1-100；只限制新保存，不删磁盘文件",
                "Preset count limit\nhow many presets the list keeps\n1-100; limits new saves only, never deletes files");
            SRApi.LocKey(Section, "Sort Mode", "列表排序", "Sort mode");
            SRApi.LocDesc(Section, "Sort Mode",
                "列表排序方式\n0＝按时间（新到旧），1＝按名称\n由列表页的排序下拉维护",
                "List sort mode\n0 = newest first, 1 = by name\nmaintained by the list page dropdown");
            // 换场景后下次打开面板重新扫盘（手动放入/删除的 XML、其它存档的改动都能看到）。
            // 页面级滚轮钩子：在窗口建立滚动区【之前】拿到滚轮，鼠标在预设表格上就滚表格。
            // （页面内部拿到的事件已被滚动区处理过，所以必须在这一层接。）
            // 手轮/滚动条统一走核心 TableScrollbarFor（见表格绘制之后）。
            try { SRApi.RegisterSceneHook(delegate { _loaded = false; }); }
            catch (Exception __sh) { SRApi.Guard.Log("布局预设/注册场景钩子", __sh); }
            try { SRApi.HotkeyAction("presets.treehouse", "返回树屋", delegate { ReturnToTreehouse(); }); }
            catch (Exception __hk) { SRApi.Guard.Log("布局预设/注册返回树屋快捷键", __hk); }
            SRApi.LocKey(Section, "Skip Treehouse", "跳过树屋流程", "Skip treehouse");
            SRApi.LocDesc(Section, "Skip Treehouse",
                "跳过树屋流程\n直接切换场景进入目标关卡，不先回树屋\n仅单人生效；联机时强制走树屋",
                "Skip treehouse\nswitches scene straight into the target level\nsingle-player only; forced on online");
            SRApi.LocKey(Section, ColWidthsKey, "列表列宽（内部）", "Preset widths (internal)");
            SRApi.LocDesc(Section, ColWidthsKey,
                "预设列表列宽\n拖动表头后自动写入\n一般无需手改；非法值回退默认",
                "Preset column widths\nwritten automatically after dragging the header\nrarely needs editing; invalid values fall back");
            SRApi.LocKey(Section, ColOrderKey, "列表列序（内部）", "Preset order (internal)");
            SRApi.LocDesc(Section, ColOrderKey,
                "预设列表列序\n拖动表头换位后自动写入\n一般无需手改；非法值回退默认",
                "Preset column order\nwritten automatically after reordering the header\nrarely needs editing; invalid values fall back");
            SRApi.LocKey(Section, ColHiddenKey, "隐藏列（内部）", "Preset hidden (internal)");
            SRApi.LocDesc(Section, ColHiddenKey,
                "预设列表隐藏列\n右键表头隐藏后自动写入\nShift+右键表头可恢复全部",
                "Preset hidden columns\nwritten after hiding with a header right-click\nShift+right-click the header restores all");
            // v2：Initialize 新增的 "Table Height Migrated" 与另外三个内部键同段，却没有本地化，
            // 设置页会显示成原生英文 key。补齐，且同样标注"勿手动修改"。
            SRApi.LocKey(Section, "Table Height Migrated", "列表高度迁移标记（内部）", "List height migration flag (internal)");
            SRApi.LocDesc(Section, "Table Height Migrated",
                "内部状态：是否已完成旧版无效的表格高度复位\n由模组自动维护，勿手动修改\n改动后需要重开设置页才会重新判断",
                "Internal: whether the legacy ineffective table height has been reset\nmaintained by the mod automatically; do not edit\na change is re-evaluated the next time the settings page opens");
        }

        // ---------- 配置读取 ----------


        private static int MaxPresets {
            get {
                try { return _maxPresets != null ? Mathf.Clamp(_maxPresets.Value, 1, 100) : 100; } catch { return 100; }
            }
        }

        private static int SortMode {
            get {
                try { return Mathf.Clamp(_sortMode, SortModeMin, SortModeMax); } catch { return 0; }
            }
        }

        // 所有动作共用的门控：模组总开关 + 本功能总开关；返回 false 时已给出用户提示。
        private static bool ActionAllowed() {
            if (!SRApi.GateMaster) {
                SRApi.Notify(SRApi.MsgMasterOff);
                return false;
            }
            return true;
        }

        private static float W(float v) {
            return SRApi.Ctl.Sc(v);
        }

        // 排序：按配置的排序方式（0=时间新→旧，1=名称 A→Z），同序时按名称稳定排序。
        private static int ComparePreset(PresetInfo a, PresetInfo b) {
            try {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int c;
                if (SortMode == 1) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                else c = b.TimeUtc.CompareTo(a.TimeUtc);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            } catch { return 0; }
        }

        // 当前搜索过滤（名称 / 关卡 / 模式）+ 关卡下拉筛选 + 排序后的可见列表。
        private static readonly SRApi.TableSource<PresetInfo> _visSource = new SRApi.TableSource<PresetInfo>();

        private static List<PresetInfo> _visibleCache;   // 当前表格的可见行（供数据列悬浮预览取预设）

        private static List<PresetInfo> VisiblePresets() {
            // 签名驱动：只有数据 / 筛选 / 排序 / 语言 / 显示模式真正变化时才重算。
            // 同一帧内 OnGUI 的多次调用由 _sigFrame 折叠成一次签名计算。
            int sig = VisibleSignature();
            if (sig != _visSig) {
                _visSig = sig;
                _visSource.Filter = PassPresetFilter;
                _visSource.Sort = ComparePreset;
                long stamp = Time.frameCount;
                _visSource.Invalidate();
                _visSource.Refresh(_presets, stamp);
            }
            return _visSource.Rows;
        }

        // 覆盖所有会影响可见行内容的输入。漏掉任何一处都只会让列表"不刷新"，所以显式枚举，
        // 不依赖调用方记得 Invalidate。签名按帧缓存，避免每趟 OnGUI 都遍历一次。
        private static int VisibleSignature() {
            if (_sigFrame == Time.frameCount) return _sigCache;
            unchecked {
                int h = 17;
                h = h * 31 + _presets.Count;
                h = h * 31 + SortMode;
                h = h * 31 + (_search ?? "").GetHashCode();
                h = h * 31 + (_levelFilter ?? "").GetHashCode();
                h = h * 31 + (_modeFilter ?? "").GetHashCode();
                h = h * 31 + (_typeFilter ?? "").GetHashCode();
                h = h * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                h = h * 31 + (_levelShowCode ? 1 : 0);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null) { h = h * 31; continue; }
                    h = h * 31 + (p.Name ?? "").GetHashCode();
                    h = h * 31 + (p.Level ?? "").GetHashCode();
                    h = h * 31 + (p.Mode ?? "").GetHashCode();
                    h = h * 31 + p.TimeUtc.GetHashCode();
                    h = h * 31 + (p.Ok ? 1 : 0);
                    h = h * 31 + (p.ModsMode ?? "").GetHashCode();
                    h = h * 31 + (p.IsLocal ? 1 : 0);
                }
                _sigFrame = Time.frameCount;
                _sigCache = h;
                return h;
            }
        }

        // 仅筛选条件（不含数据）的签名：变化时复位滚动位置与二次确认态。
        private static int FilterSignature() {
            unchecked {
                int h = 7;
                h = h * 31 + (_search ?? "").GetHashCode();
                h = h * 31 + (_levelFilter ?? "").GetHashCode();
                h = h * 31 + (_modeFilter ?? "").GetHashCode();
                h = h * 31 + (_typeFilter ?? "").GetHashCode();
                h = h * 31 + SortMode;
                return h;
            }
        }

        // 筛选下拉选项的签名：只依赖预设集合里的 Level/Mode 与当前语言。
        // 两个下拉同帧各调一次；这里一次遍历同时算出两份签名并按帧缓存。
        private static void EnsureOptionSignatures() {
            if (_optSigFrame == Time.frameCount) return;
            unchecked {
                int hl = 3, hm = 5;
                hl = hl * 31 + _presets.Count; hm = hm * 31 + _presets.Count;
                hl = hl * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                hm = hm * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null) { hl *= 31; hm *= 31; continue; }
                    hl = hl * 31 + (p.Level ?? "").GetHashCode();
                    hm = hm * 31 + (p.Mode ?? "").GetHashCode();
                }
                _optSigLevel = hl; _optSigMode = hm;
                _optSigFrame = Time.frameCount;
            }
        }

        private static int FilterOptionsSignature(bool level) {
            EnsureOptionSignatures();
            return level ? _optSigLevel : _optSigMode;
        }

        // 过滤条件（原 VisiblePresets 内的判断，原样保留语义）。
        private static bool PassPresetFilter(PresetInfo p) {
            try {
                string q = (_search ?? "").Trim();
                if (string.Equals(_typeFilter, "preset", StringComparison.Ordinal) && p.IsLocal) return false;
                if (string.Equals(_typeFilter, "local", StringComparison.Ordinal) && !p.IsLocal) return false;
                if (!string.IsNullOrEmpty(_levelFilter)
                    && !string.Equals(p.Level, _levelFilter, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrEmpty(_modeFilter)
                    && !string.Equals(p.Mode, _modeFilter, StringComparison.OrdinalIgnoreCase)) return false;
                if (q.Length > 0) {
                    bool hit = ContainsText(p.Name, q) || ContainsText(p.Level, q) || ContainsText(ModeText(p.Mode), q);
                    if (!hit) return false;
                }
                return true;
            } catch { return false; }
        }

        private static bool ContainsText(string text, string query) {
            try { return text != null && query != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        // ---------- 页面渲染（单页：保存与导入 + 搜索/排序 + 预设列表） ----------

        // 同一页按 SectionTitle 分保存 / 导入导出 / 搜索排序筛选 / 预设列表四个区；页面滚动保留，表格另有一套自己的滚轮（页面级钩子 + 表格内处理）。
        // 预设列表在固定高度区域内用自己的滚轮滚动。
        // 页面内容在布局空间里的起点 y（零高度探针），用于计算表格上方已用高度（同联机列表）。
        private static float _pageTopY;

        // 页面级滚轮：鼠标在表格矩形内就把滚轮换算成行滚动（与核心 TableWheelInput 同一套系数）。
        private static int _renderFrame = -1000;   // 本页最近一次被渲染的帧（滚轮钩子只在自己页面上工作）
        private static bool OnPresetWheel(Vector2 mouse, float wheel) {
            try {
                // 页面级滚轮钩子是全局注册的：不判"本页是否可见"的话，离开本页后仍会吃掉别的页面的滚轮。
                if (Time.frameCount - _renderFrame > 2) return false;
                // 命中框 = 表格真实矩形（布局坐标转屏幕坐标）：光标在列表里才滚列表，在界面上就交给页面滚动。
                Rect hitBox = TableScreenBox();
                if (hitBox.width <= 2f || hitBox.height <= 2f) return false;   // 表格还没画出来：本帧不抢

                // 复用表格自身算出的滚动上限（由真实 rowsH 得出，帧间稳定），与联机列表同一套。
                // 以前用 ContentArea 高度重算：那是整个右栏，远大于表格行区，maxY 被算成 0，滚轮就死了。
                if (!hitBox.Contains(mouse)) return false;
                // 交给统一实现：Shift = 横向滚列，否则竖向滚行；到头返回 false。
                return ScrollTableBy(wheel);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/页面滚轮", __ex); return false; }
        }

        // 布局坐标 -> 屏幕坐标：页面布局原点在 BeginArea(winRect+Sc(4))，与 ContentArea 只差"侧栏宽 + 2"。
        // v2：页面滚动现在真正可用（Render 不再每帧把 PageScroll 清零），BeginScrollView 内容坐标
        // 比屏幕坐标多一个 -_scroll.y，命中框必须同步减掉，否则滚过页面后滚轮会"点在别的行上"。
        private static Vector2 LayoutToScreen(Vector2 layout) {
            try {
                float offX = SRApi.ContentArea.x - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(2f);
                float offY = SRApi.ContentArea.y - SRApi.PageScroll.y;
                return new Vector2(layout.x + offX, layout.y + offY);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/坐标换算", __ex); return layout; }
        }

        // 表格滚轮：Shift 横向、否则纵向；到头返回 false 让给下一层。页面钩子与区域注册共用。
        private static bool ScrollTableBy(float wheel) {
            try {
                bool __shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (__shift && _maxScrollX > 0f) {
                    float beforeX = _scrollCols.x;
                    _scrollCols.x = Mathf.Clamp(_scrollCols.x - wheel * SRApi.Ctl.Sc(60), 0f, _maxScrollX);
                    return Mathf.Abs(_scrollCols.x - beforeX) > 0.01f;
                }
                float maxY = _maxScrollY;
                if (maxY <= 0f && _rowsHCache > 0f)
                    maxY = Mathf.Max(0f, VisiblePresets().Count * SRApi.Ctl.TableRowH() - _rowsHCache + SRApi.Ctl.TableRowH());
                if (maxY <= 0f) return false;
                float before = _scrollRows.y;
                _scrollRows.y = Mathf.Clamp(_scrollRows.y - wheel * SRApi.Ctl.Sc(56), 0f, maxY);
                _maxScrollY = maxY;
                return Mathf.Abs(_scrollRows.y - before) > 0.01f;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/表格滚动", __ex); return false; }
        }

        // 表格矩形（屏幕坐标）：_tableBox 是布局坐标，且 Layout 趟可能还是退化矩形。
        // private：拆分构建下本文件单独成 DLL，internal 跨程序集不可见；当前也只在本文件使用。
        private static Rect TableScreenBox() {
            try {
                Rect b = _tableBox;
                if (b.width <= 2f || b.height <= 2f) return b;
                Vector2 p0 = LayoutToScreen(new Vector2(b.x, b.y));
                return new Rect(p0.x, p0.y, b.width, b.height);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/表格命中框", __ex); return _tableBox; }
        }

        // 筛选行的标签统一宽度：两行控件的左边缘才能对齐成两列网格。
        // 搜索/筛选区的标签列：比保存区的表单标签窄 1/3（那里是输入框对齐，这里是短标签）
        private static float FilterLabelWidth() { return Mathf.Max(SRApi.Ctl.Sc(40f), FormLabelWidth() * (2f / 3f)); }

        private static void RowLabel(string text) {
            GUILayout.Label(text, SRApi.Ctl.Label, GUILayout.Width(FilterLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
        }

        // 列表状态栏：条数 / 筛选结果 + 最近一次操作反馈。
        // _status 此前被 10 处赋值却从未读取，用户操作后只能看到会淡出的 Notify；这里让它真正上屏。
        private static void RenderListStatus(int shown, int total) {
            try {
                string count = (shown == total)
                    ? SRApi.T("共 ", "Total ") + total + SRApi.T(" 条", " items")
                    : SRApi.T("显示 ", "Showing ") + shown + SRApi.T(" / ", " / ") + total + SRApi.T(" 条", " items");
                // v2：_status 永不清除，上屏后会一直挂着上次的"已删除预设：X"。
                // 只控制显示：值变化时重新计时，8 秒后不再拼接（不动任何写入方）。
                string status = _status;
                if (!string.IsNullOrEmpty(status)) {
                    if (!string.Equals(status, _statusShown, StringComparison.Ordinal)) {
                        _statusShown = status;
                        _statusShownAt = Time.unscaledTime;
                    }
                    if (Time.unscaledTime - _statusShownAt > 8f) status = null;
                }
                if (!string.IsNullOrEmpty(status)) count += "   ·   " + status;
                // 诊断（一直显示，不写日志也能看）：行数/行区/滚动上限/当前偏移/滚轮命中的层。
                // 若"滚不动"，这几个数字能直接指出是 maxY=0（内容没超出）、还是 layer=none（没收到滚轮）。
                try {
                    count += "   ·   行" + shown + " 行区" + _rowsHCache.ToString("0") + " maxY" + _maxScrollY.ToString("0")
                           + " y" + _scrollRows.y.ToString("0") + " 层:" + (SRApi.WheelLayer ?? "?");
                } catch { /* 静默：诊断行失败不影响列表 */ }
                // 工具行前面已有"跳过树屋 + 返回树屋"约 290 设计单位；不给状态栏设上限，
                // 导出目录那种长路径会把 FlexibleSpace 挤没、整行撑出窗口。
                float maxW = Mathf.Max(SRApi.Ctl.Sc(120f), SRApi.ContentArea.width - SRApi.Ctl.Sc(380f));
                GUILayout.Label(SRApi.Ctl.Content(count, string.IsNullOrEmpty(status) ? null : status), SRApi.Ctl.Label,
                    GUILayout.Height(SRApi.Ctl.Sc(26f)), GUILayout.MaxWidth(maxW));
            } catch (Exception __ls) { SRApi.Guard.Log("布局预设/列表状态栏", __ls); }
        }

        // 保存区的两个标签（预设名称 / XML 路径）按实测宽度取最大值，两行输入框左边缘保持对齐。
        private static float FormLabelWidth() {
            // v2：只依赖语言 + 字号缩放，签名不变直接复用（每帧 7 处调用，原实现 14 次分配）。
            int sig = (SRApi.Ctl.LangEn ? 1 : 0) * 31 + Mathf.RoundToInt(SRApi.Ctl.Sc(100f));
            if (sig == _formLabelSig) return _formLabelW;
            try {
                float wa = SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(SRApi.T("预设名称", "Preset name"))).x;
                float wb = SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(SRApi.T("XML 路径", "XML path"))).x;
                _formLabelW = Mathf.Max(SRApi.Ctl.Sc(52f), Mathf.Max(wa, wb) + SRApi.Ctl.Sc(4f));   // 标签与输入框间距缩小约 1/3
                _formLabelSig = sig;
                return _formLabelW;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/标签宽度", __ex); return SRApi.Ctl.Sc(90f); }
        }

        // ================= 布局预设页（重写：与联机页同一套"填充模式"结构） =================
        // 结构固定为三段：
        //   1) 固定头部（保存/导入/导出 + 搜索/排序/筛选 + 工具行）—— 永远可见，不随列表滚动
        //   2) 列表区：高度 = 页面视口 − 头部已用高度，占满剩余区域，只有列表本身滚动
        //   3) 底边拖拽条
        // 页面本身声明 NoPageScrollBars：整页不滚，滚轮只可能落在列表里（由页面级滚轮钩子消费）。
        private static void Render() {
            try {
                _renderFrame = Time.frameCount;
                SRApi.NoPageScrollBars = true;
                EnsureLoaded();
                EnsureCols();
                string pendingBefore = _pendingKey;

                // Layout 趟记录内容原点（表格高度/命中框都相对它计算）
                Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
                if (Event.current != null && Event.current.type == EventType.Layout) _pageTopY = topProbe.y;

                RenderSaveImportHeader();
                RenderFilterHeader();
                RenderListToolbar();
                ApplyFilterChange();

                // 零高度探针：Layout 趟 GetLastRect 可能是退化矩形，用它拿布局游标的真实 y
                Rect last = GUILayoutUtility.GetRect(1f, 0f);
                float tableH = ComputeFillTableHeight(last);
                _actionHitThisFrame = false;
                SRApi.Ctl.SetTableHighlight(_search ?? "");   // 搜索命中在表格里高亮显示
            RenderPresetTable(tableH, VisiblePresets());
                DrawInlineRename();
                CancelPendingOnBlank(pendingBefore);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染布局预设页", __ex); }
        }

        // 头部一：保存 / 导入 / 导出（固定 3 行）
        private static void RenderSaveImportHeader() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 保存 / 导入 / 导出 -", "- Save / Import / Export -"));
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(SRApi.T("预设名称", "Preset name"), SRApi.Ctl.Label,
                    GUILayout.Width(FormLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string typed = SRApi.ClearableTextField(_newName ?? "", 40, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(220)), GUILayout.Height(W(24)));
                if (typed != null && typed != _newName) _newName = typed;
                if (SRApi.Ctl.Button(SaveButtonText(), SRApi.T("把当前方块布局保存成一条命名预设\n保存到预设目录，之后可一键加载", "saves the current block layout as a named preset\nstored in the presets folder for one-click loading"), 120f, 24f)) SaveCurrent();
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }

            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(SRApi.T("XML 路径", "XML path"), SRApi.Ctl.Label,
                    GUILayout.Width(FormLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string path = SRApi.ClearableTextField(_importPath ?? "", 260, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(220)), GUILayout.Height(W(24)));
                if (path != null && path != _importPath) _importPath = path;
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }

            GUILayout.BeginHorizontal();
            try {
                GUILayout.Space(FormLabelWidth());
                if (SRApi.Ctl.Button(SRApi.T("从路径导入", "Import path"), SRApi.T("读取上面填写的 XML 路径并导入成一条预设", "imports the XML file at the path above as a preset"), 110f, 24f)) ImportFromPath();
                if (SRApi.Ctl.Button(SRApi.T("从剪贴板导入", "Import clipboard"), SRApi.T("把剪贴板里的预设文本导入成一条预设", "imports preset text from the clipboard as a preset"), 120f, 24f)) ImportFromClipboard();
                if (SRApi.Ctl.Button(SRApi.T("全部导出到目录", "Export all to folder"),
                        SRApi.T("把所有预设 XML 复制到 预设目录/exports/<时间>/ 下（按模式分子目录），并复制一份 index.json",
                            "copies every preset XML into presets/exports/<time>/ (grouped by mode) and copies index.json as well"), 140f, 24f)) ExportAllToDirectory();
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // 头部二：搜索 / 排序 / 筛选（固定 2 行 + 标签列对齐）
        private static void RenderFilterHeader() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 搜索 / 排序 / 筛选 -", "- Search / sort / filter -"));
            GUILayout.BeginHorizontal();
            try {
                RowLabel(SRApi.T("搜索", "Search"));
                Rect searchRect = GUILayoutUtility.GetLastRect();
                Event evSearch = Event.current;
                if (evSearch != null && evSearch.type == EventType.MouseDown && evSearch.button == 0
                    && searchRect.Contains(evSearch.mousePosition)) {
                    _search = "";   // 点标签清空搜索
                    evSearch.Use();
                }
                string q = SRApi.ClearableTextField(_search ?? "", 40, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(110)), GUILayout.Height(W(24)));
                if (q != null && q != _search) _search = q;
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("排序", "Sort"));
                RenderSortCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("加载", "Load"));
                RenderLoadModeCombo();
                GUILayout.Space(W(7));
                if (SRApi.Ctl.Button(SRApi.T("刷新", "Refresh"), SRApi.T("重新扫描预设目录", "rescan the presets folder"), 0f, 24f)) {
                    try { _loaded = false; LoadFromDisk(true); } catch (Exception __rx) { SRApi.Guard.Log("布局预设/刷新列表", __rx); }
                }
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }

            GUILayout.BeginHorizontal();
            try {
                RowLabel(SRApi.T("类型", "Type"));
                RenderTypeFilterCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("关卡", "Level"));
                RenderLevelFilterCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("模式", "Mode"));
                RenderModeFilterCombo();
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // 头部三：工具行（跳过树屋 / 返回树屋 / 条数状态），固定一行
        private static void RenderListToolbar() {
            List<PresetInfo> visible = VisiblePresets();
            GUILayout.BeginHorizontal();
            try {
                if (_skipTreehouse != null) {
                    SRApi.Ctl.RestoreLabel(SRApi.Ctl.Content(SRApi.T("跳过树屋流程", "Skip treehouse"),
                        SRApi.T("跳过树屋流程\n联机替换关卡时直接替换场景，不先回树屋\n房客的关卡状态可能不同步（风险自负）",
                            "skip the treehouse flow\nreplaces the level scene directly online instead of returning to the treehouse first\nclients' level state may be out of sync (at your own risk)")),
                        _skipTreehouse, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(26));
                    SRApi.Ctl.RenderControl(_skipTreehouse);
                }
                GUILayout.Space(SRApi.Ctl.Sc(8));
                if (SRApi.Ctl.HotkeyButtonNoHint("presets.treehouse", SRApi.T("返回树屋", "Back to treehouse"),
                        SRApi.T("走游戏原生的结束对局流程回到树屋（房主有效）", "ends the current match through the game's native flow and returns to the treehouse (host only)"),
                        delegate { ReturnToTreehouse(); }, SRApi.Ctl.Sc(150), SRApi.Ctl.Sc(26))) ReturnToTreehouse();
                GUILayout.Space(SRApi.Ctl.Sc(10));
                RenderListStatus(visible.Count, _presets.Count);
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // 填满剩余高度：以**屏幕可视底**为准（不再依赖"页面视口高度"的估算）。
        // 底栏是固定条，列表底边必须正好落在它上沿：多一点就被遮住最后一行，少一点就留空。
        private static float ComputeFillTableHeight(Rect last) {
            float minH = SRApi.Ctl.TableHeaderH() + SRApi.Ctl.TableRowH();
            float tableH;
            try {
                float screenTop = SRApi.ContentArea.y + last.yMax;                       // 列表顶（屏幕坐标）
                float bottom = SRApi.ContentArea.yMax - SRApi.FooterHeight - SRApi.Ctl.Sc(2f);   // 底栏上沿
                float avail = bottom - screenTop;   // 不再扣拖拽条：列表底边与底栏上沿直接相接
                tableH = Mathf.Max(minH, avail);
            } catch (Exception __sh) {
                SRApi.Guard.Log("布局预设/可视高度", __sh);
                float used = Mathf.Max(0f, last.yMax - _pageTopY);
                tableH = Mathf.Max(minH, SRApi.PageViewportHeight - used);
            }
            // 注意：不再套用「Table Height」里存的手动高度。
            // 那个值一旦被拖过一次就会永久小于可用高度，列表底部便与底栏之间留出一条缝。
            // 填充模式的原则是"列表始终占满到底栏上沿"。
            return tableH;
        }

        // 筛选/排序变化：回顶并清掉二次确认（必须在本帧控件画完之后读，否则差一帧）
        private static void ApplyFilterChange() {
            int sig = FilterSignature();
            if (sig == _filterSigLast) return;
            _filterSigLast = sig;
            _scrollRows.y = 0f;
            if (!string.IsNullOrEmpty(_pendingKey)) ClearPending();
        }

        // 点空白处取消二次确认（点中操作按钮的帧不清）
        private static void CancelPendingOnBlank(string pendingBefore) {
            try {
                if (!string.IsNullOrEmpty(_pendingKey) && _pendingKey == pendingBefore && !_actionHitThisFrame
                    && Event.current != null && Event.current.type == EventType.MouseUp && Event.current.button == 0)
                    ClearPending();
            } catch (Exception __pe) { SRApi.Guard.Log("布局预设/空白处取消确认", __pe); }
        }


        // 关卡筛选下拉：全部 + 当前预设里出现过的关卡（显示为游戏本地化关卡名，值为 XML levelSceneName）。
        private static void RenderLevelFilterCombo() {
            try {
                List<string> vals = LevelFilterValues();
                if (!string.IsNullOrEmpty(_levelFilter) && !vals.Contains(_levelFilter)) _levelFilter = "";
                int cur = 0;
                for (int i = 0; i < vals.Count; i++) {
                    if (string.Equals(vals[i], _levelFilter, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                // 选项数组由 LevelFilterValues 一并缓存：以前每帧 vals.ToArray() + 新建 labels[]。
                if (_levelOptVals == null || _levelOptVals.Length != vals.Count) {
                    _levelOptVals = vals.ToArray();
                    _levelOptLabels = null;
                }
                string[] labels = _levelOptLabels;
                if (labels == null || labels.Length != vals.Count) {
                    labels = new string[vals.Count];
                    for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : LevelDisplayNameForScene(vals[i]);
                    _levelOptLabels = labels;
                }
                // v2：LevelFilterValues 走异常分支会返回空列表，此时 labels[cur] 越界被外层 catch 吞掉，
                // 表现为下拉框闪一下消失。空数组时直接跳过本帧渲染。
                if (labels.Length == 0 || _levelOptVals == null || _levelOptVals.Length == 0) return;
                cur = Mathf.Clamp(cur, 0, labels.Length - 1);
                bool open = _levelFilterOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], _levelOptVals, labels, ref open, W(110));
                _levelFilterOpen = open;
                if (sel >= 0 && sel < vals.Count) _levelFilter = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染关卡筛选", __ex);
            }
        }

        // 关卡筛选选项：第一项为空（全部），其余为当前预设里出现过的关卡场景名，按本地化关卡名排序。
        private static List<string> LevelFilterValues() {
            int sig = FilterOptionsSignature(true);
            if (sig == _levelOptSig && _levelOpts.Count > 0) return _levelOpts;
            List<string> vals = new List<string>();
            try {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null || string.IsNullOrEmpty(p.Level)) continue;
                    if (seen.Add(p.Level)) vals.Add(p.Level);
                }
                vals.Sort(delegate(string a, string c) {
                    return string.Compare(LevelDisplayNameForScene(a), LevelDisplayNameForScene(c), StringComparison.OrdinalIgnoreCase);
                });
                vals.Insert(0, "");
                _levelOptVals = vals.ToArray();
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : LevelDisplayNameForScene(vals[i]);
                _levelOptLabels = labels;
                _levelOptSig = sig;
                _levelOpts = vals;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/关卡筛选项", __ex);
            }
            return vals;
        }

        // 模式筛选下拉：全部 + 当前预设里出现过的模式（显示为游戏本地化模式名，值为预设目录名 p.Mode）。
        private static void RenderModeFilterCombo() {
            try {
                List<string> vals = ModeFilterValues();
                if (!string.IsNullOrEmpty(_modeFilter) && !vals.Contains(_modeFilter)) _modeFilter = "";
                int cur = 0;
                for (int i = 0; i < vals.Count; i++) {
                    if (string.Equals(vals[i], _modeFilter, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                if (_modeOptVals == null || _modeOptVals.Length != vals.Count) {
                    _modeOptVals = vals.ToArray();
                    _modeOptLabels = null;
                }
                string[] labels = _modeOptLabels;
                if (labels == null || labels.Length != vals.Count) {
                    labels = new string[vals.Count];
                    for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : ModeText(vals[i]);
                    _modeOptLabels = labels;
                }
                if (labels.Length == 0 || _modeOptVals == null || _modeOptVals.Length == 0) return;
                cur = Mathf.Clamp(cur, 0, labels.Length - 1);
                bool open = _modeFilterOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], _modeOptVals, labels, ref open, W(110));
                _modeFilterOpen = open;
                if (sel >= 0 && sel < vals.Count) _modeFilter = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染模式筛选", __ex);
            }
        }

        // 模式筛选选项：第一项为空（全部），其余为当前预设里出现过的模式目录名，按本地化模式名排序。
        private static List<string> ModeFilterValues() {
            int sig = FilterOptionsSignature(false);
            if (sig == _modeOptSig && _modeOpts.Count > 0) return _modeOpts;
            List<string> vals = new List<string>();
            try {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null || string.IsNullOrEmpty(p.Mode)) continue;
                    if (seen.Add(p.Mode)) vals.Add(p.Mode);
                }
                vals.Sort(delegate(string a, string c) {
                    return string.Compare(ModeText(a), ModeText(c), StringComparison.OrdinalIgnoreCase);
                });
                vals.Insert(0, "");
                _modeOptVals = vals.ToArray();
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : ModeText(vals[i]);
                _modeOptLabels = labels;
                _modeOptSig = sig;
                _modeOpts = vals;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/模式筛选项", __ex);
            }
            return vals;
        }

        // 类型筛选下拉：全部 / 预设 / 本地（游戏保存的自定义关卡）。运行期状态，不落配置。
        private static void RenderTypeFilterCombo() {
            try {
                string[] vals = new string[] { "", "preset", "local" };
                string[] labels = new string[] {
                    SRApi.T("全部", "All"),
                    SRApi.T("预设", "Presets"),
                    SRApi.T("本地", "Local")
                };
                int cur = 1; // 默认「预设」
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], _typeFilter, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                // 运行期状态被外部改坏时回退到默认「预设」（与下拉索引 1 对应）。
                if (!string.Equals(vals[cur], _typeFilter, StringComparison.OrdinalIgnoreCase)) _typeFilter = "preset";
                bool open = _typeFilterOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, W(110));
                _typeFilterOpen = open;
                if (sel >= 0 && sel < vals.Length) _typeFilter = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染类型筛选", __ex);
            }
        }

        // 加载模式下拉：默认 + 游戏四模式（派对 / 创意 / 挑战 / 自由）。运行期状态，不落配置。
        // 值为 "" 或 GameState.GameMode 的 ToString；显示名复用 ModeText（游戏本地化 / 中文回退）。
        private static void RenderLoadModeCombo() {
            try {
                string[] vals = new string[] { "", "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY" };
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], _loadMode, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                // 运行期状态被外部改坏时回退到默认，避免下拉框显示空白。
                if (cur == 0 && !string.IsNullOrEmpty(_loadMode)) _loadMode = "";
                string[] labels = new string[vals.Length];
                labels[0] = SRApi.T("默认", "Default");
                for (int i = 1; i < vals.Length; i++) labels[i] = ModeText(vals[i]);
                bool open = _loadModeOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, W(110));
                _loadModeOpen = open;
                if (sel >= 0 && sel < vals.Length) _loadMode = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染加载模式", __ex);
            }
        }

        // 排序下拉：只有 时间 / 名称 两项。
        private static void RenderSortCombo() {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry(Section, "Sort Mode");
                string[] opts = new[] { SRApi.T("时间", "Time"), SRApi.T("名称", "Name") };
                int[] vals = new int[] { 0, 1 };
                int cur = Mathf.Clamp(SortMode, 0, opts.Length - 1);
                bool open;
                if (e == null || !SRApi.Ctl.EditOpen.TryGetValue(e, out open)) open = false;
                int sel = SRApi.Ctl.ComboBox(e, opts[cur], vals, opts, ref open, W(110));
                if (sel >= 0) {
                    if (e != null) SRApi.Ctl.SetValue(e, vals[sel]);
                    if (e != null) SRApi.Ctl.EditOpen[e] = false;
                } else if (e != null) {
                    SRApi.Ctl.EditOpen[e] = open;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染排序下拉", __ex);
            }
        }

        // 内联重命名：右键名称单元格后，该行名称格就地变成输入框（不再另开编辑条）。
        // 提交：回车 / 点击别处；取消：Esc。
        private static void DrawInlineRename() {
            try {
                if (string.IsNullOrEmpty(_renameKey) || _args == null) return;
                if (_args.rowRects == null || _args.rowRows == null || _args.vis == null) return;
                List<PresetInfo> vis = _visibleCache;
                if (vis == null) return;

                // 目标在"全部可见行"里的下标。
                int row = -1;
                for (int i = 0; i < vis.Count; i++) {
                    if (vis[i] != null && vis[i].Key == _renameKey) { row = i; break; }
                }
                PresetInfo p = (row >= 0 && row < vis.Count) ? vis[row] : null;
                if (p == null) { _renameKey = null; _renameText = null; return; }

                // rowRects 只含【当前视口内的行】（TableRowRects 从 first 到 last，rowRows 记真实行号）。
                // 以前直接用 vis 的下标去索引 rowRects[row] —— 滚动之后输入框会画到别的行上，
                // 甚至因 row >= rowRects.Count 而完全不画、状态还留着。
                Rect rowRect = default(Rect);
                bool onScreen = false;
                for (int k = 0; k < _args.rowRects.Count && k < _args.rowRows.Count; k++) {
                    if (_args.rowRows[k] == row) { rowRect = _args.rowRects[k]; onScreen = true; break; }
                }
                if (!onScreen) {
                    // 目标滚出视口：把它滚回来再画，不让编辑框凭空消失或卡住。
                    // 不在这里 clamp 到上一帧的 _maxScrollY：交给下一帧 RenderPresetTable 用真实值夹取。
                    _scrollRows.y = Mathf.Max(0f, row * SRApi.Ctl.TableRowH());
                    return;
                }

                // 名称列横向偏移：必须减 scrollX（与 ColumnAtX 同名列判定一致），并跳过钉住的操作列。
                // 名称列被隐藏时不画输入框，直接取消编辑。
                float x = _args.box.x - _args.scrollX;
                bool found = false;
                for (int k2 = 0; k2 < _args.vis.Count; k2++) {
                    int ci = _args.vis[k2];
                    if (ci == _args.pinCol) continue;
                    if (ci == ColName) { found = true; break; }
                    x += SRApi.Ctl.Sc(_colW[ci]);
                }
                if (!found) { _renameKey = null; _renameText = null; return; }

                Rect cell = new Rect(x, rowRect.y + W(1f), SRApi.Ctl.Sc(_colW[ColName]) - W(2f), Mathf.Max(W(14f), rowRect.height - W(2f)));
                GUI.BeginGroup(cell);
                try {
                    string typed = SRApi.ClearableTextField(_renameText ?? p.Name, 40, SRApi.Ctl.SearchBox,
                        GUILayout.Width(cell.width), GUILayout.Height(cell.height));
                    if (typed != null && typed != _renameText) _renameText = typed;
                } finally { GUI.EndGroup(); }

                Event e = Event.current;
                if (e == null) return;
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) {
                    // 空名不要提交：CommitRename 会拒绝但旧写法无条件清状态，用户刚打的字直接丢了。
                    if ((_renameText ?? "").Trim().Length == 0) {
                        SRApi.Notify(SRApi.T("名称不能为空", "The name cannot be empty"));
                    } else {
                        CommitRename(p);
                        _renameKey = null; _renameText = null;
                    }
                    e.Use(); return;
                }
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) {
                    _renameKey = null; _renameText = null; e.Use(); return;
                }
                // 点空白提交：不判 e.Used（文本框会先吃掉 MouseDown）。
                if (e.type == EventType.MouseDown && !cell.Contains(e.mousePosition)) {
                    if ((_renameText ?? "").Trim().Length != 0) CommitRename(p);
                    _renameKey = null; _renameText = null;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/内联重命名", __ex); }
        }

        // 标准表格：核心 TableDraw 负责表头/行/拖列宽/换列位/钉住操作列；本页只提供列定义、单元格文本与操作按钮。
        private static void RenderPresetTable(float tableH, List<PresetInfo> visible) {
            try {
                _visibleCache = visible;   // 悬浮预览按行号取预设时用（与 RowCells 同一份列表）
                float rowH = SRApi.Ctl.TableRowH();
                float headerH = SRApi.Ctl.TableHeaderH();
                // Render() 传入的 tableH 是「分区标题 + 表格」可用的总高度；分区标题本身也占高，
                // 这里扣掉它的实测高度，否则表格底部会超出可视区、最后一行被底栏裁掉且滚不到。
                // 不再二次扣减：调用方给的 tableH 已经是"可用总高度"，而分区标题在 GetLastRect 里
                // 也已经被算进 last.yMax（TableFitHeight 的 used）。再扣一次 titleH + 拖拽条，
                // 表格就会比可用区域矮一截 —— 列表中下方那片空白的直接来源。
                // 不再抬到 120：按调用方给的内容高度画，行少时不留空白表体。
                float __gripH = 0f;   // 已删除底边拖拽条
                // 行区 = 表格高 − 表头 − 拖拽条：把拖拽条算进来会让 maxY 恒为 0（列表因此永远滚不动）。
                float rowsH = Mathf.Max(0f, tableH - headerH - __gripH);
                Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(tableH));
                EnsureCols();
                // 操作列宽始终跟随当前语言的按钮文字：语言热切换后也能立刻避免英文标签被截断。
                // 操作列：默认按按钮文字实测，但最多占表格宽度的 30%（窗口变窄时不会把数据列挤没）
                _colW[ColAction] = Mathf.Min(DefaultActionColW(), Mathf.Max(SRApi.Ctl.Sc(96f), box.width * 0.30f));
                if (_args == null) _args = new SRApi.TableArgs();
                float pinW = SRApi.Ctl.Sc(_colW[ColAction]);
                // 有纵向溢出时，在数据区与钉住列之间预留滚动条宽度（与联机列表一致）。
                // 滚动条已全局隐藏（SR.Table.HideTableScrollbars），这里也不再为它留宽度：
                // 数据区直接吃满到钉住列，右侧不再空一条。
                float __sbW = 0f;

                float __pinMax = Mathf.Max(SRApi.Ctl.Sc(40f), box.width - SRApi.Ctl.Sc(80f));
                if (pinW > __pinMax) pinW = __pinMax;
                float stripW = Mathf.Max(0f, box.width - pinW - __sbW);
                // 列策略（对齐 ImGui 的 WidthStretch）：名称列做"拉伸列"——先把其他可见固定列
                // 的宽度扣掉，剩下的全给它（下限 80 设计单位）。这样窗口宽时不留空、窗口窄时
                // 其他列溢出并自动出现横向滚动；用户拖名称列宽会被下一帧重新拉伸（与 ImGui 一致：
                // 拉伸列不单独可调）。
                // 拉伸列只在**绘制副本**里生效，不动持久化宽度（否则一次拖拽会把拉伸值写进配置）。
                if (_drawW == null || _drawW.Length != _colW.Length) _drawW = new float[_colW.Length];
                System.Array.Copy(_colW, _drawW, _colW.Length);
                float __fixedOther = 0f;
                for (int i = 0; i < _order.Length; i++) {
                    int __ci = _order[i];
                    if (__ci == ColAction || __ci == ColName || _hiddenCols.Contains(__ci)) continue;
                    __fixedOther += SRApi.Ctl.Sc(_colW[__ci]);
                }
                float __nameW = Mathf.Max(SRApi.Ctl.Sc(80f), stripW - __fixedOther);
                _drawW[ColName] = __nameW / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f));
                float totalW = 0f;
                for (int i = 0; i < _order.Length; i++) {
                    int ci = _order[i];
                    if (ci == ColAction) continue;
                    if (_hiddenCols.Contains(ci)) continue;   // 隐藏列不占宽度（否则能横向滚进空白）
                    totalW += SRApi.Ctl.Sc(_drawW[ci]);
                }
                _maxScrollX = Mathf.Max(0f, totalW - stripW);
                _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _maxScrollX);
                _rowsHCache = rowsH;   // 缓存真实行区高：滚轮钩子在首帧/刚切页时用它兜底
                _maxScrollY = Mathf.Max(0f, visible.Count * rowH - rowsH + rowH);   // 留一整行余量：最后一整行都能露出来
                _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _maxScrollY);
                Event e = Event.current;
                Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
                _args.box = box;
                _args.rowsClip = new Rect(box.x, box.y + headerH, stripW, rowsH);
                _args.headerH = headerH;
                _args.rowH = rowH;
                _args.rowsH = rowsH;
                _args.stripW = stripW;
                _args.colW = _drawW;
                _args.vis = _vis;
                _args.rowCount = visible.Count;
                _args.pinCol = ColAction;
                _args.alignLeftCol = ColName;
                _args.scrollX = _scrollCols.x;
                _args.scrollY = _scrollRows.y;
                _args.mp = mp;
                _args.pinW = pinW;
                _args.pinX = box.x + stripW;
                _args.pinTitle = SRApi.T("操作", "Actions");
                _args.drawHeader = true;
                _args.headerLeft = false;
                _args.title = delegate(int ci) { return ColumnTitle(ci); };
                _args.tip = ColumnTip;
                _args.rowCells = delegate(int r) { return RowCells(visible, r); };
                _args.rowSelected = null;
                _args.rowAlpha = null;
                _args.resizable = true;
                _args.reorderable = true;
                _args.order = _order;
                _args.applyOrder = delegate(int[] o) {
                    _order = o;
                    SaveCols();
                    try { SRApi.LogInfo("[布局预设] 列序更新: " + JoinInts(_order)); } catch (Exception) { }
                };
                _args.saveOrder = delegate { SaveCols(); };
                _args.saveWidths = delegate {
                    SaveCols();
                    try { SRApi.LogInfo("[布局预设] 列宽更新: " + JoinFloats(_colW)); } catch (Exception) { }
                };
                _args.hidden = _hiddenCols;
                _args.hideable = true;
                _args.resetAll = delegate { ResetTable(); };
                _args.defaultColW = DefColW;   // 交给核心：Shift+右键时列宽一并还原到默认值
                _args.click = delegate(int ci) {
                    if (ci != ColLevel) return;
                    _levelShowCode = !_levelShowCode;
                    try { SRApi.LogInfo("[布局预设] 关卡列显示: " + (_levelShowCode ? "代码" : "名称")); } catch (Exception) { }
                };
                _args.sortCol = -1;
                _args.headerCells = _headerCells;
                _args.headerCols = _headerCols;
                _args.rowRects = _rowRects;
                _args.rowRows = _rowRows;
                _args.pinRects = _pinRects;
                _args.pinRows = _pinRows;
                _args.pinDraw = delegate(int row, Rect r, int visibleIdx) { DrawActionButtons(visible, row, r); };
                _args.emptyHint = delegate {
                    return _presets.Count == 0
                        ? SRApi.T("还没有预设：在「保存与导入」区保存当前布局，或从剪贴板 / 文件导入。", "No presets yet: use the Save & Import section to save the current layout, or import from clipboard / file.")
                        : SRApi.T("没有匹配当前搜索的预设。", "No presets match the current search.");
                };
                _hiddenBeforeTable = _hiddenCols.Count;
                _tableBox = box;      // 供页面级滚轮钩子命中判定
                // 滚轮：绘制期直接用表格矩形取用（同一坐标系，不存在换算错位）
                {
                    float __w = SRApi.Ctl.WheelTake(box);
                    if (Mathf.Abs(__w) > 0.01f) ScrollTableBy(__w);
                }
                SRApi.TableDraw(_args);
                // 拖列宽的结果在 _drawW 里（核心直接改传进去的数组）：除拉伸列外同步回持久化宽度
                for (int i = 0; i < _colW.Length && i < _drawW.Length; i++) {
                    if (i == ColName) continue;
                    if (Mathf.Abs(_colW[i] - _drawW[i]) > 0.01f) _colW[i] = _drawW[i];
                }
                HandlePresetCells();   // 数据列：悬浮预览 / 左键切三态 / 右键开界面；名称列：右键重命名
                // 不画底边拖拽条：预设列表不需要手动调高（整块区域就是列表）。
                // 竖向滚动条：几何、绘制、输入全部由核心按 TableArgs 计算（数据区右侧、钉住列之前）。
                _sbHandled = SRApi.Ctl.TableScrollbarFor(_args, e, ref _sbDragging, ref _sbGrabY, ref _scrollRows.y);
                if (_hiddenCols.Count > _hiddenBeforeTable) {
                    // 核心在右键表头时把列加进 hidden 集合；这里落盘持久化（Shift+右键清空由 resetAll 回调处理）。
                    SaveCols();
                    SRApi.LogInfo("[布局预设] " + SRApi.T("已隐藏列（Shift+右键表头可恢复全部列）", "Column hidden (Shift+right-click the header to restore all columns)"));
                }

                if (e != null) {
                    // 拖拽兜底复位（拖列宽 / 换列位由核心处理，这里只接住状态与落盘回调）。
                    SRApi.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _args.moveCol, ref _args.moveDx,
                        ref _args.dragCol, ref _dragH, SaveCols, null, null);
                    if (!_sbHandled)
                        SRApi.TableWheelInput(e, box, mp, _maxScrollX, _maxScrollY, ref _scrollCols, ref _scrollRows);
                    // 已删除 Input.GetAxis("Mouse ScrollWheel") 兜底：
                    // 页面级钩子 OnPresetWheel 在 BeginScrollView 之前已处理滚轮并 e.Use()，
                    // 把事件改成 Used 后 TableWheelInput 会自动跳过；但 GetAxis 是"每帧轮询"，
                    // 既不检查事件类型也不检查 WheelTakenThisFrame，会在同一帧再滚一次（40 + 0.1*400）。
                    // 结果就是一次滚轮滚两行、手感发飘。现在滚轮只有页面钩子一个入口。
                    if (e.type == EventType.MouseUp) SRApi.BlockWindowDrag = false;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/表格绘制", __ex);
            }
        }

        // Shift+右键表头：核心还原列宽并清空隐藏列后回调这里，再补一次"列序 + 列宽 + 隐藏"的整表还原并落盘。
        private static void ResetTable() {
            try {
                if (_tableCols != null) _tableCols.ResetAll();   // 列序 / 隐藏 / 列宽一起回默认（核心统一实现）
                for (int i = 0; i < ColCount; i++) _colW[i] = DefColW[i];
                _hiddenCols.Clear();
                SaveCols();
                SRApi.LogInfo("[布局预设] " + SRApi.T("已恢复显示全部列", "All columns are shown again"));
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/恢复全部列", __ex);
            }
        }

        private static string ColumnTitle(int ci) {
            switch (ci) {
                case ColName: return SRApi.T("名称", "Name");
                case ColLevel: return _levelShowCode ? SRApi.T("关卡代码", "Level code") : SRApi.T("关卡", "Level");
                case ColMode: return SRApi.T("模式", "Mode");
                case ColMods: return SRApi.T("数据", "Data");
                case ColTime: return SRApi.T("时间", "Time");
                case ColAction: return SRApi.T("操作", "Actions");
                default: return "";
            }
        }

        private static string ColumnTip(int ci) {
            try {
                switch (ci) {
                    case ColName: return SRApi.T("预设名称", "preset name");
                    case ColLevel: return SRApi.T("预设所属关卡；单击「关卡」表头可在关卡名称 / 关卡代码之间切换（代码缺失显示 -）",
                        "the level this preset belongs to; click the Level header to toggle between level name and level code (code shows - when unavailable)");
                    case ColMode: return SRApi.T("保存时所在的游戏模式", "game mode the preset was saved in");
                    case ColMods: return SRApi.T("这条预设带的自定义数据\n默认 = 全部用预设自带；自定义 = 修改器/规则/积分/模块概率按你的设定\n鼠标停在该列格子上可预览具体内容",
                        "custom data carried by this preset\nDefault = use whatever the preset has; Custom = your own modifiers/rules/points/block odds\nhover a cell in this column to preview the details");
                    case ColTime: return SRApi.T("保存时间（本地时间）", "save time (local time)");
                    case ColAction: return SRApi.T("加载 / 数据 / 导出到剪贴板 / 删除；删除需点两次确认\n重命名：右键「名称」列；自定义数据：右键「数据」列", "load / data / export to clipboard / delete; delete needs a second click\nrename: right-click the Name column; custom data: right-click the Data column");
                    default: return null;
                }
            } catch { return null; }
        }

        // 单元格显示签名：语言 / 关卡显示模式 / 名称 / 关卡 / 模式 / 时间 / 数据策略任一变化都重算。
        private static int CellSignature(PresetInfo p) {
            unchecked {
                int h = 11;
                h = h * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                h = h * 31 + (_levelShowCode ? 1 : 0);
                h = h * 31 + (p.Name ?? "").GetHashCode();
                h = h * 31 + (p.Level ?? "").GetHashCode();
                h = h * 31 + (p.LevelName ?? "").GetHashCode();
                h = h * 31 + (p.LevelCode ?? "").GetHashCode();
                h = h * 31 + (p.Mode ?? "").GetHashCode();
                h = h * 31 + p.TimeUtc.GetHashCode();
                h = h * 31 + (p.ModsMode ?? "").GetHashCode();
                h = h * 31 + (p.IsLocal ? 1 : 0);
                return h;
            }
        }

        private static string[] RowCells(List<PresetInfo> visible, int r) {
            PresetInfo p = PresetAt(visible, r);
            if (p == null) {
                for (int i = 0; i < ColCount; i++) _cells[i] = "";
                return _cells;
            }
            // 单元格文本缓存：核心 TableDraw 只读该数组（SR.Table.cs:232 取用后直接传给 DrawTableCells），
            // 复用安全。以前每可见行每趟 OnGUI 都做 5 次字符串生成 + 本地化，20 行就是上百次/帧。
            int sig = CellSignature(p);
            string key = p.Key;
            CellCacheEntry ent;
            if (_presetCellCache.TryGetValue(key, out ent) && ent.Sig == sig) return ent.Cells;
            if (ent == null) {
                if (_presetCellCache.Count > 256) _presetCellCache.Clear();
                ent = new CellCacheEntry();
                _presetCellCache[key] = ent;
            }
            string[] c = ent.Cells;
            c[ColName] = p.IsLocal
                ? SRApi.T("本地", "Local") + " · " + (p.Name ?? "")
                : (p.Name ?? "");
            c[ColLevel] = _levelShowCode ? LevelCodeText(p) : LevelText(p);
            c[ColMode] = ModeText(p.Mode);
            c[ColMods] = ModsLabel(p);
            c[ColTime] = TimeText(p);
            c[ColAction] = "";
            ent.Sig = sig;
            return c;
        }

        private static PresetInfo PresetAt(List<PresetInfo> visible, int row) {
            try { return (visible != null && row >= 0 && row < visible.Count) ? visible[row] : null; }
            catch { return null; }
        }

        // 钉住操作列：每行 4 个按钮（加载 / 数据 / 导出 / 删除），删除在二次确认窗口内显示「确认」；重命名走右键名称列。
        // 按钮布局、命中、悬浮全部由核心统一控件处理；本页只填按钮描述，点击由 DoAct* 分发。
        private static void DrawActionButtons(List<PresetInfo> visible, int row, Rect r) {
            try {
                PresetInfo p = PresetAt(visible, row);
                if (p == null) return;
                _actPreset = p;
                bool replacePending = IsPending("replace:" + p.Key);
                int hover = -1;
                if (p.IsLocal) {
                    // 本地关卡只给「加载」一个按钮
                    _actLocalActions[0].Label = replacePending ? SRApi.T("确认", "Confirm") : SRApi.T("加载", "Load");
                    if (SRApi.TableActionButtons(r, _actLocalActions, null, ref hover) >= 0) _actionHitThisFrame = true;
                    return;
                }
                bool delPending = IsPending("del:" + p.Key);
                _actActions[ActLoad].Label = replacePending ? SRApi.T("确认", "Confirm") : SRApi.T("加载", "Load");
                _actActions[ActExport].Label = SRApi.T("导出", "Export");
                _actActions[ActDelete].Label = delPending ? SRApi.T("确认", "Confirm") : SRApi.T("删除", "Delete");
                int hit = SRApi.TableActionButtons(r, _actActions, null, ref hover);
                if (hit >= 0) _actionHitThisFrame = true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/操作按钮", __ex);
            }
        }

        // 操作按钮点击分发（由核心在左键 MouseUp 命中后回调）：加载 / 重命名 / 导出 / 删除（两次确认）。
        private static void DoActLoad() {
            PresetInfo p = _actPreset;
            if (p != null) LoadPreset(p);
        }

        private static void DoActExport() {
            PresetInfo p = _actPreset;
            if (p != null && !p.IsLocal) ExportPreset(p);
        }

        private static void DoActDelete() {
            PresetInfo p = _actPreset;
            if (p != null && !p.IsLocal && ConfirmOrArm("del:" + p.Key)) DeletePreset(p);
        }

        // ---------- 列宽 / 列序持久化 ----------

        // 首次进入列表页时读配置：列宽按范围裁剪、列序必须是 0..ColCount-1 的合法排列且操作列固定在最后。
        private static void EnsureCols() {
            if (_colsLoaded) return;
            _colsLoaded = true;
            try {
                float[] dw = new float[ColCount];
                for (int i = 0; i < ColCount; i++) dw[i] = DefColW[i];
                _tableCols = new SRApi.TableState(_colWidthsEntry, _colOrderEntry, _colHiddenEntry, dw);
                for (int i = 0; i < ColCount; i++) {
                    float w = _tableCols.Widths[i];
                    _colW[i] = (w >= ColWidthMin && w <= ColWidthMax) ? w : DefColW[i];
                }
                _order = NormalizeOrder(_tableCols.Order);
                _hiddenCols.Clear();
                foreach (int h in _tableCols.Hidden) if (h >= 0 && h < ColCount && h != ColAction) _hiddenCols.Add(h);
                // 操作列宽度强制用默认值（按当前语言按钮文字实测）。
                _colW[ColAction] = DefaultActionColW();
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/读取列设置", __ex);
            }
        }

        // 操作列默认宽 = 4 个按钮的自适应文字宽 + 按钮间距 + 钉住列左右边距（设计单位）。
        // 每行按钮由核心按 BtnClip.CalcSize(label).x + 11px 计算，这里用同一公式取最宽标签（含二次确认的「确认」），
        // 保证中文 / 英文、普通态 / 确认态都不会被截断；DefColW[ColAction] 仅作样式不可用时的兜底。
        // 操作列默认宽：交给核心按当前语言的按钮文字实测（数据按钮删掉后不再固定 200，右侧不留空）。
        private static float DefaultActionColW() {
            try {
                float w = SRApi.TableActionColumnWidth(_actActions);
                if (w > 0f) return Mathf.Clamp(w, ColWidthMin, ColWidthMax);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/操作列宽", __ex); }
            return DefColW[ColAction];
        }

        // 表格列设置落盘/日志用的小工具（逗号分隔，便于对照 cfg）。
        private static string JoinInts(int[] a) {
            try {
                if (a == null) return "";
                string[] p = new string[a.Length];
                for (int i = 0; i < a.Length; i++) p[i] = a[i].ToString(CultureInfo.InvariantCulture);
                return string.Join(",", p);
            } catch { return ""; }
        }

        private static string JoinFloats(float[] a) {
            try {
                if (a == null) return "";
                string[] p = new string[a.Length];
                for (int i = 0; i < a.Length; i++) p[i] = Mathf.RoundToInt(a[i]).ToString(CultureInfo.InvariantCulture);
                return string.Join(",", p);
            } catch { return ""; }
        }

        // 列序归一化：只允许前 4 列换位，操作列强制固定在最后；非法/缺列时补回默认相对顺序。
        private static int[] NormalizeOrder(int[] src) {
            int[] def = new int[ColCount];
            for (int i = 0; i < ColCount; i++) def[i] = i;
            try {
                if (src == null || src.Length != ColCount) return def;
                bool[] seen = new bool[ColCount];
                List<int> seq = new List<int>(ColCount);
                for (int i = 0; i < src.Length; i++) {
                    int k = src[i];
                    if (k < 0 || k >= ColCount || k == ColAction || seen[k]) continue;
                    seen[k] = true;
                    seq.Add(k);
                }
                for (int k = 0; k < ColCount; k++) {
                    if (k == ColAction || seen[k]) continue;
                    seen[k] = true;
                    seq.Add(k);
                }
                seq.Add(ColAction);
                return seq.ToArray();
            } catch { return def; }
        }

        private static void SaveCols() {
            try {
                EnsureCols();
                // 兜底：长度不符就别写盘（写进去会被 NormalizeOrder 永久降级成默认列序）
                if (_order == null || _order.Length != ColCount) _order = NormalizeOrder(_order);
                if (_tableCols == null) return;
                for (int i = 0; i < ColCount && i < _tableCols.Widths.Length; i++) _tableCols.Widths[i] = _colW[i];
                _tableCols.Order = _order;
                _tableCols.Hidden.Clear();
                foreach (int h in _hiddenCols) _tableCols.Hidden.Add(h);
                _tableCols.Save();
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/保存列设置", __ex); }
        }

        // ---------- 二次确认 ----------

        private static bool IsPending(string key) {
            try {
                return key != null && _pendingKey == key && _pendingAt >= 0f
                    && Time.unscaledTime - _pendingAt <= ConfirmWindow;
            } catch { return false; }
        }

        private static bool IsPendingPrefix(string prefix) {
            try {
                return !string.IsNullOrEmpty(_pendingKey) && _pendingKey.StartsWith(prefix, StringComparison.Ordinal)
                    && _pendingAt >= 0f && Time.unscaledTime - _pendingAt <= ConfirmWindow;
            } catch { return false; }
        }

        // 第一次调用只登记待确认动作并返回 false；窗口内第二次调用返回 true 并清掉登记。
        private static bool ConfirmOrArm(string key) {
            if (IsPending(key)) {
                ClearPending();
                return true;
            }
            _pendingKey = key;
            _pendingAt = Time.unscaledTime;
            return false;
        }

        private static void ClearPending() {
            _pendingKey = null;
            _pendingAt = -1f;
        }

        private static string SaveButtonText() {
            return IsPendingPrefix("overwrite:") ? SRApi.T("确认覆盖", "Confirm overwrite") : SRApi.T("保存当前布局", "Save layout");
        }

    }
}
