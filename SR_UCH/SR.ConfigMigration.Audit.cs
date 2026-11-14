// 配置迁移：把老版本 cfg 里的中文 section / key 换成英文，并清掉已删功能留下的配置段。
// 必须赶在任何 Config.Bind 之前运行；文件被改写后调用方随后 ConfigFile.Reload()。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    public static partial class ConfigMigration {
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
        // 本次启动内删除过的键（启动日志一次性汇总，audit 09-P0-01）
        private static System.Collections.Generic.List<string> _removedKeysLog = new System.Collections.Generic.List<string>();

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

        // 原子替换配置文件的唯一实现：写 .tmp -> File.Replace 并留 .bak，失败退回“先备份再覆盖写”。
        public static bool WriteCfgAtomic(string cfgPath, string text, string logTag) {
            try {
                string tmpPath = cfgPath + ".tmp";
                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                try { File.Replace(tmpPath, cfgPath, cfgPath + ".bak"); }
                catch (Exception __rep) {
                    try { File.Copy(cfgPath, cfgPath + ".bak", true); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    File.WriteAllText(cfgPath, text, new UTF8Encoding(false));
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
        // 第一遍：段名与键名改名（含组合键内嵌的 section/key），就地改写 lines。
        private static void RenameSectionsAndKeys(string[] lines) {
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
        }

        public static bool Migrate(string cfgPath) {
            try {
                if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) return false;
                string text = File.ReadAllText(cfgPath, Encoding.UTF8);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                // 基准须取在换行归一化之后、改名之前：取在归一化前（CRLF）text == orig 恒不成立，会每次启动白重写文件。
                // 取在改名之后，则只有改名（无搬移/清理）时 text == orig 成立、改名结果不落盘，迁移等于没生效。
                string orig = string.Join("\n", lines);
                RenameSectionsAndKeys(lines);
                text = string.Join("\n", lines);
                // "GC After Load" 已归实验页：从 [Freeplay] 搬到 [Experiments]，不搬值则原设置读不到。
                text = MoveKeyBetweenSections(text, "Freeplay", "Experiments", "GC After Load");
                // [EX] 段里混着的本仓库自带功能归位，值必须跟着走。
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb Type");
                                        // 键搬家必须连 [Settings] 里的 "组合键 <段> <键>" 条目一起改，否则已绑定的修饰键静默丢失。
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
            // 历史遗留键（由仓库外的旧版本写入）：已确认没有任何代码再读。
            text = RemoveKeys(text, "EX", new[] { "Clear Party Box Key", "Coin Amount" });
            // 快速自杀功能已删除：清理其遗留键。
            text = RemoveKeys(text, "Quick Adjust", new[] { "Enabled", "Keybind", "Score Discount", "More Discount Values" });
            text = RemoveKeys(text, "Internal", new[] { "P Suicide Migrated" });
            // 2026-11 删除的八个功能：清掉它们的入口键（其余键随段头一起隐藏）。
            text = RemoveKeys(text, "Match Brief", new[] { "Enabled", "Show Time", "Show Deaths", "Force Obituary" });
            text = RemoveKeys(text, "Accessibility", new[] { "Enabled", "Shape Labels", "Font Scale", "High Contrast" });
            text = RemoveKeys(text, "Host Console", new[] { "Enabled", "Show Roster" });
            text = RemoveKeys(text, "Level Recorder", new[] { "Enabled" });
            text = RemoveKeys(text, "Match Profile", new[] { "Enabled" });
            text = RemoveKeys(text, "Match Cast", new[] { "Enabled" });
            text = RemoveKeys(text, "Time Control", new[] { "Enabled", "Freeze" });
            text = RemoveKeys(text, "Death Report", new[] { "Enabled" });
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
                    // 方块破坏恢复旧实现：旧键重新生效，不再清理。
            text = RemoveKeys(text, "Settings", new[] { "Show Advanced" });
            // HUD 玩家面板已删除：[Round HUD] 段里的总开关、五个列开关与五个列颜色键必须清掉，
            // 否则老配置会留下永远不再读取的脏键（界面已无对应行）。
            text = RemoveKeys(text, "HUD", new[] { "Show Player Panel", "Show Number", "Show Name", "Show Animal", "Show Score", "Show Handicap", "Color Panel Number", "Color Panel Name", "Color Panel Animal", "Color Panel Score", "Color Panel Handicap" });
                text = RemoveKeys(text, "Respawn", new[] {
                    "Enabled", "Spawn Immunity", "Delay", "Spawn Points Enabled",
                    "Set Spawn Key", "Respawn Key", "Reset Spawn Keys"
                });
                // 关卡背景功能已删除：这个键在两个曾用段名里都要清掉。
                text = RemoveKeys(text, "Level", new[] { "Level Background", "Refresh Pieces Key" });
            // 自由相机相关项迁入"自由模式"段（用户要求把该分区移到自由模式页）。
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera");
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera FOV");
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera Speed");
            // 快捷键也搬进「自由模式」段：这样它才会作为"自由相机"行的 companion 显示在同一行。
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera Key");
            // 自身状态（无敌/完美无敌/飞天/蹲移/无视对象/无视碰撞 + 6 个快捷键）从 EX 搬到 SR 的"自由模式"段：
            // 用户要求 SR 的自身状态完全独立于 EX，值搬过来保证已有开关与快捷键继续生效。
            foreach (string __sk in new[] {
                "Invincible", "God Mode", "Fly", "Crouch Move", "Ignore Objects", "Ignore Collision",
                "Invincible Key", "God Mode Key", "Fly Key", "Crouch Move Key", "Ignore Objects Key", "Ignore Collision Key" }) {
                text = MoveKeyBetweenSections(text, "EX", "自由模式", __sk);
            }
            // 「无视对象」已删除（用户要求）：清掉它的两个键（开关 + 快捷键）。
            text = RemoveKeys(text, "自由模式", new[] { "Ignore Objects", "Ignore Objects Key" });
            text = RemoveKeys(text, "EX", new[] { "Ignore Objects", "Ignore Objects Key" });
            // 「蹲移」也已删除：上面把两个键从 EX 搬过来只是历史迁移，搬完没有代码再读，
            // 直接清掉，否则它们会以"配置孤儿键"长期留在 cfg 里（EX 老段里的残留同样清）。
            text = RemoveKeys(text, "自由模式", new[] { "Crouch Move", "Crouch Move Key" });
            text = RemoveKeys(text, "EX", new[] { "Crouch Move", "Crouch Move Key" });
            // 「房客提案」已删除（用户的提案功能整条删除）：段本身是混乱牌组的活段，
            // 只清它留下的两个死键。
            text = RemoveKeys(text, "混乱牌组", new[] { "AllowProposals", "ProposeKey" });
            // 地图网格移入 SR 的"自由模式"段（早期在 [EX]/[Freeplay]）：搬值保证老配置继续生效。
            // 2.0 起改回"复用游戏自带的 Graphpaper"，只保留「常显网格」一个开关——
            // 亮度/淡出距离/颜色三个键随自制网格一起删除，老配置里的残留值在这里清掉。
            text = MoveKeyBetweenSections(text, "EX", "自由模式", "Grid Always On");
            text = RemoveKeys(text, "自由模式", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
            text = RemoveKeys(text, "EX", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
            // 「死亡无计分板」功能已删除：清掉它的遗留键。
            text = RemoveKeys(text, "Quick Adjust", new[] { "No Death Scoreboard" });
            text = RemoveKeys(text, "Freeplay", new[] { "Grid Always On", "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
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
                text = RemoveKeys(text, "Freeplay", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
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
                // 早期热键条目名是 "组合键 <键>"（不带分区名），现方案是 "组合键 <段> <键>"。
                // 旧形式的条目已无任何代码读取（值都是空），连同紧贴它的注释一起清掉。
                text = PruneLegacyComboKeys(text);
                // cc.disband 与 cc.mainmenu 都映射到 online.disband（见 KeyMap），按“后写覆盖先写”只保留最后一条。
                text = DedupeSectionKeys(text);
                if (text == orig) return false;
                // 不再内联一份写盘逻辑：统一走 WriteCfgAtomic（评审 F1 P0-01：两份实现的失败路径不一致）。
                bool __ok = WriteCfgAtomic(cfgPath, text, "配置迁移·section/key");
            // 一次性汇总本次删除的键（audit 09-P0-01：删除必须可见，不能静默抹掉用户设置）。
            try {
                if (__ok && _removedKeysLog != null && _removedKeysLog.Count > 0) {
                    SRApi.LogInfo("[配置迁移] 本次删除 " + _removedKeysLog.Count + " 个已删功能的配置键: "
                        + string.Join(", ", _removedKeysLog.ToArray()));
                    _removedKeysLog.Clear();
                }
            } catch { /* 汇总日志失败不影响迁移 */ }
            return __ok;
            } catch (Exception __ex) { SR.Guard.Log("cfg section/key 迁移", __ex); return false; }
        }
    }
}
