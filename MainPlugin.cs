// ============================================================================
// 文件：MainPlugin.cs                        程序集：SR_UCH.dll
// 层：入口（BepInEx 插件）
// 职责：配置外置到 SR_UCH_data.cfg；反射发现并初始化全部 ITweak；扫描外部
//       功能/语言包 DLL；注册 SR_UCH 程序集解析。本文件定义统一契约 ITweak。
// 结构：功能源在 Features/，核心（UI/控件/注册表/门控）在 SR_UCH/，核心不认识任何功能。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using SR_UCH.Tweaks;

namespace SR_UCH {
    //★单一真值：插件声明与核心扫描（SR.Core.EnsureScanned）必须比对**同一个**字符串。
    //  原来两边各写字面量, GUID 改名时只改一处，核心那段"自己的配置"分支就静默不进：
    //  后果是**全部组合键丢失**（FlushPendingCombos 不跑）+ **配置退回"改一次写一次盘"**
    //  （SaveOnConfigSet 保持 true -> 滚轮/滑块每帧一次磁盘 IO），而且**一条告警都没有**。
    //  提成常量后，漏改会变成**编译报错**。
    //★这里刻意用 const（要的就是编译期内联）；`ApiVersion` 用 static readonly 是**相反**的理由
    //  （那里恰恰要避免被外部模块在编译期内联）。两处理由相反，别"统一风格"改错。
    [BepInPlugin(PluginGuid, "SR_UCH", "1.0.0")]
    //IFeatureHost 由契约文件提供；BaseUnityPlugin.Config / .Logger 已经满足它，无需额外成员。
    public class MainPlugin : BaseUnityPlugin, IFeatureHost {
        public const string PluginGuid = "com.rs.SR_UCH";

        public static ManualLogSource ModLogger;

        //IFeatureHost：契约要求"功能能拿到 Config 与日志"。
        //Config 由 BaseUnityPlugin.Config 隐式满足；Logger 在基类里不是 public -> 显式实现。
        ManualLogSource IFeatureHost.Logger { get { return Logger; } }

        //已处理过的外部程序集（按 FullName 记）：同一份 DLL 只初始化一次。
        //（原来靠 bool fresh 判断"是不是刚加载的"，但 fresh 算完没用上 -> 重复处理。）
        private static readonly HashSet<string> _extSeen = new HashSet<string>();

        //★#4A 配置外置：所有 Bind 落到这个独立文件 -> 本插件自己的 cfg 保持为空，
        //    ConfigurationManager 等配置浏览器就列不出 SR_UCH 的任何条目。
        public static BepInEx.Configuration.ConfigFile Data;

