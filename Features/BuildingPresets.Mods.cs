// 布局预设 · 修改器策略（BuildingPresets 的第二个分部文件）。
// 关卡快照里的 <mods .../> 就是游戏的关卡修改器：24 个属性（15 个模式索引 + 9 个开关）。
// 预设默认原样继承这份修改器；本页允许对每条预设单独选择：
//   继承 = 用预设自带的（默认，行为与以前一致）
//   关闭 = 全部还原为默认值（等于"没有修改器"）
//   自定义 = 由用户逐项设定，加载时覆盖预设自带的值
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private static readonly Dictionary<string, string> _modsDraft = new Dictionary<string, string>();   // 草稿：属性 -> 值

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
                string m = p.ModsMode ?? ModsInherit;
                if (m == ModsOff) return SRApi.T("关", "Off");
                if (m == ModsCustom) return SRApi.T("改", "Set");
                return SRApi.T("继承", "Inherit");
            } catch { return "-"; }
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
            const int Cols = 3;
            int perCol = (Mods.Length + Cols - 1) / Cols;
            for (int row = 0; row < perCol; row++) {
                GUILayout.BeginHorizontal();
                try {
                    for (int c = 0; c < Cols; c++) {
                        int idx = c * perCol + row;
                        if (idx >= Mods.Length) { GUILayout.FlexibleSpace(); continue; }
                        ModDef d = Mods[idx];
                        string label = SRApi.T(d.Zh, d.En);
                        string tip = SRApi.T(d.Zh + "\n预设快照 <mods> 里的 " + d.Attr + "\n"
                                              + (d.IsBool ? "开关：勾选 = 开，取消 = 关" : "模式索引：0 = 默认，数值含义由游戏决定"),
                                             d.En + "\npreset snapshot attribute " + d.Attr + "\n"
                                              + (d.IsBool ? "boolean switch: checked = on" : "mode index: 0 = default; meaning is defined by the game"));
                        GUIContent lc = new GUIContent(label, tip);
                        float lw = SRApi.Ctl.Label.CalcSize(new GUIContent(label)).x + SRApi.Ctl.Sc(6);
                        GUILayout.Label(lc, SRApi.Ctl.Label, GUILayout.Width(Mathf.Min(lw, W(120))), GUILayout.Height(W(24)));
                        string cur;
                        if (!_modsDraft.TryGetValue(d.Attr, out cur) || cur == null) cur = ModDefault(d);
                        if (d.IsBool) {
                            bool on = string.Equals(cur, "True", StringComparison.OrdinalIgnoreCase);
                            Rect cr = GUILayoutUtility.GetRect(W(26), W(24), GUILayout.Width(W(26)), GUILayout.Height(W(24)));
                            if (SRApi.Ctl.Checkbox(cr, on, tip)) _modsDraft[d.Attr] = on ? "False" : "True";
                        } else {
                            string typed = SRApi.ClearableTextField(cur, 4, SRApi.Ctl.SearchBox, GUILayout.Width(W(46)), GUILayout.Height(W(22)));
                            if (typed != null && typed != cur) {
                                int v;
                                if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 0) {
                                    _modsDraft[d.Attr] = v.ToString(CultureInfo.InvariantCulture);
                                } else if (typed.Length == 0) {
                                    _modsDraft[d.Attr] = "0";
                                }
                            }
                        }
                        if (c < Cols - 1) GUILayout.Space(W(10));
                    }
                    GUILayout.FlexibleSpace();
                } finally {
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(W(4));
            LabelWrapText(SRApi.T("数值项的 0 表示默认；自定义只影响加载，不改写预设文件本身。",
                "0 means default for numeric entries; custom values only affect loading, the preset file stays untouched."));
        }

        // 选中态标记（契约层不暴露 SelItem 样式，用文字前缀即可）。
        private static string ModPick(string mode) { return _modsMode == mode ? "● " : "○ "; }

        // 长提示文字：按当前宽度自动换行，行高按两行给足。
        private static void LabelWrapText(string text) {
            GUILayout.Label(new GUIContent(text), SRApi.Ctl.LabelWrap, GUILayout.Height(W(34)));
        }
    }
}
