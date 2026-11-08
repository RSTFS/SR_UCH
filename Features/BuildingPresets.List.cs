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

        // ================= 页面二：预设列表 =================
        private static void RenderListPage() {
            _renderFrame = Time.frameCount;
            SRApi.NoPageScrollBars = true;   // 填充模式：页头固定，只有列表自己滚
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

        // 取本帧滚轮量（不做命中测试）。三条来源：
        //   ScrollWheel 事件（最可靠）→ Input.mouseScrollDelta → 都没有就当 0。
        private static float TakeListWheel() {
            try {
                Event e = Event.current;
                float d = 0f;
                if (e != null && e.type == EventType.ScrollWheel) {
                    float raw = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                    d = -raw;
                } else {
                    try { d = Input.mouseScrollDelta.y; } catch { /* 静默：该环境没有滚轮轴 */ }
                }
                if (Mathf.Abs(d) < 0.01f) return 0f;
                if (_listWheelFrame == Time.frameCount) return 0f;   // 本帧只应用一次（Layout/Repaint 各一趟）
                // 本帧已被别的控件取走（展开的下拉框、滑块…）：列表不再跟着滚。
                try { if (SRApi.WheelFrame == Time.frameCount) return 0f; } catch { /* 静默：查询失败按未消费处理 */ }
                _listWheelFrame = Time.frameCount;
                if (e != null && e.type == EventType.ScrollWheel) { try { e.Use(); } catch { /* 静默：已被消费 */ } }
                try { SRApi.MarkWheelUsed(); } catch { /* 静默：非关键路径 */ }
                return d;
            } catch (Exception __ex) { SRApi.Guard.Log("预设列表/取滚轮", __ex); return 0f; }
        }

        // 窗口在帧尾派发的页面级滚轮：绘制期没取到（比如那帧没有 ScrollWheel 事件）时兜底。
        private static bool OnListPageWheel(Vector2 mouse, float wheel) {
            try {
                if (Time.frameCount - _renderFrame > 2) return false;   // 不是本页
                if (Mathf.Abs(wheel) < 0.01f) return false;
                if (_listWheelFrame == Time.frameCount) return true;    // 绘制期已经滚过：吃掉但不再滚
                _listWheelFrame = Time.frameCount;
                float rowH = SRApi.Ctl.TableRowH();
                float before = _scrollRows.y;
                _scrollRows.y = Mathf.Clamp(_scrollRows.y - wheel * rowH * 3f, 0f, _listMaxY);
                return Mathf.Abs(_scrollRows.y - before) > 0.01f || _listMaxY > 0f;
            } catch (Exception __ex) { SRApi.Guard.Log("预设列表/页面滚轮", __ex); return false; }
        }

        // ================= 列表本体 =================
        private static void RenderListTable(List<PresetInfo> visible) {
            float rowH = SRApi.Ctl.TableRowH();
            float headerH = SRApi.Ctl.TableHeaderH();
            float minH = headerH + rowH * 2f;

            // 控件创建全部在 try 之外：Layout / Repaint 两趟必须创建同样多的控件。
            Rect probe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            // 底界：右栏纯尺寸 − 底栏。probe.y 与它是同一坐标系（都在 BeginArea 里）。
            float bottomLocal = SRApi.ContentArea.height - SRApi.FooterHeight - SRApi.Ctl.Sc(2f);
            float tableH = Mathf.Max(minH, bottomLocal - probe.y);
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true),
                GUILayout.Height(tableH));

            // 行区 / 内容高 / 滚动上限：三者同源（都用 box 自己的高度），不会再对不上。
            float rowsH = Mathf.Max(rowH, box.height - headerH);
            float contentH = Mathf.Max(0, visible.Count) * rowH;
            _listMaxY = Mathf.Max(0f, contentH - rowsH);
            _maxScrollY = _listMaxY;
            _rowsHCache = rowsH;

            float wheel = TakeListWheel();
            if (Mathf.Abs(wheel) > 0.01f) _scrollRows.y = Mathf.Clamp(_scrollRows.y - wheel * rowH * 3f, 0f, _listMaxY);
            else _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _listMaxY);

            try {
                _visibleCache = visible;
                EnsureCols();
                _colW[ColAction] = Mathf.Min(DefaultActionColW(), Mathf.Max(SRApi.Ctl.Sc(96f), box.width * 0.30f));
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
                _args.title = delegate(int ci) { return ColumnTitle(ci); };
                _args.tip = ColumnTip;
                _args.rowCells = delegate(int r) { return RowCells(visible, r); };
                _args.rowSelected = null;
                _args.rowAlpha = null;
                _args.resizable = true;
                _args.reorderable = true;
                _args.order = _order;
                _args.applyOrder = delegate(int[] o) { _order = o; SaveCols(); };
                _args.saveOrder = delegate { SaveCols(); };
                _args.saveWidths = delegate { SaveCols(); };
                _args.hidden = _hiddenCols;
                _args.hideable = true;
                _args.resetAll = delegate { ResetTable(); };
                _args.defaultColW = DefColW;
                _args.click = delegate(int ci) {
                    if (ci != ColLevel) return;
                    _levelShowCode = !_levelShowCode;
                };
                _args.sortCol = -1;
                _args.headerCells = _headerCells;
                _args.headerCols = _headerCols;
                _args.rowRects = _rowRects;
                _args.rowRows = _rowRows;
                _args.pinRects = _pinRects;
                _args.pinRows = _pinRows;
                _args.pinDraw = delegate(int row, Rect r, int visibleIdx) { DrawActionButtons(visible, row, r); };
                _args.emptyHint = delegate {
                    return _presets.Count == 0
                        ? SRApi.T("还没有预设：在「预设保存」页保存当前布局，或从剪贴板 / 文件导入。", "No presets yet: save the current layout on the Save page, or import from clipboard / file.")
                        : SRApi.T("没有匹配当前搜索的预设。", "No presets match the current search.");
                };
                _hiddenBeforeTable = _hiddenCols.Count;
                _sbHandled = false;
                SRApi.TableDraw(_args);
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
