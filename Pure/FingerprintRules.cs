// 注意事项：指纹只含成员签名；hash 与命中判定为纯函数。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal static class FingerprintRules {
        internal static string Canonical(IList<string> lines) {
            var a = new List<string>(lines);
            a.Sort(StringComparer.Ordinal);
            return string.Join("\n", a.ToArray());
        }
        internal static bool AllHit(int ok, int total) { return total >= 0 && ok == total; }
        // 属性 getter/setter 统一按属性名比对；外部组件条目直接跳过。
        internal static string MemberAlias(string member) {
            if (string.IsNullOrEmpty(member)) return member;
            if (member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal)) return member.Substring(4);
            return member;
        }
        internal static bool ShouldSkip(bool external) { return external; }
    }
}
