// 入口（BepInEx 插件）：配置外置到 SR_UCH_data.cfg；反射发现并初始化全部 ITweak；扫描外部
// 功能 / 语言包 DLL；注册 SR_UCH 程序集解析。本文件定义统一契约 ITweak。
using System;
using UnityEngine;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using SR_UCH.Tweaks;

namespace SR_UCH {
    // 单一真值：插件声明与 SR.Core.EnsureScanned 必须比对同一个字符串，否则漏改不报错。
    // 用 const 是要求编译期内联；SR.ApiVersion 用 static readonly 恰恰相反（避免被外部模块内联）。
    [BepInPlugin(PluginGuid, "SR_UCH", "1.0.0")]
    // IFeatureHost 由契约文件提供；BaseUnityPlugin.Config / .Logger 已满足它。
    public class MainPlugin : BaseUnityPlugin, IFeatureHost {
        public const string PluginGuid = "com.rs.SR_UCH";

        public static ManualLogSource ModLogger;

        // IFeatureHost：Config 由 BaseUnityPlugin.Config 隐式满足；Logger 基类不是 public，显式实现。
        ManualLogSource IFeatureHost.Logger { get { return Logger; } }

        // 已处理过的外部程序集（按 FullName）：同一份 DLL 只初始化一次。
        private static readonly HashSet<string> _extSeen = new HashSet<string>();

        // 配置外置：所有 Bind 落到这个独立文件，插件自己的 cfg 保持为空，
        // ConfigurationManager 等配置浏览器就列不出 SR_UCH 的任何条目。
        public static BepInEx.Configuration.ConfigFile Data;

