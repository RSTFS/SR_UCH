// 布局预设 · 修改器策略：关卡快照 <mods .../> 的 24 个属性（15 模式索引 + 9 开关）如何应用。
// 每条预设独立选择：继承（默认原样）/ 关闭（全部默认值）/ 当前（用游戏现值）/ 自定义（逐项覆盖）。
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

        // 一条修改器属性：Attr 是快照 XML 里 <mods> 的属性名。
        private sealed class ModDef {
            public readonly string Attr;
            public readonly string Zh;
            public readonly string En;
            public readonly bool IsBool;   // true = 开关，false = 模式索引（0 = 默认）
            public ModDef(string attr, string zh, string en, bool isBool) {
                Attr = attr; Zh = zh; En = en; IsBool = isBool;
            }
        }

        // 顺序与游戏写入一致；新增属性只追加在末尾（索引不改写）。
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

        // 策略取值："" = 预设、current = 游戏当前值、off = 全部默认；custom 仅旧索引兼容。
        private const string ModsInherit = "";   // 预设
        private const string ModsOff = "off";
        private const string ModsCustom = "custom";
        private const string ModsCurrent = "current";

        // 加载该预设时是否一并应用规则 / 积分设置 / 模块概率（草稿）。

        private static string ModDefault(ModDef d) { return d.IsBool ? "False" : "0"; }


        // 分区：索引持久化（modsMode / modsVals 两个纯文本字段）

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

        // 分区：应用到快照 XML（只在加载前调用，按策略改写 <mods>）
        // 只在加载前调用（列表统计/导出不受影响）：按策略改写 <mods> 的 24 个属性。
        // 任何解析异常都原样返回输入，绝不让预设加载失败。
        private static string ApplyModsPolicy(string xml, PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return xml;         // 本地关卡只读
                string mode = p.ModsMode ?? ModsInherit;
                if (mode == ModsCurrent) {
                    XmlDocument __cd = ParseXmlSafe(xml);
                    if (__cd == null || __cd.DocumentElement == null) return xml;
                    XmlElement __cm = __cd.DocumentElement.SelectSingleNode("mods") as XmlElement;
                    if (__cm != null) __cd.DocumentElement.RemoveChild(__cm);
                    SRApi.LogInfo("[布局预设] 修改器策略=当前，已移除 <mods>（保留游戏当前修改器）");
                    return __cd.OuterXml;
                }
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


        // 表内显示标签：继承显示“预设”，当前/关闭显示对应文字。
        private static string ModsLabel(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return "-";
                string m = p.ModsMode ?? ModsInherit;
                if (string.Equals(m, ModsCurrent, StringComparison.Ordinal)) return SRApi.T("当前", "Current");
                if (string.Equals(m, ModsOff, StringComparison.Ordinal)) return SRApi.T("关闭", "Off");
                return SRApi.T("预设", "Preset");
            } catch { return "-"; }
        }

        // 左键切换：预设 → 当前 → 关闭 → 预设。
        private static void CycleDataMode(PresetInfo p) {
            try {
                if (p == null || p.IsLocal) return;
                string m = p.ModsMode ?? ModsInherit;
                string next = string.Equals(m, ModsInherit, StringComparison.Ordinal) || string.IsNullOrEmpty(m)
                    ? ModsCurrent
                    : (string.Equals(m, ModsCurrent, StringComparison.Ordinal) ? ModsOff : ModsInherit);
                p.ModsMode = next;
                if (string.Equals(next, ModsInherit, StringComparison.Ordinal)) p.ModsVals = null;
                SaveIndex();
                SRApi.Notify(SRApi.T("数据：", "Data: ") + ModsLabel(p) + " · " + (p.Name ?? ""));
                SRApi.LogInfo("[布局预设] 数据模式切换: " + (p.Name ?? "") + " -> " + ModsLabel(p));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/切换数据模式", __ex); }
        }

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
                    return raw;   // 无对应文案（如平台速度）时保留数值
                }
                bool bv = string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase) || raw == "1";
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

        // 从 <mods> 取非默认项，格式「中文名=值」，供数据格悬浮预览。
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

        // 从 <mods> 读回属性条数（用于“清空”后回显 / 数据列统计）。
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

        // 数据列悬浮预览；名称列右键重命名。
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
                    // 左键三态切换由核心 cellClick 派发，这里只管悬浮提示。
                }
                if (col == ColName && e.type == EventType.MouseDown && e.button == 1) {
                    // IsLocal = 游戏本地关卡存档（只读，改名会破坏映射），只有自己的预设可改名。
                    if (!p.IsLocal) { ClearPending(); _renameKey = p.Key; _renameText = p.Name; _renameFocusPending = true; e.Use(); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/行内单元格", __ex); }
        }


        // 鼠标 x 落在哪一可视列（与核心 DrawTableCells 同一套排布）。
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

        private static PresetInfo _currentVisibleRow(int row) {
            try {
                List<PresetInfo> vis = _visibleCache;
                if (vis == null || row < 0 || row >= vis.Count) return null;
                return vis[row];
            } catch { return null; }
        }

        // 数据格悬浮提示：预设态列出非默认修改器，关闭/当前态各一句说明。
        private static string DataPreviewTip(PresetInfo p) {
            try {
                if (p == null) return null;
                string __mm = p.ModsMode ?? ModsInherit;
                if (string.Equals(__mm, ModsCurrent, StringComparison.Ordinal)) {
                    return SRApi.T("当前：加载时用游戏此刻的修改器（不覆盖预设自带）\n左键切到「关闭」",
                                     "Current: the level loads with the game's present modifiers (preset's own mods ignored)\nleft-click for Off");
                }
                if (string.Equals(__mm, ModsOff, StringComparison.Ordinal)) {
                    return SRApi.T("关闭：加载时把修改器写成默认值\n左键切回「预设」",
                                     "Off: modifiers are reset to defaults at load time\nleft-click for Preset");
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
