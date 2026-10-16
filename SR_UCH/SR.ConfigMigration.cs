// 配置迁移。把老版本 cfg 里的中文 section / key 换成英文，顺手清掉已经删掉的功能留下的配置段。
// 时机很要紧：必须赶在**任何 Config.Bind 之前**跑，跑完还得 ConfigFile.Reload()，不然内存里留着的还是旧键。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    internal static class ConfigMigration {
        // 已删除功能的 cfg 段名名单。
        // 用途有二：1) 老 cfg 里若还留着这些段，侧栏不该显示它们（SR.Core 启动时会 NavHide）；
        //          2) 将来若要清理老 cfg，也在这里加规则。
        // 放在这里而不是核心里，是因为"删掉一个功能"本来就要在这个文件登记；
        // 挪过来之后，删功能不需要改 SR.Core.cs（核心不维护任何功能名单）。
        public static readonly string[] DeletedSections = new string[] {
            "Saved Lobby Details",
            // **Opt-3（审计 F6 / M2）**：下面这些段今天已经**没有任何 Bind**，但老 cfg 里可能还留着
            //  （要么是下面迁移规则的"目标段"，要么是已删功能的历史段）。不登记的话，
            //  SR.Core.RebuildSections 的"兜底"分支会把它们加成侧栏栏目 ->
            //  用户看到一个内容无意义的"幻影页"。实测证据：真实 cfg 里的 [Support] 段只剩一个
            //  没人读的 Cross Platform，却会在侧栏出现（因为只有段名进过 NavHide 才会被跳过）。
            //  登记在这里 = SR.Core 启动时统一 NavHide（见 SR.Core 里遍历 DeletedSections 的那段）。
            "Camera",     // 视野：自由相机已归 [Experiments]（见下面 Opt-3 的值搬运）
            "Respawn",    // 重生：功能早已删除，段里只剩死键
            "Support",    // 支撑：功能早已删除，段里只剩 Cross Platform
            "Reflection", // 反射成员名覆盖探针早已删除（核心另有 NavHide，这里是双保险）
            "Freeplay",   // 地图/自由模式：网格键已归 [EX]；**本模组自己就该藏掉**，
                          //   原来只有 EX 模块 NavHide 它 -> 单装 SR_UCH 时会多出一个空栏目
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
            //下面这几个是早期版本用过的中文段名（与现在各功能 LocSec 的中文名一致）：
            //缺了它们，老 cfg 里这些段会整段识别不出来（键名也跟着不迁移）-> 老设置一直读不到。
            //补齐是安全的：映射只在 cfg 里**确实存在**该中文表头时才生效，
            //当前版本的 cfg 段名全是英文，不会误伤。
            { "自由模式", "Freeplay" },
            { "联机", "Online" },
            { "建造", "build" },
            { "Builder Enhancements", "build" },   // **段键改名**：老 cfg 的 [Builder Enhancements] 自动迁移到 [build]
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
            // 联机栏目的自动快捷键：老 id 带着 CC 栏目的痕迹（cc.）-> 改成 online.
            // （[Hotkeys] 段的键名就是这个 id，连同 "组合键 Hotkeys <id>" 一起迁移，用户绑过的键不丢）
            { "cc.lobbies", "online.lobbies" },
            { "cc.disband", "online.disband" },
            // 老的 cc.mainmenu 没有对应的 id 了（联机页两个按钮已合并成返回主界面）-> 并到同动作的 online.disband
            { "cc.mainmenu", "online.disband" },
        };

        // 组合键条目内层 "<section> <key>" 改名（key 本身可能含空格，故先匹配 section）
        // **Opt-35 修正**：这里必须**同时**查"功能自注册"的段/键改名表（_xSections / _xKeys），
        //   否则用 SR.Migration.Section/Key 改名的功能，它自己的组合键条目名会留在旧段名上 ->
        //   用户绑的 Shift/Ctrl/Alt 找不到目标条目而静默失效（就是 F9 那一类 bug，
        //   只不过从"漏写一条规则"升级成"机制本身没覆盖"）。自注册表优先（更新的、更具体）。
        private static string RenameComboInner(string rest) {
            foreach (KeyValuePair<string, string> kv in _xSections) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            foreach (KeyValuePair<string, string> kv in SectionMap) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            // 段名没变、只有 key 改名的情况（如 [Hotkeys] 的 cc.lobbies -> online.lobbies）：
            // 按第一个空格切成 "<section> <key>"，对后半段查 KeyMap
            int sp = rest.IndexOf(' ');
            if (sp > 0) {
                string tail = RenameKeyIn(rest.Substring(sp + 1));
                if (tail != rest.Substring(sp + 1)) return rest.Substring(0, sp + 1) + tail;
            }
            return RenameKeyIn(rest);
        }

        private static string RenameKeyIn(string key) {
            string to;
            if (_xKeys.TryGetValue(key, out to)) return to;   // **Opt-35**：功能自注册的键改名优先
            return KeyMap.TryGetValue(key, out to) ? to : key;
        }

        // 把某个 key 行从 from 段搬到 to 段（幂等：已不在 from 段就原样返回）
        private static string MoveKeyBetweenSections(string text, string from, string to, string key) {
            try {
                _appliedMoves.Add(new[] { from, key });   // **Opt-43**：登记规则意图（与文件里有没有命中无关）
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
                    //注意：outp 已经删掉了 keyLine 那一行，下标会位移, 目标段表头若在被删行之后，
                    //它在 outp 里的下标是 toHdr-1，插入点应为 toHdr；直接用 toHdr+1 会插到表头下第二行，
                    //目标段为空时甚至会落到**下一个段**里（键搬错段 = 设置读不到）。
                    //改成删完之后在 outp 里重新定位表头，索引永远对得上。
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
                // **Opt-43**：登记"打算删哪些键"，供启动后与真实绑定表比对
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
                            //组合键条目一律 Bind 在 [Settings] 段（键名 = "组合键 <目标段> <目标键>"），
                            //与普通键不在同一段 -> 必须独立于 cur==section 匹配，否则永不命中。
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

        // 把 from 段的 keyFrom 的**值**写到 to 段的 keyTo（只在"源有值、目标还没这个键"时写）。
        // 用于已删除条目的绑定值抢救：调用方随后用 RemoveKeys 删掉源键，用户绑过的键不会丢。
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

        // 键在段之间搬家时，[Settings] 里那条"组合键 <旧段> <键>"也要跟着改名 ——
        // 不改的话用户绑过的组合键（如 Shift+F9）找不到目标条目，重启后组合键会失效。
        private static string MoveComboEntry(string text, string fromSec, string toSec, string key) {
            return MoveComboEntryTo(text, fromSec, key, toSec, key);
        }

        // **Opt-4（审计 F9）**：组合键条目改名的通用版 —— 段名与键名**都可以变**。
        //  为什么需要它：Online -> Hotkeys 那次搬家不仅换段，连键名也换了
        //  （Refresh Lobbies Key -> online.lobbies），只换段名的 MoveComboEntry 表达不了，
        //  于是原来只搬了主键值、修饰键条目被后面的 RemoveKeys 一起删掉 -> 用户绑的 Shift/Ctrl/Alt 丢失。
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

        // **Opt-7（审计 F9）**：同一段内出现同名键时只保留**最后一条**。
        //  为什么不会改变行为：BepInEx 解析 cfg 是"同名后写覆盖先写"，所以保留最后一条与今天的
        //  实际生效值完全一致，只是把文件里那条永远没人读的重复行清掉。
        //  需要它的直接原因：KeyMap 把 cc.disband 与 cc.mainmenu 都映射成 online.disband，
        //  老 cfg 里两条都在时就会产生重复键（文件脏、且"用户以为两个键都还在"）。
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

        // ========================================================================
        // **Opt-43（自检 / 可维护性）**：记录本次实际应用过的"删 / 搬 / 改名"动作，
        //   启动后与"当前真正被 Config.Bind 绑定的键"比对，把冲突报出来。
        // 为什么需要它：清理与搬移名单是**手工维护**的。一旦某个键后来被功能重新启用
        //  （R432 就把 Level Bounds Cursor / Camera 加了回来），迁移就会**每次启动**把用户的设置删掉，
        //   表现为"这个开关勾了、重启就没了"，而且没有任何报错 —— 正是 Opt-42 那个实缺陷。
        // 这个自检把"规则 vs 现实"的冲突变成日志里一条明确告警 + 设置页计数。
        // 判定逻辑抽成只依赖 BCL 的 AuditAgainst，便于离线测试台直接验证。
        // ========================================================================
        private static readonly List<string[]> _appliedRemovals = new List<string[]>();  // [sec,key]
        private static readonly List<string[]> _appliedMoves = new List<string[]>();     // [fromSec,key]
        private static readonly List<string> _appliedOldSections = new List<string>();
        private static readonly List<string> _appliedOldKeys = new List<string>();
        // 分隔符：用不可见字符拼"段+键"，避免和段名/键名里的字符冲突。
        // **本文件刻意只依赖 BCL**（不引用 BepInEx 的 ConfigEntryBase/ConfigFile）：
        //  这样"规则 + 记录 + 判定"可以脱离游戏在离线测试台里直接跑（见 _tmp/audit/migtest）。
        //  "把当前条目表喂进来"的那一步在 SR.Core（MigrationRuleAudit），因为它才有 ConfigFile。
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

        // ========================================================================
        // **Opt-35（扩展性）**：功能 / 外部模块**自己**登记配置迁移规则
        // ------------------------------------------------------------------------
        // 为什么需要这个入口：本文件下面那一大段规则是**历史兼容**——老用户的 cfg 里存着旧段名/旧键名，
        //   删掉规则就等于让老用户丢设置，所以它们必须留在核心。但**新增**功能不该再往核心塞规则
        //   （"删一个功能要改核心"与"核心不认识任何功能"正好相反，也是本文件最大的维护痛点）。
        // 用法（在自己的 Initialize / SelfReg 里调，与 SR.Nav / SR.LocKey 放在一起）：
        //   SR.Migration.Section("旧段名", "新段名");
        //   SR.Migration.Key("旧键名", "新键名");
        //   SR.Migration.Move("旧段", "新段", "键名");            // 连值一起搬
        //   SR.Migration.Combo("旧段", "新段", "键名");           // 组合键条目名跟着段一起搬
        //   SR.Migration.ComboTo("旧段", "旧键", "新段", "新键");  // 段名与键名同时变
        //   SR.Migration.Remove("段名", "键1", "键2");            // 已删功能的死键
        //   SR.Migration.Retire("段名");                         // 已删功能的整段（同时 NavHide，不再当栏目显示）
        // 时机：登记当晚就会生效一次（SR.Core.EnsureScanned 里补跑 RunPending）——**不用等下次启动**，
        //   因为 EnsureScanned 在首帧、晚于全部功能与 EX 的初始化。
        // 所有规则都幂等（重复应用无副作用），与旧规则共用同一套实现（MoveKeyBetweenSections / RemoveKeys 等）。
        // 注意：组合键条目名的"旧段名"要用**迁移前**的段名（条目名是 "组合键 <段> <键>"）。
        // ========================================================================
        private static readonly Dictionary<string, string> _xSections = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _xKeys = new Dictionary<string, string>();
        private static readonly List<string[]> _xMoves = new List<string[]>();       // [from,to,key]
        private static readonly List<string[]> _xComboMoves = new List<string[]>();  // [fromSec,fromKey,toSec,toKey]
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
        public static void Combo(string fromSec, string toSec, string key) {
            if (string.IsNullOrEmpty(fromSec) || string.IsNullOrEmpty(toSec) || string.IsNullOrEmpty(key)) return;
            _xComboMoves.Add(new[] { fromSec, key, toSec, key }); _xPending = true;
        }
        public static void ComboTo(string fromSec, string fromKey, string toSec, string toKey) {
            if (string.IsNullOrEmpty(fromSec) || string.IsNullOrEmpty(fromKey) ||
                string.IsNullOrEmpty(toSec) || string.IsNullOrEmpty(toKey)) return;
            _xComboMoves.Add(new[] { fromSec, fromKey, toSec, toKey }); _xPending = true;
        }
        public static void Remove(string section, params string[] keys) {
            if (string.IsNullOrEmpty(section) || keys == null || keys.Length == 0) return;
            List<string> row = new List<string>();
            row.Add(section);
            row.AddRange(keys);
            _xRemovals.Add(row.ToArray()); _xPending = true;
        }
        // 整段退役：文件层面不需要动（键由 Remove 清），主要是让侧栏不再把它当栏目显示。
        // 直接复用核心的 NavHide（它本身支持"晚注册立即生效"）。
        public static void Retire(string section) {
            if (string.IsNullOrEmpty(section)) return;
            try { SR.NavHide(section); } catch (Exception __ex) { SR.Guard.Log("退役段隐藏", __ex); }
        }

        // **Opt-46**：原子替换配置文件的唯一实现（原来只在 RunPending 里内联了一份）。
        //  纯 BCL，便于离线测试台直接验证；失败会退回"先备份再覆盖写"。
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

        // **Opt-46（让自检可操作）**：把一批 (段,键) 从配置文本里整行删掉。
        //  pairs 用 AuditSep 拼成 "段\u0001键"（与孤儿键自检的输出同格式）。
        //  RemoveKeys 会连 "组合键 <段> <键>" 的绑定行一起删（它本来就是那个键的附属条目）。
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
                for (int i = 0; i < _xComboMoves.Count; i++)
                    text = MoveComboEntryTo(text, _xComboMoves[i][0], _xComboMoves[i][1], _xComboMoves[i][2], _xComboMoves[i][3]);
                for (int i = 0; i < _xRemovals.Count; i++) {
                    string sec = _xRemovals[i][0];
                    string[] keys = new string[_xRemovals[i].Length - 1];
                    Array.Copy(_xRemovals[i], 1, keys, 0, keys.Length);
                    text = RemoveKeys(text, sec, keys);
                }
                text = DedupeSectionKeys(text);
                if (text == orig) { _xPending = false; return false; }
                if (!WriteCfgAtomic(cfgPath, text, "配置迁移·功能规则")) return false;   // **Opt-46**：抽成一处
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
                //基准必须取在换行归一化之后、改名之前，两个方向都不能偏：
                //  · 取在归一化之前（原始文本）：cfg 是 CRLF 时，末尾的 text == orig 永远不成立
                //    （LF 比 CRLF）-> 每次启动都被判成"有改动"，白重写文件 + Reload + 打一条迁移日志；
                //  · 取在改名之后：只有改名、没有任何搬移/清理时 text == orig 同样成立 ->
                //    改名结果不落盘，下次启动又从头改一遍, 迁移等于完全没生效
                //    （中文 section/key 永远留在配置文件里，老用户的设置一直读不到）。
                string orig = string.Join("\n", lines);
                for (int i = 0; i < lines.Length; i++) {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    // section 头
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)) {
                        string name = trimmed.Substring(1, trimmed.Length - 2);
                        string to;
                        if (SectionMap.TryGetValue(name, out to)) {
                            _appliedOldSections.Add(name);   // **Opt-43**
                            lines[i] = line.Substring(0, line.Length - trimmed.Length) + "[" + to + "]";
                            continue;
                        }
                    }
                    // 键名行（形如 "键名 = 值" 或 "键名=值"）
                    int eq = trimmed.IndexOf('=');
                    if (eq > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal)) {
                        string key = trimmed.Substring(0, eq).TrimEnd();
                        string to;
                        // 2a) 组合键的键名里也含 section/key：不迁移的话用户设置过的组合键会全部失效（新名字对不上）。
                        if (key.StartsWith("组合键 ", StringComparison.Ordinal)) {
                            string rest = key.Substring(4);
                            string newRest = RenameComboInner(rest);
                            if (newRest != rest) {
                                lines[i] = line.Substring(0, line.Length - trimmed.Length) + "组合键 " + newRest + " " + trimmed.Substring(eq);
                                continue;
                            }
                        }
                        if (KeyMap.TryGetValue(key, out to)) {
                            _appliedOldKeys.Add(key);   // **Opt-43**
                            lines[i] = line.Substring(0, line.Length - trimmed.Length) + to + " " + trimmed.Substring(eq);
                        }
                    }
                }
                text = string.Join("\n", lines);
                // "GC After Load" 已归实验页：从 [Freeplay] 搬到 [Experiments]，不搬的话老用户的值留在原段读不到
                text = MoveKeyBetweenSections(text, "Freeplay", "Experiments", "GC After Load");
                // [EX] 段里混着的"本仓库自带功能"归位（值必须跟着走，否则升级后这些设置回到默认）
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb Type");
                // 允许客户端删除是外部模块的功能：老配置原本就在 [EX]，中途曾搬到 [Destroy Blocks]，
                // 现在统一搬回 [EX]（幂等：已在 EX 段就不动）
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients");
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // **Opt-4（审计 F9）**：键搬家必须连 [Settings] 里的"组合键 <段> <键>"条目一起改，
                //  否则用户绑的 Shift/Ctrl/Alt 会静默丢失（条目名里的段名对不上了）。
                //  派对盒那三键有这一步（见下面 EX -> Level 的三条 MoveComboEntry），这里漏了。
                text = MoveComboEntry(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // 会话内容页的两个开关已从 [Settings] 归位到 [Chat]（功能自包含：配置跟功能走）。
                // 必须搬值，否则老用户这两个开关的当前状态会丢（回到默认）。
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Filter Quick Msgs");
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Show Time");
                // **Opt-3（审计 F6，破坏性迁移）**：老 cfg 的 [视野]（KeyMap 已改名为 [Camera]）里
                //  存着老用户的自由相机设置。原来只做了键改名 自由相机 -> Free Camera，**段没搬** ->
                //  值留在一个今天谁都不 Bind 的 orphan 段里，而功能实际绑在 [Experiments]
                //  （见 Features/Experiments.Misc.cs 的 "Free Camera" 等）-> 老用户的开关/FOV/按键
                //  全部读不到（文本不丢，但等于丢设置）。这里把值搬到功能的真家。
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera FOV");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera Key");
                // 组合键条目名是 "组合键 <段> <键>"，所以**每个键各来一次**：
                //  · "自由相机" 的条目（RenameComboInner 已把段名改成 Camera、键名改成 Free Camera）
                //  · "Free Camera Key" 的条目（键名本来就叫这个）
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera");
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera Key");
                // 已删功能的死键：不清就永远挂在配置里，用户会以为这些功能还在（段头由
                //  DeletedSections + NavHide 负责藏，键由这里清）。
                text = RemoveKeys(text, "Freeplay", new[] { "Map Key", "Map Enabled", "Treehouse Map" });
                text = RemoveKeys(text, "Support", new[] { "Cross Platform" });
                text = RemoveKeys(text, "Reflection", new[] { "Last Game Version" });
                text = RemoveKeys(text, "Respawn", new[] {
                    "Enabled", "Spawn Immunity", "Delay", "Spawn Points Enabled",
                    "Set Spawn Key", "Respawn Key", "Reset Spawn Keys"
                });
                // **R432**：关卡背景功能已按用户要求整体删除 -> 老配置里的这个键两个历史段都要清掉
                text = RemoveKeys(text, "Level", new[] { "Level Background" });
                text = RemoveKeys(text, "Scene", new[] { "Level Background" });
                // **R431**：派对盒（刷新/重抽/关闭）的界面在「关卡」页，代码也跟着搬进关卡功能了 -> 段从 [EX] 挪到 [Level]
                text = MoveKeyBetweenSections(text, "EX", "Level", "Refresh Party Box Key");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Refresh Pieces Key");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Close Party Box Key");
                // 这三个是键位（组合键存在 [Settings] 的"组合键 <段> <键>"条目里），条目名要一起搬
                text = MoveComboEntry(text, "EX", "Level", "Refresh Party Box Key");
                text = MoveComboEntry(text, "EX", "Level", "Refresh Pieces Key");
                text = MoveComboEntry(text, "EX", "Level", "Close Party Box Key");
                // **R431**：地图网格的界面在 EX 的「其它」页，代码也在 EX -> 段名从已删除功能的 [Freeplay] 改成 [EX]
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Always On");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Max Alpha");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Fade Time");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Refresh Color");
                // **R431**：[Reflection] 段只剩一个内部迁移标记（那套成员名覆盖早已删除）-> 并入隐藏的 [Internal]
                text = MoveKeyBetweenSections(text, "Reflection", "Internal", "P Suicide Migrated");
                // 已删除的功能：可动方块力度（整块删掉）、放置形态（整块删掉 -> [CC] 段相关键全清）、
                // Online 段里删掉的两个筛选
                text = RemoveKeys(text, "CC", new[] {
                    "BlockMod Enabled", "BlockMod Log", "BlockMod All", "BlockMod BaseMotion", "BlockMod Treadmill",
                    "BlockMod Wind", "BlockMod Gravity", "BlockMod GroundFriction", "BlockMod WallFriction",
                    "BlockMod JumpForce", "BlockMod AirInertia", "BlockMod Blackhole", "BlockMod Rotation",
                    "PlaceForm Enabled", "PlaceForm Key", "PlaceForm Key Prev", "PlaceForm Key Next",
                    "PlaceForm Steps", "PlaceForm Preview", "PlaceForm Log", "PlaceForm Modifier"
                });
                //列名带版本号：v1 "Column Widths"、v2 "Column Widths 2" 都要清（当前是 v3，
                //老值残留只是没人再读的废弃键，不清也不出错，清掉更干净）
                text = RemoveKeys(text, "Online", new[] { "Filter Limit", "Filter Mods", "Column Widths", "Column Widths 2", "Sort" });
                // [Online] 段的三条隐形快捷键已删除（联机页按钮本来就走 [Hotkeys] 的在线自动快捷键）：
                // 先把用户绑过的键搬进 [Hotkeys] 对应 id（Disband Key 与 Main Menu Key 是同一个动作），再删源键。
                text = MergeKeyValue(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MergeKeyValue(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MergeKeyValue(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                // **Opt-4（审计 F9）**：只搬主键值不够 —— 修饰键存在 [Settings] 的
                //  "组合键 <段> <键>" 条目里，而紧接着的 RemoveKeys 会把 "组合键 Online <旧键>" 一起删掉，
                //  于是"用户绑过的键不丢"这句注释只对裸键成立、修饰键静默丢失。
                //  先把条目名搬到新家（这一段段名和键名**同时**变，所以用 MoveComboEntryTo），再删源键。
                text = MoveComboEntryTo(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MoveComboEntryTo(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MoveComboEntryTo(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                text = RemoveKeys(text, "Online", new[] { "Refresh Lobbies Key", "Disband Key", "Main Menu Key" });
                // [Hotkeys] 段里那 12 个**没有任何界面件登记过**的历史 id，整批删掉：
                //   · exp.disband / exp.mainmenu / exp.lobbies：早期联机自己轮询键位留下的（现在归 online.）；
                //   · exp.add_question / remove_question / add_all_questions / clear_questions：出题功能已经删了；
                //   · level.party_bomb / map.treehouse / chat.filter_quick / chat.show_time / chat.hide_window：
                //     这些开关行现在走自动快捷键（row:Level.Party Bomb 之类），那几条旧 id 已经没人认领。
                // 它们不在任何 HkItem 里，界面上永远没有可右键绑键的入口，玩家按了也不会有反应，只是永久挂在 cfg 里。
                // 注意：row:<段>.<键> 开头的 id 是**通用条目行自动登记**的，绝不能按"源码里查不到"去清 —— 这里只删
                //   上面写死的这 12 个。运行时哪些 id 被登记要等各功能 Initialize 之后才完整，迁移阶段拿它做通用判断会误删。
                text = RemoveKeys(text, "Hotkeys", new[] {
                    "exp.disband", "exp.mainmenu", "exp.lobbies", "exp.clear_questions",
                    "exp.add_question", "exp.remove_question", "exp.add_all_questions",
                    "level.party_bomb", "map.treehouse",
                    "chat.filter_quick", "chat.show_time", "chat.hide_window"
                });
                // R381：删掉解除其它墙那 5 个逐面排查开关（实测全都不是真正的墙：改它们毫无作用）。
                // 真正的墙是关卡里的 TopBoundary / LeftBoundary / RightBoundary 三面实体碰撞体，
                // 已由新的Level Bounds Open Walls接管；这 5 个键留着只会让人以为还有更多"墙"可拆。
                text = RemoveKeys(text, "Experiments", new[] {
                    "Wall Cursor Collider", "Wall Piece Bounds Check", "Wall Ignore Bounds",
                    "Wall Grid Snap", "Wall Placement Rules"
                });
                // R383：多选系统（用户要求整体删除功能）与相机只跟随角色 / 限制最远视野
                // （用户判定黑域与相机无关、这两项纯属多余）都已移除 -> 键名一并清掉，
                // 免得老配置里留着没人再读的死键，让人以为还有这些功能。
                text = RemoveKeys(text, "build", new[] {
                    "Multi Select", "Multi Select Frame", "Multi Select Add Key",
                    "Multi Select Move", "Multi Select Copy", "Multi Select Delete", "Multi Select Hint"
                });
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Cam Follow", "Level Bounds Cam Frame", "Level Bounds Hide Wall Art"
                });
                // R388：树屋/遮罩/场景里删掉的功能（票数面板、房主就绪、屏蔽随机门、锁门、
                // 倒计时时长、遮罩信息增强、屏蔽加载音效、进关卡自动设模式、"第几个门"序号）
                text = RemoveKeys(text, "Scene", new[] {
                    "Lobby Auto Vote Index", "Lobby Countdown Sec", "Lobby Vote Panel", "Lobby Host Ready",
                    "Lobby Block Random", "Lobby Lock Vote", "Lobby Lock Votes Key",
                    "Splash Show Info", "Splash Mute", "Scene Set Mode", "Scene Mode",
                    // **R393**：取消倒计时这一项仍然是被删除的功能（用户 R392 明确要求），
                    //  R393 只恢复了 LobbyTools 里的立即开始，所以这条继续清理老配置。
                    // **R413**：自动投票整块删除（用户要求），这三个键一并清理，别留在老配置里。
                    "Lobby Cancel Countdown", "Lobby Auto Vote", "Lobby Auto Vote Level", "Lobby Auto Vote Teleport"
                });
                // R391 大删减：实验区五六整页 + p4 里的大部分分区 + 加载遮罩的时长/自动放行
                text = RemoveKeys(text, "Scene", new[] {
                    "Splash Auto Skip", "Splash Auto Skip Sec",
                    "Splash Fade In", "Splash Fade Out", "Splash Show", "Splash Hide",
                    "Solid Wall TopBoundary", "Solid Wall LeftBoundary", "Solid Wall RightBoundary",
                    "Solid Wall Wall", "Solid Wall HazardSolid (1)", "Solid Wall SolidCollider",
                    "Free Camera 2", "Free Camera 2 Speed", "Free Camera 2 Fov Min", "Free Camera 2 Fov Max",
                    "Free Camera 2 Reset Key", "Free Camera 2 Char Key", "Free Camera 2 Edge", "Free Camera 2 Edge Size",
                    "Death Zone", "Death Zone Line", "Death Zone Fill", "Death Zone Pit", "Death Zone Hazard"
                });
                // **Opt-42（真机实验查出的实缺陷）**：这份名单里原来还有 `Level Bounds Cursor` 与
                //  `Level Bounds Camera` —— 而它们**今天仍被 Experiments.Bounds.cs 正常绑定**
                //  （R432 把"解除关卡边界"拆成子开关时又加回来了）。后果是每次启动的循环：
                //      迁移把这两个键删掉 -> 功能 Bind 又把它们建成默认值 -> 下次启动再删 …
                //  于是这两个子开关**永远保存不下来**（用户勾了，重启就回默认），
                //  而且配置文件每次启动都被改写一次（"每次启动都判定有改动"的真正原因）。
                //  对真实 cfg 跑的实验（migtest 的 [H] 段）就是靠 (段,键) 集合比对把它抓出来的：
                //      被删/改名 = [Experiments] Level Bounds Cursor / Level Bounds Camera
                //                + [Reflection] Last Game Version / [Support] Cross Platform（后两条是我们要清的孤儿键）
                //  现在只保留确实没有任何代码绑定的键。这类"名单撞上活键"从此有运行时自检兜底（见 AuditAppliedRules）。
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
                // **Opt-7（审计 F9）**：cc.disband 与 cc.mainmenu 都映射到 online.disband（见 KeyMap），
                //  老 cfg 里两条都在时会产生同段重名键。按 BepInEx "后写覆盖先写"的解析语义只保留最后一条，
                //  生效值与今天一致，但文件不再留一条永远没人读的重复行。
                text = DedupeSectionKeys(text);
                if (text == orig) return false;
                // **P1-03**：配置文件是用户全部设置的唯一副本。原来直接 File.WriteAllText 覆盖，
                //  一旦中途崩溃 / 断电 / 被外部编辑器锁住，文件会被截断成 0 字节或半截内容，
                //  而且**没有任何备份可恢复**（用户下次启动看到的是全默认值）。
                //  改成"写临时文件 -> 原子替换 -> 留一份 .bak"：
                //   · 先写 <cfg>.tmp：只要写不完整，后面就不会去动正式文件
                //   · File.Replace 原子替换，同时把旧内容留成 <cfg>.bak（用户可手工回滚）
                //   · File.Replace 在部分文件系统 / 文件被占用时会抛，此时退回"先备份再覆盖写"
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
