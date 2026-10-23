// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;

namespace SR_UCH {
    //功能统一契约：实现本接口即被 MainPlugin 反射发现并调用 Initialize。
    public interface ITweak {
        void Initialize(IFeatureHost host);
    }

    //功能初始化时注入的宿主：Config 读写自身配置，Logger 写 BepInEx 日志。
    //不含 Harmony —— 功能自行 new Harmony(id)。
    public interface IFeatureHost {
        ConfigFile Config { get; }
        ManualLogSource Logger { get; }
    }

    // 外部语言包契约：一个 DLL = 一门语言。类库引用 SR_UCH.dll 实现本接口，DLL 放入 BepInEx\plugins\。
    // SR 启动时扫描 plugins 目录注册实现类，界面语言下拉框随即增加选项（写入 cfg 持久保存）。
    public interface ILanguagePack {
        //稳定 id（写入 cfg，如 "fr"），不要使用显示名。
        string Id { get; }
        //下拉框显示名（如 "Français"）。
        string DisplayName { get; }
        //未收录文案的回退语言："English"（默认）或 "中文"。
        string FallbackId { get; }
        //词条表：key = SR 源码中文原文（也接受英文原名 / 枚举名 / 场景名 / KeyCode 名），value = 译文；
        //未收录的 key 按 FallbackId 回退，可增量补全。
        IDictionary<string, string> Map { get; }
    }
    // 槽位总线：功能间通信只认 "名字 + 委托签名"，不认识双方类型；Require 返回 null 表示无人提供，调用方须自行降级。
    // Provide 为覆盖语义；硬约定：反射游戏私有成员须走 SR.RefField / SR.RefMethod 并经 SR.RefOverride 登记。
    public static class Slots {
        private static readonly Dictionary<string, object> _map = new Dictionary<string, object>();
        //记录无人提供或类型不符的槽位名（最多 64 条，只记不同的名字），供自检界面显示。
        private static readonly Dictionary<string, int> _misses = new Dictionary<string, int>();
        public static int MissCount { get { return _misses.Count; } }
        public static ICollection<string> MissNames { get { return _misses.Keys; } }

        private static void NoteMiss(string slot) {
            try {
                if (_misses.Count >= 64 && !_misses.ContainsKey(slot)) return;
                int c;
                _misses.TryGetValue(slot, out c);
                _misses[slot] = c + 1;
            } catch { }
        }

        public static void Provide(string slot, object impl) {
            if (impl != null && !string.IsNullOrEmpty(slot)) { _map[slot] = impl; _misses.Remove(slot); }
        }
        public static T Require<T>(string slot) where T : class {
            if (string.IsNullOrEmpty(slot)) return null;
            object o;
            if (_map.TryGetValue(slot, out o)) {
                T typed = o as T;
                if (typed != null) return typed;
                NoteMiss(slot + "（类型不符）");   //已登记但类型不符
                return null;
            }
            NoteMiss(slot);
            return null;
        }
        //自检用：当前登记的槽位数量
        public static int Count { get { return _map.Count; } }
        public static ICollection<string> Names { get { return _map.Keys; } }
    }

    // 契约版本（对应 SMAPI 的 minimumApiVersion）：核心只增不改地推进这个数字；
    // 功能用 [SrFeature] 的 MinContract 声明所需版本，核心低于它时记录"契约不兼容"。
    public static class SRContract {
        public const int Version = 2;
    }

    // 功能清单（对应 SMAPI 的 manifest.json）：写在 ITweak 实现类上。
    // 核心据此生成功能表、检查依赖与槽位冲突、排序侧栏；未声明的旧式功能用类型全名兜底。
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SrFeatureAttribute : Attribute {
        public string Id { get; private set; }        //稳定 id（日志/依赖用），如 "sr.chat"
        public string Name { get; private set; }      //显示名
        public string Version { get; private set; }   //功能自身版本，如 "1.0"
        public int Rank { get; set; }                 //侧栏顺序建议值（越小越前）
        public string[] DependsOn { get; set; }       //依赖的功能 id；缺失时记录并降级运行
        public string[] Provides { get; set; }        //本功能会写入的槽位名（冲突检测用）
        public int MinContract { get; set; }          //所需最低契约版本
        public SrFeatureAttribute(string id, string name, string version) {
            Id = id; Name = name; Version = version; Rank = 120; MinContract = SRContract.Version;
        }
    }

    // 一条功能清单记录：核心在扫描 ITweak 时填充，界面/日志只读。
    public sealed class FeatureInfo {
        public string Id, Name, Version;
        public int Rank, MinContract;
        public string[] DependsOn, Provides;
        public Type Impl;
        public bool ManifestMissing;                  // true = 该类没有 [SrFeature]（旧式/第三方未声明）
        public bool ContractTooNew;                   // true = MinContract > 当前契约版本
        public readonly List<string> MissingDeps = new List<string>();
        public override string ToString() {
            string s = Id + " " + Version;
            if (ManifestMissing) s += "（未声明清单）";
            if (ContractTooNew) s += "（要求契约 v" + MinContract + "，当前 v" + SRContract.Version + "）";
            if (MissingDeps.Count > 0) s += "（缺依赖: " + string.Join(", ", MissingDeps.ToArray()) + "）";
            return s;
        }
    }

