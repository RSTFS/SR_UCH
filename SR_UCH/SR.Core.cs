// 框架本体：状态与初始化、各类 Register 扩展点、Provide/Require 槽位、每帧钩子派发、
// 对局开始时重置界面、日志包装 Guard。不认识任何具体功能（功能自己通过 SR.Register 挂上来）。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
[SrFeature("sr.core", "SR 核心", "1.0", Rank = 0)]
public partial class SR : ITweak {

// 分区：Core（核心状态 / 初始化 / 插件扫描）
// 约定：静态字段集中在此；新功能实现 ITweak 即被自动发现，新分区需在 sr_uch.rsp 注册。
// 分区分界（item 10）：SR 由 17 个 partial 拼成一个静态类，编译器不会拦"跨分区写别人私有字段"。
//   约定：自己分区的 private static 字段只在本文件用；确实要跨分区读写就改成 internal + 访问器。
//   机器检查：build\guards-text.ps1 的 Check-RegionFields 会拦下"新增的跨分区私有字段"，
//   历史遗留 53 个登记在 build\region-access.txt（棘轮：只减不增）。目标是逐步收敛到 0，
//   必要时再把 Gate / Ctl / Nav 提成独立静态类。

        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _tickProf;   // 帧钩子剖析开关
        private static ConfigEntry<bool> _blockInputEntry;
        private static ConfigEntry<float> _uiScaleEntry;
        private static ConfigEntry<string> _disabledPluginsEntry;
        private static ConfigEntry<bool> _allEnabledEntry;
        // 对外（外部 DLL）暴露的注册接口版本：外部模块初始化时比对自己的需求版本，不匹配就提示 / 降级。
        // 用 static readonly 而非 const：const 会在编译外部模块时被内联，握手只能靠运行时读取生效。
        public static readonly int ApiVersion = 3;
        // Ui：窗口宽高 / 位置 / 侧栏宽 / 整体缩放的唯一真值（读入与拖动 / 缩放夹取都读它）。
        internal static class Ui {
            public const int WinWMin = 400, WinWMax = 1600;
            public const int WinHMin = 300, WinHMax = 1000;
            public const int PosMin = 0, PosMax = 2000;
            public const int SidebarWMin = 0, SidebarWMax = 320;
            public const float ScaleMin = 1f, ScaleMax = 1.8f;
        }
        // Keys：设置段与其中各键名的唯一真值。
        internal static class Keys {
            public const string Settings = "Settings";
            public const string OpenKey = "Open Key";
            public const string BlockInput = "Block Input";
            public const string UiScale = "UI Scale";
            public const string Language = "Language";
            public const string SidebarWidth = "Sidebar Width";
            public const string SidebarOrder = "Sidebar Order";
            public const string AllEnabled = "All Enabled";
            public const string DisabledPlugins = "Disabled Plugins";
            public const string WinWidth = "Window Width";
            public const string WinHeight = "Window Height";
            public const string WinX = "Window X";
            public const string WinY = "Window Y";
            public const string ComboPrefix = "组合键 ";
        }

        // 反射辅助：失效告警 + 结果缓存（连"查过但没有"也缓存）。
        // 功能在 SelfReg 里用 SR.RefOverride 声明"成员 -> 支撑什么功能"，调用处用 SR.RefField / RefMethod 取。
        private static readonly Dictionary<string, string> _refFeature = new Dictionary<string, string>();
        private static readonly HashSet<string> _refWarned = new HashSet<string>();
        private static readonly Dictionary<string, FieldInfo> _refFieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> _refMethodCache = new Dictionary<string, MethodInfo>();

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

        // 把各功能声明的反射成员逐个解析一遍，日志给出可用 N / 失效 M 总览（幂等，只跑一次）。
        private static bool _refAudited;
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

        // 补丁侧自检：反射遍历 SR 自己与引用了 SR_UCH 的功能 DLL，读每条 [HarmonyPatch] 特性
        // （类级与方法级合并），逐个验证按名绑定的目标方法是否还存在（无需改补丁写法）。
        private static bool _patchAudited;
        public static int PatchAuditBad;   // 失效的补丁目标数（供 UI 提示"某些功能不可用"）

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
            PatchAuditBad = failed.Count;

