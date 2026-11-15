// 框架本体：状态与初始化、Register 扩展点、每帧钩子派发、页面重置与日志包装 Guard。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
[SrFeature("sr.core", "SR 核心", "1.0", Rank = 0)]
public partial class SR : ITweak {

// 分区：Core（核心状态 / 初始化 / 插件扫描）
// 静态字段集中于此；分区之间共享 private 字段编译器不会拦，故只在本文件用。

        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _tickProf;
        public static bool TickProfilingEnabled { get { return _tickProf != null && _tickProf.Value; } }   // 帧钩子剖析开关
        private static ConfigEntry<bool> _blockInputEntry;
        private static ConfigEntry<float> _uiScaleEntry;
        private static ConfigEntry<string> _disabledPluginsEntry;
        private static ConfigEntry<bool> _allEnabledEntry;
        // 对外暴露的注册接口版本；必须 static readonly：const 会被内联，握手只能运行时读取。
        public static readonly int ApiVersion = 3;
        // Ui：窗口宽高 / 位置 / 侧栏宽 / 整体缩放的唯一真值。
        internal static class Ui {
            public const int WinWMin = 400, WinWMax = 1600;
            public const int WinHMin = 300, WinHMax = 1000;
            public const int PosMin = 0, PosMax = 2000;
            public const int SidebarWMin = 0, SidebarWMax = 320;
            public const float ScaleMin = 1f, ScaleMax = 1.8f;
        }
        // Keys：设置段与各键名的唯一真值。
        internal static class Keys {
            public const string Settings = "Settings";
            public const string OpenKey = "Open Key";
            public const string BlockInput = "Block Input";
            public const string UiScale = "UI Scale";
            public const string Language = "Language";
            public const string SidebarWidth = "Sidebar Width";
            public const string SidebarOrder = "Sidebar Order";
            public const string AllEnabled = "All Enabled";
            public const string DisabledPlugins = "Disabled Plugins";
            public const string WinWidth = "Window Width";
            public const string WinHeight = "Window Height";
            public const string WinX = "Window X";
            public const string WinY = "Window Y";
            public const string ComboPrefix = "组合键 ";
        }

        // 反射辅助：失效告警 + 结果缓存（未命中同样缓存）。
        private static readonly Dictionary<string, string> _refFeature = new Dictionary<string, string>();
        private static readonly HashSet<string> _refWarned = new HashSet<string>();
        private static readonly Dictionary<string, FieldInfo> _refFieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> _refMethodCache = new Dictionary<string, MethodInfo>();





















        private static bool _refAudited;


        // 补丁自检：合并类级/方法级 [HarmonyPatch]，逐个验证按名绑定的目标是否仍存在。
        private static bool _patchAudited;








        public sealed class AuditIssue {
            public string Kind;    // "补丁" / "反射"
            public string Target;  // "Character.setupDeath" / "ChatDisplay.ChatHolder"
            public string Scope;   // 出问题的宿主（补丁=类名；反射=该成员支撑的功能描述）
        }
        private static readonly List<AuditIssue> _auditIssues = new List<AuditIssue>();
        public static IList<AuditIssue> AuditIssues { get { return _auditIssues; } }
        // 孤儿键数：文件里有但没被 Bind 的键，Save 时会原样写回；启动扫描后显示在设置页。
        public static int OrphanKeyCount;
        public static int AuditOrphan;
        public static int AuditGateTotal;
        public static int AuditPerFrame;
        public static int OrphanPendingCount;
        // 无归属又未被 NavHide 的配置段数（会以兜底页出现在侧栏）。
        public static int FallbackSectionCount;
        public static int AuditIssueCount { get { return _auditIssues.Count; } }
        public static int AuditPatchTotal, AuditPatchOk, AuditRefTotal, AuditRefOk;
        private static bool _startupAudited;



        // OrphanedEntries 非公开，只能反射取；拿不到就跳过，纯诊断不影响启动。
        private static System.Reflection.MemberInfo _orphanMember;
        private static bool _orphanMemberTried;
        // 反射取 OrphanedEntries，拿不到返回 null；OrphanAudit 与 CleanOrphanKeys 共用。
        private static System.Collections.IDictionary ReadOrphanDict() {
            ConfigFile cfg = _internalConfig;
            if (cfg == null) return null;
            if (!_orphanMemberTried) {
                _orphanMemberTried = true;
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                try { _orphanMember = typeof(ConfigFile).GetProperty("OrphanedEntries", BF); } catch { _orphanMember = null; }
                if (_orphanMember == null) { try { _orphanMember = typeof(ConfigFile).GetField("OrphanedEntries", BF); } catch { _orphanMember = null; } }
                if (_orphanMember == null) SR.LogWarn("[SR.Cfg] 读不到 BepInEx 的 OrphanedEntries（版本差异），跳过孤儿键自检");
            }
            if (_orphanMember == null) return null;
            try {
                object raw;
                System.Reflection.PropertyInfo pi = _orphanMember as System.Reflection.PropertyInfo;
                if (pi != null) raw = pi.GetValue(cfg, null);
                else raw = ((System.Reflection.FieldInfo)_orphanMember).GetValue(cfg);
                return raw as System.Collections.IDictionary;
            } catch (Exception __ex) { Guard.Log("读取孤儿键表", __ex); return null; }
        }









        public static bool AuditHasIssues { get { return _auditIssues.Count > 0; } }

        // 调试信息逐行独立 try，某项取不到只影响那一行。
        // 总开关：关闭时功能运行时失效，各功能开关值不变；改动自动保存。
        public static bool AllEnabled = false;
        private static bool _appliedDisabled;
        // 组合修饰持久化格式 "Shift+Ctrl+Alt"（见 SR.KeyBinds 的 ParseComboMod）。
        [Flags]
        public enum ComboMod { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _keyMods = new Dictionary<ConfigEntryBase, ComboMod>();
        private static readonly Dictionary<ConfigEntryBase, ConfigEntry<string>> _keyModEntries = new Dictionary<ConfigEntryBase, ConfigEntry<string>>();
        private static bool _visible;
        private static float _uiAlpha; // 开合淡入淡出
        private static bool _scanned;
        private enum Mode { Internal, Settings, External }
        // internal = SR_UCH 栏目，settings = 独立设置页，external = 其它插件
        private static ConfigFile _internalConfig;
        private static bool _warnedNoSelfGuid;
        private static bool _cfgDirty;
        private static float _cfgNextSaveAt;

        private static void OnAnySettingChanged(object s, SettingChangedEventArgs e) { _cfgDirty = true; }

