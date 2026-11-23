// 渲染轨迹的离线断言与影子记录；不依赖引擎，默认不参与运行时绘制。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal static class UiTrace {
        internal static int CountNodes(string trace) {
            if (string.IsNullOrEmpty(trace)) return 0;
            return trace.Split('\n').Length;
        }
        internal static bool HasDuplicate(string trace) {
            if (string.IsNullOrEmpty(trace)) return false;
            string[] lines = trace.Split('\n');
            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < lines.Length; i++) {
                int k = lines[i].LastIndexOf(':');
                string id = k >= 0 ? lines[i].Substring(k + 1) : lines[i];
                if (!ids.Add(id)) return true;
            }
            return false;
        }
        internal static bool ScrollPolicyAllowed(string policy) {
            return policy == "per-page" || policy == "reset-on-switch";
        }
        internal static string Verdict(string trace, string policy) {
            if (!ScrollPolicyAllowed(policy)) return "FAIL";
            if (HasDuplicate(trace)) return "FAIL";
            return CountNodes(trace) > 0 ? "OK" : "FAIL";
        }
        // 首个差异定位：返回行号（1 基）与两侧内容；一致返回 ""。
        internal static string Compare(string expected, string actual) {
            if (expected == null) expected = "";
            if (actual == null) actual = "";
            string[] a = expected.Split('\n');
            string[] b = actual.Split('\n');
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++) {
                string x = i < a.Length ? a[i] : "<none>";
                string y = i < b.Length ? b[i] : "<none>";
                if (x != y) return (i + 1) + "|" + x + "|" + y;
            }
            return "";
        }
        // 页面级滚动区最多一个，且不得嵌套；与节点树 Validate 同义。
        internal static string ScrollVerdict(string trace, int scrollNodes) {
            if (scrollNodes > 1) return "FAIL";
            if (!string.IsNullOrEmpty(trace) && trace.IndexOf("scroll:") >= 0 && scrollNodes == 0) return "FAIL";
            return CountNodes(trace) > 0 ? "OK" : "FAIL";
        }
    }

    // 影子轨迹：逐批接入时由渲染器调用；未接入时零开销、零行为变化。
    // 用法：Begin(pageId) → Node(kind,id)* → End() → Freeze() 得到期望轨迹。
    internal sealed class UiShadow {
        private readonly List<string> _lines = new List<string>();
        private string _page = "";
        private bool _frozen;
        internal void Begin(string pageId) {
            if (_frozen) return;
            _lines.Clear(); _page = pageId == null ? "" : pageId;
            _lines.Add("page:" + _page);
        }
        internal void Node(string kind, string id) {
            if (_frozen || string.IsNullOrEmpty(_page)) return;
            _lines.Add(kind + ":" + id);
        }
        internal void End() { _page = ""; _frozen = true; }
        internal string Freeze() { _frozen = true; return string.Join("\n", _lines.ToArray()); }
        internal void Reset() { _lines.Clear(); _page = ""; _frozen = false; }
        internal int Count { get { return _lines.Count; } }
    }
}
