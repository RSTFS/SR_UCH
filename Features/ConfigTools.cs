// 配置导出 / 导入工具（sr.configtools）：把本模组当前配置导出成单个 JSON，或从历史备份逐键写回。
// 存储：BepInEx/config/SR_UCH/config-backup/<yyyyMMdd-HHmmss>.json；导入前自动生成 pre-import-*.json。
// 约束：功能层只允许走 SRApi / Slots 契约与 BepInEx 公开类型，绝不引用 SR.* 实现层
//       （拆分构建下本文件单独编成 SR_UCH_ConfigTools.dll，只引用核心 SR_UCH.dll）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
	[SrFeature("sr.configtools", "ConfigTools", "1.0")]
	public class ConfigTools : ITweak {
		// 配置段名：导航栏目名 / LocSec / Config.Bind 三处必须完全一致。
		private const string Section = "Config Tools";
		private const int BackupVersion = 1;
		// 二次确认窗口（秒）：超时后按钮自动恢复第一次点击前的文案。
		private const float ConfirmWindow = 5f;
		// 备份数量上限的硬夹取范围（配置文件手改成离谱值也不会把整批备份删光）。
		private const int MaxBackupsMin = 1;
		private const int MaxBackupsMax = 200;
		// 孤儿键 / 跳过项的 UI 展示上限，避免导入一份陌生备份后整页被清单刷屏。
		private const int ReportListLimit = 12;

		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<int> _maxBackups;
		private static bool _featureOn;
		// IFeatureHost.Config 由契约注入（就是本模组的 SR_UCH_data.cfg），用于枚举全部段/键。
		private static ConfigFile _config;
		// 存储根目录缓存（BepInEx.Paths 的调用只做一次）。
		private static string _rootDir;

		private static bool _loaded;
		private static readonly List<BackupFile> _backups = new List<BackupFile>();

		// UI 状态：状态行 / 孤儿键清单 / 二次确认 / 最近一次导入前备份路径（失败时的回滚路径）。
		private static string _status = "";
		private static string _reportText = "";
		private static string _pendingKey;
		private static float _pendingAt = -1f;
		private static string _lastPreImportPath;

		// 一条备份文件的展示元数据。
		private sealed class BackupFile {
			public string FilePath;
			public string FileName;
			public long Size;
			public int KeyCount = -1;   // -1 = 读不出来（不是本功能格式 / 文件损坏）
			public DateTime Time = DateTime.MinValue;
			public bool PreImport;      // 文件名以 pre-import- 开头（导入前自动生成的保护备份）
		}

		// 备份 JSON 里的一条配置记录。
		private sealed class BackupEntry {
			public string Section;
			public string Key;
			public string Type;
			public string Value;
		}

		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				// 契约提供宿主 Config（BepInEx ConfigFile）：功能层用它枚举/写回全部本模组条目。
				_config = plugin.Config;
				_enabled = plugin.Config.Bind(Section, "Enabled", true,
					"配置导出/导入总开关：开启后可在本页导出、导入和删除配置备份。");
				_featureOn = _enabled.Value;
				_enabled.SettingChanged += delegate { _featureOn = _enabled.Value; };
				_maxBackups = plugin.Config.Bind(Section, "Max Backups", 20,
					new ConfigDescription("备份数量上限（1-200）：超出后自动删除最旧的备份文件；导入前自动生成的 pre-import 备份也计入。",
						new AcceptableValueRange<int>(MaxBackupsMin, MaxBackupsMax)));
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具 初始化", __ex);
			}
		}

		// 栏目注册：与 PlayerTracker 的写法一致（LocSec / Nav / RegisterPage 三件套）。
		private static void SelfReg() {
			SRApi.LocSec(Section, "配置工具", "Config Tools");
			SRApi.RegisterFooterButton(Section, "配置工具", "Config Tools", null); // 底栏入口，不再占侧栏
			SRApi.RegisterPage(Section, Render);
			SRApi.LocKey(Section, "Enabled", "配置导出/导入总开关", "Config export/import");
			SRApi.LocDesc(Section, "Enabled",
				"配置导出/导入总开关\n开启后可在本页导出、导入和删除模组配置备份\n关闭后仍会显示历史备份列表，但所有操作按钮都不生效",
				"master switch for config export/import\nwhen on, mod config backups can be exported, imported and deleted on this page\nwhen off, the backup list is still shown but every action button is inactive");
			SRApi.LocKey(Section, "Max Backups", "备份数量上限", "Max backups");
			SRApi.LocDesc(Section, "Max Backups",
				"备份数量上限\n自动清理最旧的备份文件，只保留最近 N 份\n范围 1 到 200（导入前自动生成的 pre-import 备份也计入）",
				"maximum number of backups\noldest backup files are pruned automatically, keeping only the newest N\nrange 1-200; automatic pre-import backups also count");
		}

		// ---------- 配置读取 ----------

		private static bool FeatureOn {
			get { return _featureOn; }
		}

		private static int MaxBackups {
			get {
				try { return _maxBackups != null ? Mathf.Clamp(_maxBackups.Value, MaxBackupsMin, MaxBackupsMax) : 20; } catch { return 20; }
			}
		}

		// 所有动作共用的门控：模组总开关 + 本功能总开关；返回 false 时已给出用户提示。
		private static bool ActionAllowed() {
			if (!SRApi.GateMaster) {
				SRApi.Notify(SRApi.MsgMasterOff);
				return false;
			}
			if (!FeatureOn) {
				SRApi.Notify(SRApi.T("配置工具总开关已关闭", "Config tools are disabled"));
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
				if (!_loaded) RefreshList();
				SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Settings -"));
				// 配置条目统一走核心的行渲染（名称/说明来自上面的 LocKey / LocDesc）。
				SRApi.RenderSectionEntries(Section);
				GUILayout.Space(W(6));
				RenderToolbar();
				GUILayout.Space(W(6));
				RenderList();
			} catch (Exception __ex) {
				SRApi.Guard.Log("渲染配置工具页", __ex);
			}
		}

		// 顶部：立即导出 / 刷新，状态行，备份目录，孤儿键清单。
		private static void RenderToolbar() {
			SRApi.Ctl.SectionTitle(SRApi.T("- 备份操作 -", "- Backup actions -"));
			GUILayout.BeginHorizontal();
			try {
				if (GUILayout.Button(new GUIContent(SRApi.T("立即导出", "Export now"),
					SRApi.T("把当前模组配置导出成一份新的 JSON 备份。\n文件名是导出时间（yyyyMMdd-HHmmss），同一秒内重复导出会自动加后缀。",
						"exports the current mod config as a new JSON backup.\nthe file name is the export time (yyyyMMdd-HHmmss); repeated exports in the same second get a numeric suffix")),
					SRApi.Ctl.Btn, GUILayout.Width(W(120)), GUILayout.Height(W(26)))) ExportNow();
				GUILayout.Space(W(6));
				if (GUILayout.Button(SRApi.T("刷新列表", "Refresh"), SRApi.Ctl.Btn,
					GUILayout.Width(W(90)), GUILayout.Height(W(26)))) {
					ClearPending();
					RefreshList();
				}
				GUILayout.FlexibleSpace();
			} finally {
				GUILayout.EndHorizontal();
			}
			// 目录路径单独一行：横向布局里 Label 不换行，长路径会把按钮挤出窗口。
			GUILayout.Label(SRApi.T("备份目录：", "Backup folder: ") + BackupRoot(), SRApi.Ctl.LabelWrap);
			if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, SRApi.Ctl.LabelWrap);
			if (!string.IsNullOrEmpty(_reportText)) GUILayout.Label(_reportText, SRApi.Ctl.LabelWrap);
		}

		// 列表：时间 / 文件 / 大小 / 键数 / [导入][删除]。
		private static void RenderList() {
			SRApi.Ctl.SectionTitle(SRApi.T("- 历史备份", "- Backup history") + " (" + _backups.Count + "/" + MaxBackups + ")");
			GUILayout.BeginHorizontal();
			try {
				HeaderLabel(SRApi.T("时间", "Time"), 120);
				HeaderLabel(SRApi.T("文件", "File"), 230);
				HeaderLabel(SRApi.T("大小", "Size"), 64);
				HeaderLabel(SRApi.T("键数", "Keys"), 48);
				GUILayout.FlexibleSpace();
				HeaderLabel(SRApi.T("操作", "Actions"), 132);
			} finally {
				GUILayout.EndHorizontal();
			}
			if (_backups.Count == 0) {
				GUILayout.Label(SRApi.T(
					"还没有备份：点上面的「立即导出」生成第一份。导入前系统也会自动生成一份 pre-import 保护备份。",
					"No backups yet: click Export now to create the first one. An automatic pre-import backup is also created before every import."),
					SRApi.Ctl.LabelWrap);
				return;
			}
			for (int i = 0; i < _backups.Count; i++) {
				try {
					RenderRow(_backups[i]);
				} catch (Exception __ex) {
					SRApi.Guard.Log("渲染备份行", __ex);
				}
			}
		}

		private static void HeaderLabel(string text, float width) {
			GUILayout.Label(text, SRApi.Ctl.Label, GUILayout.Width(W(width)), GUILayout.Height(W(24)));
		}

		private static void RenderRow(BackupFile b) {
			if (b == null) return;
			GUILayout.BeginHorizontal();
			try {
				string timeText = b.Time == DateTime.MinValue ? "-" : b.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
				string tip = SRApi.T("路径：", "Path: ") + b.FilePath
					+ "\n" + SRApi.T("键数：", "Keys: ") + (b.KeyCount >= 0 ? b.KeyCount.ToString() : "?")
					+ (b.PreImport ? "\n" + SRApi.T("这是导入前自动生成的保护备份", "automatic protection backup created before an import") : "");
				GUILayout.Label(timeText, SRApi.Ctl.Label, GUILayout.Width(W(120)), GUILayout.Height(W(24)));
				GUILayout.Label(new GUIContent(b.FileName, tip), SRApi.Ctl.Label, GUILayout.Width(W(230)), GUILayout.Height(W(24)));
				GUILayout.Label(FormatSize(b.Size), SRApi.Ctl.Label, GUILayout.Width(W(64)), GUILayout.Height(W(24)));
				GUILayout.Label(b.KeyCount >= 0 ? b.KeyCount.ToString() : "?", SRApi.Ctl.Label, GUILayout.Width(W(48)), GUILayout.Height(W(24)));
				GUILayout.FlexibleSpace();
				string importKey = "imp:" + b.FilePath;
				string importLabel = IsPending(importKey) ? SRApi.T("确认导入", "Confirm") : SRApi.T("导入", "Import");
				GUIContent importContent = new GUIContent(importLabel,
					SRApi.T("把这个备份逐键写回当前配置；导入前会自动先备份当前配置，类型不匹配的键跳过，未知键列为孤儿键。\n需要点两次：第一次变成「确认导入」，5 秒内再点一次才真正导入。",
						"writes this backup back into the current config key by key; the current config is backed up automatically first, type-mismatched keys are skipped and unknown keys are reported as orphans.\nneeds two clicks: the first turns the button into Confirm, a second click within 5 seconds performs the import"));
				if (GUILayout.Button(importContent, SRApi.Ctl.Btn, GUILayout.Width(W(64)), GUILayout.Height(W(24)))) {
					if (ConfirmOrArm(importKey)) ImportBackup(b);
				}
				string delKey = "del:" + b.FilePath;
				string delLabel = IsPending(delKey) ? SRApi.T("确认删除", "Confirm") : SRApi.T("删除", "Delete");
				GUIContent delContent = new GUIContent(delLabel,
					SRApi.T("删除这份备份文件；需要点两次：第一次变成「确认删除」，5 秒内再点一次才真正删除。",
						"deletes this backup file; needs two clicks: the first turns the button into Confirm, a second click within 5 seconds deletes it"));
				if (GUILayout.Button(delContent, SRApi.Ctl.Btn, GUILayout.Width(W(64)), GUILayout.Height(W(24)))) {
					if (ConfirmOrArm(delKey)) DeleteBackup(b);
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

		// ---------- 导出 ----------

		private static void ExportNow() {
			try {
				if (!ActionAllowed()) return;
				string path = ExportCurrent("", true);
				if (path == null) {
					SRApi.Notify(SRApi.T("导出配置失败（详见日志）", "Failed to export the config (see the log)"));
					return;
				}
				_status = SRApi.T("已导出配置：", "Exported config: ") + path;
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/立即导出", __ex);
			}
		}

		// 把当前配置写成一份 JSON。baseName 为空时用时间戳；导入前的保护备份传入 "pre-import-<时间>"。
		// prune=true 时按 Max Backups 清理最旧备份；返回写出的绝对路径，失败返回 null（调用方决定是否中止后续动作）。
		private static string ExportCurrent(string baseName, bool prune) {
			try {
				string dir = BackupRoot();
				Directory.CreateDirectory(dir);
				List<ConfigEntryBase> entries = AllEntries();
				if (entries.Count == 0) {
					SRApi.Notify(SRApi.T("没有可导出的配置条目", "There are no config entries to export"));
					return null;
				}
				StringBuilder sb = new StringBuilder();
				sb.Append("{\n  \"version\": ").Append(BackupVersion)
					.Append(",\n  \"exportedAt\": \"").Append(JsonEsc(TimeIso(DateTime.UtcNow)))
					.Append("\",\n  \"entries\": [\n");
				int written = 0;
				for (int i = 0; i < entries.Count; i++) {
					ConfigEntryBase e = entries[i];
					if (e == null || e.Definition == null) continue;
					string sec = e.Definition.Section ?? "";
					string key = e.Definition.Key ?? "";
					string type = TypeName(e.SettingType);
					string val = ValueToString(SafeBoxedValue(e));
					// 逗号前置：跳过无效条目时不会留下尾逗号（保证输出是合法 JSON）。
					if (written > 0) sb.Append(",\n");
					sb.Append("    {\"section\":\"").Append(JsonEsc(sec))
						.Append("\",\"key\":\"").Append(JsonEsc(key))
						.Append("\",\"type\":\"").Append(JsonEsc(type))
						.Append("\",\"value\":\"").Append(JsonEsc(val)).Append("\"}");
					written++;
				}
				sb.Append("\n  ]\n}\n");
				if (written == 0) {
					SRApi.Notify(SRApi.T("没有可导出的配置条目", "There are no config entries to export"));
					return null;
				}
				if (string.IsNullOrEmpty(baseName)) baseName = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				string path = UniquePath(Path.Combine(dir, baseName + ".json"));
				File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
				SRApi.LogInfo("[配置工具] 已导出 " + written + " 条配置 -> " + path);
				if (prune) PruneBackups();
				else RefreshList();
				return path;
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/导出", __ex);
				return null;
			}
		}

		// ---------- 导入 ----------

		private static void ImportBackup(BackupFile b) {
			string prePath = null;
			try {
				if (b == null) return;
				if (!ActionAllowed()) return;
				// 先完整读入并解析目标备份（校验失败时不做任何写入）。
				string text = ReadAllText(b.FilePath);
				if (string.IsNullOrEmpty(text)) {
					SRApi.Notify(SRApi.T("读取备份文件失败", "Failed to read the backup file"));
					return;
				}
				List<BackupEntry> records;
				if (!ParseBackup(text, out records) || records.Count == 0) {
					SRApi.Notify(SRApi.T("备份文件格式不正确（不是本功能导出的 JSON）", "The backup file format is invalid (not a JSON exported by this feature)"));
					return;
				}
				// 安全：导入前先自动备份当前配置，失败则中止导入（没有回滚点就不动手）。
				prePath = ExportCurrent("pre-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), false);
				_lastPreImportPath = prePath;
				if (prePath == null) {
					SRApi.Notify(SRApi.T("导入已取消：自动备份当前配置失败，没有可回滚的备份", "Import cancelled: failed to back up the current config, so there is no rollback point"));
					return;
				}
				// 当前条目索引：section + "\t" + key 唯一。
				List<ConfigEntryBase> entries = AllEntries();
				Dictionary<string, ConfigEntryBase> map = new Dictionary<string, ConfigEntryBase>(StringComparer.Ordinal);
				for (int i = 0; i < entries.Count; i++) {
					ConfigEntryBase e = entries[i];
					if (e == null || e.Definition == null) continue;
					map[EntryKey(e.Definition.Section, e.Definition.Key)] = e;
				}
				int written = 0;
				List<string> orphans = new List<string>();
				List<string> typeMismatch = new List<string>();
				List<string> parseFailed = new List<string>();
				for (int i = 0; i < records.Count; i++) {
					BackupEntry r = records[i];
					if (r == null) continue;
					ConfigEntryBase e;
					if (!map.TryGetValue(EntryKey(r.Section, r.Key), out e) || e == null) {
						orphans.Add(DisplaySection(r.Section) + " / " + r.Key);
						continue;
					}
					string curType = TypeName(e.SettingType);
					if (!string.IsNullOrEmpty(r.Type) && !string.Equals(r.Type, curType, StringComparison.Ordinal)) {
						typeMismatch.Add(DisplaySection(r.Section) + " / " + r.Key + "（" + r.Type + " ≠ " + curType + "）");
						continue;
					}
					object val;
					string perr;
					if (!TryParseValue(e.SettingType, r.Value, out val, out perr)) {
						parseFailed.Add(DisplaySection(r.Section) + " / " + r.Key + "（" + perr + "）");
						continue;
					}
					try {
						SRApi.Ctl.SetValue(e, val);
						written++;
					} catch (Exception __ex) {
						SRApi.Guard.Log("配置工具/写回 " + r.Section + "/" + r.Key, __ex);
						parseFailed.Add(DisplaySection(r.Section) + " / " + r.Key + "（写入异常）");
					}
				}
				// 立刻尝试落盘（失败也只是延后，核心 Tick 会再刷）。
				try { SRApi.Ctl.MarkConfigDirty(_config); } catch (Exception __ex) { SRApi.Guard.Log("配置工具/标记配置脏", __ex); }
				SRApi.FlushConfig();
				PruneBackups();
				BuildImportReport(b, records.Count, written, orphans, typeMismatch, parseFailed, prePath);
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/导入", __ex);
				string rollback = string.IsNullOrEmpty(prePath) ? _lastPreImportPath : prePath;
				_status = rollback == null
					? SRApi.T("导入失败（详见日志）", "Import failed (see the log)")
					: SRApi.T("导入失败，可用导入前备份回滚：", "Import failed; roll back with the pre-import backup: ") + rollback;
				SRApi.Notify(_status);
			}
		}

		// 生成状态行 + 孤儿键/跳过项清单（UI 展示），并汇总成一行用于 Notify。
		private static void BuildImportReport(BackupFile b, int total, int written,
			List<string> orphans, List<string> typeMismatch, List<string> parseFailed, string prePath) {
			try {
				int skipped = typeMismatch.Count + parseFailed.Count;
				string head = SRApi.T("已导入 ", "Imported ") + written + "/" + total
					+ SRApi.T(" 条；跳过类型不符 ", "; skipped type mismatch ") + typeMismatch.Count
					+ SRApi.T(" 条、解析失败 ", ", parse failure ") + parseFailed.Count
					+ SRApi.T(" 条；孤儿键 ", " keys; orphan keys ") + orphans.Count + SRApi.T(" 条", "");
				_status = head + SRApi.T("。导入前备份：", ". Pre-import backup: ") + prePath;
				StringBuilder sb = new StringBuilder();
				if (orphans.Count > 0) {
					sb.Append(SRApi.T("孤儿键（当前配置里不存在，已忽略）：", "Orphan keys (not present in the current config, ignored):"));
					AppendListLimited(sb, orphans);
				}
				if (typeMismatch.Count > 0) {
					if (sb.Length > 0) sb.Append('\n');
					sb.Append(SRApi.T("类型不匹配（已跳过）：", "Type mismatch (skipped):"));
					AppendListLimited(sb, typeMismatch);
				}
				if (parseFailed.Count > 0) {
					if (sb.Length > 0) sb.Append('\n');
					sb.Append(SRApi.T("解析/写入失败（已跳过）：", "Parse/write failure (skipped):"));
					AppendListLimited(sb, parseFailed);
				}
				if (skipped == 0 && orphans.Count == 0) {
					sb.Append(SRApi.T("全部键均已按类型写回。", "All keys were written back with matching types."));
				}
				_reportText = sb.ToString();
				SRApi.LogInfo("[配置工具] 导入 " + b.FileName + "：写入 " + written + "/" + total
					+ "，类型不符 " + typeMismatch.Count + "，解析失败 " + parseFailed.Count + "，孤儿键 " + orphans.Count);
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/导入报告", __ex);
			}
		}

		private static void AppendListLimited(StringBuilder sb, List<string> list) {
			int n = Mathf.Min(list.Count, ReportListLimit);
			for (int i = 0; i < n; i++) sb.Append("\n  - ").Append(list[i]);
			if (list.Count > n) sb.Append("\n  … " + SRApi.T("共 ", "total ") + list.Count + SRApi.T(" 条", ""));
		}

		private static void DeleteBackup(BackupFile b) {
			try {
				if (b == null) return;
				if (!ActionAllowed()) return;
				try {
					if (File.Exists(b.FilePath)) File.Delete(b.FilePath);
				} catch (Exception __ex) {
					SRApi.Guard.Log("配置工具/删除备份", __ex);
					SRApi.Notify(SRApi.T("删除备份失败", "Failed to delete the backup"));
					return;
				}
				RefreshList();
				_status = SRApi.T("已删除备份：", "Deleted backup: ") + b.FileName;
				SRApi.Notify(_status);
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/删除", __ex);
			}
		}

		// ---------- 备份文件扫描 / 清理 ----------

		private static void RefreshList() {
			_loaded = true;
			_backups.Clear();
			try {
				string dir = BackupRoot();
				if (!Directory.Exists(dir)) return;
				string[] files = Directory.GetFiles(dir, "*.json");
				for (int i = 0; i < files.Length; i++) {
					try {
						FileInfo fi = new FileInfo(files[i]);
						BackupFile b = new BackupFile();
						b.FilePath = files[i];
						b.FileName = Path.GetFileName(files[i]);
						b.Size = fi.Exists ? fi.Length : 0;
						b.Time = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
						b.PreImport = b.FileName.StartsWith("pre-import-", StringComparison.OrdinalIgnoreCase);
						b.KeyCount = CountKeysInFile(files[i]);
						_backups.Add(b);
					} catch (Exception __ex) {
						SRApi.Guard.Log("配置工具/读取备份元数据", __ex);
					}
				}
				_backups.Sort(delegate(BackupFile a, BackupFile c) {
					int r = c.Time.CompareTo(a.Time);
					if (r != 0) return r;
					return string.CompareOrdinal(c.FileName, a.FileName);
				});
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/扫描备份目录", __ex);
			}
		}

		// 粗数键数：本功能导出的格式每条记录一行且恰好出现一次 "section":，字符串里的引号都会被转义，
		// 所以不会误命中值内容。
		private static int CountKeysInFile(string path) {
			try {
				string[] lines = File.ReadAllLines(path, Encoding.UTF8);
				int n = 0;
				for (int i = 0; i < lines.Length; i++) {
					if (lines[i] != null && lines[i].IndexOf("\"section\":", StringComparison.Ordinal) >= 0) n++;
				}
				return n;
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/统计键数", __ex);
				return -1;
			}
		}

		private static void PruneBackups() {
			try {
				string dir = BackupRoot();
				if (!Directory.Exists(dir)) { RefreshList(); return; }
				string[] files = Directory.GetFiles(dir, "*.json");
				int keep = MaxBackups;
				if (files.Length <= keep) { RefreshList(); return; }
				Array.Sort(files, delegate(string a, string c) {
					try { return File.GetLastWriteTimeUtc(c).CompareTo(File.GetLastWriteTimeUtc(a)); }
					catch { return string.CompareOrdinal(c, a); }
				});
				int removed = 0;
				for (int i = keep; i < files.Length; i++) {
					try {
						File.Delete(files[i]);
						removed++;
					} catch (Exception __ex) {
						SRApi.Guard.Log("配置工具/清理旧备份", __ex);
					}
				}
				if (removed > 0) SRApi.LogInfo("[配置工具] 备份数量超过上限 " + keep + "，已清理最旧的 " + removed + " 份");
				RefreshList();
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/清理备份", __ex);
			}
		}

		// ---------- 配置枚举 ----------

		// 枚举本模组 ConfigFile 的全部条目。
		// 契约没有直接暴露"全量条目"接口，但 IFeatureHost.Config 是契约注入的 ConfigFile（BepInEx 公开类型），
		// 与核心 SR.Settings.AllEntries 同一做法：优先 ConfigFile 显式实现的 IDictionary.Values，
		// 旧版 BepInEx 退回已过时的 GetConfigEntries()。只覆盖本模组自己的 cfg，不含其它插件的 ConfigFile。
		private static List<ConfigEntryBase> AllEntries() {
			List<ConfigEntryBase> list = new List<ConfigEntryBase>();
			ConfigFile cfg = _config;
			if (cfg == null) return list;
			try {
				IDictionary<ConfigDefinition, ConfigEntryBase> dict = cfg as IDictionary<ConfigDefinition, ConfigEntryBase>;
				if (dict != null) {
					foreach (ConfigEntryBase e in dict.Values) {
						if (e != null) list.Add(e);
					}
					return list;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/枚举条目(字典)", __ex);
			}
#pragma warning disable CS0618 //旧版 BepInEx 兜底
			try {
				ConfigEntryBase[] arr = cfg.GetConfigEntries();
				if (arr != null) {
					for (int i = 0; i < arr.Length; i++) if (arr[i] != null) list.Add(arr[i]);
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/枚举条目(旧接口)", __ex);
			}
#pragma warning restore CS0618
			return list;
		}

		private static object SafeBoxedValue(ConfigEntryBase e) {
			try { return e != null ? e.BoxedValue : null; } catch (Exception __ex) { SRApi.Guard.Log("配置工具/读配置值", __ex); return null; }
		}

		private static string EntryKey(string section, string key) {
			return (section ?? "") + "\t" + (key ?? "");
		}

		private static string DisplaySection(string section) {
			return string.IsNullOrEmpty(section) ? "(General)" : section;
		}

		private static string TypeName(Type t) {
			try {
				if (t == null) return "";
				return t.FullName ?? t.Name;
			} catch { return ""; }
		}

		private static string ValueToString(object v) {
			try {
				if (v == null) return "";
				if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
				if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
				return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
			} catch { return ""; }
		}

		// 按目标条目的类型解析备份里的字符串值。返回 false 时 perr 给出简短原因。
		private static bool TryParseValue(Type t, string s, out object value, out string perr) {
			value = null;
			perr = "";
			if (t == null) { perr = SRApi.T("未知类型", "unknown type"); return false; }
			try {
				if (t == typeof(string)) {
					value = s ?? "";
					return true;
				}
				if (t == typeof(bool)) {
					if (string.IsNullOrEmpty(s)) { value = false; return true; }
					bool b;
					if (bool.TryParse(s.Trim(), out b)) { value = b; return true; }
					if (s.Trim() == "1") { value = true; return true; }
					if (s.Trim() == "0") { value = false; return true; }
					perr = SRApi.T("不是布尔值", "not a boolean");
					return false;
				}
				if (t == typeof(int)) {
					int i;
					if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) { value = i; return true; }
					perr = SRApi.T("不是整数", "not an integer");
					return false;
				}
				if (t == typeof(float)) {
					float f;
					if (float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) { value = f; return true; }
					perr = SRApi.T("不是小数", "not a number");
					return false;
				}
				if (t == typeof(double)) {
					double d;
					if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { value = d; return true; }
					perr = SRApi.T("不是小数", "not a number");
					return false;
				}
				if (t == typeof(long)) {
					long l;
					if (long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) { value = l; return true; }
					perr = SRApi.T("不是整数", "not an integer");
					return false;
				}
				if (t.IsEnum) {
					// Enum.Parse 同时接受名字与数字串，且大小写不敏感：KeyCode / 各功能枚举都能回填。
					value = Enum.Parse(t, (s ?? "").Trim(), true);
					return true;
				}
				value = Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
				return true;
			} catch (Exception __ex) {
				perr = __ex.GetType().Name;
				return false;
			}
		}

		// ---------- 备份 JSON 解析 ----------

		// 只解析本功能写出的固定格式：一个 { ... "entries": [ 每行一条 {"section"/"key"/"type"/"value"} ] }。
		// 每行一条记录使得解析无需完整 JSON 库；值里的换行/引号在导出时已转义，不会破坏行结构。
		private static bool ParseBackup(string text, out List<BackupEntry> records) {
			records = new List<BackupEntry>();
			try {
				if (string.IsNullOrEmpty(text)) return false;
				if (text.IndexOf("\"entries\"", StringComparison.Ordinal) < 0) return false;
				string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
				for (int i = 0; i < lines.Length; i++) {
					string line = lines[i];
					if (line == null) continue;
					if (line.IndexOf("\"section\":", StringComparison.Ordinal) < 0) continue;
					BackupEntry r = new BackupEntry();
					r.Section = JsonStr(line, "section");
					r.Key = JsonStr(line, "key");
					r.Type = JsonStr(line, "type");
					r.Value = JsonStr(line, "value");
					if (r.Key == null) continue; // 记录不完整：跳过损坏行
					records.Add(r);
				}
				return records.Count > 0;
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/解析备份", __ex);
				return false;
			}
		}

		// 轻量 JSON 字符串字段解析：本功能写出的格式固定为 "key":"值"，值里的引号/反斜杠/控制字符已转义。
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
						case 'b': sb.Append('\b'); break;
						case 'f': sb.Append('\f'); break;
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

		// 与 JsonStr 成对：只转义会破坏单行结构/JSON 语法的字符，中文原样输出。
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
						case '\b': sb.Append("\\b"); break;
						case '\f': sb.Append("\\f"); break;
						default:
							if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
							else sb.Append(c);
							break;
					}
				}
				return sb.ToString();
			} catch { return ""; }
		}

		// ---------- 路径 / 时间 / 显示辅助 ----------

		// BepInEx.Paths 是 BepInEx 的公开类型，功能层允许引用（契约边界只禁止引用 SR.* 实现层）。
		private static string BackupRoot() {
			if (!string.IsNullOrEmpty(_rootDir)) return _rootDir;
			try {
				_rootDir = Path.Combine(Paths.ConfigPath, "SR_UCH", "config-backup");
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/定位备份目录", __ex);
				// 极端情况下 BepInEx.Paths 不可用：退回临时目录，至少不让功能整个崩掉。
				_rootDir = Path.Combine(Path.GetTempPath(), "SR_UCH_config-backup");
			}
			return _rootDir;
		}

		// 同一秒内重复导出时避免覆盖：追加 -1、-2… 后缀。
		private static string UniquePath(string path) {
			try {
				if (!File.Exists(path)) return path;
				string dir = Path.GetDirectoryName(path);
				string name = Path.GetFileNameWithoutExtension(path);
				for (int i = 1; i < 100; i++) {
					string p = Path.Combine(dir, name + "-" + i + ".json");
					if (!File.Exists(p)) return p;
				}
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/生成唯一文件名", __ex);
			}
			return path;
		}

		private static string ReadAllText(string path) {
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
				return File.ReadAllText(path, Encoding.UTF8);
			} catch (Exception __ex) {
				SRApi.Guard.Log("配置工具/读文件", __ex);
				return null;
			}
		}

		private static string TimeIso(DateTime utc) {
			try {
				if (utc == DateTime.MinValue) utc = DateTime.UtcNow;
				return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
			} catch { return ""; }
		}

		private static string FormatSize(long bytes) {
			try {
				if (bytes < 1024) return bytes + " B";
				return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
			} catch { return "?"; }
		}
	}
}