        public void Awake() {
            //★P0 修复（修正上一轮的失误）：赋值必须是**本方法第一条语句**。
            //  上一轮我去重"重复赋值"时，误删了真正的赋值行、只留下注释里的那句引用 ->
            //  等于把 P0 原样复原（ModLogger 恒为 null）。现在只保留这一处，且在最早位置。
            ModLogger = Logger;
            //★P0 关键修复：ModLogger 必须**第一行**就赋值。
            //  它原来是 public static、默认 null，却在本方法第 44 行起就被使用（配置外置/迁移日志），
            //  而赋值在很后面的 ModLogger = Logger;, 于是**老玩家**（走迁移路径时）第 44 行就 NRE，
            //  紧接着 catch 里的 ModLogger.LogWarning **再 NRE**、被内层 catch 吞掉 ->
            //  表现就是"配置外置完全失效 + 日志里一条报错都没有"。新玩家不触发迁移路径，所以只有老玩家中招。
            //★P1 PatchOwnConfigFile 原先是**死补丁**：写在文件里，但 CreateAndPatchAll 不会递归发现
            //  独立类型 -> 从未注册。它本是"后备字段改写"之外的第二道保险，这里显式注册；
            //  失败只记日志，不影响主手段。
            try { HarmonyLib.Harmony.CreateAndPatchAll(typeof(PatchOwnConfigFile)); }
            catch (Exception __pf) { try { ModLogger.LogWarning("配置外置补丁(PatchOwnConfigFile)注册失败: " + __pf.Message); } catch { } }

            //★#4A 独立配置文件（旧 cfg 自动迁移）
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
                //★#4A 关键：把插件自己的 Config 后备字段直接指向我们的文件,
                //   仅靠 Harmony 补 get_Config 不够（实测仍写着 com.rs.SR_UCH.cfg），必须改后备字段，
                //   这样 BepInEx 自己的保存路径也落在 SR_UCH_data.cfg，插件 cfg 永远为空 -> CM 看不到任何条目。
                try {
                    System.Reflection.FieldInfo __cf = typeof(BepInEx.BaseUnityPlugin).GetField("<Config>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (__cf != null && Data != null) { __cf.SetValue(this, Data); ModLogger.LogInfo("[配置] 插件 Config 已指向 SR_UCH_data.cfg（外置生效）"); }
                    else ModLogger.LogWarning("[配置] 未找到 Config 后备字段，配置外置可能未生效");
                } catch (Exception __cfx) { try { ModLogger.LogWarning("配置外置(后备字段)失败: " + __cfx.Message); } catch { } }
            } catch (Exception __de) { try { ModLogger.LogWarning("配置外置失败: " + __de.Message); } catch { } }

            //★#5 配置迁移：GUID 从 com.gamingbeast.SR_UCH 改为 com.rs.SR_UCH -> cfg 文件名随之变化。
            //  在任何 Bind 之前，把旧 cfg 复制成新名字，保证老玩家的设置不丢。
            try {
                string __cfgDir = BepInEx.Paths.ConfigPath;
                string __oldCfg = System.IO.Path.Combine(__cfgDir, "com.gamingbeast.SR_UCH.cfg");
                string __newCfg = System.IO.Path.Combine(__cfgDir, "com.rs.SR_UCH.cfg");
                if (System.IO.File.Exists(__oldCfg) && !System.IO.File.Exists(__newCfg)) {
                    System.IO.File.Copy(__oldCfg, __newCfg, false);
                    ModLogger.LogInfo("[配置] 已从旧 GUID 配置迁移: com.gamingbeast.SR_UCH.cfg -> com.rs.SR_UCH.cfg");
                }
            } catch (Exception __mig) { try { ModLogger.LogWarning("配置迁移失败: " + __mig.Message); } catch { } }
            //程序集解析：外部功能 DLL / 语言包 DLL 只引用 SR_UCH.dll（不引用 BepInEx），
            //加载它们时 CLR 必须能定位 SR_UCH -> 全局注册一次。
            //（原来这段挂在 LoadExModule 里：每加载一个外部 DLL 就重复订阅一次事件。）
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            //反射发现全部 ITweak 并初始化（新功能只要实现 ITweak 就会被自动加载；Harmony 补丁需类自己注册）
            //这里也走 GetTypesSafe：与扫描外部 DLL 保持一致，个别类型加载失败不至于让整个插件起不来。
            //=== 生命周期/顺序确定 ===
            //1) 核心 SR 必须**最先** Initialize：它要做旧 cfg 迁移（中文 section/key -> 英文），
            //   而那一步必须早于任何功能 Config.Bind（否则功能会以旧段名建条目，迁移白做）。
            //   原来靠"反射返回顺序碰巧把 SR 排在前面", 那是运气，不是保证。
            //2) 其余功能按类型全名排序：同一份代码每次启动顺序一致，不再依赖反射返回顺序。
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
                //实例化也放进 try：某个功能缺公共无参构造/构造函数抛异常时，不能中断整个 Awake
                //（否则后面的 LoadExModule / LoadExternal 全都不执行）。
                try {
                    var tweak = (ITweak)Activator.CreateInstance(type);
                    tweak.Initialize(this);
                } catch (Exception e) {
                    ModLogger.LogError("Failed to initialize " + type.Name + ": " + e);
                }
            }
            LoadExModule();
            //外部功能 DLL / 外部语言包 DLL：一个 DLL = 一个功能（ITweak）或一门语言（ILanguagePack）。
            //放在本程序集扫描与 EX 之后：这样 EX 那套硬编码路径一字不用改，两者互不干扰。
            LoadExternal();
            //启动自检（补丁目标 / 反射成员）：必须放在**全部功能与 EX 都挂完**之后，
            //这样自检才覆盖完整。结论进日志；界面在设置页自检块与首页提示里读同一份结果。
            try { SR.RunStartupAudit(); } catch (Exception __ex) { try { ModLogger.LogWarning("[SR] 启动自检失败: " + __ex.Message); } catch { } }
        }

