// 框架本体。只管道"壳"的事：状态与初始化、各种 Register 扩展点、Provide/Require 槽位、
// 每帧钩子派发、对局开始时重置界面，还有日志包装 Guard。
// 它不认识任何一个具体功能 —— 功能都是自己通过 SR.Register 挂上来的。
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
public partial class SR : ITweak {

// 分区：Core（核心状态 / 初始化 / 插件扫描）
// 约定：静态字段集中在此；新功能实现 ITweak 即被自动发现，新分区需在 sr_uch.rsp 注册。

        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _blockInputEntry;
        private static ConfigEntry<float> _uiScaleEntry;
        private static ConfigEntry<string> _disabledPluginsEntry;
        private static ConfigEntry<bool> _allEnabledEntry;
        // 对外（附加模块 DLL）暴露的注册接口版本。外部模块初始化时拿它跟自己的需求版本比，不匹配就自己提示/降级。
        // SR 不认识任何具体外部模块，只给一个版本号。
        // 为什么用 static readonly 而不是 const：const 会在**编译外部模块时**被内联成字面量，那样模块比的
        //   是"它编译时 SR 的版本"，不是"用户机器上当前 SR 的版本" —— SR 升级后会误报或漏报。
        //   static readonly 是运行时读取，握手才真正生效。
        // 握手用途：EX 之类的外部模块加载时比对（见 ExLoader.Init），版本不够就明确报错，而不是等它跑到
        //   一半抛 MissingMethodException（表现是"装了却没反应"，极难排查）。
        // 历史：1 = 初版；2 = 门控豁免改登记制 + 跨功能槽位 Provide/Require；3 = SetChatTypingProvider 改名 RegisterTypingProvider。
        public static readonly int ApiVersion = 3;
        // 反射辅助：失效告警 + 结果缓存（连"查过但没有"也缓存）
        // 本模组反射了游戏不少私有成员。游戏一更新，任一成员改名都会让 AccessTools 静默返回 null，表现就是"功能装了没反应"。
        // 这里取不到就打一条 Warning（说明哪个成员、支撑什么功能，去重不刷屏），并且结果连失败一起缓存。
        // 各功能在 SelfReg 里用 SR.RefOverride 声明"成员 -> 支撑什么功能"，调用处用 SR.RefField / RefMethod 取。
        // **失败也要缓存**：调用点普遍写成 if (_f == null) _f = SR.RefField(...)，失败时 _f 恒为 null，于是每次调用都
        // 重跑一遍全类型反射（聊天窗口那两处挂在 ChatDisplay.Update 上，字段一改名就变成每帧好几次）。
        private static readonly Dictionary<string, string> _refFeature = new Dictionary<string, string>();
        private static readonly HashSet<string> _refWarned = new HashSet<string>();
        private static readonly Dictionary<string, FieldInfo> _refFieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> _refMethodCache = new Dictionary<string, MethodInfo>();

