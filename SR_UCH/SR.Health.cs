// 健康快照运行时采集：只负责把现有诊断数据填进同一份 HealthSnapshot。
// 文本 / [SR.SelfTest] 日志行 / JSON 的格式都定义在契约层，采集层不拼字符串。
// 注意：启动自检阶段也会调用，所有取值都要能容忍尚未初始化的来源。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SR_UCH.Tweaks {

    internal static class Health {

        internal static HealthSnapshot Capture(string gameVersion) {
            HealthSnapshot s = new HealthSnapshot();
            try {
                s.Game = string.IsNullOrEmpty(gameVersion) ? "?" : gameVersion;
                s.Contract = SRContract.Version.ToString(CultureInfo.InvariantCulture);
                s.PatchOk = SR.AuditPatchOk;
                s.PatchTotal = SR.AuditPatchTotal;
                s.RefOk = SR.AuditRefOk;
                s.RefTotal = SR.AuditRefTotal;
                s.FeaturesCount = Features.Count;
                s.FeatureProblems = Features.ProblemCount;
                s.Plugins = Safe(SR.CompatSummary());
                s.BootTimings = SR.BootTimings ?? "";
                s.SinkOk = SRApi.SinkCount();
                s.SinkTotal = SRApi.SinkTotal();
                s.MissingSinks = SRApi.MissingSinks() ?? new string[0];
                s.Errors = SR.Guard.WarnCount;
                s.LastWhat = SR.Guard.LastWhat ?? "";
                s.LastError = SR.Guard.LastError ?? "";
                s.TickProfile = Safe(SR.TickProfileReport());
                s.RuntimeReport = Safe(FeatureRuntime.Report());
                s.Attribution = Safe(SR.AttributionReport());
                IList<SR.AuditIssue> issues = SR.AuditIssues;
                List<string> rows = new List<string>();
                if (issues != null) {
                    for (int i = 0; i < issues.Count; i++) {
                        SR.AuditIssue a = issues[i];
                        if (a == null) continue;
                        rows.Add((a.Kind ?? "") + " " + (a.Target ?? "") + " <- " + (a.Scope ?? ""));
                    }
                }
                s.Issues = rows.ToArray();
            } catch (Exception ex) {
                SR.Guard.Log("健康快照采集", ex);
                s.Add(HealthCode.DegradedCatch, "health.capture", ex.GetType().Name);
            }
            CollectEvents(s);
            return s;
        }

        // 结构化事件：软依赖 miss / 降级 catch / 失效目标 / 未注入通道统一转成事件码。
        private static void CollectEvents(HealthSnapshot s) {
            SrErrorSink.Flush(s);   // 静默 catch 的事件码进同一份快照
            if (s.Errors > 0)
                s.Add(HealthCode.DegradedCatch, s.LastWhat, s.LastError);
            if (s.Issues != null) {
                for (int i = 0; i < s.Issues.Length; i++) {
                    string row = s.Issues[i] ?? "";
                    if (row.StartsWith("反射", StringComparison.Ordinal))
                        s.Add(HealthCode.ReflectionGone, "startup-audit", row);
                    else if (row.StartsWith("补丁", StringComparison.Ordinal))
                        s.Add(HealthCode.PatchGone, "startup-audit", row);
                }
            }
            if (s.MissingSinks != null) {
                for (int i = 0; i < s.MissingSinks.Length; i++)
                    s.Add(HealthCode.SinkMissing, s.MissingSinks[i], "API 通道未注入");
            }
            if (s.FeatureProblems > 0)
                s.Add(HealthCode.FeatureProblem, "feature-manifest", "问题数 " + s.FeatureProblems.ToString(CultureInfo.InvariantCulture));
        }

        // 调用方都是直接取值（不是 lambda），故按值语义：取不到/为 null 一律回落空串。
        private static string Safe(string v) {
            try { return v ?? ""; } catch (Exception ex) { SR.Guard.Log("健康快照/分区报告", ex); return ""; }
        }

        internal static string DiagnosticText(string gameVersion) {
            try { return Capture(gameVersion).ToText(); }
            catch (Exception ex) { SR.Guard.Log("健康快照/文本", ex); return "=== SR_UCH 诊断报告 ===\n(采集失败: " + ex.GetType().Name + ")\n"; }
        }

        internal static string DiagnosticJson(string gameVersion) {
            try { return Capture(gameVersion).ToJson(); }
            catch (Exception ex) { SR.Guard.Log("健康快照/JSON", ex); return "{\"schema\":1,\"verdict\":\"FAIL\"}"; }
        }
    }
}