        //只认 SR_UCH 这一个名字：外部 DLL 编译时引用的是它，运行时必须解析到已加载的本程序集。
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args) {
            try {
                AssemblyName want = new AssemblyName(args.Name);
                if (want.Name == "SR_UCH") return typeof(MainPlugin).Assembly;
            } catch { }
            return null;
        }

        //==== 外部 DLL（功能 / 语言包）：一个 DLL 一份，装几个就多几个 ====
        //契约与内部功能完全一致：实现 ITweak（配置/文案/页面/补丁都在自己的 cs 里），
        //或实现 ILanguagePack（一门语言的词条表）。不需要 [BepInPlugin]，也不用引用 BepInEx，
        //只引用 SR_UCH.dll，把生成的 DLL 丢进 BepInEx\plugins\（或 BepInEx\modules\）即可。
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
                //跳过自己与附加模块：前者已在上面反射扫描过，后者走 LoadExModule 的专用路径
                if (name == "SR_UCH" || name == "SR_UCH_EX") return;
                //已加载过（比如 BepInEx 自己先装过它）-> 复用同一份程序集，不要 LoadFile 出第二份副本
                //（LoadFile 每次都复制一份，两边看到的 typeof(SR) 不是同一个 Type，委托传过去会类型不匹配）
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

                //同一份程序集只处理一次。不去重的后果：DLL 同时出现在 plugins/ 与 modules/ 时会被
                //扫描到两次 -> ITweak.Initialize 跑两遍 -> Harmony 补丁打两遍（前缀/后缀各执行一次，
                //开关类功能表现为"点一下执行两次"）。
                //注意：不能简单用"是不是刚加载的"来跳过, 语言包允许随独立 BepInEx 插件提供，
                //那种 DLL 永远是被 BepInEx 先加载好的（不 fresh），但它的语言包必须照收。
                string key = null;
                try { key = asm.FullName; } catch { }
                if (string.IsNullOrEmpty(key)) key = dll;
                if (!_extSeen.Add(key)) return;

                Type[] types = GetTypesSafe(asm);
                //按特性类型名判断，不写 typeof(BepInPluginAttribute)：万一将来 BepInEx 改了这个类名，
                //这里不会连带编译失败（识别不出最多是"这个 DLL 被跳过"，不会让 SR 起不来）。
                //注意：[BepInPlugin] 在 BepInEx 5 是打在插件**类**上的（AttributeUsage=Class），
                //不是 assembly 级特性, 所以必须遍历类型；查 assembly 永远查不到，会把独立插件也当成
                //"无入口的功能 DLL"去实例化它的 ITweak，和插件自己的 Awake 重复初始化。
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
                        //语言包：任何 DLL 形态都收（含独立 BepInEx 插件顺带提供的语言包）
                        if (typeof(ILanguagePack).IsAssignableFrom(t)) {
                            var pack = (ILanguagePack)Activator.CreateInstance(t);
                            SR.RegisterLanguage(pack);
                            continue;
                        }
                        //功能：只收"没有 BepInPlugin 入口"的 DLL, 带入口的说明它是独立插件，
                        //BepInEx 已经实例化过它自己的插件类；这里再 Initialize 一次会重复注册。
                        //那种 DLL 想要挂 SR，请在自己的 Awake 里调 SR.RegisterTweak / 自行 Initialize。
                        if (isBepInPlugin) continue;
                        if (!typeof(ITweak).IsAssignableFrom(t)) continue;
                        var tweak = (ITweak)Activator.CreateInstance(t);
                        tweak.Initialize(this);
                        ModLogger.LogInfo("[SR] 已加载外部功能: " + t.Name + " (" + name + ".dll)");
                    } catch (Exception e) {
                        ModLogger.LogError("[SR] 外部 DLL 初始化失败 " + name + "/" + t.Name + ": " + e.Message);
                    }
                }
            } catch (Exception e) {
                //单个 DLL 出问题（缺依赖、版本不符）不影响其余，也不影响 SR 本身
                ModLogger.LogWarning("[SR] 跳过外部 DLL " + Path.GetFileName(dll) + ": " + e.Message);
            }
        }

        //GetTypes 在"该程序集引用了本机不存在的类型"时会整个抛 ReflectionTypeLoadException；
        //这里只取成功加载的那些类型，坏 DLL 不至于让整个扫描挂掉。
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

        //加载 SR_UCH_EX.dll（闭源附加模块）：它没有 [BepInPlugin] 入口也不是 BaseUnityPlugin，
        //BepInEx 扫描 plugins 时会跳过 -> 只能这里 Assembly.LoadFile + 反射调 ExLoader.Init，
        //把附加功能挂进"EX"栏目。查找顺序：plugins 目录 -> BepInEx/modules。
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
        //★#4A 只针对本插件：让 plugin.Config 返回我们自己的 ConfigFile（其它插件不受影响），
        //   于是所有 plugin.Config.Bind(...) 全部落到 SR_UCH_data.cfg。
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
