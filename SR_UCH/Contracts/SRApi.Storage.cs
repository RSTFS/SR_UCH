// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {   // 门面主体；其余成员见同目录 SRApi.*.cs（同一个 partial 类）

        // 统一存储（契约 v7）：功能的文件数据走这里——原子写（临时文件 + 替换）、UTF-8 无 BOM、目录自动创建。
        public static class Storage {
            private static string _root = "";
            public static void SetRoot(string root) { try { _root = root ?? ""; } catch (Exception) { _root = ""; } }
            public static string RootOf { get { return _root; } }
            // 核心是否已注入根目录：false 时所有写都会失败，功能应视为"存储不可用"而不是静默成功。
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
            // 绝对路径版本：功能已有自己的目录布局（如布局预设）时直接复用同一套原子写。
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
            // 原子写前备份：把现有文件复制为 <path>.bak（保存流程用；读坏时可手工/以后自动回滚）。
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
