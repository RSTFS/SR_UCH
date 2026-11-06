// 自由取色：在库存里点任意「Pick a Color」色块时，改用我们配置的颜色（移植自 BuildingPlus 的 Free Color Choice）。
// 做法：给 PickableCustomizationButton.OnAccept 加前缀，拦住原逻辑并发送我们自己的 SetpieceColorChangeEvent，
// 于是后续的染色/同步仍走游戏原有链路，不需要我们自己写网络代码。
using System;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.freecolor", "FreeColor", "1.0")]
    public class FreeColor : ITweak {
        private const string Sec = "build";
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _color;
        private static ConfigEntry<bool> _requireKey;
        private static ConfigEntry<KeyCode> _key;

        internal static bool Enabled { get { return _enabled != null && _enabled.Value && SRApi.GateMaster; } }

        // 当前配置的颜色（非法值回退到白）
        internal static Color Value {
            get {
                try {
                    string hex = _color != null ? _color.Value : null;
                    Color c;
                    return SRApi.ColorHex.ParseHex(hex, Color.white);
                } catch (Exception __ex) { SRApi.Guard.Log("自由取色/读取颜色", __ex); return Color.white; }
            }
        }

        // 是否需要按住指定键才生效（默认关：点色块就用自己的颜色）
        internal static bool KeyHeld {
            get {
                try {
                    if (_requireKey == null || !_requireKey.Value) return true;
                    if (_key == null || _key.Value == KeyCode.None) return true;
                    return Input.GetKey(_key.Value);
                } catch (Exception __ex) { SRApi.Guard.Log("自由取色/按键判定", __ex); return true; }
            }
        }

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Free Color", "自由取色", null);
            SRApi.LocDesc(Sec, "Free Color", "自由取色\n在库存里点任意调色板色块时改用下面的自定义颜色\n染色仍走游戏自己的事件与同步", "Free colour\nclicking any palette swatch in the inventory uses your custom colour below\ntinting still goes through the game's own event and sync");
            SRApi.LocKey(Sec, "Free Color Value", "自定义颜色", null);
            SRApi.LocDesc(Sec, "Free Color Value", "自定义颜色\n自由取色时使用的颜色\n点色块展开调色板", "Custom colour\nthe colour used by free colour\nclick the swatch to open the palette");
            SRApi.LocKey(Sec, "Free Color Key", "需要按住键", null);
            SRApi.LocDesc(Sec, "Free Color Key", "需要按住键\n开启后要按住下面的键点色块才用自定义颜色\n关闭则点色块就用", "Require a modifier\nwhen on, you must hold the key below while clicking a swatch\nwhen off every swatch click uses it");
            SRApi.LocKey(Sec, "Free Color Key Code", "按住键", null);
            SRApi.LocDesc(Sec, "Free Color Key Code", "按住键\n需要按住时才生效的键\n默认 LeftShift", "Modifier key\nthe key to hold when Require a modifier is on\nLeftShift by default");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Free Color", false, "自由取色：点库存调色板色块时使用自定义颜色。");
                _color = plugin.Config.Bind(Sec, "Free Color Value", "#FF8800", "自定义颜色（#RRGGBB）。");
                _requireKey = plugin.Config.Bind(Sec, "Free Color Key", false, "需要按住键才生效（默认关）。");
                _key = plugin.Config.Bind(Sec, "Free Color Key Code", KeyCode.LeftShift, "需要按住时生效的键。");
                try { Harmony.CreateAndPatchAll(typeof(FreeColorPatch)); }
                catch (Exception __h) { SRApi.Guard.Log("自由取色/补丁注册", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("自由取色/初始化", __ex); }
        }
    }

    // 前缀：命中彩色块按钮时用我们的颜色发事件，并跳过原逻辑（原逻辑发的是调色板色块自身的颜色）。
    [HarmonyPatch(typeof(PickableCustomizationButton), "OnAccept")]
    internal static class FreeColorPatch {
        [HarmonyPrefix]
        private static bool Prefix(PickableCustomizationButton __instance) {
            try {
                if (__instance == null || !FreeColor.Enabled || !FreeColor.KeyHeld) return true;
                if (__instance.customizationType != CustomizationType.BlockColors) return true;
                GameEventManager.SendEvent(new SetpieceColorChangeEvent(FreeColor.Value));
                return false;   // 已经用自己的颜色处理，跳过原逻辑
            } catch (Exception __ex) { SRApi.Guard.Log("自由取色/拦截色块", __ex); return true; }
        }
    }
}
