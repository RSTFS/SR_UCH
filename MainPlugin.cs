using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SR_UCH {
    public interface ITweak {
        void Initialize(MainPlugin plugin);
    }

    [BepInPlugin("com.gamingbeast.SR_UCH", "SR_UCH", "1.0.0")]
    public class MainPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        public void Awake() {
            ModLogger = Logger;
            //反射发现全部 ITweak 并初始化（新功能只要实现 ITweak 就会被自动加载；Harmony 补丁需类自己注册）
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes()
                         .Where(t => typeof(ITweak).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)) {
                var tweak = (ITweak)Activator.CreateInstance(type);
                try {
                    tweak.Initialize(this);
                } catch (Exception e) {
                    ModLogger.LogError("Failed to initialize " + type.Name + ": " + e);
                }
            }
            LoadExModule();
        }

        //加载 SR_UCH_EX.dll（闭源附加模块）：它没有 [BepInPlugin] 入口也不是 BaseUnityPlugin，
        //BepInEx 扫描 plugins 时会跳过 → 只能这里 Assembly.LoadFile + 反射调 ExLoader.Init，
        //把附加功能挂进"EX"栏目。查找顺序：plugins 目录 → BepInEx/modules。
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
                //程序集可能间接引用 SR_UCH.dll，确保类型解析不失败
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                    try {
                        AssemblyName want = new AssemblyName(e.Name);
                        if (want.Name == "SR_UCH") return typeof(MainPlugin).Assembly;
                        return null;
                    } catch { return null; }
                };
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
}
