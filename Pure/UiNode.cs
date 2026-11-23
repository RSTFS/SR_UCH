// 声明式页面的纯数据节点与校验；不依赖渲染后端，可离线断言。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal sealed class UiNode {
        internal string Id;
        internal string Kind;
        internal string Policy;
        internal string Layer;
        internal readonly List<UiNode> Children = new List<UiNode>();
        internal UiNode(string id, string kind) { Id = id; Kind = kind; Policy = "per-page"; Layer = "content"; }
        internal void Add(UiNode n) { if (n != null) Children.Add(n); }
    }
    internal static class UiNodeRules {
        private static readonly string[] Kinds = new string[] {
            "page", "header", "tabs", "sections", "rows", "footer",
            "toolbar", "table", "viewport", "cards", "verdict", "scroll",
            "row", "control"
        };
        private static readonly string[] Layers = new string[] { "content", "overlay", "hud" };
        private static readonly string[] Policies = new string[] { "per-page", "reset-on-switch" };

        internal static bool KindOk(string k) { return Array.IndexOf(Kinds, k) >= 0; }
        internal static bool LayerOk(string l) { return Array.IndexOf(Layers, l) >= 0; }
        internal static bool PolicyOk(string p) { return Array.IndexOf(Policies, p) >= 0; }

        internal static string Trace(UiNode root) {
            List<string> lines = new List<string>();
            Walk(root, 0, lines);
            return string.Join("\n", lines.ToArray());
        }
        private static void Walk(UiNode n, int depth, List<string> lines) {
            if (n == null) return;
            lines.Add(new string(' ', depth * 2) + n.Kind + ":" + n.Id);
            for (int i = 0; i < n.Children.Count; i++) Walk(n.Children[i], depth + 1, lines);
        }
        internal static int NodeCount(UiNode root) {
            if (root == null) return 0;
            int n = 1;
            for (int i = 0; i < root.Children.Count; i++) n += NodeCount(root.Children[i]);
            return n;
        }
        internal static IList<string> DuplicateIds(UiNode root) {
            Dictionary<string, int> seen = new Dictionary<string, int>();
            List<string> dup = new List<string>();
            Collect(root, seen, dup);
            return dup;
        }
        private static void Collect(UiNode n, Dictionary<string, int> seen, List<string> dup) {
            if (n == null) return;
            int c;
            seen.TryGetValue(n.Id, out c);
            if (c == 1 && !dup.Contains(n.Id)) dup.Add(n.Id);
            seen[n.Id] = c + 1;
            for (int i = 0; i < n.Children.Count; i++) Collect(n.Children[i], seen, dup);
        }
        // 结构不变量：节点类型、层、策略、id 唯一、滚动区不得嵌套。
        // 返回问题列表；空列表即通过。与 tools/check_uimodel.py 同源断言。
        internal static IList<string> Validate(UiNode root) {
            List<string> bad = new List<string>();
            if (root == null) { bad.Add("null-root"); return bad; }
            if (root.Kind != "page") bad.Add("root-kind");
            WalkValidate(root, 0, bad);
            IList<string> dup = DuplicateIds(root);
            for (int i = 0; i < dup.Count; i++) bad.Add("dup:" + dup[i]);
            return bad;
        }
        private static void WalkValidate(UiNode n, int scrollDepth, List<string> bad) {
            if (!KindOk(n.Kind)) bad.Add("kind:" + n.Kind);
            if (!LayerOk(n.Layer)) bad.Add("layer:" + n.Layer);
            if (!PolicyOk(n.Policy)) bad.Add("policy:" + n.Policy);
            if (string.IsNullOrEmpty(n.Id)) bad.Add("empty-id");
            int d = scrollDepth;
            if (n.Kind == "scroll") {
                d++;
                if (d > 1) bad.Add("nested-scroll:" + n.Id);
            }
            for (int i = 0; i < n.Children.Count; i++) WalkValidate(n.Children[i], d, bad);
        }
        internal static int ScrollCount(UiNode root) {
            if (root == null) return 0;
            int n = root.Kind == "scroll" ? 1 : 0;
            for (int i = 0; i < root.Children.Count; i++) n += ScrollCount(root.Children[i]);
            return n;
        }
    }
}
