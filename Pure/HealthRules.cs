// 注意事项：健康快照的统一判定；五处渲染器只读这一个结论。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal static class HealthRules {
        internal static string Verdict(bool internalOk, bool refOk, bool needReverify, int missing) {
            if (!internalOk || !refOk) return "FAIL";
            if (needReverify) return "REVERIFY";
            if (missing > 0) return "REVIEW";
            return "OK";
        }
        internal static bool AllHit(string ab) {
            if (string.IsNullOrEmpty(ab)) return false;
            string[] p = ab.Split('/');
            return p.Length == 2 && p[0] == p[1] && p[0].Length > 0;
        }
    }
}
