using BepInEx.Configuration;
using HarmonyLib;

namespace SR_UCH.Tweaks
{
    public class SyncedCycles : ITweak
    {
        private static MainPlugin _mp;
        private static ConfigEntry<bool> _enabled;
        //runtime toggle (also controlled by the in-game manager)
        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }

        public void Initialize(MainPlugin plugin)
        {
            _mp = plugin;
            _enabled = plugin.Config.Bind("地图", "同步循环", false,
                "同步循环：强制所有发射器（炮弹/火焰等）的初始延迟统一为 0.5 秒。\n原版自由模式下延迟会减去网络延迟（0.5 - ping），高 ping 时发射节奏不稳定；开启后固定 0.5 秒，全员发射时机一致。");
            _enabled.SettingChanged += (s, e) => { };
            Harmony.CreateAndPatchAll(typeof(SyncedCycles));
        }
        
        //js gotta force it to 0.5f, in challenge it is always 0.5 but in freeplay it is 0.5f - ping
        //仅自由模式生效（"无视模式限制"不作用于同步循环）
        [HarmonyPatch(typeof(ProjectileLauncher), "UpdateInitialDelay")]
        [HarmonyPrefix]
        static bool SyncDelay(ProjectileLauncher __instance)
        {
            if (!ModManager.AllEnabled) return true;
            if (!Enabled) return true;
            if (GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY) return true;
            __instance.initialDelay = 0.5f;
            return false;
        }
    }
}
