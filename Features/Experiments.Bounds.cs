// 实验 · 关卡边界页：页面渲染与条目行。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
[SrFeature("sr.experiments", "Experiments", "1.0")]
public partial class Experiments : ITweak
    {
        // 统一控件：标签 + 控件交给核心排版，标签宽度按文字实测。
        private static void AdvRow(string key, string zh, string en, string zhTip, string enTip) {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry("Experiments", key);
                if (e == null) return;
                SRApi.Ctl.Row(SRApi.T(zh, en), SRApi.T(zhTip, enTip), e, 200f);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染进阶条目", __ex); }
        }

        private static void Row(string zh, string en, string zhTip, string enTip, string key) {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry("Experiments", key);
                if (e == null) return;
                SRApi.Ctl.Row(SRApi.T(zh, en), SRApi.T(zhTip, enTip), e, 200f);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染关卡边界条目", __ex); }
        }

        public static void Render() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.Tid("6e2347e525", "解除关卡边界", "Unlock level bounds"));
                Row("解除关卡边界", "Unlock level bounds",
                    "解除关卡边界：放开关卡四周的空间限制.\n"
                    + "开启后实体墙、放置光标、相机取景、界外黑幕与方块越界放置同时解除; 关闭即逐项还原.",
                    "unlocks the level's spatial bounds\n"
                    + "removes the solid walls, placement-cursor bounds, camera framing, the out-of-bounds overlay and out-of-bounds placement at once; turning it off restores each one.",
                    "Level Bounds");
                GUILayout.Space(SRApi.Ctl.Sc(6));
                SRApi.Ctl.SectionTitle(SRApi.Tid("a17ecc051e", "进阶（一般不用改）", "Advanced (rarely needed)"));
                AdvRow("Level Bounds Radius", "越界范围", "Unlock radius",
                    "硬解除半径：光标与相机可到关卡外的距离, 不限范围.\n"
                    + "值越大可走得越远; 相机取景与放置光标的边界都按这个值换掉.",
                    "hard unlock radius: how far the cursor and camera can reach outside the level, no upper limit\n"
                    + "larger values reach farther; both the camera framing and the placement-cursor bounds use this value.");
                GUILayout.Space(SRApi.Ctl.Sc(6));
            } catch (Exception __ex) { SRApi.Guard.Log("实验区一 渲染", __ex); }
        }
}
}
