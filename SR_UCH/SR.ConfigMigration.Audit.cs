// 注意事项：保留公共签名供启动流水线与功能注册调用，避免调用方改动。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SR_UCH.Tweaks {
    public static partial class ConfigMigration {
        public static void AuditAgainst(IEnumerable<string> livePairs) { }
        public static bool HasPending { get { return false; } }
        public static void Section(string oldName, string newName) { }
        public static void Key(string oldName, string newName) { }
        public static void Move(string fromSec, string toSec, string key) { }
        public static void Remove(string section, params string[] keys) { }
        public static bool RunPending(string cfgPath) { return false; }

        // 注意事项：schema 标记放在独立的 .schema 文件，避免 BepInEx 重写 cfg 时丢掉版本行。
        public const int SchemaVersion = 3;

        public static bool EnsureSchema(string cfgPath) {
            try {
                if (string.IsNullOrEmpty(cfgPath)) return false;
                string stamp = cfgPath + ".schema";
                int cur = -1;
                if (File.Exists(stamp)) {
                    int v;
                    if (int.TryParse(File.ReadAllText(stamp).Trim(), out v)) cur = v;
                }
                if (cur == SchemaVersion) return false;
                if (File.Exists(cfgPath)) {
                    string backup = cfgPath + ".old." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Copy(cfgPath, backup, true);
                    File.WriteAllText(cfgPath, "", new UTF8Encoding(false));
                    SR.LogWarn("[配置] schema 不匹配，旧配置已归档: " + backup);
                }
                File.WriteAllText(stamp, SchemaVersion.ToString(), new UTF8Encoding(false));
                return true;
            } catch (Exception __ex) { SR.Guard.Log("配置 schema 版本门", __ex); return false; }
        }

        public static bool Migrate(string cfgPath) { return EnsureSchema(cfgPath); }

        public static bool WriteCfgAtomic(string cfgPath, string text, string logTag) {
            try {
                string tmpPath = cfgPath + ".tmp";
                File.WriteAllText(tmpPath, text, new UTF8Encoding(false));
                try { File.Replace(tmpPath, cfgPath, cfgPath + ".bak"); }
                catch (Exception __rep) {
                    try { File.Copy(cfgPath, cfgPath + ".bak", true); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    File.WriteAllText(cfgPath, text, new UTF8Encoding(false));
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    SR.LogWarn("[" + logTag + "] 原子替换失败，已退回备份加覆盖写: " + __rep.Message);
                }
                return true;
            } catch (Exception __ex) { SR.Guard.Log(logTag + "：写配置", __ex); return false; }
        }

        public static string RemovePairs(string text, IEnumerable<string> pairs) {
            try {
                if (pairs == null) return text;
                string res = text;
                int n = 0;
                foreach (string p in pairs) {
                    if (string.IsNullOrEmpty(p)) continue;
                    int i = p.IndexOf(AuditSep, StringComparison.Ordinal);
                    if (i <= 0) continue;
                    string sec = p.Substring(0, i);
                    string key = p.Substring(i + AuditSep.Length);
                    if (string.IsNullOrEmpty(sec) || string.IsNullOrEmpty(key)) continue;
                    res = RemoveKeys(res, sec, new[] { key });
                    n++;
                }
                return n > 0 ? DedupeSectionKeys(res) : res;
            } catch (Exception __ex) { SR.Guard.Log("批量清理配置键", __ex); return text; }
        }
    }
}
