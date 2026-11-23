// HUD：阶段倒计时 / 回合进度 / 关卡 / 获胜目标 / 额外信息行。
// 阶段切换来自 GameControl.handleEvent 的 Harmony Postfix；本地只读，不发送网络消息。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.roundhud", "HUD", "1.0")]
    public partial class HUD : ITweak {
        private const string Sec = "HUD";   // 原名 Round HUD，2026-10 改名（迁移见 ConfigMigration）
        private const int WinScoreFallback = 250;

        // 布局：四角按固定边距贴边；Custom 用拖动后的运行期位置。
        public enum HudLayout {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
            Custom
        }

        // 显示方案：堆叠（每项一行）/ 平铺（用分隔符连成一行）。
        public enum DisplayMode {
            Stack,
            Inline
        }

        // 页面 2 的内容条目：开关键 + 颜色键 + 名称 / 说明；内容定义表见 HUD.Content.cs。

        private static ConfigEntry<string> _extra1;
        private static ConfigEntry<string> _extra2;
        private static ConfigEntry<string> _colPhase;
        private static ConfigEntry<string> _colCountdown;
        private static ConfigEntry<string> _colRound;
        private static ConfigEntry<string> _colLevel;
        private static ConfigEntry<string> _colWin;
        private static ConfigEntry<string> _colExtra1;
        private static ConfigEntry<string> _colExtra2;

        private static GameControl _gc;
        private static GameControl.GamePhase _phase = GameControl.GamePhase.NONE;
        private static float _phaseStart;
        private static bool _eventSeen;
        private static bool _hasGame;
        private static bool _wasEnabled;
        private static float _hudAlpha;         // 淡入淡出后的总透明度（ease-out）
        private static string _timerText = "00:00";
        private static int _warnLevel;
        private static string _roundText;
        private static string _levelText;
        private static string _winText;
        private static int _lastRound;
        // 真实回合：RoundNumber 在重载 / 刷新派对盒时会被额外 +1，故只在观察到结算记分板后接受增量。
        // 判定与混乱牌组共用 Pure/RoundTruth.cs，避免两套状态机漂移。
        private static SR_UCH.Pure.RoundTruthState _rs;
        private static bool _vsPlaceSeen;

        private static bool _inLobby;

        private static string _roundNoticeText;
        private static float _roundNoticeStart;
        private const float RoundNoticeDuration = 1.5f;

        private const float LineFadeTime = 0.15f;
        private static readonly string[] _lineLast = new string[24];
        private static readonly float[] _lineShownAt = new float[24];

        // 平铺模式：独立小段（各自背景 / 颜色），一行放不下自动换行
        private static readonly string[] _segText = new string[16];
        private static readonly Color[] _segColor = new Color[16];
        private static readonly float[] _segW = new float[16];
        private static readonly float[] _segWSmooth = new float[16];
        private static readonly int[] _segRow = new int[16];
        private static readonly float[] _segX = new float[16];
        private static int _segCount;
        private static int _segRowCount;
        private static float _segRowsW;

        private static float _hudAppear;        // 0..1 出现进度
        private static float _hudShownAt;       // 本轮开始出现的时间（stagger 基准）
        private static bool _wasVisible;

        private static string _extra1Text;
        private static string _extra2Text;

        private static FieldInfo _fPlacementTimer;
        private static FieldInfo _fLevelTimer;
        private static FieldInfo _fCountdownTimer;
        private static FieldInfo _fChallengeRunTime;
        private static FieldInfo _fChallengeRunStarted;

        private static GUIStyle _phaseStyle;
        private static GUIStyle _timerStyle;
        private static GUIStyle _smallStyle;
        private static GUIStyle _noticeStyle;
        private static GUIStyle _inlineStyle;  // 允许富文本（平铺模式下每段自己的颜色）
        private static GUIStyle _roundNoticeStyle; // 回合提示大号字
        private static int _styleFont = -1;
        private static Texture2D _white;
        private const int LinePhase = 0;
        private const int LineTimer = 1;
        private const int LineSmall = 2;
        private const int LineNotice = 3;
        private const int LineInline = 4;
        private const int LineRoundNotice = 5;
        private static readonly string[] _hudPlain = new string[24];
        private static readonly string[] _hudRich = new string[24];
        private static readonly int[] _hudKinds = new int[24];
        private static readonly Color[] _hudColors = new Color[24];
        private static readonly float[] _hudHeights = new float[24];
        private static int _heightFrame = -1;   // 行高平滑每帧只推进一次
        private static float _hudW = 260f;
        private static float _hudH = 150f;
        private static float _customX = 16f;
        private static float _customY = 16f;
        private static bool _dragging;
        private static float _dragOffX;
        private static float _dragOffY;

        private static readonly HashSet<string> _keysConfig = new HashSet<string> { "Enabled", "Fade", "Show Key" };
        private static readonly HashSet<string> _keysAppearance = new HashSet<string> { "Layout", "Font Size", "Opacity", "Background Opacity" };
        private static readonly HashSet<string> _keysBehavior = new HashSet<string> { "Warning Seconds", "Blink", "Round Notice" };
        private static readonly HashSet<string> _keysDisplay = new HashSet<string> { "Display Mode" };

        // ================= 配置取值 =================
        private static bool IsEnabled() {
            try { return SRApi.GateMaster && _enabled != null && _enabled.Value; } catch { return false; }
        }

        private static bool FadeOn() {
            try { return _fade != null && _fade.Value; } catch { return true; }
        }

        private static HudLayout LayoutValue() {
            try { return _layout != null ? _layout.Value : HudLayout.TopLeft; } catch { return HudLayout.TopLeft; }
        }

        private static int FontSize() {
            try { return _fontSize != null ? Mathf.Clamp(_fontSize.Value, 10, 48) : 18; } catch { return 18; }
        }

        private static float Opacity() {
            try { return _opacity != null ? Mathf.Clamp(_opacity.Value, 0.2f, 1f) : 0.85f; } catch { return 0.85f; }
        }

        private static float BgOpacity() {
            try { return _bgOpacity != null ? Mathf.Clamp(_bgOpacity.Value, 0f, 1f) : 0.55f; } catch { return 0.55f; }
        }

        private static int WarnSeconds() {
            try { return _warnSeconds != null ? Mathf.Clamp(_warnSeconds.Value, 5, 60) : 10; } catch { return 10; }
        }

        private static bool BlinkOn() {
            try { return _blink != null && _blink.Value; } catch { return false; }
        }

        private static DisplayMode DisplayModeValue() {
            try { return _displayMode != null ? _displayMode.Value : DisplayMode.Inline; } catch { return DisplayMode.Inline; }
        }

        private static bool RoundNoticeOn() {
            try { return _roundNotice != null && _roundNotice.Value; } catch { return true; }
        }

        private static bool ShowOn(ConfigEntry<bool> e) {
            try { return e != null && e.Value; } catch { return false; }
        }

        private static string EntryHex(ConfigEntry<string> e) {
            try { return e != null ? e.Value : null; } catch { return null; }
        }

        // 配置色：核心调色板有未落盘预览时用预览色。
        private static Color Col(ConfigEntry<string> e, Color fallback) {
            Color? preview = SRApi.Ctl.PreviewColor(e);
            if (preview.HasValue) return preview.Value;
            return SRApi.ColorHex.ParseHex(EntryHex(e), fallback);
        }

        // ================= 注册 =================
        private static void SelfReg() {
            SRApi.Ref.Override("VersusControl.placementTimer", "HUD（建造阶段剩余时间）");
            SRApi.Ref.Override("VersusControl.levelTimer", "HUD（对局已用时间）");
            SRApi.Ref.Override("VersusControl.countdownTimer", "HUD（决胜倒计时）");
            SRApi.Ref.Override("ChallengeControl.runTime", "HUD（挑战计时）");
            SRApi.Ref.Override("ChallengeControl.runStarted", "HUD（挑战是否已开始）");
            SRApi.LocEnum("TopLeft", "左上");
            SRApi.LocEnum("TopRight", "右上");
            SRApi.LocEnum("BottomLeft", "左下");
            SRApi.LocEnum("BottomRight", "右下");
            SRApi.LocEnum("Custom", "自定义");
            SRApi.LocEnum("Hold", "按住显示");
            SRApi.LocEnum("Toggle", "切换显示");
            SRApi.LocEnum("Stack", "堆叠");
            SRApi.LocEnum("Inline", "平铺");
            SRApi.LocSec(Sec, "HUD", "HUD");
            SRApi.Nav(Sec, 20); // 移动轨迹(20) 之后
            SRApi.RegisterPage(Sec, "hud", "HUD", "HUD", 10, RenderPage1);
            SRApi.RegisterPage(Sec, "content", "显示内容", "Content", 20, RenderPage2);

            Loc("Enabled", "显示HUD", "Show HUD",
                "HUD 总开关\n关闭后完全不绘制 HUD\n纯本地只读，不发送任何网络消息",
                "HUD master switch\nwhen off the HUD is not drawn at all\nlocal read-only; sends no network messages");
            Loc("Fade", "淡入淡出", "Fade",
                "淡入淡出\nHUD 显示或隐藏时平滑过渡\n关闭则立即显示或隐藏",
                "fade in/out\nsmoothly transitions the HUD when showing or hiding\nwhen off the HUD appears/disappears instantly");
            Loc("Show Key", "显示按键", "Show key",
                "显示按键\n按一下直接开/关「显示 HUD」复选框\n点右侧键框后按新键改绑，Esc 取消",
                "show key\ntoggles the Show HUD checkbox directly\nclick the key box, then press a new key (Esc cancels)");
            Loc("Layout", "布局", "Layout",
                "HUD 布局\n左上/右上/左下/右下按固定边距贴边\n自定义用拖动后的位置，仅本次会话有效",
                "HUD layout\nthe four corner modes stick to the screen edges with a fixed margin\ncustom uses the dragged position, kept for this session only");
            Loc("Font Size", "字体大小", "Font size",
                "HUD 字体大小\nHUD 文字的基础字号\n范围 10 到 48",
                "HUD font size\nbase font size of the HUD text\nrange 10-48");
            Loc("Opacity", "不透明度", "Text opacity",
                "HUD 文字不透明度\n控制所有 HUD 文字的不透明度\n范围 0.2 到 1，1 为完全不透明",
                "HUD text opacity\nopacity of all HUD text\nrange 0.2-1, 1 = fully opaque");
            Loc("Background Opacity", "背景透明度", "Background opacity",
                "HUD 背景透明度\nHUD 半透明底色的透明度\n范围 0 到 1，0 为完全透明",
                "HUD background opacity\nopacity of the HUD's translucent background\nrange 0-1, 0 = fully transparent");
            Loc("Warning Seconds", "警告秒数", "Warning seconds",
                "警告秒数\n倒计时剩余该秒数时文字变黄，最后 5 秒变红\n范围 5 到 60",
                "warning seconds\nthe countdown turns yellow when less time than this remains and red in the last 5 seconds\nrange 5-60");
            Loc("Blink", "闪烁提醒", "Blink",
                "闪烁提醒\n最后 5 秒时倒计时文字闪烁\n关闭则只变红不闪",
                "blink warning\nblinks the countdown text during the final 5 seconds\nwhen off it only turns red");
            Loc("Round Notice", "回合提示", "Round notice",
                "回合提示\n回合数变化时以放大闪烁动画显示当前回合（约 1.5 秒）\n关闭则不显示",
                "round notice\nshows the current round with a scaled blinking animation when the round changes (about 1.5 seconds)\nwhen off it is hidden");

            Loc("Display Mode", "显示方案", "Display mode",
                "显示方案\n堆叠 = 每个内容单独一行；平铺 = 每个内容独立小段横向排列，放不下自动换行\n回合提示始终单独占行",
                "display mode\nStack = one line per content item; Inline = each item is a small tile laid out horizontally and wraps when needed\nthe round notice always uses its own line");
            for (int i = 0; i < _content.Length; i++) {
                ContentDef d = _content[i];
                Loc(d.Key, d.LabelZh, d.LabelEn, d.DescZh, d.DescEn);
                TweakCfg.Entry(Sec, d.ColorKey, d.LabelZh + "颜色", d.LabelEn + " color",
                    d.LabelZh + "的文字颜色\n十六进制颜色值，例如 #FFFFFF\n点色块开调色板；点名称还原默认色",
                    "text color for " + d.LabelEn.ToLowerInvariant() + "\nhex color value, for example #FFFFFF\nclick the swatch to open the picker; click the name to reset");
            }
        }

        private static void Loc(string key, string zh, string en, string descZh, string descEn) {
            TweakCfg.Entry(Sec, key, zh, en,
                descZh,
                descEn);
        }

        // ================= 设置页 =================
        private static bool IsSec(ConfigEntryBase e) {
            try { return e != null && e.Definition.Section == Sec; } catch { return false; }
        }

        private static bool InSet(ConfigEntryBase e, HashSet<string> set) {
            try { return e != null && e.Definition.Section == Sec && set.Contains(e.Definition.Key); } catch { return false; }
        }

        private static bool RowConfig(ConfigEntryBase e) { return InSet(e, _keysConfig); }
        private static bool RowAppearance(ConfigEntryBase e) { return InSet(e, _keysAppearance); }
        private static bool RowBehavior(ConfigEntryBase e) { return InSet(e, _keysBehavior); }
        private static bool RowDisplay(ConfigEntryBase e) { return InSet(e, _keysDisplay); }

        // 行可见性：颜色键与显示方式都不单独成行。
        private static bool RowVisible(ConfigEntryBase e) {
            try {
                if (!IsSec(e)) return true;
                string k = e.Definition.Key;
                if (k.StartsWith("Color ")) return false;
                return true;
            } catch { return true; }
        }

        private static string RowExtra(ConfigEntryBase e) {
            try {
                if (!IsSec(e)) return null;
                return null;   // 显示方式已删除：快捷键直接开关「显示 HUD」
            } catch { return null; }
        }

        private static readonly Func<ConfigEntryBase, bool> _rowConfig = RowConfig;
        private static readonly Func<ConfigEntryBase, bool> _rowAppearance = RowAppearance;
        private static readonly Func<ConfigEntryBase, bool> _rowBehavior = RowBehavior;
        private static readonly Func<ConfigEntryBase, bool> _rowDisplay = RowDisplay;





        // 一行：标签 + 开关 / 文本框 + 右侧色块；点名称还原默认色，点色块展开调色板。
        private static void RenderContentRow(ContentDef d) {
            try {
                ConfigEntryBase show = SRApi.Ctl.FindEntry(Sec, d.Key);
                ConfigEntryBase color = SRApi.Ctl.FindEntry(Sec, d.ColorKey);
                string tip = SRApi.T(d.DescZh, d.DescEn) + "\n" + SRApi.Tid("b91a694f3e", "点击名称还原默认色", "Click the name to reset to the default color");
                GUIContent label = new GUIContent(SRApi.T(d.LabelZh, d.LabelEn), tip);
                float h = SRApi.Ctl.Sc(26f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(150f)), GUILayout.Height(h))) {
                    SRApi.Ctl.ResetColor(color);
                }
                if (show != null) SRApi.Ctl.RenderControl(show);
                GUILayout.Space(SRApi.Ctl.Sc(6f));
                Rect swatch = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(64f), h);
                SRApi.Ctl.ColorSwatch(swatch, color);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                SRApi.Ctl.ColorPanel(color);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 内容行", __ex); }
        }

        // 滑块范围注册：字体大小 / 文字与背景透明度 / 警告秒数。
        private static bool IsKey(ConfigEntryBase e, string key) {
            try { return e != null && e.Definition.Section == Sec && e.Definition.Key == key; } catch { return false; }
        }

        private static bool FontSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 10f; max = 48f; fmt = "0";
            return IsKey(e, "Font Size");
        }

        private static bool OpacitySlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0.2f; max = 1f; fmt = "0.00";
            return IsKey(e, "Opacity");
        }

        private static bool BgOpacitySlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 1f; fmt = "0.00";
            return IsKey(e, "Background Opacity");
        }

        private static bool WarnSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 5f; max = 60f; fmt = "0";
            return IsKey(e, "Warning Seconds");
        }

        // ================= 初始化 =================
        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                ConfigFile cfg = plugin.Config;
                _enabled = cfg.Bind(Sec, "Enabled", false, "显示HUD：总开关（默认关闭）。");
                _fade = cfg.Bind(Sec, "Fade", true, "淡入淡出：HUD 显示/隐藏时平滑过渡。");
                _showKey = cfg.Bind(Sec, "Show Key", KeyCode.None, "显示按键：用于显示 HUD 的按键（默认不绑定）。");
                _layout = cfg.Bind(Sec, "Layout", HudLayout.TopRight, "HUD 布局：四角贴边或自定义拖动位置。");
                _fontSize = cfg.Bind(Sec, "Font Size", 18,
                    new ConfigDescription("字体大小（10-48）。", new AcceptableValueRange<int>(10, 48)));
                _opacity = cfg.Bind(Sec, "Opacity", 0.85f,
                    new ConfigDescription("文字不透明度（0.2-1）。", new AcceptableValueRange<float>(0.2f, 1f)));
                _bgOpacity = cfg.Bind(Sec, "Background Opacity", 0.5f,
                    new ConfigDescription("背景透明度（0-1，0 为完全透明）。", new AcceptableValueRange<float>(0f, 1f)));
                _warnSeconds = cfg.Bind(Sec, "Warning Seconds", 10,
                    new ConfigDescription("警告秒数（5-60）。", new AcceptableValueRange<int>(5, 60)));
                _blink = cfg.Bind(Sec, "Blink", true, "闪烁提醒：最后 5 秒倒计时文字闪烁。");
                _roundNotice = cfg.Bind(Sec, "Round Notice", true, "回合提示：回合数变化时以放大闪烁动画显示当前回合。");
                _displayMode = cfg.Bind(Sec, "Display Mode", DisplayMode.Inline, "显示方案：平铺（小段横向排列，自动换行，默认）/ 堆叠（每项一行）。");
                _showPhase = cfg.Bind(Sec, "Show Phase", true, "显示阶段名。");
                _colPhase = cfg.Bind(Sec, "Color Phase", "#FFFFFF", "阶段名文字颜色（#RRGGBB）。");
                _showCountdown = cfg.Bind(Sec, "Show Countdown", true, "显示倒计时。");
                _colCountdown = cfg.Bind(Sec, "Color Countdown", "#FFFFFF", "倒计时文字颜色（#RRGGBB）。");
                _showRound = cfg.Bind(Sec, "Show Round", true, "显示回合进度。");
                _colRound = cfg.Bind(Sec, "Color Round", "#FFFFFF", "回合进度文字颜色（#RRGGBB）。");
                _showLevel = cfg.Bind(Sec, "Show Level", false, "显示关卡/模式（默认关闭）。");
                _colLevel = cfg.Bind(Sec, "Color Level", "#FFFFFF", "关卡/模式文字颜色（#RRGGBB）。");
                _showWin = cfg.Bind(Sec, "Show Win Score", false, "显示获胜目标（默认关闭）。");
                _colWin = cfg.Bind(Sec, "Color Win Score", "#FFFFFF", "获胜文字颜色（#RRGGBB）。");
                _extra1 = cfg.Bind(Sec, "Extra Line 1", "", "额外信息行 1：按原文显示，留空则不显示。");
                _colExtra1 = cfg.Bind(Sec, "Color Extra 1", "#FFFFFF", "额外信息行 1 文字颜色（#RRGGBB）。");
                _extra2 = cfg.Bind(Sec, "Extra Line 2", "", "额外信息行 2：按原文显示，留空则不显示。");
                _colExtra2 = cfg.Bind(Sec, "Color Extra 2", "#FFFFFF", "额外信息行 2 文字颜色（#RRGGBB）。");

                // 行扩展点：隐藏颜色 / 显示方式行、四个滑块。
                try { SRApi.RowFilters.Add(RowVisible); } catch (Exception __ex) { SRApi.Guard.Log("HUD 行过滤", __ex); }
                try { SRApi.RowExtras.Add(RowExtra); } catch (Exception __ex) { SRApi.Guard.Log("HUD 行附加", __ex); }
                try {
                    SRApi.RowSliders.Add(FontSlider);
                    SRApi.RowSliders.Add(OpacitySlider);
                    SRApi.RowSliders.Add(BgOpacitySlider);
                    SRApi.RowSliders.Add(WarnSlider);
                } catch (Exception __ex) { SRApi.Guard.Log("HUD 滑块注册", __ex); }

                try {
                    SRApi.Migration.Remove(Sec,
                        "Pos X", "Pos Y", "Edge Margin", "Show Scores", "Show Players", "Show Eliminations", "Beep", "Beep Volume",
                        "Phase Notice", "Color Player Panel");
                } catch (Exception __ex) { SRApi.Guard.Log("HUD 清理旧键", __ex); }

                try {
                    SRApi.RegisterKey("HUD-显示键", _showKey, "press", delegate {
                        try {
                            if (!SRApi.GateMaster || _enabled == null) return;
                            _enabled.Value = !_enabled.Value;
                            SRApi.Notify(_enabled.Value ? SRApi.Tid("d2bf6159b9", "HUD：开", "HUD: ON") : SRApi.Tid("2dca0b5d56", "HUD：关", "HUD: OFF"));
                        } catch (Exception __hk) { SRApi.Guard.Log("HUD/显示键", __hk); }
                    });
                }
                catch (Exception __ex) { SRApi.Guard.Log("HUD 键位注册", __ex); }

                SRApi.RegisterTick("HUD", Tick);
                SRApi.RegisterSceneHook(ResetStateSafe);

                GameObject go = new GameObject("SR_UCH_HUD");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<HudComponent>();

                // 本类自带 [HarmonyPatch] 但 ITweak 为反射发现，须手动 CreateAndPatchAll；失败只丢本类功能。
                try { SRApi.RegisterPatch(typeof(HUD)); }
                catch (Exception e) { SRApi.LogError("HUD 补丁注册失败: " + e.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 初始化", __ex); }
        }

        // ================= 阶段事件 =================

        // ================= 每帧状态 =================

        // 显示按键 + 淡入淡出：设置窗口打开时强制可见。










        // 回合进度格式「回合：当前/最大」；唯一回合字段是 RoundNumber，须只接受结算后的增量并钳制上限。

        // 关卡名：自定义快照优先 snapshotName，只有代码时显示「自定义关卡」。

        // 关卡 / 模式行：「关卡名 · 模式」，不带前缀。



        // 树屋 / 大厅：只保留标题「大厅」与当前投票关卡。







        // 挑战模式计时：读 ChallengeControl 私有字段（反射缓存，读不到返回 -1/false）。





        private static void ResetGameState() {
            _gc = null;
            // 不清 _phase：换场景清零会让 HUD 闪烁并抹掉回合提示。
            _phaseStart = 0f;
            _eventSeen = false;
            _timerText = "00:00";
            _warnLevel = 0;
            _roundText = null;
            _levelText = null;
            _winText = null;
            _roundNoticeStart = 0f;
            _vsPlaceSeen = false;
            _inLobby = false;
            
            for (int i = 0; i < _lineLast.Length; i++) { _lineLast[i] = null; _lineShownAt[i] = 0f; }
            for (int i = 0; i < _hudHeights.Length; i++) _hudHeights[i] = 0f;
            for (int i = 0; i < _segWSmooth.Length; i++) _segWSmooth[i] = 0f;
            _heightFrame = -1;
            SR_UCH.Pure.RoundTruth.ClearTally(ref _rs);
            _dragging = false;
        }

        private static void ResetState() {
            ResetGameState();
        }

        private static void ResetStateSafe() {
            try { ResetState(); } catch (Exception __ex) { SRApi.Guard.Log("HUD 重置", __ex); }
        }

        // ================= HUD 绘制 =================
        private sealed class HudComponent : MonoBehaviour {
            private void OnGUI() {
                try { DrawHud(); } catch (Exception __ex) { SRApi.Guard.Log("HUD 绘制", __ex); }
            }
        }

        // 纯色绘制用白贴图，必须用 GUI.color 着色。
        private static void EnsureWhite() {
            if (_white != null) return;
            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _white.hideFlags = HideFlags.HideAndDontSave;
        }


        private static void EnsureStyles() {
            EnsureWhite();
            if (_phaseStyle == null) {
                GUIStyle src = SRApi.Ctl.Label;
                _phaseStyle = (src != null) ? new GUIStyle(src) : new GUIStyle(GUI.skin.label);
                _phaseStyle.wordWrap = false;
                _phaseStyle.richText = false;
                _phaseStyle.fontStyle = FontStyle.Bold;
                _phaseStyle.alignment = TextAnchor.MiddleLeft;
                _phaseStyle.padding = new RectOffset(0, 0, 0, 0);
                _phaseStyle.margin = new RectOffset(0, 0, 0, 0);
                _timerStyle = new GUIStyle(_phaseStyle);
                _smallStyle = new GUIStyle(_phaseStyle);
                _smallStyle.fontStyle = FontStyle.Normal;
                _smallStyle.richText = false;
                _noticeStyle = new GUIStyle(_smallStyle);
                _noticeStyle.fontStyle = FontStyle.Italic;
                _inlineStyle = new GUIStyle(_smallStyle);
                _inlineStyle.richText = true;
                _roundNoticeStyle = new GUIStyle(_phaseStyle);
                _roundNoticeStyle.alignment = TextAnchor.MiddleCenter;
            }
            int baseFont = FontSize();
            float scale = SRApi.Ctl.Sc(1f);
            if (scale <= 0f) scale = 1f;
            int f = Mathf.Max(8, Mathf.RoundToInt((float)baseFont * scale));
            if (f != _styleFont) {
                _styleFont = f;
                _phaseStyle.fontSize = f;
                _timerStyle.fontSize = Mathf.Max(f + 4, Mathf.RoundToInt((float)f * 1.7f));
                _smallStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt((float)f * 0.85f));
                _noticeStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt((float)f * 0.9f));
                _inlineStyle.fontSize = _smallStyle.fontSize;
                _roundNoticeStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt((float)f * 1.7f));
            }
        }

        private static void AddLineFade(ref int n, string plain, string rich, int kind, Color col) {
            if (string.IsNullOrEmpty(plain)) return;
            if (n < _hudPlain.Length) {
                _hudPlain[n] = plain;
                _hudRich[n] = string.IsNullOrEmpty(rich) ? plain : rich;
                _hudKinds[n] = kind;
                _hudColors[n] = col;
            }
            n++;
        }

        private static GUIStyle LineStyle(int kind) {
            switch (kind) {
            case LinePhase: return _phaseStyle;
            case LineTimer: return _timerStyle;
            case LineNotice: return _noticeStyle;
            case LineInline: return _inlineStyle;
            case LineRoundNotice: return _roundNoticeStyle;
            default: return _smallStyle;
            }
        }

        // 行变化淡入：文本变化后 0.15s 从 0 → 1。
        private static float SlotFadeChange(int slot, string cur) {
            if (slot < 0 || slot >= _lineLast.Length) return 1f;
            string last = _lineLast[slot];
            if (!string.Equals(last, cur)) { _lineLast[slot] = cur; _lineShownAt[slot] = Time.unscaledTime; }
            if (string.IsNullOrEmpty(cur)) return 1f;
            return Mathf.Clamp01((Time.unscaledTime - _lineShownAt[slot]) / LineFadeTime);
        }

        // 只在从无到有时淡入（避免倒计时闪烁）。
        private static float SlotFadeAppear(int slot, string cur) {
            if (slot < 0 || slot >= _lineLast.Length) return 1f;
            string last = _lineLast[slot];
            if (!string.Equals(last, cur)) {
                if (string.IsNullOrEmpty(last) && !string.IsNullOrEmpty(cur)) _lineShownAt[slot] = Time.unscaledTime;
                _lineLast[slot] = cur;
            }
            if (string.IsNullOrEmpty(cur)) return 1f;
            return Mathf.Clamp01((Time.unscaledTime - _lineShownAt[slot]) / LineFadeTime);
        }

        private static Color CountdownColor(Color baseCol) {
            if (_warnLevel == 2) return new Color(1f, 0.3f, 0.25f, 1f);
            if (_warnLevel == 1) return new Color(1f, 0.85f, 0.2f, 1f);
            return baseCol;
        }

        private static void AddSeg(Color c, string text) {
            if (string.IsNullOrEmpty(text) || _segCount >= _segText.Length) return;
            _segText[_segCount] = text;
            _segColor[_segCount] = c;
            _segCount++;
        }

        // 回合提示：大号字 + 交替闪烁 + 末尾淡出，约 1.5 秒。
        private static void AddRoundNoticeLine(ref int n, float textA) {
            if (!RoundNoticeOn() || string.IsNullOrEmpty(_roundNoticeText)) return;
            float t = (Time.unscaledTime - _roundNoticeStart) / RoundNoticeDuration;
            if (t < 0f || t >= 1f) return;
            bool bright = (Mathf.FloorToInt(Time.unscaledTime * 8f) & 1) == 0;
            float fade = t < 0.7f ? 1f : Mathf.Clamp01(1f - (t - 0.7f) / 0.3f);
            if (_roundNoticeStyle != null) {
                float baseSize = (float)FontSize() * SRApi.Ctl.Sc(1.7f);
                _roundNoticeStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(baseSize));
            }
            Color c = bright ? Color.white : new Color(1f, 0.85f, 0.3f, 1f);
            AddLineFade(ref n, _roundNoticeText, null, LineRoundNotice, WithA(c, textA * fade));
        }



        private static Color WithA(Color c, float a) { return new Color(c.r, c.g, c.b, Mathf.Clamp01(a)); }

        private static void LayoutPosition(float w, float h, out float x, out float y) {
            float sw = (float)Screen.width;
            float sh = (float)Screen.height;
            switch (LayoutValue()) {
            case HudLayout.TopRight:
                x = sw - w; y = 0f; break;
            case HudLayout.BottomLeft:
                x = 0f; y = sh - h; break;
            case HudLayout.BottomRight:
                x = sw - w; y = sh - h; break;
            case HudLayout.Custom:
                x = _customX; y = _customY; break;
            case HudLayout.TopLeft:
            default:
                x = 0f; y = 0f; break;
            }
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - w));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - h));
        }

        private static readonly GUIContent _measureContent = new GUIContent();
        private static float Measure(string text, GUIStyle style) {
            if (string.IsNullOrEmpty(text) || style == null) return 0f;
            try { _measureContent.text = text; return style.CalcSize(_measureContent).x; } catch { return 0f; }
        }

        private static void HandleDrag(Rect panel) {
            Event e = Event.current;
            if (e == null) return;
            bool canDrag = SRApi.UiOpen && LayoutValue() == HudLayout.Custom;
            if (!canDrag) { _dragging = false; return; }
            switch (e.type) {
            case EventType.MouseDown:
                if (e.button == 0 && panel.Contains(e.mousePosition)) {
                    _dragging = true;
                    _dragOffX = e.mousePosition.x - panel.x;
                    _dragOffY = e.mousePosition.y - panel.y;
                    e.Use();
                }
                break;
            case EventType.MouseDrag:
                if (_dragging) {
                    _customX = Mathf.Clamp(e.mousePosition.x - _dragOffX, 0f, Mathf.Max(0f, (float)Screen.width - panel.width));
                    _customY = Mathf.Clamp(e.mousePosition.y - _dragOffY, 0f, Mathf.Max(0f, (float)Screen.height - panel.height));
                    e.Use();
                }
                break;
            case EventType.MouseUp:
                if (_dragging && e.button == 0) {
                    _dragging = false;
                    e.Use();
                }
                break;
            }
        }
    }
}

