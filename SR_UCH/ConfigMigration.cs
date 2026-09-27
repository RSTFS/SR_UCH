using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    internal static class ConfigMigration {
        // section 头：[旧] → [新]
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
        };

        // key 名（行首 键名 = 值）：旧中文 key → 新英文 key
        private static readonly Dictionary<string, string> KeyMap = new Dictionary<string, string> {
            { "解除建造上限", "Lift Build Cap" },
            { "上限数值", "Build Cap Value" },
            { "自由相机", "Free Camera" },
            { "地图总开关", "Map Enabled" },
            { "过滤快捷消息", "Filter Quick Msgs" },
            { "显示时间", "Show Time" },
            { "加载后清理", "GC After Load" },
            // 联机栏目的自动快捷键：老 id 带着 CC 栏目的痕迹（cc.*）→ 改成 online.*
            // （[Hotkeys] 段的键名就是这个 id，连同 "组合键 Hotkeys <id>" 一起迁移，用户绑过的键不丢）
            { "cc.lobbies", "online.lobbies" },
            { "cc.disband", "online.disband" },
            // 老的 cc.mainmenu 没有对应的 id 了（联机页两个按钮已合并成「返回主界面」）→ 并到同动作的 online.disband
            { "cc.mainmenu", "online.disband" },
        };

        // 组合键条目内层 "<section> <key>" 改名（key 本身可能含空格，故先匹配 section）
        private static string RenameComboInner(string rest) {
            foreach (KeyValuePair<string, string> kv in SectionMap) {
                if (rest.StartsWith(kv.Key + " ", StringComparison.Ordinal))
                    return kv.Value + " " + RenameKeyIn(rest.Substring(kv.Key.Length + 1));
            }
            // 段名没变、只有 key 改名的情况（如 [Hotkeys] 的 cc.lobbies → online.lobbies）：
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
            return KeyMap.TryGetValue(key, out to) ? to : key;
        }

        // 把某个 key 行从 from 段搬到 to 段（幂等：已不在 from 段就原样返回）
        private static string MoveKeyBetweenSections(string text, string from, string to, string key) {
            try {
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
                    int insertAt = toHdr + 1;
                    outp.Insert(insertAt, moved);
                }
                return string.Join("\n", outp.ToArray());
            } catch (Exception __ex) { SR.Guard.Log("cfg 行搬移", __ex); return text; }
        }

        // 删掉已删除功能的废弃键（幂等：不在就原样返回）。带 "组合键 <段> <键>" 的绑定行一起删。
        private static string RemoveKeys(string text, string section, string[] keys) {
            try {
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
                    if (cur == section) {
                        int eq = t.IndexOf('=');
                        if (eq > 0) {
                            string k = t.Substring(0, eq).TrimEnd();
                            bool drop = false;
                            for (int j = 0; j < keys.Length; j++) {
                                if (k == keys[j] || k.StartsWith("组合键 " + section + " " + keys[j], StringComparison.Ordinal)) { drop = true; break; }
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

        // 返回 true 表示文件被改写过（调用方应随后 ConfigFile.Reload()）
        public static bool Migrate(string cfgPath) {
            try {
                if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) return false;
                string text = File.ReadAllText(cfgPath, Encoding.UTF8);
                string orig = text;
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++) {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    // section 头
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)) {
                        string name = trimmed.Substring(1, trimmed.Length - 2);
                        string to;
                        if (SectionMap.TryGetValue(name, out to)) {
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
                // 「允许客户端删除」是外部模块的功能：老配置原本就在 [EX]，中途曾搬到 [Destroy Blocks]，
                // 现在统一搬回 [EX]（幂等：已在 EX 段就不动）
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients");
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // 会话内容页的两个开关已从 [Settings] 归位到 [Chat]（功能自包含：配置跟功能走）。
                // 必须搬值，否则老用户这两个开关的当前状态会丢（回到默认）。
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Filter Quick Msgs");
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Show Time");
                // 已删除的功能：可动方块力度（整块删掉）、放置形态（整块删掉 → [CC] 段相关键全清）、
                // Online 段里删掉的两个筛选
                text = RemoveKeys(text, "CC", new[] {
                    "BlockMod Enabled", "BlockMod Log", "BlockMod All", "BlockMod BaseMotion", "BlockMod Treadmill",
                    "BlockMod Wind", "BlockMod Gravity", "BlockMod GroundFriction", "BlockMod WallFriction",
                    "BlockMod JumpForce", "BlockMod AirInertia", "BlockMod Blackhole", "BlockMod Rotation",
                    "PlaceForm Enabled", "PlaceForm Key", "PlaceForm Key Prev", "PlaceForm Key Next",
                    "PlaceForm Steps", "PlaceForm Preview", "PlaceForm Log", "PlaceForm Modifier"
                });
                text = RemoveKeys(text, "Online", new[] { "Filter Limit", "Filter Mods", "Column Widths", "Sort" });
                // [Online] 段的三条隐形快捷键已删除（联机页按钮本来就走 [Hotkeys] 的在线自动快捷键）：
                // 先把用户绑过的键搬进 [Hotkeys] 对应 id（Disband Key 与 Main Menu Key 是同一个动作），再删源键。
                text = MergeKeyValue(text, "Online", "Refresh Lobbies Key", "Hotkeys", "online.lobbies");
                text = MergeKeyValue(text, "Online", "Disband Key", "Hotkeys", "online.disband");
                text = MergeKeyValue(text, "Online", "Main Menu Key", "Hotkeys", "online.disband");
                text = RemoveKeys(text, "Online", new[] { "Refresh Lobbies Key", "Disband Key", "Main Menu Key" });
                if (text == orig) return false;
                //不生成 .bak 备份：迁移幂等、只改 section/key 名（值原样保留），多出的旧配置容易被误读
                File.WriteAllText(cfgPath, text, new UTF8Encoding(false));
                return true;
            } catch (Exception __ex) { SR.Guard.Log("cfg section/key 迁移", __ex); return false; }
        }
    }
}