        //功能文件在 SelfReg 里声明（key = "类型名.成员名"）：
        //    SR.RefOverride("QuickSaver.levelPortalXml", "重载关卡 / 广播方块快照");
        public static void RefOverride(string cfgKey, string feature) {
            if (string.IsNullOrEmpty(cfgKey)) return;
            // 同一个游戏成员被**多个功能**反射时，功能名要**追加**而不是覆盖：
            //  否则自检只报最后登记的那一个，游戏改名后另一个功能的失效原因就被藏起来了。
            //  实例：PartyBox.pieces 同时被方块破坏/关卡与 EX 派对盒依赖；
            //        ScoreKeeper.newPointBlocks 被重载保分与EX 加分分块依赖；
            //        VersusControl.graphScoreBoardInstance 被评分折扣与EX 加分分块依赖。
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

        //失效告警（每个成员只报一次，避免每帧路径刷屏）
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
                //注：不要再提示"可在 cfg 的 [Reflection] 段填新成员名恢复", 那套"成员名覆盖"探针
                //已随 #3 整体删除（不再创建/读取 [Reflection] 段），照着做是无效的，只会误导排查。
                AuditWarn("[SR.Ref] 反射失效: " + key + " -> " + (feat ?? "未登记")
                    + " 已禁用（游戏可能已更新了私有成员名；该功能本次会话内不再重试反射）");
            } catch (Exception __ex) { Guard.Log("RefWarn", __ex); }
        }

        public static FieldInfo RefField(Type owner, string name, string feature) {
            string key = RefKey(owner, name);
            FieldInfo cached;
            if (_refFieldCache.TryGetValue(key, out cached)) return cached;   //含"查过且没有"
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

        //带参数列表的重载（如 GetSpawnPosition(float)、resetPlayerCharacter(Character, bool)）
        public static MethodInfo RefMethod(Type owner, string name, Type[] parameters, string feature) {
            string key = RefKey(owner, name, parameters);
            MethodInfo cached;
            if (_refMethodCache.TryGetValue(key, out cached)) return cached;
            try { cached = AccessTools.Method(owner, name, parameters); } catch { cached = null; }
            _refMethodCache[key] = cached;
            if (cached == null) RefWarn(owner, name, feature);
            return cached;
        }

        //按类型名找游戏类型（"LevelSelectController" 这类简单名）：先查 Assembly-CSharp，
        //查不到再遍历已加载程序集（给将来不在主程序集里的成员留门）。
        private static Type RefType(string simpleName) {
            try {
                Type t = typeof(Level).Assembly.GetType(simpleName, false);
                if (t != null) return t;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                    try { t = a.GetType(simpleName, false); if (t != null) return t; } catch (Exception __ex) { Guard.Log("RefType", __ex); }
                }
            } catch (Exception __ex) { Guard.Log("RefType", __ex); }
            return null;
        }

        //启动总览（首次 Update 跑一次，此时所有功能的 SelfReg 都已执行完）：
        //存在性判定（自检专用，纯反射）：只看类型+基类里有没有这个名字，不关心重载歧义，
        //也不会像 AccessTools.Field 那样对方法名打 "Could not find field" 的警告（那 6 条日志就是这么来的）。
        private static bool RefExists(Type owner, string member) {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            // **V4**：支持"嵌套成员路径"（a.b.c）。登记项形如 ScoreKeeper.scoreInfo.totalScore：
            //  RefAudit 只按**第一个点**切出类型名，剩下的整段交给这里 -> 逐段下钻
            //  （嵌套类型 / 字段的类型），最后一段判存在性。没有这段，"登记内层字段"会被误判成失效。
            if (member != null && member.IndexOf('.') >= 0) return RefExistsPath(owner, member);
            try {
                for (Type t = owner; t != null; t = t.BaseType) {
                    if (t.GetField(member, F) != null) return true;
                    MethodInfo[] ms = t.GetMethods(F);
                    for (int i = 0; i < ms.Length; i++) if (ms[i].Name == member) return true;
                    // **嵌套类型也算"成员"**：EX 反射的 ScoreKeeper.scoreInfo 就是一个 private struct
                    //  （**嵌套类型**，不是字段）。只查字段/方法会把它误判成"失效",
                    //  日志里报 ✗、功能其实是好的，属于**误报**，会把真正的失效淹掉。
                    //  GetNestedType 不接受 DeclaredOnly（对嵌套类型无意义），只按可见性查。
                    if (t.GetNestedType(member, BindingFlags.Public | BindingFlags.NonPublic) != null) return true;
                }
            } catch (Exception __ex) { Guard.Log("RefExists", __ex); }
            return false;
        }

        //嵌套成员路径的解析（a.b.c）：逐段下钻，最后一段按字段/方法/嵌套类型判存在性。
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
                    if (i == parts.Length - 1) return true;          //最后一段存在即通过
                    if (nested != null) { cur = nested; continue; }  //嵌套类型：直接下钻
                    FieldInfo fi = null;
                    for (Type t = cur; t != null && fi == null; t = t.BaseType) fi = t.GetField(parts[i], F);
                    if (fi == null) return false;
                    cur = fi.FieldType;                              //字段：取它的类型继续下钻
                }
            } catch (Exception __ex) { Guard.Log("RefExistsPath", __ex); }
            return false;
        }

        //把各功能自己声明的反射成员逐个解析一遍，日志给出可用 N / 失效 M总览。
        //没有这一步时，失效成员只在对应功能被真正用到时才告警（可能一直没提示）。
        //（旧版还顺带建出 [Reflection] 段的"成员名覆盖"项，那套探针已随 #3 删除，不再建。）
        private static bool _refAudited;
        public static void RefAudit() {
            if (_refAudited) return;
            _refAudited = true;
            //补丁侧自检与反射探针自检一起做（同一时刻：各功能已 Initialize、补丁已打）
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
                        //成员名一律用内置默认名（[Reflection] 段的"成员名覆盖"探针已随 #3 删除）
                        found = RefExists(owner, member);
                    }
                    if (found) ok++;
                    else { failed.Add(kv.Key + "（" + kv.Value + "）");
                           _auditIssues.Add(new AuditIssue { Kind = "反射", Target = kv.Key, Scope = kv.Value }); }
                }
                AuditRefTotal = _refFeature.Count; AuditRefOk = ok;
                AuditLog("[SR.Ref] 反射成员 " + _refFeature.Count + " 个：可用 " + ok + "，失效 " + failed.Count);
                for (int i = 0; i < failed.Count; i++) {
                    AuditWarn("[SR.Ref]   ✗ " + failed[i]
                        + " -> 该功能已禁用。游戏更新导致改名时，请等新版模组适配（旧的在 cfg [Reflection] 段填新成员名能力已删除，照着做是无效的）。");
                }
            } catch (Exception __ex) { Guard.Log("SR.Ref 启动自检", __ex); }
        }

        // 补丁侧自检：把"按名绑定"的 43 个补丁目标也纳入启动检查
        //为什么需要：补丁全按"类型名 + 方法名"绑定，游戏更新改名后**编译不报错**，Harmony 只在自己
        //的日志里安静跳过 -> 功能"半死"（开关还在、行为没了），用户完全看不出来。
        //做法零侵入：反射遍历 SR 自己与"引用了 SR_UCH 的功能 DLL"的程序集，读每条 [HarmonyPatch]
        //特性（类级与方法级合并），逐个验证目标方法是否还存在, **不需要改任何一条补丁写法**。
        private static bool _patchAudited;
        public static int PatchAuditBad;   //失效的补丁目标数（供 UI 提示"某些功能不可用"）

        //按名绑定的目标是否仍然存在（decl/name 取不到 = 无法按名验证 -> 一律当作"没问题"，不误报）
        private static bool PatchTargetExists(Type decl, string name, Type[] args) {
            if (decl == null || string.IsNullOrEmpty(name)) return true;
            try {
                if (args != null && args.Length > 0) return AccessTools.Method(decl, name, args) != null;
                //没给参数类型：按名字找（容忍重载）
                MethodInfo[] ms = decl.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Static | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++) if (ms[i].Name == name) return true;
                return false;
            } catch { return false; }
        }

        //从一条 [HarmonyPatch] 特性里取类型 / 方法名 / 参数表；类级与方法级要合并成一份
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
                            //只审计 SR 自己与"引用了 SR_UCH 的功能 DLL"；其它插件由它们自己的日志负责
                            bool managed = false;
                            try {
                                AssemblyName an = asm.GetName();
                                if (an != null && an.Name == "SR_UCH_EX") managed = true;
                                else foreach (AssemblyName rn in asm.GetReferencedAssemblies()) { if (rn.Name == "SR_UCH") { managed = true; break; } }
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
                                    if (decl == null || string.IsNullOrEmpty(name)) continue;   //无法按名验证：跳过，不误报
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
            AuditLog("[SR.Patch] 按名绑定的补丁目标 " + total + " 个：可用 " + ok + "，失效 " + failed.Count);
            for (int i = 0; i < failed.Count; i++) AuditWarn("[SR.Patch]   ✗ " + failed[i]);
            if (failed.Count > 0) {
                AuditWarn("[SR.Patch] 以上补丁目标已不存在 -> 对应功能不会生效"
                    + "（表现为开关还在、行为没了）。游戏更新后请核对方法名/签名。");
            }
        }
        // 功能健康：自检结果登记
        //地基的可诊断落点：自检不只要打日志，还要能被**界面**读到（"功能健康可见"）。
        //这里只是登记，渲染在 SR.Settings 的自检块与首页提示里。
        //自检/诊断用的安全日志：ModLogger 在极早期可能还没就绪（也便于离线跑自检）。
        private static void AuditLog(string msg) { try { if (MainPlugin.ModLogger != null) SR.LogInfo(msg); } catch (Exception __ex) { Guard.Log("AuditLog", __ex); } }
        private static void AuditWarn(string msg) { try { if (MainPlugin.ModLogger != null) SR.LogWarn(msg); } catch (Exception __ex) { Guard.Log("AuditWarn", __ex); } }

        public sealed class AuditIssue {
            public string Kind;    // "补丁" / "反射"
            public string Target;  // "Character.setupDeath" / "ChatDisplay.ChatHolder"
            public string Scope;   // 出问题的宿主（补丁=类名；反射=该成员支撑的功能描述）
        }
        private static readonly List<AuditIssue> _auditIssues = new List<AuditIssue>();
        public static IList<AuditIssue> AuditIssues { get { return _auditIssues; } }
        public static int AuditIssueCount { get { return _auditIssues.Count; } }
        public static int AuditPatchTotal, AuditPatchOk, AuditRefTotal, AuditRefOk;
        private static bool _startupAudited;

        //一次性启动自检：补丁目标 + 反射成员。幂等（界面里重复调无害）。
        //由 MainPlugin.Awake 末尾调用 -> 不开面板也会跑，结论一定出现在日志里。
        public static void RunStartupAudit() {
            if (_startupAudited) return;
            _startupAudited = true;
            try { PatchAudit(); } catch (Exception __ex) { Guard.Log("补丁目标自检", __ex); }
            try { RefAudit(); } catch (Exception __ex) { Guard.Log("反射成员自检", __ex); }
        }

        //自检是否有问题（首页/设置页据此决定要不要显眼提示）
        public static bool AuditHasIssues { get { return _auditIssues.Count > 0; } }
        // 调试模式（由 EX 高级页开关，核心只提供信息）
        //设计：开关状态在核心（关掉 EX 也能保留设置），信息也在核心生成，EX 只负责画一个复选框。

        //调试信息：每行独立 try, 任何一项取不到都只影响那一行，不会让整块失效
        
        //本 Mod 总开关：关闭时所有内部功能运行时失效，各功能开关值不变；初始默认关闭，改动自动保存
        public static bool AllEnabled = false;
        private static bool _appliedDisabled;
        //组合键修饰：自定义键位统一走这里，捕捉时按住的修饰键存为该键位的修饰键；
        //持久化格式 "Shift+Ctrl+Alt"（见 SR.KeyBinds 的 ParseComboMod/FormatComboMod）。
        [Flags]
        public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _keyMods = new Dictionary<ConfigEntryBase, ComboMod>();
        private static readonly Dictionary<ConfigEntryBase, ConfigEntry<string>> _keyModEntries = new Dictionary<ConfigEntryBase, ConfigEntry<string>>();
        private static bool _visible;
        private static float _uiAlpha; //open/close fade
        private static bool _scanned;
        private enum Mode { Internal, Settings, External }
        //internal = SR_UCH 栏目，settings = 独立设置页，external = 其它插件
        private static ConfigFile _internalConfig;
        // "扫描时没找到自己的 GUID"只告警一次（见 EnsureScanned 末尾）
        private static bool _warnedNoSelfGuid;
        //配置落盘节流状态（SaveOnConfigSet 已关闭，见 EnsureScanned）
        private static bool _cfgDirty;
        private static float _cfgNextSaveAt;

        private static void OnAnySettingChanged(object s, SettingChangedEventArgs e) { _cfgDirty = true; }

        //把"这个配置该落盘了"统一交给节流器，而不是当场写磁盘：
        //  · SR 自己的配置：记 _cfgDirty，由 FlushConfig 每秒最多写一次；
        //  · 其它插件的配置（外部插件页那条路径）：走 _dirtyConfig（Tick 里松手即写，仍不是每帧写盘）。
        internal static void MarkConfigDirty(ConfigFile cfg) {
            if (cfg == null) return;
            if (ReferenceEquals(cfg, _internalConfig)) { _cfgDirty = true; return; }
            _dirtyConfig = cfg;
            _dirty = true;
        }

        //改过的配置最多每秒落盘一次（滚轮/滑块连续改值只写一次盘）；
        //关面板、退出前的显式 Save 仍是即时落盘，所以不会丢值。
        public static void FlushConfig() {
            if (!_cfgDirty || _internalConfig == null) return;
            if (Time.unscaledTime < _cfgNextSaveAt) return;
            _cfgNextSaveAt = Time.unscaledTime + 1f;
            //保存成功才清脏标记：Guard.Try 会吞掉异常，若 Save 失败却已清脏，这一批改动会被
            //当成"已落盘"而永久丢失（日志里只有一条可能被 5 秒去重吞掉的 Warning）。
            //保持脏则下一秒自动重试，直到成功。
            bool saved = Guard.Try("配置定期落盘", () => { _internalConfig.Save(); return true; }, false);
            if (saved) _cfgDirty = false;
        }
        private static readonly List<string> _internalSections = new List<string>();
        // #3 全部栏目（含被用户隐藏的）,  给自定义栏目的表头当列用；_internalSections 只含可见的。
        internal static readonly List<string> _allSections = new List<string>();
        // #3 用户隐藏的栏目（与 NavHide 的 _navHidden 分开：那是"永不显示"，这是用户可随时还原的）
        internal static readonly HashSet<string> _userHiddenSections = new HashSet<string>();
        //侧栏栏目自注册表（各功能文件在自己的 SelfReg 里声明；见 SR.Nav / SR.NavHide）
        private static readonly List<string> _navOrder = new List<string>();                     //注册先后
        private static readonly Dictionary<string, int> _navRank = new Dictionary<string, int>(); //顺序值（越小越靠前）
        private static readonly HashSet<string> _navHidden = new HashSet<string>();              //有 cfg 段但不单独成栏目
        private static string _selectedInternalSection = "";
        private static Mode _mode = Mode.Internal;
        private static readonly List<PluginEntry> _externalPlugins = new List<PluginEntry>();
        private static string _pluginKey = "";
        private static Vector2 _scroll;
        //文本测量缓存（控制台打开时避免每帧对所有条目 CalcSize/CalcHeight，减少掉帧）
        private static int _nameWKey;   // **R411**：从拼接长字符串改为哈希折叠（见 EntryNameWidth）
        private static float _nameWCached;
        private static readonly Dictionary<string, List<float[]>> _heightCache = new Dictionary<string, List<float[]>>();   // **R411**：两级键（文本 -> [宽,缩放,高]），免每帧拼 key
        //打开面板/地图时是否冻结自己的角色（外部模块页"冻结角色"开关控制；默认关 = 游戏照常运行）
        private static bool _pauseApplied;
        private static float _pauseSavedTs = 1f;
        private static Vector2 _leftScroll;
        // 通用扩展点（槽位）：核心不认识任何功能
        //原则：SR 只提供框架级扩展点，槽位名与形状由功能自己决定；核心不定义任何功能契约类，
        //也不为具体功能提供便利属性, 新增一个功能不需要改动 SR 的任何文件。
        //
        //1) 键值槽位：提供方自己 Provide，使用方自己 Require。
        //   双方只约定"槽位名 + 委托签名"，不强类型耦合（功能 A 不需要引用功能 B 的类型）。
        //（槽位表已移到 SR.Contracts.cs 的 Slots 类）
        //薄转发：槽位的真正实现在契约文件 SR.Contracts.cs 的 Slots 里（那才是公共 API）。
        public static void Provide(string slot, object impl) { Slots.Provide(slot, impl); }
        public static T Require<T>(string slot) where T : class {
            return Slots.Require<T>(slot);
        }

        //2) 功能 UI 是否打开：SR 据此冻结输入 / 决定是否绘制覆盖层。未注册 = 没有任何功能 UI。
        // 当前**无人注册**（地图窗口随 Freeplay 重构移除；自由相机没有自己的窗口），
        //  所以 AnyUiBlocked() 恒 false，三个消费点（SR.Window:34 / SR.Input:137 / SR.Window:221）
        //  都走"未打开"分支, **这是预期行为，不是 bug**。
        // 但要注意：总开关关闭时的收尾**不能依赖这里**。各功能必须在自己的每帧回调里判
        //  SR.GateMaster 并还原（LevelBoundsUnlock.Tick / FreeCam.ApplyToCamera 就是这么做的，
        //  见 Experiments.cs 的两处）。保留本 API 供将来带独立窗口的功能使用。
        private static readonly List<Func<bool>> _uiBlockProviders = new List<Func<bool>>();
        public static void RegisterUiBlockProvider(Func<bool> isOpen) {
            if (isOpen != null && !_uiBlockProviders.Contains(isOpen)) _uiBlockProviders.Add(isOpen);
        }
        public static bool AnyUiBlocked() {
            for (int i = 0; i < _uiBlockProviders.Count; i++) {
                try { if (_uiBlockProviders[i]()) return true; }
                catch (Exception __ex) { Guard.Log("查询功能 UI 状态", __ex); }
            }
            return false;
        }

        //3) 覆盖层绘制：SR 主体绘制完成后调用（地图窗口等）。各功能自己判断当前该不该画。
        private static readonly List<Action> _overlays = new List<Action>();
        public static void RegisterOverlay(Action draw) {
            if (draw != null && !_overlays.Contains(draw)) _overlays.Add(draw);
        }
        public static void DrawOverlays() {
            for (int i = 0; i < _overlays.Count; i++) {
                try { _overlays[i](); } catch (Exception __ex) { Guard.Log("绘制功能覆盖层", __ex); }
            }
        }

        //4) 相机视图应用（每帧取景点）：各功能自己判断当前是否该生效（地图 / 自由相机）。
        private static readonly List<Action<Camera>> _camViews = new List<Action<Camera>>();
        public static void RegisterCameraView(Action<Camera> apply) {
            if (apply != null && !_camViews.Contains(apply)) _camViews.Add(apply);
        }
        public static void ApplyCameraViews(Camera cam) {
            if (cam == null) return;
            for (int i = 0; i < _camViews.Count; i++) {
                try { _camViews[i](cam); } catch (Exception __ex) { Guard.Log("应用相机视图", __ex); }
            }
        }

        //5) 场景切换通知：功能自己清理缓存 / 复位状态（原来由核心逐个调 InvalidateMapBounds 等）。
        private static readonly List<Action> _sceneHooks = new List<Action>();
        public static void RegisterSceneHook(Action onSceneChanged) {
            if (onSceneChanged != null && !_sceneHooks.Contains(onSceneChanged)) _sceneHooks.Add(onSceneChanged);
        }
        public static void FireSceneChanged() {
            for (int i = 0; i < _sceneHooks.Count; i++) {
                try { _sceneHooks[i](); } catch (Exception __ex) { Guard.Log("场景切换通知", __ex); }
            }
        }

        //6) 收起各功能自己的 UI（总开关关闭 / 对局开始 / 换场景）。
        private static readonly List<Action> _dismissHooks = new List<Action>();
        public static void RegisterDismissHook(Action dismiss) {
            if (dismiss != null && !_dismissHooks.Contains(dismiss)) _dismissHooks.Add(dismiss);
        }
        public static void FireDismissUi() {
            for (int i = 0; i < _dismissHooks.Count; i++) {
                try { _dismissHooks[i](); } catch (Exception __ex) { Guard.Log("收起功能 UI", __ex); }
            }
        }

        //7) 滚轮分发：返回 true 表示该功能已消费这次滚轮（SR 据此 MarkWheelUsed）。
        private static readonly List<Func<float, bool>> _wheelHooks = new List<Func<float, bool>>();
        public static void RegisterWheelHook(Func<float, bool> onWheel) {
            if (onWheel != null && !_wheelHooks.Contains(onWheel)) _wheelHooks.Add(onWheel);
        }
        public static bool FireWheel(float wheel) {
            bool used = false;
            for (int i = 0; i < _wheelHooks.Count; i++) {
                try { if (_wheelHooks[i](wheel)) used = true; }
                catch (Exception __ex) { Guard.Log("滚轮分发", __ex); }
            }
            return used;
        }

        private static bool _winCollapsed; //main window folded to just the title bar
        private static ConfigEntryBase _capturing; //key capture target
        //捕捉起始帧：右键进入捕捉的同一次 MouseDown 不能被"按下即取消捕捉"清掉，
        //否则捕捉在同一事件里开始又立刻结束（右键永远绑不上）。
        private static int _captureStartFrame = -1;
        private static int _rmbConsumedFrame = -1; //当帧右键绑键：一帧只认一个按钮
        private static object _prevBoxed;
        //快捷键录制态：收集所有按键及其顺序（见 SR.Window 的录制分支 / CommitRecording）
        private static readonly List<KeyCode> _recSeq = new List<KeyCode>();
        private static readonly List<KeyCode> _recHeld = new List<KeyCode>(); //录制中仍按住的键（全部松开即完成录制）
        private static ConfigFile _dirtyConfig;
        private static readonly Dictionary<ConfigEntryBase, string> _editText = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, bool> _editOpen = new Dictionary<ConfigEntryBase, bool>();
        //window geometry
        private static int _winWidth = 720;
        private static int _winHeight = 520;
        private static float _winX = 30f;
        private static float _winY = 30f;
        //窗口几何配置条目（在 Initialize 里建一次；SR.Window 拖动/关闭时写它们，不再每次 Bind）
        private static ConfigEntry<int> _winWidthEntry, _winHeightEntry;
        private static ConfigEntry<float> _winXEntry, _winYEntry;
        private static bool _dragActive, _dragMoved, _resizing;
        private static Vector2 _dragOffset, _downPos, _resizeStart, _resizeStartSize;
        private static bool _dirty;
        //缩放同时作用于布局尺寸与字号（不使用矩阵缩放）
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
        private static Texture2D _cursorResizeTex; //悬停栏目分隔线/列分隔线时的左右调整光标
        private static Texture2D _cursorMoveTex;   //悬停可拖动单元格时的四向移动光标
        public static bool CursorResize;          //本帧是否显示调整光标（命中检测时置位，Tick 复位）
        public static bool CursorMove;            //本帧是否显示移动光标
        private static readonly List<GUIStyle> _styleList = new List<GUIStyle>();

        //管理器窗口是否真正打开（折叠 = 未打开，输入不受限）
        public static bool UiOpen { get { return _visible && !_winCollapsed; } }
        //（是否有功能 UI 打开 = SR.AnyUiBlocked()，由功能自己注册；打开时输入冻结逻辑见 SR.Input）
        //freeze game input while the manager is open
        public static bool BlockInput = true;

        //8) 门控豁免登记：**任何**模块都可以登记一条"我要求放开某项限制"的取值委托。
        //   SR 不认识"EX"，只认识"有人要求豁免模式限制 / 房主限制 / 要求冻结角色"。
        //   未登记 = 不豁免（保持原版行为）；多个模块同时登记时"任一为真即豁免"。
        //   （这里刻意不用 SetExToggles 这类带模块名的入口：那是"为具体功能留的口子"，
        //    每来一个新模块就得改一次核心, 改成登记制后，新增模块不需要动 SR 任何文件。）
        private static readonly List<Func<bool>> _modeOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _hostOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _pauseProviders = new List<Func<bool>>();

        //登记"无视模式限制"豁免（原 EX无视模式限制开关走这里）
        public static void RegisterModeOverride(Func<bool> f) {
            if (f != null && !_modeOverrides.Contains(f)) _modeOverrides.Add(f);
        }
        //登记"无视房主限制"豁免（原 EX无视房主限制开关走这里）
        public static void RegisterHostOverride(Func<bool> f) {
            if (f != null && !_hostOverrides.Contains(f)) _hostOverrides.Add(f);
        }
        //登记"冻结角色"求值（打开面板/地图时冻结自己的角色）
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
        //无视模式限制：所有"仅自由模式"的门控（视野/地图/重生/关卡等）都读它
        public static bool IgnoreModeLimit { get { return AnyTrue(_modeOverrides); } }
        //无视房主限制：门控 GateHost 的 OverrideHost 读它
        public static bool HostOverride { get { return AnyTrue(_hostOverrides); } }
        //冻结角色：打开面板/地图时冻结自己的角色
        public static bool PauseGame { get { return AnyTrue(_pauseProviders); } }

        //统一提示：游戏原生 UserMessage（屏幕上那行字）+ BepInEx 日志。
        //以前这个入口叫 Experiments.NotifyExp（挂在实验模块上），但它是**全模块共用的**提示通道，
        //成熟功能（联机等）不该绕道实验模块 -> 现在统一走这里；NotifyExp 只是转发，老调用点不受影响。
        //设计（按"统一"目标演进）：
        //  Notify(文本)              纯文本
        //  Notify(文本, 值...)          文本 + 值（避免调用方手工拼串）
        //  NotifyT(中文, English, 值...) 双语 + 值（把翻译留在调用点上，而不是拼好的字符串里）
        //  NotifyTag(来源, 文本)      指定日志来源标记（EX 等扩展模块转发用）
        //实现只有 NotifyImpl 一份：扩展模块只需转发，不会再出现"复制一份 UserMessage"的情况。
        public static void Notify(string text) { NotifyImpl(text, null); }
        public static void NotifyTag(string tag, string text) { NotifyImpl(text, tag); }
        public static void Notify(string text, params object[] args) { NotifyImpl(Cat(text, args), null); }
        public static void NotifyT(string zh, string en, params object[] args) { NotifyImpl(Cat(T(zh, en), args), null); }

        //值拼接版取词（与 NotifyT 同构）：把翻译与值都留在参数里，
        //而不是写成 SR.T("中文","English") + 值, 后者一旦要调语序/改文案，得在拼串里翻找。
        public static string TF(string zh, string en, params object[] args) { return Cat(T(zh, en), args); }
        //值拼接：null 视作空串（避免出现 "null"），其余走 ToString
        //公开：扩展模块（EX）拼提示文本也用它，避免各自再写一份 null 处理的拼接。
        public static string Cat(string text, object[] args) {
            if (args == null || args.Length == 0) return text;
            StringBuilder sb = new StringBuilder(text != null ? text : "");
            for (int i = 0; i < args.Length; i++) sb.Append(args[i] != null ? args[i].ToString() : "");
            return sb.ToString();
        }

        // **日志门面**：主程序集内部不再裸调 MainPlugin.ModLogger,
        //  1) 契约上应走 IFeatureHost.Logger（外部模块）/ 核心门面（内部）；
        //  2) 裸调在极早期可能为 null，且在 catch 里裸调会二次抛 NRE 把真因吞掉（P0 的成因）；
        //  3) 取不到就静默：宁可少一条日志，也不能让日志本身成为新的崩溃点。
        //  参数用 object（与 BepInEx ILogger.LogInfo(object) 一致）：调用点会传异常对象等非字符串。
        public static void LogInfo(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogInfo(m); } catch { } }
        public static void LogWarn(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogWarning(m); } catch { } }
        public static void LogError(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogError(m); } catch { } }

        //**屏幕合并**：游戏那个 MessageComponent 只有**一个 Text、没有队列**（后发直接覆盖先发），
        //所以"一次操作连报几条 / 快捷键连点"时玩家只看得到最后一条。这里在 0.35 秒窗口内把多条合成
        //多行一起显示（最多 3 行，再多就写"等 N 条"），窗口外仍是单条即时显示。
        //只影响屏幕：日志每条都单独写，不受合并影响。
        private static readonly List<string> _tipBatch = new List<string>();
        private static float _tipBatchUntil;

        //**通道降级**：某些场景游戏压根没实例化消息 UI（例：Savefile Viewer —— UserMessageManager.Start()
        //  显式跳过 Instantiate，于是 Instance 非空但内部 UIMsg 为 null，每次调用都抛空引用）。
        //  已知不可用时只写日志、不再逐帧抛异常；场景一换自动重试恢复。
        private static bool _tipDown;
        private static string _tipDownScene;

        private static void NotifyImpl(string text, string tag) {
            try { ShowUserMessage(text); } catch (Exception __ex) { Guard.Log("提示(UserMessage)", __ex); }
            try { SR.LogInfo("[" + (string.IsNullOrEmpty(tag) ? "SR" : tag) + "] " + text); } catch (Exception __ex) { Guard.Log("NotifyImpl", __ex); }
        }

        //把提示送到屏幕：短时合并 + 通道不可用时降级（只记日志，由调用方负责写）。
        private static void ShowUserMessage(string text) {
            if (string.IsNullOrEmpty(text)) return;
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            if (_tipDown && scene == _tipDownScene) return;   //本场景已知不可用：跳过（日志照常）
            string show = text;
            try {
                float now = Time.unscaledTime;
                if (now <= _tipBatchUntil) {
                    //同一次操作连报几条：合并成多行，避免互相顶掉
                    if (!_tipBatch.Contains(text)) _tipBatch.Add(text);
                    if (_tipBatch.Count > 3) {
                        show = _tipBatch[0] + "\n" + _tipBatch[1] + "\n" + _tipBatch[2]
                             + "\n…等 " + _tipBatch.Count + " 条";
                    } else {
                        show = string.Join("\n", _tipBatch.ToArray());
                    }
                } else {
                    _tipBatch.Clear();
                    _tipBatch.Add(text);
                }
                _tipBatchUntil = now + 0.35f;
            } catch { show = text; }   //拿不到时间就退回单条
            try {
                UserMessageManager.Instance.UserMessage(show, false);
                if (_tipDown) { _tipDown = false; _tipDownScene = null; }   //调通了说明已恢复
            } catch (Exception __ex) {
                _tipDown = true; _tipDownScene = scene;
                Guard.Log("提示(UserMessage)", __ex);
            }
        }

        //共用提示文案（高频消息只生成一次）：原来挂在 Experiments.Msgs 上，但"总开关关了/不在大厅/找不到玩家"
        //这类提示是全模块共用的，成熟功能不该绕道实验模块 -> 现在放核心，各功能直接用 SR.Msg。
        public static class Msg {
            private static string _masterOff, _notLobby;
            private static int _msgLangVer = -1;   //缓存生成时的语言版本（见 MsgSync）

            //原来这几句只在首次调用时生成一次，之后**永久缓存**：切界面语言（含切到外部语言包）
            //后界面全变了，这几条提示仍是旧语言（表现为"界面切成英文了，提示还是中文"）。
            //与 ZhKey / 实验页统计文本同一套做法：按语言版本号失效, 切语言、注册语言包都会 +1。
            private static void MsgSync() {
                if (_msgLangVer == LangVer) return;
                _msgLangVer = LangVer;
                _masterOff = null;
                _notLobby = null;
            }

            public static string MasterOff() {
                MsgSync();
                if (_masterOff == null) _masterOff = T("本 Mod 总开关已关闭", "The mod's master switch is off");
                return _masterOff;
            }
            public static string NotLobby() {
                MsgSync();
                if (_notLobby == null) _notLobby = T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player");
                return _notLobby;
            }
        }

        // 每帧钩子（功能自注册，SR.Tick 逐个 try/catch 调用）
        //为什么：Tick 里原来是**硬编码直调**（LevelTools.TickCleanup() / Online.CheckHotkeys()），
        //任一功能抛异常就会中断整段 Tick, 后面的配置落盘、UI 淡入、地图关闭全被停掉（一个功能拖垮全部）。
        //现在功能在自己的 Initialize 里 RegisterTick，SR 只按注册顺序逐个调、每个独立 try/catch：
        //  · 单个功能出错只记一条 Guard 日志（同名 5 秒去重），其余钩子与后续 Tick 逻辑照常跑；
        //  · 功能不需要被 SR 编译期引用（新功能/外部模块注册即生效，无需改 SR.Window）。
        //同一套机制还有"每帧收尾"钩子（RegisterFrameEnd）：DrawGUI 退出前调用，
        //给自绘页收尾用（典型：联机表格声明的系统光标形状要在整帧绘制结束后统一应用并复位）。
        private sealed class TickHook {
            public string Name;
            public Action Run;
        }
        private static readonly List<TickHook> _tickHooks = new List<TickHook>();
        private static readonly List<TickHook> _frameEndHooks = new List<TickHook>();

        // 按名去重（其余 10 个 Register 入口用的是"按委托 Contains"，这两个原来连 Contains 都没有）：
        //  匿名委托每次都是新实例，Contains 根本拦不住；而一旦有人把 RegisterTick 写进渲染路径，
        //  就会每帧 Add 一条 -> 列表无限增长 -> 表现为"开着面板越来越卡"。
        //  这里按名去重：同名 = 同一钩子的重新注册（覆盖 Run），与 RegisterPage 的同名覆盖语义一致。
        private static void AddOrReplaceHook(List<TickHook> list, string name, Action hook) {
            string n = string.IsNullOrEmpty(name) ? "未命名" : name;
            for (int i = 0; i < list.Count; i++) {
                TickHook h = list[i];
                if (h != null && string.Equals(h.Name, n, StringComparison.Ordinal)) { h.Run = hook; return; }
            }
            list.Add(new TickHook { Name = n, Run = hook });
        }

        public static void RegisterTick(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_tickHooks, name, hook);
        }

        public static void RegisterFrameEnd(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_frameEndHooks, name, hook);
        }

        //外部独立 BepInEx 插件形态的功能入口（带 [BepInPlugin] 的 DLL）。
        //MainPlugin 的扫描会跳过这类 DLL 里的 ITweak（BepInEx 已实例化过它的插件类，再 Initialize
        //一次会重复注册/重复打补丁），所以由插件在自己的 Awake 里调本方法把自己挂进 SR。
        //无入口的纯功能 DLL 不需要调, 扫描会自动发现并 Initialize。
        public static void RegisterTweak(ITweak tweak, IFeatureHost host) {
            if (tweak == null) return;
            try {
                tweak.Initialize(host);
            } catch (Exception __ex) {
                Guard.Log("外部插件注册功能 " + tweak.GetType().Name, __ex);
            }
        }

        private static void RunTickHooks() { RunHooks(_tickHooks, "每帧钩子:"); }

        //每帧收尾（面板关闭的早退分支也要调：否则光标形状会留在屏幕上）
        private static void RunFrameEndHooks() { RunHooks(_frameEndHooks, "每帧收尾:"); }

        private static void RunHooks(List<TickHook> hooks, string what) {
            for (int i = 0; i < hooks.Count; i++) {
                TickHook h = hooks[i];
                if (h == null || h.Run == null) continue;
                try { h.Run(); }
                catch (Exception __ex) { Guard.Log(what + h.Name, __ex); }
            }
        }

        //force-reset every UI state (used when a match starts so a stuck manager/map can
        //never hold up the snapshot-loading handshake for the whole lobby)
        public static void ForceResetUiState() {
            try {
                //对局开始：请各功能收起自己的 UI（地图窗口 / 自由相机等）。
                //不再由核心逐个点名, 功能自己用 RegisterDismissHook 声明"我该怎么收尾"。
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("对局开始时收起功能 UI", __ex); }
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _enumOptions.Clear();   // **与 _editText/_editOpen 同批失效**：配置重读后条目对象会重建，旧键会留成陈旧项
                _winCollapsed = false;
                ApplyEventSystemGate(); //re-enable the game's EventSystem
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("ForceResetUiState", __ex); }
            } catch (Exception __ex) { Guard.Log("对局开始处理", __ex); }
        }

        public void Initialize(IFeatureHost plugin) {
            _internalConfig = MainPlugin.Data != null ? MainPlugin.Data : plugin.Config; // **#4A 核心条目也走外置文件**
            //界面文案自注册（各功能文件的 section/键名/悬浮说明；本处只派发 SR 分部文件，
            //    实现了 ITweak 的功能在自己的 Initialize 里注册）
            SelfRegCore();
            //**必须在任何 Config.Bind 之前**把旧 cfg 的中文 section/key 改写成英文；
            //改写后要让 ConfigFile 重新读盘，否则 BepInEx 内存里还是旧（中文）键，
            //新建的英文条目会拿默认值，而且下次写盘会把文件又写回中文, 等于迁移白做。
            try {
                if (ConfigMigration.Migrate(plugin.Config.ConfigFilePath)) {
                    plugin.Config.Reload();
                    ClearEntryCache();   //键被改名/删掉了：条目缓存必须失效（FindInternalEntry / SectionEntries）
                    SR.LogInfo("[T1] 已迁移旧版 cfg（section/key -> 英文）并重新读盘");
                }
            } catch (Exception __ex) { Guard.Log("T1 cfg 迁移/重读", __ex); }
            //反射探针：**只做启动自检**（RefAudit 逐条解析各功能登记的成员并报可用/失效）。
            //**V7**：这里原先还写"绑定 [Reflection] 段的成员名覆盖项、用户可在 cfg 里填新成员名恢复"，
            //  那套探针已随 #3 整体删除（不再创建/读取 [Reflection] 段）—— 注释留着是误导，
            //  照着做完全无效。NavHide 只是把历史 cfg 里可能残留的该段从侧栏藏掉。
            try { NavHide("Reflection"); } catch (Exception __ex) { Guard.Log("Reflection 段隐藏", __ex); }
            _openKey = plugin.Config.Bind("Settings", "Open Key", KeyCode.Insert, "打开和关闭管理器用, 默认 Insert. 组合键的绑法: 点按钮, 按住 Shift 或 Ctrl 或 Alt, 再按主键.");
            RegisterKey("管理器-开关", _openKey, "press");
            RegisterFrameEnd("滑块残留状态清理", SliderFrameEndCleanup);   // P1 让 RegisterFrameEnd（原本零注册的扩展点）真正投入使用
            _blockInputEntry = plugin.Config.Bind("Settings", "Block Input", true, "管理器打开时冻结游戏输入, 免得误操作角色.");
            _uiScaleEntry = plugin.Config.Bind("Settings", "UI Scale", 1.3f, "整个界面放大多少, 1.0 到 1.8, 默认 1.3.");
            //界面语言：中文 / English（运行时立即生效；外部模块页面始终中文；默认英文）
            _langEntry = plugin.Config.Bind("Settings", "Language", "English", "界面用哪种语言. 装了语言包 DLL 的话, 它也会自动出现在这个下拉里. 外部插件的页面始终是中文.");
            //切语言 / 装语言包都走 ApplyLanguage：解析 id -> 内置中英文或外部语言包，并让显示名缓存失效
            _langEntry.SettingChanged += (s, e) => ApplyLanguage();
            ApplyLanguage();
            //地图总开关：默认关闭；开启后 M 键可打开地图，关闭时已打开的地图立即关闭。
            //地图网格（实验/Grid Always On）等独立功能不受本开关影响。
            //外部模块页的两个开关（无视模式限制 / 冻结角色）已随功能下放到外部模块（Bind 也在那边）
            //（本 Mod 总开关的 Bind 在下面对应的读取/订阅处，这里原来还有一次完全重复的 Bind，已删）
            //会话内容页的 3 个开关（Filter Quick Msgs / Hide Chat Window / Show Time）
            //已随功能下放到 Features/Chat.Log.cs（Bind 也在那边，第 8 条）
            //设置页栏目宽度：调节左侧栏目栏宽度（0 = 自动按文字宽度）。
            //也可直接在窗口里拖动侧栏右缘调整（拖动结果会写回本项，见 SR.Window）。
            _sidebarWEntry = plugin.Config.Bind("Settings", "Sidebar Width", 0, "左边那一栏有多宽, 单位像素, 填 0 让它自己算. 也可以直接在窗口里拖侧栏右边缘.");
            _sidebarOrderEntry = plugin.Config.Bind("Settings", "Sidebar Order", "", "侧栏栏目的先后顺序, 用逗号隔开. 在设置页的栏目顺序那一块用上移下移按钮调, 调完自动写回来.");
            // **#2 一次性清理历史污染**：早期版本在核心的 applyOrder 归一化调用里把"当时的显示顺序"
            //  落了盘（其中还经历过一版字母序兜底），于是 cfg 里留下一份**非用户自定义**的顺序值，
            //  它会永久压住默认 rank 顺序 -> 表现就是"默认排序无效"。
            //  用配置版本号只清一次；之后用户自己拖动的顺序照常保存。
            try {
                // **#6 挪到隐藏段**：这是内部版本号，不该出现在设置页（原来绑在 Settings 段 -> 多出一行显示）
                NavHide("Internal");
                ConfigEntry<int> __schema = plugin.Config.Bind("Internal", "Sidebar Order Schema", 0, "内部用: 侧栏顺序的存储版本, 用来一次性清掉历史遗留的脏值.");
                // **#4 升到 2**：v1 只清了"字母序"那一种污染，但还有另一种没清,
                //  栏目**改名**（Builder Enhancements -> build）后，老 cfg 的 Sidebar Order 里
                //  留的是旧段名。它匹配不到任何栏目 -> 变成占位死条目，改名后的新栏目被挤到末尾，
                //  表现就是"侧栏顺序乱了 / 默认排序看着没生效"。
                //  现在 ApplyCustomSidebarOrder 会自动改名映射 + 剔除死条目并回写（那边才是主修复），
                //  这里只负责把"残留旧段名"这一类整串清掉，让用户拿到干净的默认顺序。
                if (__schema.Value < 1) {
                    _sidebarOrderEntry.Value = "";
                    __schema.Value = 2;
                    SR.LogInfo("[SR] 已清理历史遗留的 Sidebar Order（恢复默认栏目顺序）");
                } else if (__schema.Value < 2) {
                    _sidebarOrderEntry.Value = "";
                    __schema.Value = 2;
                    SR.LogInfo("[SR] 已清理栏目改名残留的 Sidebar Order（恢复默认栏目顺序）");
                }
            } catch (Exception __ex) { Guard.Log("清理侧栏顺序历史值", __ex); }
            _sidebarW = _sidebarWEntry.Value;
            _sidebarWEntry.SettingChanged += (s, e) => _sidebarW = _sidebarWEntry.Value;
            //加载后清理已按 T5 迁到 Experiments.cs（section 同时改为 Experiments，界面位置不变）
            _allEnabledEntry = plugin.Config.Bind("Settings", "All Enabled", false, "整个 Mod 的总开关. 关掉以后所有内部功能都不生效, 但每个功能的开关值不会被改掉.\n默认是关的, 改动会自动保存, 下次启动保持上次的状态.");
            AllEnabled = _allEnabledEntry.Value;
            _allEnabledEntry.SettingChanged += (s, e) => AllEnabled = _allEnabledEntry.Value;
            //外部插件默认启用（不再默认禁用）；用户在外部栏手动禁用的 GUID 记入
            //本禁用列表（deny-list），重启后保持禁用；不在列表内的插件默认启用。
            _disabledPluginsEntry = plugin.Config.Bind("Settings", "Disabled Plugins", "", "不想让它跟着管理器一起出现的外部插件, 把 GUID 填进来, 用分号隔开. 没写进来的默认启用.");
            //设置页顶部有专门的插件清单（启用/禁用按钮），这个原始 GUID 字符串条目不再重复显示
            SR.RowFilters.Add(SettingsHiddenDisabledPlugins);
            //窗口宽高/XY 不再暴露为设置条目（右下角拖拽即可，见 SR.Window.ApplyScaleToWindow）
            SR.RowFilters.Add(SettingsHiddenWindowGeometry);
            BlockInput = _blockInputEntry.Value;
            _blockInputEntry.SettingChanged += (s, e) => {
                BlockInput = _blockInputEntry.Value;
                ApplyEventSystemGate();
            };
            //窗口几何条目：在这里建一次并缓存到静态字段（SR.Window 拖动时直接写缓存条目）。
            //原来 SR.Window 每个 MouseDrag 事件都重新 Bind 一次这四个 key（拖动时每秒几十次）,
            //BepInEx 对已存在条目只是字典查找，功能正确，但纯属白跑；也避免"同一 key 分散在多处 Bind"。
            //类型必须与原写法一致（宽高 int，X/Y float），否则 BepInEx 会按新类型建出另一个条目。
            _winWidthEntry = plugin.Config.Bind("Settings", "Window Width", 720, "");
            _winHeightEntry = plugin.Config.Bind("Settings", "Window Height", 520, "");
            _winXEntry = plugin.Config.Bind("Settings", "Window X", 30f, "");
            _winYEntry = plugin.Config.Bind("Settings", "Window Y", 30f, "");
            _winWidth = Mathf.Clamp(_winWidthEntry.Value, 400, 1600);
            _winHeight = Mathf.Clamp(_winHeightEntry.Value, 300, 1000);
            _winX = _winXEntry.Value;
            _winY = _winYEntry.Value;

            GameObject go = new GameObject("SR_UCHManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ManagerUI>();
            //scene switches: reset EVERY UI state so nothing leaks into the next scene.
            //a manager left open freezes characters (input block), stops GameControl
            //(countdown/power-ups freeze) and disables the EventSystem (no clicks) -
            //this used to happen when joining another player's treehouse mid-session.
            SceneManager.activeSceneChanged += (a, b) => {
                _stylesReady = false;
                //换场景：先通知各功能（清缓存 / 复位），再请它们收起自己的 UI。
                //原来核心逐个点名调 InvalidateMapBounds() / CloseMap() / CameraForceDisableLock()，
                //每加一个需要收尾的功能就得改这里, 现在功能自己注册，核心不认识任何功能。
                try { FireSceneChanged(); } catch (Exception __ex) { Guard.Log("换场景通知功能", __ex); }
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("换场景时收起功能 UI", __ex); }
                if (_visible) {
                    _visible = false;
                    _capturing = null;
                    _editText.Clear();
                    _editOpen.Clear();
                    _enumOptions.Clear();   // **与 _editText/_editOpen 同批失效**：配置重读后条目对象会重建，旧键会留成陈旧项
                    _winCollapsed = false;
                    ApplyEventSystemGate(); //re-enable the game's EventSystem
                }
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("SR.Core", __ex); }   //清掉滑块/滚轮盾的跨帧残留
            };
            //NOTE: EnsureScanned/ApplyDisabledPlugins are NOT called here - other tweaks
            //may initialize after us, so the scan runs on the manager's first Update frame
            try { Harmony.CreateAndPatchAll(typeof(SR)); } catch (Exception ex) { SR.LogError("SR 补丁注册失败: " + ex.Message); }
            //录键期间屏蔽游戏表情系统（嵌套类不会被上面的 CreateAndPatchAll 递归发现，需单独注册；失败不影响其它补丁）
            try { Harmony.CreateAndPatchAll(typeof(EmoteBlockPatch)); } catch (Exception ex) { SR.LogWarn("录键屏蔽表情系统 补丁注册失败: " + ex.Message); }
        }

        // scan: split SR_UCH (internal) from every other plugin (external)
        //侧栏栏目自注册, 每个功能声明自己的栏目与顺序（SR.Core 不再硬编码 order 数组，
        //也不再写 sec == "xxx" 的跳过分支）：
        //    SR.Nav("Player Tracker", 20);   // 出现在侧栏（顺序值越小越靠前）
        //    SR.NavHide("Camera");           // 有 cfg 段但条目并入别的页面，不单独成栏目
        //按注册表重建侧栏栏目表：注册顺序值排序 -> 兜底 cfg 段 -> 用户自定义顺序。
        //抽成独立方法的原因：晚注册（首次扫描之后，例如 BepInEx 加载顺序靠后的第三方功能）
        //必须能**立刻生效**，否则表现为"注册了但侧栏看不到"，且没有任何提示。
        private static void RebuildSections(bool resetSelection) {

            try {
                _internalSections.Clear();
                List<string> registered = new List<string>();
                foreach (string s in _navOrder) if (!_navHidden.Contains(s)) registered.Add(s);
                registered.Sort(delegate(string a, string b) {
                    int r = _navRank[a].CompareTo(_navRank[b]);
                    if (r != 0) return r;
                    return _navOrder.IndexOf(a).CompareTo(_navOrder.IndexOf(b)); //同顺序值：按注册先后
                });
                for (int i = 0; i < registered.Count; i++) {
                    if (!_internalSections.Contains(registered[i])) _internalSections.Add(registered[i]);
                }
                //兜底：既没注册也没 NavHide 的 cfg 段（附加模块新加的段 / 老 cfg 残留段）排在注册项之后
                if (_internalConfig != null) {
                    foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                        string sec = e.Definition.Section;
                        if (string.IsNullOrEmpty(sec)) sec = "(General)";
                        if (_navHidden.Contains(sec) || _navRank.ContainsKey(sec)) continue;
                        if (!_internalSections.Contains(sec)) _internalSections.Add(sec);
                    }
                }
                ApplyCustomSidebarOrder(); //侧栏自定义顺序（拖动栏目后排的）
                if (resetSelection && _internalSections.Count > 0) _selectedInternalSection = _internalSections[0];
                //当前选中项被移除 / 改名 -> 回到第一个，避免显示空白页
                if (_internalSections.Count > 0 && !_internalSections.Contains(_selectedInternalSection))
                    _selectedInternalSection = _internalSections[0];
            } catch (Exception __ex) { Guard.Log("重建侧栏栏目", __ex); }
        }

        //供自定义栏目在写完顺序后立刻生效（原 RebuildSections 为 private）
        public static void RebuildSectionsPublic(bool resetSelection) { RebuildSections(resetSelection); }

        // #2 隐藏状态**并入 Sidebar Order**（隐藏的栏目名带 "!" 前缀）,  单一真值。
        //  顺序与可见性本就是同一件东西（"这份列表"）；拆成两条配置会出现"顺序里有、隐藏里也有"的双份真值。
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
                RebuildSections(false);   //内已有"选中项被移除 -> 回到第一个"的兜底
            } catch (Exception __ex) { Guard.Log("隐藏侧栏栏目", __ex); }
        }
        public static void ClearHiddenSections() {
            try {
                if (_userHiddenSections.Count == 0) return;
                if (_sidebarOrderEntry != null) _sidebarOrderEntry.Value = string.Join(",", _allSections.ToArray());
                RebuildSections(false);
            } catch (Exception __ex) { Guard.Log("还原侧栏栏目", __ex); }
        }

        //晚注册的统一处理：首次扫描之后再注册栏目 -> 立刻重建（否则静默不显示）
        private static void OnNavChanged(string sec) {
            if (!_scanned) return;   //启动期注册：等 EnsureScanned 一次算完，不必每个都重建
            RebuildSections(false);
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("晚注册后重排 Configuration Manager", __ex); }
            if (!string.IsNullOrEmpty(sec)) AuditLog("[SR] 侧栏栏目在首次扫描后注册：" + sec + "（已即时生效）");
        }

        public static void Nav(string sec, int rank) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Remove(sec);
            if (!_navRank.ContainsKey(sec)) _navOrder.Add(sec);
            _navRank[sec] = rank;
            OnNavChanged(sec);
        }