// 回合 HUD · 设置页（RenderPage1 / RenderPage2）。

namespace SR_UCH.Tweaks {
    public partial class HUD {
        private static void RenderPage1() {
            try {
                List<ConfigEntryBase> list = SRApi.SectionEntries(Sec);
                if (list == null || list.Count == 0) { GUILayout.Label(SRApi.Tid("2be7d7a6da", "（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
                SRApi.Ctl.SectionTitle(SRApi.Tid("f1848d2d65", "配置", "Config"));
                SRApi.RenderSectionEntries(Sec, _rowConfig);
                SRApi.Ctl.SectionTitle(SRApi.Tid("cab005b3b5", "外观", "Appearance"));
                SRApi.RenderSectionEntries(Sec, _rowAppearance);
                SRApi.Ctl.SectionTitle(SRApi.Tid("f4cbef6a47", "行为", "Behavior"));
                SRApi.RenderSectionEntries(Sec, _rowBehavior);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 设置页", __ex); }
        }

        private static void RenderPage2() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.Tid("b141cce614", "显示方案", "Display mode"));
                SRApi.RenderSectionEntries(Sec, _rowDisplay);
                SRApi.Ctl.SectionTitle(SRApi.Tid("ea74f0d293", "一般", "General"));
                for (int i = 0; i <= 4; i++) RenderContentRow(_content[i]);
                SRApi.Ctl.SectionTitle(SRApi.Tid("d9c5dacd95", "其它", "Other"));
                RenderContentRow(_content[5]);
                RenderContentRow(_content[6]);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 显示内容页", __ex); }
        }
    }
}
