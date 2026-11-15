// 补丁目标审计：扫本程序集所有 [HarmonyPatch]，校验目标类型/方法是否存在。
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SR_UCH.Tweaks {
    public partial class SR {
        // 返回检查的目标总数；missing 为缺失的 "类型.方法" 列表。
        internal static int AuditPatches(out string[] missing) {
            List<string> miss = new List<string>();
            int total = 0;
            try {
                Assembly asm = typeof(SR).Assembly;
                Type[] types = asm.GetTypes();
                for (int t = 0; t < types.Length; t++) {
                    Type ty = types[t];
                    if (ty == null) continue;
                    MethodInfo[] ms = ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < ms.Length; i++) {
                        object[] attrs = ms[i].GetCustomAttributes(typeof(HarmonyPatch), true);
                        for (int a = 0; a < attrs.Length; a++) {
                            HarmonyPatch hp = attrs[a] as HarmonyPatch;
                            if (hp == null || hp.info == null) continue;
                            Type decl = hp.info.declaringType;
                            string name = hp.info.methodName;
                            if (decl == null) continue;
                            total++;
                            string label = decl.Name + (string.IsNullOrEmpty(name) ? "" : "." + name);
                            if (string.IsNullOrEmpty(name)) continue;   // 类级补丁：只校验类型存在
                            bool found = false;
                            try {
                                MethodInfo[] targets = decl.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                                for (int k = 0; k < targets.Length; k++) {
                                    if (targets[k] != null && targets[k].Name == name) { found = true; break; }
                                }
                            } catch (Exception) { found = false; }
                            if (!found) miss.Add(label);
                        }
                    }
                }
            } catch (Exception) { /* 审计本身失败不抛：由自检报"审计异常" */ }
            missing = miss.ToArray();
            return total;
        }

        // 分区：补丁注册（统一入口 + 漏注册检测）

        private static readonly List<Type> _patched = new List<Type>();
        internal static int PatchedCount { get { return _patched.Count; } }

        // 统一注册：CreateAndPatchAll 按类型扫描，失败只记日志（与各功能原行为一致）。
        internal static void RegisterPatchType(Type patchType) {
            if (patchType == null) return;
            if (_patched.Contains(patchType)) return;   // 重复注册会重复打补丁
            try {
                Harmony.CreateAndPatchAll(patchType);
                _patched.Add(patchType);
            } catch (Exception __rp) {
                LogWarn("[补丁] 注册失败 " + patchType.Name + "：" + (__rp != null ? __rp.Message : "?"));
            }
        }

        // 自检：声明了 [HarmonyPatch] 却从未 RegisterPatch 的类型 —— 这类补丁会静默失效。
        internal static string[] FindUnregisteredPatchTypes() {
            List<string> bad = new List<string>();
            try {
                Assembly asm = typeof(SR).Assembly;
                Type[] types = asm.GetTypes();
                for (int i = 0; i < types.Length; i++) {
                    Type ty = types[i];
                    if (ty == null || _patched.Contains(ty)) continue;
                    // 只看"自己声明了补丁方法"的类型（类级 [HarmonyPatch] 也在其中）。
                    bool declares = false;
                    try { declares = ty.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0; } catch (Exception) { declares = false; }
                    if (!declares) {
                        MethodInfo[] ms;
                        try { ms = ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                        catch (Exception) { continue; }
                        for (int k = 0; k < ms.Length; k++) {
                            try { if (ms[k].GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0) { declares = true; break; } }
                            catch (Exception) { /* 单个方法读不到就跳过 */ }
                        }
                    }
                    if (declares) bad.Add(ty.Name);
                }
            } catch (Exception) { /* 扫描失败不阻断自检 */ }
            return bad.ToArray();
        }
    }
}
