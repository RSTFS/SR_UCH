// 实验栏目：注册侧栏栏目与三页（p1 边界 / p2 其它 / p3 场景，各自 partial 文件）。

using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
    {
        private static void SelfReg() {
            SRApi.LocSec("Experiments", "实验", null);
            SRApi.Nav("Experiments", 999); // 栏位顺序值（越小越靠前）
            // 页签顺序由 Order 决定：p3 场景(10) / p1 边界(20) / p2 其它(30)；id 与文件对应固定。
            SRApi.RegisterPage("Experiments", "p1", "关卡边界", "Bounds", 20, Render);
            SRApi.RegisterPage("Experiments", "p2", "其它", "Misc", 30, RenderPage2);
            SRApi.RegisterPage("Experiments", "p3", "场景", "Scene", 10, RenderPage3);
        }

        // 状态行只读可复制；高度须用 Ctl.TextHeight(text, w)，固定高度会裁掉下沉字形。
        private static void DrawStatus(string text) {
            try {
                if (string.IsNullOrEmpty(text)) return;
                float w = Mathf.Max(SRApi.Ctl.Sc(160), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(40));
                float h = SRApi.Ctl.TextHeight(text, w);
                GUILayout.Label(text, SRApi.Ctl.LabelWrap, GUILayout.Width(w), GUILayout.Height(h));
            } catch (Exception __ex) { SRApi.Guard.Log("状态行", __ex); }
        }

        public void Initialize(IFeatureHost plugin)
        {
            SelfReg();
            InitCleanup(plugin);
            try { CrossPlay.Init(); } catch (Exception __cp) { SRApi.Guard.Log("跨平台联机 初始化", __cp); }
        }
}
}
