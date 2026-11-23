using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SR_UCH.Tweaks {
    public static partial class ConfigMigration {
        public static readonly string[] DeletedSections = new string[] {
            "Saved Lobby Details",
            "Camera",
            "Respawn",
            "Support",
            "Reflection",
            "Freeplay",
            "Config Tools",
            "Match Brief", "Accessibility", "Host Console",
            "Level Recorder", "Match Profile", "Match Cast", "Time Control", "Death Report",
        };

        internal const string AuditSep = "\u0001";
        // 本次启动内应用过的 Remove 规则（[sec,key]）与删除过的键名：
        // AuditAgainst 据此报"删了仍在用的活键"，Shutdown 时汇总到日志。
        private static readonly List<string[]> _appliedRemovals = new List<string[]>();
        private static System.Collections.Generic.List<string> _removedKeysLog = new System.Collections.Generic.List<string>();

        public static int RuleConflictCount;

        private static string RemoveKeys(string text, string section, string[] keys) {
            try {
                if (keys != null) for (int __k = 0; __k < keys.Length; __k++) _appliedRemovals.Add(new[] { section, keys[__k] });
                // 名单手工维护且此处尚未 Bind、读不到活键，故逐条记录删除以便事后反查。
                if (_removedKeysLog != null && keys != null) {
                    for (int __k = 0; __k < keys.Length; __k++) _removedKeysLog.Add(section + "/" + keys[__k]);
                }
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                List<string> outp = new List<string>();
                string cur = "";
                bool changed = false;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        outp.Add(lines[i]);
                        continue;
                    }
                    {
                        int eq = t.IndexOf('=');
                        if (eq > 0) {
                            string k = t.Substring(0, eq).TrimEnd();
                            bool drop = false;
                            // 组合键条目统一 Bind 在 [Settings] 段，键名 = “组合键 <目标段> <目标键>”。
                            if (k.StartsWith("组合键 " + section + " ", StringComparison.Ordinal)) {
                                for (int j = 0; j < keys.Length; j++) {
                                    if (k == "组合键 " + section + " " + keys[j]) { drop = true; break; }
                                }
                            } else if (cur == section) {
                                for (int j = 0; j < keys.Length; j++) {
                                    if (k == keys[j]) { drop = true; break; }
                                }
                            }
                            if (drop) { changed = true; continue; }
                        }
                    }
                    outp.Add(lines[i]);
                }
                return changed ? string.Join("\n", outp.ToArray()) : text;
            } catch (Exception __ex) { SR.Guard.Log("cfg 废弃键清理", __ex); return text; }
        }

        private static string DedupeSectionKeys(string text) {
            try {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                // 同名键只保留最后一条（后写覆盖先写，生效值不变）：先记每个 (段, 键) 末次行号。
                Dictionary<string, int> lastAt = new Dictionary<string, int>();
                string cur = "";
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        continue;
                    }
                    if (t.StartsWith("#", StringComparison.Ordinal)) continue;   //注释行（BepInEx 写的是 ##）
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    lastAt[cur + "\u0001" + t.Substring(0, eq).TrimEnd()] = i;
                }
                List<string> outp = new List<string>();
                cur = "";
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) {
                        cur = t.Substring(1, t.Length - 2);
                        outp.Add(lines[i]);
                        continue;
                    }
                    if (!t.StartsWith("#", StringComparison.Ordinal)) {
                        int eq = t.IndexOf('=');
                        if (eq > 0) {
                            int keep;
                            if (lastAt.TryGetValue(cur + "\u0001" + t.Substring(0, eq).TrimEnd(), out keep) && keep != i)
                                continue;   //同名键：不是最后一次出现 -> 丢（生效值在最后一条）
                        }
                    }
                    outp.Add(lines[i]);
                }
                string res = string.Join("\n", outp.ToArray());
                return res == text ? text : res;
            } catch (Exception __ex) { SR.Guard.Log("cfg 重复键清理", __ex); return text; }
        }
    }
}
