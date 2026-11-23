// 注意事项：cfg 文本解析与版本门；不读写文件，便于表驱动测试。
using System;
using System.Collections.Generic;

namespace SR_UCH.Pure {
    internal static class CfgRules {
        internal const string SchemaKey = "SchemaVersion";
        internal const int CurrentSchema = 3;

        internal static int ReadSchema(string text) {
            if (text == null) return -1;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n')) {
                string t = line.Trim();
                if (!t.StartsWith(SchemaKey, StringComparison.Ordinal)) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                int v;
                if (int.TryParse(t.Substring(eq + 1).Trim(), out v)) return v;
            }
            return -1;
        }

        internal static bool SchemaMatches(string text) { return ReadSchema(text) == CurrentSchema; }

        internal static string WithSchema(string text) {
            string body = text == null ? "" : text;
            if (SchemaMatches(body)) return body;
            return SchemaKey + "=" + CurrentSchema + "\n" + body;
        }

        // 删指定 (段, 键)；重复键保留最后一条。
        internal static string RemovePairs(string text, IList<string> pairs, char sep) {
            if (text == null) return "";
            var drop = new HashSet<string>();
            if (pairs != null) foreach (string p in pairs) if (p != null) drop.Add(p);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var last = new Dictionary<string, int>();
            string cur = "";
            for (int i = 0; i < lines.Length; i++) {
                string t = lines[i].Trim();
                if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) { cur = t.Substring(1, t.Length - 2); continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                last[cur + sep + t.Substring(0, eq).TrimEnd()] = i;
            }
            var outp = new List<string>();
            cur = "";
            for (int i = 0; i < lines.Length; i++) {
                string t = lines[i].Trim();
                if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) { cur = t.Substring(1, t.Length - 2); outp.Add(lines[i]); continue; }
                int eq = t.IndexOf('=');
                if (eq > 0) {
                    string key = t.Substring(0, eq).TrimEnd();
                    string id = cur + sep + key;
                    if (drop.Contains(id)) continue;
                    int keep;
                    if (last.TryGetValue(id, out keep) && keep != i) continue;
                }
                outp.Add(lines[i]);
            }
            return string.Join("\n", outp.ToArray());
        }
    }
}
