// 契约层分片；硬约定：不得引用 SR 其它文件，须能单独编译成程序集。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {   // 门面主体；其余成员见同目录 SRApi.*.cs（同一个 partial 类）

        // 统一存储：原子写（临时文件 + 替换）、UTF-8 无 BOM、目录自动创建。
        public static class Storage {
            private static string _root = "";
            public static void SetRoot(string root) { try { _root = root ?? ""; } catch (Exception) { _root = ""; } }
            public static string RootOf { get { return _root; } }
            // false 时所有写都会失败，功能应视为"存储不可用"，不要当作静默成功。
            public static bool Ready { get { return !string.IsNullOrEmpty(_root); } }
            private static string SafeName(string s) {
                try {
                    if (string.IsNullOrEmpty(s)) return "_";
                    System.Text.StringBuilder b = new System.Text.StringBuilder(s.Length);
                    char[] bad = System.IO.Path.GetInvalidFileNameChars();
                    for (int i = 0; i < s.Length; i++) {
                        char c = s[i]; bool ok = true;
                        for (int k = 0; k < bad.Length; k++) if (bad[k] == c) { ok = false; break; }
                        b.Append(ok ? c : '_');
                    }
                    return b.ToString();
                } catch { return "_"; }
            }
            public static string DirOf(string feature) {
                try {
                    if (string.IsNullOrEmpty(_root)) return "";
                    string d = System.IO.Path.Combine(_root, SafeName(feature));
                    if (!System.IO.Directory.Exists(d)) System.IO.Directory.CreateDirectory(d);
                    return d;
                } catch (Exception) { return ""; }
            }
            public static string PathOf(string feature, string file) {
                try {
                    string d = DirOf(feature);
                    if (string.IsNullOrEmpty(d)) return null;
                    return System.IO.Path.Combine(d, SafeName(file));
                } catch (Exception) { return null; }
            }
            public static bool WriteAtomic(string feature, string file, string text) {
                try {
                    string path = PathOf(feature, file);
                    if (string.IsNullOrEmpty(path)) return false;
                    string tmp = path + ".tmp";
                    System.IO.File.WriteAllText(tmp, text ?? "", new System.Text.UTF8Encoding(false));
                    if (System.IO.File.Exists(path)) {
                        try { System.IO.File.Replace(tmp, path, null); return true; }
                        catch { System.IO.File.Delete(path); }
                    }
                    System.IO.File.Move(tmp, path);
                    return true;
                } catch (Exception) { return false; }
            }
            public static string Read(string feature, string file) {
                try {
                    string path = PathOf(feature, file);
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                    return System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                } catch { return null; }
            }
            public static bool WriteAtomicPath(string fullPath, string text) {
                try {
                    if (string.IsNullOrEmpty(fullPath)) return false;
                    string tmp = fullPath + ".tmp";
                    System.IO.File.WriteAllText(tmp, text ?? "", new System.Text.UTF8Encoding(false));
                    if (System.IO.File.Exists(fullPath)) {
                        try { System.IO.File.Replace(tmp, fullPath, null); return true; }
                        catch (Exception) { System.IO.File.Copy(tmp, fullPath, true); }
                    } else {
                        System.IO.File.Move(tmp, fullPath);
                        return true;
                    }
                    System.IO.File.Delete(tmp);
                    return true;
                } catch (Exception) { return false; }
            }
            public static string ReadPath(string fullPath) {
                try {
                    if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return null;
                    return System.IO.File.ReadAllText(fullPath, System.Text.Encoding.UTF8);
                } catch (Exception) { return null; }
            }
            public static bool Exists(string feature, string file) {
                try { string p = PathOf(feature, file); return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p); }
                catch { return false; }
            }
            public static bool BackupPath(string fullPath) {
                try {
                    if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return false;
                    System.IO.File.Copy(fullPath, fullPath + ".bak", true);
                    return true;
                } catch (Exception) { return false; }
            }
            public static bool Delete(string feature, string file) {
                try {
                    string p = PathOf(feature, file);
                    if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) return false;
                    System.IO.File.Delete(p);
                    return true;
                } catch { return false; }
            }
        }
    }
}
