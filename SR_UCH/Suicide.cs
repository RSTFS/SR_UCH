using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public class TreehouseSuicide : ITweak {
        private static MainPlugin _mp;
        private static ConfigEntry<KeyCode> _keybind;
        //runtime toggle (also controlled by the in-game manager)
        public static bool Enabled = true;
        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            ConfigEntry<bool> enabled = _mp.Config.Bind("Treehouse Suicide", "Enabled", false, "总开关");
            Enabled = enabled.Value;
            enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
            _keybind = _mp.Config.Bind(
                "Treehouse Suicide",
                "Keybind",
                KeyCode.Alpha0,
                "自杀键（组合键设置：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键，即可设为组合键，如 Shift+0）");
            //旧版默认是 P：一键迁移到 Shift+0（用户自定义过的键位不动）
            if (_keybind.Value == KeyCode.P)
            {
                _keybind.Value = KeyCode.Alpha0;
            }
            //默认 Shift+0：注册时若用户从未设置过组合修饰，则默认 Shift
            ModManager.RegisterShiftKey("快捷自杀", _keybind, "hold");
            if (ModManager.KeyComboMod(_keybind) == ModManager.ComboMod.None)
            {
                ModManager.SetDefaultCombo(_keybind, ModManager.ComboMod.Shift);
            }
            Harmony.CreateAndPatchAll(typeof(TreehouseSuicide));
        }

        [HarmonyPatch(typeof(Character), "FixedUpdate")]
        [HarmonyPostfix]
        private static void CharacterPatch(Character __instance) {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (ModManager.UiOpen && ModManager.BlockInput) return; //UI open + block on: don't steal input
            //only ever kill the local player, never teammates
            if (!__instance.hasAuthority) return;
            //组合键判定：修饰键 + 主键（修饰键在键位捕捉时设置，默认 Shift）
            if (ModManager.ComboKeyDown(_keybind) && !__instance.Frozen) {
                //works both in the treehouse lobby and in-game (instantly skips the suicide hold bar)
                __instance.KillCharacter("Suicide", false, __instance.networkNumber);
            }
        }
    }
}
