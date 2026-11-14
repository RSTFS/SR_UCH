// 第三方插件兼容性自检：列出从 BepInEx\plugins 加载的其它插件，并给出已知冲突/注意事项。
//
// 数据来源：AppDomain 里已加载程序集的位置（含 BepInEx\plugins 且不是 SR 自己）。
// 规则来源：对本机实际安装的插件做过静态分析（Mono.Cecil）后手工登记，
// 只写"对 SR 用户有意义的结论"，不做自动推断（自动推断误报比不报更糟）。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // 一条已知兼容性规则：匹配程序集简单名（不区分大小写）。
    private sealed class CompatRule {
        public readonly string Id;
        public readonly int Level;   // 0 = 无已知冲突；1 = 注意；2 = 冲突
        public readonly string Zh, En;
        public CompatRule(string id, int level, string zh, string en) { Id = id; Level = level; Zh = zh; En = en; }
    }

    private sealed class CompatPlugin {
        public string Name, Version;
        public int Level;
        public bool Known;
        public string Zh, En;
    }

    private static readonly CompatRule[] CompatRules = new CompatRule[] {
        new CompatRule("CamHog", 1,
            "与「视觉辅助」目标相同：CamHog 在时 SR 的视觉辅助自动让位；两者保留一个即可。",
            "Same goal as Visual Aid: SR's Visual Aid yields while CamHog is loaded; keep only one."),
        new CompatRule("WalljumpBugFix", 0,
            "输入时序修复，补丁目标（Controller.Notify / Character.ReceiveEvent）与 SR 无重叠。",
            "Input timing fix; its patch targets do not overlap SR."),
        new CompatRule("LevelLibrary", 1,
            "往树屋电脑注入本地关卡库/筛选 UI；与 UCH Map Packs 同装时两边都要改电脑界面，可能互相干扰。与 SR 的布局预设共享 .snapshot 目录（只读）。",
            "Injects a local level library/filter UI into the treehouse computer; it can fight UCH Map Packs for that UI. Shares the .snapshot folder with SR presets (read-only)."),
        new CompatRule("UCHMapPacks", 1,
            "地图包/挑战模式大 mod：挑战计时与 SR 的 HUD 计时会重复显示（建议关掉一个）；与 Level Library 抢树屋电脑 UI。",
            "Big map-pack/challenge mod: its challenge timer duplicates SR's HUD timer (turn one off); competes with Level Library for the computer UI."),
        new CompatRule("EvenMoreMaps", 1,
            "自定义地图/机关大 mod。当前实例里它是 .ccc（未加载）；启用后与 SR 关卡功能的组合未实测。",
            "Custom maps/mechanics mod. Currently disabled here (.ccc); combinations with SR level features are untested."),
        new CompatRule("LevelLoadingOptimizer", 0,
            "只给 Placeable.Place 加加速前缀，与 SR 补丁无重叠。",
            "Only adds a speed-up prefix to Placeable.Place; no overlap with SR patches."),
        new CompatRule("BetterNightBG", 0,
            "只改夜间背景颜色（Level.SetBackground 后缀），纯视觉。",
            "Only recolors the night background (Level.SetBackground postfix); purely visual."),
        new CompatRule("UCHSoundScout", 1,
            "声音相关插件；SR 不改音频，但界面/按键可能重叠（未实测）。",
            "Audio-related plugin; SR does not touch audio, but UI/hotkeys may overlap (untested)."),
        new CompatRule("UCHSounds", 1,
            "声音相关插件；SR 不改音频，但界面/按键可能重叠（未实测）。",
            "Audio-related plugin; SR does not touch audio, but UI/hotkeys may overlap (untested)."),
        new CompatRule("Barnyard", 1,
            "动物/模型相关插件；与 SR 无补丁重叠，但同类目功能（角色自动选择等）可能互相覆盖状态。",
            "Animal/cosmetic plugin; no patch overlap, but similar features may overwrite each other's state."),
    };

    private static List<CompatPlugin> _compatPlugins;

    private static bool InPluginsDir(string loc) {
        if (string.IsNullOrEmpty(loc)) return false;
        return loc.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0
            && loc.IndexOf("plugins", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CompatRule FindCompatRule(string name) {
        for (int i = 0; i < CompatRules.Length; i++)
            if (string.Equals(CompatRules[i].Id, name, StringComparison.OrdinalIgnoreCase)) return CompatRules[i];
        return null;
    }

    private static void ScanCompatPlugins() {
        List<CompatPlugin> rows = new List<CompatPlugin>();
        try {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++) {
                Assembly asm = all[i];
                if (asm == null || asm.IsDynamic) continue;
                AssemblyName an = asm.GetName();
                string name = an != null ? an.Name : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (name == "SR_UCH" || name == "SR_UCH_EX" || name == "SR_Manager" || name == "SR_UCH_Manager") continue;
                if (!InPluginsDir(asm.Location)) continue;
                CompatRule rule = FindCompatRule(name);
                CompatPlugin row = new CompatPlugin();
                row.Name = name;
                row.Version = an.Version != null ? an.Version.ToString() : "";
                row.Known = rule != null;
                row.Level = rule != null ? rule.Level : 0;
                row.Zh = rule != null ? rule.Zh : "未测试：SR 没有分析过这个插件（不等于有冲突）。";
                row.En = rule != null ? rule.En : "Untested: SR has not analysed this plugin (not the same as broken).";
                rows.Add(row);
            }
            rows.Sort(delegate (CompatPlugin a, CompatPlugin b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        } catch (Exception __ex) { Guard.Log("兼容性/扫描插件", __ex); }
        _compatPlugins = rows;
    }

    private static void EnsureCompat() {
        if (_compatPlugins == null) ScanCompatPlugins();
    }

    // 诊断报告里的一行摘要。
    internal static string CompatSummary() {
        try {
            EnsureCompat();
            int conflict = 0, note = 0, other = 0;
            for (int i = 0; i < _compatPlugins.Count; i++) {
                CompatPlugin p = _compatPlugins[i];
                if (p.Level >= 2) conflict++;
                else if (p.Level == 1) note++;
                else other++;
            }
            return _compatPlugins.Count + T(" 个（冲突 ", " detected (conflict ") + conflict + T(" · 注意 ", " · note ")
                + note + T(" · 无已知冲突/未测试 ", " · clear/untested ") + other + "）";
        } catch (Exception __ex) { Guard.Log("兼容性/摘要", __ex); return "?"; }
    }

    private static string CompatVerdict(CompatPlugin p) {
        if (p.Level >= 2) return T("冲突", "conflict");
        if (p.Level == 1) return T("注意", "note");
        return p.Known ? T("无已知冲突", "clear") : T("未测试", "untested");
    }

    internal static void RenderCompatCard() {
        try {
            EnsureCompat();
            int conflict = 0, note = 0;
            for (int i = 0; i < _compatPlugins.Count; i++) {
                if (_compatPlugins[i].Level >= 2) conflict++;
                else if (_compatPlugins[i].Level == 1) note++;
            }
            string summary;
            if (_compatPlugins.Count == 0) summary = T("未检测到其它插件", "no other plugins detected");
            else summary = T("已装 " + _compatPlugins.Count + " 个：冲突 " + conflict + " · 注意 " + note,
                             _compatPlugins.Count + " installed: " + conflict + " conflict(s), " + note + " note(s)");
            Card(conflict == 0, T("第三方插件", "Other plugins"), summary, delegate {
                if (_compatPlugins.Count == 0) {
                    AuditLabel(T("（从 BepInEx\\plugins 加载的其它插件会列在这里；SR 自己的 DLL 不计入）",
                                 "(other plugins loaded from BepInEx\\plugins are listed here; SR's own DLLs are excluded)"));
                    return;
                }
                for (int i = 0; i < _compatPlugins.Count; i++) {
                    CompatPlugin p = _compatPlugins[i];
                    string head = (p.Level >= 2 ? "▲ " : p.Level == 1 ? "▲ " : p.Known ? "● " : "○ ")
                        + p.Name + (string.IsNullOrEmpty(p.Version) ? "" : " " + p.Version)
                        + "  ·  " + CompatVerdict(p);
                    AuditLabel(head);
                    AuditLabel("    " + T(p.Zh, p.En));
                }
                GUILayout.Space(Sc(4));
                AuditLabel(T("说明：结论来自对本机插件的静态分析（解析程序集/DLL 补丁目标），不是运行时互测；"
                             + "「未测试」只代表 SR 还没分析过它，不代表会冲突。",
                             "Notes: conclusions come from static analysis of installed plugins (assembly/member targets), "
                             + "not live co-op testing; \"untested\" only means SR has not analysed it yet -- not that it conflicts."));
            });
        } catch (Exception __ex) { Guard.Log("自检/第三方插件", __ex); }
    }

    // 供启动日志用的一行（不含换行）。
    internal static string CompatOneLine() {
        try {
            EnsureCompat();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _compatPlugins.Count; i++) {
                if (i > 0) sb.Append(", ");
                sb.Append(_compatPlugins[i].Name);
            }
            return sb.Length > 0 ? sb.ToString() : "(none)";
        } catch (Exception __ex) { Guard.Log("兼容性/一行", __ex); return "?"; }
    }
}
}
