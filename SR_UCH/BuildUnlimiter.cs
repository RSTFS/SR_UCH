using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    //建造上限：放宽 GameSettings.LevelFullnessScoreLimit（移植自 Osqat/UCH-BuildUnlimiter）
    public class BuildUnlimiter : ITweak {
        public const int VanillaLimit = 500;
        private const int DefaultLimit = 1000000;
        private const int MinLimit = 500;
        private const int MaxLimit = 10000000;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _limitEntry;

        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }

        public static int LimitValue {
            get { return _limitEntry != null ? Mathf.Clamp(_limitEntry.Value, MinLimit, MaxLimit) : DefaultLimit; }
        }

        private static void SelfReg() {
            SR.LocKey("Builder Enhancements", "Lift Build Cap", "解除建造上限", null);
            SR.LocDesc("Builder Enhancements", "Lift Build Cap", "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到“上限数值”（默认 1000000），超满的关卡也能正常发布/上传。\n无需按键，在树屋外开启也会生效；关闭立即恢复原版 500。", "Lift the level fullness cap: the treehouse save/publish cap rises from 500 to the Limit value (default 1000000), so over-full levels can be published.\nWorks from anywhere; turning it off restores 500.");
            SR.LocKey("Builder Enhancements", "Build Cap Value", "上限数值", "Limit Value");
            SR.LocDesc("Builder Enhancements", "Build Cap Value", "满度上限数值（500 - 10000000；游戏原版为 500）。\n修改后立即生效；数值越大，能放的方块越多后仍可发布。", "Fullness cap value (500 - 10000000; vanilla is 500).\nTakes effect immediately; higher allows more blocks before publish is blocked.");
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            _enabled = plugin.Config.Bind("Builder Enhancements", "Lift Build Cap", false,
                "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到“上限数值”（默认 1000000），超满的关卡也能正常发布/上传；关闭立即恢复原版。");
            _limitEntry = plugin.Config.Bind("Builder Enhancements", "Build Cap Value", DefaultLimit, new ConfigDescription(
                "满度上限数值（500 - 10000000；游戏原版为 500）", new AcceptableValueRange<int>(MinLimit, MaxLimit)));
            if (_limitEntry.Value < MinLimit || _limitEntry.Value > MaxLimit) {
                _limitEntry.Value = DefaultLimit;
            }
            _enabled.SettingChanged += (s, e) => Apply();
            _limitEntry.SettingChanged += (s, e) => Apply();
            Apply(); //启动时归位：关闭则保持原版 500
            //启动时 GameSettings 可能还没加载完（BepInEx 插件的 Awake 早于游戏首个场景），
            //GetInstance() 返回 null → Apply 静默失败，而之后没有任何重试时机 →
            //已勾选的「解除建造上限」要等用户手动切换一次开关才生效（建造页只读框显示 500，与勾选状态矛盾）。
            //挂场景切换重试：与 DestroyBlocks / Respawn / ExModule 等其它 tweak 的做法一致。
            SceneManager.activeSceneChanged += (a, b) => Apply();
        }

        private static void Apply() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return; //场景还没加载完：交给下次场景切换重试（见 Initialize 里的订阅）
                gs.LevelFullnessScoreLimit = (SR.GateMaster && Enabled) ? LimitValue : VanillaLimit;
                //注：这里只写游戏的字段、不改任何配置项，原来还调了一次 Config.Save() —— 纯属无谓的磁盘写
                //（挂了场景切换重试后会变成每次换场景都写一次），故去掉；配置落盘统一由 SR.FlushConfig 节流。
            } catch (Exception __ex) {
                SR.Guard.Log("应用建造上限", __ex);
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
