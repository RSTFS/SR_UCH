// 补丁目标审计：游戏更新改名/改签名时，Harmony 只在日志报一次，功能会静默失效。
// 这里扫本程序集里所有 [HarmonyPatch] 特性，逐个校验目标类型/方法是否存在，自检页统一报告。
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SR_UCH.Tweaks {
    public partial class SR {
        // 返回检查的补丁目标总数；missing 为缺失的 "类型.方法" 列表。
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
    }
}
