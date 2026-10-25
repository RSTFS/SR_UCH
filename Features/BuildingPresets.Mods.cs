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
        private static string _modsMode = ModsInherit;               // 草稿：策略
        private static readonly Dictionary<string, string> _modsDraft = new Dictionary<string, string>();
        // 规则 / 积分设置 / 模块概率：加载该预设时是否一并应用（草稿）。
        private static bool _modsApplyRules, _modsApplyPoints, _modsApplyBlocks;   // 草稿：属性 -> 值

        private static string ModDefault(ModDef d) { return d.IsBool ? "False" : "0"; }

        // 找属性定义（未知属性返回 null，加载时原样保留）。
        private static ModDef ModByAttr(string attr) {
            for (int i = 0; i < Mods.Length; i++) if (Mods[i].Attr == attr) return Mods[i];
            return null;
        }

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

        // 读一份 XML 里现有的 <mods> 值（打开编辑器时用它做自定义模式的起点）。
        private static void FillDraftFromXml(string xml) {
            for (int i = 0; i < Mods.Length; i++) _modsDraft[Mods[i].Attr] = ModDefault(Mods[i]);
            try {
                if (string.IsNullOrEmpty(xml)) return;
                XmlDocument doc = ParseXmlSafe(xml);
                if (doc == null || doc.DocumentElement == null) return;
                XmlElement mods = doc.DocumentElement.SelectSingleNode("mods") as XmlElement;
                if (mods == null) return;
                for (int i = 0; i < Mods.Length; i++) {
                    ModDef d = Mods[i];
                    string v = mods.GetAttribute(d.Attr);
                    if (!string.IsNullOrEmpty(v)) _modsDraft[d.Attr] = v;
                }
            } catch { /* 静默：读不到就按全默认显示，用户自己填 */ }
        }

        // ---------- 编辑器 ----------
        private static void OpenModsEditor(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                ClearPending();
                _modsKey = p.Key;
                _modsMode = string.IsNullOrEmpty(p.ModsMode) ? ModsInherit : p.ModsMode;
                _modsApplyRules = p.ApplyRules;
                _modsApplyPoints = p.ApplyPoints;
                _modsApplyBlocks = p.ApplyBlocks;
                FillDraftFromXml(ReadPresetXmlRaw(p));
                SRApi.LogInfo("[布局预设] 打开修改器编辑器: " + p.Name + "（当前策略=" + ModsModeText(p.ModsMode) + "）");
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/打开修改器编辑", __ex); }
        }

        private static void CloseModsEditor() { _modsKey = null; _modsDraft.Clear(); }

        private static string ModsModeText(string mode) {
            if (mode == ModsOff) return SRApi.T("关闭", "Off");
            if (mode == ModsCustom) return ModPick(ModsCustom) + SRApi.T("自定义", "Custom");
            return SRApi.T("继承", "Inherit");
        }

        // 保存：写回 PresetInfo 并落盘索引；继承状态下清空自定义值（避免残留旧值）。
        private static void SaveModsEditor() {
            try {
                PresetInfo p = FindPreset(_modsKey);
                if (p == null) { CloseModsEditor(); return; }
                p.ModsMode = _modsMode;
                p.ApplyRules = _modsApplyRules;
                p.ApplyPoints = _modsApplyPoints;
                p.ApplyBlocks = _modsApplyBlocks;
                if (_modsMode == ModsCustom) {
                    if (p.ModsVals == null) p.ModsVals = new Dictionary<string, string>();
                    p.ModsVals.Clear();
                    for (int i = 0; i < Mods.Length; i++) {
                        ModDef d = Mods[i];
                        string v;
                        if (!_modsDraft.TryGetValue(d.Attr, out v) || string.IsNullOrEmpty(v)) v = ModDefault(d);
                        p.ModsVals[d.Attr] = v;
                    }
                } else if (p.ModsVals != null) {
                    p.ModsVals.Clear();
                }
                SaveIndex();
                SRApi.LogInfo("[布局预设] 修改器策略已保存: " + p.Name + " -> " + ModsModeText(p.ModsMode));
                SRApi.Notify(SRApi.T("修改器策略：", "Modifiers: ") + ModsModeText(p.ModsMode) + " · " + p.Name);
                CloseModsEditor();
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/保存修改器策略", __ex); }
        }

        // 表内显示用短标签：继承显示 "-"（不改动），关闭显示「关」，自定义显示「改」。
        private static string ModsLabel(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return "-";
                return IsCustomData(p) ? SRApi.T("自定义", "Custom") : SRApi.T("默认", "Default");
            } catch { return "-"; }
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

        // 编辑器页面（占用表格区域）：策略三选一 + 自定义时的 24 项格子。
        // 调用方在 Render() 里判断 _modsKey 非空时改调这里，返回 true 表示本帧已接管绘制。
        private static bool RenderModsEditorIfOpen() {
            if (string.IsNullOrEmpty(_modsKey)) return false;
            PresetInfo p = FindPreset(_modsKey);
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 修改器 -", "- Modifiers -")
                    + "  " + (p != null ? p.Name : ""));
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("加载时", "On load"));
                    if (GUILayout.Button(new GUIContent(ModPick(ModsInherit) + SRApi.T("继承预设", "Inherit"),
                            SRApi.T("照用预设自带的那份修改器（默认）", "use the modifiers stored in the preset (default)")),
                        SRApi.Ctl.Btn,
                        GUILayout.Width(W(84)), GUILayout.Height(W(24)))) _modsMode = ModsInherit;
                    if (GUILayout.Button(new GUIContent(ModPick(ModsOff) + SRApi.T("关闭", "Off"),
                            SRApi.T("加载时把修改器全部还原为默认值（等于没有修改器）", "reset every modifier to its default when loading")),
                        SRApi.Ctl.Btn,
                        GUILayout.Width(W(84)), GUILayout.Height(W(24)))) _modsMode = ModsOff;
                    if (GUILayout.Button(new GUIContent(ModPick(ModsCustom) + SRApi.T("自定义", "Custom"),
                            SRApi.T("加载时用下面设定的值覆盖预设自带的修改器", "override the preset's modifiers with the values below")),
                        SRApi.Ctl.Btn,
                        GUILayout.Width(W(84)), GUILayout.Height(W(24)))) {
                        _modsMode = ModsCustom;
                        if (_modsDraft.Count == 0) FillDraftFromXml(ReadPresetXmlRaw(p));
                    }
                    GUILayout.Space(W(8));
                    if (GUILayout.Button(new GUIContent(SRApi.T("全部默认", "All defaults"),
                            SRApi.T("把下面 24 项全部填为默认值", "fill all 24 entries with their defaults")),
                        SRApi.Ctl.Btn, GUILayout.Width(W(96)), GUILayout.Height(W(24)))) {
                        for (int i = 0; i < Mods.Length; i++) _modsDraft[Mods[i].Attr] = ModDefault(Mods[i]);
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(SRApi.T("保存", "Save"), SRApi.Ctl.Btn, GUILayout.Width(W(84)), GUILayout.Height(W(24)))) {
                        SaveModsEditor();
                        return true;
                    }
                    if (GUILayout.Button(SRApi.T("返回", "Back"), SRApi.Ctl.Btn, GUILayout.Width(W(84)), GUILayout.Height(W(24)))) {
                        CloseModsEditor();
                        return true;
                    }
                } finally {
                    GUILayout.EndHorizontal();
                }
                // 规则 / 积分设置 / 模块概率：与预设一起应用（保存时写入 sidecar，加载时按勾选应用）。
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("随预设应用", "Also apply"));
                    RenderApplyToggle(ref _modsApplyRules, SRApi.T("游戏规则", "Rules"),
                        SRApi.T("加载该预设时，一并应用保存时的游戏规则（分数上限/回合数/时间限制等）",
                            "also apply the saved game rules (score limit, rounds, time limits...) when this preset loads"));
                    RenderApplyToggle(ref _modsApplyPoints, SRApi.T("积分设置", "Points"),
                        SRApi.T("加载该预设时，一并应用保存时的积分设置（各类得分与是否计入）",
                            "also apply the saved point settings (points per event and which ones count)"));
                    RenderApplyToggle(ref _modsApplyBlocks, SRApi.T("模块概率", "Block odds"),
                        SRApi.T("加载该预设时，一并应用保存时的模块出现概率",
                            "also apply the saved block spawn probabilities"));
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                // 借用游戏自己的界面：打开规则书（规则 / 积分 / 模块 / 修改器都在里面），
                // 玩家改完后回来点「读取游戏当前」，把结果写进这条预设。
                GUILayout.BeginHorizontal();
                try {
                    SRApi.Ctl.LabelAuto(SRApi.T("借用游戏界面", "Game UI"));
                    if (GUILayout.Button(new GUIContent(SRApi.T("打开游戏界面", "Open game UI"),
                            SRApi.T("先收起本面板，再打开游戏自己的规则书（规则 / 积分 / 模块概率 / 修改器都在里面）\n需在树屋或关卡里、由房主操作",
                                "closes this panel and opens the game's own rulebook (rules / points / block odds / modifiers)\nhost only, in the treehouse or a level")),
                        SRApi.Ctl.Btn, GUILayout.Width(W(110)), GUILayout.Height(W(24)))) OpenGameRulebook(p);
                    if (GUILayout.Button(new GUIContent(SRApi.T("读取游戏当前", "Read current"),
                            SRApi.T("把游戏当前的规则 / 积分 / 模块概率 / 修改器读进来，写到这条预设（三个开关自动打勾）",
                                "read the game's current rules / points / block odds / modifiers into this preset (the three toggles are turned on)")),
                        SRApi.Ctl.Btn, GUILayout.Width(W(110)), GUILayout.Height(W(24)))) ReadAllFromGame(p);
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
                if (_modsMode != ModsCustom) {
                    GUILayout.Space(W(4));
                    LabelWrapText(_modsMode == ModsOff
                        ? SRApi.T("加载这条预设时，24 项修改器都会写成默认值（0 / 否）。", "when this preset loads, all 24 modifiers are written as their defaults (0 / false).")
                        : SRApi.T("加载这条预设时，修改器保持预设自带的值，不做任何改写。", "when this preset loads, its own modifiers are kept untouched."));
                } else {
                    GUILayout.Space(W(2));
                    RenderModsGrid();
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/修改器编辑器", __ex);
            }
            return true;
        }

        // 24 项三列网格：模式索引用输入框（0 = 默认），开关用核心复选框。
        private static void RenderModsGrid() {
            // 两列 × 12 行：标签列固定宽、控件列固定宽 -> 同列逐行对齐。
            // 外面套固定高度的滚动区：列表比视口高时可用滚轮上下滚动（之前没有滚动区）。
            const int Cols = 2;
            int perCol = (Mods.Length + Cols - 1) / Cols;
            float labelW = W(104);
            float ctrlW = W(150);
            float rowH2 = W(26);
            // 高度自适应：这里已是页面内当前 y，可用高度 = 页面视口 - 已用高度（与列表同一套算法）。
            float __usedY = 0f;
            try { __usedY = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none).y; } catch { __usedY = 0f; }
            float __avail = Mathf.Max(W(140), SRApi.PageViewportHeight - __usedY - W(16));
            float gridH = Mathf.Min(perCol * rowH2 + W(8), __avail);
            Rect __gridRect = GUILayoutUtility.GetRect(0f, gridH, GUILayout.ExpandWidth(true));
            // 滚轮：直接改滚动值（嵌套滚动区在本面板里收不到滚轮事件），并吃掉事件避免页面跟着动。
            try {
                Event __we = Event.current;
                if (__we != null && __we.type == EventType.ScrollWheel && __gridRect.Contains(__we.mousePosition)) {
                    float dy = Mathf.Abs(__we.delta.y) > 0.001f ? __we.delta.y : __we.delta.x;
                    float contentH = perCol * rowH2 + W(8);
                    float maxY = Mathf.Max(0f, contentH - gridH);
                    if (maxY > 0f) {
                        _modsScroll.y = Mathf.Clamp(_modsScroll.y + dy * rowH2, 0f, maxY);
                        __we.Use();
                        SRApi.MarkWheelUsed();
                    }
                }
            } catch (Exception __whe) { SRApi.Guard.Log("布局预设/修改器滚轮", __whe); }
            GUILayout.BeginArea(__gridRect);
            _modsScroll = GUILayout.BeginScrollView(_modsScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(gridH));
            try {
                for (int row = 0; row < perCol; row++) {
                    GUILayout.BeginHorizontal();
                    try {
                        for (int c = 0; c < Cols; c++) {
                            int idx = c * perCol + row;
                            if (idx >= Mods.Length) { GUILayout.Space(labelW + ctrlW + W(16)); continue; }
                            ModDef d = Mods[idx];
                            string label = SRApi.T(d.Zh, d.En);
                            string tip = SRApi.T(d.Zh + "\n预设快照 <mods> 里的 " + d.Attr + "\n"
                                                  + (d.IsBool ? "开关：勾选 = 开，取消 = 关" : "模式：选项来自游戏内的取值表"),
                                                 d.En + "\npreset snapshot attribute " + d.Attr + "\n"
                                                  + (d.IsBool ? "boolean switch: checked = on" : "mode: options come from the game's own value table"));
                            GUILayout.Label(new GUIContent(label, tip), SRApi.Ctl.Label,
                                GUILayout.Width(labelW), GUILayout.Height(rowH2));
                            string cur;
                            if (!_modsDraft.TryGetValue(d.Attr, out cur) || cur == null) cur = ModDefault(d);
                            if (d.IsBool) {
                                bool on = string.Equals(cur, "True", StringComparison.OrdinalIgnoreCase);
                                Rect cr = GUILayoutUtility.GetRect(W(26), rowH2, GUILayout.Width(W(26)), GUILayout.Height(rowH2));
                                if (SRApi.Ctl.Checkbox(cr, on, tip)) _modsDraft[d.Attr] = on ? "False" : "True";
                                GUILayout.Space(ctrlW - W(26));
                            } else {
                                RenderModCombo(d, cur, ctrlW);
                            }
                            if (c < Cols - 1) GUILayout.Space(W(14));
                        }
                        GUILayout.FlexibleSpace();
                    } finally {
                        GUILayout.EndHorizontal();
                    }
                }
            } finally {
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }


        // ---------- 下拉框（数值项）：选项与文案都取自游戏 Modifiers 自带的取值表 / 名称函数 ----------
        private static readonly Dictionary<string, string[]> _modOptCache = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, bool> _modComboOpen = new Dictionary<string, bool>();
        private static Vector2 _modsScroll;   // 修改器网格的滚动位置

        private static void RenderModCombo(ModDef d, string curRaw, float ctrlW) {
            try {
                string[] opts = ModOptions(d);
                int cur = 0;
                int raw;
                if (int.TryParse(curRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw) && raw >= 0) cur = raw;
                if (cur >= opts.Length) cur = opts.Length - 1;
                bool open;
                if (!_modComboOpen.TryGetValue(d.Attr, out open)) open = false;
                int pick = SRApi.Ctl.ComboBox(null, opts[cur], opts, opts, ref open, ctrlW);
                _modComboOpen[d.Attr] = open;
                if (pick >= 0 && pick != cur) _modsDraft[d.Attr] = pick.ToString(CultureInfo.InvariantCulture);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/修改器下拉", __ex); }
        }

        // 选项文本："下标 · 游戏名称"；游戏给不出名称时只显示下标。
        private static string[] ModOptions(ModDef d) {
            string[] cached;
            if (_modOptCache.TryGetValue(d.Attr, out cached)) return cached;
            int n = ModOptionCount(d);
            if (n < 1) n = 1;
            string[] opts = new string[n];
            for (int i = 0; i < n; i++) {
                string label = null;
                if (!d.IsBool) { try { label = ModOptionLabel(d, i); } catch { label = null; } }
                else label = i == 0 ? SRApi.T("关", "Off") : SRApi.T("开", "On");
                opts[i] = string.IsNullOrEmpty(label)
                    ? i.ToString(CultureInfo.InvariantCulture)
                    : i.ToString(CultureInfo.InvariantCulture) + " · " + label;
            }
            _modOptCache[d.Attr] = opts;
            return opts;
        }

        // 取值个数：开关固定 2；数值项取游戏内对应取值表的长度（拿不到按 4 项占位）。
        private static int ModOptionCount(ModDef d) {
            if (d.IsBool) return 2;
            try {
                Modifiers m = Modifiers.GetInstance();
                if (m == null) return 4;
                switch (d.Attr) {
                    case "GravityMode": return m.GravityValues != null ? m.GravityValues.Length : 4;
                    case "JumpSpeedMode": return m.JumpSpeedValues != null ? m.JumpSpeedValues.Length : 4;
                    case "SprintSpeedMode": return m.SprintModifiers != null ? m.SprintModifiers.Length : 4;
                    case "GameSpeedMode": return m.GameSpeedValues != null ? m.GameSpeedValues.Length : 4;
                    case "PlatformSpeedMode": return m.PlatformMoveSpeedValues != null ? m.PlatformMoveSpeedValues.Length : 4;
                    case "RateOfFireMode": return m.RateOfFireValues != null ? m.RateOfFireValues.Length : 4;
                    case "MultiJumpMode": return m.MultiJumpValues != null ? m.MultiJumpValues.Length : 4;
                    case "ProjectileExplosionMode": return m.ProjectileExplosionScales != null ? m.ProjectileExplosionScales.Length : 4;
                    case "CharacterSizeMode": return m.CharacterScales != null ? m.CharacterScales.Length : 4;
                    case "ProjectileSpeedMode": return m.ProjectileSpeedValues != null ? m.ProjectileSpeedValues.Length : 4;
                    case "DoomsdayMeteorsMode": return m.DoomsdayModifierTimes != null ? m.DoomsdayModifierTimes.Length : 4;
                    case "DoomsdayLavaMode": return m.DoomsdayModifierTimes != null ? m.DoomsdayModifierTimes.Length : 4;
                    case "PostDeathBehaviorMode": return 4;   // None / Agony / Ghost / Zombie
                    case "CameraFlipMode": return 4;          // None / FlipX / FlipY / FlipXY
                    case "InvisibilityMode": return 4;        // Off / WhenMoving / WhenStationary / Always
                    default: return 4;
                }
            } catch { return 4; }
        }

        // 名称：优先用游戏自带的 Get*ValueString（已本地化），没有对应函数的项返回 null 只显示下标。
        // 末日两项的名称必须带"延迟秒数"（游戏函数签名是 (模式, 延迟)），延迟取游戏内 DoomsdayModifierTimes。
        private static int DoomsdayDelay(int i, int fallback) {
            try {
                Modifiers m = Modifiers.GetInstance();
                if (m == null || m.DoomsdayModifierTimes == null || m.DoomsdayModifierTimes.Length == 0) return fallback;
                return m.DoomsdayModifierTimes[Mathf.Clamp(i, 0, m.DoomsdayModifierTimes.Length - 1)];
            } catch { return fallback; }
        }

        // 没有名称函数的速度类：显示游戏内系数（×1.25），总比只有下标强。
        private static string SpeedLabel(float v) {
            try { return "×" + v.ToString("0.##", CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static float ArrayAt(float[] a, int i) {
            if (a == null || a.Length == 0) return 1f;
            return a[Mathf.Clamp(i, 0, a.Length - 1)];
        }

        private static string ModOptionLabel(ModDef d, int i) {
            try {
                Modifiers m = Modifiers.GetInstance();
                if (m != null) {
                    switch (d.Attr) {
                        case "GameSpeedMode": return SpeedLabel(ArrayAt(m.GameSpeedValues, i));
                        case "PlatformSpeedMode": return SpeedLabel(ArrayAt(m.PlatformMoveSpeedValues, i));
                        case "RateOfFireMode": return SpeedLabel(ArrayAt(m.RateOfFireValues, i));
                        case "ProjectileSpeedMode": return SpeedLabel(ArrayAt(m.ProjectileSpeedValues, i));
                        case "DoomsdayMeteorsMode": return Modifiers.GetDoomsdayMeteorsValueString(i, DoomsdayDelay(i, 0));
                        case "DoomsdayLavaMode": return Modifiers.GetDoomsdayLavaValueString(i, DoomsdayDelay(i, 0));
                    }
                }
            } catch { /* 静默：拿不到就退回固定函数表 */ }
            switch (d.Attr) {
                case "GravityMode": return Modifiers.GetGravityValueString(i);
                case "JumpSpeedMode": return Modifiers.GetJumpSpeedValueString(i);
                case "SprintSpeedMode": return Modifiers.GetSprintSpeedValueString(i);
                case "MultiJumpMode": return Modifiers.GetMultiJumpValueString(i);
                case "ProjectileExplosionMode": return Modifiers.GetProjectileExplosionValueString(i);
                case "CharacterSizeMode": return Modifiers.GetCharacterSizeValueString(i);
                case "PostDeathBehaviorMode": return Modifiers.GetPostDeathBehaviorValueString(i);
                case "CameraFlipMode": return Modifiers.GetCameraFlipValueString(i);
                case "InvisibilityMode": return Modifiers.GetInvisibilityModeValueString(i);
                default: return null;
            }
        }


        // 一个"随预设应用"勾选项：标签 + 核心复选框。
        private static void RenderApplyToggle(ref bool value, string label, string tip) {
            GUILayout.Space(W(8));
            GUILayout.Label(new GUIContent(label, tip), SRApi.Ctl.Label, GUILayout.Height(W(24)));
            Rect cr = GUILayoutUtility.GetRect(W(26), W(24), GUILayout.Width(W(26)), GUILayout.Height(W(24)));
            if (SRApi.Ctl.Checkbox(cr, value, tip)) value = !value;
        }

        // ---------- 规则 / 积分设置 / 模块概率（游戏的 GameRulePreset） ----------
        // 保存预设时把当前规则集写成 sidecar（<预设>.xml.ruleset.xml），加载时按三个开关选择性应用。
        // 用游戏自己的入口：GameRulePreset.GetRulesetFromCurrentRules / GetRulesetXmlString（读），
        // LoadRulesetFromXML + GameSettings.ApplyTemporaryRuleset（写；mods 由修改器策略单独处理，这里传 false）。
        private static string RulesetSidecarPath(PresetInfo p) {
            try {
                if (p == null || p.IsLocal || string.IsNullOrEmpty(p.FilePath)) return null;
                return p.FilePath + ".ruleset.xml";
            } catch { return null; }
        }

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
                File.WriteAllText(presetPath + ".ruleset.xml", xml, new UTF8Encoding(false));
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

        // 打开游戏自己的规则书（InventoryBook）：
        //   1) 若传了预设，先把它的规则/积分/模块/修改器灌进游戏，打开后看到的就是这条预设的数据；
        //   2) GameRuleBook 可能还没生成 —— 先 SpawnRuleBook（私有，反射），初始化回调里 Show(true) + 定位页面；
        //   3) 玩家关掉规则书后由 TickRulebook 回收：读回数据 -> 写进预设 -> 自动勾选"自定义" -> 自动弹回本面板。
        private static void OpenGameRulebook(PresetInfo p) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) { SRApi.Notify(SRApi.T("不在大厅里，打不开规则书", "Not in a lobby; the rulebook cannot be opened")); return; }
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                // 房主才把预设内容灌进游戏；房客只是查看（游戏本身会拦下修改）
                if (p != null && !p.IsLocal && SRApi.CanDoHost()) PushPresetDataToGame(p);
                _bookKey = p != null && !p.IsLocal ? p.Key : null;
                _bookSeenOpen = false;
                _bookPollAt = 0f;
                SRApi.CloseMenu();            // 先收起本面板，否则输入被冻结、规则书点不动
                // 路线 1（最贴近游戏本身）：场景里已有规则书道具 GameRuleProp，直接走它的 Use。
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
                _modsApplyRules = true; _modsApplyPoints = true; _modsApplyBlocks = true;
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
                    } catch { /* 静默：单个字段读不到就跳过，其余照读 */ }
                }
                _modsMode = ModsCustom;
                SRApi.LogInfo("[布局预设] 已从游戏读取 " + n + "/" + Mods.Length + " 项修改器到自定义值");
                SRApi.Notify(SRApi.T("已读取修改器项：", "Modifiers read: ") + n + "/" + Mods.Length);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取游戏修改器", __ex); }
        }

        // 一次把规则/积分/模块概率 + 修改器都读进来。
        private static void ReadAllFromGame(PresetInfo p) {
            try {
                ReadRulesFromGame(p);
                ReadModsFromGame();
                SRApi.Notify(SRApi.T("已读取游戏当前设置到这条预设", "Current game settings read into this preset"));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取游戏当前", __ex); }
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


        // ---------- 「数据」列的悬浮预览：把这条预设的自定义内容逐行列出来 ----------
        private static void UpdateDataCellTip() {
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
                // 数据列在可视列里的横向区间（与核心 DrawTableCells 同一套排布：从 box.x - scrollX 起逐列累加）。
                float x = _args.box.x - _args.scrollX;
                float w = 0f;
                bool found = false;
                for (int k = 0; k < _args.vis.Count; k++) {
                    int ci = _args.vis[k];
                    if (ci == _args.pinCol) continue;
                    float cw = SRApi.Ctl.Sc(_args.colW[ci]);
                    if (ci == ColMods) { w = cw; found = true; break; }
                    x += cw;
                }
                if (!found || w <= 0f) return;
                if (mp.x < x || mp.x > x + w) return;
                string tip = DataPreviewTip(_currentVisibleRow(row));
                if (!string.IsNullOrEmpty(tip)) GUI.tooltip = tip;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/数据列悬浮", __ex); }
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
                if (!IsCustomData(p)) return SRApi.T("默认：加载时用预设自带的修改器，不动规则/积分/模块概率\n点操作列「改器」可改成自定义",
                    "Default: uses the preset's own modifiers and leaves rules/points/block odds alone\nuse the Mods button in the actions column to customise");
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

        // 选中态标记（契约层不暴露 SelItem 样式，用文字前缀即可）。
        private static string ModPick(string mode) { return _modsMode == mode ? "● " : "○ "; }

        // 长提示文字：按当前宽度自动换行，行高按两行给足。
        private static void LabelWrapText(string text) {
            GUILayout.Label(new GUIContent(text), SRApi.Ctl.LabelWrap, GUILayout.Height(W(34)));
        }
    }
}
