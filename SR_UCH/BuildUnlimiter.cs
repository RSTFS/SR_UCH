using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //建造上限（移植自 Osqat/UCH-BuildUnlimiter）：把 GameSettings.LevelFullnessScoreLimit
    public class BuildUnlimiter : ITweak {
        public const int VanillaLimit = 500;
        private const int DefaultLimit = 1000000;
        private const int MinLimit = 500;
        private const int MaxLimit = 10000000;

        private static MainPlugin _mp;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _limitEntry;

        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }

        public static int LimitValue {
            get { return _limitEntry != null ? Mathf.Clamp(_limitEntry.Value, MinLimit, MaxLimit) : DefaultLimit; }
        }

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            _enabled = plugin.Config.Bind("Builder Enhancements", "解除建造上限", false,
                "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到“上限数值”（默认 1000000），超满的关卡也能正常发布/上传；关闭立即恢复原版。");
            _limitEntry = plugin.Config.Bind("Builder Enhancements", "上限数值", DefaultLimit, new ConfigDescription(
                "满度上限数值（500 - 10000000；游戏原版为 500）", new AcceptableValueRange<int>(MinLimit, MaxLimit)));
            if (_limitEntry.Value < MinLimit || _limitEntry.Value > MaxLimit) {
                _limitEntry.Value = DefaultLimit;
            }
            _enabled.SettingChanged += (s, e) => Apply();
            _limitEntry.SettingChanged += (s, e) => Apply();
            Apply(); //启动时归位：关闭则保持原版 500
        }


        private static void Apply() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return;
                gs.LevelFullnessScoreLimit = (ModManager.AllEnabled && Enabled) ? LimitValue : VanillaLimit;
                if (_mp != null) _mp.Config.Save();
            } catch {
            }
        }


        public static int CurrentLimit() {
            try {
                return GameSettings.GetInstance().LevelFullnessScoreLimit;
            } catch {
                return VanillaLimit;
            }
        }
    }
}
