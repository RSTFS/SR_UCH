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
		// 索引格式版本：v2 起新增 level / ok 字段（v1 老索引缺字段按默认值处理并在扫描时自愈）。
		private const int IndexVersion = 2;
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
		// 列宽（设计单位）默认值与允许范围；顺序与 Col* 一致（操作列也持久化宽度）。
		private static readonly float[] DefColW = new float[] { 150f, 84f, 64f, 96f, 168f };
		private const float ColWidthMin = 40f;
		private const float ColWidthMax = 600f;
		private const string ColWidthsKey = "Preset Widths";
		private const string ColOrderKey = "Preset Order";
		// 操作列按钮种类：0=加载 1=重命名 2=导出 3=删除。
		private const int ActLoad = 0;
		private const int ActRename = 1;
		private const int ActExport = 2;
		private const int ActDelete = 3;
		// 从剪贴板导入的上限：方块数 / XML 字符数。超出直接拒绝，避免一次性重建拖垮帧率或整段解析爆内存。
		private const int MaxImportBlocks = 3000;
		private const int MaxImportChars = 2000000;
		// 二次确认窗口（秒）：超时后按钮自动恢复第一次点击前的文案。
		private const float ConfirmWindow = 5f;
		// 重载锁自愈阈值（秒）：与功能 Level 的 45 秒保持一致。
		private const float ReloadLockStale = 45f;
		// 等游戏消费 levelPortalXml 的硬上限（秒）：与功能 Level 的 25 秒兜底一致。
		private const float PresetConsumeTimeout = 25f;

		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<int> _maxPresets;
		private static ConfigEntry<int> _sortModeEntry;
		private static ConfigEntry<string> _colWidthsEntry;
		private static ConfigEntry<string> _colOrderEntry;
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

		// 标准表格（核心 TableDraw）状态：列宽 / 列序 / 可见列 / 命中矩形缓存 / 滚动 / 拖拽。
		private static readonly float[] _colW = new float[ColCount];
		private static int[] _order = new int[] { 0, 1, 2, 3, 4 };
		private static bool _colsLoaded;
		private static readonly List<int> _vis = new List<int>(ColCount);
		private static readonly List<Rect> _headerCells = new List<Rect>();
		private static readonly List<int> _headerCols = new List<int>();
		private static readonly List<Rect> _rowRects = new List<Rect>();
		private static readonly List<int> _rowRows = new List<int>();
		private static readonly List<Rect> _pinRects = new List<Rect>();
		private static readonly List<int> _pinRows = new List<int>();
		// 操作列按钮几何（每行 4 个）：与 _pinRects 同一行序，按行依次追加。
		private static readonly List<Rect> _actRects = new List<Rect>();
		private static readonly List<int> _actRows = new List<int>();
		private static readonly List<int> _actKinds = new List<int>();
		private static readonly string[] _cells = new string[ColCount];
		private static SRApi.TableArgs _args;
		private static Vector2 _scrollRows;
		private static Vector2 _scrollCols;
		private static float _maxScrollX;
		private static float _maxScrollY;
		private static bool _sbDragging;
		private static bool _dragH;
		private static float _dragWatchdog;

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
			public int Blocks = -1;  // -1 = 索引里没有，需要从 XML 现算
			public DateTime TimeUtc = DateTime.MinValue;
			public bool Ok;          // XML 可解析且方块数 > 0（存 index.json；缺字段按 false，扫描时自愈）

			public string Key { get { return Mode + "/" + File; } }
		}

		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				_enabled = plugin.Config.Bind(Section, "Enabled", true,
					"布局预设总开关：开启后可保存 / 加载 / 导入导出方块布局预设。");
				_featureOn = _enabled.Value;
				_enabled.SettingChanged += delegate { _featureOn = _enabled.Value; };
				_maxPresets = plugin.Config.Bind(Section, "Max Presets", 50,
					new ConfigDescription("预设数量上限（1-500）：列表最多保留多少条，达到上限后必须先删除旧预设才能保存新布局。",
						new AcceptableValueRange<int>(1, 500)));
				_sortModeEntry = plugin.Config.Bind(Section, "Sort Mode", 0,
					new ConfigDescription("列表排序方式：0=时间（新→旧），1=名称（A→Z）。",
						new AcceptableValueRange<int>(SortModeMin, SortModeMax)));
				_sortMode = _sortModeEntry.Value;
				_sortModeEntry.SettingChanged += delegate { _sortMode = _sortModeEntry.Value; };
				// 旧配置可能残留已弃用的 2/3：夹回 0（时间）。
				try { if (_sortModeEntry.Value < SortModeMin || _sortModeEntry.Value > SortModeMax) _sortModeEntry.Value = SortModeMin; }
				catch (Exception __cx) { SRApi.Guard.Log("布局预设/裁剪排序值", __cx); }
				// 列表列宽 / 列序（内部状态，由列表表头拖动维护；不在页面上渲染编辑框）。
				_colWidthsEntry = plugin.Config.Bind(Section, ColWidthsKey, "",
					"内部状态：预设列表列宽（逗号分隔的设计单位，由表头拖动自动写入）。");
				_colOrderEntry = plugin.Config.Bind(Section, ColOrderKey, "",
					"内部状态：预设列表列顺序（逗号分隔的列索引，由表头拖动自动写入）。");
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设 初始化", __ex);
			}
		}

		// 栏目注册：两页（保存与导入 / 预设列表），与实验栏的多页写法一致。
		private static void SelfReg() {
			SRApi.LocSec(Section, "布局预设", "Layout Presets");
			SRApi.Nav(Section, 45); // 关卡(40) 之后、构建工具(50) 之前
			// 第 1 页保存与导入，第 2 页预设列表；页签顺序由 order 决定。
			SRApi.RegisterPage(Section, "save", "保存与导入", "Save & Import", 10, RenderSavePage);
			SRApi.RegisterPage(Section, "list", "预设列表", "Presets", 20, RenderListPage);
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
				"预设数量上限\n列表最多保留多少个预设，达到上限后必须先删除旧预设才能保存新布局\n范围 1 到 500；只限制新保存，不会删除磁盘上已有的预设文件。",
				"maximum number of presets\ncaps how many presets the list keeps; when reached, delete an old preset before saving a new layout\nrange 1-500; only limits new saves and never deletes existing preset files");
			SRApi.LocKey(Section, "Sort Mode", "列表排序", "Sort mode");
			SRApi.LocDesc(Section, "Sort Mode",
				"列表排序方式\n0=时间（新→旧），1=名称（A→Z）\n由列表页的排序下拉维护，不在配置区重复显示",
				"preset list sort mode\n0 = time (newest first), 1 = name (A-Z)\nmaintained by the sort dropdown on the list page and not repeated in the settings area");
			SRApi.LocKey(Section, ColWidthsKey, "列表列宽（内部）", "Preset widths (internal)");
			SRApi.LocDesc(Section, ColWidthsKey,
				"预设列表列宽\n由列表表头拖动自动写入，逗号分隔的设计单位\n一般无需手动修改；非法值会回退默认列宽",
				"preset list column widths\nwritten automatically when the list header is dragged; comma-separated design units\nnormally no need to edit; invalid values fall back to the default widths");
			SRApi.LocKey(Section, ColOrderKey, "列表列序（内部）", "Preset order (internal)");
			SRApi.LocDesc(Section, ColOrderKey,
				"预设列表列顺序\n由列表表头拖动换位自动写入，逗号分隔的列索引\n一般无需手动修改；非法值会回退默认列序",
				"preset list column order\nwritten automatically when the list header is dragged; comma-separated column indices\nnormally no need to edit; invalid values fall back to the default order");
		}

		// ---------- 配置读取 ----------

		private static bool FeatureOn {
			get { return _featureOn; }
		}

		private static int MaxPresets {
			get {
				try { return _maxPresets != null ? Mathf.Clamp(_maxPresets.Value, 1, 500) : 50; } catch { return 50; }
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

		// 当前搜索过滤（名称 / 关卡 / 模式）+ 排序后的可见列表。
		private static List<PresetInfo> VisiblePresets() {
			List<PresetInfo> list = new List<PresetInfo>();
			try {
				string q = (_search ?? "").Trim();
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					if (p == null) continue;
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

		// ---------- 页面渲染（两页：保存与导入 / 预设列表） ----------

		// 第 1 页「保存与导入」：保存当前预设 / 导入 / 导出 三个分区，各占一行。
		private static void RenderSavePage() {
			try {
				EnsureLoaded();
				SRApi.Ctl.SectionTitle(SRApi.T("- 保存当前预设 -", "- Save current preset -"));
				GUILayout.BeginHorizontal();
				try {
					GUILayout.Label(new GUIContent(SRApi.T("预设名称", "Preset name"),
						SRApi.T("保存时使用的名称，留空则自动用「关卡名 + 时间」命名\n同名预设会要求再点一次按钮确认覆盖\nWindows 不允许的文件名字符会被替换成下划线",
							"name used when saving; when empty, the level name plus a timestamp is used\na preset with the same name requires a second click to overwrite\ncharacters not allowed by Windows are replaced with underscores")),
						SRApi.Ctl.Label, GUILayout.Width(W(96)), GUILayout.Height(W(26)));
					string typed = SRApi.ClearableTextField(_newName ?? "", 40, SRApi.Ctl.SearchBox,
						GUILayout.Width(W(260)), GUILayout.Height(W(26)));
					if (typed != null && typed != _newName) _newName = typed;
					if (GUILayout.Button(SaveButtonText(), SRApi.Ctl.Btn, GUILayout.Width(W(130)), GUILayout.Height(W(26)))) SaveCurrent();
					GUILayout.FlexibleSpace();
				} finally {
					GUILayout.EndHorizontal();
				}
				GUILayout.Space(W(6));
				SRApi.Ctl.SectionTitle(SRApi.T("- 导入 -", "- Import -"));
				GUILayout.BeginHorizontal();
				try {
					GUILayout.Label(new GUIContent(SRApi.T("XML 路径", "XML path"),
						SRApi.T("从磁盘上的 XML 文件导入布局预设（例如别人发来的 .xml）\n路径可以是绝对路径，也可以是相对 BepInEx 目录的路径",
							"import a layout preset from an XML file on disk (for example a .xml received from someone)\nthe path may be absolute or relative to the BepInEx folder")),
						SRApi.Ctl.Label, GUILayout.Width(W(96)), GUILayout.Height(W(26)));
					string path = SRApi.ClearableTextField(_importPath ?? "", 260, SRApi.Ctl.SearchBox,
						GUILayout.Width(W(240)), GUILayout.Height(W(26)));
					if (path != null && path != _importPath) _importPath = path;
					if (GUILayout.Button(SRApi.T("从路径导入", "Import path"), SRApi.Ctl.Btn,
						GUILayout.Width(W(110)), GUILayout.Height(W(26)))) ImportFromPath();
					if (GUILayout.Button(SRApi.T("从剪贴板导入", "Import clipboard"), SRApi.Ctl.Btn,
						GUILayout.Width(W(120)), GUILayout.Height(W(26)))) ImportFromClipboard();
					GUILayout.FlexibleSpace();
				} finally {
					GUILayout.EndHorizontal();
				}
				GUILayout.Space(W(6));
				SRApi.Ctl.SectionTitle(SRApi.T("- 导出 -", "- Export -"));
				GUILayout.BeginHorizontal();
				try {
					if (GUILayout.Button(new GUIContent(SRApi.T("全部导出到目录", "Export all to folder"),
						SRApi.T("把所有预设 XML 复制到 预设目录/exports/<时间>/ 下（按模式分子目录），并复制一份 index.json",
							"copies every preset XML into presets/exports/<time>/ (grouped by mode) and copies index.json as well")),
						SRApi.Ctl.Btn, GUILayout.Width(W(140)), GUILayout.Height(W(24)))) ExportAllToDirectory();
					if (GUILayout.Button(new GUIContent(SRApi.T("复制清单", "Copy list"),
						SRApi.T("把全部预设的名称/模式/关卡/方块数/时间/路径作为文本清单复制到剪贴板",
							"copies a text list of every preset (name/mode/level/blocks/time/path) to the clipboard")),
						SRApi.Ctl.Btn, GUILayout.Width(W(100)), GUILayout.Height(W(24)))) CopyAllList();
					GUILayout.FlexibleSpace();
				} finally {
					GUILayout.EndHorizontal();
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("渲染保存与导入页", __ex);
			}
		}

		// 第 2 页「预设列表」：搜索/排序 + 可拖列宽/换位的标准表格。
		private static void RenderListPage() {
			try {
				EnsureLoaded();
				EnsureCols();
				SRApi.Ctl.SectionTitle(SRApi.T("- 搜索 / 排序 -", "- Search / sort -"));
				GUILayout.BeginHorizontal();
				try {
					GUILayout.Label(SRApi.T("搜索", "Search"), SRApi.Ctl.Label, GUILayout.Width(W(32)), GUILayout.Height(W(24)));
					string q = SRApi.ClearableTextField(_search ?? "", 40, SRApi.Ctl.SearchBox,
						GUILayout.Width(W(160)), GUILayout.Height(W(24)));
					if (q != null && q != _search) _search = q;
					GUILayout.Space(W(8));
					GUILayout.Label(SRApi.T("排序", "Sort"), SRApi.Ctl.Label, GUILayout.Width(W(32)), GUILayout.Height(W(24)));
					RenderSortCombo();
					GUILayout.FlexibleSpace();
				} finally {
					GUILayout.EndHorizontal();
				}
				if (!string.IsNullOrEmpty(_renameKey)) RenderRenameBar();
				GUILayout.Space(W(4));
				RenderPresetTable();
			} catch (Exception __ex) {
				SRApi.Guard.Log("渲染预设列表页", __ex);
			}
		}

		// 排序下拉：只有 时间 / 名称 两项。
		private static void RenderSortCombo() {
			try {
				ConfigEntryBase e = SRApi.Ctl.FindEntry(Section, "Sort Mode");
				string[] opts = new[] { SRApi.T("时间（新→旧）", "Time (newest first)"), SRApi.T("名称（A→Z）", "Name (A-Z)") };
				int[] vals = new int[] { 0, 1 };
				int cur = Mathf.Clamp(SortMode, 0, opts.Length - 1);
				bool open;
				if (e == null || !SRApi.Ctl.EditOpen.TryGetValue(e, out open)) open = false;
				int sel = SRApi.Ctl.ComboBox(e, opts[cur], vals, opts, ref open, W(150));
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
					GUILayout.Label(SRApi.T("重命名：", "Rename: "), SRApi.Ctl.Label, GUILayout.Width(W(64)), GUILayout.Height(W(24)));
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
		private static void RenderPresetTable() {
			try {
				List<PresetInfo> visible = VisiblePresets();
				string title = SRApi.T("- 预设列表", "- Preset list") + " (" + visible.Count + "/" + _presets.Count
					+ SRApi.T("，上限 ", ", limit ") + MaxPresets + ")";
				SRApi.Ctl.SectionTitle(title);
				float rowH = SRApi.Ctl.Sc(26);
				float headerH = SRApi.Ctl.Sc(24);
				float tableH = SRApi.Ctl.Sc(340);
				float rowsH = Mathf.Max(SRApi.Ctl.Sc(80), tableH - headerH);
				Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(tableH));
				if (box.width < SRApi.Ctl.Sc(220)) return;
				EnsureCols();
				if (_args == null) _args = new SRApi.TableArgs();
				float pinW = SRApi.Ctl.Sc(_colW[ColAction]);
				float stripW = Mathf.Max(SRApi.Ctl.Sc(80), box.width - pinW);
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
				_args.applyOrder = delegate(int[] o) { _order = NormalizeOrder(o); SaveCols(); };
				_args.saveOrder = delegate { SaveCols(); };
				_args.saveWidths = delegate { SaveCols(); };
				_args.hidden = null;
				_args.hideable = false;
				_args.defaultColW = null;
				_args.click = null;
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
						? SRApi.T("还没有预设：到「保存与导入」页保存当前布局，或从剪贴板 / 文件导入。", "No presets yet: use Save & Import to save the current layout, or import from clipboard / file.")
						: SRApi.T("没有匹配当前搜索的预设。", "No presets match the current search.");
				};
				SRApi.TableDraw(_args);
				if (e != null) {
					// 拖拽兜底复位（拖列宽 / 换列位由核心处理，这里只接住状态与落盘回调）。
					SRApi.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _args.moveCol, ref _args.moveDx,
						ref _args.dragCol, ref _dragH, SaveCols, null, null);
					SRApi.TableWheelInput(e, box, mp, _maxScrollX, _maxScrollY, ref _scrollCols, ref _scrollRows);
					HandleActionClicks(visible, e, mp);
					if (e.type == EventType.MouseUp) SRApi.BlockWindowDrag = false;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/表格绘制", __ex);
			}
		}

		private static string ColumnTitle(int ci) {
			switch (ci) {
				case ColName: return SRApi.T("名称", "Name");
				case ColLevel: return SRApi.T("关卡", "Level");
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
					case ColLevel: return SRApi.T("预设所属关卡（XML levelSceneName）；加载前会与当前关卡校验", "level the preset belongs to (XML levelSceneName); checked against the current level before loading");
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
			_cells[ColName] = p.Name ?? "";
			_cells[ColLevel] = LevelText(p);
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
		private static void DrawActionButtons(List<PresetInfo> visible, int row, Rect r) {
			try {
				if (_args == null) return;
				PresetInfo p = PresetAt(visible, row);
				if (p == null) return;
				float[] w = new float[] { 34f, 46f, 34f, 34f };
				float gap = SRApi.Ctl.Sc(2);
				float x = r.x + SRApi.Ctl.Sc(2);
				float y = r.y + SRApi.Ctl.Sc(1);
				float h = Mathf.Max(SRApi.Ctl.Sc(14), r.height - SRApi.Ctl.Sc(2));
				bool delPending = IsPending("del:" + p.Key);
				for (int k = 0; k < 4; k++) {
					float bw = SRApi.Ctl.Sc(w[k]);
					Rect br = new Rect(x, y, bw, h);
					x += bw + gap;
					if (br.xMax > r.xMax) break;
					string label;
					if (k == ActLoad) label = IsPending("load:" + p.Key) ? SRApi.T("确认", "Confirm") : SRApi.T("加载", "Load");
					else if (k == ActRename) label = SRApi.T("重命名", "Rename");
					else if (k == ActExport) label = SRApi.T("导出", "Export");
					else label = delPending ? SRApi.T("确认", "Confirm") : SRApi.T("删除", "Delete");
					bool hov = br.Contains(_args.mp);
					SRApi.Ctl.BtnClip.Draw(br, new GUIContent(label), hov, false, false, false);
					_actRects.Add(br);
					_actRows.Add(row);
					_actKinds.Add(k);
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/操作按钮", __ex);
			}
		}

		// 操作按钮点击：加载 / 重命名（进入下方编辑条）/ 导出到剪贴板 / 删除（两次确认）。
		private static void HandleActionClicks(List<PresetInfo> visible, Event e, Vector2 mp) {
			try {
				if (e == null || e.type != EventType.MouseDown || e.button != 0) {
					_actRects.Clear();
					_actRows.Clear();
					_actKinds.Clear();
					return;
				}
				for (int i = 0; i < _actRects.Count && i < _actRows.Count && i < _actKinds.Count; i++) {
					if (!_actRects[i].Contains(mp)) continue;
					PresetInfo p = PresetAt(visible, _actRows[i]);
					int kind = _actKinds[i];
					if (p != null) {
						if (kind == ActLoad) LoadPreset(p);
						else if (kind == ActRename) { ClearPending(); _renameKey = p.Key; _renameText = p.Name; }
						else if (kind == ActExport) ExportPreset(p);
						else if (kind == ActDelete) { if (ConfirmOrArm("del:" + p.Key)) DeletePreset(p); }
					}
					SRApi.BlockWindowDrag = true;
					e.Use();
					break;
				}
				_actRects.Clear();
				_actRows.Clear();
				_actKinds.Clear();
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/操作按钮点击", __ex);
			}
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
				// 操作列宽度固定为默认值（表头无法拖动它的宽度，避免手改配置把按钮挤没）。
				_colW[ColAction] = DefColW[ColAction];
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读取列设置", __ex);
			}
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
				if (_colWidthsEntry != null) SRApi.Ctl.MarkConfigDirty(_colWidthsEntry.ConfigFile);
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
				string name = (_newName ?? "").Trim();
				// 名称留空：默认用「关卡名 + 时间」，避免每次手工起名。
				if (name.Length == 0) {
					string baseName = string.IsNullOrEmpty(level) ? SRApi.T("布局", "Layout") : level;
					name = baseName + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				}
				if (name.Length > 40) name = name.Substring(0, 40);
				SaveXml(doc.OuterXml, name, mode, blocks, level);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/保存", __ex);
			}
		}

		// 保存 / 导入共用的落盘逻辑：同名覆盖需二次确认，数量上限只拦新预设。
		// level 来自 XML 根节点 levelSceneName（读不到传空串），写进预设元数据用于列表/加载前校验。
		private static void SaveXml(string xml, string name, string mode, int blocks, string level) {
			try {
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
					if (_presets.Count >= MaxPresets) {
						SRApi.Notify(SRApi.T("预设数量已达上限 ", "Preset limit reached: ") + MaxPresets
							+ SRApi.T("，请先删除旧预设", "; delete an old preset first"));
						return;
					}
				}
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
				p.Ok = true;
				SortPresets();
				SaveIndex();
				_status = SRApi.T("已保存预设：", "Saved preset: ") + name;
				SRApi.Notify(_status);
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
					doc = new XmlDocument();
					doc.LoadXml(xml);
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
				SaveXml(doc.OuterXml, name, mode, blocks, level);
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
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导出", __ex);
			}
		}

		// 全部导出到目录：预设目录/exports/<时间>/<模式>/<文件>，并附带一份 index.json。
		private static void ExportAllToDirectory() {
			try {
				if (!ActionAllowed()) return;
				if (_presets.Count == 0) {
					SRApi.Notify(SRApi.T("当前没有可导出的预设", "There are no presets to export"));
					return;
				}
				string baseDir = Path.Combine(PresetsRoot(), "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
				int copied = 0;
				int failed = 0;
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					if (p == null) continue;
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
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/全部导出", __ex);
			}
		}

		// 复制全部清单（文本）到剪贴板：名称 / 模式 / 关卡 / 方块数 / 时间 / 路径。
		private static void CopyAllList() {
			try {
				if (!ActionAllowed()) return;
				// 清单始终导出全部预设（不受搜索过滤影响），按当前排序输出。
				List<PresetInfo> list = new List<PresetInfo>(_presets);
				list.Sort(ComparePreset);
				if (list.Count == 0) {
					SRApi.Notify(SRApi.T("当前没有可复制的预设", "There are no presets to copy"));
					return;
				}
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("SR_UCH layout presets (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ")");
				for (int i = 0; i < list.Count; i++) {
					PresetInfo p = list[i];
					if (p == null) continue;
					sb.Append(i + 1).Append(". ").Append(p.Name ?? "")
						.Append("\tmode=").Append(p.Mode ?? "")
						.Append("\tlevel=").Append(LevelText(p))
						.Append("\tblocks=").Append(Mathf.Max(0, p.Blocks))
						.Append("\ttime=").Append(p.TimeUtc == DateTime.MinValue ? "-" : p.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
						.Append("\t").Append(p.FilePath ?? "")
						.AppendLine();
				}
				try {
					GUIUtility.systemCopyBuffer = sb.ToString();
				} catch (Exception __ex) {
					SRApi.Guard.Log("布局预设/复制清单写剪贴板", __ex);
					SRApi.Notify(SRApi.T("写入剪贴板失败", "Failed to write to the clipboard"));
					return;
				}
				_status = SRApi.T("已复制全部清单（", "Copied the full list (") + list.Count + SRApi.T(" 条）到剪贴板", " presets) to the clipboard");
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/复制清单", __ex);
			}
		}

		private static void DeletePreset(PresetInfo p) {
			try {
				if (p == null) return;
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
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/删除", __ex);
			}
		}

		private static void CommitRename(PresetInfo p) {
			try {
				if (p == null) return;
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
				SRApi.Notify(_status);
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
				if (!SRApi.GateModeAllows(SRApi.ModeMask.Party | SRApi.ModeMask.Creative | SRApi.ModeMask.Freeplay)) {
					SRApi.Notify(SRApi.T("加载预设：仅派对、创意或自由关卡内可用", "Load preset: available in Party, Creative and Freeplay levels only"));
					return;
				}
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
					SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
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
				string xml = ReadAllText(p.FilePath);
				if (string.IsNullOrEmpty(xml)) {
					SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
					return;
				}
				XmlDocument doc = null;
				try {
					doc = new XmlDocument();
					doc.LoadXml(xml);
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
				string currentMode = "";
				try { currentMode = st.GameMode.ToString(); } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取当前模式(加载)", __ex); }
				bool levelMismatch = !string.IsNullOrEmpty(presetScene) && !string.IsNullOrEmpty(activeScene)
					&& !string.Equals(presetScene, activeScene, StringComparison.Ordinal);
				bool modeMismatch = IsConcreteMode(p.Mode) && IsConcreteMode(currentMode)
					&& !string.Equals(p.Mode, currentMode, StringComparison.OrdinalIgnoreCase);
				if (levelMismatch || modeMismatch) {
					// 两段确认：第一次只提示差异并让操作列按钮变成「确认加载」，5 秒内再点一次才真正重载。
					string diff = SRApi.T("模式/关卡不匹配：预设 ", "Mode/level mismatch: preset ") + p.Name;
					if (modeMismatch) diff += SRApi.T("（模式 ", " (mode ") + ModeText(p.Mode) + SRApi.T("，当前 ", ", current ") + ModeText(currentMode) + "）";
					if (levelMismatch) diff += SRApi.T("（关卡 ", " (level ") + presetScene + SRApi.T("，当前 ", ", current ") + activeScene + "）";
					if (!ConfirmOrArm("load:" + p.Key)) {
						_status = diff + SRApi.T("；再点一次「加载」确认", "; click Load again to confirm");
						SRApi.Notify(_status);
						return;
					}
				}
				ClearPending();
				SetReloadBusy(true);
				try {
					// 一次性静态字段：游戏在 OnSetupStartLevel 里读取后立即置 null 并原生广播给全员。
					QuickSaver.levelPortalXml = xml;
					GameState.GameMode toMode = st.GameMode;
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
					SRApi.Notify(SRApi.T("加载预设：", "Loading preset: ") + p.Name + SRApi.T("，全员重载中（分数会重置）", "; everyone is reloading (scores will reset)"));
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
				if (!_reloadBusy) return false;
				// 关卡重载正在进行时拦下，避免与 Level 的重载并发。
				try {
					Func<bool> other = Slots.Require<Func<bool>>("level.reloadBusy");
					if (other != null && other()) {
						SRApi.Notify(SRApi.T("关卡正在重载，请稍候", "Level reload in progress; please wait"));
						return true;
					}
				} catch (Exception __ex) { SRApi.Guard.Log("预设/重载互斥检查", __ex); }
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
				if (!Directory.Exists(root)) return;
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
							pi.Ok = JsonBool(line, "ok");
							// 字段缺失判定：老索引（v1）没有 level / ok -> 默认 false/0，扫描时自愈。
							bool complete = line.IndexOf("\"level\":", StringComparison.Ordinal) >= 0
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
							bool xmlOk = TryReadPresetMeta(fp, out lv, out blocks);
							pi.Level = lv ?? "";
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
				SortPresets();
				if (upgraded) SaveIndex(); // 自愈结果立刻落盘（旧索引升级 / 新文件补登记）
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/扫描目录", __ex);
			}
		}

		private static void SaveIndex() {
			try {
				string root = PresetsRoot();
				Directory.CreateDirectory(root);
				StringBuilder sb = new StringBuilder();
				sb.Append("{\n  \"version\": ").Append(IndexVersion).Append(",\n  \"entries\": [\n");
				for (int i = 0; i < _presets.Count; i++) {
					PresetInfo p = _presets[i];
					sb.Append("    {\"file\":\"").Append(JsonEsc(p.File))
						.Append("\",\"name\":\"").Append(JsonEsc(p.Name))
						.Append("\",\"mode\":\"").Append(JsonEsc(p.Mode))
						.Append("\",\"level\":\"").Append(JsonEsc(p.Level))
						.Append("\",\"blocks\":").Append(Mathf.Max(0, p.Blocks))
						.Append(",\"time\":\"").Append(JsonEsc(TimeIso(p.TimeUtc)))
						.Append("\",\"ok\":").Append(p.Ok ? "true" : "false").Append("}");
					if (i < _presets.Count - 1) sb.Append(',');
					sb.Append('\n');
				}
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

		private static string ReadAllText(string path) {
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
				return File.ReadAllText(path, Encoding.UTF8);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/读文件", __ex);
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

		// 流式读取预设 XML 的元数据：根节点必须是 scene，取 levelSceneName 并统计顶层 block 数量。
		// 用 XmlReader 而不是 XmlDocument：扫描列表时只关心根属性与方块数，避免把整份 XML 建成 DOM。
		private static bool TryReadPresetMeta(string path, out string level, out int blocks) {
			level = "";
			blocks = 0;
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
					if (reader.IsEmptyElement) return false;
					int rootDepth = reader.Depth;
					while (reader.Read()) {
						if (reader.NodeType == XmlNodeType.Element && reader.Depth == rootDepth + 1 && reader.Name == "block") {
							blocks++;
						} else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rootDepth) {
							break;
						}
					}
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
				return p.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
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

		private static string ModeText(string mode) {
			try {
				if (string.IsNullOrEmpty(mode)) return "-";
				switch (mode.ToUpperInvariant()) {
					case "PARTY": return SRApi.T("派对", "Party");
					case "CREATIVE": return SRApi.T("创意", "Creative");
					case "FREEPLAY": return SRApi.T("自由", "Freeplay");
					case "CHALLENGE": return SRApi.T("挑战", "Challenge");
					default: return mode;
				}
			} catch { return mode; }
		}

		// 列表里的关卡显示：XML 里读不到 levelSceneName 时留空显示 "-"。
		private static string LevelText(PresetInfo p) {
			try {
				if (p == null || string.IsNullOrEmpty(p.Level)) return "-";
				return p.Level;
			} catch { return "-"; }
		}

		private static string LevelFromDoc(XmlDocument doc) {
			try {
				if (doc == null || doc.DocumentElement == null) return "";
				string lv = doc.DocumentElement.GetAttribute("levelSceneName");
				return lv ?? "";
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
