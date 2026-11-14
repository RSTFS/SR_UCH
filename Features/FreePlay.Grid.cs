// 自由模式 · 其它 → 地图网格：让游戏自带的建造网格（Graphpaper）在行动阶段也保持显示。
// 旧版（SR_UCH-SR_UCH.zip / Experiments.cs）就是这么做的，稳定可用：
//   1) prefix 掉 Graphpaper.disableGrid（游戏进 PLAY 阶段时调用它把网格淡出）；
//   2) 开关变化时对场景里所有 Graphpaper 直接 enableGrid / disableGrid，做到随开随关。
// 之前那版自己用 GL.LINES 画了一张世界网格，实测无效（不跟随关卡网格、且 OnPostRender 时常不触发），
// 已整块删除，改为复用游戏自己的网格对象。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    [SrFeature("sr.freeplay.grid", "FreePlayGrid", "2.0", Modes = (int)(SRApi.ModeMask.Freeplay))]
    public class FreePlayGrid : ITweak {
        private const string Sec = "自由模式";
        private static ConfigEntry<bool> _alwaysOn;
        private static bool _lastOn;

        // 功能是否正在生效（patch 与 Tick 共用的唯一判据）。
        internal static bool Active {
            get {
                // 只在自由模式生效（统一门控可豁免）；开关照常可开，非自由模式下无效。
                try { return SRApi.GateMaster && _alwaysOn != null && _alwaysOn.Value && SRApi.FeatureAllowed(typeof(FreePlayGrid)); }
                catch { return false; }
            }
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SRApi.LocKey(Sec, "Grid Always On", "常显网格", "Always show grid");
                SRApi.LocDesc(Sec, "Grid Always On",
                    "常显网格\n行动状态下也显示建造网格（游戏默认只在建造阶段显示）\n随开随关：开启立即淡入，关闭立即淡出",
                    "Always show grid\nkeeps the build grid visible during the play phase (vanilla shows it while building only)\ntakes effect immediately when toggled");

                _alwaysOn = plugin.Config.Bind(Sec, "Grid Always On", false, "常显网格：行动状态下也显示建造网格。");
                _lastOn = _alwaysOn.Value;
                // 本功能的补丁单独注册（CreateAndPatchAll 不递归发现独立类型）；
                // 目标失效只丢本功能，不影响后面的 Tick 注册。
                try { Harmony.CreateAndPatchAll(typeof(GridKeepOnPatch), (string)null); }
                catch (Exception __p) { SRApi.LogWarn("地图网格/补丁跳过（Graphpaper.disableGrid 不存在）: " + __p.Message); }
                SRApi.RegisterTick("自由模式-地图网格", Tick);
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/初始化", __ex); }
        }

        // 随开随关 + 换场景后补回：游戏自己的阶段事件会在换关时把网格收掉，这里只做"应开则开"。
        private static void Tick() {
            try {
                if (_alwaysOn == null) return;
                bool on = Active;
                bool changed = on != _lastOn;
                _lastOn = on;
                if (on) ApplyToAll(true);
                else if (changed) ApplyToAll(false);   // 只在"刚关掉"时收一次，之后交回游戏自己控制
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/每帧", __ex); }
        }

        private static void ApplyToAll(bool enable) {
            try {
                Graphpaper[] all = UnityEngine.Object.FindObjectsOfType<Graphpaper>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++) {
                    Graphpaper gp = all[i];
                    if (gp == null) continue;
                    try {
                        if (enable) gp.enableGrid();
                        else gp.disableGrid();
                    } catch (Exception __one) { SRApi.Guard.Log("地图网格/单个网格", __one); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/应用", __ex); }
        }

        // 自由模式页「其它」子页：只渲染这一行。
        internal static void RenderOtherPage() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 地图网格 -", "- Map grid -"));
                SRApi.Ctl.LabeledRow(SRApi.T("常显网格", "Always show grid"),
                    SRApi.T("行动状态下也显示建造网格\n随开随关：开启立即淡入，关闭立即淡出",
                        "keeps the build grid visible during the play phase\ntakes effect immediately when toggled"),
                    _alwaysOn);
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/渲染", __ex); }
        }
    }

    // 游戏进入行动阶段时会调用 Graphpaper.disableGrid() 把网格淡出；开启常显网格后跳过它。
    [HarmonyPatch(typeof(Graphpaper), "disableGrid")]
    internal static class GridKeepOnPatch {
        [HarmonyPrefix]
        private static bool Prefix() {
            try { return !FreePlayGrid.Active; }
            catch { return true; }
        }
    }
}