            for (int i = 0; i < failed.Count; i++) AuditWarn("[SR.Patch]   ✗ " + failed[i]);
            if (failed.Count > 0) {
                AuditWarn("[SR.Patch] 以上补丁目标已不存在 -> 对应功能不会生效"
                    + "（表现为开关还在、行为没了）。游戏更新后请核对方法名/签名。");
            }
        }
        // 功能健康：自检结果登记，供界面（设置页自检块与首页提示）读取。
        // 自检 / 诊断用安全日志：ModLogger 在极早期可能还没就绪。 catch (Exception __ex) { Guard.Log("AuditLog", __ex); } }
        private static void AuditWarn(string msg) { try { if (MainPlugin.ModLogger != null) SR.LogWarn(msg); } catch (Exception __ex) { Guard.Log("AuditWarn", __ex); } }

        public sealed class AuditIssue {
            public string Kind;    // "补丁" / "反射"
            public string Target;  // "Character.setupDeath" / "ChatDisplay.ChatHolder"
            public string Scope;   // 出问题的宿主（补丁=类名；反射=该成员支撑的功能描述）
        }
        private static readonly List<AuditIssue> _auditIssues = new List<AuditIssue>();
        public static IList<AuditIssue> AuditIssues { get { return _auditIssues; } }
        // 孤儿键数：BepInEx 把"文件里有但没有任何代码 Bind"的键留在 OrphanedEntries，Save 时原样写回。
        // 启动扫描后显示在设置页（可一键清理）。
        public static int OrphanKeyCount;
        // 供启动自检汇总行使用的审计计数
        public static int AuditOrphan;
        public static int AuditGateTotal;
        public static int AuditPerFrame;
        // 按需生成、尚未 Bind 的条目数（快捷键/组合键）：仅统计不告警。
        public static int OrphanPendingCount;
        // 没有登记归属、也没被 NavHide 的配置段数：它们会以兜底页出现在侧栏（含漏登记退役的已删功能段）。
        public static int FallbackSectionCount;
        public static int AuditIssueCount { get { return _auditIssues.Count; } }
        public static int AuditPatchTotal, AuditPatchOk, AuditRefTotal, AuditRefOk;
        private static bool _startupAudited;

        // 一次性启动自检（补丁目标 + 反射成员），幂等；由 MainPlugin.Awake 末尾调用，不开面板也会跑。
        public static void RunStartupAudit() {
            if (_startupAudited) return;
            _startupAudited = true;
            try { PatchAudit(); } catch (Exception __ex) { Guard.Log("补丁目标自检", __ex); }
            try { RefAudit(); } catch (Exception __ex) { Guard.Log("反射成员自检", __ex); }
            // 孤儿键审计不在这里跑：此时组合键 / Hotkeys 条目尚未惰性 Bind，会大批误报（见 EnsureScanned）。
        }

        // BepInEx 的 OrphanedEntries 不是编译期可见的公开成员，只能反射取（先属性后字段），
        // 拿不到就安静跳过（纯诊断功能，不影响启动，也不计入 AuditIssue 的失效列表）。
        private static System.Reflection.MemberInfo _orphanMember;
        private static bool _orphanMemberTried;
        // BepInEx 的 OrphanedEntries（反射取；拿不到返回 null），OrphanAudit 与 CleanOrphanKeys 共用
        private static System.Collections.IDictionary ReadOrphanDict() {
            ConfigFile cfg = _internalConfig;
            if (cfg == null) return null;
            if (!_orphanMemberTried) {
                _orphanMemberTried = true;
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                try { _orphanMember = typeof(ConfigFile).GetProperty("OrphanedEntries", BF); } catch { _orphanMember = null; }
                if (_orphanMember == null) { try { _orphanMember = typeof(ConfigFile).GetField("OrphanedEntries", BF); } catch { _orphanMember = null; } }
                if (_orphanMember == null) SR.LogWarn("[SR.Cfg] 读不到 BepInEx 的 OrphanedEntries（版本差异），跳过孤儿键自检");
            }
            if (_orphanMember == null) return null;
            try {
                object raw;
                System.Reflection.PropertyInfo pi = _orphanMember as System.Reflection.PropertyInfo;
                if (pi != null) raw = pi.GetValue(cfg, null);
                else raw = ((System.Reflection.FieldInfo)_orphanMember).GetValue(cfg);
                return raw as System.Collections.IDictionary;
            } catch (Exception __ex) { Guard.Log("读取孤儿键表", __ex); return null; }
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
                    if (seen.Contains(sec)) continue;
                    seen.Add(sec);
                    FallbackSectionCount++;
                    AuditWarn("[SR.Nav] 没有登记归属的配置段（会以兜底页出现在侧栏）: [" + sec + "]；"
                        + "若这是已删功能请用 SR.Migration.Retire(\"" + sec + "\") 登记退役，外部模块自己的段可忽略");
                }
            } catch (Exception __ex) { Guard.Log("兜底栏目审计", __ex); }
        }

        // 自检是否有问题（首页 / 设置页据此决定要不要显眼提示）
        public static bool AuditHasIssues { get { return _auditIssues.Count > 0; } }
        // 调试模式：开关状态与信息都在核心生成（关掉 EX 也保留设置），EX 只负责画复选框。

        // 调试信息：每行独立 try，任何一项取不到都只影响那一行。
        
        // 本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值不变；改动自动保存。
        public static bool AllEnabled = false;
        private static bool _appliedDisabled;
        // 组合键修饰：持久化格式 "Shift+Ctrl+Alt"（见 SR.KeyBinds 的 ParseComboMod / FormatComboMod）。
        [Flags]
        public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _keyMods = new Dictionary<ConfigEntryBase, ComboMod>();
        private static readonly Dictionary<ConfigEntryBase, ConfigEntry<string>> _keyModEntries = new Dictionary<ConfigEntryBase, ConfigEntry<string>>();
        private static bool _visible;
        private static float _uiAlpha; // 开合淡入淡出
        private static bool _scanned;
        private enum Mode { Internal, Settings, External }
        // internal = SR_UCH 栏目，settings = 独立设置页，external = 其它插件
        private static ConfigFile _internalConfig;
        // "扫描时没找到自己的 GUID"只告警一次（见 EnsureScanned）
        private static bool _warnedNoSelfGuid;
        // 配置落盘节流状态（SaveOnConfigSet 已关闭，见 EnsureScanned）
        private static bool _cfgDirty;
        private static float _cfgNextSaveAt;

        private static void OnAnySettingChanged(object s, SettingChangedEventArgs e) { _cfgDirty = true; }

        // 把"这个配置该落盘了"交给节流器：SR 自己的配置记 _cfgDirty（FlushConfig 每秒最多写一次），
        // 其它插件的配置走 _dirtyConfig（Tick 里松手即写）。必须 public：拆分模式下功能是独立程序集。
        public static void MarkConfigDirty(ConfigFile cfg) {
            if (cfg == null) return;
            if (ReferenceEquals(cfg, _internalConfig)) { _cfgDirty = true; return; }
            // 用集合而不是单槽：先改 A 插件、再改 B 插件时单槽会把 A 覆盖掉，
            // A 的改动永远不会落盘（audit 04-P0-1）。
            _dirtyConfigs.Add(cfg);
            _dirty = true;
        }

        // 改过的配置最多每秒落盘一次；关面板 / 退出前的显式 Save 仍即时落盘。
        public static void FlushConfig() {
            if (!_cfgDirty || _internalConfig == null) return;
            if (Time.unscaledTime < _cfgNextSaveAt) return;
            _cfgNextSaveAt = Time.unscaledTime + 1f;
            // 保存成功才清脏标记（失败时保持脏，下一秒重试），否则这批改动会被当成已落盘而丢失。
            bool saved = Guard.Try("配置定期落盘", () => { _internalConfig.Save(); return true; }, false);
            if (saved) _cfgDirty = false;
        }
        private static readonly List<string> _internalSections = new List<string>();
        // 全部栏目（含被隐藏的），给自定义栏目的表头当列用；_internalSections 只含可见的。
        internal static readonly List<string> _allSections = new List<string>();
        // 手动隐藏的栏目（与 NavHide 的 _navHidden 分开：那是永不显示，这个可随时还原）
        internal static readonly HashSet<string> _userHiddenSections = new HashSet<string>();
        // 侧栏栏目自注册表（各功能在自己的 SelfReg 里声明；见 SR.Nav / SR.NavHide）
        private static readonly List<string> _navOrder = new List<string>();                     // 注册先后
        private static readonly Dictionary<string, int> _navRank = new Dictionary<string, int>(); // 顺序值（越小越靠前）
        private static readonly HashSet<string> _navHidden = new HashSet<string>();              // 有 cfg 段但不单独成栏目
        private static string _selectedInternalSection = "";
        private static Mode _mode = Mode.Internal;
        private static readonly List<PluginEntry> _externalPlugins = new List<PluginEntry>();
        private static string _pluginKey = "";
        private static Vector2 _scroll;
        // 文本测量缓存（避免每帧对所有条目 CalcSize / CalcHeight）
        private static int _nameWKey;   // 文本哈希折叠键（见 EntryNameWidth）
        private static float _nameWCached;
        private static readonly Dictionary<string, List<float[]>> _heightCache = new Dictionary<string, List<float[]>>();   // 两级键（文本 -> [宽, 缩放, 高]），免每帧拼 key
        private static Vector2 _leftScroll;

        private static ConfigEntryBase _capturing; // 键位捕捉目标
        // 捕捉起始帧：右键进入捕捉的同一次 MouseDown 不能被"按下即取消捕捉"清掉。
        private static int _captureStartFrame = -1;
        private static int _rmbConsumedFrame = -1; // 当帧右键绑键：一帧只认一个按钮
        private static object _prevBoxed;
        // 快捷键录制态：收集所有按键及其顺序（见 SR.Window 的录制分支 / CommitRecording）
        private static readonly List<KeyCode> _recSeq = new List<KeyCode>();
        private static readonly List<KeyCode> _recHeld = new List<KeyCode>(); // 录制中仍按住的键（全部松开即完成录制）
        // 待落盘的外部插件配置集合（SR 自己的配置走 _cfgDirty + FlushConfig）
        private static readonly HashSet<ConfigFile> _dirtyConfigs = new HashSet<ConfigFile>();
        internal static HashSet<ConfigFile> DirtyConfigs { get { return _dirtyConfigs; } }
        private static readonly Dictionary<ConfigEntryBase, string> _editText = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, bool> _editOpen = new Dictionary<ConfigEntryBase, bool>();
        // 窗口几何
        private static int _winWidth = 720;
        private static int _winHeight = 520;
        private static float _winX = 30f;
        private static float _winY = 30f;
        // 窗口几何配置条目（Initialize 里建一次；SR.Window 拖动 / 关闭时写它们）
        private static ConfigEntry<int> _winWidthEntry, _winHeightEntry;
        private static ConfigEntry<float> _winXEntry, _winYEntry;
        private static bool _dragActive, _dragMoved, _resizing;
        private static Vector2 _dragOffset, _downPos, _resizeStart, _resizeStartSize;
        private static bool _dirty;
        // 缩放同时作用于布局尺寸与字号（不使用矩阵缩放）
        private static float _scaled = 1f;
        private static float Sc(float v) { return v * _scaled; }
        private static UnityEngine.EventSystems.EventSystem _gatedEventSystem;
        private static bool _stylesReady;
        private static GUIStyle _win, _title, _titleLabel, _titleMid, _label, _nameLabel, _labelClip, _labelClipCenter, _secHeaderCenter, _labelWrap, _chatLabel, _chatArea, _secHeader, _item, _selItem, _btn, _btnClip, _frame,
            _capture, _checkOn, _checkOff, _popup, _searchBox, _footer, _tooltip,
            _sliderTrack, _sliderFill, _sliderHandle, _sliderHandleHover, _sliderHandleActive;
        private static Font _font;
        private static Font _prevFont;
        private static Texture2D _gripTex;
        private static Texture2D _cursorTex;
        private static Texture2D _cursorResizeTex; // 悬停分隔线时的左右调整光标
        private static Texture2D _cursorMoveTex;   // 悬停可拖动单元格时的四向移动光标
        public static bool CursorResize;          // 本帧是否显示调整光标（Tick 复位）
        public static bool CursorMove;            // 本帧是否显示移动光标
        private static readonly List<GUIStyle> _styleList = new List<GUIStyle>();

        // 管理器窗口是否真正打开（打开时输入冻结逻辑见 SR.Input）
        public static bool UiOpen { get { return _visible; } }
        // 管理器打开时冻结游戏输入（是否有功能 UI 打开见 SR.AnyUiBlocked）
        public static bool BlockInput = true;

        // 8) 门控豁免登记：任何模块都可登记"要求放开某项限制"的取值委托；未登记 = 不豁免，
        // 多个模块同时登记时任一为真即豁免（核心不认识具体模块）。
        private static readonly List<Func<bool>> _modeOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _hostOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _pauseProviders = new List<Func<bool>>();

        // 豁免来源登记：谁在提权必须可见（设置页显示 + 首次登记写日志）。
        public static readonly List<string> OverrideSources = new List<string>();
        // 只允许白名单程序集登记豁免；其它来源一律拒绝并记录（防止任意插件静默提权）。
        private static readonly string[] OverrideAllowedAssemblies = new string[] { "" };   // "" = 核心自身调用
        // 找"真正发起登记的程序集"：跳过核心自身（含契约层 SRApi）的栈帧，
        // 这样经 SRApi 转发的外部调用才能识别为发起方，而不是误判成 SR_UCH。
        private static string CallerAssembly() {
            try {
                string mine = typeof(SR).Assembly.GetName().Name;
                var st = new System.Diagnostics.StackTrace();
                for (int i = 2; i < st.FrameCount; i++) {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    Type dt = m.DeclaringType;
                    if (dt == null) continue;
                    string an = dt.Assembly.GetName().Name;
                    if (string.Equals(an, mine, StringComparison.OrdinalIgnoreCase)) continue;
                    return an;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return "";
        }
        // 该程序集是否引用了 SR_UCH（= 用契约层编译出来的模块）。
        private static bool IsContractConsumer(string asmName) {
            try {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                    try {
                        if (a == null || a.IsDynamic) continue;
                        if (!string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (AssemblyName rn in a.GetReferencedAssemblies()) {
                            if (rn != null && string.Equals(rn.Name, typeof(SR).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        return false;   // 找到该程序集但没引用 SR_UCH -> 非契约调用方
                    } catch (Exception __a) { Guard.Log("豁免登记/检查引用", __a); }
                }
            } catch (Exception __ex) { Guard.Log("豁免登记/遍历程序集", __ex); }
            return false;
        }

        private static bool NoteOverrideSource(string kind) {
            try {
                string who = CallerAssembly();
                // 信任规则（不按具体模块名开白名单）：核心自身，以及**引用了 SR_UCH 的程序集**
                // 才允许登记 —— 后者就是用契约层编译出来的模块，是这些 API 的合法调用方；
                // 其它来源拒绝并写一条可见日志，避免任意插件静默提权。
                bool ok = string.IsNullOrEmpty(who) || IsContractConsumer(who);
                string line = kind + " <- " + (string.IsNullOrEmpty(who) ? "核心" : who)
                    + (ok ? "" : "（已拒绝：非契约调用方）");
                if (!OverrideSources.Contains(line)) OverrideSources.Add(line);
                if (!ok) {
                    try { LogWarn("[门控] 拒绝非契约调用方的豁免登记：" + line); }
                    catch (Exception __lw) { Guard.Log("豁免登记拒绝日志", __lw); }
                }
                return ok;
            } catch { return false; }
        }
        // 登记"无视模式限制"豁免
        public static void RegisterModeOverride(Func<bool> f) {
            if (f != null && !_modeOverrides.Contains(f) && NoteOverrideSource("模式限制")) _modeOverrides.Add(f);
        }
        // 登记"无视房主限制"豁免
        public static void RegisterHostOverride(Func<bool> f) {
            if (f != null && !_hostOverrides.Contains(f) && NoteOverrideSource("房主限制")) _hostOverrides.Add(f);
        }
        // 登记"冻结角色"求值（打开面板 / 地图时冻结自己的角色）
        public static void RegisterPauseProvider(Func<bool> f) {
            if (f != null && !_pauseProviders.Contains(f)) _pauseProviders.Add(f);
        }
        private static bool AnyTrue(List<Func<bool>> list) {
            for (int i = 0; i < list.Count; i++) {
                try { if (list[i]()) return true; }
                catch (Exception __ex) { Guard.Log("查询门控豁免", __ex); }
            }
            return false;
        }
        // 无视模式限制：所有"仅自由模式"的门控（视野 / 地图 / 重生 / 关卡等）都读它
        public static bool IgnoreModeLimit { get { return AnyTrue(_modeOverrides); } }
        // 无视房主限制：门控 GateHost 的 OverrideHost 读它
        public static bool HostOverride { get { return AnyTrue(_hostOverrides); } }
        // 冻结角色：打开面板 / 地图时冻结自己的角色
        public static bool PauseGame { get { return AnyTrue(_pauseProviders); } }


        // 共用提示文案（高频消息只生成一次），各功能直接用 SR.Msg。
        public static class Msg {

        }

        // 每帧钩子：功能在 Initialize 里 RegisterTick 注册，SR.Tick 按注册顺序逐个 try/catch 调用。
        // 功能不需被 SR 编译期引用。同一套机制还有 RegisterFrameEnd 钩子（DrawGUI 退出前调用）。
        private sealed class TickHook {
            public string Name;
            public Action Run;
            // 性能剖析（F9 浮层 + 超阈值告警）：滑动平均耗时、上一帧耗时、累计调用与超限次数。
            public float AvgMs;
            public float LastMs;
        // 单次最坏耗时：重钩子（2Hz / 0.5s）的平均值会被空闲帧摊平，只有峰值能反映真实的掉帧风险。
        public float MaxMs;
            public long Calls;
            public long Overruns;
            public float WarnedAt = -999f;
        }
        private static readonly List<TickHook> _tickHooks = new List<TickHook>();
        private static readonly List<TickHook> _frameEndHooks = new List<TickHook>();

        // 按名去重：同名 = 同一钩子的重新注册（覆盖 Run），与 RegisterPage 的同名覆盖语义一致；
        // 匿名委托每次都是新实例，用 Contains 拦不住渲染路径里的重复注册。
        private static void AddOrReplaceHook(List<TickHook> list, string name, Action hook) {
            string n = string.IsNullOrEmpty(name) ? "未命名" : name;
            for (int i = 0; i < list.Count; i++) {
                TickHook h = list[i];
                if (h != null && string.Equals(h.Name, n, StringComparison.Ordinal)) { h.Run = hook; return; }
            }
            list.Add(new TickHook { Name = n, Run = hook });
        }

        // 关机回调登记表（R2）：与 tick 同一套"按名去重、后注册先执行"的语义。
        private static readonly List<TickHook> _shutdownHooks = new List<TickHook>();

        internal static void RegisterShutdown(string name, Action cleanup) {
            if (cleanup == null) return;
            AddOrReplaceHook(_shutdownHooks, name, cleanup);
        }

        // 反向执行关机回调（后注册先跑），每个独立 try；幂等：跑完清空。
        internal static void RunShutdowns() {
            try {
                for (int i = _shutdownHooks.Count - 1; i >= 0; i--) {
                    TickHook h = _shutdownHooks[i];
                    if (h == null || h.Run == null) continue;
                    try { h.Run(); } catch (Exception __ex) { Guard.Log("关机回调 " + h.Name, __ex); }
                }
                if (_shutdownHooks.Count > 0) LogInfo("[SR] 已执行 " + _shutdownHooks.Count + " 个关机回调");
                _shutdownHooks.Clear();
            } catch (Exception __ex) { Guard.Log("关机回调批处理", __ex); }
        }

        public static void RegisterTick(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_tickHooks, name, hook);
        }

        public static void RegisterFrameEnd(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_frameEndHooks, name, hook);
        }

        // 外部独立 BepInEx 插件形态的功能入口（带 [BepInPlugin] 的 DLL）：插件在自己的 Awake 里调用。
        // MainPlugin 的扫描会跳过这类 DLL 里的 ITweak（BepInEx 已实例化过，再 Initialize 会重复注册）。
        public static void RegisterTweak(ITweak tweak, IFeatureHost host) {
            if (tweak == null) return;
            try {
                tweak.Initialize(host);
            } catch (Exception __ex) {
                Guard.Log("外部插件注册功能 " + tweak.GetType().Name, __ex);
            }
        }

        private static void RunTickHooks() { RunHooks(_tickHooks, "每帧钩子:"); }

        // 每帧收尾（面板关闭的早退分支也要调，否则光标形状会留在屏幕上）
        private static void RunFrameEndHooks() { RunHooks(_frameEndHooks, "每帧收尾:"); }

        // 性能剖析阈值：单 tick 超过 WarnMs 记一次告警（每 tick 每 10 秒最多一条），超过 BadMs 在浮层里标红。
        private const float TickWarnMs = 0.30f;
        private const float TickBadMs = 1.00f;
        private static float _tickProfLastAt = -999f;
        private static readonly List<TickHook> _tickProfTop = new List<TickHook>(8);

        private static void RunHooks(List<TickHook> hooks, string what) {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < hooks.Count; i++) {
                TickHook h = hooks[i];
                if (h == null || h.Run == null) continue;
                float t0 = Time.realtimeSinceStartup;
                try { h.Run(); }
                catch (Exception __ex) { Guard.Log(what + h.Name, __ex); }
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                h.LastMs = ms;
                h.Calls++;
                // 指数滑动平均（0.1 权重 ≈ 最近十帧），避免单帧噪声把排名抖来抖去。
                h.AvgMs = h.Calls <= 1 ? ms : (h.AvgMs * 0.9f + ms * 0.1f);
                if (ms > h.MaxMs) h.MaxMs = ms;
                if (ms >= TickWarnMs) {
                    h.Overruns++;
                    if (now - h.WarnedAt > 10f) {
                        h.WarnedAt = now;
                        // 用 Warn 而不是 Guard.Log(..., null)：后者会拼上"失败: 未知错误"，把性能告警说成异常。
                        // 明确写"单次峰值"：这类钩子可能只在个别帧重，平均值看不出来。
                        if (_tickProf != null && _tickProf.Value)
                            LogWarn("[性能] 帧钩子单次峰值 " + h.Name + " " + ms.ToString("0.00") + "ms"
                            + "（峰值 " + h.MaxMs.ToString("0.00") + "ms / 平均 " + h.AvgMs.ToString("0.00") + "ms / 阈值 "
                            + TickWarnMs.ToString("0.00") + "ms，累计 " + h.Overruns + " 次超限）");
                    }
                }
            }
            PublishTickProfile(now);
        }

        // 每 0.5 秒把"最慢的 5 个"写进 F9 浮层 + 一段日志，方便用户直接贴出来。
        private static void PublishTickProfile(float now) {
            try {
                if (now - _tickProfLastAt < 0.5f) return;
                _tickProfLastAt = now;
                _tickProfTop.Clear();
                for (int i = 0; i < _tickHooks.Count; i++) {
                    TickHook h = _tickHooks[i];
                    if (h == null || h.AvgMs <= 0f) continue;
                    _tickProfTop.Add(h);
                }
                if (_tickProfTop.Count == 0) return;
                _tickProfTop.Sort(delegate (TickHook a, TickHook b) { return b.AvgMs.CompareTo(a.AvgMs); });
                int n = Mathf.Min(5, _tickProfTop.Count);
                float total = 0f;
                for (int i = 0; i < _tickProfTop.Count; i++) total += _tickProfTop[i].AvgMs;
                // 浮层已删除：剖析结果只进日志，不再往 DiagLines 里堆
                _ = ("帧钩子合计 " + total.ToString("0.00") + "ms（" + _tickProfTop.Count + " 个）");
                for (int i = 0; i < n; i++) {
                    TickHook h = _tickProfTop[i];
                    string mark = h.AvgMs >= TickBadMs ? "!!" : (h.AvgMs >= TickWarnMs ? "!" : "  ");
                    _ = mark + " " + h.Name + " " + h.AvgMs.ToString("0.00") + "ms"
                        + (h.Overruns > 0 ? "（超限 " + h.Overruns + "）" : "");
                }
            } catch (Exception __ex) { Guard.Log("帧钩子性能剖析", __ex); }
        }

        // 供"健康检查"页导出：把当前 tick 排名拼成多行文本。
        internal static string TickProfileReport() {
            try {
                List<TickHook> all = new List<TickHook>();
                for (int i = 0; i < _tickHooks.Count; i++) if (_tickHooks[i] != null) all.Add(_tickHooks[i]);
                // 按名字排序：按耗时排会让行每 0.5 秒换位置，肉眼没法追踪。
                all.Sort(delegate (TickHook a, TickHook b) { return string.Compare(a.Name, b.Name, System.StringComparison.Ordinal); });
                StringBuilder sb = new StringBuilder();
                float total = 0f;
                for (int i = 0; i < all.Count; i++) total += all[i].AvgMs;
                sb.Append("帧钩子 ").Append(all.Count).Append(" 个，合计 ").Append(total.ToString("0.00")).AppendLine("ms");
                int n = Mathf.Min(15, all.Count);
                for (int i = 0; i < n; i++) {
                    TickHook h = all[i];
                    if (h.AvgMs <= 0.001f) break;
                    sb.Append("  ").Append(h.AvgMs.ToString("0.000")).Append("ms  ").Append(h.Name)
                      .Append("  调用=").Append(h.Calls).Append("  超限=").Append(h.Overruns).AppendLine();
                }
                return sb.ToString();
            } catch (Exception __ex) { Guard.Log("帧钩子报告", __ex); return ""; }
        }

        // 强制复位全部 UI 状态（对局开始时调用，避免卡住的管理器 / 地图拖住快照加载握手）。
        public static void ForceResetUiState() {
            try {
                // 对局开始：请各功能收起自己的 UI（功能自己用 RegisterDismissHook 声明收尾方式）。
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("对局开始时收起功能 UI", __ex); }
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _enumOptions.Clear();   // 条目对象会重建
                SR.ColorPicker.ClearStates();   // 与 _editText / _editOpen 同批失效：配置重读后条目对象会重建，旧键会留成陈旧项
                ApplyEventSystemGate(); // 重新启用游戏的 EventSystem
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("ForceResetUiState", __ex); }
            } catch (Exception __ex) { Guard.Log("对局开始处理", __ex); }
        }

        public void Initialize(IFeatureHost plugin) {
            // 表格自适应高度助手：公式留在实现层，契约层只负责转发（功能页在 Initialize 之后才会渲染）。
            try { SRApi.SetTableAutoHeightSink(delegate (float __u, float __m) { return SR.Ctl.TableAutoHeight(__u, __m); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度助手", __ex); }
            // 统一操作列按钮：绘制/命中/悬浮都在实现层，契约层只转发（功能页在 Initialize 之后才会渲染）。
            try {
                SRApi.Ctl.SetSelfToggleButtonSink(delegate (string __l, string __t, bool __o, ConfigEntry<KeyCode> __k, float __w, bool __b) {
                    return SR.Ctl.SelfToggleButton(__l, __t, __o, __k, __w, __b);
                });
                SRApi.Ctl.SetSelItemSink(delegate () { return SR.Ctl.SelItem; });
                SRApi.Ctl.SetCheckStylesSink(delegate () { return SR.Ctl.CheckOn; }, delegate () { return SR.Ctl.CheckOff; });
                SRApi.SetTableColMoveSink(delegate (Event __e, Rect __c, int __ci, int __k, List<int> __cols, Vector2 __mp, bool __in,
                    List<Rect> __hc, int[] __ord, Action<int[]> __ao, Action __sv, Action<int> __ck,
                    ref int __mc, ref float __mx, ref float __mdx, ref Vector2 __sc, ref Vector2 __scd, bool __v) {
                    return SR.TableColMove(__e, __c, __ci, __k, __cols, __mp, __in, __hc, __ord, __ao, __sv, __ck,
                        ref __mc, ref __mx, ref __mdx, ref __sc, ref __scd, __v);
                });
            } catch (Exception __sk) { Guard.Log("控件 sink 注册", __sk); }
            try {
                SRApi.Ctl.SetForceZhSink(delegate () { return SR.Ctl.ForceZh; }, delegate (bool v) { SR.Ctl.ForceZh = v; });
            } catch (Exception __fz) { Guard.Log("强制中文 sink", __fz); }
            try {
                SRApi.SetTableActionButtonsSink(delegate (Rect __r, SRApi.TableAction[] __a, float[] __w, ref int __h) { return SR.Ctl.TableActionButtons(__r, __a, __w, ref __h); });
                SRApi.SetTableActionColumnWidthSink(delegate (SRApi.TableAction[] __a) { return SR.Ctl.TableActionColumnWidth(__a); });
            } catch (Exception __ex) { Guard.Log("注入统一操作列按钮", __ex); }
            try { SRApi.SetTableAutoFitSink(delegate (float __b, float __y, float __m, ref float __bias) { return SR.Ctl.TableAutoFit(__b, __y, __m, ref __bias); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度自适应", __ex); }
            try { SRApi.SetTableAfterDrawSink(delegate (float __b, float __d, ref float __bias) { SR.Ctl.TableAfterDraw(__b, __d, ref __bias); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度校准", __ex); }
            try { SRApi.Ctl.SetTableHeightGripSink(delegate (ConfigEntryBase __e, Rect __g, float __ah, int __min, int __max) { SR.Ctl.TableHeightGrip(__e, __g, __ah, __min, __max); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度拖拽", __ex); }
            try { SRApi.SetTableScrollbarForSink(delegate (SRApi.TableArgs __a, Event __e, ref bool __dr, ref float __gy, ref float __sy) { return SR.Ctl.TableScrollbarFor(__a, __e, ref __dr, ref __gy, ref __sy); }); }
            catch (Exception __ex) { Guard.Log("注入表格滚动条一体化", __ex); }
            // 竖向滚动条绘制（ImGui 配色）由核心统一提供。
            try { SRApi.SetTableScrollbarSink(delegate (Rect __t, float __ch, float __vh, float __sy, bool __dr, out Rect __th, bool __vis, float __al) { SR.Ctl.TableScrollbar(__t, __ch, __vh, __sy, __dr, out __th, __vis, __al); }); }
            catch (Exception __ex) { Guard.Log("注入表格滚动条", __ex); }
            // “不写绑定提示”的快捷键按钮：与普通 HotkeyButton 同一套右键绑键逻辑，只少了提示行。
            try { SRApi.Ctl.SetHotkeyButtonNoHintSink(delegate (string __id, string __l, string __t, Action __a, float __w, float __h) { return SR.Ctl.HotkeyButtonNoHint(__id, __l, __t, __a, __w, __h); }); }
            catch (Exception __ex) { Guard.Log("注入无提示快捷键按钮", __ex); }
            _internalConfig = MainPlugin.Data != null ? MainPlugin.Data : plugin.Config; // 核心条目也走外置文件
            // 界面文案自注册：本处只派发 SR 分部文件，功能在自己的 Initialize 里注册自己的文案。
            SelfRegCore();
            // 必须在任何 Config.Bind 之前把旧 cfg 的中文 section / key 改写成英文；
            // 改写后要重新读盘，否则内存里仍是旧键，新建条目拿默认值且下次写盘又写回中文。
            try {
                // 迁移必须覆盖两处文件：
                //   1) plugin.Config 指向的插件自带 cfg；
                //   2) MainPlugin.Data（外置 SR_UCH_data.cfg）—— 核心与功能的条目实际都 Bind 在它上面。
                // 只迁 1) 时，外置文件里的中文旧段名永不改写，老用户升级后设置项表现为"消失"（读默认值）。
                bool migratedAny = false;
                string pluginPath = plugin.Config.ConfigFilePath;
                if (ConfigMigration.Migrate(pluginPath)) migratedAny = true;
                string internalPath = _internalConfig != null ? _internalConfig.ConfigFilePath : null;
                if (!string.IsNullOrEmpty(internalPath)
                    && !string.Equals(internalPath, pluginPath, StringComparison.OrdinalIgnoreCase)) {
                    if (ConfigMigration.Migrate(internalPath)) migratedAny = true;
                }
                if (migratedAny) {
                    try { plugin.Config.Reload(); } catch (Exception __rp) { Guard.Log("配置迁移/重载插件 cfg", __rp); }
                    try { if (_internalConfig != null) _internalConfig.Reload(); } catch (Exception __ri) { Guard.Log("配置迁移/重载外置 cfg", __ri); }
                    ClearEntryCache();   // 键被改名 / 删掉了：条目缓存必须失效
                    SR.LogInfo("[T1] 已迁移旧版 cfg（插件 cfg + 外置 cfg）并重新读盘");
                }
            } catch (Exception __ex) { Guard.Log("T1 cfg 迁移/重读", __ex); }
            // 反射探针只做启动自检（RefAudit 逐条解析登记的成员并报可用 / 失效）。
            // NavHide 把历史 cfg 里可能残留的 [Reflection] 段从侧栏藏掉。
            try { NavHide("Reflection"); } catch (Exception __ex) { Guard.Log("Reflection 段隐藏", __ex); }
            _openKey = plugin.Config.Bind(Keys.Settings, Keys.OpenKey, KeyCode.Insert, "打开和关闭管理器用, 默认 Insert. 组合键的绑法: 点按钮, 按住 Shift 或 Ctrl 或 Alt, 再按主键.");
            RegisterKey("管理器-开关", _openKey, "press");
            RegisterFrameEnd("滑块残留状态清理", SliderFrameEndCleanup);   // 滑块残留状态清理挂在每帧收尾
            _blockInputEntry = plugin.Config.Bind(Keys.Settings, Keys.BlockInput, true, "管理器打开时冻结游戏输入, 免得误操作角色.");
            _uiScaleEntry = plugin.Config.Bind(Keys.Settings, Keys.UiScale, 1.3f, "整个界面放大多少, 1.0 到 1.8, 默认 1.3.");
            // 界面语言：中文 / English（运行时立即生效；外部模块页面始终中文）
            _langEntry = plugin.Config.Bind(Keys.Settings, Keys.Language, "English", "界面用哪种语言. 装了语言包 DLL 的话, 它也会自动出现在这个下拉里. 外部插件的页面始终是中文.");
            // 帧钩子剖析开关：默认关闭，避免每 10 秒刷一条性能警告；排查卡顿时再打开。
            _tickProf = plugin.Config.Bind(Keys.Settings, "Tick Profiling", false,
                "把每个帧钩子的耗时写进日志（默认关，排查卡顿时打开）。");
            // 切语言 / 装语言包都走 ApplyLanguage：解析 id 并让显示名缓存失效
            _langEntry.SettingChanged += (s, e) => ApplyLanguage();
            ApplyLanguage();
            // 语言包外置：扫描 config/SR_UCH/lang/*.json 并注册（第三方语言包不再需要编译 DLL）。
            // 位置要求：必须在语言条目 Bind + ApplyLanguage 之后（RegisterLanguage 内部会按 cfg 里保存的语言 id
            // 重新解析，否则本次启动选不中 JSON 语言包），并在各功能的 Initialize 与第一次渲染之前。
            // 加载器自身逐文件兜底，这里再包一层 try，保证任何异常都不影响核心启动。
            try { LoadJsonLanguagePacks(); } catch (Exception __ex) { Guard.Log("加载 JSON 语言包", __ex); }
            // 地图总开关：默认关闭；开启后 M 键开地图，关闭时已打开的地图立即关闭（地图网格等独立功能不受影响）。
            // 设置页栏目宽度：0 = 自动按文字宽度；也可拖动窗口侧栏右缘调整，结果写回本项。
            _sidebarWEntry = plugin.Config.Bind(Keys.Settings, Keys.SidebarWidth, 0, "左边那一栏有多宽, 单位像素, 填 0 让它自己算. 也可以直接在窗口里拖侧栏右边缘.");
            _sidebarOrderEntry = plugin.Config.Bind(Keys.Settings, Keys.SidebarOrder, "", "侧栏栏目的先后顺序, 用逗号隔开. 在设置页的栏目顺序那一块用上移下移按钮调, 调完自动写回来.");
            // 用配置版本号一次性清掉历史遗留的 Sidebar Order 脏值（此后拖动的顺序照常保存）。
            try {
                // 内部版本号绑在隐藏段，不出现在设置页
                NavHide("Internal");
                ConfigEntry<int> __schema = plugin.Config.Bind("Internal", "Sidebar Order Schema", 0, "内部用: 侧栏顺序的存储版本, 用来一次性清掉历史遗留的脏值.");
                // 第二个版本号：清掉栏目改名后残留的旧段名（ApplyCustomSidebarOrder 负责改名映射与剔除死条目）。
                // v3: HUD / presets added and default order adjusted; drop stale custom sidebar order once.
                if (__schema.Value < 3) {
                    _sidebarOrderEntry.Value = "";
                    __schema.Value = 3;
                    SR.LogInfo("[SR] sidebar order schema v3: reset to Nav rank default order");
                }
            } catch (Exception __ex) { Guard.Log("清理侧栏顺序历史值", __ex); }
            _sidebarW = _sidebarWEntry.Value;
            _sidebarWEntry.SettingChanged += (s, e) => _sidebarW = _sidebarWEntry.Value;
            // 加载后清理已迁到 Experiments.cs（section 同时改为 Experiments，界面位置不变）
                    _allEnabledEntry = plugin.Config.Bind(Keys.Settings, Keys.AllEnabled, false, "整个 Mod 的总开关. 关掉以后所有内部功能都不生效, 但每个功能的开关值不会被改掉.\n默认是关的, 改动会自动保存, 下次启动保持上次的状态.");
            AllEnabled = _allEnabledEntry.Value;
            // 总开关关闭也走 FireDismissUi（与换场景、对局开始、EX 强制回主界面并列）
            _allEnabledEntry.SettingChanged += (s, e) => {
                AllEnabled = _allEnabledEntry.Value;
                if (!AllEnabled) {
                    try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("总开关关闭时收起功能 UI", __ex); }
                }
            };
            // 外部插件默认启用；手动禁用的 GUID 记入本列表（deny-list），重启后保持禁用。
            _disabledPluginsEntry = plugin.Config.Bind(Keys.Settings, Keys.DisabledPlugins, "", "不想让它跟着管理器一起出现的外部插件, 把 GUID 填进来, 用分号隔开. 没写进来的默认启用.");
            // 设置页顶部有专门的插件清单，这个原始 GUID 字符串条目不再显示
            SRApi.RowFilters.Add(SettingsHiddenDisabledPlugins);
            // 窗口宽高 / XY 不暴露为设置条目（右下角拖拽即可，见 SR.Window.ApplyScaleToWindow）
            SRApi.RowFilters.Add(SettingsHiddenWindowGeometry);
            BlockInput = _blockInputEntry.Value;
            _blockInputEntry.SettingChanged += (s, e) => {
                BlockInput = _blockInputEntry.Value;
                ApplyEventSystemGate();
            };
            // 窗口几何条目：在这里建一次并缓存到静态字段（SR.Window 拖动时直接写缓存条目）。
            // 类型必须与历史写法一致（宽高 int，X/Y float），否则 BepInEx 会按新类型建出另一个条目。
            _winWidthEntry = plugin.Config.Bind(Keys.Settings, Keys.WinWidth, 720, "");
            _winHeightEntry = plugin.Config.Bind(Keys.Settings, Keys.WinHeight, 520, "");
            _winXEntry = plugin.Config.Bind(Keys.Settings, Keys.WinX, 30f, "");
            _winYEntry = plugin.Config.Bind(Keys.Settings, Keys.WinY, 30f, "");
            _winWidth = Mathf.Clamp(_winWidthEntry.Value, 400, 1600);
            _winHeight = Mathf.Clamp(_winHeightEntry.Value, 300, 1000);
            _winX = _winXEntry.Value;
            _winY = _winYEntry.Value;

            GameObject go = new GameObject("SR_UCHManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ManagerUI>();
            // 换场景：复位全部 UI 状态，否则打开的管理器会冻结角色、停掉 GameControl 并禁用 EventSystem。
            SceneManager.activeSceneChanged += (a, b) => {
                _stylesReady = false;
                // 换场景：先通知各功能（FireSceneChanged），再请它们收起自己的 UI（FireDismissUi）。
                try { FireSceneChanged(); } catch (Exception __ex) { Guard.Log("换场景通知功能", __ex); }
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("换场景时收起功能 UI", __ex); }
                if (_visible) {
                    _visible = false;
                    _capturing = null;
                    _editText.Clear();
                    _editOpen.Clear();
                    _enumOptions.Clear();   // 条目对象会重建
                SR.ColorPicker.ClearStates();   // 与 _editText / _editOpen 同批失效：配置重读后条目对象会重建，旧键会留成陈旧项
                    ApplyEventSystemGate(); // 重新启用游戏的 EventSystem
                }
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("SR.Core", __ex); }   // 清掉滑块 / 滚轮盾的跨帧残留
            };
            // EnsureScanned / ApplyDisabledPlugins 不在此调用：其它 tweak 可能在本插件之后初始化，扫描在首帧 Update 跑。
            try { Harmony.CreateAndPatchAll(typeof(SR)); } catch (Exception ex) { SR.LogError("SR 补丁注册失败: " + ex.Message); }
            // 录键屏蔽表情系统的嵌套类不被 CreateAndPatchAll 递归发现，需单独注册；失败不影响其它补丁
            try { Harmony.CreateAndPatchAll(typeof(EmoteBlockPatch)); } catch (Exception ex) { SR.LogWarn("录键屏蔽表情系统 补丁注册失败: " + ex.Message); }
        }

        // 侧栏栏目自注册（SR.Nav(段, 顺序值) 越小越靠前；SR.NavHide(段) 有 cfg 段但不单独成栏目）。
        // 按注册表重建侧栏栏目表：注册顺序值排序 -> 兜底 cfg 段 -> 自定义顺序；晚注册也必须立刻生效。
        private static void RebuildSections(bool resetSelection) {

            try {
                _internalSections.Clear();
                List<string> registered = new List<string>();
                foreach (string s in _navOrder) if (!_navHidden.Contains(s)) registered.Add(s);
                registered.Sort(delegate(string a, string b) {
                    int r = _navRank[a].CompareTo(_navRank[b]);
                    if (r != 0) return r;
                    return _navOrder.IndexOf(a).CompareTo(_navOrder.IndexOf(b)); // 同顺序值：按注册先后
                });
                // 已在底栏注册按钮的栏目不再出现在左栏
                for (int i = 0; i < SRApi.FooterButtons.Count; i++) {
                    string fb = SRApi.FooterButtons[i] != null ? SRApi.FooterButtons[i].Id : null;
                    if (!string.IsNullOrEmpty(fb)) registered.Remove(fb);
                }
                for (int i = 0; i < registered.Count; i++) {
                    if (!_internalSections.Contains(registered[i])) _internalSections.Add(registered[i]);
                }
                // 兜底：既没注册也没 NavHide 的 cfg 段（含老 cfg 残留段）排在注册项之后
                if (_internalConfig != null) {
                    foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                        string sec = e.Definition.Section;
                        if (string.IsNullOrEmpty(sec)) sec = "(General)";
                        if (_navHidden.Contains(sec) || _navRank.ContainsKey(sec)) continue;
                        bool fbSec = false;
                        for (int k = 0; k < SRApi.FooterButtons.Count; k++) {
                            if (SRApi.FooterButtons[k] != null && SRApi.FooterButtons[k].Id == sec) { fbSec = true; break; }
                        }
                        if (fbSec) continue;
                        if (!_internalSections.Contains(sec)) _internalSections.Add(sec);
                    }
                }
                ApplyCustomSidebarOrder(); // 侧栏自定义顺序（拖动栏目后排的）
                if (resetSelection && _internalSections.Count > 0) _selectedInternalSection = _internalSections[0];
                // 当前选中项被移除 / 改名 -> 回到第一个，避免显示空白页
                if (_internalSections.Count > 0 && !_internalSections.Contains(_selectedInternalSection))
                    _selectedInternalSection = _internalSections[0];
            } catch (Exception __ex) { Guard.Log("重建侧栏栏目", __ex); }
        }

        // 供自定义栏目在写完顺序后立刻生效
        public static void RebuildSectionsPublic(bool resetSelection) { RebuildSections(resetSelection); }

        // 隐藏状态并入 Sidebar Order（隐藏的栏目名带 "!" 前缀）：顺序与可见性共用一份真值。
        public static void SetSectionHidden(string sec, bool hidden) {
            try {
                if (string.IsNullOrEmpty(sec)) return;
                List<string> parts = new List<string>();
                bool found = false;
                for (int i = 0; i < _allSections.Count; i++) {
                    string nm = _allSections[i];
                    bool hid = _userHiddenSections.Contains(nm);
                    if (nm == sec) { hid = hidden; found = true; }
                    parts.Add(hid ? ("!" + nm) : nm);
                }
                if (!found) return;
                if (_sidebarOrderEntry != null) _sidebarOrderEntry.Value = string.Join(",", parts.ToArray());
                RebuildSections(false);   // 内部已有"选中项被移除 -> 回到第一个"的兜底
            } catch (Exception __ex) { Guard.Log("隐藏侧栏栏目", __ex); }
        }
        // Shift+右键还原：清空 Sidebar Order（排序 + 隐藏存在同一份串里），恢复按 Nav rank 的默认栏目顺序。
        public static void ResetSidebarOrder() {
            try {
                if (_sidebarOrderEntry != null) _sidebarOrderEntry.Value = "";
                _userHiddenSections.Clear();
                RebuildSections(false);
                SR.LogInfo("[SR] 已还原侧栏栏目顺序与隐藏状态（回到默认顺序）");
            } catch (Exception __ex) { Guard.Log("还原侧栏顺序", __ex); }
        }

        // 晚注册：首次扫描之后再注册栏目 -> 立刻重建（否则静默不显示）
        private static void OnNavChanged(string sec) {
            if (!_scanned) return;   // 启动期注册：等 EnsureScanned 一次算完
            RebuildSections(false);
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("晚注册后重排 Configuration Manager", __ex); }

        }

        public static void Nav(string sec, int rank) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Remove(sec);
            if (!_navRank.ContainsKey(sec)) _navOrder.Add(sec);
            _navRank[sec] = rank;
            OnNavChanged(sec);
        }

        // 正在打字登记（通用：任何有输入框的功能都能登记）；多个来源任一为真即认为"正在打字"。
        private static readonly List<Func<bool>> _typingProviders = new List<Func<bool>>();
        public static void RegisterTypingProvider(Func<bool> provider) {
            if (provider != null && !_typingProviders.Contains(provider)) _typingProviders.Add(provider);
        }
        public static bool Typing {
            get {
                for (int i = 0; i < _typingProviders.Count; i++) {
                    try { if (_typingProviders[i]()) return true; }
                    catch (Exception __ex) { Guard.Log("查询打字状态", __ex); }
                }
                return false;
            }
        }

        public static void NavHide(string sec) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Add(sec);
            _navOrder.Remove(sec);
            _navRank.Remove(sec);
            OnNavChanged(sec);
        }

        private static void EnsureScanned() {
            if (_scanned) return;
            _scanned = true;
            bool foundSelf = false;   // 是否命中"自己的配置"：没命中要告警（见方法末尾）
            foreach (var kv in Chainloader.PluginInfos) {
                PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                PluginEntry pe = new PluginEntry {
                    guid = kv.Key,
                    name = info.Metadata != null && info.Metadata.Name != null ? info.Metadata.Name : kv.Key,
                    config = info.Instance.Config,
                    instance = info.Instance,
                    path = SafeAssemblyPath(info.Instance)
                };
                // 用 MainPlugin 的编译期常量：与 [BepInPlugin] 同源，改名漏改会编译报错。
                if (kv.Key == MainPlugin.PluginGuid) {
                    foundSelf = true;
                    _internalConfig = pe.config;
                    // 启动期注册的功能键组合条目在这里才建得出来（此前 Bind 不了隐藏条目）
                    try { FlushPendingCombos(); } catch (Exception __ex) { Guard.Log("补建按键组合条目", __ex); }
                    // 功能 / 外部模块自注册的配置迁移规则此时已登记完（本方法晚于全部 ITweak 与 EX 初始化），
                    // 这里补跑一次；规则幂等，无人登记时零开销。
                    try {
                        if (ConfigMigration.HasPending) {
                            string cfgPath = MainPlugin.Data != null ? MainPlugin.Data.ConfigFilePath : pe.config.ConfigFilePath;
                            if (!string.IsNullOrEmpty(cfgPath) && ConfigMigration.RunPending(cfgPath)) {
                                pe.config.Reload();
                                ClearEntryCache();
                                LogInfo("[T1b] 已应用功能自注册的配置迁移规则并重新读盘");
                            }
                        }
                    } catch (Exception __ex) { Guard.Log("功能自注册迁移规则", __ex); }
                    // 关掉 BepInEx 的 SaveOnConfigSet，改由 FlushConfig（最多 1 秒一次）+ 关面板 Save + 各功能显式 Save。
                    try {
                        pe.config.SaveOnConfigSet = false;
                        pe.config.SettingChanged += OnAnySettingChanged;
                    } catch (Exception __ex) { Guard.Log("配置落盘节流初始化", __ex); }
                    // 回填组合键持久化：功能 Initialize 早于本扫描，当时的组合修饰可能未落盘。
                    try {
                        foreach (var kme in new Dictionary<ConfigEntryBase, ComboMod>(_keyMods)) {
                            SetKeyComboMod(kme.Key, kme.Value);
                        }
                    } catch (Exception __ex) { Guard.Log("重新应用按键修饰键", __ex); }
                    continue;
                }
                _externalPlugins.Add(pe);
            }
            _externalPlugins.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            RebuildSections(true);
            // 栏目归属审计必须在 RebuildSections 之后（此时 _navRank / _navHidden 才完整）
            try { SectionFallbackAudit(); } catch (Exception __ex) { Guard.Log("兜底栏目审计", __ex); }
            // 迁移规则与当前真正被绑定的键比对（必须晚于全部功能的 Bind）
            try { MigrationRuleAudit(); } catch (Exception __ex) { Guard.Log("迁移规则冲突自检", __ex); }
            // 孤儿键审计同样必须在全部 Bind 之后（含 FlushPendingCombos 建的组合键条目），否则会大批误报。
            try { OrphanAudit(); } catch (Exception __ex) { Guard.Log("配置孤儿键自检", __ex); }
            if (_externalPlugins.Count > 0) _pluginKey = _externalPlugins[0].guid;
            ApplyDisabledPlugins(); // 重新禁用上次会话禁用的插件
            ApplyCmOrder();         // 让 Configuration Manager 的条目顺序与 SR 界面一致（侧栏顺序 + 栏目内绑定顺序）

            // 没命中自己的 GUID 就告警：靠 _internalConfig == null 判不出来（它从不为 null）。
            // 此时 FlushPendingCombos / SaveOnConfigSet / 组合键回填都不跑，表现为组合键全丢、配置每次写盘。
            if (!foundSelf && !_warnedNoSelfGuid) {
                _warnedNoSelfGuid = true;
                try {
                    SR.LogWarn("[SR] 扫描插件表时没找到自己的 GUID（" + MainPlugin.PluginGuid
                        + "）-> 组合键补建与配置落盘节流未启用。请检查 GUID 是否与 [BepInPlugin] 一致。");
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
        }

        // 供契约层转发（SRApi.CloseMenu）：功能页要打开游戏自己的界面时先收起本面板。
        public static void CloseMenuExternal() { CloseMenu(); }

        // 供契约层转发（SRApi.OpenMenu）：从游戏界面返回后自动把管理器重新打开。
        public static void OpenMenuExternal() {
            try {
                _visible = true;
                ApplyEventSystemGate();
                EnsureStyles();
            } catch (Exception __ex) { Guard.Log("OpenMenuExternal", __ex); }
        }

        private static void CloseMenu() {
            _visible = false;
            _capturing = null;
            ConfigFile[] __dcs = new ConfigFile[_dirtyConfigs.Count];
            _dirtyConfigs.CopyTo(__dcs);
            _dirty = false;
            // 关闭菜单时保存配置，失败记日志（逐个保存，一个失败不影响其它）
            for (int __i = 0; __i < __dcs.Length; __i++) {
                ConfigFile __dc = __dcs[__i];
                Guard.Try("关闭菜单时保存配置", () => __dc.Save());
                _dirtyConfigs.Remove(__dc);
            }
            ApplyEventSystemGate();
            try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("CloseMenu", __ex); }   // 清掉滑块 / 滚轮盾的跨帧残留
        }

        // 本 Mod 总开关在设置页不重复成行（搜索栏右侧已有专用按钮）
        private static bool AllEnabledRowHidden(ConfigEntryBase e) {
            return !(e.Definition.Section == "Settings" && e.Definition.Key == "All Enabled");
        }

        // 管理器自己的界面文案与侧栏注册
        private static void SelfRegCore() {
            SRApi.RowFilters.Add(AllEnabledRowHidden); // 设置页不重复显示本 Mod 总开关
            SR.LocSec("Settings", "设置", null);
            SR.NavHide("Settings"); // 设置页是独立顶层页（不占内部侧栏栏目）
            // 已删除功能的段名不能让老 cfg 残留段出现在侧栏；名单在 ConfigMigration，核心不维护功能名单。
            for (int i = 0; i < ConfigMigration.DeletedSections.Length; i++)
                SR.NavHide(ConfigMigration.DeletedSections[i]);
            SR.LocSec("Home", "首页", null);
            SR.Nav("Home", 10); // 首页自己的侧栏栏目（顺序 10）
            SR.RegisterPage("Home", RenderHomePage); // 首页也走自绘页注册表（SR.Window 不做 if-else 分派）
            SR.LocKey(Keys.Settings, Keys.UiScale, "界面缩放", null);
            // 统一登记：功能页里没有自带 LocDesc 的可见条目（3 行：是什么 / 怎么用 / 注意；默认值由核心追加）。
            SR.LocDesc("Chat", "Log Limit", "聊天记录条数\n最多保留多少条聊天消息\n超出后丢弃最旧的；仅本模组记录页", "Chat log limit\nhow many chat messages to keep\noldest dropped first; this mod's log page only");
            SR.LocDesc("Level", "Party Bomb", "派对盒炸弹\n开派对盒时按概率生成炸弹\n需要房主权限；仅派对盒阶段", "Party box bomb\nchance to spawn a bomb when a party box opens\nhost only; party-box phase only");
            SR.LocDesc("Level", "Party Bomb Type", "派对盒炸弹种类\n决定生成哪一种炸弹\n与游戏内炸弹类型一一对应", "Party box bomb type\nwhich bomb the party box spawns\nmatches the game's bomb types");
            SR.LocDesc("Level", "Refresh Party Box Key", "刷新派对盒按键\n按下重开派对盒并重新抽选\n未绑定时不生效", "Reroll party box key\npress to reopen the party box and reroll\nno effect while unbound");
            SR.LocDesc("Level", "Close Party Box Key", "关闭派对盒按键\n按下结束选择并进入游玩阶段\n未绑定时不生效", "Close party box key\npress to end picking and start playing\nno effect while unbound");
            SR.LocDesc("Online", "Filter Region", "区域筛选\n只列出指定区域的房间\n留空表示不限区域", "Region filter\nonly lobbies in the selected region are listed\nempty means any region");
            SR.LocDesc("Online", "Filter Mode", "模式筛选\n只列出指定模式的房间\n留空表示不限模式", "Mode filter\nonly lobbies of the selected mode are listed\nempty means any mode");
            SR.LocDesc("Online", "Filter Tag", "标签筛选\n只列出带指定标签的房间\n留空表示不限标签", "Tag filter\nonly lobbies with the selected tag are listed\nempty means any tag");
            SR.LocDesc("Online", "Filter State", "状态筛选\n按可加入状态过滤房间\n留空不限；join 可加入，full 已满，playing 对局中", "State filter\nfilters lobbies by joinability\nempty = any; join = open, full = full, playing = in a match");
            SR.LocDesc("Online", "Search Timeout", "搜索超时秒数\n等待房间列表的上限时间\n仅兜底用，一般不需要改", "Search timeout\nhow long to wait for the lobby list\nfallback only; normally no need to change");
            SR.LocDesc("Online", "Sort Column", "排序列索引\n房间列表按哪一列排序\n由表头操作维护，不建议手改", "Sort column index\nwhich column sorts the lobby list\nmaintained by the table header; avoid editing");
            SR.LocDesc("Online", "Sort Descending", "排序方向\n开启从大到小，关闭从小到大\n与排序列一起生效", "Sort direction\non = descending, off = ascending\nworks together with the sort column");
            SR.LocDesc("Chat Window", "Key Defaults v2", "内部标记：键位默认值已迁移\n仅配置迁移使用\n改动不会产生任何效果", "Internal flag: key defaults migrated\nconfig migration only\nchanging it has no effect");
            SR.LocDesc("Chat Window", "Defaults v3", "内部标记：字号默认值已迁移\n仅配置迁移使用\n改动不会产生任何效果", "Internal flag: font defaults migrated\nconfig migration only\nchanging it has no effect");
            SR.LocDesc(Keys.Settings, Keys.UiScale, "界面缩放\n整个界面按比例放大，1.0 为原始大小\n范围 0.5 到 2；字太小可适当调大", "UI scale\nscales the whole interface; 1.0 is the original size\nrange 0.5-2; increase it if the text is too small");
            // 核心自己的设置项也走"行扩展点"：设置页因此只有一份滑块绘制代码，语言下拉不按 section/key 硬编码。
            SRApi.RowSliders.Add(SettingsSliderRange);
            SR.RowComboOptions.Add(SettingsComboOptions);
            SR.LocKey(Keys.Settings, Keys.DisabledPlugins, "禁用的外部插件", null);
            SR.LocDesc(Keys.Settings, Keys.DisabledPlugins, "禁用的外部插件\n填写要禁用的插件 GUID，用分号分隔\n只影响外部插件加载；留空表示全部启用", "External plugins to disable\nenter the GUIDs to disable, separated by semicolons\naffects external plugin loading only; empty means all enabled");
            SR.LocKey(Keys.Settings, Keys.OpenKey, "打开键", null);
            SR.LocDesc(Keys.Settings, Keys.OpenKey, "打开键\n按下打开或关闭管理器\n点键框后按新键改绑，支持 Shift/Ctrl 组合键", "Open key\npress to open or close the manager\nclick the key box to rebind; Shift/Ctrl combos are supported");
            SR.LocKey(Keys.Settings, Keys.BlockInput, "冻结输入", null);
            SR.LocDesc(Keys.Settings, Keys.BlockInput, "冻结输入\n管理界面打开时冻结游戏输入\n只在你本地生效，不改对局规则", "Freeze input\nfreezes game input while the manager is open\nlocal only; does not change match rules");
            SR.LocKey(Keys.Settings, Keys.WinWidth, "窗口宽度", null);
            SR.LocDesc(Keys.Settings, Keys.WinWidth, "窗口宽度\n设置管理器窗口的宽度\n范围 400 到 1600", "Window width\nsets the manager window width\nrange 400-1600");
            SR.LocKey(Keys.Settings, Keys.WinHeight, "窗口高度", null);
            SR.LocDesc(Keys.Settings, Keys.WinHeight, "窗口高度\n设置管理器窗口的高度\n范围 300 到 1000", "Window height\nsets the manager window height\nrange 300-1000");
            SR.LocKey(Keys.Settings, Keys.WinX, "窗口X", null);
            SR.LocDesc(Keys.Settings, Keys.WinX, "窗口 X 坐标\n窗口左边距屏幕左边缘的像素距离\n坐标以屏幕左上角为原点", "Window X\nhorizontal pixel distance from the left screen edge\norigin is the top-left corner of the screen");
            SR.LocKey(Keys.Settings, Keys.WinY, "窗口Y", null);
            SR.LocDesc(Keys.Settings, Keys.WinY, "窗口 Y 坐标\n窗口上边距屏幕上边缘的像素距离\n坐标以屏幕左上角为原点", "Window Y\nvertical pixel distance from the top screen edge\norigin is the top-left corner of the screen");
            SR.LocKey(Keys.Settings, Keys.SidebarWidth, "栏目宽度", null);
            SR.LocDesc(Keys.Settings, Keys.SidebarWidth, "栏目宽度\n左侧栏目栏的宽度，0 = 自动计算\n范围 100 到 320；也可拖动侧栏右边缘调整", "Sidebar width\nwidth of the left sidebar, 0 = auto\nrange 100-320; you can also drag the sidebar right edge");
            SR.LocKey(Keys.Settings, Keys.Language, "界面语言", null);
        }

// 分区：Guard（可观测性：统一带日志的 try / catch 包装）
// 实现只有一份，在契约层 SRApi.Guard（去重窗口 / 计数 / LastWhat）；这里只做转发，
// 避免两套去重表与两套计数同时存在导致日志与统计不一致。
        public static class Guard {
            public static void Try(string what, Action action) { SRApi.Guard.Try(what, action); }
            public static T Try<T>(string what, Func<T> action, T fallback) { return SRApi.Guard.Try(what, action, fallback); }
            public static void Log(string what, Exception ex) { SRApi.Guard.Log(what, ex); }
            public static string LastWhat { get { return SRApi.Guard.LastWhat; } }
            public static string LastError { get { return SRApi.Guard.LastError; } }
            public static int WarnCount { get { return SRApi.Guard.WarnCount; } }
        }

    }

}
