// 配置迁移：把老版本 cfg 里的中文 section / key 换成英文，并清掉已删功能留下的配置段。
// 必须赶在任何 Config.Bind 之前运行；文件被改写后调用方随后 ConfigFile.Reload()。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    public static partial class ConfigMigration {
        // 已删除功能的 cfg 段名名单：SR.Core 启动时统一 NavHide，侧栏不再显示这些段。
        public static readonly string[] DeletedSections = new string[] {
            "Saved Lobby Details",
            "Camera",
            "Respawn",
            "Support",
            "Reflection",
            "Freeplay",
            "Config Tools",
            // 2026-11 删除的功能段：段头隐藏，遗留键由下面的 RemoveKeys 清理。
            "Match Brief", "Accessibility", "Host Console",
            "Level Recorder", "Match Profile", "Match Cast", "Time Control", "Death Report",
        };

        // section 头：[旧] -> [新]
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
            // HUD 原名 Round HUD（功能早已只剩 HUD），老 cfg 段头统一改名并保留全部键值。
            { "Round HUD", "HUD" },
            // 早期版本用过的中文段名（与各功能 LocSec 的中文名一致）：缺了它们对应段及其键都识别不出来。
            // 注意：[自由模式] 现在是 SR 自己的活段（自由模式页），不要再把它当旧名改名到 Freeplay，
            // 否则与"段改名规则撞上活段"自检冲突，且每次启动都会把该段的键搬走。
            { "联机", "Online" },
            { "建造", "build" },
            { "Builder Enhancements", "build" }, // 段名改名
            { "聊天窗口", "Chat Window" },
            { "方块破坏", "Destroy Blocks" },
            { "重生", "Respawn" },
            { "移动轨迹", "Player Tracker" },
        };

        // key 名（行首 键名 = 值）：旧中文 key -> 新英文 key
        private static readonly Dictionary<string, string> KeyMap = new Dictionary<string, string> {
            { "解除建造上限", "Lift Build Cap" },
            { "上限数值", "Build Cap Value" },
            { "自由相机", "Free Camera" },
            { "地图总开关", "Map Enabled" },
            { "过滤快捷消息", "Filter Quick Msgs" },
            { "显示时间", "Show Time" },
            { "加载后清理", "GC After Load" },
            // 联机自动快捷键的旧 id（cc.）改为 online.；[Hotkeys] 段的键名即该 id。
            { "cc.lobbies", "online.lobbies" },
            { "cc.disband", "online.disband" },
            // cc.mainmenu 无对应 id（联机页两个按钮已合并），并入同动作的 online.disband。
            { "cc.mainmenu", "online.disband" },
        };

        // 组合键条目内层 "<section> <key>" 改名（key 可含空格，先匹配 section）。
        // 必须同时查功能自注册的段/键改名表（_xSections / _xKeys），且自注册表优先。
        private static string RenameComboInner(string rest) {
            foreach (KeyValuePair<string, string> kv in _xSections) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            foreach (KeyValuePair<string, string> kv in SectionMap) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            // 段名未变、仅 key 改名的情况：按第一个空格切成 "<section> <key>"，对后半段查 KeyMap。
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

        // 把某个 key 行从 from 段搬到 to 段（幂等：已不在 from 段就原样返回）
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
                    // outp 已删掉 keyLine 一行、下标位移，须在 outp 里重新定位目标段表头再插入。
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

        // 删掉已删除功能的废弃键（幂等：不在就原样返回）。带 "组合键 <段> <键>" 的绑定行一起删。
        private static string RemoveKeys(string text, string section, string[] keys) {
            try {
                // 登记删除规则，供启动后的冲突自检比对。
                if (keys != null) for (int __k = 0; __k < keys.Length; __k++) _appliedRemovals.Add(new[] { section, keys[__k] });
                // 删除由"手工死键名单"驱动，名单与活键脱节时会永久抹掉用户设置（audit 09-P0-01）。
                // 迁移层此时尚未 Bind、读不到"哪些键还活着"，所以至少把每次删除写明，便于事后反查。
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
                            // 组合键条目一律 Bind 在 [Settings] 段（键名 = "组合键 <目标段> <目标键>"），与普通键不同段。
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

        // 把 from 段 keyFrom 的值写到 to 段 keyTo（仅当源有值、目标无此键）。
        // 已删除条目的绑定值抢救：调用方随后用 RemoveKeys 删掉源键。
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
                // "未设置"的值不用搬（None = KeyCode.None；0 = 枚举序号 0）
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

        // 把 from 段 keyFrom 的值经 map 变换后写进 to 段 keyTo，并删掉源键（幂等：源键不存在或目标已有同键时原样返回）。
        // 用于"列键换版本号"这类迁移：光改名不够，旧值里的列索引必须先裁剪，否则会指到别的列。
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
                    // 源行删除后目标段表头可能前移，重新在 outp 里定位再插入。
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

        // 联机表格 v5 删除了「技能系数」列（v4 的索引 3），其后各列索引统一 -1。
        private const int OnlineV4ColCount = 12;
        private const int OnlineV5ColCount = 11;
        private const int OnlineV4SkillCol = 3;

        // v4 列宽（12 项）-> v5（11 项）：裁掉已删除的技能系数列。
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

        // v4 隐藏列索引 -> v5：丢掉已删除的 3，大于 3 的索引 -1；再按新列数裁剪越界项与不可隐藏的操作列。
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

        // 键在段间搬家时，[Settings] 里的 "组合键 <旧段> <键>" 条目名要一起改，否则组合键失效。
        private static string MoveComboEntry(string text, string fromSec, string toSec, string key) {
            return MoveComboEntryTo(text, fromSec, key, toSec, key);
        }

        // 组合键条目改名通用版：段名与键名都可变。
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

        // 同一段内同名键只保留最后一条（BepInEx 解析为后写覆盖先写，生效值不变）。
        // 清理"旧命名"的 [Settings] 热键条目：名字是 "组合键 <键>"（剩余部分不以任何已存在的段名开头），
        // 且值为空。值非空的不动（可能是用户手改的，宁可留着）。
        // 注意：条目上方通常紧跟它自己的三行注释（## 说明 / # Setting type / # Default value），一起删掉。
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
                            // 连同紧贴它的注释一起删（遇到空行/段头就停）。
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
                // 先记录每个 (段, 键) 最后一次出现的行号
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

        // 记录本次实际应用过的“删 / 搬 / 改名”动作，启动后与真正被 Config.Bind 绑定的键比对并报冲突。
        // 清理与搬移名单是手工维护的；规则撞上仍被绑定的键会让该设置每次启动被删且无报错。
        private static readonly List<string[]> _appliedRemovals = new List<string[]>();  // [sec,key]
        private static readonly List<string[]> _appliedMoves = new List<string[]>();     // [fromSec,key]
        private static readonly List<string> _appliedOldSections = new List<string>();
        private static readonly List<string> _appliedOldKeys = new List<string>();
        // 分隔符：用不可见字符拼“段+键”，避免与段名/键名冲突。
        // 本文件刻意只依赖 BCL（不引用 BepInEx），以便规则 + 记录 + 判定能在离线测试台直接跑。
        // 把当前条目表喂进来的那一步在 SR.Core（MigrationRuleAudit），因为它才持有 ConfigFile。
        internal const string AuditSep = "\u0001";
        public static int RuleConflictCount;

        // 核心判定（只依赖 BCL）：live = "段\u0001键" 列表
    }
}
