// 配置迁移：把老版本 cfg 里的中文 section / key 换成英文，并清掉已删功能留下的配置段。
// 必须赶在任何 Config.Bind 之前运行；文件被改写后调用方随后 ConfigFile.Reload()。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    public static class ConfigMigration {
        // 已删除功能的 cfg 段名名单：SR.Core 启动时统一 NavHide，侧栏不再显示这些段。
        public static readonly string[] DeletedSections = new string[] {
            "Saved Lobby Details",
            "Camera",
            "Respawn",
            "Support",
            "Reflection",
            "Freeplay",
            "Config Tools",
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
            // 早期版本用过的中文段名（与各功能 LocSec 的中文名一致）：缺了它们对应段及其键都识别不出来。
            { "自由模式", "Freeplay" },
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
        public static void AuditAgainst(IEnumerable<string> livePairs) {
            RuleConflictCount = 0;
            List<string> live = new List<string>();
            if (livePairs != null) foreach (string s in livePairs) if (s != null) live.Add(s);
            for (int i = 0; i < _appliedRemovals.Count; i++)
                ReportConflict(live, _appliedRemovals[i][0], _appliedRemovals[i][1],
                    "清理名单里的键**当前仍被功能绑定** -> 该设置每次启动都会被删掉、永远保存不下来；请把它从 RemoveKeys 名单里去掉");
            for (int i = 0; i < _appliedMoves.Count; i++)
                ReportConflict(live, _appliedMoves[i][0], _appliedMoves[i][1],
                    "搬移规则的**源段**当前仍是活段（有键被绑定）-> 可能每次启动都在搬同一个键；请确认这条规则还需要保留");
            for (int i = 0; i < _appliedOldSections.Count; i++) {
                string old = _appliedOldSections[i];
                for (int j = 0; j < live.Count; j++) {
                    if (live[j].StartsWith(old + AuditSep, StringComparison.Ordinal)) {
                        RuleConflictCount++;
                        if (RuleConflictCount <= 10)
                            SR.LogWarn("[SR.Cfg] 迁移规则冲突：段改名规则用的是 [" + old
                                + "]，但它**当前仍被功能绑定** -> 改名的同时还在被写入，请统一到新段名");
                        break;
                    }
                }
            }
            for (int i = 0; i < _appliedOldKeys.Count; i++)
                ReportConflictAnySection(live, _appliedOldKeys[i]);
        }

        private static void ReportConflict(List<string> live, string sec, string key, string what) {
            if (string.IsNullOrEmpty(sec) || string.IsNullOrEmpty(key)) return;
            if (!live.Contains(sec + AuditSep + key)) return;
            RuleConflictCount++;
            if (RuleConflictCount <= 10)
                SR.LogWarn("[SR.Cfg] 迁移规则冲突：[" + sec + "] " + key + " —— " + what);
        }

        private static void ReportConflictAnySection(List<string> live, string key) {
            if (string.IsNullOrEmpty(key)) return;
            for (int i = 0; i < live.Count; i++) {
                int p = live[i].IndexOf(AuditSep, StringComparison.Ordinal);
                if (p < 0) continue;
                if (live[i].Substring(p + AuditSep.Length) != key) continue;
                RuleConflictCount++;
                if (RuleConflictCount <= 10)
                    SR.LogWarn("[SR.Cfg] 迁移规则冲突：键改名规则的旧名 " + key
                        + " **当前仍被功能绑定** -> 改名后功能绑的是旧名，读不到用户设置");
                break;
            }
        }

        // 功能 / 外部模块自行登记配置迁移规则（在 Initialize / SelfReg 里调用，与 SR.Nav / SR.LocKey 放在一起）：
        // SR.Migration.Section/Key（改名）、Move/Combo/ComboTo（搬家，ComboTo 段名与键名同时变）、Remove（死键）、Retire（整段退役）。
        // 所有规则幂等，与旧规则共用同一套实现；登记当晚即生效一次（SR.Core.EnsureScanned 补跑 RunPending）。
        private static readonly Dictionary<string, string> _xSections = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _xKeys = new Dictionary<string, string>();
        private static readonly List<string[]> _xMoves = new List<string[]>();       // [from,to,key]
        private static readonly List<string[]> _xRemovals = new List<string[]>();    // [sec, key...]
        private static bool _xPending;

        // 是否有功能登记过、但还没应用的规则（SR.Core 据此决定要不要补跑一次）
        public static bool HasPending { get { return _xPending; } }

        public static void Section(string oldName, string newName) {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
            _xSections[oldName] = newName; _xPending = true;
        }
        public static void Key(string oldName, string newName) {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
            _xKeys[oldName] = newName; _xPending = true;
        }
        public static void Move(string fromSec, string toSec, string key) {
            if (string.IsNullOrEmpty(fromSec) || string.IsNullOrEmpty(toSec) || string.IsNullOrEmpty(key)) return;
            _xMoves.Add(new[] { fromSec, toSec, key }); _xPending = true;
        }
        public static void Remove(string section, params string[] keys) {
            if (string.IsNullOrEmpty(section) || keys == null || keys.Length == 0) return;
            List<string> row = new List<string>();
            row.Add(section);
            row.AddRange(keys);
            _xRemovals.Add(row.ToArray()); _xPending = true;
        }
        // 整段退役：文件层面不动（键由 Remove 清），只让侧栏不再把它当栏目显示（复用 NavHide）。
        public static void Retire(string section) {
            if (string.IsNullOrEmpty(section)) return;
            try { SR.NavHide(section); } catch (Exception __ex) { SR.Guard.Log("退役段隐藏", __ex); }
        }

        // 原子替换配置文件的唯一实现：写 .tmp -> File.Replace 并留 .bak，失败退回“先备份再覆盖写”。
        public static bool WriteCfgAtomic(string cfgPath, string text, string logTag) {
            try {
                string tmpPath = cfgPath + ".tmp";
                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                try { File.Replace(tmpPath, cfgPath, cfgPath + ".bak"); }
                catch (Exception __rep) {
                    try { File.Copy(cfgPath, cfgPath + ".bak", true); } catch { }
                    File.WriteAllText(cfgPath, text, new UTF8Encoding(false));
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    SR.LogWarn("[" + logTag + "] 原子替换失败，已退回备份加覆盖写: " + __rep.Message);
                }
                return true;
            } catch (Exception __ex) { SR.Guard.Log(logTag + "：写配置", __ex); return false; }
        }

        // 把一批 (段,键) 从配置文本里整行删掉；pairs 用 AuditSep 拼成 "段\u0001键"。
        // RemoveKeys 会连 "组合键 <段> <键>" 的绑定行一起删。
        public static string RemovePairs(string text, IEnumerable<string> pairs) {
            try {
                if (pairs == null) return text;
                string res = text;
                int n = 0;
                foreach (string p in pairs) {
                    if (string.IsNullOrEmpty(p)) continue;
                    int i = p.IndexOf(AuditSep, StringComparison.Ordinal);
                    if (i <= 0) continue;
                    string sec = p.Substring(0, i);
                    string key = p.Substring(i + AuditSep.Length);
                    if (string.IsNullOrEmpty(sec) || string.IsNullOrEmpty(key)) continue;
                    res = RemoveKeys(res, sec, new[] { key });
                    n++;
                }
                return n > 0 ? DedupeSectionKeys(res) : res;
            } catch (Exception __ex) { SR.Guard.Log("批量清理配置键", __ex); return text; }
        }

        // 把"功能自注册"的规则应用到配置文件。幂等；返回 true 表示文件被改写（调用方应随后 ConfigFile.Reload）。
        public static bool RunPending(string cfgPath) {
            if (!_xPending) return false;
            try {
                if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) return false;
                string text = File.ReadAllText(cfgPath, Encoding.UTF8);
                string orig = string.Join("\n", text.Replace("\r\n", "\n").Split('\n'));
                if (_xSections.Count > 0 || _xKeys.Count > 0) {
                    string[] lines = text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++) {
                        string line = lines[i];
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)) {
                            string name = trimmed.Substring(1, trimmed.Length - 2);
                            string to;
                            if (_xSections.TryGetValue(name, out to))
                                lines[i] = line.Substring(0, line.Length - trimmed.Length) + "[" + to + "]";
                            continue;
                        }
                        int eq = trimmed.IndexOf('=');
                        if (eq <= 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                        string key = trimmed.Substring(0, eq).TrimEnd();
                        string to2;
                        if (key.StartsWith("组合键 ", StringComparison.Ordinal)) {
                            string rest = key.Substring(4);
                            string newRest = RenameComboInner(rest);
                            if (newRest != rest) {
                                lines[i] = line.Substring(0, line.Length - trimmed.Length) + "组合键 " + newRest + " " + trimmed.Substring(eq);
                                continue;
                            }
                        }
                        if (_xKeys.TryGetValue(key, out to2))
                            lines[i] = line.Substring(0, line.Length - trimmed.Length) + to2 + " " + trimmed.Substring(eq);
                    }
                    text = string.Join("\n", lines);
                }
                for (int i = 0; i < _xMoves.Count; i++)
                    text = MoveKeyBetweenSections(text, _xMoves[i][0], _xMoves[i][1], _xMoves[i][2]);
                for (int i = 0; i < _xRemovals.Count; i++) {
                    string sec = _xRemovals[i][0];
                    string[] keys = new string[_xRemovals[i].Length - 1];
                    Array.Copy(_xRemovals[i], 1, keys, 0, keys.Length);
                    text = RemoveKeys(text, sec, keys);
                }
                text = DedupeSectionKeys(text);
                if (text == orig) { _xPending = false; return false; }
                if (!WriteCfgAtomic(cfgPath, text, "配置迁移·功能规则")) return false;
                _xPending = false;
                return true;
            } catch (Exception __ex) { SR.Guard.Log("功能自注册迁移规则", __ex); return false; }
        }

        // 返回 true 表示文件被改写过（调用方应随后 ConfigFile.Reload()）
        public static bool Migrate(string cfgPath) {
            try {
                if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) return false;
                string text = File.ReadAllText(cfgPath, Encoding.UTF8);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                // 基准须取在换行归一化之后、改名之前：取在归一化前（CRLF）text == orig 恒不成立，会每次启动白重写文件。
                // 取在改名之后，则只有改名（无搬移/清理）时 text == orig 成立、改名结果不落盘，迁移等于没生效。
                string orig = string.Join("\n", lines);
                for (int i = 0; i < lines.Length; i++) {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    // section 头
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)) {
                        string name = trimmed.Substring(1, trimmed.Length - 2);
                        string to;
                        if (SectionMap.TryGetValue(name, out to)) {
                            _appliedOldSections.Add(name);
                            lines[i] = line.Substring(0, line.Length - trimmed.Length) + "[" + to + "]";
                            continue;
                        }
                    }
                    // 键名行（形如 "键名 = 值" 或 "键名=值"）
                    int eq = trimmed.IndexOf('=');
                    if (eq > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal)) {
                        string key = trimmed.Substring(0, eq).TrimEnd();
                        string to;
                        // 组合键的键名里也含 section/key，不迁移则已绑定的组合键全部失效。
                        if (key.StartsWith("组合键 ", StringComparison.Ordinal)) {
                            string rest = key.Substring(4);
                            string newRest = RenameComboInner(rest);
                            if (newRest != rest) {
                                lines[i] = line.Substring(0, line.Length - trimmed.Length) + "组合键 " + newRest + " " + trimmed.Substring(eq);
                                continue;
                            }
                        }
                        if (KeyMap.TryGetValue(key, out to)) {
                            _appliedOldKeys.Add(key);
                            lines[i] = line.Substring(0, line.Length - trimmed.Length) + to + " " + trimmed.Substring(eq);
                        }
                    }
                }
                text = string.Join("\n", lines);
                // "GC After Load" 已归实验页：从 [Freeplay] 搬到 [Experiments]，不搬值则原设置读不到。
                text = MoveKeyBetweenSections(text, "Freeplay", "Experiments", "GC After Load");
                // [EX] 段里混着的本仓库自带功能归位，值必须跟着走。
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb Type");
                // 允许客户端删除是外部模块的功能：老配置在 [EX]，曾搬到 [Destroy Blocks]，现统一搬回 [EX]（幂等）。
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients");
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // 键搬家必须连 [Settings] 里的 "组合键 <段> <键>" 条目一起改，否则已绑定的修饰键静默丢失。
                text = MoveComboEntry(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // 会话内容页两个开关从 [Settings] 归位到 [Chat]（配置跟功能走），须搬值。
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Filter Quick Msgs");
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Show Time");
                // 老 cfg 的 [视野]（已改名为 [Camera]）里的自由相机设置要搬到实际绑定段 [Experiments]。
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera FOV");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera Key");
                // 组合键条目名含段名与键名，每个键各改一次（自由相机 / Free Camera Key）。
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera");
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera Key");
                // 已删功能的死键：段头由 DeletedSections + NavHide 隐藏，键由这里清理。
                text = RemoveKeys(text, "Freeplay", new[] { "Map Key", "Map Enabled", "Treehouse Map" });
                text = RemoveKeys(text, "Support", new[] { "Cross Platform" });
                text = RemoveKeys(text, "Reflection", new[] { "Last Game Version" });
            text = RemoveKeys(text, "Experiments", new[] { "Cross Platform", "Level Background" });
            text = RemoveKeys(text, "Online", new[] { "Hidden Columns" });
            // EX 模块的遗留键（EX 在仓库外，无法自带清理规则）：已确认没有任何代码再读。
            text = RemoveKeys(text, "EX", new[] { "Clear Party Box Key", "Coin Amount" });
            // 快速自杀功能已删除：清理其遗留键。
            text = RemoveKeys(text, "Quick Adjust", new[] { "Enabled", "Keybind", "Score Discount", "More Discount Values" });
            text = RemoveKeys(text, "Internal", new[] { "P Suicide Migrated" });
            // 对局记录功能已删除（2026-10）：清理其配置段遗留键。
            text = RemoveKeys(text, "Match Replay", new[] { "Enabled", "Max Sessions", "Record Scores", "Record Blocks" });
            // 配置导出/导入工具已删除：该段只 Bind 过 Enabled 与 Max Backups（备份目录是固定路径，不是配置键）。
            text = RemoveKeys(text, "Config Tools", new[] { "Enabled", "Max Backups" });
            // P2 配置瘦身：清理已删除功能的废弃键（静态校验过：主模块源码里已无任何引用）。
            text = RemoveKeys(text, "Experiments", new[] { "Mute Own", "Mute Others", "Level Background Scale", "Level Bounds Treehouse", "Level Bounds Death Line", "Level Bounds Death Drop", "Level Bounds Only In Place", "Level Bounds Direction", "Level Bounds Key" });
            text = RemoveKeys(text, "Scene", new[] { "Render Hide Skybox", "Render Bg Color", "Scene Bounds", "Scene Spawn", "Scene Death Line", "Scene Death Line Len" });
            text = RemoveKeys(text, "Level", new[] { "Build Mode", "Build File", "Score Fill Per Frame", "Score Fill Total Cap", "Party Bomb Radius", "Party Bomb Trigger Ratio" });
            text = RemoveKeys(text, "Online", new[] { "Column Widths 3" });
            // 联机表格 v5 删除「技能系数」列（v4 索引 3）：v4 的列宽 / 隐藏列先裁剪索引再写进 v5 键，
            // 否则旧索引在新布局里会指到别的列（列宽错位 / 藏错列）。Column Order 没有版本号，改由 Online 加载时裁剪。
            text = MoveMappedKey(text, "Online", "Hidden Columns 4", "Online", "Hidden Columns 5", MapOnlineV4HiddenCols);
            text = MoveMappedKey(text, "Online", "Column Widths 4", "Online", "Column Widths 5", MapOnlineV4ColWidths);
            // 源键残留 / 已删除的「技能系数」取值键一并清掉。
            text = RemoveKeys(text, "Online", new[] { "Hidden Columns 4", "Column Widths 4", "Skill Mode" });
            text = RemoveKeys(text, "Player Tracker", new[] { "Show Own Trail" });
            // 方块破坏已按参考实现整体重写：旧的「列表 / 选择模式 / 删除键」那套全部删除，
            // 只剩选择键与烟雾动画两个键。旧键（含 [Settings] 里的组合键条目）一并清掉。
            text = RemoveKeys(text, "Destroy Blocks", new[] { "Realtime Interval", "Enabled", "Toggle Key", "Delete Key", "Select Mode", "List Mode", "Track Player", "Refresh Mode" });
            text = RemoveKeys(text, "Settings", new[] { "Show Advanced" });
            // HUD 玩家面板已删除：[Round HUD] 段里的总开关、五个列开关与五个列颜色键必须清掉，
            // 否则老配置会留下永远不再读取的脏键（界面已无对应行）。
            text = RemoveKeys(text, "Round HUD", new[] { "Show Player Panel", "Show Number", "Show Name", "Show Animal", "Show Score", "Show Handicap", "Color Panel Number", "Color Panel Name", "Color Panel Animal", "Color Panel Score", "Color Panel Handicap" });
                text = RemoveKeys(text, "Respawn", new[] {
                    "Enabled", "Spawn Immunity", "Delay", "Spawn Points Enabled",
                    "Set Spawn Key", "Respawn Key", "Reset Spawn Keys"
                });
                // 关卡背景功能已删除：这个键在两个曾用段名里都要清掉。
                text = RemoveKeys(text, "Level", new[] { "Level Background", "Refresh Pieces Key" });
            text = RemoveKeys(text, "Online", new[] { "Filter Min Players", "Filter AFK" });
            text = RemoveKeys(text, "Hotkeys", new[] { "level.broadcast" });
                text = RemoveKeys(text, "Scene", new[] { "Level Background" });
                // 派对盒（刷新/重抽/关闭）的界面与代码都在关卡功能：段从 [EX] 挪到 [Level]。
                text = MoveKeyBetweenSections(text, "EX", "Level", "Refresh Party Box Key");
                    text = MoveKeyBetweenSections(text, "EX", "Level", "Close Party Box Key");
                // 这三个是键位，[Settings] 里的 "组合键 <段> <键>" 条目名要一起搬。
                text = MoveComboEntry(text, "EX", "Level", "Refresh Party Box Key");
                    text = MoveComboEntry(text, "EX", "Level", "Close Party Box Key");
                // 地图网格的界面与代码都在 EX：段名从 [Freeplay] 改为 [EX]。
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Always On");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Max Alpha");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Fade Time");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Refresh Color");
                // [Reflection] 段只剩一个内部迁移标记，并入隐藏的 [Internal]。
                text = MoveKeyBetweenSections(text, "Reflection", "Internal", "P Suicide Migrated");
                // 已删功能：可动方块力度、放置形态（[CC] 段），以及 Online 段里删掉的两个筛选。
                text = RemoveKeys(text, "CC", new[] {
                    "BlockMod Enabled", "BlockMod Log", "BlockMod All", "BlockMod BaseMotion", "BlockMod Treadmill",
                    "BlockMod Wind", "BlockMod Gravity", "BlockMod GroundFriction", "BlockMod WallFriction",
                    "BlockMod JumpForce", "BlockMod AirInertia", "BlockMod Blackhole", "BlockMod Rotation",
                    "PlaceForm Enabled", "PlaceForm Key", "PlaceForm Key Prev", "PlaceForm Key Next",
                    "PlaceForm Steps", "PlaceForm Preview", "PlaceForm Log", "PlaceForm Modifier"
                });
                // 列名带版本号的废弃键（v1 "Column Widths"、v2 "Column Widths 2"）一并清理。
                text = RemoveKeys(text, "Online", new[] { "Filter Limit", "Filter Mods", "Column Widths", "Column Widths 2", "Sort" });
                // [Online] 段三条隐形快捷键已删除：先把已绑定的键搬进 [Hotkeys] 对应 id，再删源键。
                text = MergeKeyValue(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MergeKeyValue(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MergeKeyValue(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                // 只搬主键值不够：修饰键存在 [Settings] 的 "组合键 <段> <键>" 条目里，须先用 MoveComboEntryTo 改名再删源键。
                text = MoveComboEntryTo(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MoveComboEntryTo(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MoveComboEntryTo(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                text = RemoveKeys(text, "Online", new[] { "Refresh Lobbies Key", "Disband Key", "Main Menu Key" });
                // [Hotkeys] 段里这 12 个无界面件登记的旧 id 整批删掉。
                // 注意：row:<段>.<键> 形式的 id 是通用条目行自动登记的，不能按“源码里查不到”清理。
                text = RemoveKeys(text, "Hotkeys", new[] {
                    "exp.disband", "exp.mainmenu", "exp.lobbies", "exp.clear_questions",
                    "exp.add_question", "exp.remove_question", "exp.add_all_questions",
                    "level.party_bomb", "map.treehouse",
                    "chat.filter_quick", "chat.show_time", "chat.hide_window"
                });
                // 解除其它墙的 5 个逐面排查开关已删除（真正的边界墙由 Level Bounds Open Walls 负责）。
                text = RemoveKeys(text, "Experiments", new[] {
                    "Wall Cursor Collider", "Wall Piece Bounds Check", "Wall Ignore Bounds",
                    "Wall Grid Snap", "Wall Placement Rules"
                });
                // 解除关卡边界整理：已废弃的子项键全部删除（现已无任何 Config.Bind；构建期守卫会交叉核对）。
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Margin", "Level Bounds Hard", "Level Bounds Walls",
                    "Level Bounds Cursor", "Level Bounds Camera", "Level Bounds Piece Ignore",
                    "Remove UI3"
                });
                // 多选系统与“相机只跟随角色 / 限制最远视野”已删除，键名一并清掉。
                text = RemoveKeys(text, "build", new[] {
                    "Multi Select", "Multi Select Frame", "Multi Select Add Key",
                    "Multi Select Move", "Multi Select Copy", "Multi Select Delete", "Multi Select Hint"
                });
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Cam Follow", "Level Bounds Cam Frame", "Level Bounds Hide Wall Art"
                });
                // 树屋/遮罩/场景里已删除功能的键。
                text = RemoveKeys(text, "Scene", new[] {
                    "Lobby Auto Vote Index", "Lobby Countdown Sec", "Lobby Vote Panel", "Lobby Host Ready",
                    "Lobby Block Random", "Lobby Lock Vote", "Lobby Lock Votes Key",
                    "Splash Show Info", "Splash Mute", "Scene Set Mode", "Scene Mode",
                    "Lobby Cancel Countdown", "Lobby Auto Vote", "Lobby Auto Vote Level", "Lobby Auto Vote Teleport"
                });
                // 实验区整页、若干分区与加载遮罩相关键已删除。
                text = RemoveKeys(text, "Scene", new[] {
                    "Splash Auto Skip", "Splash Auto Skip Sec",
                    "Splash Fade In", "Splash Fade Out", "Splash Show", "Splash Hide",
                    "Solid Wall TopBoundary", "Solid Wall LeftBoundary", "Solid Wall RightBoundary",
                    "Solid Wall Wall", "Solid Wall HazardSolid (1)", "Solid Wall SolidCollider",
                    "Free Camera 2", "Free Camera 2 Speed", "Free Camera 2 Fov Min", "Free Camera 2 Fov Max",
                    "Free Camera 2 Reset Key", "Free Camera 2 Char Key", "Free Camera 2 Edge", "Free Camera 2 Edge Size",
                    "Death Zone", "Death Zone Line", "Death Zone Fill", "Death Zone Pit", "Death Zone Hazard"
                });
                // 这份名单只保留确实无任何代码绑定的键；名单撞上活键由运行时自检兜底（见 AuditAgainst）。
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Log", "Level Bounds Open Walls", "Level Bounds Hide Wall Art"
                });
                text = RemoveKeys(text, "Entity", new[] {
                    "Entity Ops", "Entity Reset Key", "Entity Reset Rot Only", "Entity Force Place Key",
                    "Bomb Shake", "Bomb Shake Mega", "Bomb Shake Big", "Bomb Shake Normal",
                    "Bomb Shake Mini", "Bomb Shake Time", "Bomb Shake Others"
                });
                text = RemoveKeys(text, "Support", new[] {
                    "Support Tools", "Support Scoreboard", "Support PartyBox", "Support Net Disconnect",
                    "Shake Camera Key",
                    "Show Spawn", "Show Goal", "Show Death Line", "Show Density", "Show Edges",
                    "Show Start Blocks", "Show Spawn Calc"
                });
                // cc.disband 与 cc.mainmenu 都映射到 online.disband（见 KeyMap），按“后写覆盖先写”只保留最后一条。
                text = DedupeSectionKeys(text);
                if (text == orig) return false;
                // 配置文件是全部设置的唯一副本：写临时文件 -> 原子替换 -> 留 .bak；替换失败退回先备份再覆盖写。
                string tmpPath = cfgPath + ".tmp";
                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                try {
                    File.Replace(tmpPath, cfgPath, cfgPath + ".bak");
                } catch (Exception __rep) {
                    try { File.Copy(cfgPath, cfgPath + ".bak", true); } catch { }
                    File.WriteAllText(cfgPath, text, new UTF8Encoding(false));
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    SR.LogWarn("[配置迁移] 原子替换失败，已退回备份加覆盖写: " + __rep.Message);
                }
                return true;
            } catch (Exception __ex) { SR.Guard.Log("cfg section/key 迁移", __ex); return false; }
        }
    }
}
