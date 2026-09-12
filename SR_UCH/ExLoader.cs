using SR_UCH; //MainPlugin
using SR_UCH.Tweaks; //SR

namespace SR_UCH_EX {
    //EX 加载器：SR_UCH_EX.dll 不再是一个独立 BepInEx 插件（没有
    //[BepInPlugin] 入口、不是 BaseUnityPlugin），所以 BepInEx 扫描 plugins
    //目录时不会把它加载成外部插件，也不会被其他 mod 加载器识别。它只被
    //SR_UCH 主动 Assembly.LoadFile 后调用 Init() 挂进 SR_UCH 的"EX"栏目。
    public static class ExLoader {
        //由 SR_UCH.MainPlugin.Awake 通过反射调用（host = SR_UCH 主插件实例）
        public static bool Init(SR_UCH.MainPlugin host) {
            try {
                var inst = new ExModule();
                inst.Initialize(host);
                ExModule.RegisterUi(); //把 EX 页面/文案/行扩展点注册给 SR（EX 的 UI 全部在本模块内）
                SR.RegisterCultivation(inst);
                return true;
            } catch (System.Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogError("EX 初始化失败: " + e);
                return false;
            }
        }
    }
}
