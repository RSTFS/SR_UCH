// 主动自检 / 最近异常 / 支持包导出（自检页卡片）。
// 与"启动自检"互补：启动自检是启动瞬间的被动快照，这里是用户点按钮时的主动验证，
// 能回答"开关还在、但功能没反应"这类问题；结果可直接导出成支持包发给作者。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // MainPlugin.Awake 记录的各启动阶段耗时（"A=12ms B=3ms …"），自检/诊断/日志共用。
    internal static string BootTimings;

    private static List<string> _stResults;
    private static int _stPass, _stFail;

    private static void StCheck(string name, bool ok, string detail) {
        if (_stResults == null) _stResults = new List<string>();
        if (ok) _stPass++; else _stFail++;
        _stResults.Add((ok ? "✓ " : "✗ ") + name + (string.IsNullOrEmpty(detail) ? "" : "  ·  " + detail));
    }

    // 主动验证：通道注入 / 功能清单 / 补丁与反射 / 槽位往返 / 通知 / 配置 / 本地化 / 按键表。
    internal static void RunActiveSelfTest() {
        _stResults = new List<string>();
        _stPass = 0; _stFail = 0;
        try {
            string[] patchMiss; int patchTotal = SR.AuditPatches(out patchMiss);
            StCheck(T("补丁目标存在", "Patch targets exist"), patchMiss == null || patchMiss.Length == 0,
                patchMiss != null && patchMiss.Length > 0 ? string.Join(", ", patchMiss) : T("已校验 ", "checked ") + patchTotal);
            string[] magicConf = SRApi.NetMagic.Conflicts();
            StCheck(T("网络魔数唯一", "Network magics unique"), magicConf == null || magicConf.Length == 0,
                magicConf != null && magicConf.Length > 0 ? string.Join("; ", magicConf) : T("已登记 ", "registered ") + SRApi.NetMagic.Registered);
            string[] missStates = SRApi.Sinks.MissingStates();
            StCheck(T("API 状态通道", "API state channels"), missStates == null || missStates.Length == 0,
                missStates != null && missStates.Length > 0 ? string.Join(", ", missStates) : T("全部就绪", "all ready"));
            string[] miss = SRApi.MissingSinks();
            StCheck(T("API 通道注入", "API sinks injected"), miss == null || miss.Length == 0,
                miss != null && miss.Length > 0 ? (T("缺 ", "missing ") + miss.Length) : null);

            StCheck(T("功能清单", "Feature manifest"), Features.Count > 0 && Features.ProblemCount == 0,
                T("功能 ", "features ") + Features.Count + T(" · 问题 ", " · problems ") + Features.ProblemCount);

            try { SR.ScanPatches(); } catch (Exception __sp) { Guard.Log("主动自检/重扫补丁", __sp); }
            StCheck(T("补丁与反射", "Patches & reflection"),
                SR.AuditPatchTotal > 0 && SR.AuditPatchOk == SR.AuditPatchTotal && SR.AuditRefOk == SR.AuditRefTotal,
                SR.AuditPatchOk + "/" + SR.AuditPatchTotal + T(" 补丁 · ", " patches · ") + SR.AuditRefOk + "/" + SR.AuditRefTotal + T(" 反射", " members"));

            object token = new object();
            int slotBefore = Slots.Count;
            Slots.Provide(SRApi.SlotKeys.SelfTestToken, token);
            object got = Slots.Require<object>(SRApi.SlotKeys.SelfTestToken);
            Slots.Retract(SRApi.SlotKeys.SelfTestToken, token);
            StCheck(T("槽位往返", "Slots round-trip"),
                ReferenceEquals(token, got) && Slots.Count == slotBefore,
                slotBefore + " -> " + Slots.Count);

            bool notifyOk = SRApi.SinkCount() > 0;
            try { SRApi.Notify(SRApi.T("自检：通知通道正常", "Self-test: notify path works")); }
            catch (Exception __nt) { notifyOk = false; Guard.Log("主动自检/通知", __nt); }
            StCheck(T("通知通道", "Notify path"), notifyOk, T("注入 ", "sinks ") + SRApi.SinkCount());

            int entries = 0;
            bool cfgOk = _internalConfig != null;
            if (cfgOk) {
                try { foreach (ConfigEntryBase __e in AllEntries(_internalConfig)) entries++; }
                catch (Exception __ae) { Guard.Log("主动自检/配置条目", __ae); }
            }
            StCheck(T("配置可读", "Config readable"), cfgOk && entries > 0, T("条目 ", "entries ") + entries);

            string t = SRApi.T("自检", "Self-test");
            StCheck(T("本地化回退", "Localization fallback"), !string.IsNullOrEmpty(t), t);

            StCheck(T("按键注册表", "Hotkey registry"), SR.HotkeyCount > 0,
                T("快捷键 ", "hotkeys ") + SR.HotkeyCount);
        } catch (Exception __ex) {
            Guard.Log("主动自检", __ex);
            StCheck(T("自检执行", "Self-test run"), false, __ex.GetType().Name);
        }
        SRApi.Notify(_stFail == 0
            ? SRApi.T("主动自检：全部通过", "Self-test: all passed")
            : SRApi.T("主动自检：有失败项，见自检页", "Self-test: some checks failed, see self-check page"));
    }

    internal static void RenderActiveSelfTestCard() {
        try {
            int pass = _stPass, fail = _stFail;
            bool ran = _stResults != null && _stResults.Count > 0;
            string summary = !ran ? T("未运行（点下面按钮）", "not run yet (use the button below)")
                : T("通过 " + pass + " / 失败 " + fail, pass + " passed / " + fail + " failed");
            summary += "  ·  Tick " + SR.PerfTickAvgMs.ToString("0.00") + "/" + SR.PerfTickMaxMs.ToString("0.00")
                     + " ms  ·  GUI " + SR.PerfGuiAvgMs.ToString("0.00") + "/" + SR.PerfGuiMaxMs.ToString("0.00") + " ms";
            Card(!ran || fail == 0, T("主动自检", "Active self-test"), summary, delegate {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(T("运行自检", "Run self-test"), _btn, GUILayout.Width(Sc(110)), GUILayout.Height(Sc(24))))
                    RunActiveSelfTest();
                GUILayout.Space(Sc(6));
                if (GUILayout.Button(T("导出支持包", "Export support bundle"), _btn, GUILayout.Width(Sc(150)), GUILayout.Height(Sc(24))))
                    ExportSupportBundle();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(4));
                if (!ran) {
                    AuditLabel(T("说明：点「运行自检」会主动验证 API 注入、功能清单、补丁/反射、槽位往返、通知通道、"
                                 + "配置可读、本地化与按键注册表，结果列在这里。",
                                 "Runs active checks for API injection, feature manifest, patches/reflection, slot round-trip, "
                                 + "notify path, config readability, localization and the hotkey registry."));
                    return;
                }
                for (int i = 0; i < _stResults.Count; i++) {
                    string line = _stResults[i];
                    Color prev = GUI.color;
                    try {
                        if (line.StartsWith("✗")) GUI.color = new Color(1f, 0.72f, 0.35f);
                        else if (line.StartsWith("✓")) GUI.color = new Color(0.62f, 0.92f, 0.62f);
                        // 用 AuditLabel：按可用宽度实测高度，长行会换行且不会被裁（之前用固定行高被裁）。
                        AuditLabel(line);
                    } finally { GUI.color = prev; }
                }
            });
        } catch (Exception __ex) { Guard.Log("自检/主动自检卡", __ex); }
    }

    internal static void RenderGuardRecentCard() {
        try {
            string[] recent = SRApi.Guard.Recent();
            int warns = Guard.WarnCount;
            string summary = warns == 0 ? T("没有异常记录", "no errors recorded")
                : T("累计 " + warns + " 次（同来源 5 秒内已去重）", warns + " total (deduped per source for 5s)");
            Card(warns == 0, T("最近异常", "Recent errors"), summary, delegate {
                if (recent == null || recent.Length == 0) {
                    AuditLabel(T("（无记录）", "(none)"));
                    return;
                }
                for (int i = 0; i < recent.Length; i++) AuditLabel("• " + recent[i]);
                GUILayout.Space(Sc(4));
                AuditLabel(T("说明：这里只保留最近 16 条；完整记录在 BepInEx\\LogOutput.log，" 
                             + "点「导出支持包」会带上日志尾部。",
                             "Only the latest 16 are kept; the full log is in BepInEx\\LogOutput.log, "
                             + "and the support bundle includes its tail."));
            });
        } catch (Exception __ex) { Guard.Log("自检/最近异常卡", __ex); }
    }

    // 支持包：单个 txt（诊断报告 + 主动自检 + 最近异常 + 插件清单 + 配置键名 + 日志尾部）。
    // 不含配置值（可能有玩家名字/预设 XML），只列 section/key，避免把用户数据带走。
    internal static bool ExportSupportBundle() {
        try {
            string root = null;
            try { root = BepInEx.Paths.BepInExRootPath; } catch (Exception __pr) { Guard.Log("支持包/取 BepInEx 根目录", __pr); }
            if (string.IsNullOrEmpty(root)) root = Application.persistentDataPath;
            string dir = Path.Combine(root, "SR_UCH_support");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "SR_UCH_support_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== SR_UCH 支持包 ===");
            sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("游戏版本: " + Application.version);
            string dllVer = "?";
            try { System.Version v = typeof(MainPlugin).Assembly.GetName().Version; if (v != null) dllVer = v.ToString(); }
            catch (Exception __av) { Guard.Log("支持包/版本", __av); }
            sb.AppendLine("插件版本: " + dllVer + "  契约: v" + SRContract.Version);
            sb.AppendLine("启动耗时: " + (BootTimings ?? "(未记录)"));
            sb.AppendLine("第三方插件: " + CompatSummary());
            sb.AppendLine();
            sb.AppendLine(BuildDiagnosticReport());
            sb.AppendLine("== 主动自检 ==");
            if (_stResults == null || _stResults.Count == 0) sb.AppendLine("(本次会话尚未运行)");
            else for (int i = 0; i < _stResults.Count; i++) sb.AppendLine(_stResults[i]);
            sb.AppendLine();
            sb.AppendLine("== 最近异常 ==");
            string[] recent = SRApi.Guard.Recent();
            if (recent == null || recent.Length == 0) sb.AppendLine("(无)");
            else for (int i = 0; i < recent.Length; i++) sb.AppendLine("• " + recent[i]);
            sb.AppendLine();
            sb.AppendLine("== 配置条目（仅 section / key，不含值）==");
            try {
                if (_internalConfig != null) {
                    foreach (ConfigEntryBase __e in AllEntries(_internalConfig))
                        sb.AppendLine(__e.Definition.Section + " / " + __e.Definition.Key);
                }
            } catch (Exception __ce) { sb.AppendLine("(读取失败: " + __ce.GetType().Name + ")"); Guard.Log("支持包/配置条目", __ce); }
            sb.AppendLine();
            sb.AppendLine("== 日志尾部（最后 200 行）==");
            try {
                string log = Path.Combine(root, "LogOutput.log");
                if (File.Exists(log)) {
                    string[] lines = File.ReadAllLines(log);
                    int from = Math.Max(0, lines.Length - 200);
                    for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i]);
                } else sb.AppendLine("(找不到 " + log + ")");
            } catch (Exception __le) { sb.AppendLine("(日志读取失败: " + __le.GetType().Name + ")"); Guard.Log("支持包/日志", __le); }

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            try { GUIUtility.systemCopyBuffer = file; } catch (Exception __cb) { Guard.Log("支持包/复制路径", __cb); }
            try { SR.LogInfo("[SR.Support] 已导出支持包: " + file); } catch (Exception __lg) { Guard.Log("支持包/写日志", __lg); }
            SRApi.Notify(SRApi.T("支持包已导出（路径已复制到剪贴板）", "Support bundle exported (path copied to clipboard)"));
            return true;
        } catch (Exception __ex) {
            Guard.Log("导出支持包", __ex);
            SRApi.Notify(SRApi.T("支持包导出失败，详见日志", "Support bundle export failed; see log"));
            return false;
        }
    }
}
}
