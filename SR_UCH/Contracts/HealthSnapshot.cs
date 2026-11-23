// 健康快照：自检卡片 / 诊断报告 / 支持包 / [SR.SelfTest] 日志行 / CI 基线的同一份数据模型。
// 只含字段与纯函数，不引用游戏类型、Unity 或实现层标识；契约层可独立编译与单测。
// 注意：文本与字段名同时被 tools/uch_rules.py 和生成器核对，改名要同步改三处。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SR_UCH {

    // 结构化事件码：功能没反应时留一行可粘进 issue 的码，不再是没有线索的黑洞。
    internal static class HealthCode {
        internal const string DegradedCatch = "SR-H002";     // 运行时异常被 Guard 降级
        internal const string ReflectionGone = "SR-H003";    // 反射成员消失
        internal const string PatchGone = "SR-H004";         // 补丁目标失效
        internal const string SinkMissing = "SR-H005";       // API 通道未注入
        internal const string FeatureProblem = "SR-H006";    // 功能表登记异常

        internal static readonly string[] All = new string[] {
            DegradedCatch, ReflectionGone, PatchGone, SinkMissing, FeatureProblem
        };

        internal static bool IsKnown(string code) {
            if (code == null) return false;
            for (int i = 0; i < All.Length; i++) if (All[i] == code) return true;
            // 语义码：SR-H-<域>-<SIL|GUARD>-<三位序号>，由生成器按源码派生。
            if (!code.StartsWith("SR-H-", StringComparison.Ordinal)) return false;
            string[] parts = code.Split('-');
            if (parts.Length != 5 || parts[2].Length == 0) return false;
            if (parts[3] != "SIL" && parts[3] != "GUARD") return false;
            if (parts[4].Length != 3) return false;
            for (int i = 0; i < 3; i++) if (parts[4][i] < '0' || parts[4][i] > '9') return false;
            return true;
        }

        internal static string Describe(string code) {
            if (code == DegradedCatch) return "运行时异常被降级处理";
            if (code == ReflectionGone) return "反射成员消失";
            if (code == PatchGone) return "补丁目标失效";
            if (code == SinkMissing) return "API 通道未注入";
            if (code == FeatureProblem) return "功能表登记异常";
            if (!IsKnown(code)) return "未知事件";
            string[] p = code.Split('-');
            string kind = p[3] == "SIL" ? "静默 catch（异常被吞）" : "异常已记日志后降级";
            return "域 " + p[2] + " / " + kind + " / 序号 " + p[4];
        }
    }

    // schema 版本：生成器写 build/specs/generated/health.schema.json，CI 与单测按此双向核对。
    internal static class HealthSchema {
        internal const int Version = 1;

        internal static readonly string[] Fields = new string[] {
            "schema", "verdict", "game", "contract", "patches_ok", "patches_total",
            "reflection_ok", "reflection_total", "features", "feature_problems",
            "plugins", "boot_timings", "sink_ok", "sink_total", "missing_sinks",
            "errors", "last_what", "last_error", "issues", "events"
        };

        internal static bool Has(string field) {
            for (int i = 0; i < Fields.Length; i++) if (Fields[i] == field) return true;
            return false;
        }
    }

    internal sealed class HealthEvent {
        internal string Code;
        internal string Scope;
        internal string Detail;

        internal HealthEvent(string code, string scope, string detail) {
            Code = code;
            Scope = scope ?? "";
            Detail = detail ?? "";
        }

        internal string ToLine() { return Code + " [" + Scope + "] " + Detail; }
        internal bool Valid { get { return HealthCode.IsKnown(Code); } }
    }

    internal sealed class HealthSnapshot {
        internal string Game = "?";
        internal string Contract = "0";
        internal int PatchOk, PatchTotal, RefOk, RefTotal;
        internal int FeaturesCount, FeatureProblems;
        internal string Plugins = "";
        internal string BootTimings = "";
        internal int SinkOk, SinkTotal;
        internal string[] MissingSinks = new string[0];
        internal int Errors;
        internal string LastWhat = "", LastError = "";
        internal string[] Issues = new string[0];
        internal string TickProfile = "";
        internal string RuntimeReport = "";
        internal string Attribution = "";
        internal readonly List<HealthEvent> Events = new List<HealthEvent>();

        internal void Add(string code, string scope, string detail) {
            if (!HealthCode.IsKnown(code)) return;
            Events.Add(new HealthEvent(code, scope, detail));
        }

        // 纯函数：OK / FAIL 只由可判定字段决定；运行时异常走事件码，不改变启动结论。
        internal string Verdict() {
            if (PatchTotal > 0 && PatchOk != PatchTotal) return "FAIL";
            if (RefTotal > 0 && RefOk != RefTotal) return "FAIL";
            if (SinkTotal > 0 && SinkOk != SinkTotal) return "FAIL";
            if (FeatureProblems > 0) return "FAIL";
            if (MissingSinks != null && MissingSinks.Length > 0) return "FAIL";
            return "OK";
        }

        // [SR.SelfTest] 行的唯一格式定义；tools/uch_rules.py 的正则按这里对齐。
        internal string ToSelfTestLine(string verdict) {
            return "[SR.SelfTest] " + verdict
                + " 游戏版本=" + Game
                + " 契约=v" + Contract
                + " 补丁=" + PatchOk.ToString(CultureInfo.InvariantCulture) + "/" + PatchTotal.ToString(CultureInfo.InvariantCulture)
                + " 反射=" + RefOk.ToString(CultureInfo.InvariantCulture) + "/" + RefTotal.ToString(CultureInfo.InvariantCulture)
                + " 功能=" + FeaturesCount.ToString(CultureInfo.InvariantCulture)
                + " API=" + SinkOk.ToString(CultureInfo.InvariantCulture) + "/" + SinkTotal.ToString(CultureInfo.InvariantCulture);
        }

        internal string ToText() {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== SR_UCH 诊断报告 ===");
            sb.Append("游戏版本: ").AppendLine(Game);
            sb.Append("契约版本: v").AppendLine(Contract);
            sb.Append("补丁: ").Append(PatchOk).Append("/").Append(PatchTotal)
              .Append("  反射: ").Append(RefOk).Append("/").Append(RefTotal).AppendLine();
            sb.Append("功能: ").Append(FeaturesCount).Append("  问题: ").Append(FeatureProblems).AppendLine();
            sb.Append("第三方插件: ").AppendLine(Plugins);
            sb.Append("启动耗时: ").AppendLine(string.IsNullOrEmpty(BootTimings) ? "(未记录)" : BootTimings);
            sb.Append("API 注入: ").Append(SinkOk).Append("/").Append(SinkTotal).AppendLine();
            if (MissingSinks != null && MissingSinks.Length > 0)
                sb.Append("缺失注入: ").AppendLine(string.Join(",", MissingSinks));
            sb.Append("运行时异常: ").AppendLine(Errors.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(LastWhat))
                sb.Append("最近一次: ").Append(LastWhat).Append(" - ").AppendLine(LastError ?? "");
            if (Issues != null) {
                for (int i = 0; i < Issues.Length; i++) {
                    sb.Append("  ✗ ").AppendLine(Issues[i]);
                }
            }
            sb.AppendLine(TickProfile);
            sb.AppendLine(RuntimeReport);
            sb.AppendLine(Attribution);
            sb.AppendLine("== 结构化事件（可粘贴的事件码）==");
            if (Events.Count == 0) sb.AppendLine("(无)");
            else for (int i = 0; i < Events.Count; i++) sb.AppendLine(Events[i].ToLine());
            sb.Append("健康快照: schema=v").Append(HealthSchema.Version)
              .Append(" / 字段 ").Append(HealthSchema.Fields.Length)
              .Append(" / 结论 ").AppendLine(Verdict());
            return sb.ToString();
        }

        // 手工 JSON：不依赖 System.Text.Json，老运行时与契约层独立编译都能用。
        internal string ToJson() {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            JStr(sb, "schema", HealthSchema.Version.ToString(CultureInfo.InvariantCulture), false, true);
            JStr(sb, "verdict", Verdict(), false, false);
            JStr(sb, "game", Game, false, false);
            JStr(sb, "contract", Contract, false, false);
            JNum(sb, "patches_ok", PatchOk); JNum(sb, "patches_total", PatchTotal);
            JNum(sb, "reflection_ok", RefOk); JNum(sb, "reflection_total", RefTotal);
            JNum(sb, "features", FeaturesCount); JNum(sb, "feature_problems", FeatureProblems);
            JStr(sb, "plugins", Plugins, false, false);
            JStr(sb, "boot_timings", BootTimings, false, false);
            JNum(sb, "sink_ok", SinkOk); JNum(sb, "sink_total", SinkTotal);
            JArr(sb, "missing_sinks", MissingSinks);
            JNum(sb, "errors", Errors);
            JStr(sb, "last_what", LastWhat, false, false);
            JStr(sb, "last_error", LastError, false, false);
            JArr(sb, "issues", Issues);
            sb.Append("  \"events\": [");
            for (int i = 0; i < Events.Count; i++) {
                if (i > 0) sb.Append(',');
                sb.Append("\n    {\"code\": ").Append(Q(Events[i].Code))
                  .Append(", \"scope\": ").Append(Q(Events[i].Scope))
                  .Append(", \"detail\": ").Append(Q(Events[i].Detail)).Append("}");
            }
            if (Events.Count > 0) sb.Append("\n  ");
            sb.Append("]\n}");
            return sb.ToString();
        }

        private static string Q(string s) {
            if (s == null) return "\"\"";
            StringBuilder b = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private static void JStr(StringBuilder sb, string key, string val, bool first, bool num) {
            sb.Append("  \"").Append(key).Append("\": ").Append(num ? (val ?? "0") : Q(val)).Append(",\n");
        }
        private static void JNum(StringBuilder sb, string key, int val) {
            sb.Append("  \"").Append(key).Append("\": ").Append(val.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        }
        private static void JArr(StringBuilder sb, string key, string[] arr) {
            sb.Append("  \"").Append(key).Append("\": [");
            if (arr != null) {
                for (int i = 0; i < arr.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Q(arr[i])); }
            }
            sb.Append("],\n");
        }
    }
}
