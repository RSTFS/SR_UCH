// 布局预设 · 第二页（预设列表）：整页重写，结构对齐联机列表（Online.Table.cs）。
//
// 结构：固定表头（搜索/筛选/工具行） + 一张占满剩余高度的列表 + 只有列表自己滚动。
// 与旧实现的差别，也是"列表滚不动"的根因所在：
//   1) 表高只由「右栏纯尺寸 − 底栏 − 表格顶」算出，不读任何页面级共享状态（PageContentBottom 之类）；
//   2) 行区高 = 表格矩形自己的 height − 表头，滚动上限 = 条目总高 − 行区高，两者同源，不可能不一致；
//   3) 滚轮**不做命中测试**：这一页只有列表能滚，面板开着、本页可见就把滚轮全给列表；
//      窗口帧尾派发的那条 page hook 只作兜底（绘制期没取到才用）；
//   4) 先应用滚轮、再把 scrollY 交给绘制，同一帧就能看到位移。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
        private static int _listWheelFrame = -1;   // 本帧滚轮是否已经应用过
        private static float _listMaxY;            // 本帧列表滚动上限（page hook 兜底用）
        private static float _sbGrabY;            // 竖向滚动条拖动抓取偏移（核心 TableScrollbarFor 用）

        // 表格回调委托只建一次（每次绘制都 new 会逐帧产生垃圾）：全部只引用静态成员。
        private static readonly Func<int, string> _titleFn = delegate (int ci) { return ColumnTitle(ci); };
        private static readonly Func<int, string> _tipFn = delegate (int ci) { return ColumnTip(ci); };
        // 行文本按帧缓存：表格每帧多趟绘制，RowCells 会重复格式化关卡名/模式/时间。
        private static int _rowCellsFrame = -1;
        private static List<PresetInfo> _rowCellsSrc;
        private static readonly List<string[]> _rowCellsCache = new List<string[]>();
        private static string[] RowCellsCached(int r) {
            try {
                if (_rowCellsFrame != Time.frameCount || !ReferenceEquals(_rowCellsSrc, _visibleCache)) {
                    _rowCellsFrame = Time.frameCount;
                    _rowCellsSrc = _visibleCache;
                    _rowCellsCache.Clear();
                    int n = _visibleCache != null ? _visibleCache.Count : 0;
                    for (int i = 0; i < n; i++) _rowCellsCache.Add(RowCells(_visibleCache, i));
                }
                if (r >= 0 && r < _rowCellsCache.Count) return _rowCellsCache[r];
            } catch (Exception __rc) { SRApi.Guard.Log("预设列表/行文本缓存", __rc); }
            return _visibleCache != null && r >= 0 && r < _visibleCache.Count ? RowCells(_visibleCache, r) : null;
        }
        private static readonly Func<int, string[]> _rowCellsFn = delegate (int r) { return RowCellsCached(r); };
        private static readonly Action<int[]> _applyOrderFn = delegate (int[] o) { _order = o; SaveCols(); };
        private static readonly Action _saveOrderFn = delegate { SaveCols(); };
        private static readonly Action _resetAllFn = delegate { ResetTable(); };
        private static float[] _defColWCache;   // DefColW 是数组字段，直接引用即可（不必每帧新建委托）
        private static readonly Action<int> _clickFn = OnColumnClick;
        private static readonly Func<string> _emptyHintFn = EmptyHintText;
        private static readonly Action<int, Rect, int> _pinDrawFn = delegate (int row, Rect r, int vi) { DrawActionButtons(_visibleCache, row, r); };
        private static readonly Action<int> _rowRightClickFn = delegate (int row) { StartRenameVisibleRow(row); };
        private static readonly Action<int> _rowDoubleClickFn = delegate (int row) { DoubleClickVisibleRow(row); };

        // 双击行 = 加载该预设（与操作列「加载」同一动作）。
        private static void DoubleClickVisibleRow(int row) {
            try {
                List<PresetInfo> vis = _visibleCache;
                if (vis == null || row < 0 || row >= vis.Count) return;
                PresetInfo p = vis[row];
                if (p == null) return;
                if (IsPending("replace:" + p.Key)) ClearPending();
                LoadPreset(p);
            } catch (Exception __dc) { SRApi.Guard.Log("预设列表/双击加载", __dc); }
        }

        // 右键行（核心 hit-test 派发）→ 开始内联重命名；只允许自己的预设（IsLocal 是只读存档）。
        private static void StartRenameVisibleRow(int row) {
            try {
                List<PresetInfo> vis = _visibleCache;
                if (vis == null || row < 0 || row >= vis.Count) return;
                PresetInfo p = vis[row];
                if (p == null || p.IsLocal) return;
                ClearPending();
                _renameKey = p.Key; _renameText = p.Name; _renameFocusPending = true;
            } catch (Exception __rr) { SRApi.Guard.Log("预设列表/行右键", __rr); }
        }

        private static void OnColumnClick(int ci) {
            if (ci == ColAction) return;
            // 表头点击排序（照 ImGui Table SortSpecs 的交互）：点一次升序、再点降序；点其它列切换主排序。
            // Shift+点「关卡」仍保留原来的"名称/代码"切换。
            Event __ce = Event.current;
            if (ci == ColLevel && __ce != null && __ce.shift) { _levelShowCode = !_levelShowCode; return; }
            if (_sortColOverride == ci) _sortAsc = !_sortAsc;
            else { _sortColOverride = ci; _sortAsc = true; }
            try { _scrollRows.y = 0f; } catch (Exception __sc2) { SRApi.Guard.Log("预设列表/排序回顶", __sc2); }
        }

        private static string EmptyHintText() {
            return _presets.Count == 0
                ? SRApi.T("ℹ\n还没有预设\n到「预设保存」页保存当前布局，或从剪贴板 / 文件导入。",
                          "ℹ\nNo presets yet\nSave the current layout on the Save page, or import from clipboard / file.")
                : SRApi.T("ℹ\n没有匹配的预设\n清空搜索或切换筛选条件试试。",
                          "ℹ\nNo matching presets\nClear the search or change the filters.");
        }

        // ================= 页面二：预设列表 =================
        private static void RenderListPage() {
            SRApi.NoPageScrollBars = true;   // 填充模式：页头固定，只有列表自己滚
            // Esc：取消二次确认（键盘用户不必再点空白处）。
            try {
                Event __esc = Event.current;
                if (__esc != null && __esc.type == EventType.KeyDown && __esc.keyCode == KeyCode.Escape
                    && !string.IsNullOrEmpty(_pendingKey)) { ClearPending(); __esc.Use(); }
            } catch (Exception __ee) { SRApi.Guard.Log("预设列表/Esc 取消确认", __ee); }
            string pendingBefore = _pendingKey;
            try {
                EnsureLoaded();
                EnsureCols();
            } catch (Exception __ld) { SRApi.Guard.Log("预设列表/加载", __ld); }

            RenderFilterHeader();
            RenderListToolbar();
            ApplyFilterChange();

            _actionHitThisFrame = false;
            try { SRApi.Ctl.SetTableHighlight(_search ?? ""); } catch (Exception __hl) { SRApi.Guard.Log("预设列表/高亮", __hl); }
            RenderListTable(VisiblePresets());
            DrawInlineRename();
            CancelPendingOnBlank(pendingBefore);
        }

        // 唯一应用入口：入参与 Wheel.Value / WheelPoll 同号 —— **上滚为正**（下滚为负）。
        // 以前按"正 = 向下"写，与核心 TableWheelInput（e.delta.y 向下为正）方向对冲 → 列表滚不动。
        // 步长统一为 Sc(56)（与联机列表一致，不再每个列表一套手感）。
        private static void ApplyListWheel(float notches) {
            if (Mathf.Abs(notches) < 0.01f) return;
            float before = _scrollRows.y;
            _scrollRows.y = Mathf.Clamp(_scrollRows.y - notches * SRApi.Ctl.Sc(56f), 0f, _listMaxY);
            if (Mathf.Abs(_scrollRows.y - before) > 0.01f) {
                try { SRApi.MarkWheelUsed(); } catch { /* 静默：非关键路径 */ }
            }
        }

        // 窗口在帧尾派发的页面级滚轮（兜底）：只有绘制期没消费时才生效。
        // 窗口传进来的 wheel 符号约定未知 → 统一按"和 Event.delta.y 一致（向下为正）"处理，
        // 若实测相反只改这一个符号。
        private const float PageWheelSign = 1f;
        private static int _listPageFrame = -1;   // 只有列表页刷新它（保存页不写，避免误判）

        private static bool OnListPageWheel(Vector2 mouse, float wheel) {
            try {
                if (Time.frameCount - _listPageFrame > 2) return false;   // 不是本页
                if (Mathf.Abs(wheel) < 0.0001f) return false;
                // 命中测试：只有鼠标在表格矩形上才归列表滚（否则在搜索框/工具行上滚也会带动列表）。
                if (_tableBox.width <= 1f || !_tableBox.Contains(mouse)) return false;
                if (_listWheelFrame == Time.frameCount) return true;      // 绘制期已经滚过
                _listWheelFrame = Time.frameCount;
                float notches = Mathf.Abs(wheel) < 0.5f ? 1f : Mathf.Round(Mathf.Abs(wheel));
                float before = _scrollRows.y;
                ApplyListWheel(PageWheelSign * Mathf.Sign(wheel) * Mathf.Max(1f, notches));
                // 只有真的滚动了才吃掉事件；没滚动就交回窗口，别把滚轮吞掉。
                return Mathf.Abs(_scrollRows.y - before) > 0.01f;
            } catch (Exception __ex) { SRApi.Guard.Log("预设列表/页面滚轮", __ex); return false; }
        }

        // ================= 列表本体 =================

        private static void RenderListTable(List<PresetInfo> visible) {
            float rowH = SRApi.Ctl.TableRowH();
            float headerH = SRApi.Ctl.TableHeaderH();
            float minH = headerH + rowH * 2f;

            // 与「联机」列表同一套布局（用户要求照联机列表做）：
            //   probe 取表格顶 → 表格高 = 页面内容底 − 表格顶 → 行区 = 表格高 − 表头。
            // 不再用 ContentArea/ScrollRowsH 自己算一套（多套口径就是"列表只剩一行"的根因）。
            Rect probe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            // 表格高 = 页面内容底 - 表格顶（PageContentBottom 本身已扣掉底栏与标题栏）。
            float __bottom = SRApi.PageContentBottom - SRApi.Ctl.Sc(2f);
            float tableH = Mathf.Max(minH, __bottom - probe.y);
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true),
                GUILayout.Height(tableH));
            // 关键：box 可能被布局算到可见区外（窗口缩放/页头变高/底栏变化时），直接拿 box.height 当行区会把
            // rowsH 虚高 → maxY 被压小 → "能滚但滚不到最后几行"。用核心唯一几何把行区收敛成屏幕真实可见高度，
            // 并把 box 底边夹到"页面内容底界"（= 底栏上沿），窗口/底栏变化时表格每帧都跟着变。
            if (box.yMax > __bottom) box.height = Mathf.Max(headerH + rowH * 2f, __bottom - box.y);
            float rowsH = SRApi.Ctl.ScrollRowsH(box, headerH, __bottom, rowH);
            _listMaxY = SRApi.Ctl.TableMaxScrollY(visible.Count, rowH, rowsH);
            _maxScrollY = _listMaxY;
            // 每帧把滚动位置夹回当前 maxY（与联机 Online.Table.cs 同一行）：窗口缩放/切页/页头高度变化后，
            // scrollY 不会停留在旧上限，也不会出现"到底了还差一行"或滚过头的空表体。
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _listMaxY);
            _rowsHCache = rowsH;
            _listPageFrame = Time.frameCount;

            try {
                _visibleCache = visible;
                EnsureCols();
                // 操作列宽 = 三个按钮实际需要的宽度（不再按 30% 上限硬夹：窗口一窄就把"加载/导出/删除"切掉）。
                // 只有"别把数据区挤没"这一条上限。
                float __need = DefaultActionColW();
                _colW[ColAction] = Mathf.Min(__need, Mathf.Max(SRApi.Ctl.Sc(96f), box.width - SRApi.Ctl.Sc(140f)));
                if (_args == null) _args = new SRApi.TableArgs();
                float pinW = SRApi.Ctl.Sc(_colW[ColAction]);
                float __pinMax = Mathf.Max(SRApi.Ctl.Sc(40f), box.width - SRApi.Ctl.Sc(80f));
                if (pinW > __pinMax) pinW = __pinMax;
                float stripW = Mathf.Max(0f, box.width - pinW);

                if (_drawW == null || _drawW.Length != _colW.Length) _drawW = new float[_colW.Length];
                Array.Copy(_colW, _drawW, _colW.Length);
                float totalW = 0f;
                for (int i = 0; i < _order.Length; i++) {
                    int ci = _order[i];
                    if (ci == ColAction) continue;
                    if (_hiddenCols.Contains(ci)) continue;
                    totalW += SRApi.Ctl.Sc(_drawW[ci]);
                }
                _maxScrollX = Mathf.Max(0f, totalW - stripW);
                _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _maxScrollX);

                Event e = Event.current;
                Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
                _tableBox = box;
                _args.box = box;
                _args.rowsClip = new Rect(box.x, box.y + headerH, stripW, rowsH);
                _args.headerH = headerH;
                _args.rowH = rowH;
                _args.rowsH = rowsH;
                _args.stripW = stripW;
                _args.colW = _drawW;
                _args.vis = _vis;
                _args.rowCount = visible.Count;
                _args.pinCol = ColAction;
                _args.alignLeftCol = ColName;
                _args.scrollX = _scrollCols.x;
                _args.scrollY = _scrollRows.y;
                _args.mp = mp;
                _args.pinW = pinW;
                _args.pinX = box.x + stripW;
                _args.pinTitle = SRApi.T("操作", "Actions");
                _args.drawHeader = true;
                _args.headerLeft = false;
                _args.title = _titleFn;
                _args.tip = _tipFn;
                _args.rowCells = _rowCellsFn;
                _args.rowSelected = null;
                _args.rowAlpha = null;
                _args.resizable = true;
                _args.reorderable = true;
                _args.order = _order;
                _args.applyOrder = _applyOrderFn;
                _args.saveOrder = _saveOrderFn;
                _args.saveWidths = _saveOrderFn;
                _args.hidden = _hiddenCols;
                _args.hideable = true;
                _args.resetAll = _resetAllFn;
                if (_defColWCache == null) _defColWCache = DefColW;
                _args.defaultColW = _defColWCache;
                _args.click = _clickFn;
                _args.sortCol = -1;
                _args.headerCells = _headerCells;
                _args.headerCols = _headerCols;
                _args.rowRects = _rowRects;
                _args.rowRows = _rowRows;
                _args.pinRects = _pinRects;
                _args.pinRows = _pinRows;
                _args.pinDraw = _pinDrawFn;
                _args.rowRightClick = _rowRightClickFn;
                _args.rowDoubleClick = _rowDoubleClickFn;
                _args.emptyHint = _emptyHintFn;

                _hiddenBeforeTable = _hiddenCols.Count;
                // 滚轮入口照联机列表：绘制期就地在表格矩形上取用（表格区域优先于页面钩子）。
                bool __wheelHandled = false;
                try {
                    float __wheelNow = SRApi.Ctl.WheelPoll("presets.table", box);
                    if (Mathf.Abs(__wheelNow) > 0.01f) {
                        _listWheelFrame = Time.frameCount;
                        __wheelHandled = true;
                        ApplyListWheel(__wheelNow);
                    }
                } catch (Exception __wp) { SRApi.Guard.Log("预设列表/区域滚轮", __wp); }
                // 滚轮交给核心（TableWheelInput 与 TableDraw 语义完全一致：ScrollWheel 事件、
                // box 命中、maxY>0 才滚、到边界不消费）。**在绘制前**调用，先改 scrollY 再绘制。
                // 绘制期 WheelPoll 已经滚过就跳过：两条入口都滚会变双倍步长（联机列表也是这个问题）。
                if (e != null && !__wheelHandled) {
                    try {
                        // 核心吃掉滚轮后必须打帧锁：否则同一帧的帧尾 page hook 会再滚一次（一格滚两倍）。
                        if (SRApi.TableWheelInput(e, box, mp, _maxScrollX, _listMaxY, ref _scrollCols, ref _scrollRows))
                            _listWheelFrame = Time.frameCount;
                    } catch (Exception __wh) { SRApi.Guard.Log("预设列表/核心滚轮", __wh); }
                }
                _args.scrollY = _scrollRows.y;
                SRApi.TableDraw(_args);
                // 竖向滚动条：几何/绘制/输入交给核心（与联机列表一致，不再自绘）。
                try { SRApi.Ctl.TableScrollbarFor(_args, e, ref _sbDragging, ref _sbGrabY, ref _scrollRows.y); }
                catch (Exception __sb) { SRApi.Guard.Log("预设列表/滚动条", __sb); }
                // 光标：拖列宽 / 悬停表头（与联机列表一致）。
                if (_args.dragCol >= 0) SRApi.CursorResize = true;
                if (e != null && box.Contains(mp) && _headerCells != null) {
                    for (int __hc = 0; __hc < _headerCells.Count; __hc++)
                        if (_headerCells[__hc].Contains(mp)) { SRApi.CursorMove = true; break; }
                }
                // 核心把拖列宽的结果写进 _drawW：同步回持久化宽度（漏了这段 = 表头宽度永远改不动）。
                for (int i = 0; i < _colW.Length && i < _drawW.Length; i++) {
                    if (i == ColAction) continue;
                    if (Mathf.Abs(_colW[i] - _drawW[i]) > 0.01f) _colW[i] = _drawW[i];
                }
                HandlePresetCells();
                if (_hiddenCols.Count > _hiddenBeforeTable) SaveCols();
                if (e != null) {
                    SRApi.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _args.moveCol, ref _args.moveDx,
                        ref _args.dragCol, ref _dragH, SaveCols, null, null);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("预设列表/表格绘制", __ex); }
        }
    }
}
