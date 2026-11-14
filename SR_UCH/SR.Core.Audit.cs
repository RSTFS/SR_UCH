// 框架 · 启动期自检：反射成员解析/自检、Harmony 补丁目标自检、孤儿配置键清理、迁移规则审计。
// 从 SR.Core.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {
        // 功能文件在 SelfReg 里声明（key = "类型名.成员名"）：SR.RefOverride("QuickSaver.levelPortalXml", "重载关卡")
        public static void RefOverride(string cfgKey, string feature) {
            if (string.IsNullOrEmpty(cfgKey)) return;
            // 同一个游戏成员被多个功能反射时功能名追加而不是覆盖（否则自检只报最后登记的那个功能）。
            string old;
            if (_refFeature.TryGetValue(cfgKey, out old)) {
                if (!string.IsNullOrEmpty(feature) && (old == null || old.IndexOf(feature, StringComparison.Ordinal) < 0))
                    _refFeature[cfgKey] = string.IsNullOrEmpty(old) ? feature : (old + " / " + feature);
                return;
            }
            _refFeature[cfgKey] = feature;
        }

        private static string RefKey(Type owner, string name) {
            return (owner != null ? owner.FullName : "?") + "." + name;
        }

        private static string RefKey(Type owner, string name, Type[] parameters) {
            string k = RefKey(owner, name) + "(";
            if (parameters != null) {
                for (int i = 0; i < parameters.Length; i++) {
                    if (i > 0) k += ",";
                    k += parameters[i] != null ? parameters[i].Name : "?";
                }
            }
            return k + ")";
        }

        // 失效告警（每个成员只报一次，避免每帧路径刷屏）
        private static void RefWarn(Type owner, string name, string feature) {
            string key = owner != null ? owner.Name + "." + name : name;
            if (_refWarned.Contains(key)) return;
            _refWarned.Add(key);
            string feat = feature;
            if (string.IsNullOrEmpty(feat)) {
                string full = RefKey(owner, name);
                _refFeature.TryGetValue(full, out feat);
                if (string.IsNullOrEmpty(feat)) _refFeature.TryGetValue(key, out feat);
            }
            try {
                AuditWarn("[SR.Ref] 反射失效: " + key + " -> " + (feat ?? "未登记")
                    + " 已禁用（游戏可能已更新了私有成员名；该功能本次会话内不再重试反射）");
            } catch (Exception __ex) { Guard.Log("RefWarn", __ex); }
        }

        public static FieldInfo RefField(Type owner, string name, string feature) {
            string key = RefKey(owner, name);
            FieldInfo cached;
            if (_refFieldCache.TryGetValue(key, out cached)) return cached;   // 含"查过且没有"
            try { cached = AccessTools.Field(owner, name); } catch { cached = null; }
            _refFieldCache[key] = cached;
            if (cached == null) RefWarn(owner, name, feature);
            return cached;
        }

        public static MethodInfo RefMethod(Type owner, string name, string feature) {
            string key = RefKey(owner, name);
            MethodInfo cached;
            if (_refMethodCache.TryGetValue(key, out cached)) return cached;
            try { cached = AccessTools.Method(owner, name); } catch { cached = null; }
            _refMethodCache[key] = cached;
            if (cached == null) RefWarn(owner, name, feature);
            return cached;
        }

        // 带参数列表的重载（如 GetSpawnPosition(float)、resetPlayerCharacter(Character, bool)）
        public static MethodInfo RefMethod(Type owner, string name, Type[] parameters, string feature) {
            string key = RefKey(owner, name, parameters);
            MethodInfo cached;
            if (_refMethodCache.TryGetValue(key, out cached)) return cached;
            try { cached = AccessTools.Method(owner, name, parameters); } catch { cached = null; }
            _refMethodCache[key] = cached;
            if (cached == null) RefWarn(owner, name, feature);
            return cached;
        }

        // 按类型名找游戏类型（"LevelSelectController" 这类简单名）：先查 Assembly-CSharp，查不到再遍历已加载程序集。
        // 必须写 global::Level：功能文件里也定义了 Level，否则拿到的是 SR 自己的程序集。
        private static Type RefType(string simpleName) {
            try {
                Type t = typeof(global::Level).Assembly.GetType(simpleName, false);
                if (t != null) return t;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                    try { t = a.GetType(simpleName, false); if (t != null) return t; } catch (Exception __ex) { Guard.Log("RefType", __ex); }
                }
            } catch (Exception __ex) { Guard.Log("RefType", __ex); }
            return null;
        }

        // 存在性判定（自检专用，纯反射）：只看类型 + 基类里有没有这个名字，不关心重载歧义，
        // 也不会像 AccessTools.Field 那样对方法名打 "Could not find field" 警告。
        private static bool RefExists(Type owner, string member) {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            // 支持嵌套成员路径（a.b.c）：按第一个点切出类型名，其余整段逐段下钻（嵌套类型 / 字段类型）。
            if (member != null && member.IndexOf('.') >= 0) return RefExistsPath(owner, member);
            try {
                for (Type t = owner; t != null; t = t.BaseType) {
                    if (t.GetField(member, F) != null) return true;
                    // 属性也算"存在"：UIGraphic.Visible 是属性，只查字段 / 方法会误报失效。
                    // GetProperty 在同名属性隐藏 / 歧义时会抛 AmbiguousMatchException -> 包住。
                    try { if (t.GetProperty(member, F) != null) return true; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    MethodInfo[] methods = t.GetMethods(F);
                    for (int i = 0; i < methods.Length; i++) if (methods[i].Name == member) return true;
                    // 嵌套类型也算成员：EX 反射的 ScoreKeeper.scoreInfo 是 private struct，误报会把真失效淹掉。
                    // GetNestedType 不接受 DeclaredOnly（对嵌套类型无意义），只按可见性查。
                    if (t.GetNestedType(member, BindingFlags.Public | BindingFlags.NonPublic) != null) return true;
                }
            } catch (Exception __ex) { Guard.Log("RefExists", __ex); }
            return false;
        }

        // 嵌套成员路径解析（a.b.c）：逐段下钻，最后一段按字段 / 方法 / 嵌套类型判存在性。
        private static bool RefExistsPath(Type owner, string member) {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            try {
                string[] parts = member.Split('.');
                Type cur = owner;
                for (int i = 0; i < parts.Length && cur != null; i++) {
                    bool hit = false;
                    Type nested = null;
                    for (Type t = cur; t != null && !hit; t = t.BaseType) {
                        if (t.GetField(parts[i], F) != null || t.GetProperty(parts[i], F) != null) { hit = true; break; }
                        MethodInfo[] ms = t.GetMethods(F);
                        for (int k = 0; k < ms.Length; k++) if (ms[k].Name == parts[i]) { hit = true; break; }
                        if (hit) break;
                        nested = t.GetNestedType(parts[i], BindingFlags.Public | BindingFlags.NonPublic);
                        if (nested != null) hit = true;
                    }
                    if (!hit) return false;
                    if (i == parts.Length - 1) return true;          // 最后一段存在即通过
                    if (nested != null) { cur = nested; continue; }  // 嵌套类型：直接下钻
                    FieldInfo fi = null;
                    for (Type t = cur; t != null && fi == null; t = t.BaseType) fi = t.GetField(parts[i], F);
                    if (fi == null) return false;
                    cur = fi.FieldType;                              // 字段：取它的类型继续下钻
                }
            } catch (Exception __ex) { Guard.Log("RefExistsPath", __ex); }
            return false;
        }

        public static void RefAudit() {
            if (_refAudited) return;
            _refAudited = true;
            // 与补丁侧自检一起做（此时各功能已 Initialize、补丁已打）
            try { PatchAudit(); } catch (Exception __ex) { Guard.Log("SR.Patch 启动自检", __ex); }
            try {
                if (_refFeature.Count == 0) return;
                int ok = 0;
                List<string> failed = new List<string>();
                foreach (KeyValuePair<string, string> kv in _refFeature) {
                    int dot = kv.Key.IndexOf('.');
                    string ownerName = dot > 0 ? kv.Key.Substring(0, dot) : kv.Key;
                    string member = dot > 0 ? kv.Key.Substring(dot + 1) : "";
                    Type owner = member.Length > 0 ? RefType(ownerName) : null;
                    bool found = false;
                    if (owner != null) {
                        found = RefExists(owner, member);
                    }
                    if (found) ok++;
                    else { failed.Add(kv.Key + "（" + kv.Value + "）");
                           _auditIssues.Add(new AuditIssue { Kind = "反射", Target = kv.Key, Scope = kv.Value }); }
                }
                AuditRefTotal = _refFeature.Count; AuditRefOk = ok;

                for (int i = 0; i < failed.Count; i++) {
                    AuditWarn("[SR.Ref]   ✗ " + failed[i]
                        + " -> 该功能已禁用。游戏更新导致改名时，请等新版模组适配（旧的在 cfg [Reflection] 段填新成员名能力已删除，照着做是无效的）。");
                }
            } catch (Exception __ex) { Guard.Log("SR.Ref 启动自检", __ex); }
        }

        // 按名绑定的目标是否仍然存在（decl / name 取不到时当作"没问题"，不误报）
        private static bool PatchTargetExists(Type decl, string name, Type[] args) {
            if (decl == null || string.IsNullOrEmpty(name)) return true;
            try {
                if (args != null && args.Length > 0) return AccessTools.Method(decl, name, args) != null;
                // 没给参数类型：按名字找（容忍重载）
                // 没给参数类型时只能按名字找：若同名方法有多个重载，"存在"这个结论就不可靠
                // （游戏新增同名重载会让自检形同虚设）。这里数一下重载数，多于 1 就记一条告警（audit F-7）。
                MethodInfo[] ms = decl.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Static | BindingFlags.Instance);
                int hits = 0;
                for (int i = 0; i < ms.Length; i++) if (ms[i].Name == name) hits++;
                if (hits > 1) {
                    // 重载歧义不能当成"存在"：游戏新增同名重载后按名字匹配无法确认目标，
                    // 报 true 会让自检显示健康、而对应行为其实早已失效（audit 01-P0-1）。
                    AuditWarn("[补丁自检] " + decl.Name + "." + name + " 有 " + hits + " 个同名重载，按名字匹配无法确认补丁目标（按失效计）");
                    return false;
                }
                return hits == 1;
            } catch { return false; }
        }

        // 从一条 [HarmonyPatch] 特性取类型 / 方法名 / 参数表（类级与方法级合并成一份）
        private static void ReadPatchInfo(object attr, ref Type decl, ref string name, ref Type[] args) {
            try {
                HarmonyPatch hp = attr as HarmonyPatch;
                if (hp == null) return;
                HarmonyMethod info = hp.info;
                if (info == null) return;
                if (info.declaringType != null) decl = info.declaringType;
                if (!string.IsNullOrEmpty(info.methodName)) name = info.methodName;
                if (info.argumentTypes != null && info.argumentTypes.Length > 0) args = info.argumentTypes;
            } catch (Exception __ex) { Guard.Log("ReadPatchInfo", __ex); }
        }

        public static void PatchAudit() {
            if (_patchAudited) return;
            _patchAudited = true;
            int total = 0, ok = 0;
            List<string> failed = new List<string>();
            try {
                Assembly self = Assembly.GetExecutingAssembly();
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < asms.Length; a++) {
                    Assembly asm = asms[a];
                    try {
                        if (asm == null || asm.IsDynamic) continue;
                        if (asm != self) {
                            // 只审计 SR 自己与引用了 SR_UCH 的功能 DLL；其它插件由它们自己的日志负责（不按具体模块名特判）
                            bool managed = false;
                            try {
                                AssemblyName an = asm.GetName();
                                foreach (AssemblyName rn in asm.GetReferencedAssemblies()) { if (rn.Name == "SR_UCH") { managed = true; break; } }
                            } catch (Exception __ex) { Guard.Log("PatchAudit", __ex); }
                            if (!managed) continue;
                        }
                        Type[] types;
                        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException __te) { types = __te.Types; }
                        if (types == null) continue;
                        for (int ti = 0; ti < types.Length; ti++) {
                            Type t = types[ti];
                            if (t == null) continue;
                            try {
                                object[] clsAttrs;
                                try { clsAttrs = t.GetCustomAttributes(typeof(HarmonyPatch), true); } catch { continue; }
                                if (clsAttrs == null) clsAttrs = new object[0];
                                MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                               BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                                for (int mi = 0; mi < ms.Length; mi++) {
                                    object[] mAttrs;
                                    try { mAttrs = ms[mi].GetCustomAttributes(typeof(HarmonyPatch), true); } catch { mAttrs = new object[0]; }
                                    if (clsAttrs.Length == 0 && mAttrs.Length == 0) continue;
                                    Type decl = null; string name = null; Type[] args = null;
                                    for (int k = 0; k < clsAttrs.Length; k++) ReadPatchInfo(clsAttrs[k], ref decl, ref name, ref args);
                                    for (int k = 0; k < mAttrs.Length; k++) ReadPatchInfo(mAttrs[k], ref decl, ref name, ref args);
                                    if (decl == null || string.IsNullOrEmpty(name)) continue;   // 无法按名验证：跳过，不误报
                                    total++;
                                    if (PatchTargetExists(decl, name, args)) ok++;
                                    else { failed.Add(decl.Name + "." + name + "  <- " + t.Name);
                                           _auditIssues.Add(new AuditIssue { Kind = "补丁", Target = decl.Name + "." + name, Scope = t.Name }); }
                                }
                            } catch (Exception __ex) { Guard.Log("PatchAudit", __ex); }
                        }
                    } catch (Exception __ex) { Guard.Log("PatchAudit", __ex); }
                }
            } catch (Exception __ex) { Guard.Log("SR.Patch 启动自检", __ex); }
            AuditPatchTotal = total; AuditPatchOk = ok;

            for (int i = 0; i < failed.Count; i++) AuditWarn("[SR.Patch]   ✗ " + failed[i]);
            if (failed.Count > 0) {
                AuditWarn("[SR.Patch] 以上补丁目标已不存在 -> 对应功能不会生效"
                    + "（表现为开关还在、行为没了）。游戏更新后请核对方法名/签名。");
            }
        }

        // 功能健康：自检结果登记，供界面（设置页自检块与首页提示）读取。
        // 自检 / 诊断用安全日志：ModLogger 在极早期可能还没就绪。 catch (Exception __ex) { Guard.Log("AuditLog", __ex); } }
        private static void AuditWarn(string msg) { try { if (MainPlugin.ModLogger != null) SR.LogWarn(msg); } catch (Exception __ex) { Guard.Log("AuditWarn", __ex); } }

        // 一次性启动自检（补丁目标 + 反射成员），幂等；由 MainPlugin.Awake 末尾调用，不开面板也会跑。
        public static void RunStartupAudit() {
            if (_startupAudited) return;
            _startupAudited = true;
            try { PatchAudit(); } catch (Exception __ex) { Guard.Log("补丁目标自检", __ex); }
            try { RefAudit(); } catch (Exception __ex) { Guard.Log("反射成员自检", __ex); }
            // 孤儿键审计不在这里跑：此时组合键 / Hotkeys 条目尚未惰性 Bind，会大批误报（见 EnsureScanned）。
        }

        // 从配置文件里真正清掉孤儿键（原子替换并留 .bak，会写盘）；返回清掉的条数（0 = 无可清或拿不到表）。
        public static int CleanOrphanKeys() {
            try {
                ConfigFile cfg = _internalConfig;
                if (cfg == null) return 0;
                System.Collections.IDictionary dict = ReadOrphanDict();
                if (dict == null) return 0;
                List<string> pairs = new List<string>();
                foreach (System.Collections.DictionaryEntry de in dict) {
                    ConfigDefinition def = de.Key is ConfigDefinition ? (ConfigDefinition)de.Key : default(ConfigDefinition);
                    if (string.IsNullOrEmpty(def.Section) || string.IsNullOrEmpty(def.Key)) continue;
                    pairs.Add(def.Section + ConfigMigration.AuditSep + def.Key);
                }
                if (pairs.Count == 0) return 0;
                string path = cfg.ConfigFilePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return 0;
                string text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                // 内存表同步清理：否则文件已删、界面仍按旧表显示"还有孤儿"
                try {
                    for (int i = 0; i < pairs.Count; i++) {
                        string[] __kv = pairs[i].Split(new[] { ConfigMigration.AuditSep }, StringSplitOptions.None);
                        if (__kv.Length == 2) {
                            try { dict.Remove(new ConfigDefinition(__kv[0], __kv[1])); } catch (Exception __rm) { Guard.Log("孤儿键/移出内存表", __rm); }
                        }
                    }
                } catch (Exception __cm) { Guard.Log("孤儿键/内存表清理", __cm); }
                OrphanAudit();   // 重新统计，界面与清理结果一致
                string newText = ConfigMigration.RemovePairs(text, pairs);
                if (newText == text) return 0;
                if (!ConfigMigration.WriteCfgAtomic(path, newText, "清理孤儿键")) return 0;
                cfg.Reload();
                ClearEntryCache();
                OrphanKeyCount = 0;
                OrphanPendingCount = 0;
            AuditOrphan = 0;
                LogInfo("[SR.Cfg] 已清理 " + pairs.Count + " 个孤儿键（原文件已留成 .bak）");
                return pairs.Count;
            } catch (Exception __ex) { Guard.Log("清理孤儿键", __ex); return 0; }
        }

        private static void OrphanAudit() {
            OrphanKeyCount = 0;
            OrphanPendingCount = 0;
            try {
                if (_internalConfig == null) return;
                System.Collections.IDictionary dict = ReadOrphanDict();
                if (dict == null) return;
                System.Text.StringBuilder real = new System.Text.StringBuilder();
                foreach (System.Collections.DictionaryEntry de in dict) {
                    ConfigDefinition def = de.Key is ConfigDefinition ? (ConfigDefinition)de.Key : default(ConfigDefinition);
                    // 页签记忆键（[UI] Page XXX）是动态键名：界面按"Page " + 栏目名读取，
                    // 静态扫描看不到 Bind → 会被误判成孤儿。以前提示"8 个孤儿"、点清理却说没有，
                    // 就是因为显示与清理用的不是同一份名单。
                    if (def.Section == "UI" && def.Key != null && def.Key.StartsWith("Page ", StringComparison.Ordinal)) {
                        OrphanPendingCount++;
                        continue;
                    }
                    // 快捷键条目是按需生成的（新建快捷键时才 Bind），审计跑在生成之前 → 它们只是"尚未生成"，不是孤儿键。
                    if (def.Section == "Hotkeys"
                        || (def.Section == "Settings" && def.Key != null && def.Key.StartsWith("组合键", StringComparison.Ordinal))) {
                        OrphanPendingCount++;
                        continue;
                    }
                    OrphanKeyCount++;
                    AuditOrphan = OrphanKeyCount;
                    if (real.Length > 0) real.Append(" | ");
                    real.Append("[").Append(def.Section).Append("] ").Append(def.Key);
                }
                // 只打一行：真实孤儿键逐条列出（通常极少），按需生成的快捷键条目只报数量。
                if (OrphanKeyCount > 0)
                    AuditWarn("[SR.Cfg] 配置孤儿键 " + OrphanKeyCount + " 个（配置里有、但没有代码再读；设置页可一键清理）: " + real.ToString());


            } catch (Exception __ex) { Guard.Log("孤儿键扫描", __ex); }
        }

        // 迁移规则冲突自检：把当前真正被绑定的键喂给 ConfigMigration 的纯 BCL 判定（需在全部 Bind 之后）。
        private static void MigrationRuleAudit() {
            try {
                List<string> live = new List<string>();
                if (_internalConfig != null)
                    foreach (ConfigEntryBase e in AllEntries(_internalConfig))
                        live.Add(e.Definition.Section + ConfigMigration.AuditSep + e.Definition.Key);
                ConfigMigration.AuditAgainst(live);
            } catch (Exception __ex) { Guard.Log("迁移规则冲突自检", __ex); }
        }

        // 兜底栏目审计：列出没有登记归属、也没被 NavHide 的配置段（它们会以兜底页出现在侧栏）
        private static void SectionFallbackAudit() {
            FallbackSectionCount = 0;
            try {
                if (_internalConfig == null) return;
                List<string> seen = new List<string>();
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    string sec = e.Definition.Section;
                    if (string.IsNullOrEmpty(sec)) continue;
                    if (_navHidden.Contains(sec) || _navRank.ContainsKey(sec)) continue;   // 已声明归属或已隐藏
                    // 已在底栏注册按钮的段由底栏承载，不会以兜底页出现，不算未登记归属。
                    bool footerSec = false;
                    for (int k = 0; k < SRApi.FooterButtons.Count; k++) {
                        SRApi.FooterButton fb = SRApi.FooterButtons[k];
                        if (fb != null && fb.Id == sec) { footerSec = true; break; }
                    }
                    if (footerSec) continue;
                    if (seen.Contains(sec)) continue;
                    seen.Add(sec);
                    FallbackSectionCount++;
                    AuditWarn("[SR.Nav] 没有登记归属的配置段（会以兜底页出现在侧栏）: [" + sec + "]；"
                        + "若这是已删功能请用 SR.Migration.Remove(\"" + sec + "\", 键…) 清键并 NavHide(\"" + sec + "\")，外部模块自己的段可忽略");
                }
            } catch (Exception __ex) { Guard.Log("兜底栏目审计", __ex); }
        }
}
}
