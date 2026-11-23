// 注意事项：声明自洽规则；生成器与单测共用，避免"声明写了但源码没有"。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal static class SpecRules {
        internal static bool GroupsCover(IList<string> groups, IDictionary<string, string> featureGroup) {
            if (groups == null || featureGroup == null) return false;
            var g = new HashSet<string>(groups);
            foreach (string k in featureGroup.Keys) if (!g.Contains(featureGroup[k])) return false;
            return true;
        }
        internal static bool NoDuplicate(IList<string> ids) {
            if (ids == null) return false;
            var seen = new HashSet<string>();
            foreach (string s in ids) if (!seen.Add(s)) return false;
            return true;
        }
        internal static string[] Missing(IList<string> declared, IList<string> present) {
            var p = new HashSet<string>(present ?? new string[0]);
            var outp = new List<string>();
            foreach (string d in declared ?? new string[0]) if (!p.Contains(d)) outp.Add(d);
            return outp.ToArray();
        }
    }


}

