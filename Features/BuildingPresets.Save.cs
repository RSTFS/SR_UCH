// 布局预设 · 保存 / 导出 / 落盘（保存当前布局、导出预设、写文件与索引、排序、安全文件名）。
// 从 BuildingPresets.Io.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
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
                    sb.Append("    {\"schema\":").Append(IndexSchema).Append(",\"file\":\"").Append(JsonEsc(p.File))
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
            // 显式拒绝路径分隔符与 ..：某些平台 / 是合法文件名字符，会原样保留（audit 11-P0-1）。
            s = s.Replace("..", "_").Replace('/', '_').Replace('\\', '_');
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
    }
}
