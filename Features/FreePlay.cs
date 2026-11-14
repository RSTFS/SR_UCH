// 自由模式页：单页三分区（自由相机 / 自身状态 / 其它），全部由 SR 自己实现。

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
                SRApi.LocSec(Sec, "自由模式", "FreePlay");   // 侧栏栏目中英名（缺 LocSec 时英文界面仍显示中文）
                SRApi.LocKey(Sec, "FreePlay Page", "自由模式", "FreePlay");
                // 侧栏栏目必须显式 Nav 注册：_navOrder 只由 SR.Nav 填充，只调 RegisterPage 不显示。
                SRApi.Nav(Sec, 100);
                SRApi.RegisterPage(Sec, RenderAll);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/初始化", __ex); }
        }

        private static void RenderAll() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("自由相机", "Free camera") + " -",
                    SRApi.T("开启后可以用滚轮自由缩放视野\n关闭后完全恢复游戏默认相机\n只在自由模式有效",
                            "wheel zooms the view freely while on\noff restores the default camera\nfreeplay only"));
                SRApi.RenderSectionEntries("自由模式", delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Free Camera" || e.Definition.Key == "Free Camera FOV"
                        || e.Definition.Key == "Free Camera Speed";
                });
                // 视野快捷键与自由相机开关同一行：由核心的「行 companion」机制画在复选框右边。
                GUILayout.Space(SRApi.Ctl.Sc(4));

                SRApi.Ctl.SectionTitle("- " + SRApi.T("自身状态", "Self state") + "-");
                SelfState.RenderRows();
                GUILayout.Space(SRApi.Ctl.Sc(4));

                FreePlayGrid.RenderOtherPage();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/渲染", __ex); }
        }

    }
}
