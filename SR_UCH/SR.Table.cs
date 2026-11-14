// 表格控件：TableArgs -> TableDraw 全链路（表头 / 行绘制 / 行命中 / 钉住列 / 滚动条 / 列宽换位与排序）+ 外观 TableStyle。
// 核心只在这里实现，功能层经 SRApi.TableArgs / SRApi.Ctl 纯转发使用。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Table（表格控件：参数 -> 绘制 / 命中 / 滚动条 / 列交互 + 外观）

    // 统一入口的参数（内容与状态由调用方填，绘制结果回填到列表 / 矩形）

    // 行点击状态：按下行用于过滤"从行上按下、在行上松开"的点击（拖列宽 / 滚动条释放不会误触发）。
    private static int _rowDownRow = -1;
    private static int _emptyHintFrame = -1;   // 空提示绘制帧（防重复）
    private static int _rowLastClickRow = -1;
    private static float _rowLastClickTime = -999f;

    // 统一入口：表头 + 行命中 + 行单元格 + 钉住列命中；相位顺序 PrepareArgs -> HeaderDispatch ->
    // RowHit/Selection -> HeaderInteractions -> DragGhost -> Pinned -> EmptyHint -> Rows -> ColumnHide
    // -> FinishResize -> DispatchRowClick。顺序即绘制 / 命中顺序，改动会影响 IMGUI 叠层与事件消费。
    public static void TableDraw(SRApi.TableArgs a) {
        if (a == null || a.colW == null || a.vis == null) return;
        try {
            float headerH, rowH;
            TablePrepareArgs(a, out headerH, out rowH);
            if (a.drawHeader) TableHeaderAndShiftClick(a, headerH);
            TableRowRects(a.rowCount, rowH, a.scrollY, a.rowsH, a.box.y, headerH, a.rowsClip, a.box.x, a.stripW, a.rowRects, a.rowRows);
            TableRowHitAndSelection(a);
            TableHeaderInteractions(a);
            TableDrawDragGhost(a);
            TablePinnedRects(a.rowCount, rowH, a.scrollY, a.rowsH, a.box.y, headerH, a.pinX, a.pinW, a.pinRects, a.pinRows);
            TableDrawEmptyHint(a, rowH);
            TableDrawRows(a, headerH, rowH);
            TableDrawPinned(a);   // 钉住列最后画：盖在滚动行之上，横向滚动时始终可见
            TableHandleColumnHide(a);
            TableFinishResize(a);
            TableDispatchRowClick(a);
        } catch (Exception __ex) { Guard.Log("表格绘制", __ex); }
    }

    private static void TablePrepareArgs(SRApi.TableArgs a, out float headerH, out float rowH) {
        DeriveVisibleCols(a);
        headerH = a.headerH > 0f ? a.headerH : Sc(24);
        if (a.vertical) headerH = (a.headerH > 0f ? a.headerH : Sc(22)) * Mathf.Max(1, a.vis != null ? a.vis.Count : 0); // 纵向时表头高 = 单格高 × 列数
        rowH = a.rowH > 0f ? a.rowH : Sc(26);
        // order 是换列的必需输入；未赋时兜底成恒等序。
        if (a.drawHeader && (a.order == null || a.order.Length != a.colW.Length)) {
            int[] ident = new int[a.colW.Length];
            for (int i = 0; i < ident.Length; i++) ident[i] = i;
            a.order = ident;
        }
    }

    private static void TableHeaderAndShiftClick(SRApi.TableArgs a, float headerH) {
        DrawTableHeader(a.vis, a.pinCol, a.colW, a.scrollX, a.box, a.stripW, headerH, a.mp, a.title, a.moveCol,
            a.pinW, a.pinX, a.pinTitle, a.headerCells, a.headerCols, a.vertical, a.scrollY, a.headerLeft, out a.pinHeader);
        DrawLine(new Rect(a.box.x, a.box.y + headerH - 1f, a.stripW, 1f), CurStyle.LineStrong);
        // 表头 Shift+左键交给页面处理列头专属开关；须用 MouseDown 判定，MouseUp 时修饰键可能已丢。
        try {
            Event __he = Event.current;
            if (__he != null && __he.type == EventType.MouseDown && __he.button == 0 && __he.shift
                && a.headerShiftClick != null && a.headerCells.Count > 0) {
                for (int __hk = 0; __hk < a.headerCells.Count && __hk < a.headerCols.Count; __hk++) {
                    if (!a.headerCells[__hk].Contains(__he.mousePosition)) continue;
                    a.headerShiftClick(a.headerCols[__hk]);
                    __he.Use();
                    break;
                }
            }
        } catch (Exception __ex) { Guard.Log("表头Shift点击派发", __ex); }
    }

    private static void TableRowHitAndSelection(SRApi.TableArgs a) {
        try {
            Event __re = Event.current;
            int __hov = -1;
            if (__re != null && a.rowsClip.width > 0f && a.rowsClip.height > 0f && a.rowsClip.Contains(__re.mousePosition)) {
                Vector2 __rmp = __re.mousePosition;
                bool __inPin = a.pinW > 0f && __rmp.x >= a.pinX;
                if (!__inPin) {
                    for (int k = 0; k < a.rowRects.Count && k < a.rowRows.Count; k++) {
                        if (a.rowRects[k].Contains(__rmp)) { __hov = a.rowRows[k]; break; }
                    }
                }
            }
            a.rowHover = __hov;
        } catch (Exception __ex) { Guard.Log("表格行命中", __ex); }
        if (a.rowSelected == null || a.rowRects == null || a.rowRects.Count <= 0) return;
        // 只在 Repaint 趟绘制：非 Repaint 调 DrawTexture 会报错。
        try { if (Event.current != null && Event.current.type != EventType.Repaint) return; }
        catch { return; }
        try {
            for (int __i = 0; __i < a.rowRects.Count; __i++) {
                if (!a.rowSelected(a.rowRows[__i])) continue;
                Color __oc = GUI.color;
                try { GUI.color = new Color(0.55f, 0.78f, 1f, 0.22f); GUI.DrawTexture(a.rowRects[__i], Px()); }
                finally { GUI.color = __oc; }
            }
        } catch (Exception __ex) { Guard.Log("行选中高亮", __ex); }
    }

    // 相位 4：列宽拖动 / 换位 / 列头提示 / 排序箭头（纵向表头不做列宽与换位）。
    private static void TableHeaderInteractions(SRApi.TableArgs a) {
        if (a.headerCells.Count <= 0 || a.vertical) return; // 纵向不做列宽/行高拖动
        bool inBox = a.box.Contains(a.mp);
        for (int k = 0; k < a.headerCells.Count; k++) {
            Rect cell = a.headerCells[k];
            int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
            if (a.tip != null && inBox && cell.Contains(a.mp)) {
                string tip = null;
                try { tip = a.tip(ci); } catch { tip = null; }
                if (!string.IsNullOrEmpty(tip)) GUI.tooltip = tip;
            }
            if (a.sortCol >= 0 && ci == a.sortCol) {
                SR.Ctl.SecHeaderCenter.Draw(new Rect(cell.xMax - Sc(14), cell.y, Sc(12), cell.height),
                    new GUIContent(a.sortAsc ? "\u25B2" : "\u25BC"), false, false, false, false);
            }
            if (!a.resizable) continue;
            Rect sep = new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
            if (inBox && sep.Contains(a.mp)) CursorResize = true;
            TableColResize(Event.current, cell, ci, a.mp, inBox, a.colW,
                ref a.dragCol, ref a.dragX0, ref a.dragW0, ref a.scrollColsTmp, ref a.scrollColsAtDragTmp);
        }
        if (!a.reorderable || a.order == null) return;
        if (a.allCols.Count != a.colW.Length) { a.allCols.Clear(); for (int i = 0; i < a.colW.Length; i++) a.allCols.Add(i); }
        // 列序长度与该表列数不一致时按本表列数重建，否则拖动看不出变化。
        if (a.colW != null && a.colW.Length > 0 && a.order.Length != a.colW.Length) {
            int[] __ord = new int[a.colW.Length];
            for (int i = 0; i < __ord.Length; i++) __ord[i] = i;
            a.order = __ord;
        }
        int __dragK = -1;
        if (a.moveCol >= 0) {
            for (int k = 0; k < a.headerCells.Count; k++) {
                int __c2 = (a.headerCols.Count > k) ? a.headerCols[k] : k;
                if (__c2 == a.moveCol) { __dragK = k; break; }
            }
            if (__dragK < 0 && a.moveCol < a.headerCells.Count) __dragK = a.moveCol;   //兜底：按下时记录的就是本表列下标
        }
        // 落点列列表用 headerCols（显示位 -> 数据列），不能用自然序的 allCols。
        List<int> __cols = (a.headerCols != null && a.headerCols.Count > 0) ? a.headerCols : a.allCols;
        for (int k = 0; k < a.headerCells.Count; k++) {
            if (__dragK >= 0 && k != __dragK) continue; // 拖动中：只处理拖动的那一列
            int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
            TableColMove(Event.current, a.headerCells[k], ci, k, __cols, a.mp, inBox, a.headerCells, a.order,
                a.applyOrder, a.saveOrder, a.click, ref a.moveCol, ref a.moveX0, ref a.moveDx,
                ref a.scrollColsTmp, ref a.scrollColsAtDragTmp, a.vertical);
        }
        if (a.moveCol >= 0 && Event.current != null && Event.current.type == EventType.MouseUp && Event.current.button == 0) {
            int __mv = a.moveCol; int __k = -1;
            for (int k = 0; k < a.headerCells.Count; k++) { int __c3 = (a.headerCols.Count > k) ? a.headerCols[k] : k; if (__c3 == __mv) { __k = k; break; } }
            if (__k < 0 && __mv < a.headerCells.Count) __k = __mv;
            if (__k >= 0) {
                TableColMove(Event.current, a.headerCells[__k], __mv, __k, __cols, a.mp, a.box.Contains(a.mp), a.headerCells, a.order,
                    a.applyOrder, a.saveOrder, a.click, ref a.moveCol, ref a.moveX0, ref a.moveDx,
                    ref a.scrollColsTmp, ref a.scrollColsAtDragTmp, a.vertical);
            } else { a.moveCol = -1; a.moveDx = 0f; }
        }
    }

    // 相位 5：列头拖拽动画（仅 Repaint），不改数据。
    private static void TableDrawDragGhost(SRApi.TableArgs a) {
        if (a.moveCol < 0 || Event.current == null || Event.current.type != EventType.Repaint) return;
        try {
            int __mk = -1;
            for (int k = 0; k < a.headerCells.Count; k++) { int __c = (a.headerCols.Count > k) ? a.headerCols[k] : k; if (__c == a.moveCol) { __mk = k; break; } }
            if (__mk < 0 && a.moveCol < a.headerCells.Count) __mk = a.moveCol;
            if (__mk < 0) return;
            Rect __src = a.headerCells[__mk];
            int __tgt = __mk;
            for (int j = 0; j < a.headerCells.Count; j++) { if (a.mp.x < a.headerCells[j].xMax) { __tgt = j; break; } __tgt = a.headerCells.Count - 1; }
            Color __old = GUI.color;
            try {
                if (__tgt != __mk && !(a.pinCol > 0 && a.headerCells[__tgt].x >= a.pinX)) {
                    GUI.color = new Color(1f, 0.85f, 0.25f, 0.95f);
                    GUI.Box(new Rect(a.headerCells[__tgt].xMax - Sc(1), __src.y, Sc(2), __src.height), GUIContent.none);
                }
                // 跟手半透明块
                GUI.color = new Color(1f, 1f, 1f, 0.45f);
                GUI.Box(new Rect(a.mp.x - __src.width * 0.5f, __src.y - Sc(2), __src.width, __src.height), GUIContent.none);
            } finally { GUI.color = __old; }
        } catch (Exception __ex) { Guard.Log("SR.Ctl", __ex); }
    }

    // 相位 6：钉住列的按钮定位与派发。
    private static void TableDrawPinned(SRApi.TableArgs a) {
        if (a.pinW <= 0f || a.pinDraw == null) return;
        for (int k = 0; k < a.pinRects.Count && k < a.pinRows.Count; k++) {
            try {
                a.pinDraw(a.pinRows[k], a.pinRects[k], k);
            } catch (Exception __pd) { Guard.Log("钉住列按钮", __pd); }
        }
    }

    // 相位 7：空列表提示（文本由调用方给）。
    private static void TableDrawEmptyHint(SRApi.TableArgs a, float rowH) {
        if (a.rowCount != 0 || a.emptyHint == null || _emptyHintFrame == Time.frameCount) return;
        _emptyHintFrame = Time.frameCount;   // 同一帧只画一次（防止页面/核心重复绘制出现两条提示）
        string eh = null;
        try { eh = a.emptyHint(); } catch { eh = null; }
        if (string.IsNullOrEmpty(eh)) return;
        Rect rc = a.rowsClip;
        SR.Ctl.LabelClipCenter.Draw(new Rect(rc.x, rc.y + Mathf.Max(0f, (a.rowsH - rowH * 2f) * 0.5f), Mathf.Max(Sc(60), a.stripW), rowH * 2f),
            new GUIContent(eh), false, false, false, false);
    }

    private static void TableDrawRows(SRApi.TableArgs a, float headerH, float rowH) {
        Color clipSavedColor = GUI.color; // 异常时也要还原（GUI.color 泄漏会污染游戏界面）
        GUI.BeginClip(new Rect(a.box.x, a.box.y + headerH, a.stripW, a.rowsH));
        try {
            int __firstRow = Mathf.Max(0, Mathf.FloorToInt(a.scrollY / Mathf.Max(1f, rowH)));
            int __lastRow = Mathf.Min(a.rowCount - 1, Mathf.CeilToInt((a.scrollY + a.rowsH) / Mathf.Max(1f, rowH)));
            for (int i = __firstRow; i <= __lastRow; i++) {
                float y = i * rowH - a.scrollY;
                string[] rc = a.rowCells != null ? a.rowCells(i) : null;
                float alpha = a.rowAlpha != null ? a.rowAlpha(i) : 1f;
                if ((i & 1) == 1) {
                    Color ab = CurStyle.RowAltBg;
                    if (alpha < 1f) ab.a *= alpha;
                    DrawFill(new Rect(0f, y, a.stripW, rowH), ab);
                }
                float __hy0 = Mathf.Max(y, 0f), __hy1 = Mathf.Min(y + rowH, a.rowsH);
                if (a.hoverHighlight && i == a.rowHover && __hy1 > __hy0) {
                    Color __hc = new Color(1f, 1f, 1f, 0.06f);
                    if (alpha < 1f) __hc.a *= alpha;
                    DrawFill(new Rect(0f, __hy0, a.stripW, __hy1 - __hy0), __hc);
                }
                Color prev = GUI.color;
                if (alpha < 1f) GUI.color = new Color(1f, 1f, 1f, alpha);
                DrawTableCells(a.vis, a.pinCol, a.alignLeftCol, rowH, a.scrollX, y, a.colW, rc);
                GUI.color = prev;
                DrawLine(new Rect(0f, y + rowH - 1f, a.stripW, 1f), CurStyle.LineLight);
            }
        } finally { GUI.EndClip(); GUI.color = clipSavedColor; }
    }

    // 相位 9：右键隐藏列 / Shift+右键恢复全部（含列宽还原，表头右端 +N 也可恢复）。
    private static void TableHandleColumnHide(SRApi.TableArgs a) {
        if (!a.hideable || a.hidden == null || Event.current == null || !a.box.Contains(a.mp)) return;
        Event e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 1) return;
        if (e.shift) {
            if (a.hidden != null && a.hidden.Count > 0) a.hidden.Clear();
            if (a.defaultColW != null && a.colW != null && a.defaultColW.Length == a.colW.Length) {
                for (int __i = 0; __i < a.colW.Length; __i++) a.colW[__i] = a.defaultColW[__i];
            }
            if (a.resetAll != null) { try { a.resetAll(); } catch (Exception __exr) { Guard.Log("表格还原全部", __exr); } }
            try { SR.LogInfo("[表格] Shift+右键：已还原列宽/列序并取消全部隐藏列"); } catch { /* 静默：日志不可用时忽略 */ }
            e.Use();
            return;
        }
        for (int k = 0; k < a.headerCells.Count; k++) {
            if (!a.headerCells[k].Contains(a.mp)) continue;
            int ci = k < a.headerCols.Count ? a.headerCols[k] : k;
            if (a.colW.Length - a.hidden.Count > 1) a.hidden.Add(ci); //至少留一列
            e.Use();
            break;
        }
    }

    // 相位 10：列宽拖动收尾（清状态并回调落盘）。
    private static void TableFinishResize(SRApi.TableArgs a) {
        if (a.dragCol < 0 || Event.current == null || Event.current.type != EventType.MouseUp) return;
        a.dragCol = -1;
        BlockWindowDrag = false;
        if (a.saveWidths != null) { try { a.saveWidths(); } catch (Exception __sw) { Guard.Log("表格列宽保存", __sw); } }
    }

    // 相位 11：行点击 / 双击 / 右键派发（左键须同一行按下再松开）。
    private static void TableDispatchRowClick(SRApi.TableArgs a) {
        try {
            Event __ce = Event.current;
            if (__ce == null) return;
            if (__ce.type == EventType.MouseDown && __ce.button == 0) {
                _rowDownRow = a.rowHover;
            } else if (__ce.type == EventType.MouseUp && __ce.button == 1) {
                int __rr = a.rowHover;
                if (__rr >= 0 && a.rowRightClick != null) {
                    try { a.rowRightClick(__rr); } catch (Exception __rc2) { Guard.Log("表格行右键", __rc2); }
                    __ce.Use();
                }
            } else if (__ce.type == EventType.MouseUp && __ce.button == 0) {
                int __clicked = (a.rowHover >= 0 && a.rowHover == _rowDownRow) ? a.rowHover : -1;
                _rowDownRow = -1;
                if (__clicked >= 0 && a.cellClick != null) {
                    try {
                        Vector2 __mp = __ce.mousePosition;
                        if (a.pinW <= 0f || __mp.x < a.pinX) {
                            float __x = a.box.x - a.scrollX;
                            for (int __k = 0; __k < a.vis.Count; __k++) {
                                int __ci = a.vis[__k];
                                if (__ci == a.pinCol) continue;
                                float __w = Sc(a.colW != null && __ci < a.colW.Length ? a.colW[__ci] : 60f);
                                if (__mp.x >= __x && __mp.x < __x + __w) { a.cellClick(__clicked, __ci); break; }
                                __x += __w;
                            }
                        }
                    } catch (Exception __cc) { Guard.Log("表格单元格点击", __cc); }
                }
                if (__clicked >= 0) {
                    if (a.rowClick != null) { try { a.rowClick(__clicked); } catch (Exception __rc) { Guard.Log("表格行点击", __rc); } }
                    float __now = Time.unscaledTime;
                    if (_rowLastClickRow == __clicked && __now - _rowLastClickTime <= 0.35f) {
                        _rowLastClickRow = -1; _rowLastClickTime = -999f;
                        if (a.rowDoubleClick != null) { try { a.rowDoubleClick(__clicked); } catch (Exception __rd) { Guard.Log("表格行双击", __rd); } }
                    } else {
                        _rowLastClickRow = __clicked; _rowLastClickTime = __now;
                    }
                }
            }
        } catch (Exception __ex) { Guard.Log("表格行点击派发", __ex); }
    }

    // 从 order + hidden 推导可见列（三处表格共用）；尺寸不一致时保持调用方给的 vis。
    private static void DeriveVisibleCols(SRApi.TableArgs a) {
        if (a == null || a.order == null || a.colW == null || a.order.Length != a.colW.Length) return;
        if (a.vis == null) a.vis = new List<int>(a.order.Length);
        a.vis.Clear();
        for (int i = 0; i < a.order.Length; i++) {
            int ci = a.order[i];
            if (ci < 0 || ci >= a.colW.Length) continue;
            if (a.hidden != null && a.hidden.Contains(ci)) continue;
            a.vis.Add(ci);
        }
    }

    // 表头绘制：可滚动列 + 钉住右列，输出每格屏幕矩形（命中用）。
    public static void DrawTableHeader(List<int> vis, int pinCol, float[] colW, float scrollX, Rect box, float stripW, float headerH,
        Vector2 mp, System.Func<int, string> title, int moveCol, float pinW, float pinX, string pinTitle,
        List<Rect> cells, List<int> cellCols, bool vertical, float scrollY, bool headerLeft, out Rect pinHeader) {
        cells.Clear(); cellCols.Clear(); pinHeader = new Rect(0f, 0f, 0f, 0f);
        float __vh = vertical ? (headerH * Mathf.Max(1, vis.Count)) : headerH; // 纵向：裁剪区是总高
        Color __hdrSaved = GUI.color;   // 裁剪块内改了 GUI.color，必须还原
        GUI.BeginClip(new Rect(box.x, box.y, stripW, vertical ? __vh : headerH));
        try {
        DrawFill(new Rect(0f, 0f, stripW, vertical ? __vh : headerH), CurStyle.HeaderBg);
        float x = -scrollX, y = -scrollY; // 纵向用 y 叠放
        for (int k = 0; k < vis.Count; k++) {
            int ci = vis[k];
            if (ci == pinCol) continue;
            float w = vertical ? stripW : Sc(colW[ci]);
            float h = vertical ? Sc(colW[ci]) : headerH;
            cells.Add(new Rect(box.x + x, box.y + y, w, h));
            cellCols.Add(ci);
            if (x + w > -Sc(4) && x < stripW + Sc(4)) {   //只画看得见的
                Rect __cr = new Rect(vertical ? 0f : x, vertical ? y : 0f, vertical ? stripW : w, h);
                Vector2 __lmh = new Vector2(mp.x - box.x, mp.y - box.y);
                if (__lmh.x >= 0f && __lmh.x < stripW && __lmh.y >= 0f && __lmh.y < (vertical ? __vh : headerH)
                    && __cr.Contains(__lmh) && moveCol != ci) DrawRect(__cr, 0.06f);
                if (moveCol == ci) DrawRect(__cr, 0.22f);
                (headerLeft ? SR.Ctl.LabelClip : SR.Ctl.SecHeaderCenter).Draw(__cr,
                    new GUIContent(title != null ? title(ci) : ""), false, false, false, false);
                if (vertical) DrawLine(new Rect(0f, y + h - 1f, stripW, 1f), CurStyle.LineStrong);
                else DrawLine(new Rect(x + w - 1f, 0f, 1f, headerH), CurStyle.LineStrong);
            }
            if (vertical) y += h; else x += w;
        }
        if (moveCol >= 0) {   //拖动中：在目标位置画竖线（裁剪空间里 x 要减掉裁剪原点）
            for (int k = 0; k < cells.Count; k++) {
                Rect cc = cells[k];
                if (mp.x < cc.xMax) { DrawRect(new Rect(cc.x - box.x - 1f, 0f, Sc(3), headerH), 0.9f); break; }
            }
        }
        } finally { GUI.EndClip(); GUI.color = __hdrSaved; }
        if (pinW > 0f) {
            // 直接按矩形画，不用 BeginClip：裁剪用布局坐标，窗口有偏移时会错位画到表格上方。
            pinHeader = new Rect(pinX, box.y, pinW, headerH);
            SR.Ctl.SecHeaderCenter.Draw(pinHeader, new GUIContent(pinTitle ?? ""), false, false, false, false);
            DrawLine(new Rect(pinX - 1f, box.y, 1f, headerH), CurStyle.LineStrong);
        }
    }

    // 数组版（TableDraw 走这条）：直接索引、零分配，越界列按空串处理。
    // 搜索命中高亮：页面每帧设置查询串，命中片段先画半透明底色再画文字。
    internal static string HighlightQuery;
    internal static readonly Color HighlightColor = new Color(1f, 0.84f, 0.30f, 0.22f);

    public static void DrawTableCells(List<int> vis, int skipCol, int alignLeftCol, float rowH, float scrollX, float y,
        float[] colW, string[] cells) {
        float x = -scrollX;
        for (int k = 0; k < vis.Count; k++) {
            int ci = vis[k];
            if (ci == skipCol) continue;
            float w = Sc(colW[ci]);
            Rect r = new Rect(x, y, w, rowH);
            GUIStyle st = ci == alignLeftCol ? SR.Ctl.LabelClip : SR.Ctl.LabelClipCenter;
            string s = (cells != null && ci >= 0 && ci < cells.Length) ? cells[ci] : null;
            try {
                string q = HighlightQuery;
                if (!string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(s)) {
                    int idx = s.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) {
                        float total = SR.Ctl.Label.CalcSize(new GUIContent(s)).x;
                        float pre = idx > 0 ? SR.Ctl.Label.CalcSize(new GUIContent(s.Substring(0, idx))).x : 0f;
                        float mid = SR.Ctl.Label.CalcSize(new GUIContent(s.Substring(idx, q.Length))).x;
                        float baseX = (ci == alignLeftCol) ? r.x + Sc(4f) : r.x + Mathf.Max(Sc(2f), (r.width - total) * 0.5f);
                        float hx = Mathf.Clamp(baseX + pre, r.x, r.xMax);
                        float hw = Mathf.Min(mid, r.xMax - hx);
                        if (hw > 0.5f) DrawFill(new Rect(hx, r.y + Sc(2f), hw, r.height - Sc(4f)), HighlightColor);
                    }
                }
            } catch (Exception __hl) { Guard.Log("表格/命中高亮", __hl); }
            st.Draw(r, CellContent(s), false, false, false, false);
            DrawLine(new Rect(r.xMax - 1f, r.y, 1f, r.height), CurStyle.LineLight);   //格内线用弱线
            x += w;
        }
    }

    // 复用同一个 GUIContent（每帧数百次调用）；GUIStyle.Draw 同步绘制、不持引用，复用安全。
    private static readonly GUIContent _cellContent = new GUIContent();
    private static GUIContent CellContent(string s) {
        _cellContent.text = s != null ? s : "";
        return _cellContent;
    }

    // 拖拽兜底：超过 1.2s 无鼠标事件就复位所有拖拽状态。
    public static void TableDragWatchdog(Event e, ref float watchdog,
        ref bool sbDragging, ref int moveCol, ref float moveDx, ref int dragCol, ref bool dragH,
        System.Action saveWidths, System.Action saveTableH, System.Action resetJoinDown) {
        if (e != null && e.type == EventType.MouseUp) return;
        if (watchdog > 0f && Time.unscaledTime > watchdog) {
            // 鼠标键仍按住时绝不复位，否则松开时 moveCol != ci、换位静默失效。
            if (UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2))
                return;
            sbDragging = false;
            if (moveCol >= 0) { moveCol = -1; moveDx = 0f; }
            if (dragCol >= 0) { dragCol = -1; if (saveWidths != null) saveWidths(); }
            if (dragH) { dragH = false; if (saveTableH != null) saveTableH(); }
            if (resetJoinDown != null) resetJoinDown();
            BlockWindowDrag = false;
            watchdog = 0f;
            return;
        }
        if (e == null) return;
        if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) {
            if (moveCol >= 0 || dragCol >= 0 || sbDragging || dragH) watchdog = Time.unscaledTime + 1.2f;
        } else if (e.type == EventType.MouseUp) {
            watchdog = 0f;
        }
    }

    // 表格滚轮：Shift+滚轮横向，无纵向余量时自动转横向。返回 true = 已消费。
    public static bool TableWheelInput(Event e, Rect box, Vector2 mp, float maxX, float maxY,
        ref Vector2 scrollCols, ref Vector2 scrollRows) {
        if (e == null || e.type != EventType.ScrollWheel || !box.Contains(mp)) return false;
        if (SR.Wheel.CtrlHeld) return false;   // Ctrl+滚轮留给页面（切页/缩放），列表不消费
        float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
        if (e.shift && maxX > 0f) {
            float beforeX = scrollCols.x;
            scrollCols.x = Mathf.Clamp(scrollCols.x + amt * Sc(60), 0f, maxX);
            // 已到边界：不消费，交给页面滚动
            if (Mathf.Abs(scrollCols.x - beforeX) < 0.01f) return false;
            e.Use(); MarkWheelUsed(); WheelShieldHit = true; return true;
        }
        if (maxY > 0f) {
            float before = scrollRows.y;
            scrollRows.y = Mathf.Clamp(scrollRows.y + amt * Sc(40), 0f, maxY);
            if (Mathf.Abs(scrollRows.y - before) < 0.01f) return false;
            e.Use(); MarkWheelUsed(); WheelShieldHit = true; return true;
        }
        if (maxX > 0f) {
            float beforeX = scrollCols.x;
            scrollCols.x = Mathf.Clamp(scrollCols.x + amt * Sc(60), 0f, maxX);
            if (Mathf.Abs(scrollCols.x - beforeX) < 0.01f) return false;
            e.Use(); MarkWheelUsed(); WheelShieldHit = true; return true;
        }
        return false;
    }

    // 竖向滚动条：拖拽 / 点轨道跳转，返回 true 表示已处理；统一不显示，唯一开关是 HideTableScrollbars。
    public static bool HideTableScrollbars = true;

    public static void TableScrollbar(Rect track, float contentH, float viewH, float scrollY, bool dragging, out Rect thumb, bool visible = true, float alpha = 1f) {
        thumb = default(Rect);
        try {
            if (track.width <= 0f || track.height <= 0f || contentH <= viewH + 0.5f) return;
            float th = Mathf.Max(Sc(20f), track.height * Mathf.Clamp01(viewH / Mathf.Max(1f, contentH)));
            float maxScroll = Mathf.Max(0f, contentH - viewH);
            float k2 = maxScroll > 0.01f ? Mathf.Clamp01(scrollY / maxScroll) : 0f;
            thumb = new Rect(track.x, track.y + (track.height - th) * k2, track.width, th);
            if (!visible || alpha <= 0.01f || HideTableScrollbars) return;
            float a = Mathf.Clamp01(alpha);
            DrawFill(track, new Color(0.06f, 0.06f, 0.07f, 0.94f * a));
            bool hover = Event.current != null && thumb.Contains(Event.current.mousePosition);
            DrawFill(thumb, dragging ? new Color(0.51f, 0.51f, 0.53f, a)
                : hover ? new Color(0.41f, 0.41f, 0.44f, a)
                : new Color(0.31f, 0.31f, 0.34f, a));
        } catch (Exception __ex) { Guard.Log("表格滚动条绘制", __ex); }
    }

    public static bool TableScrollbarInput(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax,
        ref bool dragging, ref float grabY, ref float scrollY) {
        if (e == null) return false;
        if (dragging) {
            if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp) {
                float thumbH = thumb.height;
                float t = Mathf.Clamp01((mp.y - track.y - grabY) / Mathf.Max(1f, track.height - thumbH));
                scrollY = t * scrollMax;
                BlockWindowDrag = true;
                if (e.type == EventType.MouseUp) { dragging = false; BlockWindowDrag = false; }
                e.Use();
                return true;
            }
            return false;
        }
        if (e.type == EventType.MouseDown && e.button == 0 && track.width > 0f && track.Contains(mp)) {
            float thumbH = thumb.height;
            bool onThumb = thumb.Contains(mp);
            grabY = onThumb ? (mp.y - thumb.y) : thumbH * 0.5f;
            dragging = true;
            BlockWindowDrag = true;
            if (!onThumb) {   //点轨道：直接跳到该位置
                float t = Mathf.Clamp01((mp.y - track.y - grabY) / Mathf.Max(1f, track.height - thumbH));
                scrollY = t * scrollMax;
            }
            e.Use();
            return true;
        }
        return false;
    }

    // 列头拖动换位 / 单击 -> 返回 true 表示已处理。
    public static bool TableColMove(Event e, Rect cell, int ci, int k, List<int> cols, Vector2 mp, bool inBox,
        List<Rect> headerCells, int[] order, System.Action<int[]> applyOrder, System.Action save, System.Action<int> click,
        ref int moveCol, ref float moveX0, ref float moveDx, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag, bool vertical = false) {
        if (e == null) return false;
        Rect sep = vertical ? new Rect(cell.x, cell.yMax - Sc(3), cell.width, Sc(7)) : new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
        Rect body = vertical ? new Rect(cell.x, cell.y, cell.width, cell.height - Sc(4)) : new Rect(cell.x, cell.y, cell.width - Sc(4), cell.height);
        if (e.type == EventType.MouseDown && e.button == 0 && inBox && body.Contains(mp) && !sep.Contains(mp)) {
            moveCol = ci; moveX0 = vertical ? mp.y : mp.x; moveDx = 0f; scrollColsAtDrag = scrollCols; // moveX0 存本轴起点
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseDrag && moveCol == ci) {
            moveDx = (vertical ? mp.y : mp.x) - moveX0; // 离开列表也继续跟手
            scrollCols = scrollColsAtDrag;
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseUp && moveCol == ci) {
            if (order == null || order.Length == 0) { moveCol = -1; moveDx = 0f; BlockWindowDrag = false; e.Use(); return true; }
            bool dragged = Mathf.Abs(moveDx) > Sc(4);  //1~3px 手抖不算拖动
            int target = k;
            if (dragged && headerCells.Count > 0) {
                for (int j = 0; j < headerCells.Count; j++) {
                    if (vertical ? (mp.y < headerCells[j].yMax) : (mp.x < headerCells[j].xMax)) { target = j; break; }
                    target = headerCells.Count - 1;
                }
            }
            if (dragged && target != k && target >= 0 && target < cols.Count) {
                int from = -1, to = -1;
                for (int i = 0; i < order.Length; i++) {
                    if (order[i] == ci) from = i;
                    if (order[i] == cols[target]) to = i;
                }
                if (from >= 0 && to >= 0 && from != to) {
                    int moved = order[from];
                    List<int> l = new List<int>(order);
                    l.RemoveAt(from);
                    l.Insert(to, moved);
                    if (applyOrder != null) applyOrder(l.ToArray());
                    if (save != null) save();
                }
            } else if (!dragged && cell.Contains(mp)) {
                if (click != null) click(ci);
            }
            moveCol = -1; moveDx = 0f; BlockWindowDrag = false; e.Use(); return true;
        }
        return false;
    }

    // 列宽拖动 -> 返回 true 表示已处理。
    public static bool TableColResize(Event e, Rect cell, int ci, Vector2 mp, bool inBox, float[] colW,
        ref int dragCol, ref float dragX0, ref float dragW0, ref Vector2 scrollCols, ref Vector2 scrollColsAtDrag) {
        if (e == null) return false;
        Rect sep = new Rect(cell.xMax - Sc(3), cell.y, Sc(7), cell.height);
        if (e.type == EventType.MouseDown && e.button == 0 && inBox && sep.Contains(mp)) {
            dragCol = ci; dragX0 = mp.x; dragW0 = colW[ci]; scrollColsAtDrag = scrollCols;
            BlockWindowDrag = true; e.Use(); return true;
        }
        if (e.type == EventType.MouseDrag && dragCol == ci) {
            colW[ci] = Mathf.Clamp(dragW0 + (mp.x - dragX0) / Mathf.Max(0.0001f, Sc(1f)), 28f, 600f);
            scrollCols = scrollColsAtDrag;
            BlockWindowDrag = true; e.Use(); return true;
        }
        return false;
    }

    // 钉住列（如联机的加入列）命中矩形：填充 pinnedRects / pinnedRows。
    public static void TablePinnedRects(int rowCount, float rowH, float scrollY, float rowsH, float boxY, float headerH,
        float pinX, float pinW, List<Rect> pinnedRects, List<int> pinnedRows) {
        pinnedRects.Clear(); pinnedRows.Clear();
        if (pinW <= 0f) return;
        for (int i = 0; i < rowCount; i++) {
            float y = i * rowH - scrollY;
            if (y > rowsH || y + rowH < 0f) continue;
            float ry = boxY + headerH + y;
            float hy0 = Mathf.Max(ry, boxY + headerH);
            float hy1 = Mathf.Min(ry + rowH, boxY + headerH + rowsH);
            pinnedRects.Add(new Rect(pinX + Sc(2), hy0, pinW - Sc(4), Mathf.Max(0f, hy1 - hy0)));
            pinnedRows.Add(i);
        }
    }

    // 行区命中矩形（与绘制共用同一裁剪规则）：填充 rowRects / rowRows。
    public static void TableRowRects(int rowCount, float rowH, float scrollY, float rowsH, float boxY, float headerH,
        Rect rowsClip, float boxX, float stripW, List<Rect> rowRects, List<int> rowRows) {
        rowRects.Clear(); rowRows.Clear();
        int first = Mathf.Max(0, Mathf.FloorToInt(scrollY / Mathf.Max(1f, rowH)));
        int last = Mathf.Min(rowCount - 1, Mathf.CeilToInt((scrollY + rowsH) / Mathf.Max(1f, rowH)));
        for (int i = first; i <= last; i++) {
            float y = i * rowH - scrollY;
            float ry = boxY + headerH + y;
            float hy0 = Mathf.Max(ry, rowsClip.y);
            float hy1 = Mathf.Min(ry + rowH, rowsClip.yMax);
            rowRects.Add(new Rect(boxX, hy0, stripW, Mathf.Max(0f, hy1 - hy0)));
            rowRows.Add(i);
        }
    }

    // 表格外观：绘制一律读 CurStyle；改下面字段默认值即可换配色。
    public sealed class TableStyle {
        // 配色取自 Dear ImGui 表格主题：表头底 / 强线 / 弱线 / 隔行底。
        public Color HeaderBg = new Color(0.19f, 0.19f, 0.20f, 1f);
        public Color LineStrong = new Color(0.31f, 0.31f, 0.35f, 1f);
        public Color LineLight = new Color(0.23f, 0.23f, 0.25f, 1f);
        public Color RowAltBg = new Color(1f, 1f, 1f, 0.06f);
    }
    private static readonly TableStyle _tableStyle = new TableStyle();
    private static TableStyle CurStyle { get { return _tableStyle; } }

}
}
