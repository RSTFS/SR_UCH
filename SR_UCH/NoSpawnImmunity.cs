using BepInEx.Configuration;
using HarmonyLib;

namespace SR_UCH.Tweaks {
    public class NoSpawnImmunity : ITweak {
        private static MainPlugin _mp;
        private static ConfigEntry<float> _immunityTime;
        //runtime toggle (also controlled by the in-game manager)
        public static bool Enabled = true;
        
        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            //master switch for the whole respawn group (immunity + delay)
            ConfigEntry<bool> enabled = _mp.Config.Bind("Respawn", "Enabled", false, "重生功能总开关（仅自由模式可用）");
            NoSpawnImmunity.Enabled = enabled.Value;
            RespawnDelay.Enabled = enabled.Value;
            enabled.SettingChanged += (s, e) => {
                NoSpawnImmunity.Enabled = enabled.Value;
                RespawnDelay.Enabled = enabled.Value;
            };
            _immunityTime = _mp.Config.Bind(
                "Respawn",
                "Spawn Immunity",
                0.3f,
                "The altered spawn immunity");
            Harmony.CreateAndPatchAll(typeof(NoSpawnImmunity));
        }

        [HarmonyPatch(typeof(Character), "StartInvincibleTimer")]
        [HarmonyPrefix]
        static void ImmunityPatch(ref float time) {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (ModManager.IgnoreModeLimit || GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY) {
                time = _immunityTime.Value;
            }
        }
    }
}