        // 登记配置待落盘：SR 自身记 _cfgDirty，其它插件进 _dirtyConfigs。必须 public：拆分模式下功能是独立程序集。
        public static void MarkConfigDirty(ConfigFile cfg) {
            if (cfg == null) return;
            if (ReferenceEquals(cfg, _internalConfig)) { _cfgDirty = true; return; }
            // 用集合而非单槽：先后改动两个插件时单槽会覆盖前者，其改动永不落盘。
            _dirtyConfigs.Add(cfg);
            _dirty = true;
        }

        // 改过的配置最多每秒落盘一次；关面板 / 退出前的显式 Save 仍即时。
        public static void FlushConfig() {
            if (!_cfgDirty || _internalConfig == null) return;
            if (Time.unscaledTime < _cfgNextSaveAt) return;
            _cfgNextSaveAt = Time.unscaledTime + 1f;
            // 保存成功才清脏标记，失败保持脏以便下次重试。
            bool saved = Guard.Try("配置定期落盘", () => { _internalConfig.Save(); return true; }, false);
            if (saved) _cfgDirty = false;
        }
        private static readonly List<string> _internalSections = new List<string>();
        // 全部栏目（含隐藏）供表头当列用；_internalSections 只含可见的。
        internal static readonly List<string> _allSections = new List<string>();
        // 手动隐藏的栏目（可还原；NavHide 是永不显示）
        internal static readonly HashSet<string> _userHiddenSections = new HashSet<string>();
        private static readonly List<string> _navOrder = new List<string>();                     // 注册先后
        private static readonly Dictionary<string, int> _navRank = new Dictionary<string, int>(); // 顺序值（越小越靠前）
        private static readonly HashSet<string> _navHidden = new HashSet<string>();              // 有 cfg 段但不单独成栏目
        private static string _selectedInternalSection = "";
        private static Mode _mode = Mode.Internal;
        private static readonly List<PluginEntry> _externalPlugins = new List<PluginEntry>();
        private static string _pluginKey = "";
        private static Vector2 _scroll;
        // 文本测量缓存，避免每帧对所有条目 CalcSize。
        private static int _nameWKey;   // 文本哈希折叠键（见 EntryNameWidth）
        private static float _nameWCached;
        private static readonly Dictionary<string, List<float[]>> _heightCache = new Dictionary<string, List<float[]>>();   // 两级键（文本 -> [宽, 缩放, 高]），免每帧拼 key
        private static Vector2 _leftScroll;

        private static ConfigEntryBase _capturing; // 键位捕捉目标
        // 捕捉起始帧：右键进入捕捉的那次 MouseDown 不能被"按下即取消"清掉。
        private static int _captureStartFrame = -1;
        private static int _rmbConsumedFrame = -1; // 当帧右键绑键：一帧只认一个按钮
        private static object _prevBoxed;
        // 快捷键录制态：按键及其顺序（见 SR.Window 的录制分支 / CommitRecording）。
        private static readonly List<KeyCode> _recSeq = new List<KeyCode>();
        private static readonly List<KeyCode> _recHeld = new List<KeyCode>(); // 录制中仍按住的键（全部松开即完成录制）
        // 待落盘的外部插件配置（SR 自身走 _cfgDirty + FlushConfig）。
        private static readonly HashSet<ConfigFile> _dirtyConfigs = new HashSet<ConfigFile>();
        internal static HashSet<ConfigFile> DirtyConfigs { get { return _dirtyConfigs; } }
        private static readonly Dictionary<ConfigEntryBase, string> _editText = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, bool> _editOpen = new Dictionary<ConfigEntryBase, bool>();
        private static int _winWidth = 720;
        private static int _winHeight = 520;
        private static float _winX = 30f;
        private static float _winY = 30f;
        private static ConfigEntry<int> _winWidthEntry, _winHeightEntry;
        private static ConfigEntry<float> _winXEntry, _winYEntry;
        private static bool _dragActive, _dragMoved, _resizing;
        private static Vector2 _dragOffset, _downPos, _resizeStart, _resizeStartSize;
        private static bool _dirty;
        private static float _scaled = 1f;
        private static float Sc(float v) { return v * _scaled; }

        // 性能计数器（自检页显示）：每帧调度与绘制的平滑耗时，不参与逻辑。
        internal static float PerfTickAvgMs, PerfGuiAvgMs, PerfTickMaxMs, PerfGuiMaxMs;
        private static UnityEngine.EventSystems.EventSystem _gatedEventSystem;
        private static bool _stylesReady;
        private static GUIStyle _win, _title, _titleLabel, _titleMid, _label, _nameLabel, _labelClip, _labelClipCenter, _secHeaderCenter, _labelWrap, _chatLabel, _chatArea, _secHeader, _item, _selItem, _btn, _btnClip, _frame,
            _capture, _checkOn, _checkOff, _popup, _searchBox, _footer, _tooltip,
            _sliderTrack, _sliderFill, _sliderHandle, _sliderHandleHover, _sliderHandleActive;
        private static Font _font;
        private static Font _prevFont;
        private static Texture2D _gripTex;
        private static Texture2D _cursorTex;
        // 自绘光标贴图（独立浮层绘制在最上层时需要自己再画一遍光标）。
        internal static Texture2D CursorTexture { get { return _cursorTex; } }
        private static Texture2D _cursorResizeTex; // 悬停分隔线时的左右调整光标
        private static Texture2D _cursorMoveTex;   // 悬停可拖动单元格时的四向移动光标
        public static bool CursorResize;          // 本帧是否显示调整光标（Tick 复位）
        public static bool CursorMove;            // 本帧是否显示移动光标
        private static readonly List<GUIStyle> _styleList = new List<GUIStyle>();

        public static bool UiOpen { get { return _visible; } }
        public static bool BlockInput = true;

        // 门控豁免：模块登记委托，任一为真即豁免；未登记不豁免。
        private static readonly List<Func<bool>> _modeOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _hostOverrides = new List<Func<bool>>();
        private static readonly List<Func<bool>> _pauseProviders = new List<Func<bool>>();