    // 功能注册表（SMAPI 的 mod registry 对应物）：核心写，自检页/日志读。
    public static class Features {
        private static readonly List<FeatureInfo> _all = new List<FeatureInfo>();
        private static readonly Dictionary<string, FeatureInfo> _byId = new Dictionary<string, FeatureInfo>();
        public static IList<FeatureInfo> All { get { return _all; } }
        public static int Count { get { return _all.Count; } }
        public static int ManifestMissingCount { get { int n = 0; for (int i = 0; i < _all.Count; i++) if (_all[i].ManifestMissing) n++; return n; } }
        public static int ProblemCount { get { int n = 0; for (int i = 0; i < _all.Count; i++) if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0) n++; return n; } }
        public static FeatureInfo Find(string id) {
            if (string.IsNullOrEmpty(id)) return null;
            FeatureInfo fi; return _byId.TryGetValue(id, out fi) ? fi : null;
        }
        // 登记一条（id 冲突时用"id#类型名"去重，避免同一 id 被两个类占用而覆盖）
        public static void Register(FeatureInfo fi) {
            if (fi == null || string.IsNullOrEmpty(fi.Id)) return;
            if (_byId.ContainsKey(fi.Id)) fi.Id = fi.Id + "#" + (fi.Impl != null ? fi.Impl.Name : "?");
            _all.Add(fi); _byId[fi.Id] = fi;
        }
        // 从类型读清单；没有 [SrFeature] 时用类型全名兜底并标记 ManifestMissing
        public static FeatureInfo FromType(Type t) {
            if (t == null) return null;
            FeatureInfo fi = new FeatureInfo { Id = t.FullName, Name = t.Name, Version = "0", Rank = 120, MinContract = 0, Impl = t, ManifestMissing = true };
            try {
                object[] attrs = t.GetCustomAttributes(typeof(SrFeatureAttribute), false);
                if (attrs != null && attrs.Length > 0) {
                    SrFeatureAttribute a = (SrFeatureAttribute)attrs[0];
                    fi.Id = a.Id; fi.Name = a.Name; fi.Version = a.Version; fi.Rank = a.Rank;
                    fi.MinContract = a.MinContract; fi.DependsOn = a.DependsOn; fi.Provides = a.Provides;
                    fi.ManifestMissing = false;
                }
            } catch { }
            fi.ContractTooNew = fi.MinContract > SRContract.Version;
            return fi;
        }
        // 扫描所有已加载程序集里的 ITweak 实现（内置 + 外部 + EX），重建功能表并解析依赖
        public static void Rebuild() {
            _all.Clear(); _byId.Clear();
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++) {
                Type[] types;
                try { types = asms[i].GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                for (int j = 0; j < types.Length; j++) {
                    Type t = types[j];
                    if (t == null || t.IsInterface || t.IsAbstract) continue;
                    try { if (!typeof(ITweak).IsAssignableFrom(t)) continue; } catch { continue; }
                    Register(FromType(t));
                }
            }
            for (int i = 0; i < _all.Count; i++) {
                FeatureInfo fi = _all[i]; fi.MissingDeps.Clear();
                if (fi.DependsOn == null) continue;
                for (int k = 0; k < fi.DependsOn.Length; k++)
                    if (!string.IsNullOrEmpty(fi.DependsOn[k]) && !_byId.ContainsKey(fi.DependsOn[k])) fi.MissingDeps.Add(fi.DependsOn[k]);
            }
        }
        // 一行摘要：合同 vN / 功能 M 个 / 问题 K 条，问题明细跟在后面
        public static string Describe() {
            string head = "[SR.Feat] 契约 v" + SRContract.Version + " · 功能 " + _all.Count + " 个";
            if (ManifestMissingCount > 0) head += " · 未声明清单 " + ManifestMissingCount + " 个";
            if (ProblemCount > 0) head += " · 有问题 " + ProblemCount + " 个";
            string detail = "";
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0 || _all[i].ManifestMissing)
                    detail += "\n    - " + _all[i].ToString();
            return head + detail;
        }
    }
    // API 门面（SMAPI 的 SMAPI.Mod* 助手对应物）：功能层只允许用这里的成员。
    // 契约层不引用实现层，日志出口由核心在启动时注入（SRApi.SetLogSinks）。
    public static class SRApi {
        private static Action<string> _info, _warn, _error;
        private static Action<string> _notify;
        // 核心启动时注入：把契约层的日志/提示接到实现（无注入时静默丢弃，不影响功能）。
        public static void SetLogSinks(Action<string> info, Action<string> warn, Action<string> error) {
            _info = info; _warn = warn; _error = error;
        }
        public static void SetNotifySink(Action<string> notify) { _notify = notify; }
        internal static void Warn(string msg) { try { Action<string> w = _warn; if (w != null) w(msg); } catch { } }
        private static string Str(object m) { try { return m == null ? "" : m.ToString(); } catch { return "<ToString 失败>"; } }
        public static void LogInfo(object m) { try { Action<string> a = _info; if (a != null) a(Str(m)); } catch { } }
        public static void LogWarn(object m) { try { Action<string> a = _warn; if (a != null) a(Str(m)); } catch { } }
        public static void LogError(object m) { try { Action<string> a = _error; if (a != null) a(Str(m)); } catch { } }
        // 用户提示（右下角/系统消息）：实现层注入 ShowUserMessage（同时会写日志）。
        public static void Notify(string text) { try { Action<string> n = _notify; if (n != null) n(text ?? ""); } catch { } }
        public static void Notify(string text, params object[] args) {
            if (args == null || args.Length == 0) { Notify(text); return; }
            try { Notify(string.Format(text, args)); } catch { Notify(text); }
        }

        // 组合键修饰符（与核心 ComboMod 数值一致，下面枚举体直接从核心源码镜像）
        [Flags] public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static Func<ConfigEntryBase, int> _keyComboMod;
        private static Action<ConfigEntryBase, int> _setDefaultCombo;
        private static Func<string, int, GUIStyle, GUILayoutOption[], string> _clearableTextField;
        private static Action<string, Action> _registerPageSimple;
        private static Action<string, string, string, string, int, Action> _registerPageFull;
        public static string MsgMasterOff { get { return T("本 Mod 总开关已关闭", "The mod's master switch is off"); } }
        public static string MsgNotLobby { get { return T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player"); } }
        public static void SetMiscSinks(Func<ConfigEntryBase, int> keyComboMod, Action<ConfigEntryBase, int> setDefaultCombo,
                                        Func<string, int, GUIStyle, GUILayoutOption[], string> clearableTextField,
                                        Action<string, Action> registerPageSimple,
                                        Action<string, string, string, string, int, Action> registerPageFull) {
            _keyComboMod = keyComboMod; _setDefaultCombo = setDefaultCombo; _clearableTextField = clearableTextField;
            _registerPageSimple = registerPageSimple; _registerPageFull = registerPageFull;
        }
        public static ComboMod KeyComboMod(ConfigEntryBase entry) { Func<ConfigEntryBase, int> f = _keyComboMod; return f != null ? (ComboMod)f(entry) : ComboMod.None; }
        public static void SetDefaultCombo(ConfigEntryBase entry, ComboMod mod) { Action<ConfigEntryBase, int> f = _setDefaultCombo; if (f != null) f(entry, (int)mod); }
        public static string ClearableTextField(string text, GUIStyle style, params GUILayoutOption[] options) { Func<string, int, GUIStyle, GUILayoutOption[], string> f = _clearableTextField; return f != null ? f(text, -1, style, options) : text; }
        public static string ClearableTextField(string text, int maxLength, GUIStyle style, params GUILayoutOption[] options) { Func<string, int, GUIStyle, GUILayoutOption[], string> f = _clearableTextField; return f != null ? f(text, maxLength, style, options) : text; }
        // 底栏按钮扩展点：核心在「设置」按钮右侧依次绘制；功能可注册自己的入口（如配置工具）。
        public sealed class FooterButton {
            public string Id, LabelZh, LabelEn; public Action OnClick;
            public FooterButton(string id, string zh, string en, Action onClick) { Id = id; LabelZh = zh; LabelEn = en; OnClick = onClick; }
        }
        public static readonly List<FooterButton> FooterButtons = new List<FooterButton>();
        public static void RegisterFooterButton(string id, string zh, string en, Action onClick) {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < FooterButtons.Count; i++) {
                if (FooterButtons[i].Id == id) { FooterButtons[i].LabelZh = zh; FooterButtons[i].LabelEn = en; FooterButtons[i].OnClick = onClick; return; }
            }
            FooterButtons.Add(new FooterButton(id, zh, en, onClick));
        }

        public static void RegisterPage(string section, Action render) { Action<string, Action> f = _registerPageSimple; if (f != null) f(section, render); }
        public static void RegisterPage(string section, string pageId, string nameZh, string nameEn, int order, Action render) { Action<string, string, string, string, int, Action> f = _registerPageFull; if (f != null) f(section, pageId, nameZh, nameEn, order, render); }

        // P2：EX 模块所需的其余 API（委托注入，行为不变）
        private static Func<bool> _allEnabled, _ignoreModeLimit;
        private static Action<bool> _setAllEnabled;
        private static Func<string, bool> _hasPage;
        private static Action<string, string> _notifyTag;
        private static Func<string, object[], string> _cat;
        private static Func<string, string> _enumNameZh;
        private static Func<float> _controlHeight;
        private static Action<float> _setControlHeight;
        private static Action<Func<bool>> _modeOverride, _hostOverride, _pauseProvider;
        private static Action<string, Action> _tabPrefix;
        public static void SetExSinks(Func<bool> allEnabled, Action<bool> setAllEnabled, Func<bool> ignoreModeLimit,
                                      Func<string, bool> hasPage, Action<string, string> notifyTag,
                                      Func<string, object[], string> cat, Func<string, string> enumNameZh,
                                      Func<float> controlHeight, Action<float> setControlHeight,
                                      Action<Func<bool>> modeOverride, Action<Func<bool>> hostOverride,
                                      Action<Func<bool>> pauseProvider, Action<string, Action> tabPrefix) {
            _allEnabled = allEnabled; _setAllEnabled = setAllEnabled; _ignoreModeLimit = ignoreModeLimit;
            _hasPage = hasPage; _notifyTag = notifyTag; _cat = cat; _enumNameZh = enumNameZh;
            _controlHeight = controlHeight; _setControlHeight = setControlHeight;
            _modeOverride = modeOverride; _hostOverride = hostOverride; _pauseProvider = pauseProvider; _tabPrefix = tabPrefix;
        }
        public static bool AllEnabled { get { Func<bool> f = _allEnabled; return f != null && f(); } set { Action<bool> a = _setAllEnabled; if (a != null) a(value); } }
        public static bool IgnoreModeLimit { get { Func<bool> f = _ignoreModeLimit; return f != null && f(); } }
        public static bool HasPage(string section) { Func<string, bool> f = _hasPage; return f != null && f(section); }
        public static void NotifyTag(string tag, string text) { Action<string, string> f = _notifyTag; if (f != null) f(tag, text); }
        public static string Cat(string text, object[] args) { Func<string, object[], string> f = _cat; return f != null ? f(text, args) : text; }
        public static string EnumNameZh(string n) { Func<string, string> f = _enumNameZh; return f != null ? f(n) : n; }
        public static float ControlHeight { get { Func<float> f = _controlHeight; return f != null ? f() : 0f; } set { Action<float> a = _setControlHeight; if (a != null) a(value); } }
        public static void RegisterModeOverride(Func<bool> f2) { Action<Func<bool>> f = _modeOverride; if (f != null) f(f2); }
        public static void RegisterHostOverride(Func<bool> f2) { Action<Func<bool>> f = _hostOverride; if (f != null) f(f2); }
        public static void RegisterPauseProvider(Func<bool> f2) { Action<Func<bool>> f = _pauseProvider; if (f != null) f(f2); }
        public static void RegisterTabPrefix(string section, Action draw) { Action<string, Action> f = _tabPrefix; if (f != null) f(section, draw); }

        // 迁移规则门面：规则注册表与 cfg 改写都在核心，契约层只转发。
        public static class Migration {
            private static Action<string, string[]> _remove;
            private static Action<string, string, string> _move;
            public static void SetSinks(Action<string, string[]> remove, Action<string, string, string> move) { _remove = remove; _move = move; }
            // 从 cfg 清理指定段下的一批死键（幂等）
            public static void Remove(string section, params string[] keys) { Action<string, string[]> f = _remove; if (f != null) f(section, keys); }
            // 把某个键从一段搬到另一段
            public static void Move(string fromSection, string toSection, string key) { Action<string, string, string> f = _move; if (f != null) f(fromSection, toSection, key); }
        }
        // 表格控件门面：TableArgs 是纯数据参数对象（与核心同字段），4 个交互函数委托注入。
        public delegate void TableDragWatchdogFn(Event e, ref float watchdog, ref bool sbDragging, ref int moveCol,
            ref float moveDx, ref int dragCol, ref bool dragH, Action saveWidths, Action saveTableH, Action resetJoinDown);
        public delegate bool TableWheelInputFn(Event e, Rect box, Vector2 mp, float maxX, float maxY,
            ref Vector2 scrollCols, ref Vector2 scrollRows);
        public delegate bool TableScrollbarInputFn(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax,
            ref bool dragging, ref float grabY, ref float scrollY);
        public delegate float TableAutoHeightFn(float usedAbove, float minH);
        private static Action<TableArgs> _tableDraw;
        private static TableDragWatchdogFn _tableDragWatchdog;
        private static TableWheelInputFn _tableWheelInput;
        private static TableScrollbarInputFn _tableScrollbarInput;
        // 存成 object 而非委托字段：本注入发生在 SR.Initialize（早于功能渲染），
        // 但晚于 MainPlugin 启动期的 SRApi.MissingSinks() 自检；用委托字段会被误报为"未注入"。
        private static object _tableAutoHeight;
        public static void SetTableSinks(Action<TableArgs> tableDraw, TableDragWatchdogFn tableDragWatchdog,
                                         TableWheelInputFn tableWheelInput, TableScrollbarInputFn tableScrollbarInput) {
            _tableDraw = tableDraw; _tableDragWatchdog = tableDragWatchdog;
            _tableWheelInput = tableWheelInput; _tableScrollbarInput = tableScrollbarInput;
        }
        // 表格自适应高度助手：页面顶部已用高度 + 表格高度 = 底栏之上可用高度。
        // 公式（标题栏 26 + 内边距 2 + 底栏 + 行区上下留白 4）由实现层注入；未注入时按同一语义兜底。
        public static void SetTableAutoHeightSink(TableAutoHeightFn tableAutoHeight) { _tableAutoHeight = tableAutoHeight; }
        public static float TableAutoHeight(float usedAbove, float minH) {
            TableAutoHeightFn f = _tableAutoHeight as TableAutoHeightFn;
            if (f != null) return f(usedAbove, minH);
            return Mathf.Max(minH, ContentArea.height - Ctl.Sc(26f) - Ctl.Sc(2f) - FooterHeight - usedAbove - Ctl.Sc(4f));
        }
                    public sealed class TableArgs {
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
            public System.Action<int> rowDoubleClick;       //0.35 秒内同一行左键点击两次时回调
            public bool hoverHighlight = true;              //rowHover >= 0 时核心在行矩形上叠一层很淡的高亮
            }
        public static void TableDraw(TableArgs a) { Action<TableArgs> f = _tableDraw; if (f != null) f(a); }
        public static void TableDragWatchdog(Event e, ref float watchdog, ref bool sbDragging, ref int moveCol,
            ref float moveDx, ref int dragCol, ref bool dragH, Action saveWidths, Action saveTableH, Action resetJoinDown) {
            TableDragWatchdogFn f = _tableDragWatchdog;
            if (f != null) f(e, ref watchdog, ref sbDragging, ref moveCol, ref moveDx, ref dragCol, ref dragH, saveWidths, saveTableH, resetJoinDown);
        }
        public static bool TableWheelInput(Event e, Rect box, Vector2 mp, float maxX, float maxY, ref Vector2 scrollCols, ref Vector2 scrollRows) {
            TableWheelInputFn f = _tableWheelInput;
            return f != null && f(e, box, mp, maxX, maxY, ref scrollCols, ref scrollRows);
        }
        public static bool TableScrollbarInput(Event e, Vector2 mp, Rect track, Rect thumb, float scrollMax, ref bool dragging, ref float grabY, ref float scrollY) {
            TableScrollbarInputFn f = _tableScrollbarInput;
            return f != null && f(e, mp, track, thumb, scrollMax, ref dragging, ref grabY, ref scrollY);
        }

        // 行扩展点（与核心同名同签名）：功能向设置页条目行注册过滤/附加控件/滑块范围。
        public delegate bool RowFilterFn(ConfigEntryBase entry);
        public delegate string RowCompanionFn(ConfigEntryBase entry);
        public delegate bool RowSliderFn(ConfigEntryBase entry, out float min, out float max, out string fmt);
        public static readonly List<RowFilterFn> RowFilters = new List<RowFilterFn>();
        public static readonly List<RowCompanionFn> RowCompanions = new List<RowCompanionFn>();
        public static readonly List<RowCompanionFn> RowExtras = new List<RowCompanionFn>();
        public static readonly List<RowSliderFn> RowSliders = new List<RowSliderFn>();

        // 对局模式门控（与核心 SR.ModeMask 按位一致）与场景环境查询
        [Flags] public enum ModeMask { None = 0, Freeplay = 1, Party = 2, Creative = 4, Challenge = 8 }
        private static Func<int, bool> _gateModeAllows;
        private static Func<string> _envSceneName;
        private static Func<bool> _envInTreehouse;
        public static void SetGateSinks(Func<int, bool> gateModeAllows, Func<string> envSceneName, Func<bool> envInTreehouse) {
            _gateModeAllows = gateModeAllows; _envSceneName = envSceneName; _envInTreehouse = envInTreehouse;
        }
        // 当前对局模式是否落在 allowed 位集合内
        public static bool GateModeAllows(ModeMask allowed) { Func<int, bool> f = _gateModeAllows; return f != null && f((int)allowed); }
        // 场景环境（只读）
        public static class Env {
            public static string SceneName { get { Func<string> f = _envSceneName; return f != null ? f() : ""; } }
            public static bool InTreehouse { get { Func<bool> f = _envInTreehouse; return f != null && f(); } }
        }

        // UI 状态/注册门面（窗口尺寸、光标样式、页面注册、按键注册）
        private static Func<Rect> _contentArea; private static Action<Rect> _setContentArea;
        private static Func<float> _footerHeight; private static Action<float> _setFooterHeight;
        private static Func<Vector2> _pageScroll; private static Action<Vector2> _setPageScroll;
        private static Func<bool> _noPageScrollBars, _cursorResize, _cursorMove;
        private static Action<bool> _setNoPageScrollBars, _setCursorResize, _setCursorMove;
        private static Action<Func<bool>> _typingProvider, _inputGateHold;
        private static Action<Action<Camera>> _cameraView;
        private static Action<string, Action, Action> _pageChrome;
        private static Action<string, ConfigEntry<KeyCode>, string> _registerKey;
        private static Func<ConfigEntry<KeyCode>, bool> _comboKeyDown, _comboKeyHeld;
        private static Action<string, Func<ConfigEntryBase, bool>> _renderSectionEntries;
        private static Func<string, List<ConfigEntryBase>> _sectionEntries2;
        private static Func<Color> _tableLineStrong;
        private static Action<Rect, Color> _drawFill;
        public static void SetUiSinks(
            Func<Rect> contentArea, Action<Rect> setContentArea, Func<float> footerHeight, Action<float> setFooterHeight,
            Func<Vector2> pageScroll, Action<Vector2> setPageScroll, Func<bool> noPageScrollBars, Action<bool> setNoPageScrollBars,
            Func<bool> cursorResize, Action<bool> setCursorResize, Func<bool> cursorMove, Action<bool> setCursorMove,
            Action<Func<bool>> typingProvider, Action<Func<bool>> inputGateHold, Action<Action<Camera>> cameraView,
            Action<string, Action, Action> pageChrome, Action<string, ConfigEntry<KeyCode>, string> registerKey,
            Func<ConfigEntry<KeyCode>, bool> comboKeyDown, Func<ConfigEntry<KeyCode>, bool> comboKeyHeld,
            Action<string, Func<ConfigEntryBase, bool>> renderSectionEntries, Func<string, List<ConfigEntryBase>> sectionEntries,
            Func<Color> tableLineStrong, Action<Rect, Color> drawFill) {
            _contentArea = contentArea; _setContentArea = setContentArea; _footerHeight = footerHeight; _setFooterHeight = setFooterHeight;
            _pageScroll = pageScroll; _setPageScroll = setPageScroll; _noPageScrollBars = noPageScrollBars; _setNoPageScrollBars = setNoPageScrollBars;
            _cursorResize = cursorResize; _setCursorResize = setCursorResize; _cursorMove = cursorMove; _setCursorMove = setCursorMove;
            _typingProvider = typingProvider; _inputGateHold = inputGateHold; _cameraView = cameraView; _pageChrome = pageChrome;
            _registerKey = registerKey; _comboKeyDown = comboKeyDown; _comboKeyHeld = comboKeyHeld;
            _renderSectionEntries = renderSectionEntries; _sectionEntries2 = sectionEntries;
            _tableLineStrong = tableLineStrong; _drawFill = drawFill;
        }
        public static Rect ContentArea { get { Func<Rect> f = _contentArea; return f != null ? f() : default(Rect); } set { Action<Rect> a = _setContentArea; if (a != null) a(value); } }
        public static float FooterHeight { get { Func<float> f = _footerHeight; return f != null ? f() : 0f; } set { Action<float> a = _setFooterHeight; if (a != null) a(value); } }
        public static Vector2 PageScroll { get { Func<Vector2> f = _pageScroll; return f != null ? f() : default(Vector2); } set { Action<Vector2> a = _setPageScroll; if (a != null) a(value); } }
        public static bool NoPageScrollBars { get { Func<bool> f = _noPageScrollBars; return f != null && f(); } set { Action<bool> a = _setNoPageScrollBars; if (a != null) a(value); } }
        public static bool CursorResize { get { Func<bool> f = _cursorResize; return f != null && f(); } set { Action<bool> a = _setCursorResize; if (a != null) a(value); } }
        public static bool CursorMove { get { Func<bool> f = _cursorMove; return f != null && f(); } set { Action<bool> a = _setCursorMove; if (a != null) a(value); } }
        public static void RegisterTypingProvider(Func<bool> provider) { Action<Func<bool>> f = _typingProvider; if (f != null) f(provider); }
        public static void RegisterInputGateHold(Func<bool> hold) { Action<Func<bool>> f = _inputGateHold; if (f != null) f(hold); }
        public static void RegisterCameraView(Action<Camera> apply) { Action<Action<Camera>> f = _cameraView; if (f != null) f(apply); }
        public static void RegisterPageChrome(string section, Action header, Action footer) { Action<string, Action, Action> f = _pageChrome; if (f != null) f(section, header, footer); }
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) { Action<string, ConfigEntry<KeyCode>, string> f = _registerKey; if (f != null) f(name, entry, mode); }
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyDown; return f != null && f(entry); }
        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) { Func<ConfigEntry<KeyCode>, bool> f = _comboKeyHeld; return f != null && f(entry); }
        public static void RenderSectionEntries(string section, Func<ConfigEntryBase, bool> filter) { Action<string, Func<ConfigEntryBase, bool>> f = _renderSectionEntries; if (f != null) f(section, filter); }
        public static void RenderSectionEntries(string section) { RenderSectionEntries(section, null); }
        public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries2; return f != null ? f(section) : null; }
        public static Color TableLineStrong { get { Func<Color> f = _tableLineStrong; return f != null ? f() : default(Color); } }
        public static void DrawFill(Rect r, Color c) { Action<Rect, Color> f = _drawFill; if (f != null) f(r, c); }
        public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }
        public static void DrawLine(Rect r) { DrawFill(r, new Color(1f, 1f, 1f, 0.22f)); }

        // UI 工具箱门面（IMGUI 控件/样式/布局）：实现在核心，契约层只转发。
        public static class Ctl {
            public delegate int ComboBoxFn(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width);
            private static Func<float, float> _sc;
            private static Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> _restoreLabel;
            private static Action<ConfigEntryBase> _renderControl;
            private static Action<ConfigEntryBase, bool, float> _renderEntryRow;
            private static Func<string, string, ConfigEntryBase> _findEntry;
            private static Func<string, List<ConfigEntryBase>> _sectionEntries;
            private static Action<ConfigEntryBase, float> _drawEntryRow;
            private static Func<string, float, float> _textHeight;
            private static Func<Rect, float, float, float, bool, float> _drawSlider;
            private static ComboBoxFn _comboBox;
            private static Func<float> _sidebarWidth, _winWidth;
            private static Action<ConfigEntryBase, object> _setValue;
            private static Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> _hotkeyActionButton;
            private static Func<string, string, string, Action, float, float, bool> _hotkeyButton;
            private static Func<string, GUIStyle> _style;
            private static Action _scrollToBottom, _noteEntryBound;
            private static Action<ConfigFile> _markConfigDirty;
            private static Func<bool> _langEn;
            private static Func<Dictionary<ConfigEntryBase, bool>> _editOpen;
            public static void SetSinks(
                Func<float, float> sc,
                Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> restoreLabel,
                Action<ConfigEntryBase> renderControl,
                Action<ConfigEntryBase, bool, float> renderEntryRow,
                Func<string, string, ConfigEntryBase> findEntry,
                Func<string, List<ConfigEntryBase>> sectionEntries,
                Action<ConfigEntryBase, float> drawEntryRow,
                Func<string, float, float> textHeight,
                Func<Rect, float, float, float, bool, float> drawSlider,
                ComboBoxFn comboBox,
                Func<float> sidebarWidth, Func<float> winWidth,
                Action<ConfigEntryBase, object> setValue,
                Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> hotkeyActionButton,
                Func<string, string, string, Action, float, float, bool> hotkeyButton,
                Func<string, GUIStyle> style,
                Action scrollToBottom, Action noteEntryBound, Action<ConfigFile> markConfigDirty,
                Func<bool> langEn, Func<Dictionary<ConfigEntryBase, bool>> editOpen) {
                _sc = sc; _restoreLabel = restoreLabel; _renderControl = renderControl; _renderEntryRow = renderEntryRow;
                _findEntry = findEntry; _sectionEntries = sectionEntries; _drawEntryRow = drawEntryRow; _textHeight = textHeight;
                _drawSlider = drawSlider; _comboBox = comboBox; _sidebarWidth = sidebarWidth; _winWidth = winWidth;
                _setValue = setValue; _hotkeyActionButton = hotkeyActionButton; _hotkeyButton = hotkeyButton; _style = style;
                _scrollToBottom = scrollToBottom; _noteEntryBound = noteEntryBound; _markConfigDirty = markConfigDirty;
                _langEn = langEn; _editOpen = editOpen;
            }
            public static float Sc(float v) { Func<float, float> f = _sc; return f != null ? f(v) : v; }
            // 表格自适应高度助手（Ctl 侧别名；功能层统一从这里取）。
            public static float TableAutoHeight(float usedAbove, float minH) { return SRApi.TableAutoHeight(usedAbove, minH); }
            public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey) { Action<GUIContent, ConfigEntryBase, float, float, ConfigEntry<KeyCode>> f = _restoreLabel; if (f != null) f(content, entry, w, h, hotkey); }
            public static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h) { RestoreLabel(content, entry, w, h, null); }
            public static void RenderControl(ConfigEntryBase entry) { Action<ConfigEntryBase> f = _renderControl; if (f != null) f(entry); }
            public static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) { Action<ConfigEntryBase, bool, float> f = _renderEntryRow; if (f != null) f(entry, isInternal, colWidth); }
            public static ConfigEntryBase FindEntry(string section, string key) { Func<string, string, ConfigEntryBase> f = _findEntry; return f != null ? f(section, key) : null; }
            public static List<ConfigEntryBase> SectionEntries(string section) { Func<string, List<ConfigEntryBase>> f = _sectionEntries; return f != null ? f(section) : null; }
            public static void DrawEntryRow(ConfigEntryBase entry, float colWidth) { Action<ConfigEntryBase, float> f = _drawEntryRow; if (f != null) f(entry, colWidth); }
            public static float TextHeight(string text, float width) { Func<string, float, float> f = _textHeight; return f != null ? f(text, width) : 0f; }
            public static float DrawSlider(Rect rect, float value, float min, float max, bool intStep) { Func<Rect, float, float, float, bool, float> f = _drawSlider; return f != null ? f(rect, value, min, max, intStep) : value; }
            public static float DrawSlider(Rect rect, float value, float min, float max) { return DrawSlider(rect, value, min, max, false); }
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width) { ComboBoxFn f = _comboBox; return f != null ? f(entry, current, vals, options, ref open, width) : 0; }
            public static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open) { return ComboBox(entry, current, vals, options, ref open, -1f); }
            public static float SidebarWidth() { Func<float> f = _sidebarWidth; return f != null ? f() : 0f; }
            public static float WinWidth { get { Func<float> f = _winWidth; return f != null ? f() : 0f; } }
            public static void SetValue(ConfigEntryBase entry, object value) { Action<ConfigEntryBase, object> f = _setValue; if (f != null) f(entry, value); }
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry, float width, bool bindable) { Func<string, string, ConfigEntry<KeyCode>, float, bool, bool> f = _hotkeyActionButton; return f != null && f(label, tooltip, keyEntry, width, bindable); }
            public static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry) { return HotkeyActionButton(label, tooltip, keyEntry, 0f, false); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action, float width, float height) { Func<string, string, string, Action, float, float, bool> f = _hotkeyButton; return f != null && f(id, label, tooltip, action, width, height); }
            public static bool HotkeyButton(string id, string label, string tooltip, Action action) { return HotkeyButton(id, label, tooltip, action, 0f, 0f); }
            private static GUIStyle Style(string name) { Func<string, GUIStyle> f = _style; return f != null ? f(name) : null; }
            public static GUIStyle Label { get { return Style("Label"); } }
            public static GUIStyle LabelWrap { get { return Style("LabelWrap"); } }
        // 自适应标签：按文本实测宽高绘制（宽度=文本宽+2，高度=CalcHeight），避免固定高度把文字下半截裁掉。
        // 功能页统一用它替代 GUILayout.Label(..., GUILayout.Height(固定值))。
        public static void LabelAuto(string text) { LabelAuto(text, 0f); }
        public static void LabelAuto(string text, float minWidth) {
            try {
                GUIStyle st = Style("Label");
                GUIContent c = new GUIContent(text ?? "");
                float w = Mathf.Max(minWidth, st.CalcSize(c).x + 2f);
                float h = Mathf.Max(st.CalcSize(c).y, st.CalcHeight(c, w));
                GUILayout.Label(c, st, GUILayout.Width(w), GUILayout.Height(h));
            } catch {
                try { GUILayout.Label(text ?? ""); } catch { }
            }
        }
            public static GUIStyle ChatLabel { get { return Style("ChatLabel"); } }
            public static GUIStyle ChatArea { get { return Style("ChatArea"); } }
            public static GUIStyle SecHeader { get { return Style("SecHeader"); } }
        // 分区标题：宽度收缩到文字实际宽度，悬浮提示只在标题文字上出现（不再整行生效）。
        public static void SectionTitle(string title, string tip = null) {
            try {
                GUIContent c = new GUIContent(title, tip);
                GUILayout.Label(c, SecHeader, GUILayout.Width(SecHeader.CalcSize(c).x));
            } catch { try { GUILayout.Label(title, SecHeader); } catch { } }
        }
            public static GUIStyle Btn { get { return Style("Btn"); } }
            public static GUIStyle BtnClip { get { return Style("BtnClip"); } }
            public static GUIStyle SearchBox { get { return Style("SearchBox"); } }
            public static GUIStyle Footer { get { return Style("Footer"); } }
            public static bool LangEn { get { Func<bool> f = _langEn; return f != null && f(); } }
            public static Dictionary<ConfigEntryBase, bool> EditOpen { get { Func<Dictionary<ConfigEntryBase, bool>> f = _editOpen; return f != null ? f() : null; } }
            public static void ScrollToBottom() { Action a = _scrollToBottom; if (a != null) a(); }
            public static void NoteEntryBound() { Action a = _noteEntryBound; if (a != null) a(); }
            public static void MarkConfigDirty(ConfigFile cfg) { Action<ConfigFile> f = _markConfigDirty; if (f != null) f(cfg); }
        }

        // 本地化门面：语言引擎（语言包 / 词条表 / 缓存）留在核心，契约层只转发，行为不变。
        private static Func<string, string, string> _t;
        private static Action<string, string> _locSec2;
        private static Action<string, string, string> _locSec3;
        private static Action<string, string, string, string> _locKey, _locDesc;
        private static Action<string, string> _locEnum;
        private static Func<string> _currentLanguageId;
        private static Func<int> _langVer;
        private static Action<string, string, object[]> _notifyT;
        public static void SetLangSinks(Func<string, string, string> t,
                                        Action<string, string> locSec2, Action<string, string, string> locSec3,
                                        Action<string, string, string, string> locKey,
                                        Action<string, string, string, string> locDesc,
                                        Action<string, string> locEnum,
                                        Func<string> currentLanguageId, Func<int> langVer,
                                        Action<string, string, object[]> notifyT) {
            _t = t; _locSec2 = locSec2; _locSec3 = locSec3; _locKey = locKey; _locDesc = locDesc;
            _locEnum = locEnum; _currentLanguageId = currentLanguageId; _langVer = langVer; _notifyT = notifyT;
        }
        // 取译文：zh 为源码中文原文，en 为英文回退
        public static string T(string zh, string en) { Func<string, string, string> f = _t; return f != null ? f(zh, en) : zh; }
        // 本地化注册：栏目 / 键名 / 描述 / 枚举值
        public static void LocSec(string sec, string zh) { Action<string, string> f = _locSec2; if (f != null) f(sec, zh); }
        public static void LocSec(string sec, string zh, string en) { Action<string, string, string> f = _locSec3; if (f != null) f(sec, zh, en); }
        public static void LocKey(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locKey; if (f != null) f(sec, key, zh, en); }
        public static void LocDesc(string sec, string key, string zh, string en) { Action<string, string, string, string> f = _locDesc; if (f != null) f(sec, key, zh, en); }
        public static void LocEnum(string name, string zh) { Action<string, string> f = _locEnum; if (f != null) f(name, zh); }
        public static string CurrentLanguageId { get { Func<string> f = _currentLanguageId; return f != null ? f() : "English"; } }
        public static int LangVer { get { Func<int> f = _langVer; return f != null ? f() : 0; } }
        public static void NotifyT(string zh, string en, params object[] args) {
            Action<string, string, object[]> f = _notifyT;
            if (f != null) f(zh, en, args);
            else Notify(string.Format(T(zh, en), args ?? new object[0]));
        }

        // 门控/UI 状态门面：状态在核心，契约层只读（provider 注入）。
        public const int ApiVersion = 3;   //与核心 SR.ApiVersion 同步
        private static Func<bool> _hasServer, _gateMaster, _gateHost, _canControl, _canDoHost, _uiOpen, _blockInput, _blockWindowDrag;
        private static Func<int> _wheelFrame;
        private static Func<float> _takeWheel;
        private static Action _markWheelUsed, _forceResetUiState, _flushConfig;
        private static Func<string, string> _selectedPageId;
        public static void SetStateSinks(
            Func<bool> hasServer, Func<bool> gateMaster, Func<bool> gateHost, Func<bool> canControl,
            Func<bool> canDoHost, Func<bool> uiOpen, Func<bool> blockInput, Func<bool> blockWindowDrag,
            Func<int> wheelFrame, Func<float> takeWheel, Action markWheelUsed,
            Action forceResetUiState, Action flushConfig, Func<string, string> selectedPageId,
            Action<bool> setBlockWindowDrag, Action<int> setWheelFrame) {
            _hasServer = hasServer; _gateMaster = gateMaster; _gateHost = gateHost; _canControl = canControl;
            _canDoHost = canDoHost; _uiOpen = uiOpen; _blockInput = blockInput; _blockWindowDrag = blockWindowDrag;
            _wheelFrame = wheelFrame; _takeWheel = takeWheel; _markWheelUsed = markWheelUsed;
            _forceResetUiState = forceResetUiState; _flushConfig = flushConfig; _selectedPageId = selectedPageId;
            _setBlockWindowDrag = setBlockWindowDrag; _setWheelFrame = setWheelFrame;
        }
        public static bool HasServer { get { return _hasServer != null && _hasServer(); } }
        public static bool GateMaster { get { return _gateMaster != null && _gateMaster(); } }
        public static bool GateHost { get { return _gateHost != null && _gateHost(); } }
        public static bool CanControlLocally { get { return _canControl != null && _canControl(); } }
        public static bool CanDoHost() { return _canDoHost != null && _canDoHost(); }
        public static bool UiOpen { get { return _uiOpen != null && _uiOpen(); } }
        public static bool BlockInput { get { return _blockInput != null && _blockInput(); } }
        private static Action<bool> _setBlockWindowDrag;
        public static bool BlockWindowDrag { get { return _blockWindowDrag != null && _blockWindowDrag(); } set { Action<bool> a = _setBlockWindowDrag; if (a != null) a(value); } }
        private static Action<int> _setWheelFrame;
        public static int WheelFrame { get { return _wheelFrame != null ? _wheelFrame() : -1; } set { Action<int> a = _setWheelFrame; if (a != null) a(value); } }
        public static float TakeWheelEvent() { return _takeWheel != null ? _takeWheel() : 0f; }
        public static void MarkWheelUsed() { Action a = _markWheelUsed; if (a != null) a(); }
        public static void ForceResetUiState() { Action a = _forceResetUiState; if (a != null) a(); }
        public static void FlushConfig() { Action a = _flushConfig; if (a != null) a(); }
        public static string SelectedPageId(string section) { Func<string, string> f = _selectedPageId; return f != null ? f(section) : null; }

        // 扩展点注册门面：注册表在核心，契约层只转发（功能层不再依赖 SR.*）。
        private static Action<string, int> _nav;
        private static Action<string> _navHide;
        private static Action<string, Action> _tick;
        private static Action<Action> _sceneHook, _dismissHook;
        private static Action<Func<float, bool>> _wheelHook;
        private static Action<string, string, Action> _hotkey;
        public static void SetExtSinks(Action<string, int> nav, Action<string> navHide, Action<string, Action> tick,
                                       Action<Action> sceneHook, Action<Action> dismissHook,
                                       Action<Func<float, bool>> wheelHook, Action<string, string, Action> hotkey) {
            _nav = nav; _navHide = navHide; _tick = tick; _sceneHook = sceneHook; _dismissHook = dismissHook; _wheelHook = wheelHook; _hotkey = hotkey;
        }
        //侧栏栏目与顺序（rank 越小越靠前）
        public static void Nav(string sec, int rank) { Action<string, int> f = _nav; if (f != null) f(sec, rank); }
        //隐藏某个配置段对应的栏目
        public static void NavHide(string sec) { Action<string> f = _navHide; if (f != null) f(sec); }
        //每帧回调（name 仅用于自检/日志）
        public static void RegisterTick(string name, Action hook) { Action<string, Action> f = _tick; if (f != null) f(name, hook); }
        //场景切换通知
        public static void RegisterSceneHook(Action hook) { Action<Action> f = _sceneHook; if (f != null) f(hook); }
        //收起功能 UI 的通知
        public static void RegisterDismissHook(Action hook) { Action<Action> f = _dismissHook; if (f != null) f(hook); }
        //滚轮钩子（返回 true = 已消费）
        public static void RegisterWheelHook(Func<float, bool> hook) { Action<Func<float, bool>> f = _wheelHook; if (f != null) f(hook); }
        //注册一个可绑定的快捷键动作
        public static void HotkeyAction(string id, string label, Action action) { Action<string, string, Action> f = _hotkey; if (f != null) f(id, label, action); }

        // 反射门面：实际实现（缓存 / 登记 RefOverride / 启动自检）在核心，契约层只转发，行为不变。
        public static class Ref {
            internal static Func<Type, string, string, FieldInfo> FieldFn;
            internal static Func<Type, string, string, MethodInfo> MethodFn;
            internal static Func<Type, string, Type[], string, MethodInfo> MethodArgsFn;
            internal static Action<string, string> OverrideFn;
            public static void SetSinks(Func<Type, string, string, FieldInfo> field,
                                        Func<Type, string, string, MethodInfo> method,
                                        Func<Type, string, Type[], string, MethodInfo> methodArgs,
                                        Action<string, string> over) {
                FieldFn = field; MethodFn = method; MethodArgsFn = methodArgs; OverrideFn = over;
            }
            public static FieldInfo Field(Type owner, string name, string feature) { Func<Type, string, string, FieldInfo> f = FieldFn; return f != null ? f(owner, name, feature) : null; }
            public static MethodInfo Method(Type owner, string name, string feature) { Func<Type, string, string, MethodInfo> f = MethodFn; return f != null ? f(owner, name, feature) : null; }
            public static MethodInfo Method(Type owner, string name, Type[] parameters, string feature) { Func<Type, string, Type[], string, MethodInfo> f = MethodArgsFn; return f != null ? f(owner, name, parameters, feature) : null; }
            public static void Override(string member, string feature) { Action<string, string> f = OverrideFn; if (f != null) f(member, feature); }
        }

        // 自检用：SRApi 自身 + 全部嵌套类型（含以后新增的门面分组，避免硬编码列表漏统计）。
        internal static Type[] Owners() {
            Type self = typeof(SRApi);
            Type[] nested = self.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            Type[] all = new Type[nested.Length + 1];
            all[0] = self;
            Array.Copy(nested, 0, all, 1, nested.Length);
            return all;
        }
        // 注入点总数（= 声明为委托类型的静态字段数）
        public static int SinkTotal() {
            int n = 0;
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) if (typeof(Delegate).IsAssignableFrom(fs[j].FieldType)) n++;
                }
            } catch { }
            return n;
        }

        // 自检：反射遍历全部注入点，返回尚未注入的 API 通道名（漏注入会让对应功能静默失效）。
        // 运行时由 MainPlugin 在注入完成后调用；Ref.TypeFn 恒为 null（核心私有，功能层用不到），列入白名单。
        public static string[] MissingSinks() {
            List<string> miss = new List<string>();
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) {
                        FieldInfo f = fs[j];
                        if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        if (f.GetValue(null) != null) continue;
                        string name = owners[i].Name + "." + f.Name;
                        bool optional = false;
                        if (!optional) miss.Add(name);
                    }
                }
            } catch { }
            return miss.ToArray();
        }
        // 已注入的 API 通道总数（自检用）
        public static int SinkCount() {
            int n = 0;
            try {
                Type[] owners = Owners();
                for (int i = 0; i < owners.Length; i++) {
                    FieldInfo[] fs = owners[i].GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int j = 0; j < fs.Length; j++) if (typeof(Delegate).IsAssignableFrom(fs[j].FieldType) && fs[j].GetValue(null) != null) n++;
                }
            } catch { }
            return n;
        }

        // 统一带日志的 try / catch 包装：同一条 what 5 秒内只记一次，每帧路径上也安全。
        public static class Guard {
            private static readonly Dictionary<string, int> _lastWarn = new Dictionary<string, int>();
            public static string LastWhat, LastError;
            public static int WarnCount;

            public static void Try(string what, Action action) {
                try { action(); } catch (Exception ex) { Warn(what, ex); }
            }
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); } catch (Exception ex) { Warn(what, ex); return fallback; }
            }
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            private static void Warn(string what, Exception ex) {
                try {
                    int now = Environment.TickCount;
                    int last;
                    if (what != null && _lastWarn.TryGetValue(what, out last) && unchecked((uint)(now - last)) < 5000u) return;
                    if (what != null) _lastWarn[what] = now;
                    LastWhat = what;
                    LastError = ex != null ? (ex.GetType().Name + ": " + ex.Message) : "未知错误";
                    WarnCount++;
                    SRApi.Warn("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch (Exception __ex) {
                    try { LastWhat = what; LastError = "Warn 自身失败: " + __ex.GetType().Name; } catch { }
                }
            }
        }
    }

}
