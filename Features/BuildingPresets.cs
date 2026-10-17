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
		// 对外槽位：Func<string xml, string displayName, bool ok>，供对局回放把阶段快照存成预设。
		private const string SlotSaveXml = "presets.saveXml";
		private const string IndexFileName = "index.json";
		private const int IndexVersion = 1;
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
		private static ConfigEntry<bool> _showTimestamps;
		// 运行期快照：配置变化回调里即时更新，供每帧渲染读取（避免每帧访问 ConfigEntry）。
		private static bool _featureOn;
		private static bool _showTimestampsOn;
		// 存储根目录缓存（BepInEx.Paths 的调用只做一次）。
		private static string _rootDir;

		private static bool _loaded;
		private static readonly List<PresetInfo> _presets = new List<PresetInfo>();

		// UI 状态：保存名称输入框、状态行、二次确认、重命名编辑。
		private static string _newName = "";
		private static string _status = "";
		private static string _pendingKey;
		private static float _pendingAt = -1f;
		private static string _renameKey;
		private static string _renameText;

		// 加载预设时的重载锁（本功能自己的，和 Level 的锁互相不可见）。
		private static bool _reloadBusy;
		private static float _reloadBusyAt = -1f;

		// 一条预设的展示元数据。磁盘上的 XML 文件是权威数据；index.json 只是名称/方块数/时间的缓存。
		private sealed class PresetInfo {
			public string Name;
			public string Mode;      // 模式目录名（GameState.GameMode 的 ToString）
			public string File;      // 安全文件名（含 .xml）
			public string FilePath;  // 扫描时补全的绝对路径
			public int Blocks = -1;  // -1 = 索引里没有，需要从 XML 现算
			public DateTime TimeUtc = DateTime.MinValue;

			public string Key { get { return Mode + "/" + File; } }
		}

		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				_enabled = plugin.Config.Bind(Section, "Enabled", false,
					"布局预设总开关：开启后可在本页保存 / 加载 / 导入导出方块布局预设。");
				_featureOn = _enabled.Value;
				_enabled.SettingChanged += delegate { _featureOn = _enabled.Value; };
				_maxPresets = plugin.Config.Bind(Section, "Max Presets", 50,
					new ConfigDescription("预设数量上限（1-500）：列表最多保留多少条，达到上限后必须先删除旧预设才能保存新布局。",
						new AcceptableValueRange<int>(1, 500)));
				_showTimestamps = plugin.Config.Bind(Section, "Show Timestamps", true,
					"显示时间列：在预设列表显示每条的保存时间；关闭后列表更紧凑。");
				_showTimestampsOn = _showTimestamps.Value;
				_showTimestamps.SettingChanged += delegate { _showTimestampsOn = _showTimestamps.Value; };
				// 对外槽位：其它功能（对局回放）把阶段快照 XML 存成预设。只新增跨功能入口，不改变本页现有行为。
				try { Slots.Provide(SlotSaveXml, (Func<string, string, bool>)SaveXmlFromOther); }
				catch (Exception __sx) { SRApi.Guard.Log("布局预设/提供槽位", __sx); }
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设 初始化", __ex);
			}
		}

		// 栏目注册：与 PlayerTracker 的写法一致（LocSec / Nav / RegisterPage 三件套）。
		private static void SelfReg() {
			SRApi.LocSec(Section, "布局预设", "Layout Presets");
			SRApi.Nav(Section, 55); // 构建工具(50) 之后、清场(60) 之前
			SRApi.RegisterPage(Section, Render);
			SRApi.LocKey(Section, "Enabled", "布局预设总开关", "Layout presets");
			SRApi.LocDesc(Section, "Enabled",
				"布局预设总开关\n开启后可在本页保存和加载方块布局预设\n关闭后本页仍可查看已有预设，但所有按钮都不生效\n与模组总开关相互独立",
				"master switch for layout presets\nwhen on, block layout presets can be saved and loaded on this page\nwhen off, existing presets are still listed but every action button is inactive\nindependent from the mod-wide master switch");
			SRApi.LocKey(Section, "Max Presets", "预设数量上限", "Max presets");
			SRApi.LocDesc(Section, "Max Presets",
				"预设数量上限\n列表最多保留多少个预设，达到上限后必须先删除旧预设才能保存新布局\n范围 1 到 500\n只限制新保存，不会删除磁盘上已有的预设文件",
				"maximum number of presets\ncaps how many presets the list keeps; when reached, delete an old preset before saving a new layout\nrange 1-500\nonly limits new saves and never deletes existing preset files");
			SRApi.LocKey(Section, "Show Timestamps", "显示时间", "Show timestamps");
			SRApi.LocDesc(Section, "Show Timestamps",
				"显示时间列\n在预设列表里显示每条的保存时间\n关闭后列表只显示名称、方块数和模式，更紧凑\n时间取自预设文件的最后修改时间",
				"show timestamps\nshows when each preset was saved in the list\nwhen off, the list only shows name, block count and mode to save space\nuses the preset file last write time");
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

		private static bool ShowTimestamps {
			get { return _showTimestampsOn; }
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

		// ---------- 页面渲染 ----------

		private static void Render() {
			try {
				EnsureLoaded();
				SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Settings -"));
				// 配置条目统一走核心的行渲染（名称/说明来自上面的 LocKey / LocDesc）。
				SRApi.RenderSectionEntries(Section);
				GUILayout.Space(W(6));
				RenderSaveBar();
				GUILayout.Space(W(6));
				RenderList();
			} catch (Exception __ex) {
				SRApi.Guard.Log("渲染布局预设页", __ex);
			}
		}

		// 保存 / 导入 / 刷新 三个入口 + 状态行。
		private static void RenderSaveBar() {
			SRApi.Ctl.SectionTitle(SRApi.T("- 保存当前布局 -", "- Save current layout -"));
			GUILayout.BeginHorizontal();
			try {
				GUILayout.Label(new GUIContent(SRApi.T("预设名称", "Preset name"),
					SRApi.T("保存时使用的名称\n同名预设会要求再点一次按钮确认覆盖\nWindows 不允许的文件名字符会被替换成下划线",
						"name used when saving\nan existing preset with the same name requires a second click to overwrite\ncharacters not allowed by Windows are replaced with underscores")),
					SRApi.Ctl.Label, GUILayout.Width(W(96)), GUILayout.Height(W(26)));
				string typed = SRApi.ClearableTextField(_newName ?? "", 40, SRApi.Ctl.SearchBox,
					GUILayout.Width(W(200)), GUILayout.Height(W(26)));
				if (typed != null && typed != _newName) _newName = typed;
				if (GUILayout.Button(SaveButtonText(), SRApi.Ctl.Btn, GUILayout.Width(W(130)), GUILayout.Height(W(26)))) SaveCurrent();
				GUILayout.Space(W(6));
				if (GUILayout.Button(SRApi.T("从剪贴板导入", "Import clipboard"), SRApi.Ctl.Btn,
					GUILayout.Width(W(140)), GUILayout.Height(W(26)))) ImportFromClipboard();
				GUILayout.FlexibleSpace();
				if (GUILayout.Button(SRApi.T("刷新列表", "Refresh"), SRApi.Ctl.Btn,
					GUILayout.Width(W(90)), GUILayout.Height(W(26)))) {
					ClearPending();
					_loaded = false;
					EnsureLoaded();
					_status = "";
				}
			} finally {
				GUILayout.EndHorizontal();
			}
			if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, SRApi.Ctl.LabelWrap);
		}

		// 列表：表头 + 每行 [加载][重命名][导出][删除]。
		private static void RenderList() {
			int max = MaxPresets;
			SRApi.Ctl.SectionTitle(SRApi.T("- 预设列表", "- Preset list") + " (" + _presets.Count + "/" + max + ")");
			GUILayout.BeginHorizontal();
			try {
				HeaderLabel(SRApi.T("名称", "Name"), 190);
				HeaderLabel(SRApi.T("方块数", "Blocks"), 56);
				HeaderLabel(SRApi.T("模式", "Mode"), 72);
				if (ShowTimestamps) HeaderLabel(SRApi.T("时间", "Time"), 120);
				GUILayout.FlexibleSpace();
				HeaderLabel(SRApi.T("操作", "Actions"), 180);
			} finally {
				GUILayout.EndHorizontal();
			}
			if (_presets.Count == 0) {
				GUILayout.Label(SRApi.T(
					"还没有预设：在任意对局里输入名称后点「保存当前布局」，或者点「从剪贴板导入」。",
					"No presets yet: enter a name and click Save layout in a match, or click Import clipboard."),
					SRApi.Ctl.LabelWrap);
				return;
			}
			for (int i = 0; i < _presets.Count; i++) {
				try {
					RenderRow(_presets[i]);
				} catch (Exception __ex) {
					SRApi.Guard.Log("渲染预设行", __ex);
				}
			}
		}

		private static void HeaderLabel(string text, float width) {
			GUILayout.Label(text, SRApi.Ctl.Label, GUILayout.Width(W(width)), GUILayout.Height(W(24)));
		}

		private static void RenderRow(PresetInfo p) {
			if (p == null) return;
			GUILayout.BeginHorizontal();
			try {
				// 重命名编辑态：名称列换成输入框，只留确定 / 取消。
				if (_renameKey == p.Key) {
					string typed = SRApi.ClearableTextField(_renameText ?? p.Name, 40, SRApi.Ctl.SearchBox,
						GUILayout.Width(W(190)), GUILayout.Height(W(24)));
					if (typed != null && typed != _renameText) _renameText = typed;
					if (GUILayout.Button(SRApi.T("确定", "OK"), SRApi.Ctl.Btn, GUILayout.Width(W(60)), GUILayout.Height(W(24)))) CommitRename(p);
					if (GUILayout.Button(SRApi.T("取消", "Cancel"), SRApi.Ctl.Btn, GUILayout.Width(W(60)), GUILayout.Height(W(24)))) {
						_renameKey = null;
						_renameText = null;
					}
					GUILayout.FlexibleSpace();
					return;
				}
				string tip = SRApi.T("预设文件：", "Preset file: ") + p.Mode + "/" + p.File
					+ "\n" + SRApi.T("方块数：", "Blocks: ") + Mathf.Max(0, p.Blocks)
					+ "\n" + SRApi.T("保存时间：", "Saved: ") + TimeText(p);
				GUILayout.Label(new GUIContent(p.Name, tip), SRApi.Ctl.Label, GUILayout.Width(W(190)), GUILayout.Height(W(24)));
				GUILayout.Label(Mathf.Max(0, p.Blocks).ToString(), SRApi.Ctl.Label, GUILayout.Width(W(56)), GUILayout.Height(W(24)));
				GUILayout.Label(ModeText(p.Mode), SRApi.Ctl.Label, GUILayout.Width(W(72)), GUILayout.Height(W(24)));
				if (ShowTimestamps) GUILayout.Label(TimeText(p), SRApi.Ctl.Label, GUILayout.Width(W(120)), GUILayout.Height(W(24)));
				GUILayout.FlexibleSpace();
				GUIContent loadContent = new GUIContent(SRApi.T("加载", "Load"),
					SRApi.T("仅房主可用：把这份预设写进 QuickSaver.levelPortalXml 后按房主视角重载当前关卡\n会重载场景并重置本局分数，全员一起重新加载",
						"host only: writes this preset into QuickSaver.levelPortalXml and reloads the current level from the host view\nthis reloads the scene, resets the score of the current match and reloads everyone"));
				if (GUILayout.Button(loadContent, SRApi.Ctl.Btn, GUILayout.Width(W(52)), GUILayout.Height(W(24)))) LoadPreset(p);
				GUIContent renameContent = new GUIContent(SRApi.T("重命名", "Rename"),
					SRApi.T("只改列表里显示的名称，磁盘上的文件名保持不变", "only changes the name shown in the list; the file name on disk stays the same"));
				if (GUILayout.Button(renameContent, SRApi.Ctl.Btn, GUILayout.Width(W(68)), GUILayout.Height(W(24)))) {
					ClearPending();
					_renameKey = p.Key;
					_renameText = p.Name;
				}
				GUIContent exportContent = new GUIContent(SRApi.T("导出", "Export"),
					SRApi.T("把预设 XML 复制到剪贴板，便于分享或手动备份", "copies the preset XML to the clipboard for sharing or manual backup"));
				if (GUILayout.Button(exportContent, SRApi.Ctl.Btn, GUILayout.Width(W(52)), GUILayout.Height(W(24)))) ExportPreset(p);
				string delKey = "del:" + p.Key;
				string delLabel = IsPending(delKey) ? SRApi.T("确认删除", "Confirm") : SRApi.T("删除", "Delete");
				GUIContent delContent = new GUIContent(delLabel,
					SRApi.T("删除预设文件；需要点两次：第一次变成「确认删除」，5 秒内再点一次才真正删除",
						"deletes the preset file; needs two clicks: the first turns the button into Confirm, a second click within 5 seconds deletes it"));
				if (GUILayout.Button(delContent, SRApi.Ctl.Btn, GUILayout.Width(W(72)), GUILayout.Height(W(24)))) {
					if (ConfirmOrArm(delKey)) DeletePreset(p);
				}
			} finally {
				GUILayout.EndHorizontal();
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
				string name = (_newName ?? "").Trim();
				if (name.Length == 0) {
					SRApi.Notify(SRApi.T("请先输入预设名称", "Enter a preset name first"));
					return;
				}
				if (name.Length > 40) name = name.Substring(0, 40);
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
				SaveXml(doc.OuterXml, name, mode, blocks);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/保存", __ex);
			}
		}

		// 保存 / 导入共用的落盘逻辑：同名覆盖需二次确认，数量上限只拦新预设。
		private static void SaveXml(string xml, string name, string mode, int blocks) {
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

		// 槽位实现：把外部 XML（对局回放的阶段快照）存成布局预设。
		// 校验：XML 非空 / 根节点必须是 scene / 方块数 1..MaxImportBlocks；重名自动加 "-2"、"-3"…，不弹二次确认。
		// 返回值：true = 已写文件并刷新索引；false = 被拒绝或失败（失败原因已 Notify）。
		private static bool SaveXmlFromOther(string xml, string displayName) {
			try {
				if (string.IsNullOrEmpty(xml)) return false;
				if (xml.Length > MaxImportChars) {
					SRApi.Notify(SRApi.T("快照 XML 过大，已拒绝保存为预设", "Snapshot XML is too large; preset save rejected"));
					return false;
				}
				XmlDocument doc = null;
				try {
					doc = new XmlDocument();
					doc.LoadXml(xml);
				} catch (Exception __px) {
					SRApi.Guard.Log("布局预设/槽位解析XML", __px);
				}
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
					SRApi.Notify(SRApi.T("快照 XML 根节点不是 scene，已拒绝", "Snapshot XML root is not scene; rejected"));
					return false;
				}
				int blocks = CountBlocks(doc);
				if (blocks <= 0) {
					SRApi.Notify(SRApi.T("快照里没有方块，已拒绝", "The snapshot contains no blocks; rejected"));
					return false;
				}
				if (blocks > MaxImportBlocks) {
					SRApi.Notify(SRApi.T("快照方块数超过上限 ", "Snapshot block count exceeds the limit ") + MaxImportBlocks + SRApi.T("，已拒绝", "; rejected"));
					return false;
				}
				StripDestroyed(doc);
				EnsureLoaded();
				if (_presets.Count >= MaxPresets) {
					SRApi.Notify(SRApi.T("预设数量已达上限 ", "Preset limit reached: ") + MaxPresets + SRApi.T("，请先删除旧预设", "; delete an old preset first"));
					return false;
				}
				string mode = "REPLAY";
				try {
					GameSettings st = GameSettings.GetInstance();
					if (st != null) mode = st.GameMode.ToString();
				} catch (Exception __mx) { SRApi.Guard.Log("布局预设/槽位读取模式", __mx); }
				string dirName = SafeDirName(mode);
				string dir = Path.Combine(PresetsRoot(), dirName);
				string baseName = (displayName ?? "").Trim();
				if (baseName.Length == 0) baseName = SRApi.T("回放快照 ", "Replay snapshot ") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				if (baseName.Length > 40) baseName = baseName.Substring(0, 40);
				string finalName = baseName;
				string fileName = SafeFileName(finalName) + ".xml";
				string key = dirName + "/" + fileName;
				int n = 2;
				while (File.Exists(Path.Combine(dir, fileName)) || FindPreset(key) != null) {
					string candidate = baseName + "-" + n.ToString(CultureInfo.InvariantCulture);
					if (candidate.Length > 60) candidate = candidate.Substring(0, 60);
					finalName = candidate;
					fileName = SafeFileName(finalName) + ".xml";
					key = dirName + "/" + fileName;
					n++;
					if (n > 999) {
						SRApi.Notify(SRApi.T("同名预设过多，保存失败", "Too many presets with the same name; save failed"));
						return false;
					}
				}
				Directory.CreateDirectory(dir);
				File.WriteAllText(Path.Combine(dir, fileName), doc.OuterXml, new UTF8Encoding(false));
				PresetInfo p = FindPreset(key);
				if (p == null) {
					p = new PresetInfo();
					p.Mode = dirName;
					p.File = fileName;
					_presets.Add(p);
				}
				p.Name = finalName;
				p.Blocks = blocks;
				p.TimeUtc = DateTime.UtcNow;
				p.FilePath = Path.Combine(dir, fileName);
				SortPresets();
				SaveIndex();
				SRApi.LogInfo("[布局预设] 槽位保存 " + key + "（" + blocks + " 块）");
				return true;
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/槽位保存", __ex);
				return false;
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
				xml = xml.Trim();
				if (xml.Length > MaxImportChars) {
					SRApi.Notify(SRApi.T("剪贴板内容过大，已拒绝导入（上限 ", "Clipboard content is too large; import rejected (limit ")
						+ (MaxImportChars / 1000) + SRApi.T(" 千字符）", "k characters)"));
					return;
				}
				XmlDocument doc = null;
				try {
					doc = new XmlDocument();
					doc.LoadXml(xml);
				} catch (Exception __ex) {
					// 解析失败是非常常见的用户错误（粘错东西），只记一次 Guard 日志并提示，不抛到页面。
					SRApi.Guard.Log("布局预设/解析剪贴板 XML", __ex);
				}
				if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
					SRApi.Notify(SRApi.T("剪贴板里不是关卡布局 XML（根节点必须是 scene）", "Clipboard content is not level layout XML (root node must be scene)"));
					return;
				}
				int blocks = CountBlocks(doc);
				if (blocks <= 0) {
					SRApi.Notify(SRApi.T("剪贴板 XML 里没有方块", "The clipboard XML contains no blocks"));
					return;
				}
				if (blocks > MaxImportBlocks) {
					SRApi.Notify(SRApi.T("方块数超过导入上限 ", "Block count exceeds the import limit ") + MaxImportBlocks
						+ SRApi.T("，已拒绝导入", "; import rejected"));
					return;
				}
				string level = "";
				try { level = doc.DocumentElement.GetAttribute("levelSceneName"); } catch { }
				if (string.IsNullOrEmpty(level)) {
					SRApi.Notify(SRApi.T("XML 缺少 levelSceneName，无法确定属于哪个关卡", "The XML has no levelSceneName, so its level cannot be determined"));
					return;
				}
				StripDestroyed(doc);
				string name = (_newName ?? "").Trim();
				if (name.Length == 0) name = SRApi.T("导入 ", "Imported ") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				if (name.Length > 40) name = name.Substring(0, 40);
				string mode = "IMPORTED";
				try {
					GameSettings st = GameSettings.GetInstance();
					if (st != null) mode = st.GameMode.ToString();
				} catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取模式(导入)", __ex); }
				SaveXml(doc.OuterXml, name, mode, blocks);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/导入", __ex);
			}
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
				try { activeScene = SceneManager.GetActiveScene().name; } catch { }
				if (!string.IsNullOrEmpty(presetScene) && !string.IsNullOrEmpty(activeScene) && presetScene != activeScene) {
					SRApi.Notify(SRApi.T("该预设属于关卡「", "This preset belongs to level \"") + presetScene
						+ SRApi.T("」，当前关卡是「", "\", but the current level is \"") + activeScene + SRApi.T("」", "\""));
					return;
				}
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
			LoadFromDisk();
		}

		// 扫描磁盘重建列表：index.json 只提供展示元数据，XML 文件才是权威数据。
		// 索引损坏 / 被手改坏时，仍按模式目录下的 XML 文件重建，绝不因为索引坏了就丢预设。
		private static void LoadFromDisk() {
			_loaded = true;
			_presets.Clear();
			try {
				string root = PresetsRoot();
				if (!Directory.Exists(root)) return;
				Dictionary<string, PresetInfo> byKey = new Dictionary<string, PresetInfo>(StringComparer.OrdinalIgnoreCase);
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
						if (!byKey.TryGetValue(key, out pi)) {
							pi = new PresetInfo();
							pi.Mode = mode;
							pi.File = file;
							pi.Name = Path.GetFileNameWithoutExtension(file);
						}
						pi.Mode = mode;
						pi.File = file;
						pi.FilePath = fp;
						// 索引没记方块数时现算一次；有缓存就信缓存（避免每次打开页面都解析一批大 XML）。
						if (pi.Blocks < 0) pi.Blocks = CountBlocksInFile(fp);
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
						.Append("\",\"blocks\":").Append(Mathf.Max(0, p.Blocks))
						.Append(",\"time\":\"").Append(JsonEsc(TimeIso(p.TimeUtc))).Append("\"}");
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
				_presets.Sort(delegate(PresetInfo a, PresetInfo b) {
					int c = b.TimeUtc.CompareTo(a.TimeUtc);
					if (c != 0) return c;
					return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
				});
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

		private static int CountBlocksInFile(string path) {
			try {
				XmlDocument doc = new XmlDocument();
				doc.Load(path);
				return CountBlocks(doc);
			} catch (Exception __ex) {
				SRApi.Guard.Log("布局预设/统计方块数", __ex);
				return 0;
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
