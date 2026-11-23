// 结构化错误模型：把「功能没反应、查不到原因」变成可粘贴的事件码。
// 只含字段与纯函数；不引用 Unity / BepInEx / 实现层类型，契约层可独立编译。
// 注意事项：所有静默 catch 的站点表由 tools/spec_engine.py 生成，见 SrCatchSites.g.cs。
using System;
using System.Collections.Generic;

namespace SR_UCH {

    internal enum SrSeverity { Info, Warn, Error }

    internal sealed class SrError {
        internal string Code;
        internal string Owner;
        internal SrSeverity Severity;
        internal string Site;
        internal string Detail;
        internal string Action;

        internal SrError(string code, string owner, SrSeverity severity, string site, string detail) {
            Code = code ?? HealthCode.DegradedCatch;
            Owner = owner ?? "";
            Severity = severity;
            Site = site ?? "";
            Detail = detail ?? "";
            Action = HealthCode.Describe(Code);
        }

        internal string ToLine() {
            return Code + " [" + Owner + "] " + Site + " " + Detail + " → " + Action;
        }
        internal bool Valid { get { return HealthCode.IsKnown(Code); } }
    }

    // 统一结果：失败时不再返回 null / false 而没有线索。
    internal struct SrResult {
        internal readonly bool Ok;
        internal readonly SrError Error;
        private SrResult(bool ok, SrError err) { Ok = ok; Error = err; }
        internal static SrResult Success() { return new SrResult(true, null); }
        internal static SrResult Fail(SrError e) { return new SrResult(false, e); }
        internal string ToLine() { return Ok ? "OK" : (Error != null ? Error.ToLine() : "FAIL"); }
    }

    // 全局收集器。Flush 把错误收进 HealthSnapshot：诊断报告 / 支持包 / [SR.SelfTest]
    // 拿到同一份数据，不再各说各话。
    internal static class SrErrorSink {
        private static readonly List<SrError> _all = new List<SrError>();

        internal static int Count { get { return _all.Count; } }
        internal static int Pending { get { return _all.Count; } }

        internal static SrError Record(string code, string owner, string site, string detail, SrSeverity sev) {
            SrError e = new SrError(code, owner, sev, site, detail);
            if (e.Valid) _all.Add(e);
            return e;
        }

        // 静默 catch 的唯一入口：站点 → 事件码 → 建议动作，一次补齐。
        internal static SrError FromCatch(string site, string detail) {
            string code = SrCatchSites.CodeFor(site);
            SrSeverity sev = SrSeverity.Warn;
            for (int i = 0; i < SrCatchSites.All.Length; i++) {
                if (SrCatchSites.All[i].At != site) continue;
                sev = SrCatchSites.All[i].Severity == "error" ? SrSeverity.Error
                    : (SrCatchSites.All[i].Severity == "info" ? SrSeverity.Info : SrSeverity.Warn);
                break;
            }
            return Record(code, "catch", site, detail, sev);
        }

        internal static void Flush(HealthSnapshot h) {
            if (h == null || _all.Count == 0) return;
            for (int i = 0; i < _all.Count; i++) {
                SrError e = _all[i];
                h.Add(e.Code, e.Owner, e.Site + " " + e.Detail);
            }
            h.Errors += _all.Count;
            h.LastWhat = "catch";
            h.LastError = _all[_all.Count - 1].ToLine();
            List<string> issues = new List<string>();
            if (h.Issues != null) issues.AddRange(h.Issues);
            for (int i = 0; i < _all.Count; i++) issues.Add(_all[i].ToLine());
            h.Issues = issues.ToArray();
            _all.Clear();
        }

        internal static void Clear() { _all.Clear(); }
    }
}
