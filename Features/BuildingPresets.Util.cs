// 布局预设 · 辅助：XML 统计与截取、时间格式化、模式/关卡显示名、index.json 轻量读写。
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

        // 分区：XML / 时间 / JSON 辅助

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

        // 流式读取预设元数据：用 XmlReader 取根属性 levelSceneName、顶层 block 数与展示名，避免整份建 DOM。
        // 统一安全解析入口：禁 DTD（防 XXE）+ 不解析外部引用；预设 XML 是不可信输入，必须走这里。
        private static XmlDocument ParseXmlSafe(string xml) {
            // 上限与导入一致：超大输入直接拒绝，避免主线程卡在 DOM 构建。
            if (string.IsNullOrEmpty(xml) || xml.Length > MaxImportChars) return null;
            // 四层防护的唯一策略源：禁 DTD / ENTITY，且根节点必须是 <scene>。
            // XmlReaderSettings 禁 DTD 属解析层；根名与 DTD/ENTITY 策略统一由 Pure 层判定。
            if (!SR_UCH.Pure.PresetXmlPolicy.ForbidsDtd(xml)) return null;
            if (!SR_UCH.Pure.PresetXmlPolicy.RootOk(xml)) return null;
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
                    // 跳过声明/注释，定位根元素
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
                    // 自闭合 <scene .../>（空预设）：根属性已读完，须按成功返回，否则元数据全丢。
                    if (reader.IsEmptyElement) return true;
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

        // 剥掉 <destroyed>：新场景会重建关卡自带件，游戏解析时遇到会中断整批加载。
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return DateTime.MinValue;
        }

        // 模式名：优先用游戏本地化术语（跟随语言设置），取不到回退界面文案。
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
                        case GameState.GameMode.PARTY: return SRApi.Tid("97c0e6938b", "派对", "Party");
                        case GameState.GameMode.CREATIVE: return SRApi.Tid("44d9d5345b", "创意", "Creative");
                        case GameState.GameMode.FREEPLAY: return SRApi.Tid("ebb2dba278", "自由", "Freeplay");
                        case GameState.GameMode.CHALLENGE: return SRApi.Tid("0cce1e0303", "挑战", "Challenge");
                    }
                }
                return mode;
            } catch { return mode; }
        }

        // 模式名对应的本地化术语（与规则书 / 树屋界面一致）。
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

        // 关卡显示名：优先保存时的展示名，否则按场景名解析（BlankLevel 为「自定义关卡」），兜底非空占位。
        private static string LevelText(PresetInfo p) {
            try {
                if (p == null) return SRApi.Tid("fa9b6bf383", "未知关卡", "Unknown level");
                if (!string.IsNullOrEmpty(p.LevelName)) return p.LevelName;
                return LevelDisplayNameForScene(p.Level);
            } catch { return SRApi.Tid("fa9b6bf383", "未知关卡", "Unknown level"); }
        }

        private static string LevelDisplayNameForScene(string scene) {
            try {
                if (string.IsNullOrEmpty(scene)) return SRApi.Tid("fa9b6bf383", "未知关卡", "Unknown level");
                if (string.Equals(scene, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return SRApi.Tid("756f1de77d", "自定义关卡", "Custom level");
                return LocalizedLevelName(scene);
            } catch { return string.IsNullOrEmpty(scene) ? SRApi.Tid("fa9b6bf383", "未知关卡", "Unknown level") : scene; }
        }

        // 当前对局若是自定义关卡，返回其名字；否则 null。
        private static string CurrentCustomLevelName() {
            try {
                GameState gs = GameState.GetInstance();
                if (gs == null) return null;
                LevelSelectController.PlayedSnapshotInfo info = gs.currentSnapshotInfo;
                string nm = info.snapshotName;
                return string.IsNullOrEmpty(nm) ? null : nm;
            } catch { return null; }
        }

        // 保存用关卡名：自定义关卡取当前关卡名（XML 里只有 BlankLevel），普通关卡用本地化名。
        private static string CurrentLevelDisplayName(string scene) {
            try {
                string active = "";
                try { active = SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (string.Equals(active, "BlankLevel", StringComparison.OrdinalIgnoreCase)) {
                    string custom = CurrentCustomLevelName();
                    if (!string.IsNullOrEmpty(custom)) return custom;
                    return SRApi.Tid("756f1de77d", "自定义关卡", "Custom level");
                }
                return LevelDisplayNameForScene(string.IsNullOrEmpty(active) ? scene : active);
            } catch { return LevelDisplayNameForScene(scene); }
        }

        // 关卡代码：优先快照码，内置关卡回退场景名（BlankLevel 返回空 -> 显示「-」）。
        private static string LevelCodeText(PresetInfo p) {
            try {
                if (p == null) return "-";
                if (!string.IsNullOrEmpty(p.LevelCode)) return p.LevelCode;
                if (!string.IsNullOrEmpty(p.Level)
                    && !string.Equals(p.Level, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return p.Level;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return "-";
        }

        private static string LevelCodeForScene(string scene) {
            try {
                if (string.IsNullOrEmpty(scene)) return "";
                if (string.Equals(scene, "BlankLevel", StringComparison.OrdinalIgnoreCase)) return "";
                return scene;
            } catch { return ""; }
        }

        // 保存用关卡代码：自定义关卡取快照码，内置关卡取场景名。
        private static string CurrentLevelCode(string scene) {
            try {
                string active = "";
                try { active = SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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

        // 场景名 -> 关卡枚举；并判当前对局是否即该预设的自定义关卡（比快照名/代码，空视为不同）。
        private static bool SameCustomLevel(PresetInfo p, GameState gs) {
            try {
                if (p == null || gs == null) return false;
                LevelSelectController.PlayedSnapshotInfo cur = gs.currentSnapshotInfo;
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

        // 本地化关卡名；读不到或枚举不识别时回退场景名。
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

        // 从 XML 取关卡代码：根属性或 <snapshotInfo code>；都没有时内置关卡回退场景名。
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

        // 只有真实对局模式才参与模式匹配校验：REPLAY / IMPORTED / UNKNOWN 无法比较。
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

        // index.json 每行一条、按行轻量解析；解析失败退回文件扫描结果，不丢预设。
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

        // 读布尔字段：缺字段或格式不同一律 false（兼容旧索引）。
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
