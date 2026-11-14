// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;


namespace SR_UCH {
    public static partial class SRApi {
        public sealed class TableAction {
            public string Label;      // 按钮文字
            public string Tip;        // 悬浮提示（可空）
            public bool Enabled = true;
            public System.Action OnClick;
        }
                    public sealed partial class TableArgs {
            public Rect box;                 //表格外框（屏幕坐标）
            public Rect rowsClip;            //行区裁剪矩形（命中和绘制共用）
            public float headerH, rowH, rowsH, stripW;
            public float[] colW;
            public List<int> vis;            //可见列顺序
            public int rowCount, pinCol = -1, alignLeftCol = -1, moveCol = -1;
            public float scrollX, scrollY;
            public Vector2 mp;
            public float pinW, pinX;
            public string pinTitle;
            public System.Func<int, string> title;          //列标题(列下标)
            public System.Func<int, string> tip;            //列头悬停提示(可选)
            public System.Func<int, string[]> rowCells;     //整行文本（行号 -> 各列文本）
            public System.Func<int, float> rowAlpha;       //行透明度（可选；不可用的行压暗）
            public bool drawHeader = true;                 //是否画表头（表头另外画时置 false）
            // 竖向排列：格子沿 Y 叠放（每项一行），默认 false。
            public bool vertical;
            public float[] defaultColW; // 默认列宽（Shift+右键还原时核心会还原成它）
            public System.Func<int, bool> rowSelected; // 某行是否“选中”（核心据此画整行高亮）
            public bool headerLeft; // 表头文字靠左（默认居中）
            public bool resizable;                         //表头分隔线可拖动改列宽（与房间列表同款）
            public int dragCol = -1;                       //列宽拖动状态（由参数对象自己持有，跨帧保持）
            public float dragX0, dragW0;
            public Vector2 scrollColsTmp, scrollColsAtDragTmp;
            public bool reorderable;                       //表头可拖动换列位
            public int[] order;                            //显示顺序（数据列下标）
            public System.Action<int[]> applyOrder;        //换位回调（调用方保存新顺序）
            public System.Action saveOrder;                //换位后落盘（可选）
            public System.Action saveWidths;               //列宽拖动松开时落盘（可选；核心自己处理 MouseUp 清状态）
            public System.Action<int> click;               //单击列头（可选）
            public System.Action resetAll;                 //Shift+右键=还原全部（可选）
            // 钉住列按钮与空列表提示也由本控件负责绘制：三处列表只提供内容。
            public System.Action<int, Rect, int> pinDraw;  //(数据行下标, 钉住列绝对矩形, 可见序号)
            public System.Func<string> emptyHint;          //无行时的居中提示（返回 null/空 = 不画）
            public float moveX0, moveDx;
            public List<int> allCols = new List<int>();
            public System.Collections.Generic.HashSet<int> hidden; //被隐藏的列（右键表头隐藏，Shift+右键恢复）
            public bool hideable;                                  //是否允许右键隐藏列 / 显示+N恢复按钮
            public int sortCol = -1;                               //当前排序列（仅用于给列头画标记）
            public bool sortAsc = true;
            public List<Rect> headerCells = new List<Rect>();
            public List<int> headerCols = new List<int>();
            public List<Rect> rowRects = new List<Rect>();
            public List<int> rowRows = new List<int>();
            public List<Rect> pinRects = new List<Rect>();
            public List<int> pinRows = new List<int>();
            public Rect pinHeader;
            // 行交互：核心每帧用 rowRects 做命中并回填，调用方只读。
            public int rowHover = -1;                       //鼠标命中的数据行号；-1 = 没有
            public System.Action<int> rowClick;             //左键 MouseUp 命中非钉住列区域时回调行号
            public System.Action<int> rowDoubleClick;
            public System.Action<int> rowRightClick;   // 右键单击行（MouseUp 右键，仅在行上）
            public System.Action<int> headerShiftClick; // Shift+左键单击表头（参数=数据列下标）
            public float scrollbarAlpha;   // 滚动条淡入淡出透明度（核心维护）       //0.35 秒内同一行左键点击两次时回调
            public bool hoverHighlight = true;              //rowHover >= 0 时核心在行矩形上叠一层很淡的高亮
            }
    public sealed partial class TableArgs { 
        // 带默认值的构造入口 + 必填校验：表格页少写样板，漏填（表头 title / 行回调等）能被自检/日志抓到。
        public static TableArgs Create(Rect box, float[] colW, List<int> vis, int rowCount,
                                       System.Func<int, string> title, System.Func<int, string[]> rowCells) {
            TableArgs a = new TableArgs();
            a.box = box; a.rowsClip = box;   // 页面会用真实表头/行区覆盖；这里给合法初值
            a.colW = colW; a.vis = vis; a.rowCount = rowCount;
            a.title = title; a.rowCells = rowCells;
            a.hidden = new HashSet<int>();
            a.headerCells = new List<Rect>(); a.headerCols = new List<int>();
            a.rowRects = new List<Rect>(); a.rowRows = new List<int>();
            a.pinRects = new List<Rect>(); a.pinRows = new List<int>();
            a.drawHeader = true; a.resizable = true; a.reorderable = true; a.hideable = true;
            a.sortCol = -1; a.sortAsc = true; a.hoverHighlight = true;
            return a;
        }
        // 返回参数问题列表（空 = 合法）。核心 TableDraw 每帧调用一次并按问题签名去重记日志。
        public string[] Validate() {
            List<string> p = new List<string>();
            try {
                if (box.width <= 1f || box.height <= 1f) p.Add("box 尺寸无效");
                if (colW == null || colW.Length == 0) p.Add("colW 为空");
                if (vis == null) p.Add("vis 为空");
                if (rowCount < 0) p.Add("rowCount < 0");
                if (!vertical && rowsClip.height <= 0f && rowCount > 0) p.Add("rowsClip 高度无效");
                if (drawHeader && title == null) p.Add("需要表头但缺少 title 回调");
                if (rowCells == null && rowCount > 0) p.Add("有行但缺少 rowCells 回调");
                if (pinCol >= 0 && (colW == null || pinCol >= colW.Length)) p.Add("pinCol 越界");
                if (order != null && colW != null && order.Length != colW.Length) p.Add("order 长度与 colW 不一致");
            } catch (Exception) { p.Add("Validate 自身异常"); }
            return p.ToArray();
        }
    }
    public sealed class TableState {
        private readonly BepInEx.Configuration.ConfigEntry<string> _w, _o, _h;
        private readonly float[] _defaultW;
        private readonly int _count;
        public int[] Order;
        public readonly HashSet<int> Hidden = new HashSet<int>();
        public float[] Widths;