        public void Awake() {
            // ModLogger 必须是本方法第一条语句：下面的步骤与其 catch 内的日志都依赖它。
            ModLogger = Logger;
            // PatchOwnConfigFile 需显式注册（CreateAndPatchAll 不递归发现独立类型），失败只记日志。
            try { HarmonyLib.Harmony.CreateAndPatchAll(typeof(PatchOwnConfigFile)); }
            catch (Exception __pf) { try { ModLogger.LogWarning("配置外置补丁(PatchOwnConfigFile)注册失败: " + __pf.Message); } catch { } }

            // 独立配置文件（旧 cfg 自动迁移）
            try {
                string __dir = BepInEx.Paths.ConfigPath;
                string __newF = System.IO.Path.Combine(__dir, "SR_UCH_data.cfg");
                string __oldA = System.IO.Path.Combine(__dir, "com.rs.SR_UCH.cfg");
                string __oldB = System.IO.Path.Combine(__dir, "com.gamingbeast.SR_UCH.cfg");
                if (!System.IO.File.Exists(__newF)) {
                    if (System.IO.File.Exists(__oldA)) { System.IO.File.Copy(__oldA, __newF); ModLogger.LogInfo("[配置] 已迁移 com.rs.SR_UCH.cfg -> SR_UCH_data.cfg"); }
                    else if (System.IO.File.Exists(__oldB)) { System.IO.File.Copy(__oldB, __newF); ModLogger.LogInfo("[配置] 已迁移 com.gamingbeast.SR_UCH.cfg -> SR_UCH_data.cfg"); }
                }
                Data = new BepInEx.Configuration.ConfigFile(__newF, true);
                // 把插件 Config 的后备字段直接指向本文件：只补 get_Config 不够，BepInEx 自身保存路径也走这里。
                try {
                    System.Reflection.FieldInfo __cf = typeof(BepInEx.BaseUnityPlugin).GetField("<Config>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (__cf != null && Data != null) { __cf.SetValue(this, Data); ModLogger.LogInfo("[配置] 插件 Config 已指向 SR_UCH_data.cfg（外置生效）"); }
                    else ModLogger.LogWarning("[配置] 未找到 Config 后备字段，配置外置可能未生效");
                } catch (Exception __cfx) { try { ModLogger.LogWarning("配置外置(后备字段)失败: " + __cfx.Message); } catch { } }
            } catch (Exception __de) { try { ModLogger.LogWarning("配置外置失败: " + __de.Message); } catch { } }

            // 在任何 Bind 之前把旧 GUID 的 cfg 复制成新名字（GUID 改名导致文件名变化）。
            try {
                string __cfgDir = BepInEx.Paths.ConfigPath;
                string __oldCfg = System.IO.Path.Combine(__cfgDir, "com.gamingbeast.SR_UCH.cfg");
                string __newCfg = System.IO.Path.Combine(__cfgDir, "com.rs.SR_UCH.cfg");
                if (System.IO.File.Exists(__oldCfg) && !System.IO.File.Exists(__newCfg)) {
                    System.IO.File.Copy(__oldCfg, __newCfg, false);
                    ModLogger.LogInfo("[配置] 已从旧 GUID 配置迁移: com.gamingbeast.SR_UCH.cfg -> com.rs.SR_UCH.cfg");
                }
            } catch (Exception __mig) { try { ModLogger.LogWarning("配置迁移失败: " + __mig.Message); } catch { } }
            // 程序集解析：外部功能 / 语言包 DLL 只引用 SR_UCH.dll（不引用 BepInEx），全局注册一次。
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            // 契约层 API 门面的日志出口：接到本插件的日志系统。
            try {
                SRApi.SetLogSinks(
                    delegate (string __m) { SR.LogInfo(__m); },
                    delegate (string __m) { SR.LogWarn(__m); },
                    delegate (string __m) { SR.LogError(__m); });
                SRApi.SetNotifySink(delegate (string __t) { SR.Notify(__t); });
                SRApi.Ref.SetSinks(
                    delegate (Type __o, string __n, string __f) { return SR.RefField(__o, __n, __f); },
                    delegate (Type __o, string __n, string __f) { return SR.RefMethod(__o, __n, __f); },
                    delegate (Type __o, string __n, Type[] __p, string __f) { return SR.RefMethod(__o, __n, __p, __f); },
                    delegate (string __m, string __f) { SR.RefOverride(__m, __f); },
                    null);   // SR.RefType 是核心私有，功能层用不到（唯一一处出现在注释里）
                SRApi.SetExtSinks(
                    delegate (string __s, int __r) { SR.Nav(__s, __r); },
                    delegate (string __s) { SR.NavHide(__s); },
                    delegate (string __n, Action __a) { SR.RegisterTick(__n, __a); },
                    delegate (Action __a) { SR.RegisterSceneHook(__a); },
                    delegate (Action __a) { SR.RegisterDismissHook(__a); },
                    delegate (Func<float, bool> __w) { SR.RegisterWheelHook(__w); },
                    delegate (string __i, string __l, Action __a) { SR.HotkeyAction(__i, __l, __a); });
                SRApi.SetStateSinks(
                    delegate { return SR.HasServer; },
                    delegate { return SR.GateMaster; },
                    delegate { return SR.GateHost; },
                    delegate { return SR.CanControlLocally; },
                    delegate { return SR.CanDoHost(); },
                    delegate { return SR.UiOpen; },
                    delegate { return SR.BlockInput; },
                    delegate { return SR.BlockWindowDrag; },
                    delegate { return SR.WheelFrame; },
                    delegate { return SR.TakeWheelEvent(); },
                    delegate { SR.MarkWheelUsed(); },
                    delegate { SR.ForceResetUiState(); },
                    delegate { SR.FlushConfig(); },
                    delegate (string __s) { return SR.SelectedPageId(__s); },
                    delegate (bool __v) { SR.BlockWindowDrag = __v; },
                    delegate (int __v) { SR.WheelFrame = __v; });
                SRApi.SetLangSinks(
                    delegate (string __z, string __e) { return SR.T(__z, __e); },
                    delegate (string __s, string __z) { SR.LocSec(__s, __z); },
                    delegate (string __s, string __z, string __e) { SR.LocSec(__s, __z, __e); },
                    delegate (string __s, string __k, string __z, string __e) { SR.LocKey(__s, __k, __z, __e); },
                    delegate (string __s, string __k, string __z, string __e) { SR.LocDesc(__s, __k, __z, __e); },
                    delegate (string __n, string __z) { SR.LocEnum(__n, __z); },
                    delegate { return SR.CurrentLanguageId; },
                    delegate { return SR.LangVer; },
                    delegate (string __z, string __e, object[] __a) { SR.NotifyT(__z, __e, __a); });
                SRApi.Ctl.SetSinks(
                    delegate (float __v) { return SR.Ctl.Sc(__v); },
                    delegate (GUIContent __c, ConfigEntryBase __e, float __w, float __h, ConfigEntry<KeyCode> __k) { SR.Ctl.RestoreLabel(__c, __e, __w, __h, __k); },
                    delegate (ConfigEntryBase __e) { SR.Ctl.RenderControl(__e); },
                    delegate (ConfigEntryBase __e, bool __i, float __c2) { SR.Ctl.RenderEntryRow(__e, __i, __c2); },
                    delegate (string __s, string __k) { return SR.Ctl.FindEntry(__s, __k); },
                    delegate (string __s) { return SR.Ctl.SectionEntries(__s); },
                    delegate (ConfigEntryBase __e, float __w) { SR.Ctl.DrawEntryRow(__e, __w); },
                    delegate (string __t2, float __w) { return SR.Ctl.TextHeight(__t2, __w); },
                    delegate (Rect __r, float __v, float __mn, float __mx, bool __i) { return SR.Ctl.DrawSlider(__r, __v, __mn, __mx, __i); },
                    delegate (ConfigEntryBase __e, string __cur, Array __vals, string[] __opts, ref bool __open, float __w) { return SR.Ctl.ComboBox(__e, __cur, __vals, __opts, ref __open, __w); },
                    delegate { return SR.Ctl.SidebarWidth(); },
                    delegate { return SR.Ctl.WinWidth; },
                    delegate (ConfigEntryBase __e, object __v) { SR.Ctl.SetValue(__e, __v); },
                    delegate (string __l, string __t2, ConfigEntry<KeyCode> __k, float __w, bool __b) { return SR.Ctl.HotkeyActionButton(__l, __t2, __k, __w, __b); },
                    delegate (string __id, string __l, string __t2, Action __a, float __w, float __h) { return SR.Ctl.HotkeyButton(__id, __l, __t2, __a, __w, __h); },
                    delegate (string __n) {
                        switch (__n) {
                            case "Label": return SR.Ctl.Label;
                            case "LabelWrap": return SR.Ctl.LabelWrap;
                            case "ChatLabel": return SR.Ctl.ChatLabel;
                            case "ChatArea": return SR.Ctl.ChatArea;
                            case "SecHeader": return SR.Ctl.SecHeader;
                            case "Btn": return SR.Ctl.Btn;
                            case "BtnClip": return SR.Ctl.BtnClip;
                            case "SearchBox": return SR.Ctl.SearchBox;
                            case "Footer": return SR.Ctl.Footer;
                        }
                        return null;
                    },
                    delegate { SR.Ctl.ScrollToBottom(); },
                    delegate { SR.Ctl.NoteEntryBound(); },
                    delegate (ConfigFile __c) { SR.Ctl.MarkConfigDirty(__c); },
                    delegate { return SR.Ctl.LangEn; },
                    delegate { return SR.Ctl.EditOpen; });
                SRApi.SetUiSinks(
                    delegate { return SR.ContentArea; }, delegate (Rect __v) { SR.ContentArea = __v; },
                    delegate { return SR.FooterHeight; }, delegate (float __v) { SR.FooterHeight = __v; },
                    delegate { return SR.PageScroll; }, delegate (Vector2 __v) { SR.PageScroll = __v; },
                    delegate { return SR.NoPageScrollBars; }, delegate (bool __v) { SR.NoPageScrollBars = __v; },
                    delegate { return SR.CursorResize; }, delegate (bool __v) { SR.CursorResize = __v; },
                    delegate { return SR.CursorMove; }, delegate (bool __v) { SR.CursorMove = __v; },
                    delegate (Func<bool> __p) { SR.RegisterTypingProvider(__p); },
                    delegate (Func<bool> __h) { SR.RegisterInputGateHold(__h); },
                    delegate (Action<Camera> __a) { SR.RegisterCameraView(__a); },
                    delegate (string __s, Action __h, Action __f) { SR.RegisterPageChrome(__s, __h, __f); },
                    delegate (string __n, ConfigEntry<KeyCode> __e, string __m) { SR.RegisterKey(__n, __e, __m); },
                    delegate (ConfigEntry<KeyCode> __e) { return SR.ComboKeyDown(__e); },
                    delegate (ConfigEntry<KeyCode> __e) { return SR.ComboKeyHeld(__e); },
                    delegate (string __s, Func<ConfigEntryBase, bool> __f) { SR.RenderSectionEntries(__s, __f); },
                    delegate (string __s) { return SR.SectionEntries(__s); },
                    delegate { return SR.TableLineStrong; },
                    delegate (Rect __r, Color __c) { SR.DrawFill(__r, __c); });
                SRApi.SetGateSinks(
                    delegate (int __m) { return SR.GateModeAllows((SR.ModeMask)__m); },
                    delegate { return SR.Env.SceneName; },
                    delegate { return SR.Env.InTreehouse; });
                SRApi.SetMiscSinks(
                    delegate (ConfigEntryBase __e) { return (int)SR.KeyComboMod(__e); },
                    delegate (ConfigEntryBase __e, int __m) { SR.SetDefaultCombo(__e, (SR.ComboMod)__m); },
                    delegate (string __t, int __mx, GUIStyle __st, GUILayoutOption[] __o) {
                        return __mx < 0 ? SR.ClearableTextField(__t, __st, __o) : SR.ClearableTextField(__t, __mx, __st, __o);
                    },
                    delegate (string __s, Action __a) { SR.RegisterPage(__s, __a.Invoke); },
                    delegate (string __s, string __p, string __zh, string __en, int __o, Action __a) { SR.RegisterPage(__s, __p, __zh, __en, __o, __a.Invoke); });
                SRApi.SetTableSinks(
                    delegate (SRApi.TableArgs __a) { SR.TableDraw(__a); },
                    delegate (Event __e, ref float __w, ref bool __sb, ref int __mc, ref float __md, ref int __dc, ref bool __dh, Action __sw, Action __sh, Action __rj) { SR.TableDragWatchdog(__e, ref __w, ref __sb, ref __mc, ref __md, ref __dc, ref __dh, __sw, __sh, __rj); },
                    delegate (Event __e, Rect __box, Vector2 __mp, float __mx, float __my, ref Vector2 __sc, ref Vector2 __sr) { return SR.TableWheelInput(__e, __box, __mp, __mx, __my, ref __sc, ref __sr); },
                    delegate (Event __e, Vector2 __mp, Rect __tr, Rect __th, float __sm, ref bool __dr, ref float __gy, ref float __sy) { return SR.TableScrollbarInput(__e, __mp, __tr, __th, __sm, ref __dr, ref __gy, ref __sy); });
            } catch { }
            // 反射发现全部 ITweak 并初始化（Harmony 补丁需类自己注册），个别类型加载失败不致命。
            // 顺序：SR 必须最先 Initialize（旧 cfg 迁移必须早于任何功能 Config.Bind）；其余按类型全名排序。
            List<Type> __tweaks = new List<Type>();
            foreach (Type __t in GetTypesSafe(Assembly.GetExecutingAssembly())) {
                if (typeof(ITweak).IsAssignableFrom(__t) && !__t.IsInterface && !__t.IsAbstract) __tweaks.Add(__t);
            }
            __tweaks.Sort(delegate(Type a, Type b) {
                int ra = a == typeof(SR_UCH.Tweaks.SR) ? 0 : 1;
                int rb = b == typeof(SR_UCH.Tweaks.SR) ? 0 : 1;
                if (ra != rb) return ra - rb;
                return string.Compare(a.FullName, b.FullName, StringComparison.Ordinal);
            });
            foreach (Type type in __tweaks) {
                // 实例化也放进 try：单个功能构造失败不能中断后续的 LoadExModule / LoadExternal。
                try {
                    var tweak = (ITweak)Activator.CreateInstance(type);
                    tweak.Initialize(this);
                } catch (Exception e) {
                    ModLogger.LogError("Failed to initialize " + type.Name + ": " + e);
                }
            }
            LoadExModule();
            // 外部功能 DLL / 语言包 DLL：一个 DLL 一个功能（ITweak）或一门语言（ILanguagePack）；
            // 放在本程序集扫描与 EX 之后（EX 的硬编码路径不受影响）。
            LoadExternal();
            // 功能清单（SMAPI 的 manifest 等价物）：内置 / 外部 / EX 的 ITweak 全部就位后统一登记，校验依赖与契约版本。
            try { Features.Rebuild(); ModLogger.LogInfo(Features.Describe()); } catch (Exception __ft) { try { ModLogger.LogWarning("[SR.Feat] 功能清单登记失败: " + __ft.Message); } catch { } }
            // 启动自检（补丁目标 / 反射成员）：必须在全部功能与 EX 都挂完之后，结论进日志。
            try { SR.RunStartupAudit(); } catch (Exception __ex) { try { ModLogger.LogWarning("[SR] 启动自检失败: " + __ex.Message); } catch { } }
        }

        // 只认 SR_UCH 这几个程序集名：外部 DLL 编译时引用的就是它。
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args) {
            try {
                AssemblyName want = new AssemblyName(args.Name);
                // 拆分模式下核心文件名是 SR_Manager.dll / SR_UCH_Manager.dll（程序集标识仍是 "SR_UCH"）。
                if (want.Name == "SR_UCH" || want.Name == "SR_Manager" || want.Name == "SR_UCH_Manager")
                    return typeof(MainPlugin).Assembly;
            } catch { }
            return null;
        }

