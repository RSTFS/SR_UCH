// 配置迁移：把旧版 cfg 的中文 section/key 改为英文，并清理已删功能的配置段。
// 必须在任何 Config.Bind 之前运行；改写后由调用方 ConfigFile.Reload()。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    public static partial class ConfigMigration {
        // 已删除功能的 cfg 段名：启动时统一 NavHide，侧栏不再显示。
        public static readonly string[] DeletedSections = new string[] {
            "Saved Lobby Details",
            "Camera",
            "Respawn",
            "Support",
            "Reflection",
            "Freeplay",
            "Config Tools",
            "Match Brief", "Accessibility", "Host Console",
            "Level Recorder", "Match Profile", "Match Cast", "Time Control", "Death Report",
        };

        private static readonly Dictionary<string, string> SectionMap = new Dictionary<string, string> {
            { "设置", "Settings" },
            { "首页", "Home" },
            { "快速调整", "Quick Adjust" },
            { "关卡", "Level" },
            { "地图", "Freeplay" },
            { "视野", "Camera" },
            { "实验", "Experiments" },
            { "会话内容", "Chat" },
            { "Treehouse Suicide", "Quick Adjust" },
            { "Round HUD", "HUD" },
            // 早期中文段名（与各功能 LocSec 中文名一致）：缺了它们对应段与键都不识别。
            // 注意：[自由模式] 是活段，不可当旧名改到 Freeplay，否则每次启动都会搬走其键。
            { "联机", "Online" },
            { "建造", "build" },
            { "Builder Enhancements", "build" }, // 段名改名
            { "聊天窗口", "Chat Window" },
            { "方块破坏", "Destroy Blocks" },
            { "重生", "Respawn" },
            { "移动轨迹", "Player Tracker" },
        };

        // key 名改名表（行首“键名 = 值”）：旧中文 -> 新英文。
        private static readonly Dictionary<string, string> KeyMap = new Dictionary<string, string> {
            { "解除建造上限", "Lift Build Cap" },
            { "上限数值", "Build Cap Value" },
            { "自由相机", "Free Camera" },
            { "地图总开关", "Map Enabled" },
            { "过滤快捷消息", "Filter Quick Msgs" },
            { "显示时间", "Show Time" },
            { "加载后清理", "GC After Load" },
            // 联机快捷键旧 id（cc.）改为 online.，[Hotkeys] 键名即该 id；cc.mainmenu 并入 online.disband。
            { "cc.lobbies", "online.lobbies" },
            { "cc.disband", "online.disband" },
            { "cc.mainmenu", "online.disband" },
        };

        // 组合键条目内层“<section> <key>”改名：先匹配段再改键，自注册表 _xSections/_xKeys 优先。
        private static string RenameComboInner(string rest) {
            foreach (KeyValuePair<string, string> kv in _xSections) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            foreach (KeyValuePair<string, string> kv in SectionMap) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            // 段名未变、仅 key 改名：按首个空格切段后查 KeyMap。
            int sp = rest.IndexOf(' ');
            if (sp > 0) {
                string tail = RenameKeyIn(rest.Substring(sp + 1));
                if (tail != rest.Substring(sp + 1)) return rest.Substring(0, sp + 1) + tail;
            }
            return RenameKeyIn(rest);
        }

        private static string RenameKeyIn(string key) {
            string to;
            if (_xKeys.TryGetValue(key, out to)) return to; // 功能自注册的键改名优先
            return KeyMap.TryGetValue(key, out to) ? to : key;
        }

        // 把 from 段的 key 行搬到 to 段（幂等：源段无此键则原样返回）。
        private static string MoveKeyBetweenSections(string text, string from, string to, string key) {
            try {
                _appliedMoves.Add(new[] { from, key }); // 登记规则（供冲突自检，与文件里是否命中无关）
                string[] lines = text.Split('\n');
                int fromHdr = -1, toHdr = -1, keyLine = -1;
                string cur = "";
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        if (cur == from) fromHdr = i;
                        if (cur == to) toHdr = i;
                        continue;
                    }
                    int eq = t.IndexOf('=');
                    if (eq > 0 && cur == from && t.Substring(0, eq).TrimEnd() == key) keyLine = i;
                }
                if (keyLine < 0 || fromHdr < 0) return text;
                string moved = lines[keyLine];
                List<string> outp = new List<string>();
                for (int i = 0; i < lines.Length; i++) if (i != keyLine) outp.Add(lines[i]);
                if (toHdr < 0) {
                    outp.Add("");
                    outp.Add("[" + to + "]");
                    outp.Add(moved);
                } else {
                    int insertAt = -1;
                    for (int i = 0; i < outp.Count; i++) {
                        if (outp[i].Trim() == "[" + to + "]") { insertAt = i + 1; break; }
                    }
                    if (insertAt < 0) {
                        outp.Add("");
                        outp.Add("[" + to + "]");
                        outp.Add(moved);
                    } else {
                        outp.Insert(insertAt, moved);
                    }
                }
                return string.Join("\n", outp.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 行搬移", __ex); return text; }
        }

        // 删除废弃键（幂等）：含 [Settings] 里对应的“组合键 <段> <键>”绑定行。
        private static string RemoveKeys(string text, string section, string[] keys) {
            try {
                if (keys != null) for (int __k = 0; __k < keys.Length; __k++) _appliedRemovals.Add(new[] { section, keys[__k] });
                // 名单手工维护且此处尚未 Bind、读不到活键，故逐条记录删除以便事后反查。
                if (_removedKeysLog != null && keys != null) {
                    for (int __k = 0; __k < keys.Length; __k++) _removedKeysLog.Add(section + "/" + keys[__k]);
                }
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                List<string> outp = new List<string>();
                string cur = "";
                bool changed = false;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        outp.Add(lines[i]);
                        continue;
                    }
                    {
                        int eq = t.IndexOf('=');
                        if (eq > 0) {
                            string k = t.Substring(0, eq).TrimEnd();
                            bool drop = false;
                            // 组合键条目统一 Bind 在 [Settings] 段，键名 = “组合键 <目标段> <目标键>”。
                            if (k.StartsWith("组合键 " + section + " ", StringComparison.Ordinal)) {
                                for (int j = 0; j < keys.Length; j++) {
                                    if (k == "组合键 " + section + " " + keys[j]) { drop = true; break; }
                                }
                            } else if (cur == section) {
                                for (int j = 0; j < keys.Length; j++) {
                                    if (k == keys[j]) { drop = true; break; }
                                }
                            }
                            if (drop) { changed = true; continue; }
                        }
                    }
                    outp.Add(lines[i]);
                }
                return changed ? string.Join("\n", outp.ToArray()) : text;
            } catch (Exception __ex) { SR.Guard.Log("cfg 废弃键清理", __ex); return text; }
        }

        // 把 from 段 keyFrom 的值写到 to 段 keyTo：仅当源有值、目标无此键（已有优先）。
        private static string MergeKeyValue(string text, string from, string keyFrom, string to, string keyTo) {
            try {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                string cur = "";
                int fromLine = -1, toLine = -1, toHdr = -1;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        if (cur == to) toHdr = i;
                        continue;
                    }
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).TrimEnd();
                    if (cur == from && k == keyFrom) fromLine = i;
                    if (cur == to && k == keyTo) toLine = i;
                }
                if (fromLine < 0 || toLine >= 0) return text; //源没有 / 目标已有（已有优先，不覆盖）
                int e0 = lines[fromLine].IndexOf('=');
                if (e0 <= 0) return text;
                string val = lines[fromLine].Substring(e0 + 1).Trim();
                // 空值 / None / 0 视为未设置，不搬。
                if (val.Length == 0 || val == "None" || val == "0") return text;
                List<string> outp = new List<string>(lines);
                string newLine = keyTo + " = " + val;
                if (toHdr < 0) {
                    outp.Add("");
                    outp.Add("[" + to + "]");
                    outp.Add(newLine);
                } else {
                    outp.Insert(toHdr + 1, newLine);
                }
                return string.Join("\n", outp.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 键值合并", __ex); return text; }
        }

        // 值经 map 变换后搬到 to 段并删源键（幂等）；用于须先裁剪旧值索引的迁移。
        private static string MoveMappedKey(string text, string from, string keyFrom, string to, string keyTo,
            Func<string, string> map) {
            try {
                _appliedMoves.Add(new[] { from, keyFrom }); // 登记规则，供启动后的冲突自检
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                string cur = "";
                int src = -1, dst = -1, toHdr = -1;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        if (cur == to) toHdr = i;
                        continue;
                    }
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).TrimEnd();
                    if (cur == from && k == keyFrom) src = i;
                    if (cur == to && k == keyTo) dst = i;
                }
                if (src < 0 || dst >= 0) return text; // 源没有 / 目标已有（已有优先，源留给 RemoveKeys 清理）
                int e0 = lines[src].IndexOf('=');
                if (e0 <= 0) return text;
                string val = lines[src].Substring(e0 + 1).Trim();
                string mapped = map != null ? map(val) : val;
                if (mapped == null) mapped = "";
                string newLine = keyTo + " = " + mapped;
                List<string> outp = new List<string>(lines.Length + 1);
                for (int i = 0; i < lines.Length; i++) if (i != src) outp.Add(lines[i]);
                if (toHdr < 0) {
                    outp.Add("");
                    outp.Add("[" + to + "]");
                    outp.Add(newLine);
                } else {
                    // 源行删除后段头可能前移，重新在 outp 中定位再插入。
                    int insertAt = -1;
                    for (int i = 0; i < outp.Count; i++) {
                        if (outp[i].Trim() == "[" + to + "]") { insertAt = i + 1; break; }
                    }
                    if (insertAt < 0) {
                        outp.Add("");
                        outp.Add("[" + to + "]");
                        outp.Add(newLine);
                    } else {
                        outp.Insert(insertAt, newLine);
                    }
                }
                return string.Join("\n", outp.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 键值迁移", __ex); return text; }
        }

        private const int OnlineV4ColCount = 12;
        private const int OnlineV5ColCount = 11;
        private const int OnlineV4SkillCol = 3;

        // v4 列宽（12 项）-> v5（11 项）：裁掉技能系数列；形状不符则原样搬。
        private static string MapOnlineV4ColWidths(string v) {
            try {
                if (string.IsNullOrEmpty(v)) return v;
                string[] p = v.Split(',');
                if (p.Length != OnlineV4ColCount) return v;   // 不是 v4 的 12 项形状：原样搬
                List<string> keep = new List<string>(OnlineV5ColCount);
                for (int i = 0; i < p.Length; i++) { if (i != OnlineV4SkillCol) keep.Add(p[i]); }
                return string.Join(",", keep.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 联机列宽迁移", __ex); return v; }
        }

        // v4 隐藏列索引 -> v5：去掉索引 3、大于 3 的减 1，再裁越界项与不可隐藏的操作列。
        private static string MapOnlineV4HiddenCols(string v) {
            try {
                if (string.IsNullOrEmpty(v)) return v;
                List<string> keep = new List<string>();
                foreach (string s in v.Split(',')) {
                    int k;
                    if (!int.TryParse(s.Trim(), out k)) continue;
                    if (k == OnlineV4SkillCol) continue;                 // 已删除的技能系数列
                    if (k > OnlineV4SkillCol) k -= 1;
                    if (k < 0 || k >= OnlineV5ColCount) continue;        // 越界项
                    if (k == OnlineV5ColCount - 1) continue;             // v5 的操作列不可隐藏
                    string t = k.ToString();
                    if (!keep.Contains(t)) keep.Add(t);
                }
                return string.Join(",", keep.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 联机隐藏列迁移", __ex); return v; }
        }

        // 键在段间搬家时同步改 [Settings] 里的组合键条目名，否则组合键失效。
        private static string MoveComboEntry(string text, string fromSec, string toSec, string key) {
            return MoveComboEntryTo(text, fromSec, key, toSec, key);
        }

        private static string MoveComboEntryTo(string text, string fromSec, string fromKey, string toSec, string toKey) {
            try {
                string oldName = "组合键 " + fromSec + " " + fromKey;
                string newName = "组合键 " + toSec + " " + toKey;
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                bool changed = false;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].TrimStart();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).TrimEnd() != oldName) continue;
                    int lead = lines[i].Length - t.Length;
                    lines[i] = lines[i].Substring(0, lead) + newName + " " + t.Substring(eq);
                    changed = true;
                }
                return changed ? string.Join("\n", lines) : text;
            } catch (Exception __ex) { SR.Guard.Log("组合键条目搬家", __ex); return text; }
        }

        // 清理旧命名热键：名为“组合键 <键>”、剩余部分不以任何段名开头且值为空的条目。
        // 值非空的不动（可能是用户手改）；条目上方紧邻的 # 注释一并删除。
        private static string PruneLegacyComboKeys(string text) {
            try {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                HashSet<string> sections = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < lines.Length; i++) {
                    string s = lines[i].Trim();
                    if (s.Length > 2 && s[0] == '[' && s[s.Length - 1] == ']') sections.Add(s.Substring(1, s.Length - 2));
                }
                List<string> keep = new List<string>(lines.Length);
                string cur = null;
                int removed = 0;
                for (int i = 0; i < lines.Length; i++) {
                    string ln = lines[i];
                    string s = ln.Trim();
                    if (s.Length > 2 && s[0] == '[' && s[s.Length - 1] == ']') {
                        cur = s.Substring(1, s.Length - 2);
                        keep.Add(ln);
                        continue;
                    }
                    if (cur == "Settings" && s.StartsWith("组合键 ", StringComparison.Ordinal)) {
                        string rest = s.Substring(4).Trim();   // "组合键 " = 3 字 + 空格
                        int eq = rest.IndexOf('=');
                        string name = eq >= 0 ? rest.Substring(0, eq).Trim() : rest;
                        string val = eq >= 0 ? rest.Substring(eq + 1).Trim() : "";
                        bool prefixed = false;
                        foreach (string sec in sections) {
                            if (name.StartsWith(sec + " ", StringComparison.Ordinal)) { prefixed = true; break; }
                        }
                        if (!prefixed && val.Length == 0) {
                            // 连同紧贴的 # 注释一起删（遇空行/段头停）。
                            while (keep.Count > 0) {
                                string back = keep[keep.Count - 1].Trim();
                                if (back.Length == 0 || back[0] != '#') break;
                                keep.RemoveAt(keep.Count - 1);
                            }
                            removed++;
                            continue;
                        }
                    }
                    keep.Add(ln);
                }
                if (removed > 0) SR.LogInfo("[配置迁移] 清理旧命名热键条目 " + removed + " 个（无分区前缀且值为空）");
                return string.Join("\n", keep);
            } catch (Exception __ex) { SR.Guard.Log("cfg 旧热键条目清理", __ex); return text; }
        }

        private static string DedupeSectionKeys(string text) {
            try {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                // 同名键只保留最后一条（后写覆盖先写，生效值不变）：先记每个 (段, 键) 末次行号。
                Dictionary<string, int> lastAt = new Dictionary<string, int>();
                string cur = "";
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        continue;
                    }
                    if (t.StartsWith("#", StringComparison.Ordinal)) continue;   //注释行（BepInEx 写的是 ##）
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    lastAt[cur + "\u0001" + t.Substring(0, eq).TrimEnd()] = i;
                }
                List<string> outp = new List<string>();
                cur = "";
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        outp.Add(lines[i]);
                        continue;
                    }
                    if (!t.StartsWith("#", StringComparison.Ordinal)) {
                        int eq = t.IndexOf('=');
                        if (eq > 0) {
                            int keep;
                            if (lastAt.TryGetValue(cur + "\u0001" + t.Substring(0, eq).TrimEnd(), out keep) && keep != i)
                                continue;   //同名键：不是最后一次出现 -> 丢（生效值在最后一条）
                        }
                    }
                    outp.Add(lines[i]);
                }
                string res = string.Join("\n", outp.ToArray());
                return res == text ? text : res;
            } catch (Exception __ex) { SR.Guard.Log("cfg 重复键清理", __ex); return text; }
        }

        // 记录本次实际应用的删/搬/改名，启动后与真正 Bind 的键比对报冲突；名单手工维护，撞上仍绑定的键会无报错丢设置。
        private static readonly List<string[]> _appliedRemovals = new List<string[]>();  // [sec,key]
        private static readonly List<string[]> _appliedMoves = new List<string[]>();     // [fromSec,key]
        private static readonly List<string> _appliedOldSections = new List<string>();
        private static readonly List<string> _appliedOldKeys = new List<string>();
        // 用不可见字符拼“段+键”，避免与段名/键名冲突。
        // 本文件只依赖 BCL（不引 BepInEx），规则/记录/判定可在离线测试台直接跑；喂条目表在 SR.Core.MigrationRuleAudit。
        internal const string AuditSep = "\u0001";
        public static int RuleConflictCount;

    }
}
