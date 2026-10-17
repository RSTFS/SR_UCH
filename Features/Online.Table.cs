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
        private const int HostCol = 6;                 // 房主列靠左显示，其余列居中
        private const int QualityCol = 2;              // 游戏质量列：Shift+单击表头切换技能匹配质量系数
        private const float CodeLabelW = 58f;          // 房间码标签宽度

        // 单击表头：Shift+左键触发列专属切换（排序不再通过点表头切换，避免误触）。
        // 质量列实际由页面自己的 MouseDown 入口切换（见 HandleQualityHeaderMouseDown）；
        // 这里保留核心 click 回调的备用路径：核心在 MouseUp 才派发，且 IMGUI 到那时可能已丢失 Shift，
        // 故用 ShiftHeldNow() 同时看 Event.current.shift 与 Input.GetKey。
        private static void HeaderClick(int ci) {
            try {
                if (!ShiftHeldNow()) {
                    // 点击表头不再切换升/降序（按需求移除）。
                    return;
                }
                if (ci == QualityCol) ToggleQualityCoeff("core click 回调");
            } catch (Exception __ex) { SRApi.Guard.Log("表头点击", __ex); }
        }

        // Shift 状态：MouseUp/Used 事件上的修饰键不可靠，补 Input.GetKey 兜底。
        private static bool ShiftHeldNow() {
            try { if (Event.current != null && Event.current.shift) return true; } catch { }
            try { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); } catch { }
            return false;
        }

        // 切换「游戏质量」列的技能匹配质量系数显示；页面 MouseDown 与核心 click 两条入口共用。
        private static void ToggleQualityCoeff(string from) {
            if (_showQualityCoeff == null) return;
            bool v = !_showQualityCoeff.Value;
            _showQualityCoeff.Value = v;
            SaveEntry(_showQualityCoeff);
            try {
                SRApi.LogInfo("[联机] 游戏质量系数 -> " + (v ? "开（显示 0~1）" : "关（仅 ✓）") + "；入口=" + from);
            } catch { }
            Notes(v ? "游戏质量：显示技能匹配系数（0~1）" : "游戏质量：仅显示 ✓",
                  v ? "Quality: showing the skill-match coefficient (0-1)" : "Quality: marks only");
        }

        // 页面自己的 Shift+左键质量列表头入口：本帧表头几何刚由 TableDraw 算好，直接复算命中，
        // 完全不依赖核心 MouseUp 的 click 派发（根因见文件头对 TableColMove 的说明）。
        private static void HandleQualityHeaderMouseDown(Vector2 mp, bool shift) {
            try {
                if (!shift) return;
                int hit = HeaderColAt(mp);
                // 实机排查用：确认 Shift+左键的按下事件确实进到页面、且命中的是不是质量列。
                try {
                    SRApi.LogInfo("[联机] Shift+左键按下：表头命中列=" + hit + "（质量列=" + QualityCol
                        + "，Event.shift=" + (Event.current != null && Event.current.shift) + "）");
                } catch { }
                if (hit != QualityCol) return;
                // 本次按下由页面处理：清掉核心刚记下的列头拖动状态，避免 MouseUp 再回调一次 click 把开关切回去。
                _tableArgs.moveCol = -1;
                _tableArgs.moveDx = 0f;
                ToggleQualityCoeff("页面 MouseDown");
            } catch (Exception __ex) { SRApi.Guard.Log("联机表头 Shift 点击", __ex); }
        }

        // 表头命中（独立复算，避免依赖 core headerCells 的时序）：返回命中的数据列索引，未命中 -1。
        // 列序/隐藏/横向滚动与核心 DeriveVisibleCols 同一套规则；钉住的操作列不参与。
        private static int HeaderColAt(Vector2 mp) {
            try {
                if (_order == null || _cw == null || _order.Length != ColCount || _cw.Length != ColCount) return -1;
                Rect box = _tableArgs.box;
                float headerH = SRApi.Ctl.Sc(24);
                if (box.width <= 0f || box.height <= 0f) return -1;
                if (mp.y < box.y || mp.y >= box.y + headerH) return -1;
                float x = box.x - _scrollCols.x;
                float stripEnd = box.x + _tableArgs.stripW;
                float sepW = SRApi.Ctl.Sc(4f);   // 右边 4px 是核心的列宽分隔线（拖列宽用），不算点击表头
                for (int i = 0; i < ColCount; i++) {
                    int ci = _order[i];
                    if (ci < 0 || ci >= ColCount || ci == JoinCol || _hiddenCols.Contains(ci)) continue;
                    float w = SRApi.Ctl.Sc(_cw[ci]);
                    if (mp.x >= x && mp.x < x + w - sepW && mp.x >= box.x && mp.x < stripEnd) return ci;
                    x += w;
                }
            } catch { }
            return -1;
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
            buf[3] = ProgressText(li);
            buf[4] = ModeText(li);
            buf[5] = TagText(li);
            buf[6] = string.IsNullOrEmpty(li.LobbyOwner) ? "?" : li.LobbyOwner;
            buf[7] = LimitText(li);
            buf[8] = FlagsText(li);
            buf[CombinedCol] = li.CombinedHealthSkill.ToString(CultureInfo.InvariantCulture);
            buf[JoinCol] = "";
            _cellCache[li] = buf;
            return buf;
        }

        private static readonly List<int> _visScratch = new List<int>(ColCount);   // 交给核心按 order+hidden 填充
        private static int _hiddenColsBeforeTable;                                // 本帧核心处理右键隐藏前的列数

        private static void TableDraw(Rect box) {
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
            float joinW = SRApi.Ctl.Sc(_cw[JoinCol]);
            float stripW = Mathf.Max(SRApi.Ctl.Sc(40), viewW - joinW);     //可横向滚动的列区
            // 可见列由核心 TableDraw 按 order + hidden 自行推导（本页不再维护可见列副本），
            // 这里只按同一规则提前算横向滚动上限。
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
            _tableMaxY = Mathf.Max(0f, contentH - rowsH);
            _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _tableMaxX);
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _tableMaxY);
            if (e == null) return;
            if (e.type == EventType.Repaint) GUI.Box(box, GUIContent.none, SRApi.Ctl.Footer);
            DrawTableRows(box, headerH, rowH, stripW, rowsH, viewW, mp);
        }


        private static void DrawTableRows(Rect box, float headerH, float rowH, float stripW, float rowsH, float viewW, Vector2 mp) {
            // _rowRows 必须与 _rowRectsPage 一起清空：两者是"行矩形 -> _shownLobbies 下标"的一一对应。
            // 表头 / 行命中 / 行绘制统一交给核心入口。
            _tableArgs.box = box; _tableArgs.rowsClip = _rowsClipPage;
            _tableArgs.headerH = headerH; _tableArgs.rowH = rowH; _tableArgs.rowsH = rowsH; _tableArgs.stripW = stripW;
            _tableArgs.colW = _cw; _tableArgs.vis = _visScratch; _tableArgs.rowCount = _shownLobbies.Count;
            _tableArgs.pinCol = JoinCol; _tableArgs.alignLeftCol = HostCol;
            _tableArgs.scrollX = _scrollCols.x; _tableArgs.scrollY = _scrollRows.y; _tableArgs.mp = mp;
            _tableArgs.pinW = _joinW; _tableArgs.pinX = _joinXPage; _tableArgs.drawHeader = true;   // 表头交给核心绘制
            // 核心画表头需要列标题回调，缺失会导致表头空白。
            _tableArgs.title = delegate(int ci) { return SRApi.T(Titles[ci], TitlesEn[ci]); };
            // 表头悬停说明交给核心的 tip 回调（核心负责 inBox/单元格命中并设置 GUI.tooltip）。
            _tableArgs.tip = TipText;
            _tableArgs.pinTitle = SRApi.T(Titles[JoinCol], TitlesEn[JoinCol]);
        // 隐藏列交由核心 TableDraw 处理（右键隐藏 / Shift+右键还原全部）。
        _tableArgs.hidden = _hiddenCols; _tableArgs.hideable = true;
        // 列宽拖动 / 列头换位全部交给核心，本页只提供列定义与回调；拖拽状态由核心持有，本页不镜像。
        _tableArgs.resizable = true;
        _tableArgs.reorderable = true;
        // 把本页的列序源与换位回调注入核心。
        _tableArgs.order = _order;
        _tableArgs.applyOrder = delegate(int[] o) { _order = o; SaveCols(); };
            // Shift+右键 = 还原全部：接到本表自己的还原例程 ResetTable。
            _tableArgs.resetAll = delegate { ResetTable(); };
        _tableArgs.click = HeaderClickOnce;
            _tableArgs.rowCells = delegate(int r) { return CellTexts(_shownLobbies[r]); };
            _tableArgs.rowAlpha = delegate(int r) { return IsJoinable(_shownLobbies[r]) ? 1f : 0.55f; };
            _tableArgs.headerCells = _headerCellsPage; _tableArgs.headerCols = _headerCols;
            _tableArgs.rowRects = _rowRectsPage; _tableArgs.rowRows = _rowRows;
            _tableArgs.pinRects = _joinRectsPage; _tableArgs.pinRows = _joinRows;
            // 钉住列按钮：位置/裁剪由核心算好，本页只负责画按钮内容。
            _tableArgs.pinDraw = delegate(int row, Rect r, int visible) { DrawJoinButton(row, r, mp); };
            _tableArgs.emptyHint = delegate { return SRApi.T("（没有符合筛选的房间）", "(no lobbies match the filters)"); };
            _hiddenColsBeforeTable = _hiddenCols.Count;
            SRApi.TableDraw(_tableArgs);
            // 收回核心算出的钉住列表头矩形，供下方命中判定显示"操作"列 tooltip。
            _joinHeaderPage = _tableArgs.pinHeader;
            // 空提示由核心的 emptyHint 在行区裁剪内绘制。
            if (_joinW > 0f) SRApi.DrawLine(new Rect(_joinXPage - 1f, box.y + headerH, 1f, rowsH), SRApi.TableLineStrong);   // 钉住列分隔线
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

        // 钉住列的加入按钮：核心 pinDraw 已给出裁剪后的矩形，这里只画按钮与悬停/按下态。
        private static void DrawJoinButton(int row, Rect r, Vector2 mp) {
            try {
                if (row < 0 || row >= _shownLobbies.Count) return;
                Matchmaker.LobbyListInfo li = _shownLobbies[row];
                bool can = IsJoinable(li);
                bool hov = can && r.Contains(mp);
                // 与旧版一样把按钮限制在行区裁剪框内（最后一行只露出一半时不越界画到页脚）。
                float y0 = r.y + SRApi.Ctl.Sc(1);
                float y1 = Mathf.Min(r.y + r.height, _rowsClipPage.yMax - SRApi.Ctl.Sc(1));
                float bh = Mathf.Max(0f, y1 - y0);
                if (bh <= 0f) return;
                Color pc = GUI.color;
                if (!can) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                // 窄格子按钮：用内边距小的样式，避免文字被裁掉。
                SRApi.Ctl.BtnClip.Draw(new Rect(r.x, y0, r.width, bh),
                    new GUIContent(JoinText(li)), hov, hov && _joinDownRow == row, false, false);
                GUI.color = pc;
            } catch (Exception __ex) { SRApi.Guard.Log("联机表格加入按钮", __ex); }
        }
        // 表头说明：由核心 TableDraw 在命中表头单元格时回调（核心负责 inBox / 单元格命中 / GUI.tooltip）。
        // 分隔线悬停与滚动条拖动时不显示，保持与旧版一致的观感。
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

        // 表头分隔线（可拖列宽）当前是否被悬停：用于抑制 core tip 回调（旧版同样在分隔线上不弹说明）。
        private static bool HoveringSeparatorNow(Vector2 mp) {
            try {
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    Rect cell = _headerCellsPage[k];
                    Rect sep = new Rect(cell.xMax - SRApi.Ctl.Sc(3), cell.y, SRApi.Ctl.Sc(7), cell.height);
                    if (sep.Contains(mp)) return true;
                }
            } catch { }
            return false;
        }

        private static void TableInput(Rect box, Event e) {
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
            // 光标：调整光标（分隔线悬停 / 拖列宽）由核心 TableDraw 统一置位，本页只补拖拽期间与移动光标。
            if (_tableArgs.dragCol >= 0) SRApi.CursorResize = true;
            bool inBox = box.Contains(mp);   // 横向滚出的列会落到框外，故把判定收回框内
            bool hoverCell = false;
            if (inBox) {
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (_headerCellsPage[k].Contains(mp)) { hoverCell = true; break; }
                }
            }
            SRApi.CursorMove = hoverCell || _tableArgs.moveCol >= 0;
            // 表头说明已交给核心 tip 回调；核心没有钉住列的 tip，这里只补"操作"列表头。
            if (inBox && !_sbDragging && _joinHeaderPage.width > 0f && _joinHeaderPage.Contains(mp)) {
                GUI.tooltip = SRApi.T(TitleTips[JoinCol], TitleTipsEn[JoinCol]);
            }
            // 右键：表头 = 隐藏该列（核心已处理，本页只负责落盘与提示）；Shift+右键 = 还原全部（核心清列并回调
            // resetAll）；房间行 = 隐藏该房间（本功能自己的状态，无核心 API）。
            if (e.type == EventType.MouseDown && e.button == 1 && inBox) {
                if (e.shift) return;   // 核心 TableDraw 已清空隐藏列并回调 resetAll
                int hitCol = -1;
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (!_headerCellsPage[k].Contains(mp)) continue;
                    hitCol = _headerCols[k];
                    break;
                }
                if (hitCol >= 0) {
                    // 隐藏动作由核心 TableDraw 完成；确实新增了隐藏列才落盘与提示。
                    if (_hiddenCols.Contains(hitCol) && _hiddenCols.Count > _hiddenColsBeforeTable) {
                        SaveCols();
                        string nm = SRApi.T(Titles[hitCol], TitlesEn[hitCol]);
                        // SRApi.T(zh, en) 两个实参都会求值：两参相同时先拼一次再传。
                        string hiddenColMsg = SRApi.T("已隐藏列：", "Hidden column: ") + nm;
                        Notes(hiddenColMsg, hiddenColMsg);
                    }
                } else {
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
