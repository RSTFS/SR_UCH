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
        // 索引格式版本：v2 起新增 level / ok；v3 起新增 levelName；v4 起新增 levelCode（关卡代码/快照码）。
        // 老索引缺字段按默认值处理并在扫描时自愈。
        private const int IndexVersion = 4;
        // index.json 每条记录的 schema 版本：缺失 = 旧格式（下次写盘会补上）。
        private const int IndexSchema = 1;
        // 排序方式：0=时间（新→旧），1=名称（A→Z）。旧配置里的 2/3 已弃用，读取时夹到 0。
        private const int SortModeMin = 0;
        private const int SortModeMax = 1;
        // 表格列索引（与 TableArgs.colW 顺序一致）：名称 / 关卡 / 模式 / 修改器 / 时间 / 操作（操作列钉在右侧）。
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
        // 操作列按钮种类：0=加载 1=导出 2=删除（重命名改为右键名称列；「数据」按钮已按用户要求删除）。
        private const int ActLoad = 0;
        private const int ActExport = 1;
        private const int ActDelete = 2;
        // 从剪贴板导入的上限：方块数 / XML 字符数。超出直接拒绝，避免一次性重建拖垮帧率或整段解析爆内存。
        private const int MaxImportBlocks = 3000;
        private const int MaxImportChars = 2000000;
        // 游戏本地关卡快照：7z 压缩文件的大小 / 解压后 XML 大小上限（与导入上限同一套防护，防止畸形文件拖垮扫描）。
        private const int MaxSnapshotBytes = 4 * 1024 * 1024;
        private const int MaxLocalXmlBytes = 8 * 1024 * 1024;
        // 二次确认窗口（秒）：超时后按钮自动恢复第一次点击前的文案。
        private const float ConfirmWindow = 5f;
        // 重载锁自愈阈值（秒）：与功能 Level 的 45 秒保持一致。
        private const float ReloadLockStale = SrTimeouts.ReloadLockStaleSeconds;   // 单一真值：契约层 SrTimeouts
        // 等游戏消费 levelPortalXml 的硬上限（秒）：与功能 Level 的 25 秒兜底一致。
        private const float PresetConsumeTimeout = SrTimeouts.PresetConsumeTimeoutSeconds;   // 单一真值：契约层 SrTimeouts

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _maxPresets;
        private static ConfigEntry<bool> _skipTreehouse;   // 跳过树屋流程（联机直接替换关卡，风险自负）
        private static bool _actionHitThisFrame;   // 本帧是否命中操作按钮（空白处取消二次确认）
        private static ConfigEntry<int> _sortModeEntry;
        private static ConfigEntry<string> _colWidthsEntry;
        private static ConfigEntry<string> _colOrderEntry;
        private static ConfigEntry<string> _colHiddenEntry;
        private static ConfigEntry<int> _tableHEntry;
        // 运行期快照：配置变化回调里即时更新，供每帧渲染读取（避免每帧访问 ConfigEntry）。
        private static bool _featureOn;
        private static int _sortMode;
        // 存储根目录缓存（BepInEx.Paths 的调用只做一次）。
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
        // 操作列按钮描述（复用对象，避免逐行分配）：布局 / 命中 / 悬浮统一交给核心 SRApi.TableActionButtons。
        // 不传固定宽度（widths = null）：核心按当前语言文字宽自适应，避免英文标签被截断。
        private static PresetInfo _actPreset;
        // 用户要求：删掉「数据」按钮 —— 它打开的是游戏原生规则书界面，与预设列表的「预设 / 关闭」两态重复。
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
        private static float _maxScrollX;
        private static float _maxScrollY;
        private static bool _sbDragging;
        private static float _sbGrabY;
        private static bool _sbHandled;    // 本帧核心滚动条是否已处理鼠标事件
        private static bool _dragH;
        private static float _dragWatchdog;
        // 关卡列显示模式：false = 关卡名称（本地化），true = 关卡代码（快照码/场景名）；运行期状态，不落配置。
        private static bool _levelShowCode;

        // 加载预设时的重载锁（本功能自己的，和 Level 的锁互相不可见）。
        private static string _reloadBusyReason;   // 锁的来源（日志用）
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
            // 修改器策略："" = 继承预设自带（默认）；"off" = 全部还原默认；"custom" = 用 ModsVals 覆盖。
            public string ModsMode = "";
            // 加载该预设时是否一并应用 游戏规则 / 积分设置 / 模块概率（保存时写入 sidecar）。
            public bool ApplyRules, ApplyPoints, ApplyBlocks;
            public Dictionary<string, string> ModsVals;   // 仅 custom 时有值：<mods> 属性名 -> 值
            // 来源：false = 本功能 presets 目录里的预设；true = 游戏本地保存的自定义关卡（.snapshot，只读）。
            public bool IsLocal;
            public string LocalRelative = ""; // 本地条目在 snapshots 目录里的相对文件名（key 用 "local:"+相对路径）

            public string Key { get { return IsLocal ? "local:" + LocalRelative : Mode + "/" + File; } }
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Section, "Enabled", false,
                    "布局预设总开关：开启后可保存 / 加载 / 导入导出方块布局预设。");
                _featureOn = _enabled.Value;
                _enabled.SettingChanged += delegate { _featureOn = _enabled.Value; };
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
            // 重载锁自愈：TickReloadLock 写好了却从未注册，等于"超时兜底"根本不存在，
            // 失败路径漏放锁只能等用户再点一次加载（audit 10-P0-2）。
            try { SRApi.RegisterTick("布局预设-锁自愈", TickReloadLock); }
            catch (Exception __tl) { SRApi.Guard.Log("布局预设/注册锁自愈", __tl); }
            // 已删除的配置键：Show Timestamps（时间列固定显示，不再提供开关）。
            // 用功能自注册的迁移规则清理旧 cfg，避免遗留孤儿键（只列真正不再 Bind 的键）。
            try { SRApi.Migration.Remove(Section, "Show Timestamps"); }
            catch (Exception __mx) { SRApi.Guard.Log("布局预设/注册淘汰键", __mx); }
            SRApi.LocKey(Section, "Enabled", "布局预设总开关", "Layout presets");
            SRApi.LocDesc(Section, "Enabled",
                "布局预设总开关\n开启后可保存与加载方块布局\n关闭后仍可查看列表，但操作全部无效",
                "Layout preset master switch\nsave and load block layouts while on\noff still lists presets but disables every action");
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
            // 返回树屋：注册动作并允许右键绑键（悬浮提示里不写绑定说明）。
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
        }

        // ---------- 配置读取 ----------

        private static bool FeatureOn {
            get { return _featureOn; }
        }

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
            if (!FeatureOn) {
                SRApi.Notify(SRApi.T("布局预设总开关已关闭", "Layout presets are disabled"));
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
        private static long _visStamp = -1;

        private static List<PresetInfo> _visibleCache;   // 当前表格的可见行（供数据列悬浮预览取预设）

        private static List<PresetInfo> VisiblePresets() {
            // 核心数据管线：过滤/排序每帧最多一次（同一帧多次调用返回同一份缓存）。
            _visSource.Filter = PassPresetFilter;
            _visSource.Sort = ComparePreset;
            long stamp = Time.frameCount;
            if (_visStamp != stamp) { _visSource.Invalidate(); _visStamp = stamp; }
            return _visSource.Refresh(_presets, stamp);
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

        private static void Render() {
            try {
                EnsureLoaded();
                EnsureCols();
                // 本帧开始前的二次确认状态：只有"之前已确认、本帧又没点中操作按钮"才在空白处取消，
                // 避免把本帧其它按钮（保存/导入）刚点出来的二次确认立刻清掉。
                string pendingBefore = _pendingKey;
                // 页面滚动保持可用（滚动条已统一隐藏）：表格内部滚轮失效时也能靠页面滚动看到全部内容。
                Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
                if (Event.current != null && Event.current.type == EventType.Layout) { _pageTopY = topProbe.y; }

                // 分区 1：保存（名称 + 保存，紧凑一行）。
                SRApi.Ctl.SectionTitle(SRApi.T("- 保存 -", "- Save -"));
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("预设名称", "Preset name"));
                    string typed = SRApi.ClearableTextField(_newName ?? "", 40, SRApi.Ctl.SearchBox,
                        GUILayout.Width(W(240)), GUILayout.Height(W(24)));
                    if (typed != null && typed != _newName) _newName = typed;
                    if (GUILayout.Button(SaveButtonText(), SRApi.Ctl.Btn, GUILayout.Width(W(120)), GUILayout.Height(W(24)))) SaveCurrent();
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }

                // 分区 2：导入 / 导出（XML 路径单独一行；下一行为 路径导入 / 剪贴板导入 / 全部导出）。
                SRApi.Ctl.SectionTitle(SRApi.T("- 导入 / 导出 -", "- Import / Export -"));
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("XML 路径", "XML path"));
                    string path = SRApi.ClearableTextField(_importPath ?? "", 260, SRApi.Ctl.SearchBox,
                        GUILayout.Width(W(300)), GUILayout.Height(W(24)));
                    if (path != null && path != _importPath) _importPath = path;
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                try {
                    if (GUILayout.Button(SRApi.T("从路径导入", "Import path"), SRApi.Ctl.Btn,
                        GUILayout.Width(W(110)), GUILayout.Height(W(24)))) ImportFromPath();
                    if (GUILayout.Button(SRApi.T("从剪贴板导入", "Import clipboard"), SRApi.Ctl.Btn,
                        GUILayout.Width(W(120)), GUILayout.Height(W(24)))) ImportFromClipboard();
                    if (GUILayout.Button(SRApi.Ctl.Content(SRApi.T("全部导出到目录", "Export all to folder"),
                        SRApi.T("把所有预设 XML 复制到 预设目录/exports/<时间>/ 下（按模式分子目录），并复制一份 index.json",
                            "copies every preset XML into presets/exports/<time>/ (grouped by mode) and copies index.json as well")),
                        SRApi.Ctl.Btn, GUILayout.Width(W(140)), GUILayout.Height(W(24)))) ExportAllToDirectory();
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(W(4));

                // 分区 3：列表工具。分两行避免窗口变窄时控件被挤出页面：
                // 第一行搜索 / 排序 / 刷新，第二行关卡 / 模式筛选 / 加载模式；标签统一 LabelAuto，下拉宽度自适应。
                SRApi.Ctl.SectionTitle(SRApi.T("- 搜索 / 排序 / 筛选 -", "- Search / sort / filter -"));
                GUILayout.BeginHorizontal();
                try {
                    // 搜索标签：LabelAuto 绘制 + 命中区域判定，点击清空搜索框。
                    SRApi.Ctl.LabelAuto(SRApi.T("搜索", "Search"));
                    Rect searchRect = GUILayoutUtility.GetLastRect();
                    Event evSearch = Event.current;
                    if (evSearch != null && evSearch.type == EventType.MouseDown && evSearch.button == 0
                        && searchRect.Contains(evSearch.mousePosition)) {
                        _search = "";
                        evSearch.Use();
                    }
                    string q = SRApi.ClearableTextField(_search ?? "", 40, SRApi.Ctl.SearchBox,
                        GUILayout.Width(W(120)), GUILayout.Height(W(24)));
                    if (q != null && q != _search) _search = q;
                    GUILayout.Space(W(8));
                    SRApi.Ctl.LabelAuto(SRApi.T("类型", "Type"));
                    RenderTypeFilterCombo();
                    GUILayout.Space(W(8));
                    SRApi.Ctl.LabelAuto(SRApi.T("排序", "Sort"));
                    RenderSortCombo();
                    GUILayout.Space(W(7));
                    if (GUILayout.Button(SRApi.Ctl.Content(SRApi.T("刷新", "Refresh"),
                            SRApi.T("重新扫描预设目录（手动放入/删除了 XML 时用），也用于重建索引",
                                "rescan the presets folder (after manually adding/removing XML files) and rebuild the index")),
                        SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Btn.CalcSize(SRApi.Ctl.Content(SRApi.T("刷新", "Refresh"))).x + SRApi.Ctl.Sc(18)), GUILayout.Height(W(24)))) {
                        try { _loaded = false; LoadFromDisk(true); } catch (Exception __rx) { SRApi.Guard.Log("布局预设/刷新列表", __rx); }
                    }
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("关卡", "Level"));
                    RenderLevelFilterCombo();
                    GUILayout.Space(W(8));
                    SRApi.Ctl.LabelAuto(SRApi.T("模式", "Mode"));
                    RenderModeFilterCombo();
                    GUILayout.Space(W(8));
                    SRApi.Ctl.LabelAuto(SRApi.T("加载模式", "Load mode"));
                    RenderLoadModeCombo();
                    // 用户要求：删掉「加载模式」右侧的数据按钮（它打开的是游戏原生规则书界面）。
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                // 底部工具：跳过树屋流程（标准配置勾选框）+ 返回树屋（普通核心按钮）。
                // 单独一行，不与上面的筛选行混在一起。
                GUILayout.BeginHorizontal();
                try {
                    if (_skipTreehouse != null) {
                        // 标准配置行 = 可点击标签（点名称还原默认）+ 核心开关控件
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
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                if (!string.IsNullOrEmpty(_renameKey)) RenderRenameBar();
                // 自建的修改器编辑页已下线：修改器/规则/积分/模块一律用游戏自己的界面（操作列的「游戏界面」）。
                GUILayout.Space(W(2));
                // 高度：核心统一公式（页面顶部已用高度 + 表格高度 = 底栏之上可用高度），窗口变化实时跟随。
                // 关键：GetLastRect() 在 Layout 趟里可能是退化矩形（诊断日志里 lastYMax=1.0），
            // 用它算"已用高度"会得到 ~0，表格于是按整个视口取高、直接溢出页面底部。
            // 这里放一个零高度探针，拿到当前布局游标的真实 y。
            Rect last = GUILayoutUtility.GetRect(1f, 0f);
                // 直接以“页面内容底界 - 当前布局 Y”为表格高度：窗口大小/栏高变化都自动跟随，不再依赖预留估算。
                // 表格高度：核心统一公式（页面视口 − 页面已用高度，再按 Table Height 夹取）。
                // 真正决定表格高度的是这里（渲染函数只在 tableH<=0 时才回退到 TableHeight）。
            // 与联机列表逐字一致：页布局顶 + 已用高度 -> 核心公式（PageViewportHeight − used）。
            // 我上一版把 ContentArea（屏幕坐标）和 last.yMax（布局坐标）混算，结果反而更矮。
            // 核心化：高度 = min(可用, 表头 + 行数*行高 + 拖拽条)。行少 -> 收紧，行多 -> 填满并滚动。
            float tableH = SRApi.Ctl.ListTableHeight(last.yMax, _pageTopY, _tableHEntry, SRApi.Ctl.TableMinH(),
                VisiblePresets().Count, SRApi.Ctl.TableRowH(), SRApi.Ctl.TableHeaderH(),
                SRApi.Ctl.TableGripH() + SRApi.Ctl.TableGripGap() * 2f, true);   // 底部贴页面底：填满可用高度
                _actionHitThisFrame = false;
                List<PresetInfo> visible = VisiblePresets();
                RenderPresetTable(tableH, visible);
                // 二次确认取消：本帧左键 MouseUp 没有命中任何操作按钮 = 点了空白处
                try {
                    if (!string.IsNullOrEmpty(_pendingKey) && _pendingKey == pendingBefore && !_actionHitThisFrame
                        && Event.current != null && Event.current.type == EventType.MouseUp && Event.current.button == 0) {
                        ClearPending();
                    }
                } catch (Exception __pe) { SRApi.Guard.Log("布局预设/空白处取消确认", __pe); }
            } catch (Exception __ex) {
                SRApi.Guard.Log("渲染布局预设页", __ex);
            }
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
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : LevelDisplayNameForScene(vals[i]);
                bool open = _levelFilterOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals.ToArray(), labels, ref open);
                _levelFilterOpen = open;
                if (sel >= 0 && sel < vals.Count) _levelFilter = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染关卡筛选", __ex);
            }
        }

        // 关卡筛选选项：第一项为空（全部），其余为当前预设里出现过的关卡场景名，按本地化关卡名排序。
        private static List<string> LevelFilterValues() {
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
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : ModeText(vals[i]);
                bool open = _modeFilterOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals.ToArray(), labels, ref open);
                _modeFilterOpen = open;
                if (sel >= 0 && sel < vals.Count) _modeFilter = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染模式筛选", __ex);
            }
        }

        // 模式筛选选项：第一项为空（全部），其余为当前预设里出现过的模式目录名，按本地化模式名排序。
        private static List<string> ModeFilterValues() {
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
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, W(90));
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
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, W(90));
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
                int sel = SRApi.Ctl.ComboBox(e, opts[cur], vals, opts, ref open);
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

        // 重命名编辑条（列表操作列进入）：表格单元格不能内嵌输入框，单独一行编辑。
        private static void RenderRenameBar() {
            try {
                PresetInfo p = FindPreset(_renameKey);
                if (p == null) { _renameKey = null; _renameText = null; return; }
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("重命名：", "Rename: "));
                    string typed = SRApi.ClearableTextField(_renameText ?? p.Name, 40, SRApi.Ctl.SearchBox,
                        GUILayout.Width(W(220)), GUILayout.Height(W(24)));
                    if (typed != null && typed != _renameText) _renameText = typed;
                    if (GUILayout.Button(SRApi.T("确定", "OK"), SRApi.Ctl.Btn, GUILayout.Width(W(60)), GUILayout.Height(W(24)))) {
                        CommitRename(p);
                        _renameKey = null;
                        _renameText = null;
                    }
                    if (GUILayout.Button(SRApi.T("取消", "Cancel"), SRApi.Ctl.Btn, GUILayout.Width(W(60)), GUILayout.Height(W(24)))) {
                        _renameKey = null;
                        _renameText = null;
                    }
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/重命名编辑条", __ex);
            }
        }

        // 标准表格：核心 TableDraw 负责表头/行/拖列宽/换列位/钉住操作列；本页只提供列定义、单元格文本与操作按钮。
        private static void RenderPresetTable(float tableH, List<PresetInfo> visible) {
            try {
                _visibleCache = visible;   // 悬浮预览按行号取预设时用（与 RowCells 同一份列表）
                string title = SRApi.T("- 预设列表", "- Preset list") + " (" + visible.Count + "/" + _presets.Count
                    + SRApi.T("，上限 ", ", limit ") + MaxPresets + ")";
                SRApi.Ctl.SectionTitle(title);
                float titleH = 0f;
                try { titleH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().height); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                float rowH = SRApi.Ctl.TableRowH();
                float headerH = SRApi.Ctl.TableHeaderH();
                if (tableH <= 0f) tableH = TableHeight();
                // Render() 传入的 tableH 是「分区标题 + 表格」可用的总高度；分区标题本身也占高，
                // 这里扣掉它的实测高度，否则表格底部会超出可视区、最后一行被底栏裁掉且滚不到。
                // 不再二次扣减：调用方给的 tableH 已经是"可用总高度"，而分区标题在 GetLastRect 里
                // 也已经被算进 last.yMax（TableFitHeight 的 used）。再扣一次 titleH + 拖拽条，
                // 表格就会比可用区域矮一截 —— 列表中下方那片空白的直接来源。
                tableH = Mathf.Max(SRApi.Ctl.Sc(120), tableH);
                float rowsH = Mathf.Max(SRApi.Ctl.Sc(80), tableH - headerH);
                Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(tableH));
                EnsureCols();
                // 操作列宽始终跟随当前语言的按钮文字：语言热切换后也能立刻避免英文标签被截断。
                _colW[ColAction] = DefaultActionColW();
                if (_args == null) _args = new SRApi.TableArgs();
                float pinW = SRApi.Ctl.Sc(_colW[ColAction]);
                // 有纵向溢出时，在数据区与钉住列之间预留滚动条宽度（与联机列表一致）。
                // 滚动条已全局隐藏（SR.Table.HideTableScrollbars），这里也不再为它留宽度：
                // 数据区直接吃满到钉住列，右侧不再空一条。
                float __sbW = 0f;
                float stripW = Mathf.Max(SRApi.Ctl.Sc(80), box.width - pinW - __sbW);
                float totalW = 0f;
                for (int i = 0; i < _order.Length; i++) {
                    int ci = _order[i];
                    if (ci == ColAction) continue;
                    totalW += SRApi.Ctl.Sc(_colW[ci]);
                }
                _maxScrollX = Mathf.Max(0f, totalW - stripW);
                _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _maxScrollX);
                _maxScrollY = Mathf.Max(0f, visible.Count * rowH - rowsH);
                _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _maxScrollY);
                Event e = Event.current;
                Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
                _args.box = box;
                _args.rowsClip = new Rect(box.x, box.y + headerH, stripW, rowsH);
                _args.headerH = headerH;
                _args.rowH = rowH;
                _args.rowsH = rowsH;
                _args.stripW = stripW;
                _args.colW = _colW;
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
                // 与联机列表一致：表头可拖列宽、可拖动换位，变化经回调写回配置。
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
                SRApi.TableDraw(_args);
                HandlePresetCells();   // 数据列：悬浮预览 / 左键切三态 / 右键开界面；名称列：右键重命名
                // 底边拖拽条（核心统一绘制 + 拖动 + 持久化）。
                SRApi.Ctl.TableHeightGripBar(_tableHEntry, (box.height + SRApi.Ctl.TableGripH() + SRApi.Ctl.TableGripGap() * 2f) / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f)), 120, 1000);
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
                    // 临时诊断日志：点表头时记录核心是否认到拖动/列宽拖动（构建后看 BepInEx 日志即可定位拖拽问题）。
                    if (e.type == EventType.MouseDown && e.button == 0) {
                        try {
                            for (int k = 0; k < _headerCells.Count; k++) {
                                if (!_headerCells[k].Contains(mp)) continue;
                                SRApi.LogInfo("[布局预设] 表头按下: 列=" + _headerCols[k] + " moveCol=" + _args.moveCol
                                    + " dragCol=" + _args.dragCol + " inBox=" + _args.box.Contains(mp));
                                break;
                            }
                        } catch (Exception) { }
                    }
                    if (e.type == EventType.MouseUp) SRApi.BlockWindowDrag = false;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/表格绘制", __ex);
            }
        }

        // 表格高度：按页面视口高度扣掉上方固定行，尽量让整页不出现页面级滚动条；最小 140、最大 480 设计单位。
        // 预设列表本身在固定高度区域内用 TableWheelInput 滚动，所以页面不需要滚动。
