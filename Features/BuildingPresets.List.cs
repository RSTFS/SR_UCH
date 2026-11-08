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

        // 滚轮：**唯一来源 = ScrollWheel 事件**，唯一方向约定 = 「向下滚 = notches 为正」。
        // 教训（见 列表滚动问题深度分析.md）：
        //   · Input.mouseScrollDelta.y 的正负号与 IMGUI Event.delta.y 相反，混用会让方向反掉；
        //   · Layout 趟读 Input 会抢在真正的 ScrollWheel 趟之前，再用帧锁把正确路径挡掉，
        //     结果每格滚轮只被方向反的那条路消费 → 永远滚不动。
        // 现在只在 ScrollWheel 趟取量、无条件 e.Use()、再判帧锁。
        private static float TakeListWheel() {
            try {
                Event e = Event.current;
                if (e == null || e.type != EventType.ScrollWheel) return 0f;
                float raw = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                if (Mathf.Abs(raw) < 0.0001f) return 0f;
                try { e.Use(); } catch { /* 静默：已被别处消费 */ }
                if (_listWheelFrame == Time.frameCount) return 0f;
                _listWheelFrame = Time.frameCount;
                // delta.y > 0 = 向下滚 = 内容上移 = scrollY 增加
                float notches = Mathf.Abs(raw) < 0.5f ? 1f : Mathf.Round(Mathf.Abs(raw));
                return Mathf.Sign(raw) * Mathf.Max(1f, notches);
            } catch (Exception __ex) { SRApi.Guard.Log("预设列表/取滚轮", __ex); return 0f; }
        }

        // 唯一应用入口：notches > 0 = 向下滚。
        private static void ApplyListWheel(float notches) {
            // 没有滚轮时**不做任何夹取**：Layout / Repaint 两趟算出的 box.height 可能不同，
            // 每帧无条件 Clamp 会把刚滚到的位置夹回去（表现为"滚一下弹回顶部/滚不动"）。
            if (Mathf.Abs(notches) < 0.01f) return;
            float rowH = SRApi.Ctl.TableRowH();
            float before = _scrollRows.y;
            _scrollRows.y = Mathf.Clamp(_scrollRows.y + notches * rowH * 3f, 0f, _listMaxY);
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
                if (_listWheelFrame == Time.frameCount) return true;      // 绘制期已经滚过
                _listWheelFrame = Time.frameCount;
                float notches = Mathf.Abs(wheel) < 0.5f ? 1f : Mathf.Round(Mathf.Abs(wheel));
                ApplyListWheel(PageWheelSign * Mathf.Sign(wheel) * Mathf.Max(1f, notches));
                return true;
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
            // 底界 = 右栏高度 − 底栏高度（**不再额外留缝**：用户要求列表底边直接连到底栏上沿）。
            float bottomLocal = SRApi.ContentArea.height - SRApi.FooterHeight;
            float tableH = Mathf.Max(minH, bottomLocal - probe.y);
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true),
                GUILayout.Height(tableH));

            // 行区 / 内容高 / 滚动上限：三者同源（都用 box 自己的高度），不会再对不上。
            float rowsH = Mathf.Max(rowH, box.height - headerH);
            float contentH = Mathf.Max(0, visible.Count) * rowH;
            _listMaxY = Mathf.Max(0f, contentH - rowsH);
            _maxScrollY = _listMaxY;
            _rowsHCache = rowsH;

            _listPageFrame = Time.frameCount;
            ApplyListWheel(TakeListWheel());

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
                SRApi.TableDraw(_args);
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