        // 豁免来源登记：谁在提权必须可见（设置页显示 + 首次登记写日志）。
        public static readonly List<string> OverrideSources = new List<string>();
        // 找真正的发起程序集：跳过核心自身（含 SRApi）的栈帧，避免把转发误判成 SR_UCH。
        private static string CallerAssembly() {
            try {
                string mine = typeof(SR).Assembly.GetName().Name;
                var st = new System.Diagnostics.StackTrace();
                for (int i = 2; i < st.FrameCount; i++) {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    Type dt = m.DeclaringType;
                    if (dt == null) continue;
                    string an = dt.Assembly.GetName().Name;
                    if (string.Equals(an, mine, StringComparison.OrdinalIgnoreCase)) continue;
                    return an;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return "";
        }
        private static bool IsContractConsumer(string asmName) {
            try {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                    try {
                        if (a == null || a.IsDynamic) continue;
                        if (!string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (AssemblyName rn in a.GetReferencedAssemblies()) {
                            if (rn != null && string.Equals(rn.Name, typeof(SR).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        return false;   // 找到该程序集但没引用 SR_UCH -> 非契约调用方
                    } catch (Exception __a) { Guard.Log("豁免登记/检查引用", __a); }
                }
            } catch (Exception __ex) { Guard.Log("豁免登记/遍历程序集", __ex); }
            return false;
        }

        private static bool NoteOverrideSource(string kind) {
            try {
                string who = CallerAssembly();
                // 只信任核心自身与引用 SR_UCH 的程序集；其它来源拒绝并写日志，避免静默提权。
                bool ok = string.IsNullOrEmpty(who) || IsContractConsumer(who);
                string line = kind + " <- " + (string.IsNullOrEmpty(who) ? "核心" : who)
                    + (ok ? "" : "（已拒绝：非契约调用方）");
                if (!OverrideSources.Contains(line)) OverrideSources.Add(line);
                if (!ok) {
                    try { LogWarn("[门控] 拒绝非契约调用方的豁免登记：" + line); }
                    catch (Exception __lw) { Guard.Log("豁免登记拒绝日志", __lw); }
                }
                return ok;
            } catch { return false; }
        }
        public static void RegisterModeOverride(Func<bool> f) {
            if (f != null && !_modeOverrides.Contains(f) && NoteOverrideSource("模式限制")) _modeOverrides.Add(f);
        }
        public static void RegisterHostOverride(Func<bool> f) {
            if (f != null && !_hostOverrides.Contains(f) && NoteOverrideSource("房主限制")) _hostOverrides.Add(f);
        }
        public static void RegisterPauseProvider(Func<bool> f) {
            if (f != null && !_pauseProviders.Contains(f)) _pauseProviders.Add(f);
        }
        private static bool AnyTrue(List<Func<bool>> list) {
            for (int i = 0; i < list.Count; i++) {
                try { if (list[i]()) return true; }
                catch (Exception __ex) { Guard.Log("查询门控豁免", __ex); }
            }
            return false;
        }
        public static bool IgnoreModeLimit { get { return AnyTrue(_modeOverrides); } }
        public static bool HostOverride { get { return AnyTrue(_hostOverrides); } }
        public static bool PauseGame { get { return AnyTrue(_pauseProviders); } }


        private static bool _selfCheckFooterDone;   // 自检底栏入口只注册一次（第一帧注册，顺序 设置 | 通知 | 自检）
        public void Initialize(IFeatureHost plugin) {
            // 表格自适应高度：公式在实现层，契约层只转发（功能页在 Initialize 后才渲染）。
            try { SRApi.SetTableAutoHeightSink(delegate (float __u, float __m) { return SR.Ctl.TableAutoHeight(__u, __m); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度助手", __ex); }
            // 统一操作列按钮：绘制/命中/悬浮在实现层，契约层只转发。
            try {
                SRApi.Ctl.SetSelfToggleButtonSink(delegate (string __l, string __t, bool __o, ConfigEntry<KeyCode> __k, float __w, bool __b) {
                    return SR.Ctl.SelfToggleButton(__l, __t, __o, __k, __w, __b);
                });
                SRApi.Ctl.SetSelItemSink(delegate () { return SR.Ctl.SelItem; });
                SRApi.Ctl.SetLabeledRowSink(delegate (string __l, string __t, ConfigEntryBase __e, float __w) { SR.Ctl.LabeledRow(__l, __t, __e, __w); });
                SRApi.Ctl.SetRowSliderSink(delegate (string __l, string __t, ConfigEntryBase __e, float __min, float __max, float __w, string __f) {
                    return SR.Ctl.RowSlider(__l, __t, __e, __min, __max, __w, __f);
                });
                // _rowCombo 未注入时，模式/关卡/类型下拉拿不到值；契约 sink 是 7 参（不含 cur）。
                try {
                    SRApi.Ctl.SetRowComboSink(delegate (string __l, string __t, ConfigEntryBase __e, string[] __vals, string[] __labels, float __w, float __ls) {
                        return SR.Ctl.RowCombo(__l, __t, __e, __vals, __labels, __w, __ls);
                    });
                } catch (Exception __rc) { Guard.Log("注入 RowCombo 通道", __rc); }
                SRApi.SetTypingSink(delegate { return SR.Typing; });
                SRApi.Ctl.SetLabelColumnSink(delegate (string __l) { return SR.Ctl.LabelColumnWidth(__l); });
                SRApi.SetWheelLayerSink(delegate { return SR.Wheel.LastDecision; });
                SRApi.Ctl.SetTableHighlightSink(delegate (string __q) { SR.HighlightQuery = __q; });
                SRApi.Ctl.SetFillScrollSink(delegate (string __id, Action<float> __h) { SRApi.Ctl.AddFillHandler(__id, __h); });
                SRApi.Ctl.SetWheelFallbackSink(delegate (string __k, Rect __r, Func<float, bool> __a) { SR.Wheel.RegisterFallback(__k, __r, __a); });
                SRApi.Ctl.SetWheelPollSink(delegate (string __k, Rect __r) { return SR.WheelPoll(__k, __r); });
                SRApi.SetWheelTakeSink(delegate (Rect __rect) { return SR.Wheel.TakeIfInside(__rect); });
                SRApi.SetOverlaySink(delegate (Action __d) { SR.RegisterOverlay(__d); });
                SRApi.SetRoundedSink(delegate (Rect __r, Color __c, float __rad) { SR.DrawRounded(__r, __c, __rad); });
                SRApi.Ctl.SetButtonSink(delegate (string __l, string __t, float __w, float __h) {
                    return SR.Ctl.Button(__l, __t, __w, __h);
                });
                SRApi.Ctl.SetRowComboSink(delegate (string __l, string __t, ConfigEntryBase __e, string[] __vals, string[] __labels, float __cw, float __lw) {
                    return SR.Ctl.RowCombo(__l, __t, __e, __vals, __labels, __cw, __lw);
                });
                SRApi.Ctl.SetCheckStylesSink(delegate () { return SR.Ctl.CheckOn; }, delegate () { return SR.Ctl.CheckOff; });
                SRApi.SetTableColMoveSink(delegate (Event __e, Rect __c, int __ci, int __k, List<int> __cols, Vector2 __mp, bool __in,
                    List<Rect> __hc, int[] __ord, Action<int[]> __ao, Action __sv, Action<int> __ck,
                    ref int __mc, ref float __mx, ref float __mdx, ref Vector2 __sc, ref Vector2 __scd, bool __v) {
                    return SR.TableColMove(__e, __c, __ci, __k, __cols, __mp, __in, __hc, __ord, __ao, __sv, __ck,
                        ref __mc, ref __mx, ref __mdx, ref __sc, ref __scd, __v);
                });
            } catch (Exception __sk) { Guard.Log("控件 sink 注册", __sk); }
            try {
                SRApi.Ctl.SetForceZhSink(delegate () { return SR.Ctl.ForceZh; }, delegate (bool v) { SR.Ctl.ForceZh = v; });
            } catch (Exception __fz) { Guard.Log("强制中文 sink", __fz); }
            try {
                SRApi.SetTableActionButtonsSink(delegate (Rect __r, SRApi.TableAction[] __a, float[] __w, ref int __h) { return SR.Ctl.TableActionButtons(__r, __a, __w, ref __h); });
                SRApi.SetTableActionColumnWidthSink(delegate (SRApi.TableAction[] __a) { return SR.Ctl.TableActionColumnWidth(__a); });
            } catch (Exception __ex) { Guard.Log("注入统一操作列按钮", __ex); }
            try { SRApi.SetTableAutoFitSink(delegate (float __b, float __y, float __m, ref float __bias) { return SR.Ctl.TableAutoFit(__b, __y, __m, ref __bias); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度自适应", __ex); }
            try { SRApi.SetTableAfterDrawSink(delegate (float __b, float __d, ref float __bias) { SR.Ctl.TableAfterDraw(__b, __d, ref __bias); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度校准", __ex); }
            try { SRApi.Ctl.SetTableHeightGripSink(delegate (ConfigEntryBase __e, Rect __g, float __ah, int __min, int __max) { SR.Ctl.TableHeightGrip(__e, __g, __ah, __min, __max); }); }
            catch (Exception __ex) { Guard.Log("注入表格高度拖拽", __ex); }
            try { SRApi.SetTableScrollbarForSink(delegate (SRApi.TableArgs __a, Event __e, ref bool __dr, ref float __gy, ref float __sy) { return SR.Ctl.TableScrollbarFor(__a, __e, ref __dr, ref __gy, ref __sy); }); }
            catch (Exception __ex) { Guard.Log("注入表格滚动条一体化", __ex); }
            // 竖向滚动条绘制（ImGui 配色）由核心统一提供。
            try { SRApi.SetTableScrollbarSink(delegate (Rect __t, float __ch, float __vh, float __sy, bool __dr, out Rect __th, bool __vis, float __al) { SR.Ctl.TableScrollbar(__t, __ch, __vh, __sy, __dr, out __th, __vis, __al); }); }
            catch (Exception __ex) { Guard.Log("注入表格滚动条", __ex); }
            // 无绑定提示的快捷键按钮：与 HotkeyButton 同一套右键绑键逻辑，只少提示行。
            try { SRApi.Ctl.SetHotkeyButtonNoHintSink(delegate (string __id, string __l, string __t, Action __a, float __w, float __h) { return SR.Ctl.HotkeyButtonNoHint(__id, __l, __t, __a, __w, __h); }); }
            catch (Exception __ex) { Guard.Log("注入无提示快捷键按钮", __ex); }
            _internalConfig = MainPlugin.Data != null ? MainPlugin.Data : plugin.Config; // 核心条目也走外置文件
            // 界面文案自注册：派发各分部文件，功能在 Initialize 里注册自己的文案。
            SelfRegCore();
            // 必须在任何 Config.Bind 之前把旧 cfg 的中文段/键改成英文并重新读盘，否则新条目取默认值且写盘又写回中文。
            try {
                // 两处文件都要迁：插件自带 cfg 与 MainPlugin.Data（条目实际 Bind 在后者），只迁前者会让外置旧段名永不改写。
                bool migratedAny = false;
                string pluginPath = plugin.Config.ConfigFilePath;
                if (ConfigMigration.Migrate(pluginPath)) migratedAny = true;
                string internalPath = _internalConfig != null ? _internalConfig.ConfigFilePath : null;
                if (!string.IsNullOrEmpty(internalPath)
                    && !string.Equals(internalPath, pluginPath, StringComparison.OrdinalIgnoreCase)) {
                    if (ConfigMigration.Migrate(internalPath)) migratedAny = true;
                }
                if (migratedAny) {
                    try { plugin.Config.Reload(); } catch (Exception __rp) { Guard.Log("配置迁移/重载插件 cfg", __rp); }
                    try { if (_internalConfig != null) _internalConfig.Reload(); } catch (Exception __ri) { Guard.Log("配置迁移/重载外置 cfg", __ri); }
                    ClearEntryCache();   // 键被改名 / 删掉了：条目缓存必须失效
                    SR.LogInfo("[T1] 已迁移旧版 cfg（插件 cfg + 外置 cfg）并重新读盘");
                }
            } catch (Exception __ex) { Guard.Log("T1 cfg 迁移/重读", __ex); }
            // 反射探针只做启动自检；NavHide 把 cfg 里残留的 [Reflection] 段从侧栏藏掉。
            try { NavHide("Reflection"); } catch (Exception __ex) { Guard.Log("Reflection 段隐藏", __ex); }
            _openKey = plugin.Config.Bind(Keys.Settings, Keys.OpenKey, KeyCode.Insert, "打开和关闭管理器用, 默认 Insert. 组合键的绑法: 点按钮, 按住 Shift 或 Ctrl 或 Alt, 再按主键.");
            RegisterKey("管理器-开关", _openKey, "press");
            RegisterFrameEnd("滑块残留状态清理", SliderFrameEndCleanup);   // 滑块残留状态清理挂在每帧收尾
            _blockInputEntry = plugin.Config.Bind(Keys.Settings, Keys.BlockInput, true, "管理器打开时冻结游戏输入, 免得误操作角色.（默认开启）");
            _uiScaleEntry = plugin.Config.Bind(Keys.Settings, Keys.UiScale, 1.3f, "整个界面放大多少, 1.0 到 1.8, 默认 1.3.");
            // 界面语言，运行时立即生效；外部模块页面始终中文。
            _langEntry = plugin.Config.Bind(Keys.Settings, Keys.Language, "English", "界面用哪种语言. 装了语言包 DLL 的话, 它也会自动出现在这个下拉里. 外部插件的页面始终是中文.");
            // 帧钩子剖析：默认关闭，避免频繁刷性能警告；排查卡顿时再开。
            _tickProf = plugin.Config.Bind(Keys.Settings, "Tick Profiling", false,
                "把每个帧钩子的耗时写进日志（默认关，排查卡顿时打开）。");
            // 切语言 / 装语言包都走 ApplyLanguage：解析 id 并让显示名缓存失效。
            _langEntry.SettingChanged += (s, e) => ApplyLanguage();
            ApplyLanguage();
            // 语言包外置：扫描 config/SR_UCH/lang/*.json 并注册；须在语言条目 Bind + ApplyLanguage 之后、
            // 各功能 Initialize 与首次渲染之前。
            try { LoadJsonLanguagePacks(); } catch (Exception __ex) { Guard.Log("加载 JSON 语言包", __ex); }
            // 地图总开关：开启后 M 键开地图，关闭时已开的地图立即关闭。
            // 侧栏栏目宽度：0 = 按文字宽度自动；拖侧栏右缘也可调整并写回。
            SidebarWEntry = plugin.Config.Bind(Keys.Settings, Keys.SidebarWidth, 0, "左边那一栏有多宽, 单位像素, 填 0 让它自己算. 也可以直接在窗口里拖侧栏右边缘.");
            SidebarOrderEntry = plugin.Config.Bind(Keys.Settings, Keys.SidebarOrder, "", "侧栏栏目的先后顺序, 用逗号隔开. 在设置页的栏目顺序那一块用上移下移按钮调, 调完自动写回来.");
            // 用配置版本号一次性清掉无效的 Sidebar Order 值。
            try {
                // 内部版本号绑在隐藏段，不出现在设置页
                NavHide("Internal");
                ConfigEntry<int> __schema = plugin.Config.Bind("Internal", "Sidebar Order Schema", 0, "内部用: 侧栏顺序的存储版本, 用来一次性清掉历史遗留的脏值.");
                // 第二个版本号：清掉栏目改名后残留的旧段名（由 ApplyCustomSidebarOrder 做改名映射与剔除）。
                if (__schema.Value < 3) {
                    SidebarOrderEntry.Value = "";
                    __schema.Value = 3;
                    SR.LogInfo("[SR] sidebar order schema v3: reset to Nav rank default order");
                }
            } catch (Exception __ex) { Guard.Log("清理侧栏顺序历史值", __ex); }
            SidebarW = SidebarWEntry.Value;
            SidebarWEntry.SettingChanged += (s, e) => SidebarW = SidebarWEntry.Value;
                    _allEnabledEntry = plugin.Config.Bind(Keys.Settings, Keys.AllEnabled, false, "整个 Mod 的总开关. 关掉以后所有内部功能都不生效, 但每个功能的开关值不会被改掉.\n默认是关的, 改动会自动保存, 下次启动保持上次的状态.");
            AllEnabled = _allEnabledEntry.Value;
            // 总开关关闭也走 FireDismissUi（与换场景、对局开始、EX 强制回主界面同理）。
            _allEnabledEntry.SettingChanged += (s, e) => {
                AllEnabled = _allEnabledEntry.Value;
                if (!AllEnabled) {
                    try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("总开关关闭时收起功能 UI", __ex); }
                }
            };
            // 外部插件默认启用；手动禁用的 GUID 记入 deny-list，重启后保持禁用。
            _disabledPluginsEntry = plugin.Config.Bind(Keys.Settings, Keys.DisabledPlugins, "", "不想让它跟着管理器一起出现的外部插件, 把 GUID 填进来, 用分号隔开. 没写进来的默认启用.");
            SRApi.RowFilters.Add(SettingsHiddenDisabledPlugins);
            SRApi.RowFilters.Add(SettingsHiddenWindowGeometry);
            BlockInput = _blockInputEntry.Value;
            _blockInputEntry.SettingChanged += (s, e) => {
                BlockInput = _blockInputEntry.Value;
                ApplyEventSystemGate();
            };
            // 窗口几何条目建一次并缓存；类型须与既有写法一致（宽高 int，X/Y float），否则 BepInEx 会另建条目。
            _winWidthEntry = plugin.Config.Bind(Keys.Settings, Keys.WinWidth, 720, "");
            _winHeightEntry = plugin.Config.Bind(Keys.Settings, Keys.WinHeight, 520, "");
            _winXEntry = plugin.Config.Bind(Keys.Settings, Keys.WinX, 30f, "");
            _winYEntry = plugin.Config.Bind(Keys.Settings, Keys.WinY, 30f, "");
            _winWidth = Mathf.Clamp(_winWidthEntry.Value, 400, 1600);
            _winHeight = Mathf.Clamp(_winHeightEntry.Value, 300, 1000);
            _winX = _winXEntry.Value;
            _winY = _winYEntry.Value;

            GameObject go = new GameObject("SR_UCHManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ManagerUI>();
            // 换场景：复位全部 UI 状态，否则管理器会冻结角色、停掉 GameControl 并禁用 EventSystem。
            SceneManager.activeSceneChanged += (a, b) => {
                _stylesReady = false;
                // 先通知功能（FireSceneChanged），再请它们收起自己的 UI（FireDismissUi）。
                try { FireSceneChanged(); } catch (Exception __ex) { Guard.Log("换场景通知功能", __ex); }
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("换场景时收起功能 UI", __ex); }
                if (_visible) {
                    _visible = false;
                    _capturing = null;
                    _editText.Clear();
                    _editOpen.Clear();
                    _enumOptions.Clear();   // 条目对象会重建
                SR.ColorPicker.ClearStates();   // 与 _editText / _editOpen 同批失效：配置重读后条目对象会重建，旧键会留成陈旧项
                    ApplyEventSystemGate(); // 重新启用游戏的 EventSystem
                }
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("SR.Core", __ex); }   // 清掉滑块 / 滚轮盾的跨帧残留
            };
            // 扫描不在此调用：其它 tweak 可能晚于本插件初始化，改在首帧 Update 跑。
            try { Harmony.CreateAndPatchAll(typeof(SR)); } catch (Exception ex) { SR.LogError("SR 补丁注册失败: " + ex.Message); }
            // 录键屏蔽表情系统的嵌套类不被 CreateAndPatchAll 递归发现，需单独注册。
            try { Harmony.CreateAndPatchAll(typeof(EmoteBlockPatch)); } catch (Exception ex) { SR.LogWarn("录键屏蔽表情系统 补丁注册失败: " + ex.Message); }
        }

        // 侧栏栏目自注册（Nav 顺序值越小越靠前，NavHide 不成栏目）；晚注册也立即生效。
        private static void RebuildSections(bool resetSelection) {

            try {
                _internalSections.Clear();
                List<string> registered = new List<string>();
                foreach (string s in _navOrder) if (!_navHidden.Contains(s)) registered.Add(s);
                registered.Sort(delegate(string a, string b) {
                    int r = _navRank[a].CompareTo(_navRank[b]);
                    if (r != 0) return r;
                    return _navOrder.IndexOf(a).CompareTo(_navOrder.IndexOf(b)); // 同顺序值：按注册先后
                });
                for (int i = 0; i < SRApi.FooterButtons.Count; i++) {
                    string fb = SRApi.FooterButtons[i] != null ? SRApi.FooterButtons[i].Id : null;
                    if (!string.IsNullOrEmpty(fb)) registered.Remove(fb);
                }
                for (int i = 0; i < registered.Count; i++) {
                    if (!_internalSections.Contains(registered[i])) _internalSections.Add(registered[i]);
                }
                // 兜底：既没注册也没 NavHide 的 cfg 段排最后
                if (_internalConfig != null) {
                    foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                        string sec = e.Definition.Section;
                        if (string.IsNullOrEmpty(sec)) sec = "(General)";
                        if (_navHidden.Contains(sec) || _navRank.ContainsKey(sec)) continue;
                        bool fbSec = false;
                        for (int k = 0; k < SRApi.FooterButtons.Count; k++) {
                            if (SRApi.FooterButtons[k] != null && SRApi.FooterButtons[k].Id == sec) { fbSec = true; break; }
                        }
                        if (fbSec) continue;
                        if (!_internalSections.Contains(sec)) _internalSections.Add(sec);
                    }
                }
                ApplyCustomSidebarOrder(); // 侧栏自定义顺序（拖动栏目后排的）
                if (resetSelection && _internalSections.Count > 0) _selectedInternalSection = _internalSections[0];
                // 选中项被移除 / 改名时回到第一个，避免空白页
                if (_internalSections.Count > 0 && !_internalSections.Contains(_selectedInternalSection))
                    _selectedInternalSection = _internalSections[0];
            } catch (Exception __ex) { Guard.Log("重建侧栏栏目", __ex); }
        }

        public static void RebuildSectionsPublic(bool resetSelection) { RebuildSections(resetSelection); }

        // 隐藏状态并入 Sidebar Order（隐藏项带 "!" 前缀），顺序与可见性共用一份真值。
        public static void SetSectionHidden(string sec, bool hidden) {
            try {
                if (string.IsNullOrEmpty(sec)) return;
                List<string> parts = new List<string>();
                bool found = false;
                for (int i = 0; i < _allSections.Count; i++) {
                    string nm = _allSections[i];
                    bool hid = _userHiddenSections.Contains(nm);
                    if (nm == sec) { hid = hidden; found = true; }
                    parts.Add(hid ? ("!" + nm) : nm);
                }
                if (!found) return;
                if (SidebarOrderEntry != null) SidebarOrderEntry.Value = string.Join(",", parts.ToArray());
                RebuildSections(false);   // 内部已有"选中项被移除 -> 回到第一个"的兜底
            } catch (Exception __ex) { Guard.Log("隐藏侧栏栏目", __ex); }
        }
        // Shift+右键还原：清空 Sidebar Order，恢复按 Nav rank 的默认顺序。
        public static void ResetSidebarOrder() {
            try {
                if (SidebarOrderEntry != null) SidebarOrderEntry.Value = "";
                _userHiddenSections.Clear();
                RebuildSections(false);
                SR.LogInfo("[SR] 已还原侧栏栏目顺序与隐藏状态（回到默认顺序）");
            } catch (Exception __ex) { Guard.Log("还原侧栏顺序", __ex); }
        }

        // 晚注册：首次扫描后注册栏目需立刻重建，否则静默不显示。
        private static void OnNavChanged(string sec) {
            if (!_scanned) return;   // 启动期注册：等 EnsureScanned 一次算完
            RebuildSections(false);
            try { ApplyCmOrder(); } catch (Exception __ex) { Guard.Log("晚注册后重排 Configuration Manager", __ex); }

        }

        public static void Nav(string sec, int rank) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Remove(sec);
            if (!_navRank.ContainsKey(sec)) _navOrder.Add(sec);
            _navRank[sec] = rank;
            OnNavChanged(sec);
        }

        // 正在打字登记：任一来源为真即认为在打字。
        private static readonly List<Func<bool>> _typingProviders = new List<Func<bool>>();
        // OnGUI 按键捕获：界面吞输入时比 Input.GetKeyDown 可靠；记录只留两帧，命中即消费。
        private static int _guiKeyAt;
        private static readonly List<KeyCode> _guiKeys = new List<KeyCode>();

        internal static bool GuiKeyThisFrame { get { return _guiKeys.Count > 0 && Time.frameCount - _guiKeyAt <= 1; } }

        internal static void RecordGuiKeyEvent(Event e) {
            try {
                if (e == null || e.type != EventType.KeyDown) return;
                KeyCode k = e.keyCode;
                if (k == KeyCode.None) return;
                // Input.GetKeyDown 已上报的键不再补记，否则同一次按下会触发两次（面板开了又关）。
                if (Input.GetKeyDown(k)) return;
                if (Time.frameCount - _guiKeyAt > 1) { _guiKeys.Clear(); _guiKeyAt = Time.frameCount; }
                if (!_guiKeys.Contains(k)) _guiKeys.Add(k);
            } catch (Exception __ex) { Guard.Log("记录 IMGUI 按键", __ex); }
        }

        internal static bool ConsumeGuiKey(KeyCode k) {
            try {
                if (_guiKeys.Count == 0) return false;
                if (Time.frameCount - _guiKeyAt > 2) { _guiKeys.Clear(); return false; }
                return _guiKeys.Remove(k);
            } catch (Exception __ex) { Guard.Log("读取 IMGUI 按键", __ex); return false; }
        }

        public static void RegisterTypingProvider(Func<bool> provider) {
            if (provider != null && !_typingProviders.Contains(provider)) _typingProviders.Add(provider);
        }
        public static bool Typing {
            get {
                for (int i = 0; i < _typingProviders.Count; i++) {
                    try { if (_typingProviders[i]()) return true; }
                    catch (Exception __ex) { Guard.Log("查询打字状态", __ex); }
                }
                return false;
            }
        }

        public static void NavHide(string sec) {
            if (string.IsNullOrEmpty(sec)) return;
            _navHidden.Add(sec);
            _navOrder.Remove(sec);
            _navRank.Remove(sec);
            OnNavChanged(sec);
        }

        private static void EnsureScanned() {
            if (_scanned) return;
            _scanned = true;
            bool foundSelf = false;   // 是否命中"自己的配置"：没命中要告警（见方法末尾）
            foreach (var kv in Chainloader.PluginInfos) {
                PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                PluginEntry pe = new PluginEntry {
                    guid = kv.Key,
                    name = info.Metadata != null && info.Metadata.Name != null ? info.Metadata.Name : kv.Key,
                    config = info.Instance.Config,
                    instance = info.Instance,
                    path = SafeAssemblyPath(info.Instance)
                };
                if (kv.Key == MainPlugin.PluginGuid) {
                    foundSelf = true;
                    _internalConfig = pe.config;
                    try { FlushPendingCombos(); } catch (Exception __ex) { Guard.Log("补建按键组合条目", __ex); }
                    // 本方法晚于全部 ITweak 与 EX 初始化，此处补跑一次迁移规则（幂等，无登记时零开销）。
                    try {
                        if (ConfigMigration.HasPending) {
                            string cfgPath = MainPlugin.Data != null ? MainPlugin.Data.ConfigFilePath : pe.config.ConfigFilePath;
                            if (!string.IsNullOrEmpty(cfgPath) && ConfigMigration.RunPending(cfgPath)) {
                                pe.config.Reload();
                                ClearEntryCache();
                                LogInfo("[T1b] 已应用功能自注册的配置迁移规则并重新读盘");
                            }
                        }
                    } catch (Exception __ex) { Guard.Log("功能自注册迁移规则", __ex); }
                    // 关掉 BepInEx 的 SaveOnConfigSet，改由 FlushConfig（最多 1 秒一次）+ 显式 Save 落盘。
                    try {
                        pe.config.SaveOnConfigSet = false;
                        pe.config.SettingChanged += OnAnySettingChanged;
                    } catch (Exception __ex) { Guard.Log("配置落盘节流初始化", __ex); }
                    try {
                        foreach (var kme in new Dictionary<ConfigEntryBase, ComboMod>(_keyMods)) {
                            SetKeyComboMod(kme.Key, kme.Value);
                        }
                    } catch (Exception __ex) { Guard.Log("重新应用按键修饰键", __ex); }
                    continue;
                }
                _externalPlugins.Add(pe);
            }
            _externalPlugins.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            RebuildSections(true);
            // 栏目归属检查须在 RebuildSections 之后（此时 _navRank / _navHidden 才完整）
            try { SectionFallbackAudit(); } catch (Exception __ex) { Guard.Log("兜底栏目审计", __ex); }
            // 迁移规则须与真正被绑定的键比对，必须晚于全部功能的 Bind
            try { MigrationRuleAudit(); } catch (Exception __ex) { Guard.Log("迁移规则冲突自检", __ex); }
            // 孤儿键检查同样必须在全部 Bind 之后（含补建的组合键条目），否则会大批误报。
            try { OrphanAudit(); } catch (Exception __ex) { Guard.Log("配置孤儿键自检", __ex); }
            if (_externalPlugins.Count > 0) _pluginKey = _externalPlugins[0].guid;
            ApplyDisabledPlugins(); // 重新禁用上次会话禁用的插件
            ApplyCmOrder();         // 让 Configuration Manager 的条目顺序与 SR 界面一致（侧栏顺序 + 栏目内绑定顺序）

            // 没命中自己的 GUID 就告警：此时组合键补建 / 回填都不跑，表现为组合键全丢、配置每次写盘。
            if (!foundSelf && !_warnedNoSelfGuid) {
                _warnedNoSelfGuid = true;
                try {
                    SR.LogWarn("[SR] 扫描插件表时没找到自己的 GUID（" + MainPlugin.PluginGuid
                        + "）-> 组合键补建与配置落盘节流未启用。请检查 GUID 是否与 [BepInPlugin] 一致。");
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
        }

        // 供 SRApi.CloseMenu 转发：功能页要开游戏自己的界面时先收起本面板。
        public static void CloseMenuExternal() { CloseMenu(); }

        // 供 SRApi.OpenMenu 转发：从游戏界面返回后把管理器重新打开。
        public static void OpenMenuExternal() {
            try {
                _visible = true;
                ApplyEventSystemGate();
                EnsureStyles();
            } catch (Exception __ex) { Guard.Log("OpenMenuExternal", __ex); }
            finally {
                // EnsureStyles 写了共享皮肤的选择色，此处不在绘制期，须马上还原。
                try { RestoreSkinSelectionColor(); } catch (Exception __rc) { Guard.Log("OpenMenuExternal/还原选中色", __rc); }
            }
        }

        private static void CloseMenu() {
            _visible = false;
            _capturing = null;
            ConfigFile[] __dcs = new ConfigFile[_dirtyConfigs.Count];
            _dirtyConfigs.CopyTo(__dcs);
            _dirty = false;
            // 关闭菜单时逐个保存，单个失败不影响其它。
            for (int __i = 0; __i < __dcs.Length; __i++) {
                ConfigFile __dc = __dcs[__i];
                // 成功才移除：写盘失败时保留脏标记，下一次 Tick 重试。
                try { __dc.Save(); _dirtyConfigs.Remove(__dc); }
                catch (Exception __se) { Guard.Log("关闭菜单时保存配置 " + (__dc.ConfigFilePath ?? "?"), __se); }
            }
            ApplyEventSystemGate();
            try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("CloseMenu", __ex); }   // 清掉滑块 / 滚轮盾的跨帧残留
        }

        // 总开关不在设置页重复成行（搜索栏右侧已有专用按钮）
        private static bool AllEnabledRowHidden(ConfigEntryBase e) {
            return !(e.Definition.Section == "Settings" && e.Definition.Key == "All Enabled");
        }

        private static void SelfRegCore() {
            SRApi.RowFilters.Add(AllEnabledRowHidden); // 设置页不重复显示本 Mod 总开关
            SR.LocSec("Settings", "设置", null);
            SR.NavHide("Settings"); // 设置页是独立顶层页（不占内部侧栏栏目）
            // 已删除功能的段名不能让残留段出现在侧栏；名单由 ConfigMigration 维护。
            for (int i = 0; i < ConfigMigration.DeletedSections.Length; i++)
                SR.NavHide(ConfigMigration.DeletedSections[i]);
            SR.LocSec("Home", "首页", null);
            SR.Nav("Home", 10); // 首页自己的侧栏栏目（顺序 10）
            SR.RegisterPage("Home", RenderHomePage); // 首页也走自绘页注册表（SR.Window 不做 if-else 分派）
            // 底栏入口先注册先显示：通知在功能 Initialize 注册，自检首帧注册，顺序为 设置 | 通知 | 自检。
            SR.RegisterTick("底栏-自检入口", delegate {
                if (_selfCheckFooterDone) return;
                _selfCheckFooterDone = true;
                try {
                    SR.RegisterPage("Self-check", "Audit", "自检", "Self-check", 10, SR.RenderAuditBlock);
                    SRApi.RegisterFooterButton("Self-check", "自检", "Self-check", null);
                } catch (Exception __fe) { Guard.Log("底栏-自检入口", __fe); }
            });
            SR.LocKey(Keys.Settings, Keys.UiScale, "界面缩放", null);
            // 登记无自带 LocDesc 的可见条目（是什么 / 怎么用 / 注意；默认值由核心追加）。
            SR.LocDesc("Chat", "Log Limit", "聊天记录条数\n最多保留多少条聊天消息\n超出后丢弃最旧的；仅本模组记录页", "Chat log limit\nhow many chat messages to keep\noldest dropped first; this mod's log page only");
            SR.LocDesc("Level", "Party Bomb", "派对盒炸弹\n开派对盒时按概率生成炸弹\n需要房主权限；仅派对盒阶段", "Party box bomb\nchance to spawn a bomb when a party box opens\nhost only; party-box phase only");
            SR.LocDesc("Level", "Party Bomb Type", "派对盒炸弹种类\n决定生成哪一种炸弹\n与游戏内炸弹类型一一对应", "Party box bomb type\nwhich bomb the party box spawns\nmatches the game's bomb types");
            SR.LocDesc("Level", "Refresh Party Box Key", "刷新派对盒按键\n按下重开派对盒并重新抽选\n未绑定时不生效", "Reroll party box key\npress to reopen the party box and reroll\nno effect while unbound");
            SR.LocDesc("Level", "Close Party Box Key", "关闭派对盒按键\n按下结束选择并进入游玩阶段\n未绑定时不生效", "Close party box key\npress to end picking and start playing\nno effect while unbound");
            SR.LocDesc("Online", "Filter Region", "区域筛选\n只列出指定区域的房间\n留空表示不限区域", "Region filter\nonly lobbies in the selected region are listed\nempty means any region");
            SR.LocDesc("Online", "Filter Mode", "模式筛选\n只列出指定模式的房间\n留空表示不限模式", "Mode filter\nonly lobbies of the selected mode are listed\nempty means any mode");
            SR.LocDesc("Online", "Filter Tag", "标签筛选\n只列出带指定标签的房间\n留空表示不限标签", "Tag filter\nonly lobbies with the selected tag are listed\nempty means any tag");
            SR.LocDesc("Online", "Filter State", "状态筛选\n按可加入状态过滤房间\n留空不限；join 可加入，full 已满，playing 对局中", "State filter\nfilters lobbies by joinability\nempty = any; join = open, full = full, playing = in a match");
            SR.LocDesc("Online", "Search Timeout", "搜索超时秒数\n等待房间列表的上限时间\n仅兜底用，一般不需要改", "Search timeout\nhow long to wait for the lobby list\nfallback only; normally no need to change");
            SR.LocDesc("Online", "Sort Column", "排序列索引\n房间列表按哪一列排序\n由表头操作维护，不建议手改", "Sort column index\nwhich column sorts the lobby list\nmaintained by the table header; avoid editing");
            SR.LocDesc("Online", "Sort Descending", "排序方向\n开启从大到小，关闭从小到大\n与排序列一起生效", "Sort direction\non = descending, off = ascending\nworks together with the sort column");
            SR.LocDesc("Chat Window", "Key Defaults v2", "内部标记：键位默认值已迁移\n仅配置迁移使用\n改动不会产生任何效果", "Internal flag: key defaults migrated\nconfig migration only\nchanging it has no effect");
            SR.LocDesc("Chat Window", "Defaults v3", "内部标记：字号默认值已迁移\n仅配置迁移使用\n改动不会产生任何效果", "Internal flag: font defaults migrated\nconfig migration only\nchanging it has no effect");
            SR.LocDesc(Keys.Settings, Keys.UiScale, "界面缩放\n整个界面按比例放大，1.0 为原始大小\n范围 0.5 到 2；字太小可适当调大", "UI scale\nscales the whole interface; 1.0 is the original size\nrange 0.5-2; increase it if the text is too small");
            // 核心设置项也走行扩展点，设置页因此只有一份滑块绘制代码。
            SRApi.RowSliders.Add(SettingsSliderRange);
            SR.RowComboOptions.Add(SettingsComboOptions);
            SR.LocKey(Keys.Settings, Keys.DisabledPlugins, "禁用的外部插件", null);
            SR.LocDesc(Keys.Settings, Keys.DisabledPlugins, "禁用的外部插件\n填写要禁用的插件 GUID，用分号分隔\n只影响外部插件加载；留空表示全部启用", "External plugins to disable\nenter the GUIDs to disable, separated by semicolons\naffects external plugin loading only; empty means all enabled");
            SR.LocKey(Keys.Settings, Keys.OpenKey, "打开键", null);
            SR.LocDesc(Keys.Settings, Keys.OpenKey, "打开键\n按下打开或关闭管理器\n点键框后按新键改绑，支持 Shift/Ctrl 组合键", "Open key\npress to open or close the manager\nclick the key box to rebind; Shift/Ctrl combos are supported");
            SR.LocKey(Keys.Settings, Keys.BlockInput, "冻结输入", null);
            SR.LocDesc(Keys.Settings, Keys.BlockInput, "冻结输入\n管理界面打开时冻结游戏输入\n只在你本地生效，不改对局规则", "Freeze input\nfreezes game input while the manager is open\nlocal only; does not change match rules");
            SR.LocKey(Keys.Settings, Keys.WinWidth, "窗口宽度", null);
            SR.LocDesc(Keys.Settings, Keys.WinWidth, "窗口宽度\n设置管理器窗口的宽度\n范围 400 到 1600", "Window width\nsets the manager window width\nrange 400-1600");
            SR.LocKey(Keys.Settings, Keys.WinHeight, "窗口高度", null);
            SR.LocDesc(Keys.Settings, Keys.WinHeight, "窗口高度\n设置管理器窗口的高度\n范围 300 到 1000", "Window height\nsets the manager window height\nrange 300-1000");
            SR.LocKey(Keys.Settings, Keys.WinX, "窗口X", null);
            SR.LocDesc(Keys.Settings, Keys.WinX, "窗口 X 坐标\n窗口左边距屏幕左边缘的像素距离\n坐标以屏幕左上角为原点", "Window X\nhorizontal pixel distance from the left screen edge\norigin is the top-left corner of the screen");
            SR.LocKey(Keys.Settings, Keys.WinY, "窗口Y", null);
            SR.LocDesc(Keys.Settings, Keys.WinY, "窗口 Y 坐标\n窗口上边距屏幕上边缘的像素距离\n坐标以屏幕左上角为原点", "Window Y\nvertical pixel distance from the top screen edge\norigin is the top-left corner of the screen");
            SR.LocKey(Keys.Settings, Keys.SidebarWidth, "栏目宽度", null);
            SR.LocDesc(Keys.Settings, Keys.SidebarWidth, "栏目宽度\n左侧栏目栏的宽度，0 = 自动计算\n范围 100 到 320；也可拖动侧栏右边缘调整", "Sidebar width\nwidth of the left sidebar, 0 = auto\nrange 100-320; you can also drag the sidebar right edge");
            SR.LocKey(Keys.Settings, Keys.Language, "界面语言", null);
        }

// 分区：Guard（可观测性：统一带日志的 try / catch 包装）
// 实现在契约层 SRApi.Guard，这里只转发。
        public static class Guard {
            public static void Try(string what, Action action) { SRApi.Guard.Try(what, action); }
            public static T Try<T>(string what, Func<T> action, T fallback) { return SRApi.Guard.Try(what, action, fallback); }
            public static void Log(string what, Exception ex) { SRApi.Guard.Log(what, ex); }
            public static string LastWhat { get { return SRApi.Guard.LastWhat; } }
            public static string LastError { get { return SRApi.Guard.LastError; } }
            public static int WarnCount { get { return SRApi.Guard.WarnCount; } }
        }

    }

}