// **合并补齐**：聊天输入状态（附件树的其它文件在用，其 SR.Core 里缺这两项）
        //正在打字登记（通用：任何有输入框的功能都能登记，SR 不认识具体是谁）。
        //改名自 SetChatTypingProvider：入口名带 "Chat" 等于在核心里写"聊天功能存在"，
        //  每来一个需要键位避让的输入框就得再加一个带功能名的入口, 与空壳化方向相反。
        //  改成通用登记后，多个来源任一为真即认为"正在打字"（键位轮询据此避让）。
        private static readonly List<Func<bool>> _typingProviders = new List<Func<bool>>();
        public static void RegisterTypingProvider(Func<bool> provider) {
            if (provider != null && !_typingProviders.Contains(provider)) _typingProviders.Add(provider);
        }
        //属性名也去掉了 Chat 前缀：原叫 ChatTyping，但"是否在打字"是通用状态
        //（将来任何带输入框的功能都能登记），核心不该在 API 名字里写"聊天"。
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
            bool foundSelf = false;   // **是否命中"自己的配置"**：没命中要告警（见方法末尾）
            foreach (var kv in Chainloader.PluginInfos) {
                PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                PluginEntry pe = new PluginEntry {
                    guid = kv.Key,
                    name = info.Metadata != null && info.Metadata.Name != null ? info.Metadata.Name : kv.Key,
                    config = info.Instance.Config,
                    instance = info.Instance
                };
                // **用 MainPlugin 的编译期常量，不再写字面量**：与 [BepInPlugin] 同源，改名漏改会编译报错。
                if (kv.Key == MainPlugin.PluginGuid) {
                    foundSelf = true;
                    _internalConfig = pe.config;
                    //启动期注册的功能键组合条目在这里才建得出来（此前 Bind 不了隐藏条目）-> 补建并读回 cfg
                    try { FlushPendingCombos(); } catch (Exception __ex) { Guard.Log("补建按键组合条目", __ex); }
                    //配置落盘改为统一节流：BepInEx 默认 SaveOnConfigSet=true（改一次值写一次盘），
                    //滚轮/滑块这类"每帧改值"的控件会变成每帧一次磁盘 IO。这里关掉自动写盘，
                    //改由 FlushConfig（每帧最多 1 秒落盘一次）+ 关面板 Save + 各功能显式 Save 负责。
                    try {
                        pe.config.SaveOnConfigSet = false;
                        pe.config.SettingChanged += OnAnySettingChanged;
                    } catch (Exception __ex) { Guard.Log("配置落盘节流初始化", __ex); }
                    //回填组合键持久化：各功能 Initialize 早于本扫描，运行时组合修饰
                    //可能已设置但未落盘（当时 _internalConfig 为 null）, 现在补写。
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
            if (_externalPlugins.Count > 0) _pluginKey = _externalPlugins[0].guid;
            ApplyDisabledPlugins(); //re-disable plugins from the last session
            ApplyCmOrder();         //让 Configuration Manager 的条目顺序与 SR 界面一致（侧栏顺序 + 栏目内绑定顺序）

            // **没命中"自己的 GUID"时告警**：上面那段（FlushPendingCombos / SaveOnConfigSet=false / 组合键回填）
            //  全都不会跑，而功能**看起来正常**, 因为 Initialize 已把 _internalConfig 赋成 MainPlugin.Data，
            //  恰好是正确值，把副作用掩盖了。真正暴露的只有两处、且都不报错：组合键全丢、配置退回每次写盘。
            //  （所以这里判 foundSelf 而不是 _internalConfig == null, 后者从来不为 null，那样写永远不告警。）
            if (!foundSelf && !_warnedNoSelfGuid) {
                _warnedNoSelfGuid = true;
                try {
                    SR.LogWarn("[SR] 扫描插件表时没找到自己的 GUID（" + MainPlugin.PluginGuid
                        + "）-> 组合键补建与配置落盘节流未启用。请检查 GUID 是否与 [BepInPlugin] 一致。");
                } catch { }
            }
        }

        private static void CloseMenu() {
            _visible = false;
            _capturing = null;
            var dc = _dirtyConfig;
            _dirtyConfig = null;
            _dirty = false;
            //关闭菜单时保存配置：失败记日志，不再静默/抛异常
            Guard.Try("关闭菜单时保存配置", () => { if (dc != null) dc.Save(); });
            ApplyEventSystemGate();
            try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("CloseMenu", __ex); }   //清掉滑块/滚轮盾的跨帧残留
        }

        //本 Mod 总开关在设置页不重复成行（搜索栏右侧已有专用按钮）
        private static bool AllEnabledRowHidden(ConfigEntryBase e) {
            return !(e.Definition.Section == "Settings" && e.Definition.Key == "All Enabled");
        }

        //管理器自己的界面文案与侧栏注册
        private static void SelfRegCore() {
            SR.RowFilters.Add(AllEnabledRowHidden); //设置页不重复显示本 Mod 总开关（搜索栏右侧有专用按钮）
            SR.LocSec("Settings", "设置", null);
            SR.NavHide("Settings"); //T7：设置页是独立顶层页（不占内部侧栏栏目）
            //已删除功能的段名：老 cfg 里可能还留着，不能让它出现在侧栏。
            //名单放在 ConfigMigration（删功能时本来就要在那加清理规则），核心不维护功能名单。
            for (int i = 0; i < ConfigMigration.DeletedSections.Length; i++)
                SR.NavHide(ConfigMigration.DeletedSections[i]);
            SR.LocSec("Home", "首页", null);
            SR.Nav("Home", 10); //T7：本功能自己的侧栏栏目（顺序 10）
            SR.RegisterPage("Home", RenderHomePage); //首页也走自绘页注册表（SR.Window 不再有 if-else 分派）
            //（自由模式段的文案随该功能移到 SR_UCH_EX 的 ExModule（原 Features\Freeplay.cs）,  核心只认注册 API，不认识任何功能）
            SR.LocKey("Settings", "UI Scale", "界面缩放", null);
            SR.LocDesc("Settings", "UI Scale", "整个界面放大多少. 1.0 是原始大小, 1.3 是默认值. 嫌字小就调大一点.", "UI zoom, default 1.3.");
            SR.LocKey("Settings", "Disabled Plugins", "禁用的外部插件", null);
            SR.LocDesc("Settings", "Disabled Plugins", "不想让它跟着管理器一起出现的外部插件, 把它的 GUID 填进来, 用分号隔开. 没写进来的默认都启用.", "External plugins to disable, as a semicolon-separated GUID list. Plugins not listed stay enabled.");
            SR.LocKey("Settings", "Open Key", "打开键", null);
            SR.LocDesc("Settings", "Open Key", "打开和关闭管理器用, 默认 Insert. 想绑组合键就点右边的框, 按住 Shift 或 Ctrl 再按主键.", "Open/close the manager (default Insert). Click the box to bind a combo key.");
            SR.LocKey("Settings", "Block Input", "冻结输入", null);
            SR.LocDesc("Settings", "Block Input", "开着的时候, 管理界面一打开就冻结游戏输入, 免得手一滑把角色弄跑了.", "Freeze game input while the manager is open, so you do not move your character by accident.");
            SR.LocKey("Settings", "Window Width", "窗口宽度", null);
            SR.LocDesc("Settings", "Window Width", "管理器窗口有多宽, 400 到 1600.", "Manager window width, 400 - 1600.");
            SR.LocKey("Settings", "Window Height", "窗口高度", null);
            SR.LocDesc("Settings", "Window Height", "管理器窗口有多高, 300 到 1000.", "Manager window height, 300 - 1000.");
            SR.LocKey("Settings", "Window X", "窗口X", null);
            SR.LocDesc("Settings", "Window X", "窗口左边离屏幕左边缘多远, 单位像素, 以屏幕左上角为原点.", "Window X in pixels, measured from the top-left corner of the screen.");
            SR.LocKey("Settings", "Window Y", "窗口Y", null);
            SR.LocDesc("Settings", "Window Y", "窗口上边离屏幕上边缘多远, 单位像素, 以屏幕左上角为原点.", "Window Y in pixels, measured from the top-left corner of the screen.");
            SR.LocKey("Settings", "Sidebar Width", "栏目宽度", null);
            SR.LocDesc("Settings", "Sidebar Width", "左边那一栏有多宽, 单位像素, 填 0 让它自己算, 范围 100 到 320. 也可以直接在窗口里拖侧栏右边缘.", "Left sidebar width in pixels, 0 = auto, 100 - 320. You can also drag the sidebar's right edge.");
            SR.LocKey("Settings", "Language", "界面语言", null);
        }

