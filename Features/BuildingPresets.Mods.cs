// 布局预设 · 修改器策略（BuildingPresets 的第二个分部文件）。
// 关卡快照里的 <mods .../> 就是游戏的关卡修改器：24 个属性（15 个模式索引 + 9 个开关）。
// 预设默认原样继承这份修改器；本页允许对每条预设单独选择：
//   继承 = 用预设自带的（默认，行为与以前一致）
//   关闭 = 全部还原为默认值（等于"没有修改器"）
//   自定义 = 由用户逐项设定，加载时覆盖预设自带的值
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {

        // 一条修改器属性的描述：Attr 是快照 XML 里 <mods> 的属性名。
        private sealed class ModDef {
            public readonly string Attr;
            public readonly string Zh;
            public readonly string En;
            public readonly bool IsBool;   // true = 开关（True/False）；false = 模式索引（0 = 默认）
            public ModDef(string attr, string zh, string en, bool isBool) {
                Attr = attr; Zh = zh; En = en; IsBool = isBool;
            }
        }

        // 顺序与游戏写入顺序一致；新增属性只追加在末尾（索引不改写）。
        private static readonly ModDef[] Mods = new ModDef[] {
            new ModDef("GravityMode",             "重力",       "Gravity",            false),
            new ModDef("JumpSpeedMode",           "跳跃高度",   "Jump speed",         false),
            new ModDef("SprintSpeedMode",         "冲刺速度",   "Sprint speed",       false),
            new ModDef("GameSpeedMode",           "游戏速度",   "Game speed",         false),
            new ModDef("InvisibilityMode",        "隐身",       "Invisibility",       false),
            new ModDef("PlatformSpeedMode",       "平台速度",   "Platform speed",     false),
            new ModDef("RateOfFireMode",          "射速",       "Rate of fire",       false),
            new ModDef("MultiJumpMode",           "多段跳",     "Multi jump",         false),
            new ModDef("ProjectileExplosionMode", "子弹爆炸",   "Projectile blast",   false),
            new ModDef("CharacterSizeMode",       "角色大小",   "Character size",     false),
            new ModDef("PostDeathBehaviorMode",   "死亡后行为", "Post-death",         false),
            new ModDef("CameraFlipMode",          "相机翻转",   "Camera flip",        false),
            new ModDef("DoomsdayMeteorsMode",     "末日陨石",   "Doomsday meteors",   false),
            new ModDef("DoomsdayLavaMode",        "末日岩浆",   "Doomsday lava",      false),
            new ModDef("ProjectileSpeedMode",     "子弹速度",   "Projectile speed",   false),
            new ModDef("JetpackMode",             "喷气背包",   "Jetpack",            true),
            new ModDef("WallJumpsDisabled",       "禁用蹬墙跳", "No wall jumps",      true),
            new ModDef("WallSlidesDisabled",      "禁用贴墙滑", "No wall slides",     true),
            new ModDef("DanceInvincibility",      "跳舞无敌",   "Dance invincible",   true),
            new ModDef("MirrorControls",          "反向操控",   "Mirror controls",    true),
            new ModDef("PlayerPlayerCollisions",  "玩家互撞",   "Player collisions",  true),
            new ModDef("PreviewModsInTreehouse",  "树屋预览",   "Preview in treehouse", true),
            new ModDef("ForceLobbyModifiers",     "强制大厅修改器", "Force lobby mods", true),
            new ModDef("Frictionless",            "无摩擦",     "Frictionless",       true)
        };

        // 策略取值："" = 继承预设（默认）；"off" = 全部默认；"custom" = 用自定义值覆盖。
        private const string ModsInherit = "";
        private const string ModsOff = "off";
        private const string ModsCustom = "custom";

        // 编辑器状态（不落配置：打开时从预设/索引读，保存时写回索引）。
        private static readonly Dictionary<string, string> _modsDraft = new Dictionary<string, string>();
        // 规则 / 积分设置 / 模块概率：加载该预设时是否一并应用（草稿）。

        private static string ModDefault(ModDef d) { return d.IsBool ? "False" : "0"; }


        // ---------- 索引持久化：modsMode / modsVals 两个纯文本字段 ----------
        // modsVals 用 "attr=value;attr=value" 编码（属性名与取值都是字母数字，无需转义；仍按 JsonEsc 写字符串）。

        private static string ModsValsToText(PresetInfo p) {
            try {
                if (p == null || p.ModsVals == null || p.ModsVals.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Mods.Length; i++) {
                    string v;
                    if (!p.ModsVals.TryGetValue(Mods[i].Attr, out v) || v == null) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(Mods[i].Attr).Append('=').Append(v);
                }
                return sb.ToString();
            } catch { return ""; }
        }

        private static void ParseModsVals(PresetInfo p, string text) {
            try {
                if (p == null || string.IsNullOrEmpty(text)) return;
                p.ModsVals = new Dictionary<string, string>();
                string[] parts = text.Split(';');
                for (int i = 0; i < parts.Length; i++) {
                    string kv = parts[i];
                    if (string.IsNullOrEmpty(kv)) continue;
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = kv.Substring(0, eq).Trim();
                    string v = kv.Substring(eq + 1).Trim();
                    if (k.Length == 0) continue;
                    p.ModsVals[k] = v;
                }
            } catch { /* 静默：索引被手改坏时按"无自定义值"处理，不影响预设加载 */ }
        }

        // ---------- 应用到快照 XML ----------
        // 只在加载前调用（列表统计/导出不受影响）：把 <mods> 的 24 个属性按策略改写。
        // 返回（可能被修改过的）XML 字符串；任何解析异常都原样返回输入，绝不会让预设加载失败。
        private static string ApplyModsPolicy(string xml, PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return xml;         // 游戏本地关卡是只读数据，不做改写
                string mode = p.ModsMode ?? ModsInherit;
                if (mode != ModsOff && mode != ModsCustom) return xml;
                if (string.IsNullOrEmpty(xml)) return xml;
                XmlDocument doc = ParseXmlSafe(xml);
                if (doc == null || doc.DocumentElement == null) return xml;
                XmlElement mods = doc.DocumentElement.SelectSingleNode("mods") as XmlElement;
                if (mods == null) {
                    mods = doc.CreateElement("mods");
                    doc.DocumentElement.PrependChild(mods);
                }
                for (int i = 0; i < Mods.Length; i++) {
                    ModDef d = Mods[i];
                    string v = null;
                    if (mode == ModsCustom && p.ModsVals != null) p.ModsVals.TryGetValue(d.Attr, out v);
                    mods.SetAttribute(d.Attr, string.IsNullOrEmpty(v) ? ModDefault(d) : v);
                }
                SRApi.LogInfo("[布局预设] 修改器策略=" + (mode == ModsOff ? "关闭" : "自定义") + "，已改写 " + p.Name + " 的快照 mods");
                return doc.OuterXml;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/修改器改写", __ex);
                return xml;
            }
        }





        // 表内显示用短标签：继承显示 "-"（不改动），关闭显示「关」，自定义显示「改」。
        private static string ModsLabel(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return "-";
                string m = p.ModsMode ?? ModsInherit;
                if (string.Equals(m, ModsOff, StringComparison.Ordinal)) return SRApi.T("关闭", "Off");
                // 历史数据里的 ModsCustom 一律按"预设"显示：真值已迁回 XML 的 <mods>。
                return SRApi.T("预设", "Preset");
            } catch { return "-"; }
        }

        // 两态切换（用户要求取消旁车，XML 的 <mods> 是唯一真值）：
        //   预设 = 加载时用 XML 里的 <mods>；关闭 = 加载时把修改器写成默认值（等于不用这套修改器）。
        private static void CycleDataMode(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                string m = p.ModsMode ?? ModsInherit;
                string next = string.Equals(m, ModsOff, StringComparison.Ordinal) ? ModsInherit : ModsOff;
                p.ModsMode = next;
                // 兼容旧数据：进入"预设"态时把遗留的索引自定义值丢掉（真值已回到 XML）。
                if (string.Equals(next, ModsInherit, StringComparison.Ordinal)) p.ModsVals = null;
                SaveIndex();
                SRApi.Notify(SRApi.T("数据：", "Data: ") + ModsLabel(p) + " · " + (p.Name ?? ""));
                SRApi.LogInfo("[布局预设] 数据模式切换: " + (p.Name ?? "") + " -> " + ModsLabel(p));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/切换数据模式", __ex); }
        }





        // ---------- 下拉框（数值项）：选项与文案都取自游戏 Modifiers 自带的取值表 / 名称函数 ----------










        // ---------- 规则 / 积分设置 / 模块概率（游戏的 GameRulePreset） ----------
        // 保存预设时把当前规则集写成 sidecar（<预设>.xml.ruleset.xml），加载时按三个开关选择性应用。
        // 用游戏自己的入口：GameRulePreset.GetRulesetFromCurrentRules / GetRulesetXmlString（读），
        // LoadRulesetFromXML + GameSettings.ApplyTemporaryRuleset（写；mods 由修改器策略单独处理，这里传 false）。
        // 侧车放在 <预设根>/rulesets/<模式>/<预设文件名> —— 单独子目录，绝不混进预设列表。
        private const string RulesetsDirName = "rulesets";

        // 加载前调用：按该预设的三个开关，把 sidecar 里的规则集应用到当前对局（房主侧）。
        // ---------- 借用游戏自己的界面 ----------
        // 打开游戏规则书（TabletRulesScreen 那一套）：规则 / 积分 / 模块概率 / 修改器都在里面，
        // 玩家改完关掉，再回本页点「读取游戏当前」。必须先把本面板收起来，否则输入被冻结、游戏界面点不动。
        // 正在游戏规则书里编辑的预设 Key（null = 没在编辑）；_bookSeenOpen = 已确认打开过一次（用来识别"已关闭"）

        // 由预设文件路径推出侧车路径（保存时用，此时 PresetInfo 可能还没建好）。
        // 打开游戏自己的规则书（InventoryBook）：
        //   1) 若传了预设，先把它的规则/积分/模块/修改器灌进游戏，打开后看到的就是这条预设的数据；
        //   2) GameRuleBook 可能还没生成 —— 先 SpawnRuleBook（私有，反射），初始化回调里 Show(true) + 定位页面；
        //   3) 玩家关掉规则书后由 TickRulebook 回收：读回数据 -> 写进预设 -> 自动勾选"自定义" -> 自动弹回本面板。
        // 场景里的平板（规则/修改器界面的载体）。
        // 场景里的规则书道具（树屋里的那本）。关卡里通常没有 -> 返回 null。
        // 走 InventoryBook：AddPlayer（放进光标）+ Show + 翻到规则页；这条路径关卡内也能用。
        // 生成规则书对象（SpawnRuleBook 是私有方法，走反射；失败只影响这一次打开）。
        // 把预设内容灌进游戏（打开规则书前调用）：规则/积分/模块按勾选应用，修改器一律按预设值写。
        // 每 0.25 秒看一次：规则书从"打开"变"关闭"就回收数据。
        // 规则书已关闭：把游戏当前内容读回这条预设，自动设为"自定义"，再自动弹回本面板。
        // 读游戏当前的规则集 -> 写进这条预设的 sidecar，并把三个开关打开。
        // 读游戏当前的 24 项修改器 -> 直接写回该预设 XML 的 <mods>（不再经过旁车 / 自定义草稿）。
        // 把草稿里的 24 项修改器写进预设 XML 的 <mods> 节点（XML 是唯一真值，不再写旁车 / 索引）。
        // 把修改器的原始值翻成游戏自己的本地化文本（可读性要求）：优先调用 Modifiers 的
        // Get*ValueString；布尔项走 GetOnOffValueString；两者都拿不到时退回原始值。
        private static string ModValueText(ModDef d, string raw) {
            try {
                if (string.IsNullOrEmpty(raw)) return "";
                if (!d.IsBool) {
                    int iv;
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) return raw;
                    string fn = null;
                    switch (d.Attr) {
                        case "GravityMode": fn = "GetGravityValueString"; break;
                        case "JumpSpeedMode": fn = "GetJumpSpeedValueString"; break;
                        case "SprintSpeedMode": fn = "GetSprintSpeedValueString"; break;
                        case "ProjectileSpeedMode": fn = "GetSprintSpeedValueString"; break;
                        case "GameSpeedMode": fn = "GetSprintSpeedValueString"; break;
                        case "MultiJumpMode": fn = "GetMultiJumpValueString"; break;
                        case "ProjectileExplosionMode": fn = "GetProjectileExplosionValueString"; break;
                        case "CharacterSizeMode": fn = "GetCharacterSizeValueString"; break;
                        case "PostDeathBehaviorMode": fn = "GetPostDeathBehaviorValueString"; break;
                        case "CameraFlipMode": fn = "GetCameraFlipValueString"; break;
                        case "InvisibilityMode": fn = "GetInvisibilityModeValueString"; break;
                        case "DoomsdayMeteorsMode": fn = "GetDoomsdayMeteorsValueString"; break;
                        case "DoomsdayLavaMode": fn = "GetDoomsdayLavaValueString"; break;
                    }
                    if (fn != null) {
                        MethodInfo mi = SRApi.Ref.Method(typeof(Modifiers), fn, "布局预设/修改器预览取值文本");
                        if (mi != null) {
                            object[] args = mi.GetParameters().Length >= 2 ? new object[] { iv, 0 } : new object[] { iv };
                            object r = mi.Invoke(null, args);
                            string s = r as string;
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
                    }
                    return raw;   // 游戏没有对应文案（如平台速度）：保留数值，避免显示空白
                }
                // 布尔项：走游戏的"开/关"本地化
                bool bv = string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase) || raw == "1";
                // 反射登记：修改器布尔项文案靠这个方法，游戏改名后自检要能报出来。
                SRApi.Ref.Override("Modifiers.GetOnOffValueString", "布局预设：修改器布尔项文案");
                MethodInfo onOff = SRApi.Ref.Method(typeof(Modifiers), "GetOnOffValueString", "布局预设/修改器预览开关文本");
                if (onOff != null) {
                    object r = onOff.Invoke(null, new object[] { bv });
                    string s = r as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return bv ? SRApi.T("开", "on") : SRApi.T("关", "off");
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/修改器预览取值文本", __ex);
                return raw;
            }
        }

        // 从预设 XML 的 <mods> 取"非默认项"，格式「中文名=值」，供数据格悬浮预览（最多 10 项）。
        private static string ModsSummaryFromXml(PresetInfo p, int maxItems) {
            try {
                if (p == null || p.IsLocal) return "";
                string path = p.FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                XmlDocument doc = ParseXmlSafe(ReadAllText(path));
                if (doc == null || doc.DocumentElement == null) return "";
                XmlElement mods = doc.DocumentElement.SelectSingleNode("mods") as XmlElement;
                if (mods == null) return "";
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < Mods.Length && shown < maxItems; i++) {
                    ModDef d = Mods[i];
                    string v = mods.GetAttribute(d.Attr);
                    if (string.IsNullOrEmpty(v)) continue;
                    if (string.Equals(v, ModDefault(d), StringComparison.OrdinalIgnoreCase)) continue;   // 只列非默认
                    if (shown > 0) sb.Append(SRApi.T(" · ", " · "));
                    sb.Append(SRApi.T(d.Zh, d.En)).Append('=').Append(ModValueText(d, v));
                    shown++;
                }
                return sb.ToString();
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/修改器预览", __ex);
                return "";
            }
        }

        // 从预设 XML 的 <mods> 读回 24 项（用于"清空"后回显 / 数据列统计）。
        private static int ModsCountInXml(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return 0;
                string path = p.FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
                XmlDocument doc = ParseXmlSafe(ReadAllText(path));
                if (doc == null || doc.DocumentElement == null) return 0;
                XmlElement mods = doc.DocumentElement.SelectSingleNode("mods") as XmlElement;
                return mods == null ? 0 : mods.Attributes.Count;
            } catch { return 0; }
        }



        // ---------- 行内单元格交互 ----------
        // 数据列：悬浮预览 / 左键切换 预设-自定义-关闭 / 右键打开数据界面
        // 名称列：右键重命名（操作列不再放重命名按钮）
        private static void HandlePresetCells() {
            try {
                Event e = Event.current;
                if (e == null || _args == null || _args.rowRects == null || _args.vis == null || _args.colW == null) return;
                if (e.type == EventType.Layout) return;
                Vector2 mp = e.mousePosition;
                if (!_args.box.Contains(mp)) return;
                int row = -1;
                for (int i = 0; i < _args.rowRects.Count && i < _args.rowRows.Count; i++) {
                    if (_args.rowRects[i].Contains(mp)) { row = _args.rowRows[i]; break; }
                }
                if (row < 0) return;
                int col = ColumnAtX(mp.x);
                if (col < 0) return;
                PresetInfo p = _currentVisibleRow(row);
                if (p == null) return;
                if (col == ColMods) {
                    string tip = DataPreviewTip(p);
                    if (!string.IsNullOrEmpty(tip)) GUI.tooltip = tip;
                    if (e.type == EventType.MouseDown && e.button == 0) { _cellDownRow = row; _cellDownCol = col; }
                    if (e.type == EventType.MouseUp && e.button == 0) {
                        // 必须是"同一格里按下再松开"，否则拖列宽/拖滚动条时松手会误切
                        if (_cellDownRow == row && _cellDownCol == col) { CycleDataMode(p); e.Use(); }
                        _cellDownRow = -1; _cellDownCol = -1;
                        return;
                    }
                }
                if (col == ColName && e.type == EventType.MouseDown && e.button == 1) {
                    if (!p.IsLocal) { ClearPending(); _renameKey = p.Key; _renameText = p.Name; e.Use(); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/行内单元格", __ex); }
        }

        private static int _cellDownRow = -1;   // 左键按下的行（数据列三态切换的守卫）
        private static int _cellDownCol = -1;   // 左键按下的列

        // 鼠标 x 落在可视列的哪一列（与核心 DrawTableCells 同一套排布）。
        private static int ColumnAtX(float x) {
            try {
                float left = _args.box.x - _args.scrollX;
                for (int k = 0; k < _args.vis.Count; k++) {
                    int ci = _args.vis[k];
                    if (ci == _args.pinCol) continue;
                    float cw = SRApi.Ctl.Sc(_args.colW[ci]);
                    if (x >= left && x <= left + cw) return ci;
                    left += cw;
                }
            } catch { /* 静默：布局未就绪时当作没命中 */ }
            return -1;
        }

        // 可见行 -> 预设（行号是 visible 列表的下标，与 RowCells 用的是同一份）。
        private static PresetInfo _currentVisibleRow(int row) {
            try {
                List<PresetInfo> vis = _visibleCache;
                if (vis == null || row < 0 || row >= vis.Count) return null;
                return vis[row];
            } catch { return null; }
        }

        // 数据格悬浮：只保留修改器预览（用户要求）。
        //   预设态：列出该 XML 里存的非默认修改器，值用游戏本地化文本；
        //   关闭态：一句说明（加载时写成默认值）。
        private static string DataPreviewTip(PresetInfo p) {
            try {
                if (p == null) return null;
                if (string.Equals(p.ModsMode ?? ModsInherit, ModsOff, StringComparison.Ordinal)) {
                    return SRApi.T("关闭：加载时把修改器写成默认值\n左键切回「预设」", "Off: modifiers are reset to defaults at load time\nleft-click to go back to Preset");
                }
                string summary = ModsSummaryFromXml(p, 12);
                if (string.IsNullOrEmpty(summary)) {
                    int n = ModsCountInXml(p);
                    return n <= 0
                        ? SRApi.T("预设：该 XML 没有修改器，加载时用游戏当前值\n左键切到「关闭」", "Preset: this XML stores no modifiers; the game's current values are used\nleft-click for Off")
                        : SRApi.T("预设：修改器全部为默认值\n左键切到「关闭」", "Preset: all stored modifiers are default\nleft-click for Off");
                }
                return SRApi.T("预设：", "Preset: ") + summary;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/数据格提示", __ex); return null; }
        }


    }
}
