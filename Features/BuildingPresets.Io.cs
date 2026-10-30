// 布局预设 · 存储与导入导出（BuildingPresets 分部）：保存 / 导入 / 导出 / 删除 / 重命名、
// 快照获取、预设目录与索引（index.json）读写、游戏本地自定义关卡扫描。
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

    public partial class BuildingPresets {

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
                    name = baseName + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
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
                if (!string.IsNullOrEmpty(path)) {
                    // 只接受预设支持的两类扩展名：这两类都是 XML/压缩快照，其它文件读进来只会白解析。
                    string ext = Path.GetExtension(path) ?? "";
                    if (!ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".snapshot", StringComparison.OrdinalIgnoreCase)) {
                        SRApi.Notify(SRApi.T("只能导入 .xml 或 .snapshot 文件", "Only .xml or .snapshot files can be imported"));
                        return;
                    }
                }
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
                try { defaultName = Path.GetFileNameWithoutExtension(path); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                if (name.Length == 0) name = SRApi.T("导入 ", "Imported ") + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
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
                try { if (File.Exists(input)) return Path.GetFullPath(input); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                string p = null;
                try { p = Path.Combine(Paths.BepInExRootPath, input); if (File.Exists(p)) return p; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                try { p = Path.Combine(Paths.ConfigPath, input); if (File.Exists(p)) return p; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                try {
                // 先净化文件名，再取全路径并确认仍在预设根目录内（防目录穿越）。
                string safe = SafeFileName(input);
                string root2 = Path.GetFullPath(PresetsRoot());
                string full = Path.GetFullPath(Path.Combine(root2, safe));
                if (full.StartsWith(root2, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                if (File.Exists(p.FilePath)) File.Delete(p.FilePath);
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
                // 回写 XML 的 srName：XML 是权威数据源，只改索引的话，换场景重扫（LoadFromDisk 从 XML 回填）
                // 会把名字退回旧的 —— 这是"重命名后名字自己变回去"的根因。
                try {
                    string xmlPath = p.FilePath;
                    if (!string.IsNullOrEmpty(xmlPath) && File.Exists(xmlPath)) {
                        string text = File.ReadAllText(xmlPath);
                        XmlDocument doc = ParseXmlSafe(text);
                        if (doc != null && doc.DocumentElement != null) {
                            doc.DocumentElement.SetAttribute("srName", name);
                            WriteFileAtomic(xmlPath, doc.OuterXml);
                        }
                    }
                } catch (Exception __wx) { SRApi.Guard.Log("布局预设/重命名回写XML", __wx); }
                _renameKey = null;
                _renameText = null;
                SaveIndex();
                _status = SRApi.T("已重命名预设：", "Renamed preset: ") + name;
                SRApi.LogInfo("[布局预设] " + _status);
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/重命名", __ex);
            }
        }


        // 原子写文本文件：同目录写 .tmp，成功后替换目标；失败时保留原文件（绝不留下半截文件）。
        private static void WriteFileAtomic(string path, string text) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text ?? "", new UTF8Encoding(false));
                if (File.Exists(path)) {
                    try { File.Replace(tmp, path, null); return; }
                    catch (Exception __rp) { SRApi.Guard.Log("布局预设/原子替换", __rp); }
                }
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch (Exception __dt) { SRApi.Guard.Log("布局预设/删除临时文件", __dt); }
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/原子写", __ex); }
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
                            pi.ModsMode = JsonStr(line, "modsMode") ?? "";
                            pi.ApplyRules = JsonBool(line, "applyRules");
                            pi.ApplyPoints = JsonBool(line, "applyPoints");
                            pi.ApplyBlocks = JsonBool(line, "applyBlocks");
                            ParseModsVals(pi, JsonStr(line, "modsVals"));
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
                    // rulesets 是规则/积分/模块概率的侧车目录，不是预设；exporse 同理。
                    if (string.Equals(mode, "rulesets", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(mode, "exports", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] files = Directory.GetFiles(dir, "*.xml");
                    for (int j = 0; j < files.Length; j++) {
                        string fp = files[j];
                        string file = Path.GetFileName(fp);
                        if (file.EndsWith(".ruleset.xml", StringComparison.OrdinalIgnoreCase)) continue;   // 旧版侧车
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
        // 加载前按该预设的修改器策略改写 <mods>（继承时原样返回）。
        private static string ReadPresetXml(PresetInfo p) {
            string xml = ReadPresetXmlRaw(p);
            return string.IsNullOrEmpty(xml) ? xml : ApplyModsPolicy(xml, p);
        }

        // 原样读取（不带修改器策略）：编辑器打开时用它读取预设自带的修改器值做草稿。
        private static string ReadPresetXmlRaw(PresetInfo p) {
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
                        .Append("\",\"ok\":").Append(p.Ok ? "true" : "false")
                        .Append(",\"applyRules\":").Append(p.ApplyRules ? "true" : "false")
                        .Append(",\"applyPoints\":").Append(p.ApplyPoints ? "true" : "false")
                        .Append(",\"applyBlocks\":").Append(p.ApplyBlocks ? "true" : "false")
                        .Append(",\"modsMode\":\"").Append(JsonEsc(p.ModsMode ?? ""))
                        .Append("\",\"modsVals\":\"").Append(JsonEsc(ModsValsToText(p))).Append("\"}");
                }
                if (!first) sb.Append('\n');
                sb.Append("  ]\n}\n");
                // 原子写：先写 .tmp 再替换。直接 WriteAllText 是"截断+写入"，中途崩溃/磁盘满会留下半截 JSON，
                // 下次启动就只剩流式扫描的元数据（modsVals / applyRules 等自定义数据全丢）。
                WriteFileAtomic(Path.Combine(root, IndexFileName), sb.ToString());
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return null;
        }

        private static void SortPresets() {
            try {
                _presets.Sort(ComparePreset);
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                if (s.Length == 0) return "UNKNOWN";
                // 归一化：具体的 GameMode 名统一大写（PARTY/CREATIVE/...），其它（imported / unknown 等）
                // 一律折成 UNKNOWN，避免 "IMPORTED" 与 "imported" 各自建一个目录（audit A-10）。
                string upper = s.ToUpperInvariant();
                if (upper == "PARTY" || upper == "CREATIVE" || upper == "CHALLENGE" || upper == "FREEPLAY") return upper;
                return "UNKNOWN";
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


    }
}
