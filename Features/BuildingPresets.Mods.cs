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
        private static string _modsKey;                              // 正在编辑的预设 Key（null = 未打开）
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
                if (string.Equals(m, ModsCustom, StringComparison.Ordinal)) return SRApi.T("自定义", "Custom");
                if (string.Equals(m, ModsOff, StringComparison.Ordinal)) return SRApi.T("关闭", "Off");
                return SRApi.T("预设", "Preset");
            } catch { return "-"; }
        }

        // 三态切换：预设 -> 自定义 -> 关闭 -> 预设（左键点「数据」列的格子）。
        private static void CycleDataMode(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                string m = p.ModsMode ?? ModsInherit;
                string next;
                if (string.Equals(m, ModsCustom, StringComparison.Ordinal)) next = ModsOff;
                else if (string.Equals(m, ModsOff, StringComparison.Ordinal)) next = ModsInherit;
                else next = ModsCustom;
                p.ModsMode = next;
                if (string.Equals(next, ModsCustom, StringComparison.Ordinal) && !IsCustomData(p)) {
                    SRApi.Notify(SRApi.T("这条预设还没有自定义数据：右键「数据」列（或点「数据」按钮）先在游戏界面里设置再读取",
                        "This preset has no custom data yet: right-click the Data column (or the Data button) to set it up in the game UI first"));
                }
                if (string.Equals(next, ModsInherit, StringComparison.Ordinal)) {
                    p.ApplyRules = p.ApplyPoints = p.ApplyBlocks = false;
                }
                SaveIndex();
                SRApi.Notify(SRApi.T("数据：", "Data: ") + ModsLabel(p) + " · " + (p.Name ?? ""));
                SRApi.LogInfo("[布局预设] 数据模式切换: " + (p.Name ?? "") + " -> " + ModsLabel(p));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/切换数据模式", __ex); }
        }

        // 这条预设是否**真的**带自定义数据：光有开关不算，必须有实打实存下来的内容。
        // （以前只看开关，规则读失败时也会显示"自定义"，导致显示的条目和预设实际内容对不上。）
        private static bool IsCustomData(PresetInfo p) {
            if (p == null) return false;
            bool customMods = string.Equals(p.ModsMode, ModsCustom, StringComparison.Ordinal)
                              && p.ModsVals != null && p.ModsVals.Count > 0;
            if (customMods) return true;
            if (string.Equals(p.ModsMode, ModsOff, StringComparison.Ordinal)) return true;   // 关闭 = 明确要去掉修改器
            if (!(p.ApplyRules || p.ApplyPoints || p.ApplyBlocks)) return false;
            string side = RulesetSidecarPath(p);
            try { return !string.IsNullOrEmpty(side) && File.Exists(side); } catch { return false; }
        }




        // ---------- 下拉框（数值项）：选项与文案都取自游戏 Modifiers 自带的取值表 / 名称函数 ----------










        // ---------- 规则 / 积分设置 / 模块概率（游戏的 GameRulePreset） ----------
        // 保存预设时把当前规则集写成 sidecar（<预设>.xml.ruleset.xml），加载时按三个开关选择性应用。
        // 用游戏自己的入口：GameRulePreset.GetRulesetFromCurrentRules / GetRulesetXmlString（读），
        // LoadRulesetFromXML + GameSettings.ApplyTemporaryRuleset（写；mods 由修改器策略单独处理，这里传 false）。
        // 侧车放在 <预设根>/rulesets/<模式>/<预设文件名> —— 单独子目录，绝不混进预设列表。
        private static string RulesetSidecarPath(PresetInfo p) {
            try {
                if (p == null || p.IsLocal || string.IsNullOrEmpty(p.FilePath)) return null;
                string main = RulesetSidecarPathForFile(p.FilePath);   // <预设>.xml.ruleset.xml（与预设同目录）
                if (!string.IsNullOrEmpty(main) && File.Exists(main)) return main;
                // 兼容上一版写到 rulesets/<模式>/ 的旧侧车
                string dir = Path.GetDirectoryName(p.FilePath) ?? "";
                string mode = Path.GetFileName(dir);
                string dirRoot = PresetsRoot();
                if (!string.IsNullOrEmpty(dirRoot)) {
                    string legacy = Path.Combine(Path.Combine(dirRoot, RulesetsDirName), mode);
                    legacy = Path.Combine(legacy, Path.GetFileName(p.FilePath));
                    if (File.Exists(legacy)) return legacy;
                }
                return main;
            } catch { return null; }
        }

        private const string RulesetsDirName = "rulesets";

        private static void SaveRulesetSidecar(string presetPath) {
            try {
                if (string.IsNullOrEmpty(presetPath)) return;
                GameRulePreset cur = GameRulePreset.GetRulesetFromCurrentRules();
                if (cur == null) return;
                string xml = cur.GetRulesetXmlString();
                if (string.IsNullOrEmpty(xml)) return;
                if (xml.Length > MaxImportChars) {
                    SRApi.LogWarn("[布局预设] 规则集过大，跳过写入 (len=" + xml.Length + ")");
                    return;
                }
                File.WriteAllText(RulesetSidecarPathForFile(presetPath), xml, new UTF8Encoding(false));
                SRApi.LogInfo("[布局预设] 已写入规则集 sidecar (" + xml.Length + " 字符)");
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/写规则集", __ex); }
        }

        // 加载前调用：按该预设的三个开关，把 sidecar 里的规则集应用到当前对局（房主侧）。
        private static void ApplyRulesetForPreset(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                if (!p.ApplyRules && !p.ApplyPoints && !p.ApplyBlocks) return;
                string path = RulesetSidecarPath(p);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                    SRApi.LogInfo("[布局预设] 预设没有规则集 sidecar，跳过规则应用: " + (p.Name ?? ""));
                    return;
                }
                XmlDocument doc = ParseXmlSafe(File.ReadAllText(path));
                if (doc == null) { SRApi.LogWarn("[布局预设] 规则集解析失败，跳过"); return; }
                GameRulePreset preset = ScriptableObject.CreateInstance<GameRulePreset>();
                if (preset == null) return;
                preset.LoadRulesetFromXML(doc);
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) { SRApi.LogWarn("[布局预设] 取不到 GameSettings，规则未应用"); return; }
                gs.ApplyTemporaryRuleset(preset, p.ApplyRules, p.ApplyPoints, p.ApplyBlocks, false);
                SRApi.LogInfo("[布局预设] 已应用规则集 规则=" + p.ApplyRules + " 积分=" + p.ApplyPoints + " 模块=" + p.ApplyBlocks);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/应用规则集", __ex); }
        }


        // ---------- 借用游戏自己的界面 ----------
        // 打开游戏规则书（TabletRulesScreen 那一套）：规则 / 积分 / 模块概率 / 修改器都在里面，
        // 玩家改完关掉，再回本页点「读取游戏当前」。必须先把本面板收起来，否则输入被冻结、游戏界面点不动。
        // 正在游戏规则书里编辑的预设 Key（null = 没在编辑）；_bookSeenOpen = 已确认打开过一次（用来识别"已关闭"）
        private static string _bookKey;
        private static bool _bookSeenOpen;
        private static float _bookPollAt;

        // 由预设文件路径推出侧车路径（保存时用，此时 PresetInfo 可能还没建好）。
        private static string RulesetSidecarPathForFile(string presetPath) {
            try {
                if (string.IsNullOrEmpty(presetPath)) return null;
                return presetPath + ".ruleset.xml";
            } catch { return null; }
        }

        // 打开游戏自己的规则书（InventoryBook）：
        //   1) 若传了预设，先把它的规则/积分/模块/修改器灌进游戏，打开后看到的就是这条预设的数据；
        //   2) GameRuleBook 可能还没生成 —— 先 SpawnRuleBook（私有，反射），初始化回调里 Show(true) + 定位页面；
        //   3) 玩家关掉规则书后由 TickRulebook 回收：读回数据 -> 写进预设 -> 自动勾选"自定义" -> 自动弹回本面板。
        private static void OpenGameRulebook(PresetInfo p) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) { SRApi.Notify(SRApi.T("不在大厅里，打不开规则书", "Not in a lobby; the rulebook cannot be opened")); return; }
                LevelSelectController lsc = lm.CurrentLevelSelectController;   // 关卡里可能为 null，后面有兜底
                // 房主才把预设内容灌进游戏；房客只是查看（游戏本身会拦下修改）
                if (p != null && !p.IsLocal && SRApi.CanDoHost()) PushPresetDataToGame(p);
                _bookKey = p != null && !p.IsLocal ? p.Key : null;
                _bookSeenOpen = false;
                _bookPollAt = 0f;
                SRApi.CloseMenu();            // 先收起本面板，否则输入被冻结、界面点不动
                // 诊断：把三条路线需要的东西是否在场记一次，关卡内打不开时看这行就知道缺什么。
                try {
                    Tablet __diagT = FindTablet();
                    GameRuleProp __diagP = FindRuleProp();
                    LevelSelectController __diagL = lsc;
                    int __screens = 0;
                    if (__diagT != null && __diagT.tabletScreens != null) __screens = __diagT.tabletScreens.Count;
                    string __scene = "?";
                    try { __scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { }
                    SRApi.LogInfo("[布局预设][数据界面] 场景=" + __scene + " Tablet=" + (__diagT != null ? ("有(" + __screens + "屏)") : "无")
                        + " 规则书道具=" + (__diagP != null ? "有" : "无") + " 关卡选择控制器=" + (__diagL != null ? "有" : "无"));
                } catch (Exception __dg) { SRApi.Guard.Log("布局预设/数据界面诊断", __dg); }
                // 路线 0（首选）：平板界面 Tablet —— 里面有"规则 / 积分 / 模块 / 修改器"的整块页面。
                Tablet tablet = FindTablet();
                LobbyPlayer local = FindLocalLobbyPlayer(lm);
                if (tablet != null) {
                    try {
                        PickCursor cur = local != null ? local.CursorInstance as PickCursor : null;
                        if (cur == null) cur = UnityEngine.Object.FindObjectOfType(typeof(PickCursor)) as PickCursor;
                        tablet.OnShowTablet();
                        // 先回到平板主界面，再切到"规则"那块屏幕（TabletRulesScreen）——它才是规则/积分/模块/修改器的上一级页面。
                        bool jumped = false;
                        try {
                            // resetToMain 是私有的：走 SRApi.Ref（会登记反射豁免）
                            MethodInfo __rtm = SRApi.Ref.Method(typeof(Tablet), "resetToMain", "布局预设/平板回到主界面");
                            if (__rtm != null) __rtm.Invoke(tablet, null);
                            TabletScreen rules = null;
                            System.Collections.IEnumerable all = tablet.tabletScreens as System.Collections.IEnumerable;
                            if (all != null) {
                                foreach (object o in all) {
                                    if (o is TabletRulesScreen) { rules = (TabletScreen)o; break; }
                                }
                            }
                            if (rules != null) { tablet.GotoScreen(rules); jumped = true; }
                        } catch (Exception __rs) { SRApi.Guard.Log("布局预设/平板主界面", __rs); }
                        if (!jumped) tablet.OpenLegacyRuleScreen(cur);
                        bool shown = tablet.CurrentlyShown;   // Tablet.CurrentlyShown 是 bool：界面是否已显示
                        if (shown) {
                            _bookSeenOpen = true;
                            SRApi.LogInfo("[布局预设] 已打开平板规则/修改器界面（Tablet.OpenLegacyRuleScreen）");
                            return;
                        }
                        SRApi.LogWarn("[布局预设] 平板没切到规则页（cursor=" + (cur != null ? "有" : "空") + "），继续尝试账本路线");
                    } catch (Exception __tb) { SRApi.Guard.Log("布局预设/平板规则界面", __tb); }
                } else {
                    SRApi.LogWarn("[布局预设] 场景里没有 Tablet，尝试账本路线");
                }
                // 路线 1（次选）：场景里已有规则书道具 GameRuleProp，直接走它的 Use。
                GameRuleProp prop = FindRuleProp();
                if (prop != null) {
                    LobbyPlayer lp = FindLocalLobbyPlayer(lm);
                    if (lp != null) {
                        prop.Use(lp, InputEvent.InputKey.NoKey);
                        _bookSeenOpen = true;
                        SRApi.LogInfo("[布局预设] 已通过规则书道具打开（GameRuleProp.Use）");
                        return;
                    }
                }
                // 路线 2：没有道具（或在关卡里）就生成规则书，初始化回调里用 InventoryBook API 打开。
                LevelSelectController __lsc = lsc;
                if (__lsc != null) {
                    if (__lsc.GameRuleBook == null) SpawnRuleBook(__lsc);
                    __lsc.ExecuteOnRuleBookInitialized(delegate { OpenRulebookByApi(__lsc); });
                    SRApi.LogInfo("[布局预设] 已请求生成/打开规则书（SpawnRuleBook + InventoryBook）");
                } else {
                    SRApi.Notify(SRApi.T("当前场景没有关卡选择控制器，打不开规则书", "No level-select controller in this scene; the rulebook cannot be opened"));
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/打开游戏规则书", __ex);
                SRApi.Notify(SRApi.T("打开游戏规则书失败", "Failed to open the game rulebook"));
            }
        }

        // 场景里的平板（规则/修改器界面的载体）。
        private static Tablet FindTablet() {
            try { return UnityEngine.Object.FindObjectOfType(typeof(Tablet)) as Tablet; }
            catch { return null; }
        }

        // 场景里的规则书道具（树屋里的那本）。关卡里通常没有 -> 返回 null。
        private static GameRuleProp FindRuleProp() {
            try {
                return UnityEngine.Object.FindObjectOfType(typeof(GameRuleProp)) as GameRuleProp;
            } catch { return null; }
        }

        private static LobbyPlayer FindLocalLobbyPlayer(LobbyManager lm) {
            try {
                foreach (LobbyPlayer x in lm.GetLobbyPlayers()) {
                    if (x != null && x.isLocalPlayer) return x;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/找本地玩家", __ex); }
            return null;
        }

        // 走 InventoryBook：AddPlayer（放进光标）+ Show + 翻到规则页；这条路径关卡内也能用。
        private static void OpenRulebookByApi(LevelSelectController lsc) {
            try {
                if (lsc == null || lsc.GameRuleBook == null) {
                    SRApi.LogWarn("[布局预设] 规则书对象为空，未能打开");
                    SRApi.Notify(SRApi.T("规则书没能生成（在树屋里再试一次）", "The rulebook could not be spawned (try again in the treehouse)"));
                    return;
                }
                LobbyManager lm = LobbyManager.instance;
                LobbyPlayer lp = lm != null ? FindLocalLobbyPlayer(lm) : null;
                lsc.GameRuleBook.Show(true);
                if (lp != null) {
                    // 先按"加入玩家"的完整形式登记（控制器拿不到就传 null，游戏会自己兜）；
                    // 失败了再退回只点亮光标，保证书至少能显示出来。
                    bool added = false;
                    try {
                        Character ch = lp.CharacterInstance;
                        lsc.GameRuleBook.AddPlayer(lp.localNumber, lp.networkNumber, null,
                            ch != null ? ch.CharacterSprite : default(Character.Animals));
                        added = true;
                    } catch (Exception __ap) { SRApi.Guard.Log("布局预设/规则书加入玩家", __ap); }
                    if (!added) {
                        try { lsc.GameRuleBook.ShowCursor(lp.localNumber); }
                        catch (Exception __sc) { SRApi.Guard.Log("布局预设/规则书点亮光标", __sc); }
                    }
                }
                lsc.GameRuleBook.GotoPage((int)InventoryPage.PageTypes.RulePage, true, true);
                _bookSeenOpen = true;
                SRApi.LogInfo("[布局预设] 已打开游戏规则书（InventoryBook）");
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/API 打开规则书", __ex); }
        }

        // 生成规则书对象（SpawnRuleBook 是私有方法，走反射；失败只影响这一次打开）。
        private static void SpawnRuleBook(LevelSelectController lsc) {
            try {
                MethodInfo m = SRApi.Ref.Method(typeof(LevelSelectController), "SpawnRuleBook", "布局预设/生成规则书");
                if (m != null) m.Invoke(lsc, null);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/生成规则书", __ex); }
        }

        // 把预设内容灌进游戏（打开规则书前调用）：规则/积分/模块按勾选应用，修改器一律按预设值写。
        private static void PushPresetDataToGame(PresetInfo p) {
            try {
                if (p == null) return;
                string path = RulesetSidecarPath(p);
                string xml = (!string.IsNullOrEmpty(path) && File.Exists(path)) ? File.ReadAllText(path) : null;
                // 修改器：把自定义值合进规则集 XML，再整份应用（loadModifiers=true）
                if (string.Equals(p.ModsMode, ModsCustom, StringComparison.Ordinal)) {
                    if (string.IsNullOrEmpty(xml)) xml = GameRulePreset.GetRulesetFromCurrentRules() != null
                        ? GameRulePreset.GetRulesetFromCurrentRules().GetRulesetXmlString() : null;
                    xml = ApplyModsPolicy(xml, p);
                }
                if (string.IsNullOrEmpty(xml)) {
                    SRApi.LogInfo("[布局预设] 该预设没有规则集，打开规则书只显示游戏当前内容");
                    return;
                }
                XmlDocument doc = ParseXmlSafe(xml);
                if (doc == null) return;
                GameRulePreset preset = ScriptableObject.CreateInstance<GameRulePreset>();
                if (preset == null) return;
                preset.LoadRulesetFromXML(doc);
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return;
                bool loadMods = string.Equals(p.ModsMode, ModsCustom, StringComparison.Ordinal);
                gs.ApplyTemporaryRuleset(preset, p.ApplyRules || p.ApplyPoints || p.ApplyBlocks ? p.ApplyRules : true,
                    p.ApplyPoints, p.ApplyBlocks, loadMods);
                SRApi.LogInfo("[布局预设] 已把预设内容灌入游戏（规则/积分/模块/修改器）以便在规则书里查看");
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/灌入预设内容", __ex); }
        }

        // 每 0.25 秒看一次：规则书从"打开"变"关闭"就回收数据。
        private static void TickRulebook() {
            // 顺手做重载锁自愈（每帧一次、只比较两个 float，成本可忽略）——不依赖用户再点一次加载。
            TickReloadLock();
            if (string.IsNullOrEmpty(_bookKey)) return;
            if (Time.unscaledTime < _bookPollAt) return;
            _bookPollAt = Time.unscaledTime + 0.25f;
            try {
                LobbyManager lm = LobbyManager.instance;
                LevelSelectController lsc = lm != null ? lm.CurrentLevelSelectController : null;
                if (lsc == null) return;
                InventoryBook book = lsc.GameRuleBook;
                bool open = book != null && book.gameObject != null && book.gameObject.activeInHierarchy && book.inInventory;
                if (open) { _bookSeenOpen = true; return; }
                if (!_bookSeenOpen) return;    // 还没真正打开过，继续等
                FinishRulebookEdit();
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/规则书轮询", __ex); }
        }

        // 规则书已关闭：把游戏当前内容读回这条预设，自动设为"自定义"，再自动弹回本面板。
        private static void FinishRulebookEdit() {
            try {
                string key = _bookKey;
                _bookKey = null;
                _bookSeenOpen = false;
                PresetInfo p = FindPreset(key);
                if (p == null) return;
                ReadRulesFromGame(p);      // 规则 / 积分 / 模块概率 -> sidecar + 三个开关
                ReadModsFromGame();        // 24 项修改器 -> 草稿
                p.ModsMode = ModsCustom;
                if (p.ModsVals == null) p.ModsVals = new Dictionary<string, string>();
                p.ModsVals.Clear();
                for (int i = 0; i < Mods.Length; i++) {
                    string v;
                    if (!_modsDraft.TryGetValue(Mods[i].Attr, out v) || string.IsNullOrEmpty(v)) v = ModDefault(Mods[i]);
                    p.ModsVals[Mods[i].Attr] = v;
                }
                SaveIndex();
                _modsKey = p.Key;          // 回来后直接停在该预设的编辑器上
                SRApi.OpenMenu();          // 自动弹回管理器
                SRApi.Notify(SRApi.T("规则书已关闭，已保存到预设并设为自定义：", "Rulebook closed; saved to the preset as custom: ") + (p.Name ?? ""));
                SRApi.LogInfo("[布局预设] 规则书关闭：已回收数据并把 " + (p.Name ?? "") + " 设为自定义");
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/规则书回收", __ex); }
        }

        // 读游戏当前的规则集 -> 写进这条预设的 sidecar，并把三个开关打开。
        private static void ReadRulesFromGame(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                GameRulePreset cur = GameRulePreset.GetRulesetFromCurrentRules();
                if (cur == null) { SRApi.Notify(SRApi.T("取不到当前规则集", "Current ruleset unavailable")); return; }
                string xml = cur.GetRulesetXmlString();
                string path = RulesetSidecarPath(p);
                if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(path)) return;
                File.WriteAllText(path, xml, new UTF8Encoding(false));
                p.ApplyRules = true; p.ApplyPoints = true; p.ApplyBlocks = true;
                SaveIndex();
                SRApi.LogInfo("[布局预设] 已读取游戏当前规则集 (" + xml.Length + " 字符) -> " + (p.Name ?? ""));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取游戏规则集", __ex); }
        }

        // 读游戏当前的 24 项修改器 -> 填进草稿（自动切到"自定义"，否则草稿不会被采用）。
        private static void ReadModsFromGame() {
            try {
                Modifiers m = Modifiers.GetInstance();
                if (m == null) { SRApi.Notify(SRApi.T("取不到修改器实例", "Modifier instance unavailable")); return; }
                int n = 0;
                int fail = 0;
                for (int i = 0; i < Mods.Length; i++) {
                    ModDef d = Mods[i];
                    string field = ModFieldName(d.Attr);
                    if (string.IsNullOrEmpty(field)) continue;
                    try {
                        FieldInfo fi = SRApi.Ref.Field(typeof(Modifiers), field, "布局预设/读取修改器");
                        if (fi == null) continue;
                        object v = fi.GetValue(m);
                        if (v == null) continue;
                        if (d.IsBool) _modsDraft[d.Attr] = ((bool)v) ? "True" : "False";
                        else _modsDraft[d.Attr] = Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                        n++;
                    } catch (Exception __rf) {
                        fail++;
                        SRApi.Guard.Log("布局预设/读取修改器字段 " + d.Attr, __rf);
                    }
                }
                SRApi.LogInfo("[布局预设] 已从游戏读取 " + n + "/" + Mods.Length + " 项修改器到自定义值（失败 " + fail + "）");
                if (n == 0) {
                    // 一项都没读到 = 这次"读取"实际什么都没拿到：必须明确失败，不能显示成成功（audit A-5）。
                    SRApi.Notify(SRApi.T("没能从游戏读到任何修改器（详见日志）", "Could not read any modifier from the game (see the log)"));
                    return;
                }
                SRApi.Notify(SRApi.T("已读取修改器项：", "Modifiers read: ") + n + "/" + Mods.Length
                    + (fail > 0 ? SRApi.T("（部分失败）", " (partial)") : ""));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取游戏修改器", __ex); }
        }

        // 快照属性名 -> Modifiers 里的字段名（部分字段是 camelCase），读不到返回 null。
        private static string ModFieldName(string attr) {
            switch (attr) {
                case "GravityMode": return "GravityMode";
                case "JumpSpeedMode": return "JumpSpeedMode";
                case "SprintSpeedMode": return "SprintSpeedMode";
                case "GameSpeedMode": return "GameSpeedMode";
                case "InvisibilityMode": return "invisibilityMode";
                case "PlatformSpeedMode": return "PlatformSpeedMode";
                case "RateOfFireMode": return "RateOfFireMode";
                case "MultiJumpMode": return "MultiJumpMode";
                case "ProjectileExplosionMode": return "ProjectileExplosionMode";
                case "CharacterSizeMode": return "CharacterSizeMode";
                case "PostDeathBehaviorMode": return "PostDeathBehaviorMode";
                case "CameraFlipMode": return "CameraFlipMode";
                case "DoomsdayMeteorsMode": return "DoomsdayMeteorsMode";
                case "DoomsdayLavaMode": return "DoomsdayLavaMode";
                case "ProjectileSpeedMode": return "ProjectileSpeedMode";
                case "JetpackMode": return "jetpackMode";
                case "WallJumpsDisabled": return "wallJumpsDisabled";
                case "WallSlidesDisabled": return "wallSlidesDisabled";
                case "DanceInvincibility": return "danceInvincibility";
                case "MirrorControls": return "mirrorControls";
                case "PlayerPlayerCollisions": return "playerPlayerCollisions";
                case "PreviewModsInTreehouse": return "modsPreview";
                case "ForceLobbyModifiers": return "forceLobbyModifiers";
                case "Frictionless": return "frictionless";
                default: return null;
            }
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
                    if (e.type == EventType.MouseDown && e.button == 1) { OpenGameRulebook(p); e.Use(); return; }
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

        // 多行预览：模式 + 各项非默认修改器 + 规则/积分/模块概率开关。
        private static string DataPreviewTip(PresetInfo p) {
            try {
                if (p == null) return null;
                string mode = p.ModsMode ?? ModsInherit;
                if (string.Equals(mode, ModsInherit, StringComparison.Ordinal))
                    return SRApi.T("预设：加载时原样使用预设自带的数据，不做任何改写\n左键点这一格可在 预设 / 自定义 / 关闭 之间切换",
                        "Preset: load-time data is left untouched\nleft-click this cell to cycle Preset / Custom / Off");
                if (string.Equals(mode, ModsOff, StringComparison.Ordinal))
                    return SRApi.T("关闭：加载时把修改器写成默认值，且不应用规则/积分/模块概率\n左键点这一格可切换",
                        "Off: modifiers are reset to defaults and rules/points/block odds are not applied\nleft-click this cell to cycle");
                StringBuilder sb = new StringBuilder();
                sb.Append(SRApi.T("自定义数据", "Custom data"));
                if (string.Equals(p.ModsMode, ModsCustom, StringComparison.Ordinal)) {
                    sb.Append("\n").Append(SRApi.T("修改器：", "Modifiers: "));
                    int shown = 0;
                    for (int i = 0; i < Mods.Length && shown < 12; i++) {
                        ModDef d = Mods[i];
                        string v;
                        if (p.ModsVals == null || !p.ModsVals.TryGetValue(d.Attr, out v)) continue;
                        if (string.IsNullOrEmpty(v) || v == ModDefault(d)) continue;   // 只列非默认项
                        if (shown > 0) sb.Append(" · ");
                        sb.Append(SRApi.T(d.Zh, d.En)).Append('=').Append(v);
                        shown++;
                    }
                    if (shown == 0) sb.Append(SRApi.T("（全部默认）", "(all default)"));
                } else if (string.Equals(p.ModsMode, ModsOff, StringComparison.Ordinal)) {
                    sb.Append("\n").Append(SRApi.T("修改器：全部写成默认值", "Modifiers: reset to defaults"));
                }
                if (p.ApplyRules) sb.Append("\n").Append(SRApi.T("游戏规则：随预设应用", "Rules: applied with the preset"));
                if (p.ApplyPoints) sb.Append("\n").Append(SRApi.T("积分设置：随预设应用", "Points: applied with the preset"));
                if (p.ApplyBlocks) sb.Append("\n").Append(SRApi.T("模块概率：随预设应用", "Block odds: applied with the preset"));
                return sb.ToString();
            } catch { return null; }
        }


    }
}
