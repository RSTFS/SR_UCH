// 布局预设 · 页面头部 UI：保存/导入/导出、搜索/排序/筛选、工具行。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
        private static void RenderSaveImportHeader() {
            SRApi.Ctl.SectionTitle(SRApi.T("保存 / 导入 / 导出", "Save / Import / Export"));
            using (var __row = SRApi.Ctl.Row()) {
            
                GUILayout.Label(SRApi.T("预设名称", "Preset name"), SRApi.Ctl.Label,
                    GUILayout.Width(FormLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string typed = SRApi.ClearableTextField(_newName ?? "", 40, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(220)), GUILayout.Height(W(24)));
                if (typed != null && typed != _newName) _newName = typed;
                if (SRApi.Ctl.Button(SaveButtonText(), SRApi.T("把当前方块布局保存成一条命名预设\n保存到预设目录，之后可一键加载", "saves the current block layout as a named preset\nstored in the presets folder for one-click loading"), 120f, 24f)) SaveCurrent();
                GUILayout.FlexibleSpace();
            }

            using (var __row = SRApi.Ctl.Row()) {
            
                GUILayout.Label(SRApi.T("XML 路径", "XML path"), SRApi.Ctl.Label,
                    GUILayout.Width(FormLabelWidth()), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string path = SRApi.ClearableTextField(_importPath ?? "", 260, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(220)), GUILayout.Height(W(24)));
                if (path != null && path != _importPath) _importPath = path;
                GUILayout.FlexibleSpace();
            }

            using (var __row = SRApi.Ctl.Row()) {
            
                GUILayout.Space(FormLabelWidth());
                if (SRApi.Ctl.Button(SRApi.T("从路径导入", "Import path"), SRApi.T("读取上面填写的 XML 路径并导入成一条预设", "imports the XML file at the path above as a preset"), 110f, 24f)) ImportFromPath();
                if (SRApi.Ctl.Button(SRApi.T("从剪贴板导入", "Import clipboard"), SRApi.T("把剪贴板里的预设文本导入成一条预设", "imports preset text from the clipboard as a preset"), 120f, 24f)) ImportFromClipboard();
                if (SRApi.Ctl.Button(SRApi.T("全部导出到目录", "Export all to folder"),
                        SRApi.T("把所有预设 XML 复制到 预设目录/exports/<时间>/ 下（按模式分子目录），并复制一份 index.json",
                            "copies every preset XML into presets/exports/<time>/ (grouped by mode) and copies index.json as well"), 140f, 24f)) ExportAllToDirectory();
                GUILayout.FlexibleSpace();
            }
        }

        // 头部二：搜索 / 排序 / 筛选（固定 2 行 + 标签列对齐）
        private static void RenderFilterHeader() {
            SRApi.Ctl.SectionTitle(SRApi.T("搜索 / 排序 / 筛选", "Search / sort / filter"));
            using (var __row = SRApi.Ctl.Row()) {
            
                RowLabel(SRApi.T("搜索", "Search"));
                Rect searchRect = GUILayoutUtility.GetLastRect();
                Event evSearch = Event.current;
                if (evSearch != null && evSearch.type == EventType.MouseDown && evSearch.button == 0
                    && searchRect.Contains(evSearch.mousePosition)) {
                    _search = "";   // 点标签清空搜索
                    evSearch.Use();
                }
                string q = SRApi.ClearableTextField(_search ?? "", 40, SRApi.Ctl.SearchBox,
                    GUILayout.Width(W(110)), GUILayout.Height(W(24)));
                if (q != null && q != _search) _search = q;
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("排序", "Sort"));
                RenderSortCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("加载", "Load"));
                RenderLoadModeCombo();
                GUILayout.Space(W(7));
                if (SRApi.Ctl.Button(SRApi.T("刷新", "Refresh"), SRApi.T("重新扫描预设目录", "rescan the presets folder"), 0f, 24f)) {
                    try { _loaded = false; LoadFromDisk(true); } catch (Exception __rx) { SRApi.Guard.Log("布局预设/刷新列表", __rx); }
                }
                GUILayout.FlexibleSpace();
            }

            using (var __row = SRApi.Ctl.Row()) {
            
                RowLabel(SRApi.T("类型", "Type"));
                RenderTypeFilterCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("关卡", "Level"));
                RenderLevelFilterCombo();
                GUILayout.Space(W(8));
                RowLabel(SRApi.T("模式", "Mode"));
                RenderModeFilterCombo();
                GUILayout.FlexibleSpace();
            }
        }

        // 头部三：工具行（跳过树屋 / 返回树屋 / 条数状态），固定一行
        private static void RenderListToolbar() {
            List<PresetInfo> visible = VisiblePresets();
            using (var __row = SRApi.Ctl.Row()) {
            
                if (_skipTreehouse != null) {
                    SRApi.Ctl.RestoreLabel(SRApi.Ctl.Content(SRApi.T("跳过树屋流程", "Skip treehouse"),
                        SRApi.T("跳过树屋流程\n联机替换关卡时直接替换场景，不先回树屋\n房客的关卡状态可能不同步（风险自负）",
                            "skip the treehouse flow\nreplaces the level scene directly online instead of returning to the treehouse first\nclients' level state may be out of sync (at your own risk)")),
                        _skipTreehouse, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(26));
                    SRApi.Ctl.RenderControl(_skipTreehouse);
                }
                GUILayout.Space(SRApi.Ctl.Sc(8));
                if (SRApi.Ctl.HotkeyButtonNoHint("presets.treehouse", SRApi.T("返回树屋", "Back to treehouse"),
                        SRApi.T("走游戏原生的结束对局流程回到树屋（房主有效）", "ends the current match through the game's native flow and returns to the treehouse (host only)"),
                        delegate { ReturnToTreehouse(); }, SRApi.Ctl.Sc(150), SRApi.Ctl.Sc(26))) ReturnToTreehouse();
                GUILayout.Space(SRApi.Ctl.Sc(10));
                RenderListStatus(visible.Count, _presets.Count);
                GUILayout.FlexibleSpace();
            }
        }

    }
}
