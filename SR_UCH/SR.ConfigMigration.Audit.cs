// 配置迁移：把旧 cfg 里的中文段/键名换成英文，并清掉已删功能的配置键。
// 必须赶在任何 Config.Bind 之前运行；文件被改写后调用方须自行 ConfigFile.Reload()。
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

        // 功能/外部模块在 Initialize 里登记迁移规则（与 SR.Nav / SR.LocKey 放在一起）：
        // Section/Key 改名、Move 搬家、Remove 清死键；全部规则幂等。
        // 登记后由 SR.Core.EnsureScanned 补跑 RunPending。
        private static readonly Dictionary<string, string> _xSections = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _xKeys = new Dictionary<string, string>();
        private static readonly List<string[]> _xMoves = new List<string[]>();       // [from,to,key]
        private static readonly List<string[]> _xRemovals = new List<string[]>();    // [sec, key...]
        private static bool _xPending;
        // 本次启动内删除过的键（启动时一次性汇总到日志）
        private static System.Collections.Generic.List<string> _removedKeysLog = new System.Collections.Generic.List<string>();

        // 有已登记但尚未应用的规则（SR.Core 据此决定是否补跑）
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

        // 把一批 (段,键) 整行删掉；pairs 用 AuditSep 拼成 "段\u0001键"，组合键绑定行一并删。
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

        // 应用功能自注册的规则；幂等，返回 true 表示文件被改写（调用方应随后 Reload）。
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

        // 执行内置迁移；第一遍由 RenameSectionsAndKeys 改名，返回 true 表示文件被改写。
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
        // 基准须取在换行归一化之后、改名之前，否则会每次启动白重写文件或改名结果不落盘。
                string orig = string.Join("\n", lines);
                RenameSectionsAndKeys(lines);
                text = string.Join("\n", lines);
        // "GC After Load" 从 [Freeplay] 搬到 [Experiments]，不搬值则原设置读不到。
                text = MoveKeyBetweenSections(text, "Freeplay", "Experiments", "GC After Load");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb");
                text = MoveKeyBetweenSections(text, "EX", "Level", "Party Bomb Type");
                                        // 键搬家要连 [Settings] 的 "组合键 <段> <键>" 一起改，否则修饰键丢失。
                        // 会话内容页两个开关从 [Settings] 归位到 [Chat]，须搬值。
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Filter Quick Msgs");
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Show Time");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera FOV");
                text = MoveKeyBetweenSections(text, "Camera", "Experiments", "Free Camera Key");
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera");
                text = MoveComboEntry(text, "Camera", "Experiments", "Free Camera Key");
                // 已删功能的死键：段头由 DeletedSections + NavHide 隐藏，键由这里清理。
                text = RemoveKeys(text, "Freeplay", new[] { "Map Key", "Map Enabled", "Treehouse Map" });
                text = RemoveKeys(text, "Support", new[] { "Cross Platform" });
                text = RemoveKeys(text, "Reflection", new[] { "Last Game Version" });
            text = RemoveKeys(text, "Experiments", new[] { "Cross Platform", "Level Background" });
            text = RemoveKeys(text, "Online", new[] { "Hidden Columns" });
            // 历史遗留键（仓库外旧版本写入），已确认无代码再读。
            text = RemoveKeys(text, "EX", new[] { "Clear Party Box Key", "Coin Amount" });
            text = RemoveKeys(text, "Quick Adjust", new[] { "Enabled", "Keybind", "Score Discount", "More Discount Values" });
            text = RemoveKeys(text, "Internal", new[] { "P Suicide Migrated" });
            text = RemoveKeys(text, "Match Brief", new[] { "Enabled", "Show Time", "Show Deaths", "Force Obituary" });
            text = RemoveKeys(text, "Accessibility", new[] { "Enabled", "Shape Labels", "Font Scale", "High Contrast" });
            text = RemoveKeys(text, "Host Console", new[] { "Enabled", "Show Roster" });
            text = RemoveKeys(text, "Level Recorder", new[] { "Enabled" });
            text = RemoveKeys(text, "Match Profile", new[] { "Enabled" });
            text = RemoveKeys(text, "Match Cast", new[] { "Enabled" });
            text = RemoveKeys(text, "Time Control", new[] { "Enabled", "Freeze" });
            text = RemoveKeys(text, "Death Report", new[] { "Enabled" });
            text = RemoveKeys(text, "Match Replay", new[] { "Enabled", "Max Sessions", "Record Scores", "Record Blocks" });
            text = RemoveKeys(text, "Config Tools", new[] { "Enabled", "Max Backups" });
            text = RemoveKeys(text, "Experiments", new[] { "Mute Own", "Mute Others", "Level Background Scale", "Level Bounds Treehouse", "Level Bounds Death Line", "Level Bounds Death Drop", "Level Bounds Only In Place", "Level Bounds Direction", "Level Bounds Key" });
            text = RemoveKeys(text, "Scene", new[] { "Render Hide Skybox", "Render Bg Color", "Scene Bounds", "Scene Spawn", "Scene Death Line", "Scene Death Line Len" });
            text = RemoveKeys(text, "Level", new[] { "Build Mode", "Build File", "Score Fill Per Frame", "Score Fill Total Cap", "Party Bomb Radius", "Party Bomb Trigger Ratio" });
            text = RemoveKeys(text, "Online", new[] { "Column Widths 3" });
            // 联机表格 v5 删掉「技能系数」列（旧 v4 索引 3）：列宽/隐藏列须先裁剪索引再写进 v5 键，
            // 否则旧索引会指到别的列；Column Order 无版本号，改由 Online 加载时裁剪。
            text = MoveMappedKey(text, "Online", "Hidden Columns 4", "Online", "Hidden Columns 5", MapOnlineV4HiddenCols);
            text = MoveMappedKey(text, "Online", "Column Widths 4", "Online", "Column Widths 5", MapOnlineV4ColWidths);
            text = RemoveKeys(text, "Online", new[] { "Hidden Columns 4", "Column Widths 4", "Skill Mode" });
            text = RemoveKeys(text, "Player Tracker", new[] { "Show Own Trail" });
            text = RemoveKeys(text, "Settings", new[] { "Show Advanced" });
            // [Round HUD] 玩家面板已删除：清掉总开关、列开关与列颜色键，否则留下永不读取的脏键。
            text = RemoveKeys(text, "HUD", new[] { "Show Player Panel", "Show Number", "Show Name", "Show Animal", "Show Score", "Show Handicap", "Color Panel Number", "Color Panel Name", "Color Panel Animal", "Color Panel Score", "Color Panel Handicap" });
                text = RemoveKeys(text, "Respawn", new[] {
                    "Enabled", "Spawn Immunity", "Delay", "Spawn Points Enabled",
                    "Set Spawn Key", "Respawn Key", "Reset Spawn Keys"
                });
                text = RemoveKeys(text, "Level", new[] { "Level Background", "Refresh Pieces Key" });
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera");
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera FOV");
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera Speed");
            text = MoveKeyBetweenSections(text, "Experiments", "自由模式", "Free Camera Key");
            foreach (string __sk in new[] {
                "Invincible", "God Mode", "Fly", "Crouch Move", "Ignore Objects", "Ignore Collision",
                "Invincible Key", "God Mode Key", "Fly Key", "Crouch Move Key", "Ignore Objects Key", "Ignore Collision Key" }) {
                text = MoveKeyBetweenSections(text, "EX", "自由模式", __sk);
            }
            text = RemoveKeys(text, "自由模式", new[] { "Ignore Objects", "Ignore Objects Key" });
            text = RemoveKeys(text, "EX", new[] { "Ignore Objects", "Ignore Objects Key" });
            text = RemoveKeys(text, "自由模式", new[] { "Crouch Move", "Crouch Move Key" });
            text = RemoveKeys(text, "EX", new[] { "Crouch Move", "Crouch Move Key" });
            text = RemoveKeys(text, "混乱牌组", new[] { "AllowProposals", "ProposeKey" });
            // 地图网格只保留「常显网格」；亮度/淡出距离/颜色三个旧键一并清掉。
            text = MoveKeyBetweenSections(text, "EX", "自由模式", "Grid Always On");
            text = RemoveKeys(text, "自由模式", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
            text = RemoveKeys(text, "EX", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
            text = RemoveKeys(text, "Quick Adjust", new[] { "No Death Scoreboard" });
            text = RemoveKeys(text, "Freeplay", new[] { "Grid Always On", "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
            text = RemoveKeys(text, "Online", new[] { "Filter Min Players", "Filter AFK" });
            text = RemoveKeys(text, "Hotkeys", new[] { "level.broadcast" });
                text = RemoveKeys(text, "Scene", new[] { "Level Background" });
                text = MoveKeyBetweenSections(text, "EX", "Level", "Refresh Party Box Key");
                    text = MoveKeyBetweenSections(text, "EX", "Level", "Close Party Box Key");
                text = MoveComboEntry(text, "EX", "Level", "Refresh Party Box Key");
                    text = MoveComboEntry(text, "EX", "Level", "Close Party Box Key");
                text = MoveKeyBetweenSections(text, "Freeplay", "EX", "Grid Always On");
                text = RemoveKeys(text, "Freeplay", new[] { "Grid Max Alpha", "Grid Fade Time", "Grid Refresh Color" });
                // [Reflection] 只剩内部迁移标记，并入隐藏的 [Internal]。
                text = MoveKeyBetweenSections(text, "Reflection", "Internal", "P Suicide Migrated");
                text = RemoveKeys(text, "CC", new[] {
                    "BlockMod Enabled", "BlockMod Log", "BlockMod All", "BlockMod BaseMotion", "BlockMod Treadmill",
                    "BlockMod Wind", "BlockMod Gravity", "BlockMod GroundFriction", "BlockMod WallFriction",
                    "BlockMod JumpForce", "BlockMod AirInertia", "BlockMod Blackhole", "BlockMod Rotation",
                    "PlaceForm Enabled", "PlaceForm Key", "PlaceForm Key Prev", "PlaceForm Key Next",
                    "PlaceForm Steps", "PlaceForm Preview", "PlaceForm Log", "PlaceForm Modifier"
                });
                text = RemoveKeys(text, "Online", new[] { "Filter Limit", "Filter Mods", "Column Widths", "Column Widths 2", "Sort" });
                // [Online] 三条隐形快捷键已删除：先把值搬进 [Hotkeys] 对应 id，再删源键。
                text = MergeKeyValue(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MergeKeyValue(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MergeKeyValue(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                // 只搬主键值不够：修饰键在 [Settings] 的 "组合键 <段> <键>" 条目里，须先改名再删源键。
                text = MoveComboEntryTo(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MoveComboEntryTo(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MoveComboEntryTo(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                text = RemoveKeys(text, "Online", new[] { "Refresh Lobbies Key", "Disband Key", "Main Menu Key" });
                // [Hotkeys] 里 12 个无界面件登记的旧 id 整批删掉。
                // row:<段>.<键> 由通用条目行自动登记，不能按“源码里查不到”清理。
                text = RemoveKeys(text, "Hotkeys", new[] {
                    "exp.disband", "exp.mainmenu", "exp.lobbies", "exp.clear_questions",
                    "exp.add_question", "exp.remove_question", "exp.add_all_questions",
                    "level.party_bomb", "map.treehouse",
                    "chat.filter_quick", "chat.show_time", "chat.hide_window"
                });
                text = RemoveKeys(text, "Experiments", new[] {
                    "Wall Cursor Collider", "Wall Piece Bounds Check", "Wall Ignore Bounds",
                    "Wall Grid Snap", "Wall Placement Rules"
                });
                // 解除关卡边界子项键已废弃（已无 Config.Bind；构建期守卫会交叉核对）。
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Margin", "Level Bounds Hard", "Level Bounds Walls",
                    "Level Bounds Cursor", "Level Bounds Camera", "Level Bounds Piece Ignore",
                    "Remove UI3"
                });
                text = RemoveKeys(text, "build", new[] {
                    "Multi Select", "Multi Select Frame", "Multi Select Add Key",
                    "Multi Select Move", "Multi Select Copy", "Multi Select Delete", "Multi Select Hint"
                });
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Cam Follow", "Level Bounds Cam Frame", "Level Bounds Hide Wall Art"
                });
                text = RemoveKeys(text, "Scene", new[] {
                    "Lobby Auto Vote Index", "Lobby Countdown Sec", "Lobby Vote Panel", "Lobby Host Ready",
                    "Lobby Block Random", "Lobby Lock Vote", "Lobby Lock Votes Key",
                    "Splash Show Info", "Splash Mute", "Scene Set Mode", "Scene Mode",
                    "Lobby Cancel Countdown", "Lobby Auto Vote", "Lobby Auto Vote Level", "Lobby Auto Vote Teleport"
                });
                text = RemoveKeys(text, "Scene", new[] {
                    "Splash Auto Skip", "Splash Auto Skip Sec",
                    "Splash Fade In", "Splash Fade Out", "Splash Show", "Splash Hide",
                    "Solid Wall TopBoundary", "Solid Wall LeftBoundary", "Solid Wall RightBoundary",
                    "Solid Wall Wall", "Solid Wall HazardSolid (1)", "Solid Wall SolidCollider",
                    "Free Camera 2", "Free Camera 2 Speed", "Free Camera 2 Fov Min", "Free Camera 2 Fov Max",
                    "Free Camera 2 Reset Key", "Free Camera 2 Char Key", "Free Camera 2 Edge", "Free Camera 2 Edge Size",
                    "Death Zone", "Death Zone Line", "Death Zone Fill", "Death Zone Pit", "Death Zone Hazard"
                });
                // 名单只留无任何代码绑定的键；撞上活键由运行时自检兜底（见 AuditAgainst）。
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
                // 旧式 "组合键 <键>"（不带段名）条目已无代码读取，连同紧贴的注释一起清掉。
                text = PruneLegacyComboKeys(text);
                // cc.disband 与 cc.mainmenu 都映射到 online.disband，后写覆盖先写。
                text = DedupeSectionKeys(text);
                if (text == orig) return false;
                // 统一走 WriteCfgAtomic，保证失败路径一致。
                bool __ok = WriteCfgAtomic(cfgPath, text, "配置迁移·section/key");
            // 一次性汇总本次删除的键（删除要可见，不能静默抹掉用户设置）。
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
