// Online 的 partial 分片（表格绘制 / 命中 / 列交互）。
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
        // 落盘统一交给 SRApi.Ctl.MarkConfigDirty；拖动期间高度只写 _dragHTmp，到这里才同步进配置。
        private static void SaveTableH() {
            if (_tableH != null) SRApi.Ctl.MarkConfigDirty(_tableH.ConfigFile);
        }

        private static void SaveEntry(ConfigEntryBase e) { if (e != null) SRApi.Ctl.MarkConfigDirty(e.ConfigFile); }

        // 表格：固定表头 + 房间行 + 自绘竖向滚动条，几何全部用绝对矩形手动偏移。
        private const int HostCol = 6;                 // 房主列靠左显示，其余列居中
        private const int QualityCol = 2;              // 游戏质量列：Shift+单击表头切换技能匹配质量系数
        private const float CodeLabelW = 58f;          // 房间码标签宽度

        // 单击表头：Shift+左键触发列专属切换；质量列实际由页面 MouseDown 入口切换，这里只是备用路径。
        // 核心在 MouseUp 派发且那时可能已丢 Shift，故用 ShiftHeldNow() 同时看事件与 Input。
        private static void HeaderClick(int ci) {
            try {
                if (!ShiftHeldNow()) {
                    return;
                }
                if (ci == QualityCol) ToggleQualityCoeff("core click 回调");
            } catch (Exception __ex) { SRApi.Guard.Log("表头点击", __ex); }
        }

        private static bool ShiftHeldNow() {
            try { if (Event.current != null && Event.current.shift) return true; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return false;
        }

        // 切换「游戏质量」列的质量系数显示；两个入口共用。
        private static void ToggleQualityCoeff(string from) {
            if (_showQualityCoeff == null) return;
            bool v = !_showQualityCoeff.Value;
            _showQualityCoeff.Value = v;
            SaveEntry(_showQualityCoeff);
            try {
                SRApi.LogInfo("[联机] 游戏质量系数 -> " + (v ? "开（显示 0~1）" : "关（仅 ✓）") + "；入口=" + from);
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            Notes(v ? "游戏质量：显示技能匹配系数（0~1）" : "游戏质量：仅显示 ✓",
                  v ? "Quality: showing the skill-match coefficient (0-1)" : "Quality: marks only");
        }

        // 页面自己的 Shift+左键质量列表头入口：本帧表头几何刚由 TableDraw 算好，直接复算命中。
        // 返回命中的数据列索引，未命中 -1；列序 / 隐藏 / 横向滚动与 DeriveVisibleCols 同规则，钉住列不参与。
        private static List<Matchmaker.LobbyListInfo> _shownLobbies = new List<Matchmaker.LobbyListInfo>();
        private static readonly List<Rect> _headerCellsPage = new List<Rect>();   //可滚动列的表头单元格（页面坐标）
        private static readonly List<int> _headerCols = new List<int>();          //上表每个单元格 -> 列索引
        private static readonly List<Rect> _rowRectsPage = new List<Rect>();      //房间行（页面坐标，已按可视区裁剪）
        private static readonly List<int> _rowRows = new List<int>();             //房间行 -> _shownLobbies 下标
        private static readonly List<Rect> _joinRectsPage = new List<Rect>();     //加入按钮（页面坐标，钉在右边）
        private static readonly List<int> _joinRows = new List<int>();            //加入按钮 -> _shownLobbies 下标
        private static bool _dragH;   // 核心拖拽看门狗需要一个 ref bool（高度拖拽由核心处理）
        private static bool _sbDragging;
        private static bool _sbHandled;   // 本帧核心滚动条是否已处理鼠标事件                //正在拖竖向滚动条
        private static float _sbGrabY;                  //按下时鼠标相对滑块顶部的偏移
        private static float _tableMaxX, _tableMaxY;
        private static Rect _tableScreenBox;   // 表格矩形（屏幕坐标）：滚轮只在这个范围内滚列表
    private static float _rowsHCache;   // 行区高度缓存（滚轮钩子首帧兜底）
        private static float _joinW, _joinXPage;  // 钉在右边的操作列几何
        private static int _joinDownRow = -1;           //加入按钮按下的行（仅用于高亮）
        private static string _joinDownId;              // 按下时该行的 sLobbyID（松开时据此定位，避免重排后点错房）
        private static ulong _pendingSteamId; // Steam 平台要加入的房间号（LobbyListInfo.UnityMatchID）
        private static float _dragWatchdog;             // 拖拽兜底：太久没有鼠标事件就复位（不用 Input.GetMouseButton）
        private static int _clickFrame = -1;            //表头单击一帧只处理一次
        private static readonly SRApi.TableArgs _tableArgs = new SRApi.TableArgs();
        private static readonly System.Action<int, Rect, int> _pinDrawCached = delegate(int row, Rect r, int visible) {
            DrawJoinButton(row, r, _tableArgs != null ? _tableArgs.mp : Vector2.zero);
        }; //统一入口参数（复用，避免每帧分配）

        private static readonly SRApi.TableSource<Matchmaker.LobbyListInfo> _lobbySource =
            new SRApi.TableSource<Matchmaker.LobbyListInfo>();

        // 当前会显示的房间（筛选 + 未隐藏 + 排序）：走核心数据管线，最多每 0.5 秒或脏标记时重算。
        private static void CollectShown() {
            try {
                if (_lobbiesDirty) { _lobbySource.Invalidate(); }
                _lobbySource.Filter = PassLobbyFilter;
                _lobbySource.Sort = CompareLobbies;
                long stamp = (long)(Time.unscaledTime * 2f);
                _shownLobbies = _lobbySource.Refresh(_lobbies, stamp);
                _lobbiesDirty = false;   // 本次已按脏标记重算，清掉避免每帧重复排序
            } catch (Exception __ex) { SRApi.Guard.Log("联机/筛选排序", __ex); }
        }

        // 可视行单元格文本：按房间跨帧复用数组，超过 CellStoreMax 整体清空。
        // 键用 sLobbyID 而非对象引用：房间对象每次刷新都会重建，引用作键会让命中率掉到 0。
        private static readonly System.Collections.Generic.Dictionary<string, string[]> _cellStore =
            new System.Collections.Generic.Dictionary<string, string[]>();
        private const int CellStoreMax = 256;
        private static readonly System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]> _cellRowsCache =
            new System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]>();
        private static int _cellRowsFrame = -1;
        // 房间稳定键：优先平台房间 id，其次 UnityMatchID，最后房主名。
        private static string CellKey(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null) return null;
                if (!string.IsNullOrEmpty(li.sLobbyID)) return li.sLobbyID;
                if (li.UnityMatchID != 0UL) return li.UnityMatchID.ToString(CultureInfo.InvariantCulture);
                return li.LobbyOwner ?? "";
            } catch { return null; }
        }

        private static string[] CellTexts(Matchmaker.LobbyListInfo li) {
            if (_cellRowsFrame != Time.frameCount) { _cellRowsFrame = Time.frameCount; _cellRowsCache.Clear(); }
            string[] hit;
            if (_cellRowsCache.TryGetValue(li, out hit)) return hit;
            string key = CellKey(li);
            string[] buf;
            if (string.IsNullOrEmpty(key) || !_cellStore.TryGetValue(key, out buf) || buf == null || buf.Length != ColCount) {
                if (_cellStore.Count >= CellStoreMax) _cellStore.Clear();
                buf = new string[ColCount];
                if (!string.IsNullOrEmpty(key)) _cellStore[key] = buf;
            }
            buf[0] = RegionShort(li);
            buf[1] = li.Players + "/4";
            buf[2] = QualityText(li);
            buf[3] = ProgressText(li);
            buf[4] = ModeText(li);
            buf[5] = TagText(li);
            buf[6] = string.IsNullOrEmpty(li.LobbyOwner) ? "?" : li.LobbyOwner;
            buf[7] = LimitText(li);
            buf[8] = FlagsText(li);
            buf[CombinedCol] = li.CombinedHealthSkill.ToString(CultureInfo.InvariantCulture);
            buf[JoinCol] = "";
            _cellRowsCache[li] = buf;
            return buf;
        }

        private static readonly List<int> _visScratch = new List<int>(ColCount);   // 交给核心按 order+hidden 填充
        private static int _hiddenColsBeforeTable;                                // 本帧核心处理右键隐藏前的列数

        // 页面级滚轮：页面关掉滚动后表格自己吃滚轮，只有这个钩子能在滚动区建立前拿到事件。
    internal static bool OnLobbyWheel(Vector2 mouse, float wheel) {
        try {
            Rect __box = _tableScreenBox;
            if (__box.width <= 2f || __box.height <= 2f) return false;
            if (!__box.Contains(mouse)) return false;
            return ScrollTableByWheel(wheel);
        } catch (Exception __ex) { SRApi.Guard.Log("联机列表/页面滚轮", __ex); return false; }
    }

    // 表格滚轮：Shift 横向、否则纵向；到头返回 false 让给下一层。
    internal static bool ScrollTableByWheelPublic(float wheel) { return ScrollTableByWheel(wheel); }

    private static bool ScrollTableByWheel(float wheel) {
        try {
            bool __shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (__shift && _tableMaxX > 0f) {
                float beforeX = _scrollCols.x;
                _scrollCols.x = Mathf.Clamp(_scrollCols.x - wheel * SRApi.Ctl.Sc(60), 0f, _tableMaxX);
                return Mathf.Abs(_scrollCols.x - beforeX) > 0.01f;
            }
            float __maxY = _tableMaxY;
            if (__maxY <= 0f && _rowsHCache > 0f)
                __maxY = _shownLobbies != null ? SRApi.Ctl.TableMaxScrollY(_shownLobbies.Count, SRApi.Ctl.TableRowH(), _rowsHCache) : 0f;
            if (__maxY <= 0f) return false;
            float before = _scrollRows.y;
            _scrollRows.y = Mathf.Clamp(_scrollRows.y - wheel * SRApi.Ctl.Sc(56), 0f, __maxY);
            _tableMaxY = __maxY;
            return Mathf.Abs(_scrollRows.y - before) > 0.01f;
        } catch (Exception __ex) { SRApi.Guard.Log("联机列表/表格滚动", __ex); return false; }
    }

    private static void TableDraw(Rect box) {
            // _cw 未就绪时先重试初始化，避免 NullReference。
            if (_cw == null || _cw.Length <= JoinCol) { try { EnsureWidths(); } catch (Exception __ew) { SRApi.Guard.Log("联机表格/列宽初始化", __ew); } }
            if (_cw == null || _cw.Length <= JoinCol) return;
            Event e = Event.current;
            Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
            CollectShown();
            float rowH = SRApi.Ctl.TableRowH(), headerH = SRApi.Ctl.TableHeaderH();
            try {
                float __offX = SRApi.ContentArea.x - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(2f);
                // 页面可能已滚动：命中框要同步减掉 _scroll.y。
                float __offY = SRApi.ContentArea.y - SRApi.PageScroll.y;
                if (box.width > 2f && box.height > 2f) {
                    _tableScreenBox = new Rect(box.x + __offX, box.y + __offY, box.width, box.height);
                    // 表格区自注册：区域优先于页面钩子。
                {
                    float __w = SRApi.Ctl.WheelPoll("online.table", box);
                    if (Mathf.Abs(__w) > 0.01f) ScrollTableByWheel(__w);
                }
                }
            } catch (Exception __tbox) { SRApi.Guard.Log("联机表格/命中框", __tbox); }
            // 行区 = 屏幕真实可见高度（box 可能被布局算到可见区外）。
            float rowsH = SRApi.Ctl.ScrollRowsH(box, headerH, SRApi.PageContentBottom, rowH);
            float contentH = _shownLobbies.Count * rowH;
            float sbW = 0f;
            float viewW = Mathf.Max(SRApi.Ctl.Sc(60), box.width - sbW);
            // 操作列钉在表格右边缘，不参与横向滚动。
            float joinW = SRApi.Ctl.Sc(_cw[JoinCol]);
            float stripW = Mathf.Max(SRApi.Ctl.Sc(40), viewW - joinW);     //可横向滚动的列区

            // 可见列由核心推导，本页只提前算横向滚动上限。
            float totalW = 0f;
            for (int i = 0; i < ColCount; i++) {
                int ci = _order != null && i < _order.Length ? _order[i] : i;
                if (ci == JoinCol || _hiddenCols.Contains(ci)) continue;
                totalW += SRApi.Ctl.Sc(_cw[ci]);
            }
            _joinW = joinW;
            _joinXPage = box.x + viewW - joinW;
            _rowsClipPage = new Rect(box.x, box.y + headerH, stripW, rowsH);
            _tableMaxX = Mathf.Max(0f, totalW - stripW);
            // maxY = 内容高 − 行区（不能 +rowH，多给一行会滚过表尾）。
            _tableMaxY = SRApi.Ctl.TableMaxScrollY(_shownLobbies.Count, rowH, rowsH);
            _rowsHCache = rowsH;
            _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _tableMaxX);
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _tableMaxY);
            if (e == null) return;
            if (e.type == EventType.Repaint) GUI.Box(box, GUIContent.none, SRApi.Ctl.Footer);
            DrawTableRows(box, headerH, rowH, stripW, rowsH, viewW, mp);
        }


        // 回调委托只创建一次（每帧都会跑，内联 delegate 会逐帧产生垃圾）。
        private static readonly Func<int, string> _titleFn = delegate(int ci) { return SRApi.T(Titles[ci], TitlesEn[ci]); };
        private static readonly Action<int[]> _applyOrderFn = delegate(int[] o) { _order = o; SaveCols(); };
        private static readonly Action _resetAllFn = delegate { ResetTable(); };
        private static readonly Action<int> _headerShiftFn = delegate(int ci) { if (ci == QualityCol) ToggleQualityCoeff("core header"); };
        private static readonly Func<int, string[]> _rowCellsFn = delegate(int r) { return CellTexts(_shownLobbies[r]); };

        private static readonly Func<int, float> _rowAlphaFn = delegate(int r) { return IsJoinable(_shownLobbies[r]) ? 1f : 0.55f; };
        private static readonly Func<string> _emptyHintFn = delegate { return SRApi.T("（没有符合筛选的房间）", "(no lobbies match the filters)"); };
        private static readonly Action _joinDownReset = delegate { _joinDownRow = -1; };
        private static readonly Func<int, string> _tipFn = delegate (int ci) { return TipText(ci); };
        private static readonly Action<int> _clickFn = delegate (int ci) { HeaderClickOnce(ci); };

        private static void DrawTableRows(Rect box, float headerH, float rowH, float stripW, float rowsH, float viewW, Vector2 mp) {
            // _rowRows 必须与 _rowRectsPage 一起清空：两者是行矩形与 _shownLobbies 下标的一一对应。
            _tableArgs.box = box; _tableArgs.rowsClip = _rowsClipPage;
            _tableArgs.headerH = headerH; _tableArgs.rowH = rowH; _tableArgs.rowsH = rowsH; _tableArgs.stripW = stripW;
            _tableArgs.colW = _cw; _tableArgs.vis = _visScratch; _tableArgs.rowCount = _shownLobbies.Count;
            _tableArgs.pinCol = JoinCol; _tableArgs.alignLeftCol = HostCol;
            _tableArgs.scrollX = _scrollCols.x; _tableArgs.scrollY = _scrollRows.y; _tableArgs.mp = mp;
            _tableArgs.pinW = _joinW; _tableArgs.pinX = _joinXPage; _tableArgs.drawHeader = true;   // 表头交给核心绘制
            // 缺标题回调会导致表头空白。
            _tableArgs.title = _titleFn;
            _tableArgs.tip = _tipFn;
            _tableArgs.pinTitle = SRApi.T(Titles[JoinCol], TitlesEn[JoinCol]);
        _tableArgs.hidden = _hiddenCols; _tableArgs.hideable = true;
        _tableArgs.resizable = true;
        _tableArgs.reorderable = true;
        _tableArgs.order = _order;
        _tableArgs.applyOrder = _applyOrderFn;
            // Shift+右键 = 还原全部：接到本表的 ResetTable。
            _tableArgs.resetAll = _resetAllFn;
        _tableArgs.click = _clickFn;
            _tableArgs.headerShiftClick = _headerShiftFn;
            // 行右键（钉住操作列不算行命中）：隐藏该房间。
            _tableArgs.rowCells = _rowCellsFn;
            _tableArgs.rowAlpha = _rowAlphaFn;
            _tableArgs.headerCells = _headerCellsPage; _tableArgs.headerCols = _headerCols;
            _tableArgs.rowRects = _rowRectsPage; _tableArgs.rowRows = _rowRows;
            _tableArgs.pinRects = _joinRectsPage; _tableArgs.pinRows = _joinRows;
            // 钉住列按钮：位置 / 裁剪由核心算好，本页只画内容（复用静态委托避免闭包）。
            _tableArgs.pinDraw = _pinDrawCached;
            _tableArgs.emptyHint = _emptyHintFn;
            _hiddenColsBeforeTable = _hiddenCols.Count;
            SRApi.TableDraw(_tableArgs);
            _joinHeaderPage = _tableArgs.pinHeader;
            if (_joinW > 0f) SRApi.DrawLine(new Rect(_joinXPage - 1f, box.y + headerH, 1f, rowsH), SRApi.TableLineStrong);   // 钉住列分隔线
            // 竖向滚动条：几何/绘制/输入全部由核心按 TableArgs 计算（数据区右侧、钉住列之前）。
            _sbHandled = SRApi.Ctl.TableScrollbarFor(_tableArgs, Event.current, ref _sbDragging, ref _sbGrabY, ref _scrollRows.y);
        }

        // 钉住列的加入按钮：核心已给出裁剪后的矩形，这里只画按钮与悬停 / 按下态。
        private static void DrawJoinButton(int row, Rect r, Vector2 mp) {
            try {
                if (row < 0 || row >= _shownLobbies.Count) return;
                Matchmaker.LobbyListInfo li = _shownLobbies[row];
                bool can = IsJoinable(li);
                bool hov = can && r.Contains(mp);
                float y0 = r.y + SRApi.Ctl.Sc(1);
                float y1 = Mathf.Min(r.y + r.height, _rowsClipPage.yMax - SRApi.Ctl.Sc(1));
                float bh = Mathf.Max(0f, y1 - y0);
                if (bh <= 0f) return;
                Color pc = GUI.color;
                if (!can) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                SRApi.Ctl.BtnClip.Draw(new Rect(r.x, y0, r.width, bh),
                    new GUIContent(JoinText(li)), hov, hov && _joinDownRow == row, false, false);
                GUI.color = pc;
            } catch (Exception __ex) { SRApi.Guard.Log("联机表格加入按钮", __ex); }
        }
        // 表头说明：核心在命中时回调；分隔线悬停与滚动条拖动时不显示。
        private static string TipText(int ci) {
            try {
                if (ci < 0 || ci >= TitleTips.Length) return null;
                Vector2 mp = _tableArgs != null ? _tableArgs.mp : Vector2.zero;
                if (_sbDragging || HoveringSeparatorNow(mp)) return null;
                string tip = SRApi.T(TitleTips[ci], TitleTipsEn[ci]);
                if (ci == QualityCol) {
                    bool on = _showQualityCoeff != null && _showQualityCoeff.Value;
                    tip += "\n" + SRApi.T(on ? "当前：显示匹配质量系数（✓ 后追加 0~1）" : "当前：不显示匹配质量系数（Shift+点一下打开）",
                                         on ? "now: coefficient shown (0-1 after the marks)" : "now: coefficient hidden (Shift+click to show)");
                }
                return tip;
            } catch { return null; }
        }

        private static bool HoveringSeparatorNow(Vector2 mp) {
            try {
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    Rect cell = _headerCellsPage[k];
                    Rect sep = new Rect(cell.xMax - SRApi.Ctl.Sc(3), cell.y, SRApi.Ctl.Sc(7), cell.height);
                    if (sep.Contains(mp)) return true;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return false;
        }

        private static void TableInput(Rect box, Event e) {
            if (e == null) return;
            Vector2 mp = e.mousePosition;
            // 拖拽兜底复位：不用 Input.GetMouseButton（与 IMGUI 事件流不同步），改为超时无事件才复位。
            try {
                SRApi.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _tableArgs.moveCol, ref _tableArgs.moveDx, ref _tableArgs.dragCol, ref _dragH,
                    SaveWidths, SaveTableH, delegate { _joinDownRow = -1; });
            } catch (Exception __ex) { SRApi.Guard.Log("联机表格输入处理", __ex); } // 兜底：表头拖拽 / 列宽 / 加入点击都在这里
            if (_sbHandled) return;   // 核心滚动条已处理本帧事件
            SRApi.TableWheelInput(e, box, mp, _tableMaxX, _tableMaxY, ref _scrollCols, ref _scrollRows);
            if (_tableArgs.dragCol >= 0) SRApi.CursorResize = true;
            bool inBox = box.Contains(mp);   // 横向滚出的列会落到框外，故把判定收回框内
            bool hoverCell = false;
            if (inBox) {
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (_headerCellsPage[k].Contains(mp)) { hoverCell = true; break; }
                }
            }
            SRApi.CursorMove = hoverCell || _tableArgs.moveCol >= 0;
            if (inBox && !_sbDragging && _joinHeaderPage.width > 0f && _joinHeaderPage.Contains(mp)) {
                GUI.tooltip = SRApi.T(TitleTips[JoinCol], TitleTipsEn[JoinCol]);
            }
            // Shift+右键 = 还原全部：核心通常已处理，这里再兜一层（ResetTable 幂等）。
            if (e.button == 1 && e.shift && inBox
                && (e.type == EventType.MouseDown || e.type == EventType.Used)) {
                ResetTable();
                e.Use();
                return;
            }
            // 右键：表头 = 隐藏该列（核心已处理，本页只落盘与提示）；房间行 = 隐藏该房间。
            if (e.type == EventType.MouseDown && e.button == 1 && inBox) {
                if (e.shift) return;   // 核心 TableDraw 已清空隐藏列并回调 resetAll
                int hitCol = -1;
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (!_headerCellsPage[k].Contains(mp)) continue;
                    hitCol = _headerCols[k];
                    break;
                }
                if (hitCol >= 0) {
                    // 确实新增了隐藏列才落盘与提示。
                    if (_hiddenCols.Contains(hitCol) && _hiddenCols.Count > _hiddenColsBeforeTable) {
                        SaveCols();
                        string nm = SRApi.T(Titles[hitCol], TitlesEn[hitCol]);
                        string hiddenColMsg = SRApi.T("已隐藏列：", "Hidden column: ") + nm;
                        Notes(hiddenColMsg, hiddenColMsg);
                    }
                    e.Use();   // 表头右键隐藏列由核心完成，这里只负责落盘/提示
                }
                }
            // 加入按钮：手动点击
            for (int i = 0; i < _joinRectsPage.Count; i++) {
                if (!_joinRectsPage[i].Contains(mp)) continue;
                int si = _joinRows[i];
                Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                if (li == null || !IsJoinable(li)) break;
                // 只在按下到松开之间阻止窗口拖动，否则悬停即锁住窗口拖动。
                if (e.type == EventType.MouseDown && e.button == 0) { _joinDownRow = si; _joinDownId = li.sLobbyID; SRApi.BlockWindowDrag = true; e.Use(); }
                else if (e.type == EventType.MouseUp && e.button == 0 && !string.IsNullOrEmpty(_joinDownId) && _joinDownId == li.sLobbyID) {
                    try { SRApi.LogInfo("[联机] 列表项 sLobbyID=" + li.sLobbyID + " UnityMatchID=" + li.UnityMatchID + " (UnityMatchID=0 表示该列表不是 Steam 大厅数据)"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    _joinDownRow = -1; _joinDownId = null; SRApi.BlockWindowDrag = false; JoinLobbySteam(li.UnityMatchID, li.sLobbyID); e.Use();
                }
                break;
            }
            if (e.type == EventType.MouseUp) { _joinDownRow = -1; _joinDownId = null; }
        }

        // Shift+右键还原列宽 / 高度 / 列序，并取消列隐藏。
        private static void ResetTable() {
            try {
                _cw = (float[])DefWidths.Clone();
                _widthsLoaded = true;
                _order = new int[ColCount];
                for (int i = 0; i < ColCount; i++) _order[i] = i;
                _hiddenCols.Clear();
                if (_tableH != null) _tableH.Value = 0;
                _lobbiesDirty = true;
                SaveCols();
                Notes("已还原列宽、顺序与高度，并显示全部房间与列", "Widths, order and height reset; all sessions and columns restored");
            } catch (Exception __ex) { SRApi.Guard.Log("还原联机表格列设置", __ex); }
        }

        private static readonly string[] ModeVals = { "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY", "OTHER" };
        private static readonly string[] TagVals = { "Fun", "Competitive", "Beginner", "CustomLevels" };

        // 下拉显示名（内部值 -> 界面名），选项列表与当前选中项共用。
    }
}
