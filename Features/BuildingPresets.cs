// 布局预设库（sr.buildingpresets）：把当前方块布局存成命名预设，支持列表管理 / 加载 / 导入导出。
// 存储：BepInEx/config/SR_UCH/presets/<模式>/<安全文件名>.xml + 同目录 index.json（元数据缓存）。
// 约束：全部功能同程序集编译为单一 SR_UCH.dll；功能层经 SRApi 契约访问实现层，不得直接引用 SR.* 私有状态。
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
    [SrFeature("sr.buildingpresets", "BuildingPresets", "1.0")]
    public partial class BuildingPresets : ITweak {
        // 配置段名：导航栏目名 / LocSec / Config.Bind 三处必须完全一致。
        private const string Section = "Building Presets";
        private const string IndexFileName = "index.json";
        private const int IndexVersion = 4;
        // index.json 每条记录的 schema 版本：缺失 = 旧格式。
        private const int IndexSchema = 1;
        // 排序方式：0=时间（新→旧），1=名称（A→Z）。
        // 列索引（与 TableArgs.colW 顺序一致）：名称 / 关卡 / 模式 / 数据 / 时间 / 操作（操作列钉右）。
        private const int ColName = 0;
        private const int ColLevel = 1;
        private const int ColMode = 2;
        private const int ColMods = 3;
        private const int ColTime = 4;
        private const int ColAction = 5;
        private const int ColCount = 6;
        // 列宽由核心 TableColumn 模板 + 文字实测推导（换算回设计单位，渲染时再 Sc）；操作列宽由 DefaultActionColW() 实测。
        private static readonly SRApi.TableColumn[] _presetCols = new SRApi.TableColumn[] {
            new SRApi.TableColumn { TitleZh = "名称", TitleEn = "Name", TipZh = "预设名称", TipEn = "preset name", MinW = 120f, PadW = 22f, AlignLeft = true },
            new SRApi.TableColumn { TitleZh = "关卡", TitleEn = "Level", TipZh = "关卡", TipEn = "level", MinW = 64f, PadW = 22f },
            new SRApi.TableColumn { TitleZh = "模式", TitleEn = "Mode", TipZh = "模式", TipEn = "mode", MinW = 52f, PadW = 22f },
            new SRApi.TableColumn { TitleZh = "数据", TitleEn = "Data", TipZh = "数据", TipEn = "data", MinW = 64f, PadW = 22f },
            new SRApi.TableColumn { TitleZh = "时间", TitleEn = "Time", TipZh = "时间", TipEn = "time", MinW = 76f, PadW = 22f },
            new SRApi.TableColumn { TitleZh = "操作", TitleEn = "Actions", TipZh = "操作", TipEn = "actions", MinW = 200f, PadW = 22f },
        };
        private static float[] _defColWComputed;
        private static float[] DefColW {
            get {
                if (_defColWComputed != null) return _defColWComputed;
                float[] px = null;
                try { px = SRApi.Ctl.TableColumnWidths(_presetCols); } catch (Exception __cw) { SRApi.Guard.Log("预设列表/默认列宽", __cw); }
                float scale = Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f));
                float[] result = new float[ColCount];
                for (int i = 0; i < ColCount; i++) {
                    float v = (px != null && i < px.Length) ? px[i] / scale : 80f;
                    result[i] = Mathf.Clamp(Mathf.Round(v), 40f, 400f);
                }
                _defColWComputed = result;
                return result;
            }
        }
        private const float ColWidthMin = 40f;
        private const float ColWidthMax = 600f;
        private const string ColWidthsKey = "Preset Widths";
        private const string ColOrderKey = "Preset Order";
        private const string ColHiddenKey = "Preset Hidden";
        private const int ActLoad = 0;
        private const int ActExport = 1;
        private const int ActDelete = 2;
        // 剪贴板导入上限：方块数 / XML 字符数。
        private const int MaxImportBlocks = 3000;
        private const int MaxImportChars = 2000000;
        // 游戏本地关卡快照：7z 文件大小 / 解压后 XML 大小上限
        private const int MaxSnapshotBytes = 4 * 1024 * 1024;
        private const int MaxLocalXmlBytes = 8 * 1024 * 1024;
        private const float ConfirmWindow = 5f;
        private const float ReloadLockStale = SrTimeouts.ReloadLockStaleSeconds;   // 单一真值：契约层 SrTimeouts
        private const float PresetConsumeTimeout = SrTimeouts.PresetConsumeTimeoutSeconds;   // 单一真值：契约层 SrTimeouts
        private static ConfigEntry<int> _maxPresets;
        private static ConfigEntry<bool> _skipTreehouse;   // 跳过树屋流程（联机直接替换关卡，风险自负）
        private static bool _actionHitThisFrame;   // 本帧是否命中操作按钮（空白处取消二次确认）
        private static ConfigEntry<string> _filterTypeEntry, _filterLevelEntry, _filterModeEntry;
        private static ConfigEntry<string> _colWidthsEntry;
        private static ConfigEntry<string> _colOrderEntry;
        private static ConfigEntry<string> _colHiddenEntry;
        private static ConfigEntry<int> _tableHEntry;
        private static int _sortColOverride = -1;   // 表头点击排序的临时覆盖（-1 = 用 Sort Mode 下拉）
        private static bool _sortAsc = true;
        private static string _rootDir;

        private static bool _loaded;
        private static readonly List<PresetInfo> _presets = new List<PresetInfo>();

        // UI 状态：名称输入框、状态文案、二次确认、重命名、搜索词、路径输入框。
        private static string _newName = "";
        private static string _status = "";
        private static string _pendingKey;
        private static float _pendingAt = -1f;
        private static string _renameKey;
        private static string _renameText;
        // 条件控件的"本帧是否显示"锁存（Layout / Repaint 必须一致）。
        private static bool _renameFocusPending;   // 打开重命名弹窗后是否还要抢一次输入焦点
        private static string _search = "";
        private static string _importPath = "";
        private static string _levelFilter = "";
        private static bool _levelFilterOpen;
        private static string _modeFilter = "";
        private static bool _modeFilterOpen;
        // 类型筛选（运行期状态）："" = 全部，preset = 预设，local = 本地关卡。
        private static string _typeFilter = "";   // 默认「全部」（presets + 本地存档），否则只剩自己保存的几条
        private static bool _typeFilterOpen;
        // 加载模式（运行期状态）："" = 跟随预设自身模式，否则强制使用该模式。
        private static string _loadMode = "";
        private static bool _loadModeOpen;

        // 标准表格状态：列宽 / 列序 / 隐藏列 / 可见列 / 命中矩形 / 滚动 / 拖拽。
        private static readonly float[] _colW = new float[ColCount];
        private static float[] _drawW;   // 绘制用列宽副本（拉伸列只改它）
        // 初值长度必须与 ColCount 一致：长度不符时 DeriveVisibleCols 会直接 return，表格一列不画。
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
        private static bool _dragH;
        private static float _dragWatchdog;

        // ---------- 列表数据管线缓存 ----------
        // 可见行由"数据 + 筛选 + 排序"签名驱动，签名不变时跨帧复用。
        private static int _visSig = int.MinValue;
        private static int _sigFrame = -1, _sigCache;
        // 筛选下拉按 _presets 的 Level/Mode 签名缓存，避免每帧建 List + HashSet + 排序。
        private static int _levelOptSig = int.MinValue, _modeOptSig = int.MinValue;
        private static List<string> _levelOpts = new List<string>(), _modeOpts = new List<string>();
        private static string[] _levelOptVals = new string[0], _levelOptLabels = new string[0];
        private static string[] _modeOptVals = new string[0], _modeOptLabels = new string[0];
        // 单元格文本缓存：key 用预设的稳定标识串（不用 PresetInfo 引用，避免旧对象累积）；
        // 值带显示签名，上限 256，超出整体清空（预设上限 100）。
        private sealed class CellCacheEntry {
            public int Sig;
            public readonly string[] Cells = new string[ColCount];
        }
        private static readonly Dictionary<string, CellCacheEntry> _presetCellCache = new Dictionary<string, CellCacheEntry>(64);
        private static int _filterSigLast = int.MinValue;
        // 标签宽度缓存（按语言签名）；FormLabelWidth 每帧被调用 7 次。
        private static int _formLabelSig = int.MinValue;
        private static float _formLabelW = 90f;
        private static string _statusShown;
        private static float _statusShownAt = -1000f;
        private static int _optSigFrame = -1, _optSigLevel, _optSigMode;
        private static bool _levelShowCode;

        private static string _reloadBusyReason;   // 锁的来源
        private static float _otherBusySince = -1f;   // 对方重载锁开始等待的时刻（-1 = 没在等）
        private static bool _reloadBusy;
        private static float _reloadBusyAt = -1f;

        // 一条预设的展示元数据；磁盘 XML 是权威数据，index.json 只是缓存。
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
        // 修改器策略："" = 预设自带；"current" = 游戏当前值；"off" = 全部还原默认。
            public string ModsMode = "";
            // 加载时是否一并应用 游戏规则 / 积分设置 / 模块概率（存于 sidecar）。
            public bool ApplyRules, ApplyPoints, ApplyBlocks;
        public Dictionary<string, string> ModsVals;   // 旧索引（custom）遗留值；新数据不再写入
            // 来源：false = 本功能预设目录；true = 游戏本地保存的关卡（.snapshot，只读）。
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
                try {
                    if (_maxPresets.Value == 50) _maxPresets.Value = 100;
                    else if (_maxPresets.Value < 1) _maxPresets.Value = 1;
                    else if (_maxPresets.Value > 100) _maxPresets.Value = 100;
                } catch (Exception __mxp) { SRApi.Guard.Log("布局预设/裁剪数量上限", __mxp); }
                // 排序统一由表头点击承担（点列头切升降序），不再保留独立的排序下拉配置项。
                // 类型/关卡/模式筛选落盘；搜索框不存（临时输入）。
                _filterTypeEntry = plugin.Config.Bind(Section, "Filter Type", "",
                    "列表页类型筛选：空=全部 / preset=仅预设 / local=仅本地存档");
                _filterLevelEntry = plugin.Config.Bind(Section, "Filter Level", "",
                    "列表页关卡筛选：空=全部，其余为关卡场景名");
                _filterModeEntry = plugin.Config.Bind(Section, "Filter Mode", "",
                    "列表页模式筛选：空=全部，其余为模式名");
                _typeFilter = _filterTypeEntry.Value ?? "";
                _levelFilter = _filterLevelEntry.Value ?? "";
                _modeFilter = _filterModeEntry.Value ?? "";
                System.EventHandler __h_filterType = delegate { _typeFilter = _filterTypeEntry.Value ?? ""; };
                _filterTypeEntry.SettingChanged += __h_filterType;
                Sub.Revoke(() => _filterTypeEntry.SettingChanged -= __h_filterType);
                System.EventHandler __h_filterLevel = delegate { _levelFilter = _filterLevelEntry.Value ?? ""; };
                _filterLevelEntry.SettingChanged += __h_filterLevel;
                Sub.Revoke(() => _filterLevelEntry.SettingChanged -= __h_filterLevel);
                System.EventHandler __h_filterMode = delegate { _modeFilter = _filterModeEntry.Value ?? ""; };
                _filterModeEntry.SettingChanged += __h_filterMode;
                Sub.Revoke(() => _filterModeEntry.SettingChanged -= __h_filterMode);
                _skipTreehouse = plugin.Config.Bind(Section, "Skip Treehouse", false,
                    "跳过树屋流程：勾选后联机替换关卡不再先回树屋，而是直接替换场景（房客的 SelectedLevel 可能不同步，风险自负）。");
                // 列表列宽 / 列序（由表头拖动维护，不在页面上渲染编辑框）。
                _colWidthsEntry = plugin.Config.Bind(Section, ColWidthsKey, "",
                    "内部状态：预设列表列宽（逗号分隔的设计单位，由表头拖动自动写入）。");
                _colOrderEntry = plugin.Config.Bind(Section, ColOrderKey, "",
                    "内部状态：预设列表列顺序（逗号分隔的列索引，由表头拖动自动写入）。");
                _colHiddenEntry = plugin.Config.Bind(Section, ColHiddenKey, "",
                    "内部状态：预设列表隐藏列（逗号分隔的列索引，右键表头隐藏后自动写入）。");
            // 不能加 AcceptableValueRange：会把默认值 0 夹成 120。
            _tableHEntry = plugin.Config.Bind(Section, "Table Height", 0,
                new ConfigDescription(SRApi.Tid("26766e29df", "预设列表高度（0 = 自动；拖动表格底边可直接修改并保存）", "Preset list height (0 = auto; drag the table bottom edge to change and save)")));
            if (_tableHEntry.Value == 120) _tableHEntry.Value = 0;
            try {
                ConfigEntry<bool> __migH = plugin.Config.Bind(Section, "Table Height Migrated", false,
                    "内部状态：是否已把旧版无效的表格高度复位（勿手动修改）。");
                if (!__migH.Value) {
                    __migH.Value = true;
                    if (_tableHEntry.Value > 0) { _tableHEntry.Value = 0; SRApi.LogInfo("[布局预设] 旧的表格高度值已复位为自动（此前该值从未生效）"); }
                }
            } catch (Exception __mh) { SRApi.Guard.Log("布局预设/表格高度迁移", __mh); }
                // 互斥锁：关卡重载动手前会读它，双方都忙时互相拦下。
                SrCaps.PresetsReloadBusy = () => _reloadBusy;
                FeatureRuntime.RecordRevoke(() => SrCaps.PresetsReloadBusy = null);
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设 初始化", __ex);
            }
        }

        // 栏目注册：单页（保存 / 导入导出 / 搜索排序筛选 + 列表同页，用 SectionTitle 分区）。
        private static void SelfReg() {
            SRApi.LocSec(Section, "布局预设", "Layout Presets");
            SRApi.Nav(Section, 70); // 关卡(40) 之后、构建工具(50) 之前
            // 多页必须用 6 参重载：单页重载会覆盖 PageRenderers 并清掉 _pageLists。
            SRApi.RegisterPage(Section, "save", "预设保存", "Save", 10, Render);
            SRApi.RegisterPage(Section, "list", "预设列表", "List", 20, RenderListPage);
            SRApi.RegisterPageWheel(OnListPageWheel);   // 帧尾兜底：绘制期没取到滚轮时由窗口派发
            // 填充页没有页面滚动区，滚轮由列表自己接。
            SRApi.Ctl.RegisterFillScroll(Section, delegate (float __w) {
                try { ApplyListWheel(PageWheelSign * Mathf.Sign(__w) * Mathf.Max(1f, Mathf.Abs(__w))); }
                catch (Exception __fw) { SRApi.Guard.Log("布局预设/填充页滚轮", __fw); }
            });
            // 页面关掉滚动，列表要自己吃滚轮：只有页面级钩子能在滚动区建立前拿到事件。
            // 重载锁自愈与 levelPortalXml 兜底清理两个 tick 都必须注册。
            try { SRApi.RegisterTick("布局预设-锁自愈", TickReloadLock); }
            catch (Exception __tl) { SRApi.Guard.Log("布局预设/注册锁自愈", __tl); }
            try { SRApi.RegisterTick("布局预设-XML兜底", TickXmlGuard); }
            catch (Exception __tx) { SRApi.Guard.Log("布局预设/注册XML兜底", __tx); }
            try { SRApi.Migration.Remove(Section, "Show Timestamps"); }
            catch (Exception __mx) { SRApi.Guard.Log("布局预设/注册淘汰键", __mx); }
            try { SRApi.Migration.Remove(Section, "Enabled"); }
            catch (Exception __me) { SRApi.Guard.Log("布局预设/淘汰总开关键", __me); }
            TweakCfg.Entry(Section, "Max Presets", "预设数量上限", "Max presets",
                "预设数量上限\n列表最多保留多少条预设\n范围 1-100；只限制新保存，不删磁盘文件", "Preset count limit\nhow many presets the list keeps\n1-100; limits new saves only, never deletes files");
            TweakCfg.Entry(Section, "Filter Type", "类型筛选", "Filter type",
                "列表页的类型筛选（会存盘）\n全部 / 仅预设 / 仅本地存档\n默认「全部」", "List type filter (persisted)\nall / presets only / local saves only\ndefault: all");
            TweakCfg.Entry(Section, "Filter Level", "关卡筛选", "Filter level",
                "列表页的关卡筛选（会存盘）\n空 = 全部关卡\n其余为关卡场景名", "List level filter (persisted)\nempty = all levels\notherwise a level scene name");
            TweakCfg.Entry(Section, "Filter Mode", "模式筛选", "Filter mode",
                "列表页的模式筛选（会存盘）\n空 = 全部模式\n其余为保存时的模式名", "List mode filter (persisted)\nempty = all modes\notherwise the mode the preset was saved in");
            TweakCfg.Entry(Section, "Sort Mode", "列表排序", "Sort mode",
                "列表排序方式\n0＝按时间（新到旧），1＝按名称\n由列表页的排序下拉维护", "List sort mode\n0 = newest first, 1 = by name\nmaintained by the list page dropdown");
            // 换场景后下次打开面板重新扫盘，能看到手动增删的 XML。
            try { SRApi.RegisterSceneHook(delegate { _loaded = false; }); }
            catch (Exception __sh) { SRApi.Guard.Log("布局预设/注册场景钩子", __sh); }
            try { SRApi.HotkeyAction("presets.treehouse", "返回树屋", delegate { ReturnToTreehouse(); }); }
            catch (Exception __hk) { SRApi.Guard.Log("布局预设/注册返回树屋快捷键", __hk); }
            TweakCfg.Entry(Section, "Skip Treehouse", "跳过树屋流程", "Skip treehouse",
                "跳过树屋流程\n直接切换场景进入目标关卡，不先回树屋\n仅单人生效；联机时强制走树屋", "Skip treehouse\nswitches scene straight into the target level\nsingle-player only; forced on online");
            TweakCfg.Entry(Section, ColWidthsKey, "列表列宽（内部）", "Preset widths (internal)",
                "预设列表列宽\n拖动表头后自动写入\n一般无需手改；非法值回退默认", "Preset column widths\nwritten automatically after dragging the header\nrarely needs editing; invalid values fall back");
            TweakCfg.Entry(Section, ColOrderKey, "列表列序（内部）", "Preset order (internal)",
                "预设列表列序\n拖动表头换位后自动写入\n一般无需手改；非法值回退默认", "Preset column order\nwritten automatically after reordering the header\nrarely needs editing; invalid values fall back");
            TweakCfg.Entry(Section, ColHiddenKey, "隐藏列（内部）", "Preset hidden (internal)",
                "预设列表隐藏列\n右键表头隐藏后自动写入\nShift+右键表头可恢复全部", "Preset hidden columns\nwritten after hiding with a header right-click\nShift+right-click the header restores all");
            // 内部键同段却没有本地化会显示成英文 key，补齐并标注"勿手动修改"。
            TweakCfg.Entry(Section, "Table Height Migrated", "列表高度迁移标记（内部）", "List height migration flag (internal)",
                "内部状态：是否已完成旧版无效的表格高度复位\n由模组自动维护，勿手动修改\n改动后需要重开设置页才会重新判断",
                "Internal: whether the legacy ineffective table height has been reset\nmaintained by the mod automatically; do not edit\na change is re-evaluated the next time the settings page opens");
        }



        private static int MaxPresets {
            get {
                try { return _maxPresets != null ? Mathf.Clamp(_maxPresets.Value, 1, 100) : 100; } catch { return 100; }
            }
        }

        // 动作共用门控：模组总开关 + 本功能总开关；返回 false 时已提示。
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

        // 默认按时间新→旧；点表头切到该列排序（再点切升降序）。同序时按名称稳定排序。
        private static int ComparePreset(PresetInfo a, PresetInfo b) {
            try {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int c;
                if (_sortColOverride >= 0) {
                    switch (_sortColOverride) {
                        case ColName: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                        case ColLevel: c = string.Compare(a.LevelName ?? a.Level, b.LevelName ?? b.Level, StringComparison.OrdinalIgnoreCase); break;
                        case ColMode: c = string.Compare(ModeText(a.Mode), ModeText(b.Mode), StringComparison.OrdinalIgnoreCase); break;
                        case ColTime: c = a.TimeUtc.CompareTo(b.TimeUtc); break;
                        default: c = 0; break;
                    }
                    if (!_sortAsc) c = -c;
                } else c = b.TimeUtc.CompareTo(a.TimeUtc);   // 默认：新 → 旧
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            } catch { return 0; }
        }

        private static readonly SRApi.TableSource<PresetInfo> _visSource = new SRApi.TableSource<PresetInfo>();

        private static List<PresetInfo> _visibleCache;   // 当前表格的可见行（供数据列悬浮预览取预设）

        // ---------- 页面渲染 ----------

        // 同一页用 SectionTitle 分区；表格用自己的滚轮（页面级钩子 + 表内处理），在固定高度区域内滚动。

        private static int _renderFrame = -1000;   // 本页最近一次被渲染的帧（滚轮钩子只在自己页面上工作）

        // 筛选行标签统一宽度：两行控件左缘对齐；比保存区表单标签窄 1/3。
        private static float FilterLabelWidth() { return Mathf.Max(SRApi.Ctl.Sc(40f), FormLabelWidth() * (2f / 3f)); }

        private static void RowLabel(string text) {
            GUILayout.Label(text, SRApi.Ctl.Label, GUILayout.Width(FilterLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
        }

        // 列表状态栏：条数 / 筛选结果 + 最近一次操作反馈（_status 原本只写不读）。
        private static void RenderListStatus(int shown, int total) {
            try {
                string count = (shown == total)
                    ? SRApi.Tid("e892ae517a", "共 ", "Total ") + total + SRApi.Tid("cd8df9bab8", " 条", " items")
                    : SRApi.Tid("7675e5bf32", "显示 ", "Showing ") + shown + SRApi.Tid("fbd1bece3e", " / ", " / ") + total + SRApi.Tid("cd8df9bab8", " 条", " items");
                // _status 永不清除，这里只控制显示：值变化时重新计时，8 秒后不再拼接。
                string status = _status;
                if (!string.IsNullOrEmpty(status)) {
                    if (!string.Equals(status, _statusShown, StringComparison.Ordinal)) {
                        _statusShown = status;
                        _statusShownAt = Time.unscaledTime;
                    }
                    if (Time.unscaledTime - _statusShownAt > 8f) status = null;
                }
                if (!string.IsNullOrEmpty(status)) count += "   ·   " + status;
                // 工具行前面已有约 290 设计单位：状态栏须设上限，否则长路径会撑出窗口。
                float maxW = Mathf.Max(SRApi.Ctl.Sc(120f), SRApi.ContentArea.width - SRApi.Ctl.Sc(380f));
                GUILayout.Label(SRApi.Ctl.Content(count, string.IsNullOrEmpty(status) ? null : status), SRApi.Ctl.Label,
                    GUILayout.Height(SRApi.Ctl.Sc(26f)), GUILayout.MaxWidth(maxW));
            } catch (Exception __ls) { SRApi.Guard.Log("布局预设/列表状态栏", __ls); }
        }

        // 保存区两个标签按实测宽度取最大值，使两行输入框左缘对齐。
        private static float FormLabelWidth() {
            // 只依赖语言 + 字号缩放，签名不变直接复用。
            int sig = (SRApi.Ctl.LangEn ? 1 : 0) * 31 + Mathf.RoundToInt(SRApi.Ctl.Sc(100f));
            if (sig == _formLabelSig) return _formLabelW;
            try {
                float wa = SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(SRApi.Tid("d220332704", "预设名称", "Preset name"))).x;
                float wb = SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(SRApi.Tid("c7a0abd3fd", "XML 路径", "XML path"))).x;
                _formLabelW = Mathf.Max(SRApi.Ctl.Sc(52f), Mathf.Max(wa, wb) + SRApi.Ctl.Sc(4f));   // 标签与输入框间距缩小约 1/3
                _formLabelSig = sig;
                return _formLabelW;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/标签宽度", __ex); return SRApi.Ctl.Sc(90f); }
        }

        // ================= 布局预设页（填充模式） =================
        // 三段结构：常驻头部、列表区（视口减头部高）、底边拖拽条；页面声明 NoPageScrollBars，滚轮只落在列表里。
        private static void Render() {
            try {
                _renderFrame = Time.frameCount;
                SRApi.NoPageScrollBars = true;    // 填充模式：页头固定，只有列表自己滚
                EnsureLoaded();
                EnsureCols();
                string pendingBefore = _pendingKey;

                RenderSaveImportHeader();
                CancelPendingOnBlank(pendingBefore);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染布局预设页", __ex); }
        }


        // 筛选/排序变化：回顶并清二次确认（须在本帧控件画完后读，否则差一帧）
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
















        // 重命名弹窗：右键行弹出输入框 + 确定/取消；用绝对矩形绘制，不依赖行内输入框与焦点。
        // 打开重命名浮窗（右键行时直接调用；不依赖列表页是否正在渲染）。
        private static void EnsureRenameFloat() {
            try {
                if (string.IsNullOrEmpty(_renameKey)) return;
                if (!SRApi.Visible) return;          // 一类窗口只能由 SR 界面触发
                if (SRApi.Ctl.FloatIsOpen) return;
                _renameOwnsFloat = true;
                SRApi.Ctl.FloatOpen(SRApi.Tid("a74db606eb", "重命名预设", "Rename preset"),
                    new Vector2(420f, 130f), DrawRenameContent, CancelRename);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重命名浮窗", __ex); }
        }

        // 每帧维护：有目标就保证窗口存在；目标消失则收窗。
        private static void DrawInlineRename() {
            try {
                if (string.IsNullOrEmpty(_renameKey)) {
                    if (SRApi.Ctl.FloatIsOpen && _renameOwnsFloat) CancelRename();
                    return;
                }
                EnsureRenameFloat();
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重命名浮窗", __ex); }
        }

        private static PresetInfo _renameTarget;
        private static bool _renameOwnsFloat;

        private static void DrawRenameContent() {
            try {
                PresetInfo p = _renameTarget;
                if (p == null) { SRApi.Ctl.FloatClose(); return; }
                float w = Mathf.Max(SRApi.Ctl.Sc(200f), SRApi.Ctl.FloatRect.width - SRApi.Ctl.Sc(20f));
                GUI.SetNextControlName("srPresetRenameField");
                string typed = GUI.TextField(new Rect(0f, 0f, w, SRApi.Ctl.Sc(26f)), _renameText ?? p.Name ?? "", 40);
                if (typed != _renameText) _renameText = typed;
                if (_renameFocusPending) { GUI.FocusControl("srPresetRenameField"); _renameFocusPending = false; }
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CancelRename(); e.Use(); return; }
                if (e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { SubmitRename(p); e.Use(); return; }
                float bw = SRApi.Ctl.Sc(76f);
                if (GUI.Button(new Rect(w - bw * 2f - SRApi.Ctl.Sc(8f), SRApi.Ctl.Sc(36f), bw, SRApi.Ctl.Sc(24f)), SRApi.Tid("6259d30d8a", "确定", "OK"), SRApi.Ctl.Btn))
                    SubmitRename(p);
                else if (GUI.Button(new Rect(w - bw, SRApi.Ctl.Sc(36f), bw, SRApi.Ctl.Sc(24f)), SRApi.Tid("d94a8eaf28", "取消", "Cancel"), SRApi.Ctl.Btn))
                    CancelRename();
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重命名内容", __ex); }
        }

        private static void SubmitRename(PresetInfo p) {
            try {
                if ((_renameText ?? "").Trim().Length == 0) {
                    SRApi.NotifyT("名称不能为空", "The name cannot be empty");
                    return;
                }
                CommitRename(p);
            } catch (Exception __sr) { SRApi.Guard.Log("布局预设/重命名提交", __sr); }
            CancelRename();
        }

        private static void CancelRename() {
            _renameKey = null; _renameText = null; _renameTarget = null;
            if (_renameOwnsFloat) { _renameOwnsFloat = false; SRApi.Ctl.FloatClose(); }
        }

        // Shift+右键表头：核心还原列宽与隐藏列后回调这里，再补一次整表还原并落盘。
        private static void ResetTable() {
            try {
                if (_tableCols != null) _tableCols.ResetAll();   // 列序 / 隐藏 / 列宽一起回默认（核心统一实现）
                for (int i = 0; i < ColCount; i++) _colW[i] = DefColW[i];
                _hiddenCols.Clear();
                SaveCols();
                SRApi.LogInfo("[布局预设] " + SRApi.Tid("6fc3737f38", "已恢复显示全部列", "All columns are shown again"));
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/恢复全部列", __ex);
            }
        }

        private static string ColumnTitle(int ci) {
            string __arrow = (ci == _sortColOverride && ci != ColAction) ? (_sortAsc ? " ▲" : " ▼") : "";
            switch (ci) {
                case ColName: return SRApi.Tid("84f28b852b", "名称", "Name") + __arrow;
                case ColLevel: return (_levelShowCode ? SRApi.Tid("afde63ce14", "关卡代码", "Level code") : SRApi.Tid("2cf41c8c56", "关卡", "Level")) + __arrow;
                case ColMode: return SRApi.Tid("47384f58db", "模式", "Mode") + __arrow;
                case ColMods: return SRApi.Tid("e932c3b4ad", "数据", "Data");
                case ColTime: return SRApi.Tid("772ed3aeed", "时间", "Time") + __arrow;
                case ColAction: return SRApi.Tid("d4eb333d8f", "操作", "Actions");
                default: return "";
            }
        }

        private static string ColumnTip(int ci) {
            try {
                switch (ci) {
                    case ColName: return SRApi.Tid("9d0259eed3", "预设名称", "preset name");
                    case ColLevel: return SRApi.Tid("15eb825eee", "预设所属关卡；单击「关卡」表头可在关卡名称 / 关卡代码之间切换（代码缺失显示 -）", "the level this preset belongs to; click the Level header to toggle between level name and level code (code shows - when unavailable)");
                    case ColMode: return SRApi.Tid("dea7d3304c", "保存时所在的游戏模式", "game mode the preset was saved in");
                    case ColMods: return SRApi.Tid("c006e12dfa", "这条预设带的自定义数据\n默认 = 全部用预设自带；自定义 = 修改器/规则/积分/模块概率按你的设定\n鼠标停在该列格子上可预览具体内容", "custom data carried by this preset\nDefault = use whatever the preset has; Custom = your own modifiers/rules/points/block odds\nhover a cell in this column to preview the details");
                    case ColTime: return SRApi.Tid("31939b5f4f", "保存时间（本地时间）", "save time (local time)");
                    case ColAction: return SRApi.Tid("2bcb9071eb", "加载 / 数据 / 导出到剪贴板 / 删除；删除需点两次确认\n重命名：右键「名称」列；自定义数据：右键「数据」列", "load / data / export to clipboard / delete; delete needs a second click\nrename: right-click the Name column; custom data: right-click the Data column");
                    default: return null;
                }
            } catch { return null; }
        }

        // 单元格显示签名：语言 / 关卡显示模式 / 各字段任一变化都重算。
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
            // 单元格文本缓存：核心 TableDraw 只读该数组，避免每帧上百次字符串生成。
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
                ? SRApi.Tid("c94b4b3e96", "本地", "Local") + " · " + (p.Name ?? "")
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

        // 钉住操作列：每行按钮（加载 / 数据 / 导出 / 删除），删除在确认窗口内显示「确认」；布局与命中由核心处理。
        private static void DrawActionButtons(List<PresetInfo> visible, int row, Rect r) {
            try {
                PresetInfo p = PresetAt(visible, row);
                if (p == null) return;
                _actPreset = p;
                bool replacePending = IsPending("replace:" + p.Key);
                int hover = -1;
                if (p.IsLocal) {
                    // 本地关卡只给「加载」一个按钮
                    _actLocalActions[0].Label = PendingLabel("加载", "Load", "replace:" + p.Key);
                    if (SRApi.TableActionButtons(r, _actLocalActions, null, ref hover) >= 0) _actionHitThisFrame = true;
                    return;
                }
                bool delPending = IsPending("del:" + p.Key);
                _actActions[ActLoad].Label = PendingLabel("加载", "Load", "replace:" + p.Key);
                _actActions[ActExport].Label = SRApi.Tid("9bb429ef37", "导出", "Export");
                _actActions[ActDelete].Label = PendingLabel("删除", "Delete", "del:" + p.Key);
                int hit = SRApi.TableActionButtons(r, _actActions, null, ref hover);
                if (hit >= 0) _actionHitThisFrame = true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/操作按钮", __ex);
            }
        }

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


        // 首次进入列表页读配置：列宽按范围裁剪、列序须是合法排列且操作列固定在最后。
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
                _colW[ColAction] = DefaultActionColW();
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/读取列设置", __ex);
            }
        }

        // 操作列默认宽 = 最宽按钮文字宽 + 按钮间距 + 边距；与核心 BtnClip.CalcSize(label).x + 11px 同公式。
        private static float DefaultActionColW() {
            try {
                float w = SRApi.TableActionColumnWidth(_actActions);
                if (w > 0f) return Mathf.Clamp(w, ColWidthMin, ColWidthMax);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/操作列宽", __ex); }
            return DefColW[ColAction];
        }

        // 列序归一化：只允许前 4 列换位，操作列固定在最后；非法 / 缺列时补回默认顺序。
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
                // 长度不符就别写盘（写进去会被 NormalizeOrder 永久降级成默认列序）
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

        // 首次调用只登记并返回 false；窗口内第二次返回 true 并清掉登记。
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

        // 二次确认按钮文案：窗口内显示「确认 (2.3s)」。
        private static string PendingLabel(string zhBase, string enBase, string key) {
            try {
                if (!IsPending(key)) return SRApi.T(zhBase, enBase);
                float left = Mathf.Max(0f, ConfirmWindow - (Time.unscaledTime - _pendingAt));
                return SRApi.T("确认 (" + left.ToString("0.0") + "s)", "Confirm (" + left.ToString("0.0") + "s)");
            } catch { return SRApi.T(zhBase, enBase); }
        }

        private static string SaveButtonText() {
            return IsPendingPrefix("overwrite:") ? PendingLabel("确认覆盖", "Confirm overwrite", _pendingKey) : SRApi.Tid("dae2b8018f", "保存当前布局", "Save layout");
        }

    }
}
