// 回合 HUD · 设置页（RenderPage1 / RenderPage2）。
// 从 HUD.cs 拆出来（该文件同时装着定义表、样式、绘制与设置页）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class HUD {
        private static void RenderPage1() {
            try {
                List<ConfigEntryBase> list = SRApi.SectionEntries(Sec);
                if (list == null || list.Count == 0) { GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
                SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Config -"));
                SRApi.RenderSectionEntries(Sec, _rowConfig);
                SRApi.Ctl.SectionTitle(SRApi.T("- 外观 -", "- Appearance -"));
                SRApi.RenderSectionEntries(Sec, _rowAppearance);
                SRApi.Ctl.SectionTitle(SRApi.T("- 行为 -", "- Behavior -"));
                SRApi.RenderSectionEntries(Sec, _rowBehavior);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 设置页", __ex); }
        }

        private static void RenderPage2() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 显示方案 -", "- Display mode -"));
                SRApi.RenderSectionEntries(Sec, _rowDisplay);
                SRApi.Ctl.SectionTitle(SRApi.T("- 一般 -", "- General -"));
                for (int i = 0; i <= 4; i++) RenderContentRow(_content[i]);
                SRApi.Ctl.SectionTitle(SRApi.T("- 其它 -", "- Other -"));
                RenderContentRow(_content[5]);
                RenderContentRow(_content[6]);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 显示内容页", __ex); }
        }
    }
}
