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
	[SrFeature("sr.buildingpresets", "BuildingPresets", "1.0")]
	public class BuildingPresets : ITweak {
		// 配置段名：导航栏目名 / LocSec / Config.Bind 三处必须完全一致。
		private const string Section = "Building Presets";
		private const string IndexFileName = "index.json";
		// 索引格式版本：v2 起新增 level / ok；v3 起新增 levelName；v4 起新增 levelCode（关卡代码/快照码）。
		// 老索引缺字段按默认值处理并在扫描时自愈。
		private const int IndexVersion = 4;
		// 排序方式：0=时间（新→旧），1=名称（A→Z）。旧配置里的 2/3 已弃用，读取时夹到 0。
		private const int SortModeMin = 0;
		private const int SortModeMax = 1;
		// 表格列索引（与 TableArgs.colW 顺序一致）：名称 / 关卡 / 模式 / 时间 / 操作（操作列钉在右侧）。
		private const int ColName = 0;
		private const int ColLevel = 1;
		private const int ColMode = 2;
		private const int ColTime = 3;
		private const int ColAction = 4;
		private const int ColCount = 5;
		// 列宽（设计单位）默认值与允许范围；顺序与 Col* 一致。
		// 操作列的实际默认宽由 DefaultActionColW() 按当前语言按钮文字实时计算（DefColW[ColAction] 仅作兜底）。
		private static readonly float[] DefColW = new float[] { 150f, 84f, 64f, 96f, 200f };
		private const float ColWidthMin = 40f;
		private const float ColWidthMax = 600f;
		private const string ColWidthsKey = "Preset Widths";
		private const string ColOrderKey = "Preset Order";
		private const string ColHiddenKey = "Preset Hidden";
		// 操作列按钮种类：0=加载 1=重命名 2=导出 3=删除。
		private const int ActLoad = 0;
		private const int ActRename = 1;
		private const int ActExport = 2;
		private const int ActDelete = 3;
		// 从剪贴板导入的上限：方块数 / XML 字符数。超出直接拒绝，避免一次性重建拖垮帧率或整段解析爆内存。
		private const int MaxImportBlocks = 3000;
		private const int MaxImportChars = 2000000;
		// 游戏本地关卡快照：7z 压缩文件的大小 / 解压后 XML 大小上限（与导入上限同一套防护，防止畸形文件拖垮扫描）。
		private const int MaxSnapshotBytes = 4 * 1024 * 1024;
		private const int MaxLocalXmlBytes = 8 * 1024 * 1024;
		// 二次确认窗口（秒）：超时后按钮自动恢复第一次点击前的文案。
		private const float ConfirmWindow = 5f;
		// 重载锁自愈阈值（秒）：与功能 Level 的 45 秒保持一致。
		private const float ReloadLockStale = 45f;
		// 等游戏消费 levelPortalXml 的硬上限（秒）：与功能 Level 的 25 秒兜底一致。
		private const float PresetConsumeTimeout = 25f;

		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<int> _maxPresets;
		private static ConfigEntry<bool> _skipTreehouse;   // 跳过树屋流程（联机直接替换关卡，风险自负）
		private static bool _actionHitThisFrame;   // 本帧是否命中操作按钮（空白处取消二次确认）
		private static ConfigEntry<int> _sortModeEntry;
		private static ConfigEntry<string> _colWidthsEntry;
		private static ConfigEntry<string> _colOrderEntry;
		private static ConfigEntry<string> _colHiddenEntry;
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
		private static int[] _order = new int[] { 0, 1, 2, 3, 4 };
		private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
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
		private static readonly SRApi.TableAction[] _actActions = new SRApi.TableAction[] {
			new SRApi.TableAction { OnClick = DoActLoad },
			new SRApi.TableAction { OnClick = DoActRename },
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
		private static Vector2 _scrollCols;
		private static float _maxScrollX;
		private static float _maxScrollY;
		private static bool _sbDragging;
		private static float _sbGrabY;
		private static float _tableBias;   // 表格高度自校准偏差（核心 TableAutoFit/TableAfterDraw 维护）
		private static bool _sbHandled;    // 本帧核心滚动条是否已处理鼠标事件
		private static Rect _sbTrack;
		private static Rect _sbThumb;
		private static bool _dragH;
		private static float _dragWatchdog;
		// 关卡列显示模式：false = 关卡名称（本地化），true = 关卡代码（快照码/场景名）；运行期状态，不落配置。
		private static bool _levelShowCode;

		// 加载预设时的重载锁（本功能自己的，和 Level 的锁互相不可见）。
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
			// 已删除的配置键：Show Timestamps（时间列固定显示，不再提供开关）。
			// 用功能自注册的迁移规则清理旧 cfg，避免遗留孤儿键（只列真正不再 Bind 的键）。
			try { SRApi.Migration.Remove(Section, "Show Timestamps"); }
			catch (Exception __mx) { SRApi.Guard.Log("布局预设/注册淘汰键", __mx); }
			SRApi.LocKey(Section, "Enabled", "布局预设总开关", "Layout presets");
			SRApi.LocDesc(Section, "Enabled",
				"布局预设总开关\n开启后可保存和加载方块布局预设\n关闭后本页仍可查看已有预设，但所有操作都不生效；与模组总开关相互独立。",
				"master switch for layout presets\nwhen on, block layout presets can be saved and loaded on this page\nwhen off, existing presets are still listed but every action is inactive; independent from the mod-wide master switch");
			SRApi.LocKey(Section, "Max Presets", "预设数量上限", "Max presets");
			SRApi.LocDesc(Section, "Max Presets",
				"预设数量上限\n列表最多保留多少个预设，达到上限后必须先删除旧预设才能保存新布局\n范围 1 到 100；只限制新保存，不会删除磁盘上已有的预设文件。",
				"maximum number of presets\ncaps how many presets the list keeps; when reached, delete an old preset before saving a new layout\nrange 1-100; only limits new saves and never deletes existing preset files");
			SRApi.LocKey(Section, "Sort Mode", "列表排序", "Sort mode");
			SRApi.LocDesc(Section, "Sort Mode",
				"列表排序方式\n0=时间（新→旧），1=名称（A→Z）\n由列表页的排序下拉维护，不在配置区重复显示",
				"preset list sort mode\n0 = time (newest first), 1 = name (A-Z)\nmaintained by the sort dropdown on the list page and not repeated in the settings area");
			// 换场景后下次打开面板重新扫盘（手动放入/删除的 XML、其它存档的改动都能看到）。
			try { SRApi.RegisterSceneHook(delegate { _loaded = false; }); }
			catch (Exception __sh) { SRApi.Guard.Log("布局预设/注册场景钩子", __sh); }
			// 返回树屋：注册动作并允许右键绑键（悬浮提示里不写绑定说明）。
			try { SRApi.HotkeyAction("presets.treehouse", "返回树屋", delegate { ReturnToTreehouse(); }); }
			catch (Exception __hk) { SRApi.Guard.Log("布局预设/注册返回树屋快捷键", __hk); }
			SRApi.LocKey(Section, "Skip Treehouse", "跳过树屋流程", "Skip treehouse");
			SRApi.LocDesc(Section, "Skip Treehouse",
				"跳过树屋流程\n勾选后联机替换关卡时不再先回树屋，而是直接替换场景进入目标关卡\n房客的关卡选择状态可能不同步（按关卡读取的岩浆/统计等次要设置可能不一致）",
				"skip the treehouse flow\nwhen on, online level replacement switches the scene directly instead of returning to the treehouse first\nclients' selected-level state may be out of sync (minor per-level settings like lava/stats may differ)");
			SRApi.LocKey(Section, ColWidthsKey, "列表列宽（内部）", "Preset widths (internal)");
			SRApi.LocDesc(Section, ColWidthsKey,
				"预设列表列宽\n由列表表头拖动自动写入，逗号分隔的设计单位\n一般无需手动修改；非法值会回退默认列宽",
				"preset list column widths\nwritten automatically when the list header is dragged; comma-separated design units\nnormally no need to edit; invalid values fall back to the default widths");
			SRApi.LocKey(Section, ColOrderKey, "列表列序（内部）", "Preset order (internal)");
			SRApi.LocDesc(Section, ColOrderKey,
				"预设列表列顺序\n由列表表头拖动换位自动写入，逗号分隔的列索引\n一般无需手动修改；非法值会回退默认列序",
				"preset list column order\nwritten automatically when the list header is dragged; comma-separated column indices\nnormally no need to edit; invalid values fall back to the default order");
			SRApi.LocKey(Section, ColHiddenKey, "隐藏列（内部）", "Preset hidden (internal)");
			SRApi.LocDesc(Section, ColHiddenKey,
				"预设列表隐藏列\n右键表头隐藏列后自动写入，逗号分隔的列索引\nShift+右键表头可恢复显示全部列；非法索引会被忽略",
				"preset list hidden columns\nwritten automatically after hiding a column with a header right-click; comma-separated column indices\nShift+right-click the header to show all columns again; invalid indices are ignored");
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
		private static List<PresetInfo> VisiblePresets() {
			List<PresetInfo> list = new List<PresetInfo>();
			try {
				string q = (_search ?? "").Trim();
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					if (p == null) continue;
					if (string.Equals(_typeFilter, "preset", StringComparison.Ordinal) && p.IsLocal) continue;
					if (string.Equals(_typeFilter, "local", StringComparison.Ordinal) && !p.IsLocal) continue;
					if (!string.IsNullOrEmpty(_levelFilter)
						&& !string.Equals(p.Level, _levelFilter, StringComparison.OrdinalIgnoreCase)) continue;
					if (!string.IsNullOrEmpty(_modeFilter)
						&& !string.Equals(p.Mode, _modeFilter, StringComparison.OrdinalIgnoreCase)) continue;
					if (q.Length > 0) {
						// 名称 / 关卡名是规格要求的过滤维度；模式文本也顺手匹配，便于按模式搜索。
						bool hit = ContainsText(p.Name, q) || ContainsText(p.Level, q) || ContainsText(ModeText(p.Mode), q);
						if (!hit) continue;
					}
					list.Add(p);
				}
				list.Sort(ComparePreset);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/过滤排序", __ex);
			}
			return list;
		}

		private static bool ContainsText(string text, string query) {
			try { return text != null && query != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
			catch { return false; }
		}

		// ---------- 页面渲染（单页：保存与导入 + 搜索/排序 + 预设列表） ----------

		// 同一页按 SectionTitle 分保存 / 导入导出 / 搜索排序筛选 / 预设列表四个区；页面本身不滚动（NoPageScrollBars + 表格高度按视口计算），
		// 预设列表在固定高度区域内用自己的滚轮滚动。
		// 页面内容在布局空间里的起点 y（零高度探针），用于计算表格上方已用高度（同联机列表）。
		private static float _pageTopY;
		private static bool _pageTopYSet;

		private static void Render() {
			try {
				EnsureLoaded();
				EnsureCols();
				// 本帧开始前的二次确认状态：只有"之前已确认、本帧又没点中操作按钮"才在空白处取消，
				// 避免把本帧其它按钮（保存/导入）刚点出来的二次确认立刻清掉。
				string pendingBefore = _pendingKey;
				// 页面级滚动关闭：内容按视口高度布局，滚轮只在预设表格内部滚动。
				try {
					SRApi.NoPageScrollBars = true;
					SRApi.PageScroll = Vector2.zero;
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/关闭页面滚动", __ex); }
				Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
				if (Event.current != null && Event.current.type == EventType.Layout) { _pageTopY = topProbe.y; _pageTopYSet = true; }

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
					if (GUILayout.Button(new GUIContent(SRApi.T("全部导出到目录", "Export all to folder"),
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
					if (GUILayout.Button(new GUIContent(SRApi.T("刷新", "Refresh"),
							SRApi.T("重新扫描预设目录（手动放入/删除了 XML 时用），也用于重建索引",
								"rescan the presets folder (after manually adding/removing XML files) and rebuild the index")),
						SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Btn.CalcSize(new GUIContent(SRApi.T("刷新", "Refresh"))).x + SRApi.Ctl.Sc(18)), GUILayout.Height(W(24)))) {
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
						SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("跳过树屋流程", "Skip treehouse"),
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
				GUILayout.Space(W(2));
				// 高度：核心统一公式（页面顶部已用高度 + 表格高度 = 底栏之上可用高度），窗口变化实时跟随。
				Rect last = GUILayoutUtility.GetLastRect();
				// 直接以“页面内容底界 - 当前布局 Y”为表格高度：窗口大小/栏高变化都自动跟随，不再依赖预留估算。
				// 表格高度 = 页面视口高 − 页面内已用高度（都是布局量，不掺窗口/底栏坐标）。
				float usedAbove = _pageTopYSet ? Mathf.Max(0f, last.yMax - _pageTopY) : SRApi.Ctl.Sc(300);
				float tableH = Mathf.Max(SRApi.Ctl.Sc(120), SRApi.PageViewportHeight - usedAbove);
				_actionHitThisFrame = false;
				RenderPresetTable(tableH);
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
				int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open);
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
		private static void RenderPresetTable(float tableH) {
			try {
				List<PresetInfo> visible = VisiblePresets();
				string title = SRApi.T("- 预设列表", "- Preset list") + " (" + visible.Count + "/" + _presets.Count
					+ SRApi.T("，上限 ", ", limit ") + MaxPresets + ")";
				SRApi.Ctl.SectionTitle(title);
				float titleH = 0f;
				try { titleH = Mathf.Max(0f, GUILayoutUtility.GetLastRect().height); } catch { }
				float rowH = SRApi.Ctl.Sc(26);
				float headerH = SRApi.Ctl.Sc(24);
				if (tableH <= 0f) tableH = TableHeight();
				// Render() 传入的 tableH 是「分区标题 + 表格」可用的总高度；分区标题本身也占高，
				// 这里扣掉它的实测高度，否则表格底部会超出可视区、最后一行被底栏裁掉且滚不到。
				tableH = Mathf.Max(SRApi.Ctl.Sc(120), tableH - titleH);
				float rowsH = Mathf.Max(SRApi.Ctl.Sc(80), tableH - headerH);
				Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(tableH));
				if (box.width < SRApi.Ctl.Sc(220)) return;
				EnsureCols();
				// 操作列宽始终跟随当前语言的按钮文字：语言热切换后也能立刻避免英文标签被截断。
				_colW[ColAction] = DefaultActionColW();
				if (_args == null) _args = new SRApi.TableArgs();
				float pinW = SRApi.Ctl.Sc(_colW[ColAction]);
				// 有纵向溢出时，在数据区与钉住列之间预留滚动条宽度（与联机列表一致）。
				bool __overflowY = visible.Count * rowH > rowsH + 0.5f;
				float __sbW = __overflowY ? SRApi.Ctl.Sc(13) : 0f;
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
				_args.defaultColW = null;
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
				SRApi.TableDraw(_args);
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
			float fallback = SRApi.Ctl.Sc(340);
			try {
				float areaH = SRApi.ContentArea.height;
				if (areaH <= 0f) return fallback;
				float reserve = SRApi.Ctl.Sc(300);
				float avail = areaH - SRApi.FooterHeight - reserve;
				return Mathf.Clamp(avail, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(480));
			} catch { return fallback; }
		}

		// Shift+右键表头：核心清空隐藏列后回调这里，落盘并提示。
		private static void ResetTable() {
			try {
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
					case ColTime: return SRApi.T("保存时间（本地时间）", "save time (local time)");
					case ColAction: return SRApi.T("加载 / 重命名 / 导出到剪贴板 / 删除；删除需点两次确认", "load / rename / export to clipboard / delete; delete needs a second click");
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
			_cells[ColTime] = TimeText(p);
			_cells[ColAction] = "";
			return _cells;
		}

		private static PresetInfo PresetAt(List<PresetInfo> visible, int row) {
			try { return (visible != null && row >= 0 && row < visible.Count) ? visible[row] : null; }
			catch { return null; }
		}

		// 钉住操作列：每行 4 个按钮（加载 / 重命名 / 导出 / 删除），删除在二次确认窗口内显示「确认」。
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
				_actActions[ActRename].Label = SRApi.T("重命名", "Rename");
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

		private static void DoActRename() {
			PresetInfo p = _actPreset;
			if (p == null || p.IsLocal) return;
			ClearPending();
			_renameKey = p.Key;
			_renameText = p.Name;
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
				for (int i = 0; i < ColCount; i++) _colW[i] = DefColW[i];
				for (int i = 0; i < ColCount; i++) _order[i] = i;
				string w = _colWidthsEntry != null ? _colWidthsEntry.Value : null;
				if (!string.IsNullOrEmpty(w)) {
					string[] p = w.Split(',');
					if (p.Length == ColCount) {
						for (int i = 0; i < p.Length; i++) {
							float f;
							if (float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f)
								&& f >= ColWidthMin && f <= ColWidthMax) _colW[i] = f;
						}
					}
				}
				string o = _colOrderEntry != null ? _colOrderEntry.Value : null;
				if (!string.IsNullOrEmpty(o)) {
					string[] p = o.Split(',');
					int[] src = new int[p.Length];
					bool ok = p.Length == ColCount;
					for (int i = 0; ok && i < p.Length; i++) {
						if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out src[i])) ok = false;
					}
					if (ok) _order = NormalizeOrder(src);
				}
				// 隐藏列：逐项做范围/合法性裁剪（操作列固定可见，不允许被隐藏）。
				string h = _colHiddenEntry != null ? _colHiddenEntry.Value : null;
				if (!string.IsNullOrEmpty(h)) {
					string[] p = h.Split(',');
					for (int i = 0; i < p.Length; i++) {
						int k;
						if (int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
							&& k >= 0 && k < ColCount && k != ColAction) _hiddenCols.Add(k);
					}
				}
				// 操作列宽度强制用默认值：默认宽按当前语言的 4 个按钮文字实测（见 DefaultActionColW），
				// 旧配置里持久化的大宽度会在这里被收敛，避免右侧留白。表头仍可正常显示（宽度不允许拖动）。
				_colW[ColAction] = DefaultActionColW();
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取列设置", __ex);
			}
		}

		// 操作列默认宽 = 4 个按钮的自适应文字宽 + 按钮间距 + 钉住列左右边距（设计单位）。
		// 每行按钮由核心按 BtnClip.CalcSize(label).x + 11px 计算，这里用同一公式取最宽标签（含二次确认的「确认」），
		// 保证中文 / 英文、普通态 / 确认态都不会被截断；DefColW[ColAction] 仅作样式不可用时的兜底。
		private static float DefaultActionColW() {
			try {
				GUIStyle st = SRApi.Ctl.BtnClip;
				if (st == null) return DefColW[ColAction];
				float scale = SRApi.Ctl.Sc(1f);
				if (scale <= 0f) scale = 1f;
				string[][] labels = new string[][] {
					new string[] { SRApi.T("加载", "Load"), SRApi.T("确认", "Confirm") },
					new string[] { SRApi.T("重命名", "Rename") },
					new string[] { SRApi.T("导出", "Export") },
					new string[] { SRApi.T("删除", "Delete"), SRApi.T("确认", "Confirm") }
				};
				// 6px = 钉住列左右各 2px + 行内左 2px；间距 2px/个（与核心 TableActionButtons 一致）。
				float total = SRApi.Ctl.Sc(6f);
				for (int i = 0; i < labels.Length; i++) {
					float bw = 0f;
					for (int j = 0; j < labels[i].Length; j++) {
						bw = Mathf.Max(bw, st.CalcSize(new GUIContent(labels[i][j])).x);
					}
					total += bw + SRApi.Ctl.Sc(11f);
					if (i > 0) total += SRApi.Ctl.Sc(2f);
				}
				float design = Mathf.Ceil(total / scale);
				return Mathf.Clamp(design, ColWidthMin, ColWidthMax);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/计算操作列宽", __ex);
				return DefColW[ColAction];
			}
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
			int[] def = new int[] { 0, 1, 2, 3, 4 };
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
				if (_colWidthsEntry != null && _colW != null) {
					string[] p = new string[ColCount];
					for (int i = 0; i < ColCount; i++) p[i] = Mathf.RoundToInt(_colW[i]).ToString(CultureInfo.InvariantCulture);
					_colWidthsEntry.Value = string.Join(",", p);
				}
				if (_colOrderEntry != null && _order != null) {
					string[] p = new string[_order.Length];
					for (int i = 0; i < _order.Length; i++) p[i] = _order[i].ToString(CultureInfo.InvariantCulture);
					_colOrderEntry.Value = string.Join(",", p);
				}
				if (_colHiddenEntry != null) {
					List<string> l = new List<string>();
					foreach (int k in _hiddenCols) l.Add(k.ToString(CultureInfo.InvariantCulture));
					l.Sort();
					_colHiddenEntry.Value = string.Join(",", l.ToArray());
				}
				// 配置落盘走核心节流器（MarkConfigDirty + FlushConfig）。
				if (_colWidthsEntry != null) SRApi.Ctl.MarkConfigDirty(_colWidthsEntry.ConfigFile);
				SRApi.FlushConfig();
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/保存列设置", __ex);
			}
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

		// ---------- 保存 / 导入 / 导出 / 删除 / 重命名 ----------

		private static void SaveCurrent() {
			try {
				if (!ActionAllowed()) return;
				XmlDocument doc;
				string mode;
				if (!TryGetCurrentSnapshot(out doc, out mode)) return;
				// <destroyed> 节点在重载后的新场景里没有意义，且会让整批解析中断（与广播快照的处理一致），保存前剥掉。
				StripDestroyed(doc);
				int blocks = CountBlocks(doc);
				if (blocks <= 0) {
					SRApi.Notify(SRApi.T("当前场景没有可保存的方块", "No blocks to save in the current scene"));
					return;
				}
				string level = LevelFromDoc(doc);
				string levelName = CurrentLevelDisplayName(level);
				string levelCode = CurrentLevelCode(level);
				string name = (_newName ?? "").Trim();
				// 名称留空：默认用「关卡名 + 时间」，避免每次手工起名。
				if (name.Length == 0) {
					string baseName = string.IsNullOrEmpty(level) ? SRApi.T("布局", "Layout") : level;
					name = baseName + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				}
				if (name.Length > 40) name = name.Substring(0, 40);
				SaveXml(doc.OuterXml, name, mode, blocks, level, levelName, levelCode);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/保存", __ex);
			}
		}

		// 保存 / 导入共用的落盘逻辑：同名覆盖需二次确认，数量上限只拦新预设。
		// level 来自 XML 根节点 levelSceneName（读不到传空串），写进预设元数据用于列表/加载前校验。
		private static void SaveXml(string xml, string name, string mode, int blocks, string level, string levelName, string levelCode) {
			try {
				// 保存侧与读取/导入同一上限：避免产生"能存不能取"的文件（读不出来只能手动删）。
				if (string.IsNullOrEmpty(xml)) return;
				if (xml.Length > MaxImportChars || blocks > MaxImportBlocks) {
					SRApi.Notify(SRApi.T("布局太大，无法保存为预设（超出读取上限）", "The layout is too large to save as a preset (exceeds the load limit)"));
					SRApi.LogWarn("[布局预设] 保存被拒：xml 长度=" + xml.Length + " 方块数=" + blocks);
					return;
				}
				string root = PresetsRoot();
				string dirName = SafeDirName(mode);
				string dir = Path.Combine(root, dirName);
				string fileName = SafeFileName(name) + ".xml";
				string path = Path.Combine(dir, fileName);
				string key = dirName + "/" + fileName;
				bool exists = false;
				try { exists = File.Exists(path) || FindPreset(key) != null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/检查同名", __ex); }
				if (exists) {
					if (!ConfirmOrArm("overwrite:" + key)) {
						_status = SRApi.T("同名预设已存在，再点一次「确认覆盖」：", "A preset with this name exists; click Confirm overwrite again: ") + name;
						SRApi.Notify(_status);
						return;
					}
				} else {
					ClearPending();
					if (CountPresetEntries() >= MaxPresets) {
						SRApi.Notify(SRApi.T("预设数量已达上限 ", "Preset limit reached: ") + MaxPresets
							+ SRApi.T("，请先删除旧预设", "; delete an old preset first"));
						return;
					}
				}
				// 把显示名写进 XML 根属性：索引丢失/重建时名字不会退回旧文件名。
				try {
					XmlDocument __md = ParseXmlSafe(xml);
					if (__md.DocumentElement != null && !string.IsNullOrEmpty(name))
						__md.DocumentElement.SetAttribute("srName", name);
					xml = __md.OuterXml;
				} catch (Exception __nx) { SRApi.Guard.Log("布局预设/写入显示名", __nx); }
				Directory.CreateDirectory(dir);
				File.WriteAllText(path, xml, new UTF8Encoding(false));
				PresetInfo p = FindPreset(key);
				if (p == null) {
					p = new PresetInfo();
					p.Mode = dirName;
					p.File = fileName;
					_presets.Add(p);
				}
				p.Name = name;
				p.Blocks = blocks;
				p.TimeUtc = DateTime.UtcNow;
				p.FilePath = path;
				p.Level = level ?? "";
				p.LevelName = levelName ?? "";
				p.LevelCode = levelCode ?? "";
				p.Ok = true;
				SortPresets();
				SaveIndex();
				_status = SRApi.T("已保存预设：", "Saved preset: ") + name;
				SRApi.LogInfo("[布局预设] " + _status);
				SRApi.LogInfo("[布局预设] 保存 " + key + "（" + blocks + " 块）");
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/写文件", __ex);
				SRApi.Notify(SRApi.T("保存预设失败", "Failed to save the preset"));
			}
		}

		private static void ImportFromClipboard() {
			try {
				if (!ActionAllowed()) return;
				string xml = null;
				try { xml = GUIUtility.systemCopyBuffer; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取剪贴板", __ex); }
				if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) {
					SRApi.Notify(SRApi.T("剪贴板是空的", "The clipboard is empty"));
					return;
				}
				ImportXmlText(xml, null, SRApi.T("剪贴板", "clipboard"));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导入(剪贴板)", __ex);
			}
		}

		// 从磁盘 XML 文件导入：支持绝对路径，也支持相对 BepInEx 根目录 / 配置目录 / 预设目录的路径。
		private static void ImportFromPath() {
			try {
				if (!ActionAllowed()) return;
				string input = (_importPath ?? "").Trim();
				if (input.Length >= 2 && input[0] == '"' && input[input.Length - 1] == '"') input = input.Substring(1, input.Length - 2).Trim();
				if (input.Length == 0) {
					SRApi.Notify(SRApi.T("请先填写 XML 文件路径", "Enter the XML file path first"));
					return;
				}
				string path = ResolveImportPath(input);
				if (string.IsNullOrEmpty(path)) {
					SRApi.Notify(SRApi.T("找不到 XML 文件：", "XML file not found: ") + input);
					return;
				}
				long length = 0;
				try { length = new FileInfo(path).Length; }
				catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取文件大小", __ex); }
				if (length > MaxImportChars) {
					SRApi.Notify(SRApi.T("XML 文件过大，已拒绝导入", "The XML file is too large; import rejected"));
					return;
				}
				string xml = null;
				try { xml = File.ReadAllText(path, Encoding.UTF8); }
				catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取路径文件", __ex); }
				if (string.IsNullOrEmpty(xml)) {
					SRApi.Notify(SRApi.T("读取 XML 文件失败", "Failed to read the XML file"));
					return;
				}
				string defaultName = null;
				try { defaultName = Path.GetFileNameWithoutExtension(path); } catch { }
				ImportXmlText(xml, defaultName, SRApi.T("文件", "file"));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导入(路径)", __ex);
			}
		}

		// 剪贴板 / 文件路径共用的 XML 导入：校验根节点/方块数/关卡名后落盘（同名覆盖仍走 SaveXml 的二次确认）。
		private static void ImportXmlText(string xml, string defaultBaseName, string sourceLabel) {
			try {
				if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) {
					SRApi.Notify(SRApi.T("导入内容为空", "The import content is empty"));
					return;
				}
				xml = xml.Trim();
				if (xml.Length > MaxImportChars) {
					SRApi.Notify(SRApi.T("导入内容过大，已拒绝（上限 ", "Import content is too large; rejected (limit ")
						+ (MaxImportChars / 1000) + SRApi.T(" 千字符）", "k characters)"));
					return;
				}
				XmlDocument doc = null;
				try {
					doc = ParseXmlSafe(xml);
				} catch (Exception __ex) {
					// 解析失败是非常常见的用户错误（粘错东西），只记一次 Guard 日志并提示，不抛到页面。
					SRApi.Guard.Log("布局预设/解析导入 XML", __ex);
				}
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
					SRApi.Notify(SRApi.T("导入内容不是关卡布局 XML（根节点必须是 scene）", "The import content is not level layout XML (root node must be scene)"));
					return;
				}
				int blocks = CountBlocks(doc);
				if (blocks <= 0) {
					SRApi.Notify(SRApi.T("导入 XML 里没有方块", "The imported XML contains no blocks"));
					return;
				}
				if (blocks > MaxImportBlocks) {
					SRApi.Notify(SRApi.T("方块数超过导入上限 ", "Block count exceeds the import limit ") + MaxImportBlocks
						+ SRApi.T("，已拒绝导入", "; import rejected"));
					return;
				}
				string level = LevelFromDoc(doc);
				if (string.IsNullOrEmpty(level)) {
					SRApi.Notify(SRApi.T("XML 缺少 levelSceneName，无法确定属于哪个关卡", "The XML has no levelSceneName, so its level cannot be determined"));
					return;
				}
				StripDestroyed(doc);
				string name = (_newName ?? "").Trim();
				if (name.Length == 0 && !string.IsNullOrEmpty(defaultBaseName)) name = defaultBaseName.Trim();
				if (name.Length == 0) name = SRApi.T("导入 ", "Imported ") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				if (name.Length > 40) name = name.Substring(0, 40);
				string mode = "IMPORTED";
				try {
					GameSettings st = GameSettings.GetInstance();
					if (st != null) mode = st.GameMode.ToString();
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取模式(导入)", __ex); }
				SRApi.LogInfo("[布局预设] 从" + sourceLabel + "导入：" + name + "（" + blocks + " 块，关卡 " + level + "）");
				SaveXml(doc.OuterXml, name, mode, blocks, level, LevelDisplayNameForScene(level), LevelCodeFromDoc(doc));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导入XML", __ex);
			}
		}

		// 路径解析：绝对路径直接使用；相对路径依次尝试 当前目录 / BepInEx 根目录 / 配置目录 / 预设目录。
		private static string ResolveImportPath(string input) {
			try {
				if (string.IsNullOrEmpty(input)) return null;
				try { if (File.Exists(input)) return Path.GetFullPath(input); } catch { }
				string p = null;
				try { p = Path.Combine(Paths.BepInExRootPath, input); if (File.Exists(p)) return p; } catch { }
				try { p = Path.Combine(Paths.ConfigPath, input); if (File.Exists(p)) return p; } catch { }
				try { p = Path.Combine(PresetsRoot(), input); if (File.Exists(p)) return p; } catch { }
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/解析导入路径", __ex);
			}
			return null;
		}

		private static void ExportPreset(PresetInfo p) {
			try {
				if (p == null) return;
				if (p.IsLocal) return; // 本地关卡是游戏存档，不提供导出
				if (!ActionAllowed()) return;
				string xml = ReadAllText(p.FilePath);
				if (string.IsNullOrEmpty(xml)) {
					SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
					return;
				}
				try {
					GUIUtility.systemCopyBuffer = xml;
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/写剪贴板", __ex);
					SRApi.Notify(SRApi.T("写入剪贴板失败", "Failed to write to the clipboard"));
					return;
				}
				_status = SRApi.T("已导出到剪贴板：", "Exported to clipboard: ") + p.Name;
				SRApi.LogInfo("[布局预设] " + _status);
				SRApi.Notify(_status);   // 导出是显式操作，给屏幕提示
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导出", __ex);
			}
		}

		// 全部导出到目录：预设目录/exports/<时间>/<模式>/<文件>，并附带一份 index.json。
		private static void ExportAllToDirectory() {
			try {
				if (!ActionAllowed()) return;
				if (CountPresetEntries() == 0) {
					SRApi.LogInfo("[布局预设] " + SRApi.T("当前没有可导出的预设", "There are no presets to export"));
					return;
				}
				string baseDir = Path.Combine(PresetsRoot(), "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
				int copied = 0;
				int failed = 0;
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					if (p == null || p.IsLocal) continue;
					try {
						string xml = ReadAllText(p.FilePath);
						if (string.IsNullOrEmpty(xml)) { failed++; continue; }
						string dir = Path.Combine(baseDir, SafeDirName(p.Mode));
						Directory.CreateDirectory(dir);
						File.WriteAllText(Path.Combine(dir, p.File), xml, new UTF8Encoding(false));
						copied++;
					} catch (Exception __ex) {
						SRApi.Guard.Log("布局预设/导出全部单项", __ex);
						failed++;
					}
				}
				try {
					string index = Path.Combine(PresetsRoot(), IndexFileName);
					if (File.Exists(index)) {
						Directory.CreateDirectory(baseDir);
						File.Copy(index, Path.Combine(baseDir, IndexFileName), true);
					}
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/导出全部索引", __ex);
				}
				if (copied == 0) {
					SRApi.Notify(SRApi.T("全部导出失败（详见日志）", "Exporting all presets failed (see the log)"));
					return;
				}
				_status = SRApi.T("已导出 ", "Exported ") + copied + SRApi.T(" 个预设到：", " presets to: ") + baseDir
					+ (failed > 0 ? SRApi.T("（失败 ", " (failed ") + failed + SRApi.T(" 个）", ")") : "");
				SRApi.LogInfo("[布局预设] " + _status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/全部导出", __ex);
			}
		}

		private static void DeletePreset(PresetInfo p) {
			try {
				if (p == null) return;
				if (p.IsLocal) return; // 游戏本地存档不允许从这里删除
				if (!ActionAllowed()) return;
				try {
					if (File.Exists(p.FilePath)) File.Delete(p.FilePath);
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/删除文件", __ex);
					SRApi.Notify(SRApi.T("删除预设文件失败", "Failed to delete the preset file"));
					return;
				}
				_presets.Remove(p);
				SaveIndex();
				_status = SRApi.T("已删除预设：", "Deleted preset: ") + p.Name;
				SRApi.LogInfo("[布局预设] " + _status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/删除", __ex);
			}
		}

		private static void CommitRename(PresetInfo p) {
			try {
				if (p == null) return;
				if (p.IsLocal) return; // 本地关卡名来自游戏存档，不在这里改名
				if (!ActionAllowed()) return;
				string name = (_renameText ?? "").Trim();
				if (name.Length == 0) {
					SRApi.Notify(SRApi.T("名称不能为空", "The name cannot be empty"));
					return;
				}
				if (name.Length > 40) name = name.Substring(0, 40);
				p.Name = name;
				_renameKey = null;
				_renameText = null;
				SaveIndex();
				_status = SRApi.T("已重命名预设：", "Renamed preset: ") + name;
				SRApi.LogInfo("[布局预设] " + _status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/重命名", __ex);
			}
		}

		// ---------- 加载（房主）：写 levelPortalXml + 复刻原生重载流程 ----------

		// 为什么没有直接调 Level.ReloadLevel()：
		//   1) Level.ReloadLevel() 内部会重新调用 qs.GetCurrentXmlSnapshot() 并覆盖 QuickSaver.levelPortalXml，
		//      无法加载"不是当前布局"的预设；它也没有接受外部 XML 的公开入口。
		//   2) 拆分构建下 Level 编在独立的 SR_UCH_Level.dll，本文件只引用核心 SR_UCH.dll 与游戏程序集，
		//      按契约不能引用 Level 类型（否则拆分构建缺引用直接编译失败）。
		// 因此这里按 Level.ReloadLevelImpl / ReloadSceneRoutine 的同一套原生步骤复刻：
		//   写 QuickSaver.levelPortalXml -> 重置方块 ID 序列 -> 广播 PrepareToReloadScene -> 加载画面 FadeIn
		//   -> LobbyManager.ReloadScene(模式) -> 等游戏在 OnSetupStartLevel 消费 levelPortalXml。
		// 坑位与 Level 保持一致：失败必须清 levelPortalXml、锁 45 秒自愈、25 秒未消费兜底清 XML。
		private static void LoadPreset(PresetInfo p) {
			if (p == null) return;
			bool coroutineStarted = false;
			try {
				if (!ActionAllowed()) return;
				// GateHost 会放行开了"无视房主限制"的房客，但 WriteSyncVar / ReloadScene 在客户端不执行，会假成功。
				if (!SRApi.GateHost || !NetworkServer.active) {
					SRApi.NotifyT("仅房主可加载预设：", "Host only: ", p.Name);
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if (lm == null) {
					SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if (gc == null) {
					// 树屋/大厅（没有当前对局）：走游戏原生开局流程，进入预设关卡后应用方块。
					if (lm.CurrentLevelSelectController != null) {
						TryLoadPresetInTreehouse(p);
						return;
					}
					SRApi.Notify(SRApi.T("未进入有效对局或树屋", "Not in an active match or treehouse"));
					return;
				}
				if (gc.GetComponent<QuickSaver>() == null) {
					SRApi.Notify(SRApi.T("当前对局不支持加载预设", "Loading presets is not supported in this match"));
					return;
				}
				GameState gs = GameState.GetInstance();
				GameSettings st = GameSettings.GetInstance();
				if (gs == null || st == null) {
					SRApi.Notify(SRApi.T("游戏状态尚未就绪，请稍后再试", "Game state not ready; try again later"));
					return;
				}
				if (ReloadBlocked()) return;
				string xml = ReadPresetXml(p);
				if (string.IsNullOrEmpty(xml)) {
					SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
					return;
				}
				XmlDocument doc = null;
				try {
					doc = ParseXmlSafe(xml);
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/解析预设文件", __ex);
				}
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
					SRApi.Notify(SRApi.T("预设文件已损坏（根节点不是 scene）", "The preset file is damaged (root node is not scene)"));
					return;
				}
				string presetScene = "";
				string activeScene = "";
				try { presetScene = doc.DocumentElement.GetAttribute("levelSceneName"); } catch { }
				if (string.IsNullOrEmpty(presetScene)) presetScene = p.Level ?? "";
				try { activeScene = SceneManager.GetActiveScene().name; } catch { }
				bool levelMismatch = !string.IsNullOrEmpty(presetScene) && !string.IsNullOrEmpty(activeScene)
					&& !string.Equals(presetScene, activeScene, StringComparison.Ordinal);
				// 自定义关卡：场景名都是 BlankLevel，要靠快照名/代码区分是不是同一个自定义关卡。
				bool customPreset = string.Equals(presetScene, "BlankLevel", StringComparison.OrdinalIgnoreCase);
				bool customIdentityDiff = customPreset && !SameCustomLevel(p, gs);
				if (customIdentityDiff) levelMismatch = true;
				if (levelMismatch) {
					// 跨关卡加载 = 替换关卡：内置关卡走树屋 LaunchLevel；自定义关卡（BlankLevel）直接替换进 BlankLevel 场景并带上快照信息。
					GameState.LevelName targetLevel;
					if (customPreset) {
						targetLevel = GameState.LevelName.BLANKLEVEL;
					} else if (!TrySceneToLevel(presetScene, out targetLevel) || targetLevel == GameState.LevelName.BLANKLEVEL) {
						SRApi.Notify(SRApi.T("无法识别预设所属关卡，暂不支持自动替换关卡", "The preset's level could not be recognized; automatic level replacement is not supported"));
						return;
					}
					GameState.GameMode targetMode = ResolveLoadMode(p, st.GameMode);
					bool skipTreehouse = _skipTreehouse != null && _skipTreehouse.Value;
					bool hasRemoteClients = HasRemoteClients();
					// 自定义关卡在联机时只能走「跳过树屋流程」直接替换；未勾选时只提示，不执行。
					if (customPreset && hasRemoteClients && !skipTreehouse) {
						SRApi.Notify(SRApi.T("联机替换自定义关卡需要先勾选『跳过树屋流程』", "Replacing a custom level online requires enabling 'Skip treehouse' first"));
						return;
					}
					if (!ConfirmOrArm("replace:" + p.Key)) {
						_status = SRApi.T("将替换关卡：", "Level will be replaced: ") + LevelDisplayNameForScene(activeScene)
							+ SRApi.T(" → ", " -> ") + LevelDisplayNameForScene(presetScene)
							+ SRApi.T("；目标模式：", "; target mode: ") + ModeText(targetMode.ToString())
							+ SRApi.T("；5 秒内再点一次「确认」执行",
								"; click Confirm again within 5 seconds");
						SRApi.Notify(_status);
						return;
					}
					ClearPending();
					SetReloadBusy(true);
					try {
						// 单人/无远程客户端，或勾选了「跳过树屋流程」：直接在当前对局内切场景。
						// 否则退回树屋方案，因为游戏没有在“对局内”更新房客 SelectedLevel 的 RPC/消息。
						bool __directOk = (!hasRemoteClients || skipTreehouse) && StartDirectReplace(p, xml, targetLevel, targetMode);
						if (__directOk) {
							coroutineStarted = true;
							SRApi.LogInfo("[布局预设] 直接替换关卡并加载预设：" + p.Name
								+ " → " + (customPreset ? (string.IsNullOrEmpty(p.LevelName) ? SRApi.T("自定义关卡", "Custom level") : p.LevelName) : LevelDisplayNameForScene(presetScene))
								+ (skipTreehouse && hasRemoteClients ? SRApi.T("（已跳过树屋流程，房客关卡状态可能不同步）", " (treehouse flow skipped; clients' level state may be out of sync)") : ""));
						} else if (customPreset) {
							SetReloadBusy(false);   // 失败必须释放锁（否则后续所有加载都被拦）
							SRApi.Notify(SRApi.T("替换自定义关卡失败（详见日志）", "Failed to replace into the custom level (see the log)"));
							coroutineStarted = true;   // XML 已在 StartDirectReplace 内部清理
						} else {
							lm.StartCoroutine(ReplaceLevelRoutine(p.Name, xml, presetScene, targetLevel, targetMode));
							coroutineStarted = true;
							SRApi.LogInfo("[布局预设] 正在替换关卡并加载预设：" + p.Name
								+ SRApi.T("（会先回树屋再进入 ", " (returns to the treehouse first, then enters ") + LevelDisplayNameForScene(presetScene) + "）");
						}
					} catch (Exception __rx) {
						SRApi.Guard.Log("布局预设/开始替换关卡", __rx);
						try { QuickSaver.levelPortalXml = null; } catch { }
						SetReloadBusy(false);
						SRApi.Notify(SRApi.T("加载预设失败：无法开始替换关卡", "Failed to load the preset: could not start the level replacement"));
					}
					return;
				}
				ClearPending();
				SetReloadBusy(true);
				try {
					// 一次性静态字段：游戏在 OnSetupStartLevel 里读取后立即置 null 并原生广播给全员。
					QuickSaver.levelPortalXml = xml;
					// 不再校验/拦截模式差异：按预设自己的模式（或页面选择的加载模式）走原来的重载流程（ReloadScene 会把模式同步给房客）。
					GameState.GameMode toMode = ResolveLoadMode(p, st.GameMode);
					try { st.GameMode = toMode; } catch (Exception __mx) { SRApi.Guard.Log("布局预设/设置预设模式", __mx); }
					MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene {
						reloadToMode = toMode,
						snapshotInfo = gs.currentSnapshotInfo
					};
					NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
					if (LoadingInterstitialSplash.Instance != null) {
						LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
						LoadingInterstitialSplash.Instance.FadeIn();
					}
					// 成败提示在协程内真正执行完才发出；锁由协程末尾复位。
					lm.StartCoroutine(LoadPresetRoutine(p.Name, xml, toMode));
					coroutineStarted = true;
					SRApi.LogInfo("[布局预设] " + SRApi.T("加载预设：", "Loading preset: ") + p.Name
						+ "（" + LevelText(p) + "）"
						+ SRApi.T("，全员重载中（分数会重置）", "; everyone is reloading (scores will reset)"));
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/开始加载", __ex);
					SRApi.Notify(SRApi.T("加载预设失败", "Failed to load the preset"));
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/加载", __ex);
			}
			// 协程没启动成功时必须立即清 levelPortalXml 并放锁，否则下一次原生重载会误用这份预设。
			if (!coroutineStarted) {
				try { QuickSaver.levelPortalXml = null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理预设XML", __ex); }
				SetReloadBusy(false);
			}
		}

		// 返回树屋：走游戏原生「结束对局」流程（与替换关卡的第一步相同），房主有效。
		private static void ReturnToTreehouse() {
			try {
				if (!SRApi.GateHost || !NetworkServer.active) {
					SRApi.NotifyT("仅房主可返回树屋：", "Host only: ", SRApi.T("返回树屋", "Back to treehouse"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				GameControl gc = lm != null ? lm.CurrentGameController as GameControl : null;
				if (gc == null) { SRApi.Notify(SRApi.T("当前不在对局中，无需返回树屋", "Not in a match; nothing to return from")); return; }
				SetReloadBusy(true);
				gc.EndGame();
				// EndGame 会切到树屋场景；锁必须由 watchdog 释放，否则后续所有加载都被"重载进行中"拦住。
				try { lm.StartCoroutine(TreehouseReturnWatchdog()); }
				catch (Exception __wx) { SRApi.Guard.Log("布局预设/返回树屋watchdog", __wx); SetReloadBusy(false); }
				SRApi.Notify(SRApi.T("正在返回树屋…", "Returning to the treehouse…"));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/返回树屋", __ex);
				SetReloadBusy(false);
				SRApi.Notify(SRApi.T("返回树屋失败", "Failed to return to the treehouse"));
			}
		}

		// 返回树屋 watchdog：等树屋/大厅就绪或超时后释放重载锁。
		private static IEnumerator TreehouseReturnWatchdog() {
			float deadline = Time.unscaledTime + 25f;
			while (Time.unscaledTime < deadline) {
				try {
					LobbyManager cur = LobbyManager.instance;
					if (cur != null && cur.CurrentLevelSelectController != null
						&& SceneManager.GetActiveScene().name == "TreeHouseLobby") break;
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/返回树屋watchdog", __ex); }
				yield return null;
			}
			SetReloadBusy(false);
		}

		// 预设目录名（GameState.GameMode 的 ToString）-> 游戏模式枚举；IMPORTED/UNKNOWN 等非具体模式返回 false。
		private static bool TryParseMode(string mode, out GameState.GameMode gm) {
			gm = GameState.GameMode.FREEPLAY;
			try {
				if (string.IsNullOrEmpty(mode)) return false;
				if (!Enum.TryParse<GameState.GameMode>(mode.Trim(), true, out gm)) return false;
				return Enum.IsDefined(typeof(GameState.GameMode), gm);
			} catch { return false; }
		}

		// 计算本次加载实际使用的模式：
		//   1) 默认（_loadMode 为空）：跟随预设自身保存的模式（p.Mode），解析失败时保留调用方传入的当前模式；
		//   2) 选择了具体「加载模式」：强制覆盖预设模式，所有加载/重载路径都以它为准。
		private static GameState.GameMode ResolveLoadMode(PresetInfo p, GameState.GameMode fallback) {
			GameState.GameMode mode = fallback;
			try {
				GameState.GameMode presetMode;
				if (p != null && IsConcreteMode(p.Mode) && TryParseMode(p.Mode, out presetMode)) mode = presetMode;
				GameState.GameMode forcedMode;
				if (TryParseMode(_loadMode, out forcedMode)) mode = forcedMode;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/解析加载模式", __ex);
			}
			return mode;
		}

		// 是否还有远程客户端（房主自己不算）：只有单人/无远程玩家时才允许“不经树屋”的直接切关卡，
		// 因为游戏没有在对局内给房客同步 GameState.SelectedLevel 的 RPC/消息（那套 RpcSetNextLevel 在树屋的
		// LevelSelectController 上，对局场景里没有该对象）。
		private static bool HasRemoteClients() {
			try {
				if (!NetworkServer.active) return false;
				if (NetworkServer.connections == null) return true;
				return NetworkServer.connections.Count > 1;
			} catch { return true; }
		}

		// 不经树屋的直接替换（仅单人/无远程客户端）：在当前对局内设置 SelectedLevel / 模式 / 快照信息，
		// 写 QuickSaver.levelPortalXml，再走原生 LobbyManager.ReloadScene（内部 ServerChangeScene 到 SelectedLevel）。
		// 调用链：GameState.SelectedLevel=target → GameSettings.GameMode=targetMode → currentSnapshotInfo →
		//         QuickSaver.levelPortalXml=xml → 广播 PrepareToReloadScene → FadeIn → ReloadScene(targetMode)
		//         → 新场景 OnSetupStartLevel 应用预设。房客端 SelectedLevel 无法更新，所以有远程客户端时不用这条路。
		private static bool StartDirectReplace(PresetInfo p, string xml, GameState.LevelName targetLevel, GameState.GameMode targetMode) {
			try {
				LobbyManager lm = LobbyManager.instance;
				GameState gs = GameState.GetInstance();
				GameSettings st = GameSettings.GetInstance();
				if (lm == null || gs == null || st == null || !NetworkServer.active) return false;
				gs.SelectedLevel = targetLevel;
				LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
				info.nextLevel = targetLevel;
				if (p != null) {
					info.snapshotName = p.LevelName;
					info.snapshotCode = p.LevelCode;
				}
				// 旧索引可能没有名字/代码：从预设 XML 根属性补齐（自定义关卡必须带上，否则关卡显示空白）。
				if (string.IsNullOrEmpty(info.snapshotName) || string.IsNullOrEmpty(info.snapshotCode)) {
					try {
						XmlDocument __xd = ParseXmlSafe(xml);
						XmlElement __root = __xd.DocumentElement;
						if (__root != null) {
							if (string.IsNullOrEmpty(info.snapshotName)) info.snapshotName = __root.GetAttribute("snapshotName");
							if (string.IsNullOrEmpty(info.snapshotCode)) info.snapshotCode = __root.GetAttribute("snapshotCode");
							if (string.IsNullOrEmpty(info.snapshotCode)) info.snapshotCode = __root.GetAttribute("levelCode");
						}
					} catch (Exception __xe) { SRApi.Guard.Log("布局预设/解析快照名", __xe); }
				}
				gs.currentSnapshotInfo = info;
				st.GameMode = targetMode;
				QuickSaver.levelPortalXml = xml;
				MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene {
					reloadToMode = targetMode,
					snapshotInfo = info
				};
				NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
				if (LoadingInterstitialSplash.Instance != null) {
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				lm.StartCoroutine(LoadPresetRoutine(p != null ? p.Name : "", xml, targetMode));
				SRApi.LogInfo("[布局预设] 直接替换关卡（无远程客户端）: " + LevelDisplayNameForScene(SceneManager.GetActiveScene().name)
					+ " -> " + LevelDisplayNameForScene(GameState.GetLevelSceneName(targetLevel)));
				return true;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/直接替换关卡", __ex);
				try { QuickSaver.levelPortalXml = null; } catch { }
				return false;
			}
		}

		// 替换关卡加载协程：先用 GameControl.EndGame() 走游戏原生「回树屋」流程（房主会向房客 RPC 一起回），
		// 等 TreeHouseLobby + LevelSelectController 就绪后，用与树屋加载相同的原生 LaunchLevel(portal) 进入预设关卡，
		// 再覆盖 QuickSaver.levelPortalXml 为预设 XML，让新场景 OnSetupStartLevel 应用方块。
		private static IEnumerator ReplaceLevelRoutine(string name, string xml, string presetScene,
			GameState.LevelName targetLevel, GameState.GameMode targetMode) {
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame();
			bool ok = false;
			if (StartReturnToTreehouse(targetMode)) {
				float deadline = Time.unscaledTime + 20f;
				LevelSelectController lsc = null;
				while (Time.unscaledTime < deadline) {
					LobbyManager cur = LobbyManager.instance;
					lsc = cur != null ? cur.CurrentLevelSelectController : null;
					if (lsc != null && SceneManager.GetActiveScene().name == "TreeHouseLobby") break;
					yield return null;
				}
				if (lsc == null) {
					SRApi.Notify(SRApi.T("替换关卡失败：等待回到树屋超时", "Level replacement failed: timed out waiting to return to the treehouse"));
				} else {
					// 等树屋门/大厅状态初始化（场景刚加载完的一两帧门可能还没激活）。
					yield return new WaitForSeconds(0.8f);
					ok = EnterPresetLevelFromTreehouse(lsc, name, xml, presetScene, targetLevel);
				}
			}
			if (!ok) {
				try { QuickSaver.levelPortalXml = null; } catch (Exception __cx) { SRApi.Guard.Log("布局预设/清理替换关卡XML", __cx); }
				SetReloadBusy(false);
			}
		}

		// 替换关卡第一步（无 yield，避免 C# 不允许在 try/catch 里 yield）：原生 EndGame 回树屋。
		private static bool StartReturnToTreehouse(GameState.GameMode targetMode) {
			try {
				LobbyManager lm = LobbyManager.instance;
				GameControl gc = lm != null ? lm.CurrentGameController as GameControl : null;
				if (lm == null || gc == null || !NetworkServer.active) {
					SRApi.Notify(SRApi.T("无法替换关卡：当前没有可结束的对局", "Cannot replace the level: there is no active match"));
					return false;
				}
				// 目标模式先在本机设好：LaunchLevel 内部会 CallRpcSetGameMode 同步给房客。
				try { GameSettings st = GameSettings.GetInstance(); if (st != null) st.GameMode = targetMode; } catch (Exception __mx) { SRApi.Guard.Log("布局预设/替换关卡设置模式", __mx); }
				// 原生「回树屋」：RPC 全员淡出到 TreeHouseLobby，并清理当前对局玩家。
				gc.EndGame();
				return true;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/结束对局回树屋", __ex);
				SRApi.Notify(SRApi.T("替换关卡失败：无法结束当前对局", "Level replacement failed: could not end the current match"));
				return false;
			}
		}

		// 替换关卡第二步（无 yield）：树屋已就绪，找门并走原生 LaunchLevel 进预设关卡，再注入预设 XML。
		private static bool EnterPresetLevelFromTreehouse(LevelSelectController lsc, string name, string xml,
			string presetScene, GameState.LevelName targetLevel) {
			try {
				LevelPortal portal = FindTreehousePortal(lsc, targetLevel);
				if (portal == null) {
					SRApi.Notify(SRApi.T("替换关卡失败：树屋里找不到「", "Level replacement failed: no treehouse portal for \"")
						+ LevelDisplayNameForScene(presetScene) + SRApi.T("」的入口（可能未解锁）", "\" (possibly locked)"));
					return false;
				}
				if (!AllLobbyPlayersPickedCharacter(LobbyManager.instance)) {
					SRApi.Notify(SRApi.T("已回到树屋：有玩家还没选角色，先选好角色后再点一次「加载」即可进入预设关卡",
						"Returned to the treehouse: some players have not picked a character; pick one and click Load again to enter the preset level"));
					return false;
				}
				try {
					if (LoadingInterstitialSplash.Instance != null) {
						LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
						LoadingInterstitialSplash.Instance.FadeIn();
					}
				} catch (Exception __fx) { SRApi.Guard.Log("布局预设/替换关卡加载画面", __fx); }
				lsc.LaunchLevel(portal);
				try { QuickSaver.levelPortalXml = xml; } catch (Exception __qx) { SRApi.Guard.Log("布局预设/替换关卡写入预设XML", __qx); }
				try {
					GameState gs = GameState.GetInstance();
					if (gs != null) {
						gs.SelectedLevel = targetLevel;
						LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
						info.nextLevel = targetLevel;
						gs.currentSnapshotInfo = info;
					}
				} catch (Exception __sx) { SRApi.Guard.Log("布局预设/替换关卡同步状态", __sx); }
				LobbyManager.instance.StartCoroutine(TreehouseLoadWatchdog(name, xml));
				SRApi.LogInfo("[布局预设] " + SRApi.T("正在替换关卡：进入 ", "Replacing level: entering ") + LevelDisplayNameForScene(presetScene)
					+ SRApi.T(" 并应用预设 ", " and applying preset ") + name);
				return true;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/替换关卡进入", __ex);
				SRApi.Notify(SRApi.T("替换关卡加载失败（详见日志）", "Level replacement failed (see the log)"));
				return false;
			}
		}
		// 树屋加载调用链（复用游戏原生流程，避免自己拼网络消息）：
		//   1) 读预设 XML，取 levelSceneName → 反查 GameState.LevelName → 在树屋关卡门里找到对应 LevelPortal；
		//   2) 先 LoadingInterstitialSplash.FadeIn()：原生 LevelSelectController.FadeToLevel 要等 FadeOut 进入 SHOW，
		//      而房主本机不会收到 ClientRpc RpcSetNextLevel（那里才会 FadeIn），所以这一步必须自己触发；
		//   3) 调游戏原生 LevelSelectController.LaunchLevel(portal)：它会 CallRpcSetNextLevel 让房客同步、
		//      清理/准备大厅玩家，然后 FadeToLevel → LobbyManager.ServerChangeScene(关卡场景)；
		//   4) LaunchLevel 同步把 QuickSaver.levelPortalXml 设为 portal.snapshotXml（普通关卡为 null），
		//      调用返回后立刻用预设 XML 覆盖；FadeToLevel 还要等 FadeOut.SHOW + 0.2 秒 + UnloadUnusedAssets，
		//      所以覆盖一定发生在新场景 OnSetupStartLevel 读取 levelPortalXml 之前；
		//   5) 新场景 OnSetupStartLevel 读取 levelPortalXml → CompressAndSendSnapshotBytes 广播 + LoadXmlSnapshotFromString
		//      本地应用 →「切到预设关卡并应用方块」完成。仅房主可执行；自定义关卡（BlankLevel）无法用 LevelPortal 映射。
		private static void TryLoadPresetInTreehouse(PresetInfo p) {
			bool started = false;
			try {
				LobbyManager lm = LobbyManager.instance;
				if (lm == null) {
					SRApi.Notify(SRApi.T("当前不在树屋或对局中", "Not in a treehouse or match"));
					return;
				}
				LevelSelectController lsc = lm.CurrentLevelSelectController;
				if (lsc == null) {
					SRApi.Notify(SRApi.T("当前不在树屋/大厅中，无法切换到预设关卡", "Not in the treehouse/lobby; cannot switch to the preset level"));
					return;
				}
				if (!SRApi.GateHost || !NetworkServer.active) {
					SRApi.NotifyT("仅房主可在树屋加载预设：", "Host only: ", p.Name);
					return;
				}
				if (ReloadBlocked()) return;
				string xml = ReadPresetXml(p);
				if (string.IsNullOrEmpty(xml)) {
					SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
					return;
				}
				XmlDocument doc = null;
				try {
					doc = ParseXmlSafe(xml);
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/树屋解析预设文件", __ex);
				}
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
					SRApi.Notify(SRApi.T("预设文件已损坏（根节点不是 scene）", "The preset file is damaged (root node is not scene)"));
					return;
				}
				string presetScene = "";
				try { presetScene = doc.DocumentElement.GetAttribute("levelSceneName"); } catch { }
				if (string.IsNullOrEmpty(presetScene)) presetScene = p.Level ?? "";
				GameState.LevelName target;
				if (!TrySceneToLevel(presetScene, out target)) {
					SRApi.Notify(SRApi.T("树屋加载仅支持游戏内置关卡；自定义关卡请先进入该关卡再加载预设", "Treehouse loading only supports built-in levels; enter custom levels first and load there"));
					return;
				}
				if (target == GameState.LevelName.BLANKLEVEL) {
					// 自定义关卡：树屋里没有可用的关卡门，直接替换进 BlankLevel 并带上预设的快照名/代码。
					// 联机时必须勾选「跳过树屋流程」才允许直接替换（否则只提示，避免房客关卡状态不同步）。
					bool skipTH = _skipTreehouse != null && _skipTreehouse.Value;
					if (HasRemoteClients() && !skipTH) {
						SRApi.Notify(SRApi.T("联机替换自定义关卡需要先勾选『跳过树屋流程』", "Replacing a custom level online requires enabling 'Skip treehouse' first"));
						return;
					}
					if (!AllLobbyPlayersPickedCharacter(lm)) {
						SRApi.Notify(SRApi.T("有玩家还没选角色：请等所有人选好角色后再加载（否则可能开局异常）", "Some players have not picked a character yet; wait until everyone has one"));
						return;
					}
					GameState.GameMode __cm = ResolveLoadMode(p, GameSettings.GetInstance().GameMode);
					if (StartDirectReplace(p, xml, GameState.LevelName.BLANKLEVEL, __cm)) {
						SetReloadBusy(true);
						started = true;
						SRApi.LogInfo("[布局预设] 正直接从树屋进入自定义关卡并加载预设：" + p.Name);
					} else {
						SRApi.Notify(SRApi.T("进入自定义关卡失败（详见日志）", "Failed to enter the custom level (see the log)"));
					}
					return;
				}
				LevelPortal portal = FindTreehousePortal(lsc, target);
				if (portal == null) {
					SRApi.Notify(SRApi.T("树屋里找不到「", "No treehouse portal found for \"") + LocalizedLevelName(presetScene)
						+ SRApi.T("」的入口（可能未解锁或不可用）", "\" (possibly locked or unavailable)"));
					return;
				}
				if (!AllLobbyPlayersPickedCharacter(lm)) {
					SRApi.Notify(SRApi.T("有玩家还没选角色：现在加载会把未选角色的玩家移出房间，请等所有人选好角色后再试", "Some players have not picked a character yet; loading now would remove them from the lobby. Wait until everyone has picked a character"));
					return;
				}
				// 树屋普通关卡：LaunchLevel 会用 GameSettings.GameMode 并 CallRpcSetGameMode 同步给房客，
				// 因此进入前先按「默认预设模式 / 强制加载模式」设置，保证该路径同样受加载模式控制。
				try {
					GameSettings __lst = GameSettings.GetInstance();
					if (__lst != null) __lst.GameMode = ResolveLoadMode(p, __lst.GameMode);
				} catch (Exception __mex) { SRApi.Guard.Log("布局预设/树屋加载设置模式", __mex); }
				// 房主本机不会收到 ClientRpc RpcSetNextLevel，需要自己把加载画面推到 SHOW，否则原生 FadeToLevel 会一直等。
				try {
					if (LoadingInterstitialSplash.Instance != null) {
						LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
						LoadingInterstitialSplash.Instance.FadeIn();
					}
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋加载画面", __ex); }
				lsc.LaunchLevel(portal);
				// LaunchLevel 已把 levelPortalXml 写成 portal.snapshotXml（普通关卡为 null），立刻覆盖为预设 XML。
				try { QuickSaver.levelPortalXml = xml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋写入预设XML", __ex); }
				// 补齐房主本机的 SelectedLevel/currentSnapshotInfo（ClientRpc 只在房客端执行）。
				try {
					GameState gs = GameState.GetInstance();
					if (gs != null) {
						gs.SelectedLevel = target;
						LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
						info.nextLevel = target;
						gs.currentSnapshotInfo = info;
					}
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋同步关卡状态", __ex); }
				SetReloadBusy(true);
				lm.StartCoroutine(TreehouseLoadWatchdog(p.Name, xml));
				started = true;
				SRApi.LogInfo("[布局预设] " + SRApi.T("加载预设并进入关卡：", "Loading preset into level: ") + p.Name
					+ SRApi.T(" → ", " -> ") + LocalizedLevelName(presetScene));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/树屋加载", __ex);
				SRApi.Notify(SRApi.T("从树屋加载预设失败（详见日志）", "Failed to load the preset from the treehouse (see the log)"));
			}
			if (!started) {
				try { QuickSaver.levelPortalXml = null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理树屋预设XML", __ex); }
				SetReloadBusy(false);
			}
		}

		// 在树屋里找目标关卡的门：优先 LevelSelectController.portals（场景实际摆放的门），只返回激活状态的门。
		private static LevelPortal FindTreehousePortal(LevelSelectController lsc, GameState.LevelName target) {
			try {
				if (lsc != null && lsc.portals != null) {
					for (int i = 0; i < lsc.portals.Length; i++) {
						LevelPortal p = lsc.portals[i];
						if (p == null || p.TargetLevel != target) continue;
						if (p.gameObject == null || !p.gameObject.activeInHierarchy) continue;
						return p;
					}
				}
				LevelPortal[] all = UnityEngine.Object.FindObjectsOfType<LevelPortal>();
				if (all != null) {
					for (int i = 0; i < all.Length; i++) {
						LevelPortal p = all[i];
						if (p == null || p.TargetLevel != target) continue;
						if (p.gameObject == null || !p.gameObject.activeInHierarchy) continue;
						return p;
					}
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/查找树屋关卡门", __ex);
			}
			return null;
		}

		// LaunchLevel 会移除还没选角色的玩家；先检查，避免用户误操作把人踢出房间。
		private static bool AllLobbyPlayersPickedCharacter(LobbyManager lm) {
			try {
				if (lm == null || lm.lobbySlots == null) return true;
				for (int i = 0; i < lm.lobbySlots.Length; i++) {
					LobbyPlayer lp = lm.lobbySlots[i] as LobbyPlayer;
					if (lp == null) continue;
					if (lp.PickedAnimal == Character.Animals.NONE) return false;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/检查大厅玩家角色", __ex);
			}
			return true;
		}

		// 树屋加载后的保护：等游戏消费 levelPortalXml（新场景 OnSetupStartLevel 后置 null）再放锁；
		// 25 秒未消费则清掉本次写入的 XML，避免污染下一次原生开局。
		private static IEnumerator TreehouseLoadWatchdog(string name, string expectedXml) {
			float startedAt = Time.unscaledTime;
			bool consumed = false;
			while (Time.unscaledTime - startedAt < PresetConsumeTimeout) {
				string remaining = null;
				try { remaining = QuickSaver.levelPortalXml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取树屋预设XML", __ex); }
				if (string.IsNullOrEmpty(remaining) || remaining != expectedXml) {
					consumed = true;
					break;
				}
				yield return null;
			}
			if (!consumed) {
				try {
					if (QuickSaver.levelPortalXml == expectedXml) QuickSaver.levelPortalXml = null;
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理树屋超时预设XML", __ex); }
				SRApi.LogWarn("[布局预设] 树屋加载后预设 XML 未被游戏消费（可能开局失败），已清掉: " + name);
			}
			SetReloadBusy(false);
		}

		private static IEnumerator LoadPresetRoutine(string name, string expectedXml, GameState.GameMode toMode) {
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame(); // 等加载画面 FadeIn 生效（与 Level 的重载协程一致）
			bool ok = false;
			try {
				LobbyManager lm = LobbyManager.instance;
				if (lm != null) {
					// 与原生重载一致：Reset 方块 ID 序列必须紧挨着 ReloadScene，否则重载后新 ID 会与快照里的 parentID/mainID 错位。
					try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重置方块ID序列", __ex); }
					lm.ReloadScene(toMode);
					ok = true;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/重载场景", __ex);
			}
			if (!ok) {
				SRApi.Notify(SRApi.T("加载预设失败：重载场景失败", "Failed to load the preset: scene reload failed"));
				try { QuickSaver.levelPortalXml = null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理预设XML", __ex); }
				SetReloadBusy(false);
				yield break;
			}
			// 游戏在 OnSetupStartLevel 消费 levelPortalXml 后立即置 null：等消费完成再放锁。
			// 25 秒还没被消费（场景没真正重建）就清掉本次写入的 XML，避免污染下一次原生重载。
			float started = Time.unscaledTime;
			bool consumed = false;
			while (Time.unscaledTime - started < PresetConsumeTimeout) {
				string remaining = null;
				try { remaining = QuickSaver.levelPortalXml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取预设XML", __ex); }
				if (string.IsNullOrEmpty(remaining) || remaining != expectedXml) {
					consumed = true;
					break;
				}
				yield return null;
			}
			if (!consumed) {
				try {
					if (QuickSaver.levelPortalXml == expectedXml) QuickSaver.levelPortalXml = null;
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理超时预设XML", __ex); }
				SRApi.LogWarn("[布局预设] 预设 XML 未被游戏消费（可能重载未完成），已清掉，避免影响下一次原生重载: " + name);
				SRApi.Notify(SRApi.T("预设加载可能未生效：等待场景重载超时", "The preset may not have been applied: timed out waiting for the scene reload"));
			}
			SetReloadBusy(false);
		}

		private static bool ReloadBlocked() {
			try {
				// 先查对方：自己准备动手时，关卡重载正在跑就必须拦下（对方忙时自己也要等）。
				try {
					Func<bool> other = Slots.Require<Func<bool>>("level.reloadBusy");
					if (other != null && other()) {
						SRApi.Notify(SRApi.T("关卡正在重载，请稍候", "Level reload in progress; please wait"));
						return true;
					}
				} catch (Exception __ex) { SRApi.Guard.Log("预设/重载互斥检查", __ex); }
				if (!_reloadBusy) return false;
				if (_reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > ReloadLockStale) {
					SetReloadBusy(false);
					SRApi.LogWarn("[布局预设] 检测到残留的忙碌锁（超过 45 秒未复位），已自动放锁");
					return false;
				}
				SRApi.Notify(SRApi.T("重载进行中，请稍候", "Reload in progress"));
				return true;
			} catch { return false; }
		}

		private static void SetReloadBusy(bool busy) {
			_reloadBusy = busy;
			_reloadBusyAt = busy ? Time.unscaledTime : -1f;
		}

		// ---------- 快照获取 ----------

		// 取本机当前看到的布局快照（保存是本地行为，不做房主门控；房客看到的是同步后的方块）。
		private static bool TryGetCurrentSnapshot(out XmlDocument doc, out string mode) {
			doc = null;
			mode = "UNKNOWN";
			if (!SRApi.GateMaster) {
				SRApi.Notify(SRApi.MsgMasterOff);
				return false;
			}
			LobbyManager lm = LobbyManager.instance;
			if (lm == null) {
				SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
				return false;
			}
			GameControl gc = lm.CurrentGameController as GameControl;
			if (gc == null) {
				SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
				return false;
			}
			QuickSaver qs = gc.GetComponent<QuickSaver>();
			if (qs == null) {
				SRApi.Notify(SRApi.T("当前对局不支持保存布局", "Saving layouts is not supported in this match"));
				return false;
			}
			doc = qs.GetCurrentXmlSnapshot(false);
			if (doc == null || doc.DocumentElement == null) {
				SRApi.Notify(SRApi.T("布局快照生成失败", "Failed to generate the layout snapshot"));
				doc = null;
				return false;
			}
			try {
				GameSettings st = GameSettings.GetInstance();
				if (st != null) mode = st.GameMode.ToString();
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取当前模式", __ex);
			}
			return true;
		}

		// ---------- 存储 ----------

		// BepInEx.Paths 是 BepInEx 的公开类型，功能层允许引用（契约边界只禁止引用 SR.* 实现层）。
		private static string PresetsRoot() {
			if (!string.IsNullOrEmpty(_rootDir)) return _rootDir;
			try {
				_rootDir = Path.Combine(Paths.ConfigPath, "SR_UCH", "presets");
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/定位存储目录", __ex);
				// 极端情况下 BepInEx.Paths 不可用：退回临时目录，至少不让功能整个崩掉。
				_rootDir = Path.Combine(Path.GetTempPath(), "SR_UCH_presets");
			}
			return _rootDir;
		}

		private static void EnsureLoaded() {
			if (_loaded) return;
			LoadFromDisk(false);
		}

		// 扫描磁盘重建列表：index.json 只提供展示元数据，XML 文件才是权威数据。
		// 索引损坏 / 被手改坏 / 旧版索引缺字段（level / ok）时按 XML 重新读取元数据，扫完回写自愈。
		private static void LoadFromDisk(bool revalidate) {
			_loaded = true;
			_presets.Clear();
			bool upgraded = false; // 旧索引缺字段 / 出现新文件 / 强制重校验结果变化：扫完回写一次
			try {
				string root = PresetsRoot();
				if (!Directory.Exists(root)) {
					LoadLocalSnapshots();
					SortPresets();
					return;
				}
				Dictionary<string, PresetInfo> byKey = new Dictionary<string, PresetInfo>(StringComparer.OrdinalIgnoreCase);
				Dictionary<string, bool> completeIdx = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
				string index = Path.Combine(root, IndexFileName);
				if (File.Exists(index)) {
					try {
						string[] lines = File.ReadAllLines(index, Encoding.UTF8);
						for (int i = 0; i < lines.Length; i++) {
							string line = lines[i];
							if (line == null || line.IndexOf("\"file\"", StringComparison.Ordinal) < 0) continue;
							string file = JsonStr(line, "file");
							string mode = JsonStr(line, "mode");
							if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(mode)) continue;
							PresetInfo pi = new PresetInfo();
							pi.File = file;
							pi.Mode = mode;
							pi.Name = JsonStr(line, "name");
							if (string.IsNullOrEmpty(pi.Name)) pi.Name = Path.GetFileNameWithoutExtension(file);
							pi.Blocks = JsonInt(line, "blocks");
							pi.TimeUtc = ParseTime(JsonStr(line, "time"));
							pi.Level = JsonStr(line, "level");
							if (pi.Level == null) pi.Level = "";
							pi.LevelName = JsonStr(line, "levelName");
							if (pi.LevelName == null) pi.LevelName = "";
							pi.LevelCode = JsonStr(line, "levelCode");
							if (pi.LevelCode == null) pi.LevelCode = "";
							pi.Ok = JsonBool(line, "ok");
							// 字段缺失判定：老索引（v1-v3）没有 level / levelName / levelCode / ok -> 默认 false/0，扫描时自愈。
							bool complete = line.IndexOf("\"level\":", StringComparison.Ordinal) >= 0
								&& line.IndexOf("\"levelName\":", StringComparison.Ordinal) >= 0
								&& line.IndexOf("\"levelCode\":", StringComparison.Ordinal) >= 0
								&& line.IndexOf("\"ok\":", StringComparison.Ordinal) >= 0;
							completeIdx[mode + "/" + file] = complete;
							if (!complete) upgraded = true;
							byKey[mode + "/" + file] = pi;
						}
					} catch (Exception __ex) {
						SRApi.Guard.Log("布局预设/读索引", __ex);
					}
				}
				string[] dirs = Directory.GetDirectories(root);
				for (int i = 0; i < dirs.Length; i++) {
					string dir = dirs[i];
					string mode = Path.GetFileName(dir);
					string[] files = Directory.GetFiles(dir, "*.xml");
					for (int j = 0; j < files.Length; j++) {
						string fp = files[j];
						string file = Path.GetFileName(fp);
						string key = mode + "/" + file;
						PresetInfo pi;
						bool complete = false;
						if (!byKey.TryGetValue(key, out pi)) {
							pi = new PresetInfo();
							pi.Mode = mode;
							pi.File = file;
							pi.Name = Path.GetFileNameWithoutExtension(file);
							upgraded = true; // 索引里没有这个文件：扫完回写登记
						} else {
							complete = completeIdx.ContainsKey(key) && completeIdx[key];
						}
						pi.Mode = mode;
						pi.File = file;
						pi.FilePath = fp;
						// 索引缺元数据（老索引 / 无索引 / 刷新强制重校验）：流式读一次 XML 根节点与方块数。
						if (revalidate || !complete || string.IsNullOrEmpty(pi.Level) || !pi.Ok || pi.Blocks < 0) {
							bool oldOk = pi.Ok;
							string oldLevel = pi.Level ?? "";
							int oldBlocks = pi.Blocks;
							string lv;
							int blocks;
							string metaName;
							string metaCode;
							string metaPreset = "";
							bool xmlOk = TryReadPresetMeta(fp, out lv, out blocks, out metaName, out metaCode, out metaPreset);
							pi.Level = lv ?? "";
							if (!string.IsNullOrEmpty(metaPreset) && (string.IsNullOrEmpty(pi.Name) || pi.Name == Path.GetFileNameWithoutExtension(pi.File)))
								pi.Name = metaPreset;   // 索引丢失时优先用 XML 里保存的显示名，避免重命名回退成旧文件名
							if (string.IsNullOrEmpty(pi.LevelName) && !string.IsNullOrEmpty(metaName)) {
								pi.LevelName = metaName;
								upgraded = true;
							}
							if (string.IsNullOrEmpty(pi.LevelCode) && !string.IsNullOrEmpty(metaCode)) {
								pi.LevelCode = metaCode;
								upgraded = true;
							}
							pi.Blocks = xmlOk ? blocks : Mathf.Max(0, pi.Blocks);
							pi.Ok = xmlOk && blocks > 0;
							if (!complete) upgraded = true;
							if (revalidate && (oldOk != pi.Ok || !string.Equals(oldLevel, pi.Level, StringComparison.Ordinal) || oldBlocks != pi.Blocks)) upgraded = true;
						}
						if (pi.TimeUtc == DateTime.MinValue) {
							try { pi.TimeUtc = File.GetLastWriteTimeUtc(fp); } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读文件时间", __ex); }
						}
						byKey[key] = pi;
					}
				}
				foreach (KeyValuePair<string, PresetInfo> kv in byKey) {
					PresetInfo pi = kv.Value;
					if (string.IsNullOrEmpty(pi.FilePath)) continue; // 索引里有、磁盘上已删的条目直接丢弃
					try { if (!File.Exists(pi.FilePath)) continue; } catch { continue; }
					_presets.Add(pi);
				}
				LoadLocalSnapshots(); // 游戏本地保存的自定义关卡（只读，不写 index.json）
				SortPresets();
				if (upgraded) SaveIndex(); // 自愈结果立刻落盘（旧索引升级 / 新文件补登记）
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/扫描目录", __ex);
			}
		}

		// ---------- 游戏本地保存的自定义关卡（只读扫描） ----------

		// 游戏快照目录：优先用游戏自己的 QuickSaver.LocalSavesFolder（PC = Application.persistentDataPath + "/snapshots"），
		// 取不到时退回 persistentDataPath 拼接。只读，不写/不删任何文件。
		private static string LocalSnapshotsFolder() {
			try {
				string f = QuickSaver.LocalSavesFolder;
				if (!string.IsNullOrEmpty(f)) return f;
			} catch (Exception __ex) { SRApi.Guard.Log("布局预设/本地快照目录(游戏)", __ex); }
			try { return Path.Combine(Application.persistentDataPath, "snapshots"); } catch { return null; }
		}

		// 本地快照是 SevenZipHelper 压缩的 UTF8 XML（与原版 DoLocalSave 一致）：校验压缩大小/解压大小/字符数/方块数后
		// 用 ParseXmlSafe 解析，返回解压后的 XML 字符串。只读，绝不写回。
		private static string ReadLocalSnapshotXml(string path, out XmlDocument doc, out int blocks) {
			doc = null;
			blocks = 0;
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
				FileInfo fi = new FileInfo(path);
				if (fi.Length <= 0 || fi.Length > MaxSnapshotBytes) {
					SRApi.LogWarn("[布局预设] 本地关卡文件大小异常，已跳过: " + path + " (" + fi.Length + "B)");
					return null;
				}
				byte[] bytes = File.ReadAllBytes(path);
				// 7z/LZMA 头：前 5 字节是编码属性，随后 8 字节是解压后字节数（小端）。先按声明大小拦截解压炸弹。
				if (bytes.Length >= 13) {
					long declared = 0L;
					for (int i = 0; i < 8; i++) declared |= (long)bytes[5 + i] << (8 * i);
					if (declared > MaxLocalXmlBytes) {
						SRApi.LogWarn("[布局预设] 本地关卡解压后过大，已跳过: " + path + " (" + declared + "B)");
						return null;
					}
				}
				byte[] raw = SevenZip.Compression.LZMA.SevenZipHelper.Decompress(bytes);
				if (raw == null || raw.Length == 0 || raw.Length > MaxLocalXmlBytes) return null;
				string xml = Encoding.UTF8.GetString(raw);
				if (string.IsNullOrEmpty(xml) || xml.Length > MaxImportChars) return null;
				doc = ParseXmlSafe(xml);
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") return null;
				blocks = CountBlocks(doc);
				if (blocks > MaxImportBlocks) {
					SRApi.LogWarn("[布局预设] 本地关卡方块数超限，已跳过: " + path + " (" + blocks + ")");
					return null;
				}
				return xml;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取本地关卡快照", __ex);
				return null;
			}
		}

		// 本地关卡展示名：文件名去掉 .snapshot 与 .c/.v 后缀（游戏自己的 GetSnapshotNameWithoutSuffix 同一规则），
		// 空名回退文件名。
		private static string LocalLevelName(string fileName) {
			try {
				string n = fileName ?? "";
				if (n.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 9);
				if (n.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".v", StringComparison.OrdinalIgnoreCase))
					n = n.Substring(0, n.Length - 2);
				return string.IsNullOrEmpty(n) ? (fileName ?? "") : n;
			} catch { return fileName ?? ""; }
		}

		// 本地关卡模式：快照 XML 本身不存 GameMode；只能从文件名后缀推断关卡类型（.c=挑战 / .v=对战），
		// 其它情况留空（列表显示 -），避免瞎猜。
		private static string LocalModeFromFilename(string fileName) {
			try {
				string n = fileName ?? "";
				if (n.EndsWith(".c.snapshot", StringComparison.OrdinalIgnoreCase)) return GameState.GameMode.CHALLENGE.ToString();
				if (n.EndsWith(".v.snapshot", StringComparison.OrdinalIgnoreCase)) return GameState.GameMode.PARTY.ToString();
			} catch { }
			return "";
		}

		// 扫描 snapshots 目录，把本地保存的自定义关卡追加进 _presets（IsLocal=true，key="local:"+文件名）。
		private static void LoadLocalSnapshots() {
			try {
				string folder = LocalSnapshotsFolder();
				if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
				string[] files = Directory.GetFiles(folder, "*.snapshot");
				for (int i = 0; i < files.Length; i++) {
					string fp = files[i];
					string fn = Path.GetFileName(fp);
					try {
						XmlDocument doc;
						int blocks;
						string xml = ReadLocalSnapshotXml(fp, out doc, out blocks);
						if (string.IsNullOrEmpty(xml) || doc == null || doc.DocumentElement == null) continue;
						PresetInfo li = new PresetInfo();
						li.IsLocal = true;
						li.LocalRelative = fn;
						li.File = fn;
						li.FilePath = fp;
						li.Name = LocalLevelName(fn);
						// 快照里若带 snapshotName/levelName（部分导入/下载的 .snapshot），优先用它作为名称；
						// 原版本地保存的 XML 没有该属性，按文件名回退。
						string snapName = doc.DocumentElement.GetAttribute("snapshotName");
						if (string.IsNullOrEmpty(snapName)) snapName = doc.DocumentElement.GetAttribute("levelName");
						if (!string.IsNullOrEmpty(snapName)) li.Name = snapName;
						li.Mode = "";   // 本地快照不存 GameMode：留空表示跟随当前/所选的加载模式，避免按后缀误判成派对
						li.Blocks = blocks;
						li.Ok = blocks > 0;
						li.TimeUtc = File.GetLastWriteTimeUtc(fp);
						li.Level = doc.DocumentElement.GetAttribute("levelSceneName") ?? "";
						// 自定义关卡（BlankLevel）用文件名作为实际关卡名；内置关卡让 LevelText 走本地化场景名。
						if (string.Equals(li.Level, "BlankLevel", StringComparison.OrdinalIgnoreCase)) li.LevelName = li.Name;
						string code = doc.DocumentElement.GetAttribute("snapshotCode");
						if (string.IsNullOrEmpty(code)) code = doc.DocumentElement.GetAttribute("levelCode");
						li.LevelCode = code ?? "";
						_presets.Add(li);
					} catch (Exception __ex) {
						SRApi.Guard.Log("布局预设/扫描本地关卡单项", __ex);
					}
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/扫描本地关卡", __ex);
			}
		}

		// 统一读取入口：预设读 .xml 文本；本地关卡解压 .snapshot（仍走同一套大小/方块上限）。
		private static string ReadPresetXml(PresetInfo p) {
			try {
				if (p == null) return null;
				if (!p.IsLocal) return ReadAllText(p.FilePath);
				XmlDocument doc;
				int blocks;
				return ReadLocalSnapshotXml(p.FilePath, out doc, out blocks);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取预设XML", __ex);
				return null;
			}
		}

		private static int CountPresetEntries() {
			try {
				int n = 0;
				for (int i = 0; i < _presets.Count; i++) {
					if (_presets[i] != null && !_presets[i].IsLocal) n++;
				}
				return n;
			} catch { return 0; }
		}

		private static void SaveIndex() {
			try {
				string root = PresetsRoot();
				Directory.CreateDirectory(root);
				StringBuilder sb = new StringBuilder();
				sb.Append("{\n  \"version\": ").Append(IndexVersion).Append(",\n  \"entries\": [\n");
				bool first = true;
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					if (p == null || p.IsLocal) continue; // 本地关卡是游戏存档，不写进预设索引
					if (!first) sb.Append(",\n");
					first = false;
					sb.Append("    {\"file\":\"").Append(JsonEsc(p.File))
						.Append("\",\"name\":\"").Append(JsonEsc(p.Name))
						.Append("\",\"mode\":\"").Append(JsonEsc(p.Mode))
						.Append("\",\"level\":\"").Append(JsonEsc(p.Level))
						.Append("\",\"levelName\":\"").Append(JsonEsc(p.LevelName))
						.Append("\",\"levelCode\":\"").Append(JsonEsc(p.LevelCode))
						.Append("\",\"blocks\":").Append(Mathf.Max(0, p.Blocks))
						.Append(",\"time\":\"").Append(JsonEsc(TimeIso(p.TimeUtc)))
						.Append("\",\"ok\":").Append(p.Ok ? "true" : "false").Append("}");
				}
				if (!first) sb.Append('\n');
				sb.Append("  ]\n}\n");
				File.WriteAllText(Path.Combine(root, IndexFileName), sb.ToString(), new UTF8Encoding(false));
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/写索引", __ex);
			}
		}

		private static PresetInfo FindPreset(string key) {
			try {
				if (string.IsNullOrEmpty(key)) return null;
				for (int i = 0; i < _presets.Count; i++) {
					if (string.Equals(_presets[i].Key, key, StringComparison.OrdinalIgnoreCase)) return _presets[i];
				}
			} catch { }
			return null;
		}

		private static void SortPresets() {
			try {
				_presets.Sort(ComparePreset);
			} catch { }
		}

		// 文件名安全化：去掉 Windows 不允许的字符，截断长度并处理空名。
		private static string SafeFileName(string name) {
			try {
				string src = (name ?? "").Trim();
				char[] bad = Path.GetInvalidFileNameChars();
				StringBuilder sb = new StringBuilder(src.Length);
				for (int i = 0; i < src.Length; i++) sb.Append(Array.IndexOf(bad, src[i]) < 0 ? src[i] : '_');
				string s = sb.ToString().Trim().TrimEnd('.');
				if (s.Length > 60) s = s.Substring(0, 60).Trim();
				if (s.Length == 0) s = "preset_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				return s;
			} catch {
				return "preset_" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
			}
		}

		private static string SafeDirName(string mode) {
			try {
				if (string.IsNullOrEmpty(mode)) return "UNKNOWN";
				char[] bad = Path.GetInvalidFileNameChars();
				StringBuilder sb = new StringBuilder(mode.Length);
				for (int i = 0; i < mode.Length; i++) sb.Append(Array.IndexOf(bad, mode[i]) < 0 ? mode[i] : '_');
				string s = sb.ToString().Trim();
				return s.Length == 0 ? "UNKNOWN" : s;
			} catch { return "UNKNOWN"; }
		}

		// 读取预设 XML：加载路径与导入路径用同一套上限（超大/畸形文件直接拒绝，避免主线程长时间卡顿或 OOM）。
		private static string ReadAllText(string path) {
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
				long len = new FileInfo(path).Length;
				if (len > MaxImportChars) {
					SRApi.Notify(SRApi.T("预设文件过大，已拒绝加载（上限约 2MB）", "Preset file is too large and was not loaded (limit about 2MB)"));
					SRApi.LogWarn("[布局预设] 预设文件过大，已拒绝加载: " + path + " (" + len + " bytes)");
					return null;
				}
				string xml = File.ReadAllText(path, Encoding.UTF8);
				if (!string.IsNullOrEmpty(xml) && xml.Length > MaxImportChars) {
					SRApi.Notify(SRApi.T("预设文件过大，已拒绝加载（上限约 2MB）", "Preset file is too large and was not loaded (limit about 2MB)"));
					return null;
				}
				// 方块数上限：与导入路径的 MaxImportBlocks 保持一致（超限的畸形文件不进入重建流程）。
				int blocks = 0;
				int at = 0;
				while (true) {
					at = xml.IndexOf("<block", at, StringComparison.Ordinal);
					if (at < 0) break;
					int after = at + 6;
					// 只认 <block ... / <block> / <block/>：避免把 <blocked> 之类的节点误计成方块。
					if (after < xml.Length) {
						char ch = xml[after];
						if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '>' || ch == '/') blocks++;
					}
					if (blocks > MaxImportBlocks) {
						SRApi.Notify(SRApi.T("预设方块数量超限，已拒绝加载", "The preset has too many blocks and was not loaded"));
						SRApi.LogWarn("[布局预设] 预设方块数超限，已拒绝加载: " + path);
						return null;
					}
					at = after;
				}
				return xml;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取预设文件", __ex);
				return null;
			}
		}

		// ---------- XML / 时间 / JSON 辅助 ----------

		private static int CountBlocks(XmlDocument doc) {
			try {
				if (doc == null || doc.DocumentElement == null) return 0;
				int n = 0;
				XmlNodeList list = doc.DocumentElement.ChildNodes;
				for (int i = 0; i < list.Count; i++) {
					XmlNode node = list[i];
					if (node != null && node.NodeType == XmlNodeType.Element && node.Name == "block") n++;
				}
				return n;
			} catch { return 0; }
		}

		// 流式读取预设 XML 的元数据：根节点必须是 scene，取 levelSceneName 并统计顶层 block 数量；
		// 如果 XML 里带 snapshotInfo/snapshotName（自定义关卡快照）也顺便取出展示名。
		// 用 XmlReader 而不是 XmlDocument：扫描列表时只关心根属性与方块数，避免把整份 XML 建成 DOM。
		// 统一的安全 XML 解析入口：禁 DTD（即禁外部实体 / XXE）+ 不解析外部引用。
		// 预设 XML 是玩家之间分享的不可信输入（剪贴板 / 论坛下载），所有解析必须走这里。
		private static XmlDocument ParseXmlSafe(string xml) {
			XmlDocument doc = new XmlDocument();
			XmlReaderSettings s = new XmlReaderSettings();
			s.DtdProcessing = DtdProcessing.Prohibit;
			s.XmlResolver = null;
			using (XmlReader r = XmlReader.Create(new StringReader(xml ?? ""), s)) doc.Load(r);
			return doc;
		}

		private static bool TryReadPresetMeta(string path, out string level, out int blocks, out string levelName, out string levelCode, out string presetName) {
			presetName = "";
			level = "";
			blocks = 0;
			levelName = "";
			levelCode = "";
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
				XmlReaderSettings settings = new XmlReaderSettings();
				settings.IgnoreComments = true;
				settings.IgnoreWhitespace = true;
				settings.DtdProcessing = DtdProcessing.Prohibit;
				settings.XmlResolver = null;
				using (XmlReader reader = XmlReader.Create(path, settings)) {
					if (!reader.Read()) return false;
					// 跳过 XML 声明 / 注释，定位根元素。
					while (reader.NodeType != XmlNodeType.Element) {
						if (!reader.Read()) return false;
					}
					if (reader.Name != "scene") return false;
					string lv = reader.GetAttribute("levelSceneName");
					level = lv ?? "";
					string nm = reader.GetAttribute("snapshotName");
					if (string.IsNullOrEmpty(nm)) nm = reader.GetAttribute("levelName");
					if (!string.IsNullOrEmpty(nm)) levelName = nm;
					string pn = reader.GetAttribute("srName");
					if (!string.IsNullOrEmpty(pn)) presetName = pn;
					string cd = reader.GetAttribute("snapshotCode");
					if (string.IsNullOrEmpty(cd)) cd = reader.GetAttribute("levelCode");
					if (!string.IsNullOrEmpty(cd)) levelCode = cd;
					if (reader.IsEmptyElement) return false;
					int rootDepth = reader.Depth;
					while (reader.Read()) {
						if (reader.NodeType == XmlNodeType.Element && reader.Depth == rootDepth + 1) {
							if (reader.Name == "block") {
								blocks++;
							} else if (reader.Name == "snapshotInfo") {
								if (string.IsNullOrEmpty(levelName)) {
									string sn = reader.GetAttribute("snapshotName");
									if (!string.IsNullOrEmpty(sn)) levelName = sn;
								}
								if (string.IsNullOrEmpty(levelCode)) {
									string c = reader.GetAttribute("code");
									if (string.IsNullOrEmpty(c)) c = reader.GetAttribute("snapshotCode");
									if (!string.IsNullOrEmpty(c)) levelCode = c;
								}
							}
						} else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rootDepth) {
							break;
						}
					}
					if (string.IsNullOrEmpty(levelCode)) levelCode = LevelCodeForScene(level);
					return true;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取预设元数据", __ex);
				return false;
			}
		}

		// 剥掉 <destroyed> 节点：重载后的新场景会重建关卡自带件，这些路径没有意义，
		// 而且游戏解析时遇到它们会中断整批加载（与广播快照的处理保持一致）。
		private static void StripDestroyed(XmlDocument doc) {
			try {
				if (doc == null || doc.DocumentElement == null) return;
				List<XmlNode> bad = new List<XmlNode>();
				foreach (XmlNode node in doc.DocumentElement.ChildNodes) {
					if (node != null && node.NodeType == XmlNodeType.Element && node.Name == "destroyed") bad.Add(node);
				}
				for (int i = 0; i < bad.Count; i++) doc.DocumentElement.RemoveChild(bad[i]);
				if (bad.Count > 0) SRApi.LogInfo("[布局预设] 已剥离 " + bad.Count + " 个 <destroyed> 节点");
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/剥离 destroyed", __ex);
			}
		}

		private static string TimeText(PresetInfo p) {
			try {
				if (p == null || p.TimeUtc == DateTime.MinValue) return "-";
				return p.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
			} catch { return "-"; }
		}

		private static string TimeIso(DateTime utc) {
			try {
				if (utc == DateTime.MinValue) utc = DateTime.UtcNow;
				return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
			} catch { return ""; }
		}

		private static DateTime ParseTime(string s) {
			try {
				DateTime dt;
				if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
					DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return dt;
			} catch { }
			return DateTime.MinValue;
		}

		// 模式名：优先用游戏自己的本地化术语（RuleBook/*ModeText，跟随游戏语言设置），
		// 取不到再回退到界面文案；不再自造「创造」这类与游戏不一致的译名。
		private static string ModeText(string mode) {
			try {
				if (string.IsNullOrEmpty(mode)) return "-";
				GameState.GameMode gm = GameState.GameMode.FREEPLAY;
				bool parsed = false;
				try { parsed = Enum.TryParse<GameState.GameMode>(mode.Trim(), true, out gm) && Enum.IsDefined(typeof(GameState.GameMode), gm); }
				catch { parsed = false; }
				if (parsed) {
					string term = ModeLocalizationTerm(gm);
					if (!string.IsNullOrEmpty(term)) {
						string localized = null;
						try { localized = I2.Loc.LocalizationManager.GetTranslation(term); } catch (Exception __lx) { SRApi.Guard.Log("布局预设/模式本地化", __lx); }
						if (!string.IsNullOrEmpty(localized) && localized.IndexOf('/') < 0) return localized;
					}
					switch (gm) {
						case GameState.GameMode.PARTY: return SRApi.T("派对", "Party");
						case GameState.GameMode.CREATIVE: return SRApi.T("创意", "Creative");
						case GameState.GameMode.FREEPLAY: return SRApi.T("自由", "Freeplay");
						case GameState.GameMode.CHALLENGE: return SRApi.T("挑战", "Challenge");
					}
				}
				return mode;
			} catch { return mode; }
		}

		// 游戏本地化术语表里的模式名（与规则书 / 树屋界面一致）。
		private static string ModeLocalizationTerm(GameState.GameMode mode) {
			try {
				switch (mode) {
					case GameState.GameMode.FREEPLAY: return "RuleBook/FreePlayText";
					case GameState.GameMode.CREATIVE: return "RuleBook/CreativeModeText";
					case GameState.GameMode.PARTY: return "RuleBook/PartyModeText";
					case GameState.GameMode.CHALLENGE: return "RuleBook/ChallengeModeText";
					default: return null;
				}
			} catch { return null; }
		}

		// 列表里的关卡显示：优先用保存时记下的展示名（自定义关卡名），否则按场景名解析成游戏本地化名；
		// 都取不到时给非空占位，避免列表出现空白单元格。
		private static string LevelText(PresetInfo p) {
			try {
				if (p == null) return SRApi.T("未知关卡", "Unknown level");
				if (!string.IsNullOrEmpty(p.LevelName)) return p.LevelName;
				return LevelDisplayNameForScene(p.Level);
			} catch { return SRApi.T("未知关卡", "Unknown level"); }
		}

		// 场景名 -> 展示名：自定义关卡（BlankLevel）用「自定义关卡」占位（保存时会记下具体关卡名），
		// 普通关卡用游戏本地化名；空场景也给非空占位，避免列表空白。
		private static string LevelDisplayNameForScene(string scene) {
			try {
				if (string.IsNullOrEmpty(scene)) return SRApi.T("未知关卡", "Unknown level");
				if (string.Equals(scene, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return SRApi.T("自定义关卡", "Custom level");
				return LocalizedLevelName(scene);
			} catch { return string.IsNullOrEmpty(scene) ? SRApi.T("未知关卡", "Unknown level") : scene; }
		}

		// 当前对局如果是自定义关卡，返回它的名字（GameState.currentSnapshotInfo.snapshotName）。
		private static string CurrentCustomLevelName() {
			try {
				GameState gs = GameState.GetInstance();
				if (gs == null) return null;
				LevelSelectController.PlayedSnapshotInfo info = gs.currentSnapshotInfo;
				string nm = info.snapshotName;
				return string.IsNullOrEmpty(nm) ? null : nm;
			} catch { return null; }
		}

		// 保存时记下的展示用关卡名：自定义关卡用当前关卡名（XML 里只有 BlankLevel，无法还原），普通关卡用本地化名。
		private static string CurrentLevelDisplayName(string scene) {
			try {
				string active = "";
				try { active = SceneManager.GetActiveScene().name; } catch { }
				if (string.Equals(active, "BlankLevel", StringComparison.OrdinalIgnoreCase)) {
					string custom = CurrentCustomLevelName();
					if (!string.IsNullOrEmpty(custom)) return custom;
					return SRApi.T("自定义关卡", "Custom level");
				}
				return LevelDisplayNameForScene(string.IsNullOrEmpty(active) ? scene : active);
			} catch { return LevelDisplayNameForScene(scene); }
		}

		// 关卡代码显示：优先保存时记下的快照码（自定义关卡），内置关卡回退场景名；都没有时显示「-」。
		private static string LevelCodeText(PresetInfo p) {
			try {
				if (p == null) return "-";
				if (!string.IsNullOrEmpty(p.LevelCode)) return p.LevelCode;
				if (!string.IsNullOrEmpty(p.Level)
					&& !string.Equals(p.Level, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return p.Level;
			} catch { }
			return "-";
		}

		// 场景名对应的关卡代码：内置关卡就是场景名；自定义关卡（BlankLevel）没有存储时返回空（显示「-」）。
		private static string LevelCodeForScene(string scene) {
			try {
				if (string.IsNullOrEmpty(scene)) return "";
				if (string.Equals(scene, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return "";
				return scene;
			} catch { return ""; }
		}

		// 保存时记下的关卡代码：自定义关卡取快照码（GameState.currentSnapshotInfo.snapshotCode），内置关卡取场景名。
		private static string CurrentLevelCode(string scene) {
			try {
				string active = "";
				try { active = SceneManager.GetActiveScene().name; } catch { }
				if (string.Equals(active, "BlankLevel", StringComparison.OrdinalIgnoreCase)) {
					GameState gs = GameState.GetInstance();
					if (gs != null) {
						string code = gs.currentSnapshotInfo.snapshotCode;
						if (!string.IsNullOrEmpty(code)) return code;
					}
					return LevelCodeForScene(scene);
				}
				return LevelCodeForScene(string.IsNullOrEmpty(active) ? scene : active);
			} catch { return LevelCodeForScene(scene); }
		}

		// 场景名 -> 游戏关卡枚举：遍历 GameState.GetLevelSceneName 反查（覆盖 Farm/Rooftops/.../PrototypeN）。
		// 当前对局是否就是该预设的自定义关卡（比较快照名/代码；任一为空时视为不同）。
		private static bool SameCustomLevel(PresetInfo p, GameState gs) {
			try {
				if (p == null || gs == null) return false;
				LevelSelectController.PlayedSnapshotInfo cur = gs.currentSnapshotInfo;
				// PlayedSnapshotInfo 是结构体，无需判空
				string pn = p.LevelName ?? "";
				string pc = p.LevelCode ?? "";
				string cn = cur.snapshotName ?? "";
				string cc = cur.snapshotCode ?? "";
				bool nameSame = !string.IsNullOrEmpty(pn) && !string.IsNullOrEmpty(cn) && string.Equals(pn, cn, StringComparison.OrdinalIgnoreCase);
				bool codeSame = !string.IsNullOrEmpty(pc) && !string.IsNullOrEmpty(cc) && string.Equals(pc, cc, StringComparison.OrdinalIgnoreCase);
				return nameSame || codeSame;
			} catch { return false; }
		}

		private static bool TrySceneToLevel(string scene, out GameState.LevelName level) {
			level = GameState.LevelName.FARM;
			try {
				if (string.IsNullOrEmpty(scene)) return false;
				Array all = Enum.GetValues(typeof(GameState.LevelName));
				for (int i = 0; i < all.Length; i++) {
					GameState.LevelName lv = (GameState.LevelName)all.GetValue(i);
					if (string.Equals(GameState.GetLevelSceneName(lv), scene, StringComparison.OrdinalIgnoreCase)) {
						level = lv;
						return true;
					}
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/场景名映射关卡", __ex);
			}
			return false;
		}

		// 游戏本地化关卡名（跟随游戏语言设置）；读不到/枚举不识别时回退传入的场景名。
		private static string LocalizedLevelName(string scene) {
			try {
				if (string.IsNullOrEmpty(scene)) return "";
				GameState.LevelName lv;
				if (TrySceneToLevel(scene, out lv)) {
					string name = GameState.GetLevelLocalizedName(lv);
					if (!string.IsNullOrEmpty(name)
						&& !string.Equals(name, "default Level name error", StringComparison.Ordinal)
						&& name.IndexOf('/') < 0) return name;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/关卡本地化名称", __ex);
			}
			return scene;
		}

		private static string LevelFromDoc(XmlDocument doc) {
			try {
				if (doc == null || doc.DocumentElement == null) return "";
				string lv = doc.DocumentElement.GetAttribute("levelSceneName");
				return lv ?? "";
			} catch { return ""; }
		}

		// 从预设 XML 里取关卡代码（快照码）：根属性 snapshotCode/levelCode，或 <snapshotInfo code=...>；
		// 都没有时内置关卡回退场景名、自定义关卡返回空（列表显示 -）。
		private static string LevelCodeFromDoc(XmlDocument doc) {
			try {
				if (doc == null || doc.DocumentElement == null) return "";
				string c = doc.DocumentElement.GetAttribute("snapshotCode");
				if (string.IsNullOrEmpty(c)) c = doc.DocumentElement.GetAttribute("levelCode");
				if (!string.IsNullOrEmpty(c)) return c;
				foreach (XmlNode node in doc.DocumentElement.ChildNodes) {
					if (node == null || node.NodeType != XmlNodeType.Element || node.Name != "snapshotInfo") continue;
					c = node.Attributes != null ? node.Attributes["code"] != null ? node.Attributes["code"].Value : null : null;
					if (string.IsNullOrEmpty(c) && node.Attributes != null && node.Attributes["snapshotCode"] != null) c = node.Attributes["snapshotCode"].Value;
					if (!string.IsNullOrEmpty(c)) return c;
				}
				return LevelCodeForScene(LevelFromDoc(doc));
			} catch { return ""; }
		}

		// 只有真实对局模式才参与"模式不匹配"校验：REPLAY / IMPORTED / UNKNOWN 等目录名无法比较。
		private static bool IsConcreteMode(string mode) {
			try {
				if (string.IsNullOrEmpty(mode)) return false;
				string m = mode.Trim();
				if (string.Equals(m, "REPLAY", StringComparison.OrdinalIgnoreCase)) return false;
				if (string.Equals(m, "IMPORTED", StringComparison.OrdinalIgnoreCase)) return false;
				if (string.Equals(m, "UNKNOWN", StringComparison.OrdinalIgnoreCase)) return false;
				return true;
			} catch { return false; }
		}

		// index.json 由本功能按固定格式每行一条写出；这里按行做轻量解析。
		// 解析不到（用户手改坏）时该条元数据退回文件扫描结果，不会丢预设。
		private static string JsonStr(string line, string key) {
			try {
				string pat = "\"" + key + "\":\"";
				int i = line.IndexOf(pat, StringComparison.Ordinal);
				if (i < 0) return null;
				int p = i + pat.Length;
				StringBuilder sb = new StringBuilder();
				while (p < line.Length) {
					char c = line[p++];
					if (c == '"') return sb.ToString();
					if (c != '\\') { sb.Append(c); continue; }
					if (p >= line.Length) break;
					char e = line[p++];
					switch (e) {
						case '"': sb.Append('"'); break;
						case '\\': sb.Append('\\'); break;
						case '/': sb.Append('/'); break;
						case 'n': sb.Append('\n'); break;
						case 'r': sb.Append('\r'); break;
						case 't': sb.Append('\t'); break;
						case 'u':
							if (p + 4 <= line.Length) {
								int cp;
								if (int.TryParse(line.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp)) sb.Append((char)cp);
								p += 4;
							}
							break;
						default: sb.Append(e); break;
					}
				}
				return sb.ToString();
			} catch { return null; }
		}

		private static int JsonInt(string line, string key) {
			try {
				string pat = "\"" + key + "\":";
				int i = line.IndexOf(pat, StringComparison.Ordinal);
				if (i < 0) return -1;
				int p = i + pat.Length;
				int sign = 1;
				if (p < line.Length && line[p] == '-') { sign = -1; p++; }
				int v = 0;
				bool any = false;
				while (p < line.Length && line[p] >= '0' && line[p] <= '9') {
					v = v * 10 + (line[p] - '0');
					any = true;
					p++;
				}
				return any ? sign * v : -1;
			} catch { return -1; }
		}

		// 读布尔字段（index.json 的 ok）：缺字段或格式不同一律按 false（向后兼容 v1 索引）。
		private static bool JsonBool(string line, string key) {
			try {
				string pat = "\"" + key + "\":";
				int i = line.IndexOf(pat, StringComparison.Ordinal);
				if (i < 0) return false;
				int p = i + pat.Length;
				while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
				return string.Compare(line, p, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
			} catch { return false; }
		}

		private static string JsonEsc(string s) {
			try {
				if (string.IsNullOrEmpty(s)) return "";
				StringBuilder sb = new StringBuilder(s.Length + 8);
				for (int i = 0; i < s.Length; i++) {
					char c = s[i];
					switch (c) {
						case '"': sb.Append("\\\""); break;
						case '\\': sb.Append("\\\\"); break;
						case '\n': sb.Append("\\n"); break;
						case '\r': sb.Append("\\r"); break;
						case '\t': sb.Append("\\t"); break;
						default:
							if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
							else sb.Append(c);
							break;
					}
				}
				return sb.ToString();
			} catch { return ""; }
		}
	}
}
