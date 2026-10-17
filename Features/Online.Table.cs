// Online 的 partial 分片（只做代码组织，无逻辑改动）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    public partial class Online {
        private static void HeightGrip() {
            Event e = Event.current;
            Rect r = GUILayoutUtility.GetLastRect();
            GUILayout.Space(SRApi.Ctl.Sc(2));
            GUILayout.Box(GUIContent.none, SRApi.Ctl.Footer, GUILayout.Height(SRApi.Ctl.Sc(4)), GUILayout.ExpandWidth(true));
            Rect grip = GUILayoutUtility.GetLastRect();
            if (e == null) return;
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) {
                _dragH = true;
                _dragHY0 = e.mousePosition.y;
                _dragH0 = _tableH != null && _tableH.Value > 0 ? _tableH.Value : (r.height / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f)));
                // 临时值为 0 表示本次按下后尚无真正的 MouseDrag；只点击不拖动时不会把自动高度误写成固定高度。
                _dragHTmp = _tableH != null && _tableH.Value > 0 ? _tableH.Value : 0f;
                SRApi.BlockWindowDrag = true;
                e.Use();
            } else if (e.type == EventType.MouseDrag && _dragH) {
                // _dragH0 是设计单位，鼠标位移是屏幕像素，必须再除一次 SRApi.Ctl.Sc(1)。
                int v = Mathf.Clamp(Mathf.RoundToInt(_dragH0 + (e.mousePosition.y - _dragHY0) / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f))), 120, 1000);
                // 拖动期间只累加到 _dragHTmp，松手时由 SaveTableH 一次性写入配置（每帧写会触发 setter 与范围校验）。
                _dragHTmp = v;
                SRApi.BlockWindowDrag = true;
                e.Use();
            }
        }

        // 落盘统一交给 SRApi.Ctl.MarkConfigDirty（节流写盘，失败有 Warning）。
        // 拖动期间高度只写 _dragHTmp，到这里才同步进配置；核心的拖拽看门狗也会调本方法。
        private static void SaveTableH() {
            if (_tableH != null) {
                if (_dragHTmp > 0f) {
                    int h = Mathf.RoundToInt(_dragHTmp);
                    if (h != _tableH.Value) _tableH.Value = h;
                }
                SRApi.Ctl.MarkConfigDirty(_tableH.ConfigFile);
            }
        }

        private static void SaveEntry(ConfigEntryBase e) { if (e != null) SRApi.Ctl.MarkConfigDirty(e.ConfigFile); }

        // 表格：固定表头 + 房间行 + 自绘竖向滚动条，几何全部用绝对矩形手动偏移。
        private const int HostCol = 7;                 // 房主列靠左显示，其余列居中
        private const int QualityCol = 2;              // 游戏质量列：Shift+单击表头切换技能匹配质量系数
        private const int SkillCol = 3;                // 技能系数列：Shift+单击表头切换最大 / 平均 / 最小
        private const float CodeLabelW = 58f;          // 房间码标签宽度

        // 单击表头：左键按该列排序（升 -> 降 -> 默认，见 CycleSort）；Shift+左键触发列专属切换。
        private static void HeaderClick(int ci) {
            try {
                bool shift = false;
                try { shift = Event.current != null && Event.current.shift; } catch { }
                if (shift) {
                    if (ci == QualityCol) {
                        bool v = _showQualityCoeff != null && !_showQualityCoeff.Value;
                        if (_showQualityCoeff != null) {
                            _showQualityCoeff.Value = v;
                            SaveEntry(_showQualityCoeff);
                        }
                        Notes(v ? "游戏质量：显示技能匹配系数" : "游戏质量：仅显示 ✓",
                              v ? "Quality: showing the skill-match coefficient" : "Quality: marks only");
                        return;
                    }
                    if (ci == SkillCol) {
                        string cur = SkillModeCfg();
                        string next = cur == "max" ? "avg" : cur == "avg" ? "min" : "max";   //最大 -> 平均 -> 最小
                        if (_skillMode != null) {
                            _skillMode.Value = next;
                            SaveEntry(_skillMode);
                        }
                        // Notes(zh, en) 两个实参都会求值：先取一次名字，避免重复计算。
                        string skillName = SkillModeName(next);
                        Notes(SRApi.T("技能系数：", "Skill coefficient: ") + skillName, "Skill coefficient: " + skillName);
                        return;
                    }
                }
                CycleSort(ci);
            } catch (Exception __ex) { SRApi.Guard.Log("表头点击", __ex); }
        }

        // 列排序三态循环：升序 -> 降序 -> 默认（回到游戏自己的顺序）。
        private static void CycleSort(int ci) {
            try {
                if (ci < 0 || ci >= ColCount || ci == JoinCol) return;   // 操作列无排序语义
                if (_sortCol != ci) { _sortCol = ci; _sortDesc = false; }
                else if (!_sortDesc) { _sortDesc = true; }
                else { _sortCol = -1; _sortDesc = false; }
                if (_sortColEntry != null) { _sortColEntry.Value = _sortCol; SaveEntry(_sortColEntry); }
                if (_sortDescEntry != null) { _sortDescEntry.Value = _sortDesc; SaveEntry(_sortDescEntry); }
                SortLobbies();
                // Notes(zh, en) 两个实参都会求值：先拼一次再传。
                string nm = SRApi.T(Titles[ci], TitlesEn[ci]);
                string state = _sortCol < 0 ? SRApi.T("默认排序（可加入优先）", "default order (joinable first)")
                             : (_sortDesc ? SRApi.T("降序", "descending") : SRApi.T("升序", "ascending"));
                string msg = nm + " · " + state;
                Notes(msg, msg);
            } catch (Exception __ex) { SRApi.Guard.Log("列排序切换", __ex); }
        }

        private static string SkillModeCfg() {
            string v = _skillMode != null ? (_skillMode.Value ?? "avg") : "avg";
            return v == "max" || v == "min" ? v : "avg";
        }

        private static string SkillModeName(string mode) {
            if (mode == "max") return SRApi.T("最大技能", "max");
            if (mode == "min") return SRApi.T("最小技能", "min");
            return SRApi.T("平均技能", "average");
        }
        private static readonly List<Matchmaker.LobbyListInfo> _shownLobbies = new List<Matchmaker.LobbyListInfo>();
        private static readonly List<Rect> _headerCellsPage = new List<Rect>();   //可滚动列的表头单元格（页面坐标）
        private static readonly List<int> _headerCols = new List<int>();          //上表每个单元格 -> 列索引
        private static readonly List<Rect> _rowRectsPage = new List<Rect>();      //房间行（页面坐标，已按可视区裁剪）
        private static readonly List<int> _rowRows = new List<int>();             //房间行 -> _shownLobbies 下标
        private static readonly List<Rect> _joinRectsPage = new List<Rect>();     //加入按钮（页面坐标，钉在右边）
        private static readonly List<int> _joinRows = new List<int>();            //加入按钮 -> _shownLobbies 下标
        private static bool _sbDragging;                //正在拖竖向滚动条
        private static float _sbGrabY;                  //按下时鼠标相对滑块顶部的偏移
        private static float _tableMaxX, _tableMaxY;
        private static float _joinW, _joinXPage;  // 钉在右边的操作列几何
        private static float _pageTopY;                 //页面内容在布局空间里的起点 y（见 RenderOnlinePage）
        private static int _joinDownRow = -1;           //加入按钮按下的行（仅用于高亮）
        private static string _joinDownId;              // 按下时该行的 sLobbyID（松开时据此定位，避免重排后点错房）
        private static ulong _pendingSteamId; // Steam 平台要加入的房间号（LobbyListInfo.UnityMatchID）
        private static float _dragWatchdog;             // 拖拽兜底：太久没有鼠标事件就复位（不用 Input.GetMouseButton）
        private static int _clickFrame = -1;            //表头单击一帧只处理一次
        private static readonly SRApi.TableArgs _tableArgs = new SRApi.TableArgs(); //统一入口参数（复用，避免每帧分配）

        // 当前会显示的房间（筛选 + 未隐藏），顺序已由 SortLobbies 排好。
        private static void CollectShown() {
            _shownLobbies.Clear();
            for (int i = 0; i < _lobbies.Count; i++) {
                Matchmaker.LobbyListInfo li = _lobbies[i];
                if (li == null || !PassFilter(li)) continue;
                if (_hidden.Contains(li.sLobbyID)) continue;
                _shownLobbies.Add(li);
            }
        }

        // 可视行的单元格文本数组：按房间跨帧复用同一份数组，仅在首次出现时分配。
        // 房间对象每次刷新都会重建，故设上限兜底：超过 CellStoreMax 时整体清空。
        private static readonly System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]> _cellStore =
            new System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]>();
        private const int CellStoreMax = 256;
        // 按帧缓存每个房间的单元格文本：IMGUI 一帧有 Layout+Repaint+多个输入事件，避免重复计算。
        private static readonly System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]> _cellCache =
            new System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]>();
        private static int _cellCacheFrame = -1;
        private static string[] CellTexts(Matchmaker.LobbyListInfo li) {
            if (_cellCacheFrame != Time.frameCount) { _cellCacheFrame = Time.frameCount; _cellCache.Clear(); }
            string[] hit;
            if (_cellCache.TryGetValue(li, out hit)) return hit;
            string[] buf;
            if (!_cellStore.TryGetValue(li, out buf) || buf == null || buf.Length != ColCount) {
                if (_cellStore.Count >= CellStoreMax) _cellStore.Clear();
                buf = new string[ColCount];
                _cellStore[li] = buf;
            }
            buf[0] = RegionShort(li);
            buf[1] = li.Players + "/4";
            buf[2] = QualityText(li);
            buf[3] = SkillText(li);
            buf[4] = ProgressText(li);
            buf[5] = ModeText(li);
            buf[6] = TagText(li);
            buf[7] = string.IsNullOrEmpty(li.LobbyOwner) ? "?" : li.LobbyOwner;
            buf[8] = LimitText(li);
            buf[9] = FlagsText(li);
            buf[CombinedCol] = li.CombinedHealthSkill.ToString(CultureInfo.InvariantCulture);
            buf[JoinCol] = "";
            _cellCache[li] = buf;
            return buf;
        }

        private static void TableDraw(Rect box, List<int> vis) {
            Event e = Event.current;
            Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
            CollectShown();
            float rowH = SRApi.Ctl.Sc(26), headerH = SRApi.Ctl.Sc(24);
            float rowsH = Mathf.Max(SRApi.Ctl.Sc(20), box.height - headerH);
            float contentH = _shownLobbies.Count * rowH;
            bool overflow = contentH > rowsH + 0.5f;                    //只有真的超出才画滚动条
            float sbW = overflow ? SRApi.Ctl.Sc(13) : 0f;
            float viewW = Mathf.Max(SRApi.Ctl.Sc(60), box.width - sbW);
            // 操作列钉在表格右边缘，不参与横向滚动，加入按钮始终可见可点。
            float joinW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] == JoinCol) joinW = SRApi.Ctl.Sc(_cw[JoinCol]);
            float stripW = Mathf.Max(SRApi.Ctl.Sc(40), viewW - joinW);     //可横向滚动的列区
            float totalW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] != JoinCol) totalW += SRApi.Ctl.Sc(_cw[vis[i]]);
            _joinW = joinW;
            _joinXPage = box.x + viewW - joinW;
            _rowsClipPage = new Rect(box.x, box.y + headerH, stripW, rowsH);
            _tableMaxX = Mathf.Max(0f, totalW - stripW);
            _tableMaxY = Mathf.Max(0f, contentH - rowsH);
            _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _tableMaxX);
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _tableMaxY);
            if (e == null) return;
            if (e.type == EventType.Repaint) GUI.Box(box, GUIContent.none, SRApi.Ctl.Footer);
                DrawTableRows(vis, box, headerH, rowH, stripW, rowsH, viewW, mp);
        }


        private static void DrawTableRows(List<int> vis, Rect box, float headerH, float rowH, float stripW, float rowsH, float viewW, Vector2 mp) {
            // _rowRows 必须与 _rowRectsPage 一起清空：两者是"行矩形 -> _shownLobbies 下标"的一一对应。
            // 表头 / 行命中 / 行绘制统一交给核心入口。
            _tableArgs.box = box; _tableArgs.rowsClip = _rowsClipPage;
            _tableArgs.headerH = headerH; _tableArgs.rowH = rowH; _tableArgs.rowsH = rowsH; _tableArgs.stripW = stripW;
            _tableArgs.colW = _cw; _tableArgs.vis = vis; _tableArgs.rowCount = _shownLobbies.Count;
            _tableArgs.pinCol = JoinCol; _tableArgs.alignLeftCol = HostCol;
            _tableArgs.scrollX = _scrollCols.x; _tableArgs.scrollY = _scrollRows.y; _tableArgs.mp = mp;
            _tableArgs.pinW = _joinW; _tableArgs.pinX = _joinXPage; _tableArgs.drawHeader = true;   // 表头交给核心绘制
            // 核心画表头需要列标题回调，缺失会导致表头空白。
            _tableArgs.title = delegate(int ci) { return SRApi.T(Titles[ci], TitlesEn[ci]); };
            _tableArgs.pinCol = JoinCol;
            _tableArgs.pinTitle = SRApi.T(Titles[JoinCol], TitlesEn[JoinCol]);
        // 隐藏列交由核心 TableDraw 处理（右键隐藏 / Shift+右键还原全部）。
        _tableArgs.hidden = _hiddenCols; _tableArgs.hideable = true;
        // 列宽拖动 / 列头换位全部交给核心，本页只提供列定义与回调；拖拽状态由核心持有，本页不镜像。
        _tableArgs.resizable = true;
        _tableArgs.reorderable = true;
        // 把本页的列序源与换位回调注入核心。
        _tableArgs.order = _order;
        _tableArgs.applyOrder = delegate(int[] o) { _order = o; _visCache = null; SaveCols(); };
            // Shift+右键 = 还原全部：接到本表自己的还原例程 ResetTable。
            _tableArgs.resetAll = delegate { ResetTable(); };
        _tableArgs.click = HeaderClickOnce;
            _tableArgs.rowCells = delegate(int r) { return CellTexts(_shownLobbies[r]); };
            _tableArgs.rowAlpha = delegate(int r) { return IsJoinable(_shownLobbies[r]) ? 1f : 0.55f; };
            _tableArgs.headerCells = _headerCellsPage; _tableArgs.headerCols = _headerCols;
            _tableArgs.rowRects = _rowRectsPage; _tableArgs.rowRows = _rowRows;
            _tableArgs.pinRects = _joinRectsPage; _tableArgs.pinRows = _joinRows;
            _tableArgs.emptyHint = delegate { return SRApi.T("（没有符合筛选的房间）", "(no lobbies match the filters)"); };
            SRApi.TableDraw(_tableArgs);
            // 收回核心算出的钉住列表头矩形，供下方命中判定显示"操作"列 tooltip。
            _joinHeaderPage = _tableArgs.pinHeader;
            // 空提示由核心的 emptyHint 在行区裁剪内绘制。
            // 钉住的操作列：加入按钮始终在表格右边缘且可点。
            if (_joinW > 0f) {
                int vk = 0;
                GUI.BeginClip(new Rect(_joinXPage, box.y + headerH, _joinW, rowsH));
                try {
                for (int i = 0; i < _shownLobbies.Count; i++) {
                    float y = i * rowH - _scrollRows.y;
                    if (y > rowsH || y + rowH < 0f) continue;
                    Matchmaker.LobbyListInfo li = _shownLobbies[i];
                    bool can = IsJoinable(li);
                    Rect rp = vk < _joinRectsPage.Count ? _joinRectsPage[vk] : new Rect(0f, 0f, 0f, 0f);
                    vk++;
                    bool hov = can && rp.Contains(mp);
                    Color pc = GUI.color;
                    if (!can) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    // 窄格子按钮：用内边距小的样式，避免文字被裁掉。
                    SRApi.Ctl.BtnClip.Draw(new Rect(SRApi.Ctl.Sc(2), y + SRApi.Ctl.Sc(1), _joinW - SRApi.Ctl.Sc(4), rowH - SRApi.Ctl.Sc(2)),
                        new GUIContent(JoinText(li)), hov, hov && _joinDownRow == i, false, false);
                    GUI.color = pc;
                }
                } finally { GUI.EndClip(); }
                SRApi.DrawLine(new Rect(_joinXPage - 1f, box.y + headerH, 1f, rowsH), SRApi.TableLineStrong);   // 钉住列分隔线
            }
            // 竖向滚动条：几何字段必须在这里赋值，否则核心 TableScrollbarInput 的 track.width > 0 判据不成立。
            // 颜色对齐 ImGui 的 ScrollbarBg / Grab / GrabHovered / GrabActive。
            if (_shownLobbies.Count * rowH > rowsH + 0.5f) {
                Rect __track = new Rect(box.x + viewW + SRApi.Ctl.Sc(1), box.y + headerH, SRApi.Ctl.Sc(13) - SRApi.Ctl.Sc(2), rowsH);
                float __contentH = _shownLobbies.Count * rowH;
                float __thumbH = Mathf.Clamp(rowsH * rowsH / Mathf.Max(1f, __contentH), SRApi.Ctl.Sc(18), rowsH);
                float __ty = __track.y + (_tableMaxY > 0f ? Mathf.Clamp01(_scrollRows.y / _tableMaxY) : 0f) * (rowsH - __thumbH);
                Rect __thumb = new Rect(__track.x, __ty, __track.width, __thumbH);
                _sbTrackPage = __track; _sbThumbPage = __thumb;
                SRApi.DrawFill(__track, new Color(0.06f, 0.06f, 0.07f, 0.94f));                    //ImGui ScrollbarBg
                SRApi.DrawFill(__thumb, _sbDragging ? new Color(0.51f, 0.51f, 0.53f, 1f)           //ImGui GrabActive
                                    : __thumb.Contains(mp) ? new Color(0.41f, 0.41f, 0.44f, 1f) //ImGui GrabHovered
                                    : new Color(0.31f, 0.31f, 0.34f, 1f));                      //ImGui Grab
            } else {
                _sbTrackPage = new Rect(0f, 0f, 0f, 0f);
                _sbThumbPage = new Rect(0f, 0f, 0f, 0f);
            }
        }
        private static void TableInput(Rect box, List<int> vis, Event e) {
            if (e == null) return;
            Vector2 mp = e.mousePosition;
            // 拖拽兜底复位：不能用 Input.GetMouseButton(0) 判断（与 IMGUI 事件流不同步），改为超时无鼠标事件才复位。
            try {
                SRApi.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _tableArgs.moveCol, ref _tableArgs.moveDx, ref _tableArgs.dragCol, ref _dragH,
                    SaveWidths, SaveTableH, delegate { _joinDownRow = -1; });
            } catch (Exception __ex) { SRApi.Guard.Log("联机表格输入处理", __ex); } // 兜底：表头拖拽 / 列宽 / 加入点击都在这里
            //竖向滚动条拖拽 / 点轨道 -> 核心实现
            if (SRApi.TableScrollbarInput(e, mp, _sbTrackPage, _sbThumbPage, _tableMaxY, ref _sbDragging, ref _sbGrabY, ref _scrollRows.y)) return;
            //滚轮（纵向 / Ctrl 横向 / 无纵向余量转横向）-> 核心实现
            SRApi.TableWheelInput(e, box, mp, _tableMaxX, _tableMaxY, ref _scrollCols, ref _scrollRows);
            // 表头：分隔线调宽（<->）/ 表头换位（+）/ 单击切换 / 右键隐藏 / Shift+右键还原
            bool hoverSep = false, hoverCell = false;
            bool inBox = box.Contains(mp);   // 横向滚出的列会落到框外，故把判定收回框内
            for (int k = 0; k < _headerCellsPage.Count; k++) {
                int ci = _headerCols[k];
                Rect cell = _headerCellsPage[k];
                Rect sep = new Rect(cell.xMax - SRApi.Ctl.Sc(3), cell.y, SRApi.Ctl.Sc(7), cell.height);
                bool onSep = inBox && sep.Contains(mp);
                if (onSep) hoverSep = true;
                else if (inBox && cell.Contains(mp)) hoverCell = true;
            }
            SetMouseCursor(hoverSep || _tableArgs.dragCol >= 0, hoverCell || _tableArgs.moveCol >= 0);
            // 悬停在表头：显示该列说明（GUI.tooltip 在 OnGUI 末尾统一画）
            if (inBox && !_sbDragging && !hoverSep) {
                int hitCol = -1;
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (!_headerCellsPage[k].Contains(mp)) continue;
                    hitCol = _headerCols[k];
                    break;
                }
                if (hitCol < 0 && _joinHeaderPage.width > 0f && _joinHeaderPage.Contains(mp)) hitCol = JoinCol;
                if (hitCol >= 0) {
                    string tip = SRApi.T(TitleTips[hitCol], TitleTipsEn[hitCol]);
                    if (hitCol == SkillCol) {
                        // 悬浮提示里带上自己的技能系数（存档中的 SkillMean）。
                        string mine = MySkillText();
                        tip += "\n" + SRApi.T("你自己的技能系数：", "your own skill: ") + (mine != null ? mine : "?");
                        tip += "\n" + SRApi.T("当前显示：", "showing: ") + SkillModeName(SkillModeCfg());
                    } else if (hitCol == QualityCol) {
                        bool on = _showQualityCoeff != null && _showQualityCoeff.Value;
                        tip += "\n" + SRApi.T(on ? "当前：显示匹配质量系数" : "当前：不显示匹配质量系数（点一下打开）",
                                             on ? "now: coefficient shown" : "now: coefficient hidden (click to show)");
                    }
                    GUI.tooltip = tip;
                }
            }
            // 右键：表头 = 隐藏该列 / Shift = 还原；房间行 = 隐藏该房间
            if (e.type == EventType.MouseDown && e.button == 1 && box.Contains(mp)) {
                if (e.shift) {
                    ResetTable();
                    e.Use();
                } else {
                    bool handled = false;
                    for (int k = 0; k < _headerCellsPage.Count; k++) {
                        if (!_headerCellsPage[k].Contains(mp)) continue;
                        int ci = _headerCols[k];
                        if (_headerCols.Count > 2) {      //至少留 2 列（操作列是钉住的，不参与）
                            _hiddenCols.Add(ci);
                            _visCache = null;
                            SaveCols();
                            string nm = SRApi.T(Titles[ci], TitlesEn[ci]);
                            // SRApi.T(zh, en) 两个实参都会求值：两参相同时先拼一次再传。
                            string hiddenColMsg = SRApi.T("已隐藏列：", "Hidden column: ") + nm;
                            Notes(hiddenColMsg, hiddenColMsg);
                        }
                        handled = true;
                        break;
                    }
                    if (!handled) {
                        for (int i = 0; i < _rowRectsPage.Count; i++) {
                            if (!_rowRectsPage[i].Contains(mp)) continue;
                            int si = _rowRows[i];
                            Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                            if (li != null) {
                                _hidden.Add(li.sLobbyID);
                                _lobbiesDirty = true;   // 可见集合变了，需重算"筛选后 N 个"
                                Notes("已隐藏房间：" + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner),
                                      "Hidden: " + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner));
                            }
                            break;
                        }
                    }
                    e.Use();
                }
            }
            // 加入按钮：手动点击
            for (int i = 0; i < _joinRectsPage.Count; i++) {
                if (!_joinRectsPage[i].Contains(mp)) continue;
                int si = _joinRows[i];
                Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                if (li == null || !IsJoinable(li)) break;
                // 只在按下到松开之间阻止窗口拖动：无条件设 true 会让鼠标悬停即锁住窗口拖动。
                if (e.type == EventType.MouseDown && e.button == 0) { _joinDownRow = si; _joinDownId = li.sLobbyID; SRApi.BlockWindowDrag = true; e.Use(); }
                else if (e.type == EventType.MouseUp && e.button == 0 && !string.IsNullOrEmpty(_joinDownId) && _joinDownId == li.sLobbyID) {
                    try { SRApi.LogInfo("[联机] 列表项 sLobbyID=" + li.sLobbyID + " UnityMatchID=" + li.UnityMatchID + " (UnityMatchID=0 表示该列表不是 Steam 大厅数据)"); } catch { }
                    _joinDownRow = -1; _joinDownId = null; SRApi.BlockWindowDrag = false; JoinLobbySteam(li.UnityMatchID, li.sLobbyID); e.Use();
                }
                break;
            }
            if (e.type == EventType.MouseUp) { _joinDownRow = -1; _joinDownId = null; }
        }

        // Shift+右键还原列宽 / 表格高度 / 列顺序，并取消所有隐藏（房间与列）。
        private static void ResetTable() {
            try {
                _cw = (float[])DefWidths.Clone();
                // 还原后列宽已就位，同步置上初始化标记。
                _widthsLoaded = true;
                _order = new int[ColCount];
                for (int i = 0; i < ColCount; i++) _order[i] = i;
                _hiddenCols.Clear();
                _visCache = null;
                if (_tableH != null) _tableH.Value = 0;
                _hidden.Clear();
                _lobbiesDirty = true;
                SaveCols();
                Notes("已还原列宽、顺序与高度，并显示全部房间与列", "Widths, order and height reset; all sessions and columns restored");
            } catch (Exception __ex) { SRApi.Guard.Log("还原联机表格列设置", __ex); }
        }

        // 模式选项顺序：全部(空) -> 派对 -> 创意 -> 挑战 -> 自由 -> 其它（"全部"由下拉框首项提供）。
        private static readonly string[] ModeVals = { "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY", "OTHER" };
        private static readonly string[] TagVals = { "Fun", "Competitive", "Beginner", "CustomLevels" };

        // 下拉显示名（内部值 -> 界面名）：选项列表与当前选中项共用，避免两处不一致。
    }
}