// 分区：Guard（可观测性：统一带日志的 try/catch 包装）
// 关键设计：同一条 what 5 秒内只记一次，故每帧路径上的 catch（相机、方块高亮等）也能安全接入而不刷屏日志。
// 反射探测型 catch（探测可选游戏成员，成员不存在是常态）不接入, 那是预期分支，不是错误。
        public static class Guard {
            // 执行一个动作；异常时记 Warning（带 what 标识），不抛出。
            public static void Try(string what, Action action) {
                try { action(); }
                catch (Exception ex) { Warn(what, ex); }
            }

            // 执行一个取值动作；异常时记 Warning 并返回 fallback。
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); }
                catch (Exception ex) { Warn(what, ex); return fallback; }
            }

            //供把 catch { } 改造成 catch (Exception __ex) { Guard.Log("...", __ex); } 用（不改动原有 try 结构）。
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            //去重时间窗（秒）
            private const float RepeatWindowSeconds = 5f;
            private static readonly Dictionary<string, float> _lastWarn = new Dictionary<string, float>();

            //单独小方法读 Unity 时间：Unity 原生调用不可用时，失败发生在**这个方法的 JIT 期**，
            //只有调用方的 try 抓得住, 而 Guard 的存在意义就是"绝不抛"，所以不能直接在 Warn 里调
            //Time.unscaledTime（否则 Warn 自己 JIT 失败，所有 catch { Guard.Log(...) } 都变成抛点）。
            private static float NowUnscaled() { try { return Time.unscaledTime; } catch { return 0f; } }

            //最近一次被 Guard 记录的失败：便于离线自检 / 问题上报（日志可能被去重或未及时看）。
            //不改动任何原有行为，纯记录。
            public static string LastWhat, LastError;
            public static int WarnCount;

            private static void Warn(string what, Exception ex) {
                try {
                    float now = NowUnscaled();
                    float last;
                    //now<=0（时间读不到）时**不做去重**：否则 last 永远是 0，会把后续日志全部吞掉
                    if (now > 0f && what != null && _lastWarn.TryGetValue(what, out last) && now - last < RepeatWindowSeconds) return;
                    if (now > 0f && what != null) _lastWarn[what] = now;
                    LastWhat = what;
                    LastError = ex != null ? (ex.GetType().Name + ": " + ex.Message) : "未知错误";
                    WarnCount++;
                    if (MainPlugin.ModLogger != null)
                        SR.LogWarn("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch (Exception __ex) {
                    // 原文是 Guard.Log("Warn", __ex), 而 Guard.Log 的实现就是 Warn(...)，
                    //  于是"Warn 里再抛 -> 又调 Warn -> 再抛..."无限递归 -> StackOverflow。
                    //  Guard 的设计契约是"绝不抛"（见上方注释），它自己的异常处理器更不能是抛点。
                    //  这里只把失败记进 LastError 供离线自检查看，不再回到 Warn。
                    try { LastWhat = what; LastError = "Warn 自身失败: " + __ex.GetType().Name; } catch { }
                }
            }
        }

	}
}