        public TableState(BepInEx.Configuration.ConfigEntry<string> widths, BepInEx.Configuration.ConfigEntry<string> order,
                          BepInEx.Configuration.ConfigEntry<string> hidden, float[] defaults) {
            _w = widths; _o = order; _h = hidden;
            _count = defaults != null ? defaults.Length : 0;
            _defaultW = defaults != null ? (float[])defaults.Clone() : new float[0];
            Widths = (float[])_defaultW.Clone();
            Order = new int[_count];
            for (int i = 0; i < _count; i++) Order[i] = i;
            Load();
        }

        public void Load() {
            try {
                if (_w != null && !string.IsNullOrEmpty(_w.Value)) {
                    string[] parts = _w.Value.Split(',');
                    for (int i = 0; i < parts.Length && i < Widths.Length; i++) {
                        float v;
                        if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0f) Widths[i] = v;
                    }
                }
                if (_o != null && !string.IsNullOrEmpty(_o.Value)) {
                    string[] parts = _o.Value.Split(',');
                    List<int> ord = new List<int>();
                    for (int i = 0; i < parts.Length; i++) {
                        int v;
                        if (int.TryParse(parts[i].Trim(), out v) && v >= 0 && v < _count && !ord.Contains(v)) ord.Add(v);
                    }
                    for (int k = 0; k < _count; k++) if (!ord.Contains(k)) ord.Add(k);
                    if (ord.Count == _count) Order = ord.ToArray();
                }
                if (_h != null && !string.IsNullOrEmpty(_h.Value)) {
                    Hidden.Clear();
                    foreach (string s in _h.Value.Split(',')) {
                        int v;
                        if (int.TryParse(s.Trim(), out v) && v >= 0 && v < _count) Hidden.Add(v);
                    }
                    if (Hidden.Count >= _count) { Hidden.Clear(); }   // 至少留一列
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        public void Save() {
            try {
                if (_w != null && Widths != null) {
                    string[] p = new string[Widths.Length];
                    for (int i = 0; i < Widths.Length; i++) p[i] = Mathf.RoundToInt(Widths[i]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _w.Value = string.Join(",", p);
                }
                if (_o != null && Order != null) {
                    string[] p = new string[Order.Length];
                    for (int i = 0; i < Order.Length; i++) p[i] = Order[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _o.Value = string.Join(",", p);
                }
                if (_h != null) {
                    List<int> hs = new List<int>(Hidden);
                    hs.Sort();
                    string[] p = new string[hs.Count];
                    for (int i = 0; i < hs.Count; i++) p[i] = hs[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _h.Value = string.Join(",", p);
                }
                BepInEx.Configuration.ConfigFile f = _w != null ? _w.ConfigFile : (_o != null ? _o.ConfigFile : null);
                if (f != null) f.Save();
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        // 还原：列宽回默认、列序回自然序、隐藏清空。
        public void ResetAll() {
            for (int i = 0; i < Widths.Length && i < _defaultW.Length; i++) Widths[i] = _defaultW[i];
            for (int i = 0; i < Order.Length; i++) Order[i] = i;
            Hidden.Clear();
            Save();
        }
    }
        public sealed class TableSource<T> where T : class {
            private readonly List<T> _rows = new List<T>();
            private readonly List<T> _buf = new List<T>();
            public Func<T, bool> Filter;          // null = 全部通过
            public Comparison<T> Sort;            // null = 保持源顺序
            public long Stamp { get; private set; }   // 上次重算用的版本戳
            public List<T> Rows { get { return _rows; } }

            public List<T> Refresh(IList<T> source, long stamp) {
                if (Stamp == stamp) return _rows;
                Stamp = stamp;
                _rows.Clear();
                if (source == null) return _rows;
                _buf.Clear();
                for (int i = 0; i < source.Count; i++) {
                    T v = source[i];
                    if (v == null) continue;
                    if (Filter != null) { try { if (!Filter(v)) continue; } catch { continue; } }
                    _buf.Add(v);
                }
                if (Sort != null) { try { _buf.Sort(Sort); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
                _rows.AddRange(_buf);
                return _rows;
            }

            public void Invalidate() { Stamp = long.MinValue; }
        }
        public sealed class TableColumn {
            public string TitleZh, TitleEn, TipZh, TipEn;
            public float MinW = 40f;      // 设计单位下限
            public float PadW = 22f;      // 在文字宽度上追加的余量
            public bool AlignLeft;        // 是否靠左
        }
    }
}