        // 外部 DLL（功能 / 语言包）：实现 ITweak 或 ILanguagePack，不需要 [BepInPlugin] 与 BepInEx 引用，
        // 只引用 SR_UCH.dll，把生成的 DLL 丢进 BepInEx\plugins\（或 BepInEx\modules\）。
        private void LoadExternal() {
            string[] dirs = null;
            try {
                dirs = new[] { Paths.PluginPath, Path.Combine(Paths.BepInExRootPath, "modules") };
            } catch (Exception e) { ModLogger.LogWarning("[SR] 定位插件目录失败: " + e.Message); return; }
            try {
                foreach (string dir in dirs) {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.dll"); } catch { continue; }
                    for (int i = 0; i < files.Length; i++) LoadOneExternal(files[i]);
                }
            } catch (Exception e) {
                ModLogger.LogError("[SR] 外部 DLL 扫描失败: " + e);
            }
        }

        private void LoadOneExternal(string dll) {
            try {
                string name = Path.GetFileNameWithoutExtension(dll);
                // 跳过自己与附加模块；拆分模式下核心自己的文件名同样要跳过，否则会把"自己"再扫一遍。
                if (name == "SR_UCH" || name == "SR_Manager" || name == "SR_UCH_Manager" || name == "SR_UCH_EX") return;
                // 已加载过则复用同一份程序集：LoadFile 会复制副本，typeof(SR) 不再是同一个 Type。
                Assembly asm = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                    try {
                        if (a.IsDynamic) continue;
                        string an = Path.GetFileNameWithoutExtension(a.Location);
                        if (string.Equals(an, name, StringComparison.OrdinalIgnoreCase)) { asm = a; break; }
                    } catch { }
                }
                if (asm == null) asm = Assembly.LoadFrom(dll);
                if (asm == null) return;

                // 同一份程序集只处理一次：重复会 Initialize 两遍、补丁打两遍。
                // 语言包允许随独立 BepInEx 插件提供，故不能按"是不是刚加载的"判断。
                string key = null;
                try { key = asm.FullName; } catch { }
                if (string.IsNullOrEmpty(key)) key = dll;
                if (!_extSeen.Add(key)) return;

                Type[] types = GetTypesSafe(asm);
                // 按特性类型名判断，不写 typeof(BepInPluginAttribute)：BepInEx 改名不致编译失败。
                // [BepInPlugin] 打在插件类上而非 assembly 上：必须遍历类型，否则独立插件会被重复初始化。
                bool isBepInPlugin = false;
                for (int ti = 0; ti < types.Length && !isBepInPlugin; ti++) {
                    Type tCheck = types[ti];
                    if (tCheck == null) continue;
                    try {
                        object[] attrs = tCheck.GetCustomAttributes(false);
                        for (int ai = 0; ai < attrs.Length; ai++) {
                            if (attrs[ai] != null && attrs[ai].GetType().Name == "BepInPluginAttribute") { isBepInPlugin = true; break; }
                        }
                    } catch { }
                }
                for (int i = 0; i < types.Length; i++) {
                    Type t = types[i];
                    if (t == null || t.IsInterface || t.IsAbstract) continue;
                    try {
                        // 语言包：任何 DLL 形态都收（含独立 BepInEx 插件顺带提供的）
                        if (typeof(ILanguagePack).IsAssignableFrom(t)) {
                            var pack = (ILanguagePack)Activator.CreateInstance(t);
                            SR.RegisterLanguage(pack);
                            continue;
                        }
                        // 功能：只收没有 BepInPlugin 入口的 DLL；带入口的插件自行调 SR.RegisterTweak。
                        if (isBepInPlugin) continue;
                        if (!typeof(ITweak).IsAssignableFrom(t)) continue;
                        // 阶段C（SMAPI 行为）：功能清单要求的最低契约版本高于当前核心时拒绝加载，而不是带病初始化。
                        FeatureInfo __fi = Features.FromType(t);
                        if (__fi != null && __fi.MinContract > SRContract.Version) {
                            ModLogger.LogError("[SR.Feat] 拒绝加载 " + __fi.Id + "：" + __fi.ToString());
                            continue;
                        }
                        var tweak = (ITweak)Activator.CreateInstance(t);
                        tweak.Initialize(this);
                        ModLogger.LogInfo("[SR] 已加载外部功能: " + t.Name + " (" + name + ".dll)");
                    } catch (Exception e) {
                        ModLogger.LogError("[SR] 外部 DLL 初始化失败 " + name + "/" + t.Name + ": " + e.Message);
                    }
                }
            } catch (Exception e) {
                // 单个 DLL 出问题（缺依赖 / 版本不符）不影响其余，也不影响 SR 本身
                ModLogger.LogWarning("[SR] 跳过外部 DLL " + Path.GetFileName(dll) + ": " + e.Message);
            }
        }

