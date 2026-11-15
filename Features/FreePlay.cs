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
                // 自由模式页拆成三个分页：相机与自身 / 地图 / 重生点。
                SRApi.RegisterPage(Sec, "main", "一般", "General", 10, RenderAll);
                SRApi.RegisterPage(Sec, "map", "地图", "Map", 20, RenderMapPage);
                SRApi.RegisterPage(Sec, "spawn", "重生点", "Spawn points", 30, RenderSpawnPage);
                FreePlayMap.Initialize(plugin);
                SpawnPointTools.Initialize(plugin);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/初始化", __ex); }
        }

        // 地图页：开关 + 视野 + 打开按钮 + 说明（地图本身是覆盖在主窗口之上的全屏视图）。
        private static void RenderMapPage() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("地图", "Map") + " -",
                    SRApi.T("俯视查看整张关卡平面图\n打开后可拖动平移、滚轮缩放、右键操作\n仅自由模式可用",
                            "overhead view of the whole level\ndrag to pan, wheel to zoom, right-click for actions\nfreeplay only"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Map Enabled" || e.Definition.Key == "Map FOV";
                });
                GUILayout.Space(SRApi.Ctl.Sc(6));
                string label = FreePlayMap.IsOpen ? SRApi.T("关闭地图", "Close map") : SRApi.T("打开地图", "Open map");
                if (SRApi.Ctl.ButtonRow(new string[] { label }, new float[] { 120f }, 24f) == 0) FreePlayMap.Toggle();
                GUILayout.Space(SRApi.Ctl.Sc(4));
                GUILayout.Label(SRApi.T("右键菜单：传送到此处 / 添加重生点 / 删除最近的重生点 / 清空重生点 / 关闭地图",
                    "Right-click menu: teleport here / add spawn point / remove nearest / clear / close map"), SRApi.Ctl.Label);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图页/渲染", __ex); }
        }

        private static void RenderSpawnPage() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("重生点", "Spawn points") + " -",
                    SRApi.T("自定义出生位置，配合地图页右键使用\n重生会传送到最近的重生点\n没有自定义点时回到游戏默认出生点",
                            "custom spawn positions, used from the map page's right-click menu\nrespawn teleports to the nearest point\nfalls back to the game's default spawn"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Spawn Points";
                });
                GUILayout.Space(SRApi.Ctl.Sc(6));
                SpawnPointTools.RenderRows();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式重生点页/渲染", __ex); }
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
