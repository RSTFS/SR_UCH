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
        private static string RenameComboInner(string rest) {
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
                // 允许客户端删除是外部模块的功能：老配置原本就在 [EX]，中途曾搬到 [Destroy Blocks]，
                // 现在统一搬回 [EX]（幂等：已在 EX 段就不动）
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients");
                text = MoveKeyBetweenSections(text, "Destroy Blocks", "EX", "Allow Clients Key");
                // 会话内容页的两个开关已从 [Settings] 归位到 [Chat]（功能自包含：配置跟功能走）。
                // 必须搬值，否则老用户这两个开关的当前状态会丢（回到默认）。
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Filter Quick Msgs");
                text = MoveKeyBetweenSections(text, "Settings", "Chat", "Show Time");
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
                text = RemoveKeys(text, "Experiments", new[] {
                    "Level Bounds Log", "Level Bounds Open Walls", "Level Bounds Cursor", "Level Bounds Camera",
                    "Level Bounds Hide Wall Art"
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