private static float TableHeight() {
            // 和联机列表同一条核心公式：页面视口 − 表格之前已用高度，再夹到 [min, 表高配置]。
            // 以前这里固定扣 300px，表格下方留白、手轮落在留白上就变成滚页面。
            try {
                Rect last = GUILayoutUtility.GetLastRect();
                // TableFitHeight 会把高度夹到"表高配置"上限；预设页这个配置值偏小，
                // 于是表格永远填不满、下面留一大片空白。这里直接用剩余可用高度，
                // 表高配置只在它更大时作为上限（用户拖高过才生效）。
                float avail = SRApi.ContentArea.yMax - last.yMax - SRApi.FooterHeight - SRApi.Ctl.Sc(8);
                float cap = _tableHEntry != null && _tableHEntry.Value > 0 ? SRApi.Ctl.Sc(_tableHEntry.Value) : 0f;
                float h = avail > 0f ? avail : SRApi.Ctl.Sc(340);
                if (cap > h) h = Mathf.Min(cap, avail > 0f ? Mathf.Max(avail, cap) : cap);
                return Mathf.Max(SRApi.Ctl.Sc(140), h);
            } catch (Exception __th) { SRApi.Guard.Log("预设/表格高度", __th); return SRApi.Ctl.Sc(340); }
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

        private static string[] RowCells(List<PresetInfo> visible, int r) {
            PresetInfo p = PresetAt(visible, r);
            if (p == null) {
                for (int i = 0; i < ColCount; i++) _cells[i] = "";
                return _cells;
            }
            _cells[ColName] = p.IsLocal
                ? SRApi.T("本地", "Local") + " · " + (p.Name ?? "")
                : (p.Name ?? "");
            _cells[ColLevel] = _levelShowCode ? LevelCodeText(p) : LevelText(p);
            _cells[ColMode] = ModeText(p.Mode);
            _cells[ColMods] = ModsLabel(p);
            _cells[ColTime] = TimeText(p);
            _cells[ColAction] = "";
            return _cells;
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
