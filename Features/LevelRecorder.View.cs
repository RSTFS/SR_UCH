// 关卡记录器 · 查看页（LevelRecorder 分部）
//
// 「查看方便/清晰」的取舍：不做长滚动列表。这个项目的滚轮架构换过六代，
// 长滚动容易和页面钩子/表格滚轮抢事件；改用「过滤 + 分页 + 展开详情 + 一键复制」，
// 一屏看完一页，翻页是显式按钮，行为完全可预期。
//
// 控件一律走核心 Ctl 门面；不裸用 GUILayout.BeginHorizontal / GUI.enabled。

using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class LevelRecorder {
        // ---------- 查看页配置 ----------
        private static ConfigEntry<bool> _viewArchive, _logDetail, _onlyKey;
        private static ConfigEntry<int> _pageSize;

        // ---------- 页面状态（不落盘，打开面板时保持，换局不复位） ----------
        private static string _filterKind = "all", _filterRound = "all", _filterPlayer = "all", _filterText = "";
        private static bool _openKind, _openRound, _openPlayer, _openPreset;
        private static int _page, _detailSeq = -1;
        private static string[] _kindVals, _kindLabels, _roundVals, _roundLabels, _playerVals, _playerLabels;
        private static int _filterSig = -1;
        private static string[] _presetItems = new string[0];
        private static string _selPresetKey = "all";
        private static bool _clearArmed;

        private static void BindViewConfig(IFeatureHost plugin) {
            _viewArchive = plugin.Config.Bind(Sec, "View Archive", false, "查看上一局归档，而不是当前局。局末自动归档，换局不清空。");
            _logDetail = plugin.Config.Bind(Sec, "Export Details", true, "复制 / 导出时附带每条的详情（坐标、方块 ID 等）。");
            _onlyKey = plugin.Config.Bind(Sec, "Key Events Only", false, "只看关键事件（隐藏聊天、得分、阶段、移除、系统）。");
            _pageSize = plugin.Config.Bind(Sec, "Page Size", 30,
                new ConfigDescription("时间线每页显示多少条（10-80）。", new AcceptableValueRange<int>(10, 80)));
        }

        // ================= 过滤 =================

        private static void EnsureFilterOptions(List<Rec> recs) {
            int sig = recs.Count * 31 + (recs.Count > 0 ? recs[recs.Count - 1].seq : 0) + (ViewArchive ? 7 : 0);
            if (sig == _filterSig && _kindVals != null) return;
            _filterSig = sig;

            string[] names = Enum.GetNames(typeof(Kind));
            _kindVals = new string[names.Length + 1];
            _kindLabels = new string[names.Length + 1];
            _kindVals[0] = "all";
            _kindLabels[0] = SRApi.T("全部类型", "All types");
            for (int i = 0; i < names.Length; i++) {
                _kindVals[i + 1] = names[i];
                _kindLabels[i + 1] = KindName((Kind)Enum.Parse(typeof(Kind), names[i]));
            }

            int maxRound = 1;
            var players = new SortedSet<int>();
            for (int i = 0; i < recs.Count; i++) {
                if (recs[i].round > maxRound) maxRound = recs[i].round;
                if (recs[i].player >= 0) players.Add(recs[i].player);
            }
            _roundVals = new string[maxRound + 1];
            _roundLabels = new string[maxRound + 1];
            _roundVals[0] = "all";
            _roundLabels[0] = SRApi.T("全部回合", "All rounds");
            for (int i = 1; i <= maxRound; i++) {
                _roundVals[i] = i.ToString(CultureInfo.InvariantCulture);
                _roundLabels[i] = SRApi.T("第 ", "Round ") + i;
            }
            _playerVals = new string[players.Count + 1];
            _playerLabels = new string[players.Count + 1];
            _playerVals[0] = "all";
            _playerLabels[0] = SRApi.T("全部玩家", "All players");
            int k = 1;
            foreach (int p in players) {
                _playerVals[k] = p.ToString(CultureInfo.InvariantCulture);
                _playerLabels[k] = "#" + p;
                k++;
            }
            if (Array.IndexOf(_kindVals, _filterKind) < 0) _filterKind = "all";
            if (Array.IndexOf(_roundVals, _filterRound) < 0) _filterRound = "all";
            if (Array.IndexOf(_playerVals, _filterPlayer) < 0) _filterPlayer = "all";
        }

        private static bool IsKeyEvent(Kind k) {
            switch (k) {
                case Kind.Chat: case Kind.Remove: case Kind.Point:
                case Kind.Phase: case Kind.System: return false;
                default: return true;
            }
        }

        internal static List<Rec> Filtered(List<Rec> recs) {
            var outp = new List<Rec>();
            if (recs == null) return outp;
            bool keyOnly = _onlyKey != null && _onlyKey.Value;
            string needle = string.IsNullOrEmpty(_filterText) ? "" : _filterText.Trim();
            for (int i = 0; i < recs.Count; i++) {
                Rec r = recs[i];
                if (_filterKind != "all" && r.kind.ToString() != _filterKind) continue;
                if (_filterRound != "all" && r.round.ToString(CultureInfo.InvariantCulture) != _filterRound) continue;
                if (_filterPlayer != "all" && r.player.ToString(CultureInfo.InvariantCulture) != _filterPlayer) continue;
                if (keyOnly && !IsKeyEvent(r.kind)) continue;
                if (needle.Length > 0 &&
                    (r.text == null || r.text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) &&
                    (r.detail == null || r.detail.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                outp.Add(r);
            }
            return outp;
        }

        // ================= 时间线页 =================

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private static string Clip(string s, int n) {
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s ?? "";
            return s.Substring(0, n) + "...";
        }

        private static void RenderTimelinePage() {
            List<Rec> recs = CurrentRecs();
            List<Snap> snaps = CurrentSnaps();
            EnsureFilterOptions(recs);
            List<Rec> view = Filtered(recs);

            // 状态条：一眼看清在看哪一局、有多少内容。
            SRApi.Ctl.SectionTitle(SRApi.T("- 状态 -", "- Status -"));
            string scope = ViewArchive ? SRApi.T("上一局", "Last match") : SRApi.T("当前局", "Current match");
            Line("[" + scope + "] " + (ViewArchive ? _archTitle : _matchTitle) + " · " +
                 recs.Count + SRApi.T(" 条事件 / ", " events / ") + snaps.Count + SRApi.T(" 份快照", " snapshots"),
                 "[" + scope + "] " + (ViewArchive ? _archTitle : _matchTitle) + " · " +
                 recs.Count + " events / " + snaps.Count + " snapshots");

            // 过滤行：三个下拉 + 搜索框。
            SRApi.Ctl.SectionTitle(SRApi.T("- 筛选 -", "- Filter -"));
            SRApi.Ctl.FilterCombo("rec.kind", _kindVals, _kindLabels,
                () => _filterKind, v => { _filterKind = v; _page = 0; }, ref _openKind, "all", SRApi.Ctl.Sc(130f));
            SRApi.Ctl.FilterCombo("rec.round", _roundVals, _roundLabels,
                () => _filterRound, v => { _filterRound = v; _page = 0; }, ref _openRound, "all", SRApi.Ctl.Sc(110f));
            SRApi.Ctl.FilterCombo("rec.player", _playerVals, _playerLabels,
                () => _filterPlayer, v => { _filterPlayer = v; _page = 0; }, ref _openPlayer, "all", SRApi.Ctl.Sc(110f));
            string q = SRApi.ClearableTextField(_filterText, SRApi.Ctl.SearchBox, GUILayout.Width(SRApi.Ctl.Sc(180f)));
            if (q != _filterText) { _filterText = q ?? ""; _page = 0; }
            Line(SRApi.T("筛选结果：", "Filtered: ") + view.Count + SRApi.T(" 条（点击任意一行展开详情）", " rows (tap a row for details)"),
                 "Filtered: " + view.Count + " rows (tap a row for details)");

            // 分页：显式按钮，不依赖滚轮。
            int size = _pageSize != null ? Mathf.Clamp(_pageSize.Value, 10, 80) : 30;
            int pages = Mathf.Max(1, (view.Count + size - 1) / size);
            if (_page >= pages) _page = pages - 1;
            if (_page < 0) _page = 0;
            int from = _page * size;
            int to = Mathf.Min(view.Count, from + size);

            SRApi.Ctl.SectionTitle(SRApi.T("- 时间线 -", "- Timeline -"));
            for (int i = from; i < to; i++) {
                Rec r = view[i];
                string nm = r.kind == Kind.Place && r.pid >= 0 ? NameOf(r.pid) : null;
                string body = !string.IsNullOrEmpty(nm) && r.text.IndexOf("#" + r.pid, StringComparison.Ordinal) >= 0
                    ? r.text.Replace("#" + r.pid, nm) : r.text;
                string row = "[" + Clock(r.at) + "] R" + r.round + "  " + KindName(r.kind) + "  " +
                             (r.player >= 0 ? "#" + r.player + "  " : "") + Clip(body, 88);
                if (SRApi.Ctl.Button(row, SRApi.T("点击展开 / 收起详情", "Tap to expand / collapse details")))
                    _detailSeq = (_detailSeq == r.seq) ? -1 : r.seq;
                if (_detailSeq == r.seq) {
                    string det = string.IsNullOrEmpty(r.detail)
                        ? SRApi.T("（这条没有额外详情）", "(no extra details for this row)")
                        : r.detail;
                    if (r.snap >= 0 && r.snap < snaps.Count)
                        det += SRApi.T(" · 关联快照：", " · linked snapshot: ") + snaps[r.snap].label;
                    Line("    └ " + det, "    └ " + det);
                }
            }
            if (view.Count == 0)
                Line(SRApi.T("没有符合条件的记录。放宽筛选，或先打一局。", "No matching rows. Loosen the filter or play a match first."),
                     "No matching rows. Loosen the filter or play a match first.");

            SRApi.Ctl.SectionTitle(SRApi.T("- 翻页与导出 -", "- Pages & export -"));
            Line(SRApi.T("第 ", "Page ") + (_page + 1) + " / " + pages + SRApi.T(" 页 · 显示 ", " · showing ") +
                 (to - from) + SRApi.T(" 条", " rows"), "Page " + (_page + 1) + " / " + pages + " · showing " + (to - from) + " rows");
            if (SRApi.Ctl.Button(SRApi.T("← 上一页", "← Prev"), SRApi.T("往前翻一页", "Previous page")) && _page > 0) _page--;
            if (SRApi.Ctl.Button(SRApi.T("下一页 →", "Next →"), SRApi.T("往后翻一页", "Next page")) && _page < pages - 1) _page++;
            if (SRApi.Ctl.Button(SRApi.T("跳到最新", "Jump to latest"), SRApi.T("直接看最后一页", "Go to the last page"))) _page = pages - 1;

            if (SRApi.Ctl.Button(SRApi.T("复制筛选结果", "Copy filtered"), SRApi.T("只复制当前筛选出来的行", "Copy only the filtered rows")))
                CopyText(BuildText(recs, snaps, true));
            if (SRApi.Ctl.Button(SRApi.T("复制整局", "Copy whole match"), SRApi.T("复制全部事件（含详情开关）", "Copy all events (details per setting)")))
                CopyText(BuildText(recs, snaps, false));
            if (SRApi.Ctl.Button(SRApi.T("导出到文件", "Export to file"), SRApi.T("写入 BepInEx/config/SR_UCH/records/", "Write to BepInEx/config/SR_UCH/records/")))
                ExportToFile();

            SRApi.Ctl.SectionTitle(SRApi.T("- 局切换 -", "- Match scope -"));
            SRApi.Ctl.HotkeyButton("rec.scope", ViewArchive ? SRApi.T("切回当前局", "Switch to current") : SRApi.T("查看上一局", "View last match"),
                                   SRApi.T("上一局在局末自动归档，换局不清空", "Last match is archived at game end"),
                                   delegate { _viewArchive.Value = !_viewArchive.Value; _page = 0; _filterSig = -1; });
            Line(SRApi.T("死亡记录只覆盖本机角色（游戏只在 hasAuthority 时派发死亡事件）；其余放置 / 得分 / 聊天 / 到达均为全场。",
                         "Death rows cover the local character only (the game emits them only with authority); placements / points / chat / finishes cover the whole room."),
                 "Death rows cover the local character only; other rows cover the whole room.");
        }

        // ================= 布局快照页 =================

        private static void RenderLayoutsPage() {
            List<Snap> snaps = CurrentSnaps();
            SRApi.Ctl.SectionTitle(SRApi.T("- 本局布局快照 -", "- Layout snapshots -"));
            Line(SRApi.T("共 ", "Total ") + snaps.Count + SRApi.T(" 份；上限 ", " snapshots; cap ") + MaxSnaps +
                 SRApi.T("。每份都能一键存成布局预设，之后在「布局预设」页加载。", ". Each can be saved as a layout preset and loaded from the Layout Presets page."),
                 "Total " + snaps.Count + "; cap " + MaxSnaps + ". Each can be saved as a preset.");
            if (_fSnapshot == null)
                Line(SRApi.T("布局预设功能尚未绑定（拆分构建下缺少该 DLL，或刚启动还在重试）。", "Layout Presets slots are not bound yet (missing split DLL or still retrying)."),
                     "Layout Presets slots are not bound yet.");

            for (int i = 0; i < snaps.Count; i++) {
                Snap s = snaps[i];
                string head = "[" + Clock(s.at) + "] R" + s.round + "  " + s.label +
                              SRApi.T("（", " (") + s.blocks + SRApi.T(" 块）", " blocks)");
                Line(head, head);
                if (_fSavePreset != null) {
                    int idx = i;
                    if (SRApi.Ctl.Button(SRApi.T("  存为布局预设", "  Save as layout preset"),
                                         SRApi.T("沿用布局预设的全部校验与索引落盘", "Uses the preset feature's validation and index"))) {
                        string name = "Rec R" + s.round + "-" + Clock(s.at).Replace(":", "");
                        bool ok = _fSavePreset(name, s.xml);
                        SRApi.NotifyT(ok ? "已存为预设：{0}" : "保存失败：{0}", ok ? "Saved preset: {0}" : "Save failed: {0}", name);
                    }
                }
                if (SRApi.Ctl.Button(SRApi.T("  复制这份 XML", "  Copy this XML"), SRApi.T("复制快照原文，可贴到布局预设的导入框", "Copy raw XML for the preset import box")))
                    CopyText(s.xml);
            }
            if (SRApi.Ctl.Button(SRApi.T("立刻抓一份当前布局", "Capture current layout now"),
                                 SRApi.T("不等待回合切换，手动记一份", "Capture without waiting for the next round")))
                CaptureSnapshot(SRApi.T("手动", "Manual"), CurrentRound());

            SRApi.Ctl.SectionTitle(SRApi.T("- 已有布局预设 -", "- Saved layout presets -"));
            if (SRApi.Ctl.Button(SRApi.T("刷新预设列表", "Refresh preset list"), SRApi.T("重新读取磁盘上的预设索引", "Re-read presets from disk"))) {
                _presetItems = _fListPresets != null ? _fListPresets() : new string[0];
                _filterSig = -1;
            }
            if (_presetItems.Length == 0)
                Line(SRApi.T("还没有读到预设，点一次「刷新预设列表」。", "No presets read yet; tap Refresh preset list."),
                     "No presets read yet; tap Refresh preset list.");
            else {
                string[] vals = new string[_presetItems.Length + 1];
                string[] labels = new string[_presetItems.Length + 1];
                vals[0] = "all";
                labels[0] = SRApi.T("选择一个预设", "Pick a preset");
                for (int i = 0; i < _presetItems.Length; i++) {
                    int bar = _presetItems[i].IndexOf('|');
                    vals[i + 1] = bar > 0 ? _presetItems[i].Substring(0, bar) : _presetItems[i];
                    labels[i + 1] = bar > 0 ? _presetItems[i].Substring(bar + 1) : _presetItems[i];
                }
                SRApi.Ctl.FilterCombo("rec.preset", vals, labels,
                    () => _selPresetKey, v => _selPresetKey = v, ref _openPreset, "all", SRApi.Ctl.Sc(240f));
                if (_selPresetKey != "all" && _aLoadPreset != null) {
                    if (SRApi.Ctl.Button(SRApi.T("载入此预设", "Load this preset"),
                                         SRApi.T("走布局预设的原生重载流程，会重新加载场景", "Uses the preset's reload flow; the scene will reload")))
                        _aLoadPreset(_selPresetKey);
                }
            }
            Line(SRApi.T("联动是双向的：布局预设发起载入时，时间线会自动记一条「发起载入布局预设」。",
                         "The link is two-way: loading a preset leaves a 'requested layout preset' row on the timeline."),
                 "The link is two-way: preset loads are recorded on the timeline.");
        }

        // ================= 设置页 =================

        private static void RenderOptionsPage() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 记录 -", "- Recording -"));
            CfgRow(_enabled);
            CfgRow(_snapEachRound);
            CfgRow(_autoExport);
            CfgRow(_logPhaseEnd);
            CfgRow(_hideCommands);
            CfgRow(_maxEvents);
            CfgRow(_copyKey);
            CfgRow(_exportKey);

            SRApi.Ctl.SectionTitle(SRApi.T("- 查看 -", "- Viewing -"));
            CfgRow(_viewArchive);
            CfgRow(_logDetail);
            CfgRow(_onlyKey);
            CfgRow(_pageSize);

            SRApi.Ctl.SectionTitle(SRApi.T("- 清空 -", "- Clear -"));
            if (SRApi.Ctl.Button(_clearArmed ? SRApi.T("再点一次确认清空本局", "Tap again to confirm clear")
                                             : SRApi.T("清空本局记录", "Clear current match"),
                                 SRApi.T("只清内存，不删已导出的文件；归档不受影响", "Memory only; exported files and the archive are untouched"))) {
                if (_clearArmed) { ResetMatch(); _clearArmed = false; _filterSig = -1; _page = 0; }
                else _clearArmed = true;
            }

            SRApi.Ctl.SectionTitle(SRApi.T("- 说明 -", "- Notes -"));
            Line("只房主安装即可记录全场：服务器把放置 / 得分 / 聊天 / 到达消息转发给所有客户端，房主端读得到。房客零安装，屏幕上看不到本页。",
                 "Host-only install records the whole room: the server relays placements, points, chat and finishes to every client. Guests see no panel.");
            Line("全部数据都是值，不持有 Unity 对象；换局自动清空，局末归档上一局。不新增网络消息、补丁、反射登记，不改任何游戏判定。",
                 "Only value types are stored; cleared on a new match, previous match archived at game end. No new messages, patches or reflection entries.");
            Line("时间用 Time.unscaledTime，梦境定格时记录照常走。", "Timing uses unscaledTime, so recording keeps working during a local time freeze.");
        }
    }
}
