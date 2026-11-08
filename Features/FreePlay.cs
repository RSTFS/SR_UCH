// 自由模式页：单页三分区（自由相机 / 自身状态 / 其它），全部由 SR 自己实现，不依赖 EX。

using System;
using BepInEx.Configuration;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    [SrFeature("sr.freeplay", "FreePlay", "1.0")]
    public class FreePlayPage : ITweak {
        private const string Sec = "自由模式";

        public void Initialize(IFeatureHost plugin) {
            try {
                SRApi.LocKey(Sec, "FreePlay Page", "自由模式", "Freeplay");
                // 侧栏栏目必须显式 Nav 注册：_navOrder 只由 SR.Nav 填充，
                // 只调 RegisterPage 的栏目不会出现在左栏（这就是"加了但侧栏没有"的原因）。
                SRApi.Nav(Sec, 85);
                // 单页三分区（用户要求：不要页签）
                SRApi.RegisterPage(Sec, RenderAll);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/初始化", __ex); }
        }

        // 单页三分区：自由相机 → 自身状态 → 其它
        private static void RenderAll() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("自由相机", "Free camera") + " -",
                    SRApi.T("开启后可以用滚轮自由缩放视野\n关闭后完全恢复游戏默认相机\n只在自由模式有效",
                            "wheel zooms the view freely while on\noff restores the default camera\nfreeplay only"));
                SRApi.RenderSectionEntries("自由模式", delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Free Camera" || e.Definition.Key == "Free Camera FOV"
                        || e.Definition.Key == "Free Camera Speed";
                });
                // 视野快捷键（原来由 EX 页的行 companion 提供，EX 那份删掉后这行消失）。
                // 拆分成多个 DLL 后不能直接引用 Experiments 里的 FreeCam 类型 → 按配置项名字取。
                ConfigEntryBase camKey = SRApi.Ctl.FindEntry("Experiments", "Free Camera Key");
                if (camKey != null) {
                    SRApi.Ctl.Row(SRApi.T("视野快捷键", "Camera key"),
                        SRApi.T("按下开关自由相机\n可改绑，支持组合键", "toggles the free camera\nrebindable, combos supported"),
                        camKey);
                }
                GUILayout.Space(SRApi.Ctl.Sc(4));

                SRApi.Ctl.SectionTitle("- " + SRApi.T("自身状态", "Self state") + "-");
                // 自身状态由 SR 自己实现（完全独立于 EX）：无敌 / 完美无敌 / 飞天 / 蹲移 / 无视对象 / 无视碰撞。
                SelfState.RenderRows();
                GUILayout.Space(SRApi.Ctl.Sc(4));

                FreePlayGrid.RenderOtherPage();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/渲染", __ex); }
        }

        // 「其它」子页：地图网格（SR 自己实现，不依赖 EX）
        private static void RenderGridTab() {
            try {
                // 不按模式隐藏/灰显：照常显示，效果由各功能自己在非自由模式下跳过
                FreePlayGrid.RenderOtherPage();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/其它", __ex); }
        }

    }
}
