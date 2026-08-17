using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;

namespace SR_UCH {
    public interface ITweak {
        void Initialize(MainPlugin plugin);
    }

    [BepInPlugin("com.gamingbeast.SR_UCH", "SR_UCH", "1.0.0")]
    public class MainPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        public void Awake() {
            ModLogger = Logger;
            //loop thru every tweak and initialize it
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes()
                         .Where(t => typeof(ITweak).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)) {
                var tweak = (ITweak)Activator.CreateInstance(type);
                try {
                    tweak.Initialize(this);
                } catch (Exception e) {
                    ModLogger.LogError("Failed to initialize " + type.Name + ": " + e);
                }
            }
            //附加模块：由 SR_UCH 主动加载（BepInEx/plugins 下的独立模块，不被任何加载器识别）
            LoadExModule();
        }

        //加载 SR_UCH_EX.dll：该 DLL 没有 [BepInPlugin] 入口、不是
        //BaseUnityPlugin 子类，BepInEx 扫描 plugins 目录时会直接跳过它（不会
        //加载成外部插件、不显示在外部列表），其他 mod 加载器也无法把它当作
        //插件加载。只有这里显式 Assembly.LoadFile + 反射调用 ExLoader.Init
        //把附加功能挂进 SR_UCH 的"EX"栏目。
        //查找顺序：先找 plugins 目录（与 SR_UCH.dll 放一起），再找 BepInEx/modules。
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