        // GetTypes 遇"程序集引用了本机不存在的类型"会整体抛 ReflectionTypeLoadException：只取成功加载的类型。
        private static Type[] GetTypesSafe(Assembly asm) {
            try { return asm.GetTypes(); } catch (ReflectionTypeLoadException ex) {
                List<Type> ok = new List<Type>();
                Type[] ts = ex.Types;
                if (ts != null) {
                    for (int i = 0; i < ts.Length; i++) if (ts[i] != null) ok.Add(ts[i]);
                }
                return ok.ToArray();
            } catch { return new Type[0]; }
        }

        // 加载 SR_UCH_EX.dll：无 [BepInPlugin] 入口也不是 BaseUnityPlugin，BepInEx 会跳过 ->
        // 只能 LoadFile + 反射调 ExLoader.Init，把附加功能挂进 EX 栏目；查找 plugins -> modules。
        private void LoadExModule() {
            try {
                string dll = Path.Combine(Paths.PluginPath, "SR_UCH_EX.dll");
                if (!File.Exists(dll)) {
                    string alt = Path.Combine(Paths.BepInExRootPath, "modules", "SR_UCH_EX.dll");
                    if (File.Exists(alt)) dll = alt;
                }
                if (!File.Exists(dll)) {
                    ModLogger.LogInfo("附加模块未找到，跳过");
                    return;
                }
                Assembly asm = Assembly.LoadFile(dll);
                Type loader = asm.GetType("SR_UCH_EX.ExLoader");
                if (loader == null) { ModLogger.LogError("附加模块缺少 ExLoader"); return; }
                MethodInfo init = loader.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                if (init == null) { ModLogger.LogError("附加模块缺少 Init 入口"); return; }
                bool ok = (bool)init.Invoke(null, new object[] { this });
                ModLogger.LogInfo("附加模块加载完成: " + (ok ? "成功" : "失败"));
            } catch (Exception e) {
                ModLogger.LogError("附加模块加载失败: " + e);
            }
        }
    }
        // 只针对本插件：plugin.Config 返回自己的 ConfigFile，于是所有 Bind 落到 SR_UCH_data.cfg。
        [HarmonyLib.HarmonyPatch(typeof(BepInEx.BaseUnityPlugin), "get_Config")]
        internal static class PatchOwnConfigFile {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(BepInEx.BaseUnityPlugin __instance, ref BepInEx.Configuration.ConfigFile __result) {
                try {
                    if (!(__instance is MainPlugin)) return true;
                    if (MainPlugin.Data == null) return true;
                    __result = MainPlugin.Data;
                    return false;
                } catch { return true; }
            }
        }

}
