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
    // 本类自带 [HarmonyPatch]，由 ITweak 反射发现后手动 CreateAndPatchAll。
    [SrFeature("sr.roundhud", "HUD", "1.0")]
    public class HUD : ITweak {
        private const string Sec = "Round HUD";
        private const int WinScoreFallback = 250;

        // 布局：四角按固定边距贴边；Custom 用拖动后的运行期位置（会话内有效）。
        public enum HudLayout {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
            Custom
        }

        // 显示键的工作方式。
        public enum ShowMode {
            Hold,
            Toggle
        }

        // 显示方案：堆叠（每项一行）/ 平铺（全部内容用分隔符连成一行）。
        public enum DisplayMode {
            Stack,
            Inline
        }

        // 页面 2 的内容条目：开关键 + 颜色键 + 名称/说明（说明 3 行，与 LocDesc 共用）。
        private sealed class ContentDef {
            public string Key, ColorKey, LabelZh, LabelEn, DescZh, DescEn;
        }

        private static readonly ContentDef[] _content = new ContentDef[] {
            new ContentDef {
                Key = "Show Phase", ColorKey = "Color Phase",
                LabelZh = "阶段名", LabelEn = "Phase name",
                DescZh = "阶段名\n在 HUD 显示当前阶段（建造、游玩、决胜）\n关闭后隐藏该行",
                DescEn = "Phase name\nshows the current phase in the HUD (build, play, sudden death)\nhidden when off"
            },
            new ContentDef {
                Key = "Show Countdown", ColorKey = "Color Countdown",
                LabelZh = "倒计时", LabelEn = "Countdown",
                DescZh = "倒计时\n显示阶段倒计时，读不到时长时显示已用时间\n关闭后隐藏该行",
                DescEn = "Countdown\nphase countdown, or elapsed time when no duration is known\nhidden when off"
            },
            new ContentDef {
                Key = "Show Round", ColorKey = "Color Round",
                LabelZh = "回合进度", LabelEn = "Round progress",
                DescZh = "回合进度\n普通模式显示「回合：当前/最大」，挑战模式显示「重试：N」\n取不到回合信息时隐藏该行",
                DescEn = "Round progress\nround current/max, or retry count in challenge mode\nhidden when round info is unavailable"
            },
            new ContentDef {
                Key = "Show Level", ColorKey = "Color Level",
                LabelZh = "关卡/模式", LabelEn = "Level / mode",
                DescZh = "关卡/模式\n显示「关卡名 · 模式」，不带前缀\n取不到时隐藏该行",
                DescEn = "Level / mode\nshows level name and mode without a prefix\nhidden when unavailable"
            },
            new ContentDef {
                Key = "Show Win Score", ColorKey = "Color Win Score",
                LabelZh = "获胜", LabelEn = "Win",
                DescZh = "获胜目标\n显示游戏设置里的获胜目标分\n读不到时显示 250；关闭后隐藏该行",
                DescEn = "Win target\nwinning score from the game settings\nfalls back to 250; hidden when off"
            },
            new ContentDef {
                Key = "Extra Line 1", ColorKey = "Color Extra 1",
                LabelZh = "额外信息行 1", LabelEn = "Extra line 1",
                DescZh = "额外信息行 1\n自定义文本，按原文显示（不翻译）\n留空则不显示",
                DescEn = "Extra line 1\ncustom text shown as typed (not translated)\nhidden when empty"
            },
            new ContentDef {
                Key = "Extra Line 2", ColorKey = "Color Extra 2",
                LabelZh = "额外信息行 2", LabelEn = "Extra line 2",
                DescZh = "额外信息行 2\n自定义文本，按原文显示（不翻译）\n留空则不显示",
                DescEn = "Extra line 2\ncustom text shown as typed (not translated)\nhidden when empty"
            }
        };

        // ---- 配置项 ----
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _fade;
        private static ConfigEntry<KeyCode> _showKey;
        private static ConfigEntry<ShowMode> _showMode;
        private static ConfigEntry<HudLayout> _layout;
        private static ConfigEntry<int> _fontSize;
        private static ConfigEntry<float> _opacity;
        private static ConfigEntry<float> _bgOpacity;
        private static ConfigEntry<int> _warnSeconds;
        private static ConfigEntry<bool> _blink;
        private static ConfigEntry<bool> _roundNotice;
        private static ConfigEntry<DisplayMode> _displayMode;
        private static ConfigEntry<bool> _showPhase;
        private static ConfigEntry<bool> _showCountdown;
        private static ConfigEntry<bool> _showRound;
        private static ConfigEntry<bool> _showLevel;
        private static ConfigEntry<bool> _showWin;
        private static ConfigEntry<string> _extra1;
        private static ConfigEntry<string> _extra2;
        private static ConfigEntry<string> _colPhase;
        private static ConfigEntry<string> _colCountdown;
        private static ConfigEntry<string> _colRound;
        private static ConfigEntry<string> _colLevel;
        private static ConfigEntry<string> _colWin;
        private static ConfigEntry<string> _colExtra1;
        private static ConfigEntry<string> _colExtra2;

        // ---- 阶段/对局状态 ----
        private static GameControl _gc;
        private static GameControl.GamePhase _phase = GameControl.GamePhase.NONE;
        private static float _phaseStart;
        private static bool _eventSeen;
        private static bool _hasGame;
        private static bool _wasEnabled;
        private static bool _toggleOn;          // 切换显示模式的运行期状态
        private static float _hudAlpha;         // 淡入淡出后的总透明度（ease-out）
        private static string _timerText = "00:00";
        private static int _warnLevel;
        private static string _roundText;
        private static string _levelText;
        private static string _winText;
        private static int _lastRound;
        // 真实回合：GameControl.RoundNumber 会在重载/刷新派对盒时被额外 +1，用“结算记分板”事件
        // 作为“确实进入下一回合”的证据；未确认的增量先挂起，达到上限时钳制显示。
        private static int _trueRound;
        private static int _roundPending;
        private static bool _roundTallySeen;
        private static bool _vsPlaceSeen;

        // ---- 树屋/大厅 ----
        private static bool _inLobby;

        // ---- 回合提示动画 ----
        private static string _roundNoticeText;
        private static float _roundNoticeStart;
        private const float RoundNoticeDuration = 1.5f;

        // ---- 内容行变化淡入 ----
        private const float LineFadeTime = 0.15f;
        private static readonly string[] _lineLast = new string[24];
        private static readonly float[] _lineShownAt = new float[24];

        // ---- 平铺模式：独立小段（各自背景/颜色），一行放不下自动换行 ----
        private static readonly string[] _segText = new string[16];
        private static readonly Color[] _segColor = new Color[16];
        private static readonly float[] _segW = new float[16];
        private static readonly float[] _segWSmooth = new float[16];
        private static readonly int[] _segRow = new int[16];
        private static readonly float[] _segX = new float[16];
        private static int _segCount;
        private static int _segRowCount;
        private static float _segRowsW;

        // ---- 丝滑动画：整体滑入 + 行 stagger ----
        private static float _hudAppear;        // 0..1 出现进度
        private static float _hudShownAt;       // 本轮开始出现的时间（stagger 基准）
        private static bool _wasVisible;

        // ---- 额外信息行 ----
        private static string _extra1Text;
        private static string _extra2Text;

        // ---- 反射缓存（VersusControl 计时字段）----
        private static FieldInfo _fPlacementTimer;
        private static FieldInfo _fLevelTimer;
        private static FieldInfo _fCountdownTimer;
        private static FieldInfo _fChallengeRunTime;
        private static FieldInfo _fChallengeRunStarted;

        // ---- HUD 绘制 ----
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
        // Custom 布局位置：运行期内存，会话内保持，不写配置。
        private static float _customX = 16f;
        private static float _customY = 16f;
        private static bool _dragging;
        private static float _dragOffX;
        private static float _dragOffY;

        // ---- 设置页 ----
        private static readonly HashSet<string> _keysConfig = new HashSet<string> { "Enabled", "Fade", "Show Key" };
        private static readonly HashSet<string> _keysAppearance = new HashSet<string> { "Layout", "Font Size", "Opacity", "Background Opacity" };
        private static readonly HashSet<string> _keysBehavior = new HashSet<string> { "Warning Seconds", "Blink", "Round Notice" };
        private static readonly HashSet<string> _keysDisplay = new HashSet<string> { "Display Mode" };

        // ================= 配置取值 =================
        private static bool IsEnabled() {
            try { return _enabled != null && _enabled.Value; } catch { return false; }
        }

        private static bool FadeOn() {
            try { return _fade != null && _fade.Value; } catch { return true; }
        }

        private static ShowMode ModeValue() {
            try { return _showMode != null ? _showMode.Value : ShowMode.Hold; } catch { return ShowMode.Hold; }
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

        // 配置色：核心调色板有未落盘预览时用预览色（拖动/输入即时生效），否则读配置值。
        private static Color Col(ConfigEntry<string> e, Color fallback) {
            Color? preview = SRApi.Ctl.PreviewColor(e);
            if (preview.HasValue) return preview.Value;
            return SRApi.ColorHex.ParseHex(EntryHex(e), fallback);
        }

        // ================= 注册 =================
        private static void SelfReg() {
            // 本文件反射的游戏私有成员，改名时启动自检会在日志里告警。
            SRApi.Ref.Override("VersusControl.placementTimer", "HUD（建造阶段剩余时间）");
            SRApi.Ref.Override("VersusControl.levelTimer", "HUD（对局已用时间）");
            SRApi.Ref.Override("VersusControl.countdownTimer", "HUD（决胜倒计时）");
            SRApi.Ref.Override("ChallengeControl.runTime", "HUD（挑战计时）");
            SRApi.Ref.Override("ChallengeControl.runStarted", "HUD（挑战是否已开始）");
            // 枚举下拉框的中文显示名（英文模式仍显示枚举原名）。
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
            SRApi.Nav(Sec, 25); // 移动轨迹(20) 之后
            SRApi.RegisterPage(Sec, "hud", "HUD", "HUD", 10, RenderPage1);
            SRApi.RegisterPage(Sec, "content", "显示内容", "Content", 20, RenderPage2);

            Loc("Enabled", "显示HUD", "Show HUD",
                "HUD 总开关\n关闭后完全不绘制 HUD\n纯本地只读，不发送任何网络消息",
                "HUD master switch\nwhen off the HUD is not drawn at all\nlocal read-only; sends no network messages");
            Loc("Fade", "淡入淡出", "Fade",
                "淡入淡出\nHUD 显示或隐藏时平滑过渡\n关闭则立即显示或隐藏",
                "fade in/out\nsmoothly transitions the HUD when showing or hiding\nwhen off the HUD appears/disappears instantly");
            Loc("Show Key", "显示按键", "Show key",
                "显示按键\n按住或切换用于显示 HUD 的按键\n点右侧键框后按新键改绑，Esc 取消",
                "show key\nkey used to show the HUD by holding or toggling\nclick the key box, then press a new key (Esc cancels)");
            Loc("Show Mode", "显示方式", "Show mode",
                "显示方式\n按住显示：按住显示键期间显示 HUD\n切换显示：按一下在显示与隐藏之间切换",
                "show mode\nHold = visible while the show key is held\nToggle = tap the show key to switch visibility");
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

            // 显示方案下拉。
            Loc("Display Mode", "显示方案", "Display mode",
                "显示方案\n堆叠 = 每个内容单独一行；平铺 = 每个内容独立小段横向排列，放不下自动换行\n回合提示始终单独占行",
                "display mode\nStack = one line per content item; Inline = each item is a small tile laid out horizontally and wraps when needed\nthe round notice always uses its own line");
            // 第二页内容：开关 + 颜色（颜色说明统一模板）。
            for (int i = 0; i < _content.Length; i++) {
                ContentDef d = _content[i];
                Loc(d.Key, d.LabelZh, d.LabelEn, d.DescZh, d.DescEn);
                SRApi.LocKey(Sec, d.ColorKey, d.LabelZh + "颜色", d.LabelEn + " color");
                SRApi.LocDesc(Sec, d.ColorKey,
                    d.LabelZh + "的文字颜色\n十六进制颜色值，例如 #FFFFFF\n点色块开调色板；点名称还原默认色",
                    "text color for " + d.LabelEn.ToLowerInvariant() + "\nhex color value, for example #FFFFFF\nclick the swatch to open the picker; click the name to reset");
            }
        }

        private static void Loc(string key, string zh, string en, string descZh, string descEn) {
            SRApi.LocKey(Sec, key, zh, en);
            SRApi.LocDesc(Sec, key, descZh, descEn);
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

        // 行可见性：颜色键用同行的色块渲染，显示方式跟随显示按键行，都不单独成行。
        private static bool RowVisible(ConfigEntryBase e) {
            try {
                if (!IsSec(e)) return true;
                string k = e.Definition.Key;
                if (k == "Show Mode") return false;
                if (k.StartsWith("Color ")) return false;
                return true;
            } catch { return true; }
        }

        // 显示按键行的同行附加控件：显示方式下拉框。
        private static string RowExtra(ConfigEntryBase e) {
            try {
                if (!IsSec(e)) return null;
                return e.Definition.Key == "Show Key" ? "Show Mode" : null;
            } catch { return null; }
        }

        private static readonly Func<ConfigEntryBase, bool> _rowConfig = RowConfig;
        private static readonly Func<ConfigEntryBase, bool> _rowAppearance = RowAppearance;
        private static readonly Func<ConfigEntryBase, bool> _rowBehavior = RowBehavior;
        private static readonly Func<ConfigEntryBase, bool> _rowDisplay = RowDisplay;

        private static void RenderPage1() {
            try {
                List<ConfigEntryBase> list = SRApi.SectionEntries(Sec);
                if (list == null || list.Count == 0) { GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
                SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Config -"));
                SRApi.RenderSectionEntries(Sec, _rowConfig);
                SRApi.Ctl.SectionTitle(SRApi.T("- 外观 -", "- Appearance -"));
                SRApi.RenderSectionEntries(Sec, _rowAppearance);
                SRApi.Ctl.SectionTitle(SRApi.T("- 行为 -", "- Behavior -"));
                SRApi.RenderSectionEntries(Sec, _rowBehavior);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 设置页", __ex); }
        }

        private static void RenderPage2() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 显示方案 -", "- Display mode -"));
                SRApi.RenderSectionEntries(Sec, _rowDisplay);
                SRApi.Ctl.SectionTitle(SRApi.T("- 一般 -", "- General -"));
                for (int i = 0; i <= 4; i++) RenderContentRow(_content[i]);
                SRApi.Ctl.SectionTitle(SRApi.T("- 其它 -", "- Other -"));
                RenderContentRow(_content[5]);
                RenderContentRow(_content[6]);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 显示内容页", __ex); }
        }

        // 一行：标签 + 开关/文本框 + 右侧统一色块；点名称还原默认色，点色块展开核心调色板。
        private static void RenderContentRow(ContentDef d) {
            try {
                ConfigEntryBase show = SRApi.Ctl.FindEntry(Sec, d.Key);
                ConfigEntryBase color = SRApi.Ctl.FindEntry(Sec, d.ColorKey);
                string tip = SRApi.T(d.DescZh, d.DescEn) + "\n" + SRApi.T("点击名称还原默认色", "Click the name to reset to the default color");
                GUIContent label = new GUIContent(SRApi.T(d.LabelZh, d.LabelEn), tip);
                float h = SRApi.Ctl.Sc(26f);
                GUILayout.BeginHorizontal();
                // 名称文本可点击：把该内容颜色还原为配置默认色（写盘与调色板状态同步由核心处理）。
                if (GUILayout.Button(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(150f)), GUILayout.Height(h))) {
                    SRApi.Ctl.ResetColor(color);
                }
                if (show != null) SRApi.Ctl.RenderControl(show);
                GUILayout.Space(SRApi.Ctl.Sc(6f));
                // 色块：点击展开/收起统一调色板（预览色、写盘时机都由核心负责）。
                Rect swatch = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(64f), h);
                SRApi.Ctl.ColorSwatch(swatch, color);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                // 展开时由核心补画调色板：HUD 不再持有任何调色板状态。
                SRApi.Ctl.ColorPanel(color);
            } catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 内容行", __ex); }
        }

        // 滑块范围注册：字体大小 / 文字不透明度 / 背景透明度 / 警告秒数。
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
                // 第一页：配置 / 外观 / 行为。
                _enabled = cfg.Bind(Sec, "Enabled", false, "显示HUD：总开关（默认关闭）。");
                _fade = cfg.Bind(Sec, "Fade", true, "淡入淡出：HUD 显示/隐藏时平滑过渡。");
                _showKey = cfg.Bind(Sec, "Show Key", KeyCode.BackQuote, "显示按键：用于显示 HUD 的按键（默认 ~）。");
            // 默认就是"显示"状态：以前 _toggleOn 初始为 false，每次重开游戏都得先按一下显示键才出现，
            // 现在开局即有 HUD，按键只用来切换隐藏/显示。
            _toggleOn = true;
                _showMode = cfg.Bind(Sec, "Show Mode", ShowMode.Hold, "显示方式：按住显示 / 切换显示。");
                _layout = cfg.Bind(Sec, "Layout", HudLayout.TopLeft, "HUD 布局：四角贴边或自定义拖动位置。");
                _fontSize = cfg.Bind(Sec, "Font Size", 18,
                    new ConfigDescription("字体大小（10-48）。", new AcceptableValueRange<int>(10, 48)));
                _opacity = cfg.Bind(Sec, "Opacity", 0.85f,
                    new ConfigDescription("文字不透明度（0.2-1）。", new AcceptableValueRange<float>(0.2f, 1f)));
                _bgOpacity = cfg.Bind(Sec, "Background Opacity", 0.5f,
                    new ConfigDescription("背景透明度（0-1，0 为完全透明）。", new AcceptableValueRange<float>(0f, 1f)));
                _warnSeconds = cfg.Bind(Sec, "Warning Seconds", 10,
                    new ConfigDescription("警告秒数（5-60）。", new AcceptableValueRange<int>(5, 60)));
                _blink = cfg.Bind(Sec, "Blink", false, "闪烁提醒：最后 5 秒倒计时文字闪烁。");
                _roundNotice = cfg.Bind(Sec, "Round Notice", true, "回合提示：回合数变化时以放大闪烁动画显示当前回合。");
                // 第二页：显示方案 + 显示内容 + 颜色。
                _displayMode = cfg.Bind(Sec, "Display Mode", DisplayMode.Inline, "显示方案：平铺（小段横向排列，自动换行，默认）/ 堆叠（每项一行）。");
                _showPhase = cfg.Bind(Sec, "Show Phase", false, "显示阶段名（默认关闭）。");
                _colPhase = cfg.Bind(Sec, "Color Phase", "#FFFFFF", "阶段名文字颜色（#RRGGBB）。");
                _showCountdown = cfg.Bind(Sec, "Show Countdown", false, "显示倒计时（默认关闭）。");
                _colCountdown = cfg.Bind(Sec, "Color Countdown", "#FFFFFF", "倒计时文字颜色（#RRGGBB）。");
                _showRound = cfg.Bind(Sec, "Show Round", false, "显示回合进度（默认关闭）。");
                _colRound = cfg.Bind(Sec, "Color Round", "#FFFFFF", "回合进度文字颜色（#RRGGBB）。");
                _showLevel = cfg.Bind(Sec, "Show Level", false, "显示关卡/模式（默认关闭）。");
                _colLevel = cfg.Bind(Sec, "Color Level", "#FFFFFF", "关卡/模式文字颜色（#RRGGBB）。");
                _showWin = cfg.Bind(Sec, "Show Win Score", false, "显示获胜目标（默认关闭）。");
                _colWin = cfg.Bind(Sec, "Color Win Score", "#FFFFFF", "获胜文字颜色（#RRGGBB）。");
                _extra1 = cfg.Bind(Sec, "Extra Line 1", "", "额外信息行 1：按原文显示，留空则不显示。");
                _colExtra1 = cfg.Bind(Sec, "Color Extra 1", "#FFFFFF", "额外信息行 1 文字颜色（#RRGGBB）。");
                _extra2 = cfg.Bind(Sec, "Extra Line 2", "", "额外信息行 2：按原文显示，留空则不显示。");
                _colExtra2 = cfg.Bind(Sec, "Color Extra 2", "#FFFFFF", "额外信息行 2 文字颜色（#RRGGBB）。");

                // 行扩展点：隐藏颜色/显示方式行、显示方式跟随显示按键行、四个滑块。
                try { SRApi.RowFilters.Add(RowVisible); } catch (Exception __ex) { SRApi.Guard.Log("HUD 行过滤", __ex); }
                try { SRApi.RowExtras.Add(RowExtra); } catch (Exception __ex) { SRApi.Guard.Log("HUD 行附加", __ex); }
                try {
                    SRApi.RowSliders.Add(FontSlider);
                    SRApi.RowSliders.Add(OpacitySlider);
                    SRApi.RowSliders.Add(BgOpacitySlider);
                    SRApi.RowSliders.Add(WarnSlider);
                } catch (Exception __ex) { SRApi.Guard.Log("HUD 滑块注册", __ex); }

                // 旧版已删除的键：自动从旧 cfg 清理（幂等；原文件由核心备份）。
                try {
                    SRApi.Migration.Remove(Sec,
                        "Pos X", "Pos Y", "Edge Margin", "Show Scores", "Show Players", "Show Eliminations", "Beep", "Beep Volume",
                        "Phase Notice", "Color Player Panel");
                } catch (Exception __ex) { SRApi.Guard.Log("HUD 清理旧键", __ex); }

                // 显示键接入核心键位系统（用于按键显示/冲突提示）。
                try { SRApi.RegisterKey("HUD-显示键", _showKey, "hold"); }
                catch (Exception __ex) { SRApi.Guard.Log("HUD 键位注册", __ex); }

                SRApi.RegisterTick("sr.roundhud", Tick);
                SRApi.RegisterSceneHook(ResetStateSafe);

                // 常驻对象：只承载 OnGUI 绘制。
                GameObject go = new GameObject("SR_UCH_HUD");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<HudComponent>();

                // 本类自带 [HarmonyPatch] 但 ITweak 为反射发现，须手动 CreateAndPatchAll；失败只丢本类功能。
                try { Harmony.CreateAndPatchAll(typeof(HUD), (string)null); }
                catch (Exception e) { SRApi.LogError("HUD 补丁注册失败: " + e.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 初始化", __ex); }
        }

        // ================= 阶段事件 =================
        [HarmonyPatch(typeof(GameControl), "handleEvent")]
        [HarmonyPostfix]
        private static void OnGameControlEvent(GameControl __instance, global::GameEvent.GameEvent e) {
            try {
                if (__instance == null || e == null) return;
                StartPhaseEvent start = e as StartPhaseEvent;
                if (start != null) { OnPhaseStarted(__instance, start.Phase); return; }
                EndPhaseEvent end = e as EndPhaseEvent;
                if (end != null) { OnPhaseEnded(end.Phase); return; }
                ScoreboardEvent sb = e as ScoreboardEvent;
                if (sb != null) {
                    // 记分板出现/结算完成 = 本回合确实推进过，可作为“真实回合”增量的证据。
                    try { if (sb.Showing || sb.AfterTally) _roundTallySeen = true; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 阶段事件", __ex); }
        }

        private static void OnPhaseStarted(GameControl gc, GameControl.GamePhase phase) {
            _gc = gc;
            _eventSeen = true;
            _phase = phase;
            _phaseStart = Time.unscaledTime;
            _vsPlaceSeen = false;
        }

        private static void OnPhaseEnded(GameControl.GamePhase phase) {
            if (phase != _phase) return;
            if (Time.unscaledTime < _phaseStart) _phaseStart = Time.unscaledTime;
        }

        // ================= 每帧状态 =================
        private static void Tick() {
            try {
                if (!IsEnabled()) {
                    _hasGame = false;
                    _inLobby = false;
                    _wasEnabled = false;
                    StepAlpha(false);
                    return;
                }
                if (!_wasEnabled) {
                    _wasEnabled = true;
                    _eventSeen = false;
                    _phase = GameControl.GamePhase.NONE;
                    _phaseStart = Time.unscaledTime;
                    _lastRound = 0;
                    _trueRound = 0;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = (lm != null) ? lm.CurrentGameController : null;
                if (gc == null) {
                    // 树屋/大厅：HUD 完全不显示（用户要求删除大厅显示）。
                    if (_gc != null || _hasGame) ResetGameState();
                    _inLobby = false;
                    
                    StepAlpha(false);
                    return;
                }
                _inLobby = false;
                if (!ReferenceEquals(gc, _gc)) {
                    _gc = gc;
                    _phase = GameControl.GamePhase.NONE;
                    _phaseStart = Time.unscaledTime;
                    _eventSeen = false;
                    _vsPlaceSeen = false;
                    _timerText = "00:00";
                    _warnLevel = 0;
                    _lastRound = 0;
                    _trueRound = 0;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                if (!_eventSeen) {
                    GameControl.GamePhase p = gc.Phase;
                    if (p != GameControl.GamePhase.NONE && p != _phase) {
                        _phase = p;
                        _phaseStart = Time.unscaledTime;
                    }
                }
                if (_phase == GameControl.GamePhase.NONE) { _hasGame = false; StepAlpha(false); return; }
                _hasGame = true;

                GameSettings gs = ReadSettings();
                UpdateTimer(gc, gs);
                UpdateRound(gc, gs);
                UpdateLevel(gs);
                UpdateWin(gs);
                UpdateExtraText();
                UpdateRoundNotice();
                UpdateVisible();
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 状态更新", __ex); }
        }

        // 显示按键 + 淡入淡出：设置窗口打开时强制可见（便于拖动/预览）。
        private static void UpdateVisible() {
            bool keyVis;
            try {
                if (SRApi.UiOpen) keyVis = true;
                else if (ModeValue() == ShowMode.Hold) keyVis = SRApi.ComboKeyHeld(_showKey);
                else {
                    if (SRApi.ComboKeyDown(_showKey)) _toggleOn = !_toggleOn;
                    keyVis = _toggleOn;
                }
            } catch { keyVis = false; }
            StepAlpha(IsEnabled() && (_hasGame || _inLobby) && keyVis);
        }

        private static void StepAlpha(bool target) {
            float want = target ? 1f : 0f;
            bool fade = FadeOn();
            if (target && !_wasVisible) _hudShownAt = Time.unscaledTime;
            _wasVisible = target;
            if (fade) {
                _hudAppear = Mathf.MoveTowards(_hudAppear, want, 4f * Time.unscaledDeltaTime);
                _hudAlpha = EaseOut(_hudAppear);   // alpha 也用 ease-out
            } else {
                _hudAppear = want;
                _hudAlpha = want;
            }
        }

        // ease-out（t = 1-(1-x)^3）。
        private static float EaseOut(float x) {
            float inv = 1f - Mathf.Clamp01(x);
            return 1f - inv * inv * inv;
        }

        // 内容行依次淡入：每行延迟 20ms，0.2s 完成。
        private static float StaggerAlpha(int index) {
            if (_hudShownAt <= 0f) return 1f;
            float x = (Time.unscaledTime - _hudShownAt - index * 0.02f) / 0.2f;
            return EaseOut(x);
        }

        // 位移动画与 alpha 分离：位移用 0.25s 的独立 ease-out 进度。
        private static float StaggerOffset(int index) {
            if (_hudShownAt <= 0f) return 1f;
            float x = (Time.unscaledTime - _hudShownAt - index * 0.02f) / 0.25f;
            return 1f - EaseOut(x);
        }

        private static GameSettings ReadSettings() {
            try { return GameSettings.GetInstance(); } catch { return null; }
        }

        private static void UpdateTimer(GameControl gc, GameSettings gs) {
            // 挑战模式：计时以游戏自己的 runTime 为准，按空格开始前（runStarted=false）不计数。
            if (IsChallengeMode() && gc is ChallengeControl) { UpdateChallengeTimer(gc); return; }

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _phaseStart);
            float remaining = -1f;
            bool countdown = false;
            VersusControl vs = gc as VersusControl;
            switch (_phase) {
            case GameControl.GamePhase.START: {
                float d = gc.StartDelay;
                if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
                break;
            }
            case GameControl.GamePhase.PLACE: {
                // 派对模式：建造阶段以游戏自己的 placementTimer 为准（每个派对盒/每次进入建造都会重置）。
                if (IsPartyMode(gs)) {
                    if (vs != null) {
                        float pt = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                        if (pt >= 0f) { countdown = true; remaining = pt; break; }
                    }
                    float partyTotal = gs != null ? gs.PlaceTime : 0f;
                    if (partyTotal > 0.05f) { countdown = true; remaining = partyTotal - elapsed; }
                    break;
                }
                bool useTimer = gs != null && gs.UsePlaceTimer;
                if (useTimer) {
                    if (vs != null) {
                        float t = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                        if (t >= 0f) {
                            if (t > 0.01f) _vsPlaceSeen = true;
                            if (_vsPlaceSeen) { countdown = true; remaining = t; break; }
                        }
                    }
                    float total = gs != null ? gs.PlaceTime : 0f;
                    if (total > 0.05f) { countdown = true; remaining = total - elapsed; }
                }
                break;
            }
            case GameControl.GamePhase.PLAY: {
                if (gs != null && gs.GameLimitType == GameLimitType.TIME && vs != null) {
                    float used = ReadFloat(ref _fLevelTimer, vs, "levelTimer", "HUD（对局已用时间）");
                    if (used >= 0f) { countdown = true; remaining = (float)gs.MaxTime - used; }
                }
                break;
            }
            case GameControl.GamePhase.SUDDENDEATH: {
                if (vs != null) {
                    float t = ReadFloat(ref _fCountdownTimer, vs, "countdownTimer", "HUD（决胜倒计时）");
                    if (t > 0.01f) { countdown = true; remaining = t; }
                }
                break;
            }
            case GameControl.GamePhase.END: {
                float d = gc.WinTime;
                if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
                break;
            }
            default:
                break;
            }
            if (countdown) {
                if (remaining < 0f) remaining = 0f;
                _timerText = FormatClock(Mathf.CeilToInt(remaining));
                float warn = WarnSeconds();
                float red = Mathf.Min(5f, warn);
                _warnLevel = remaining <= red ? 2 : (remaining <= warn ? 1 : 0);
            } else {
                _timerText = FormatClock(Mathf.FloorToInt(elapsed));
                _warnLevel = 0;
            }
        }

        // 挑战模式叫「重试」（无上限），其它模式叫「回合」（有上限）。
        private static bool IsChallengeMode() {
            try { GameSettings gs = GameSettings.GetInstance(); return gs != null && gs.GameMode == GameState.GameMode.CHALLENGE; }
            catch { return false; }
        }

        // 派对模式：只用于「建造阶段不计时」的判断（读不到设置时按非派对处理，保持原行为）。
        private static bool IsPartyMode(GameSettings gs) {
            try {
                GameSettings s = gs != null ? gs : GameSettings.GetInstance();
                return s != null && s.GameMode == GameState.GameMode.PARTY;
            } catch { return false; }
        }

        private static string RoundWord() {
            return IsChallengeMode() ? SRApi.T("重试：", "Retry: ") : SRApi.T("回合：", "Round: ");
        }

        // 回合进度格式：「回合：当前/最大」。
        // 游戏里唯一的回合字段是 GameControl.roundNumber（protected；属性 RoundNumber），
        // 只在 VersusControl.ToPlaceMode（反编译行 144554）/ ChallengeControl（122107）里 ++；
        // GameState / ScoreKeeper 都没有回合字段（已全量检索）。该值会被场景重载/刷新派对盒额外 +1，
        // 因此这里只在“观察到回合结算记分板”后接受增量，未确认的增量挂起；并钳制 ≤ MaxRounds。
        private static void UpdateRound(GameControl gc, GameSettings gs) {
            try {
                int candidate = gc.RoundNumber;
                // 挑战模式：每死一次 = 一次重试，没有「回合上限」概念，也不发结算记分板事件。
                // 直接采用游戏自身的 roundNumber（避免被“结算证据”门槛卡住），只显示「重试：N」。
                if (IsChallengeMode()) {
                    if (candidate <= 0) { _roundText = null; return; }
                    _trueRound = candidate;
                    _roundPending = 0;
                    _roundTallySeen = false;
                    _roundText = RoundWord() + candidate;
                    return;
                }
                if (candidate < _trueRound) {
                    // 新对局/重载回退：以较小值重新开始
                    _trueRound = candidate;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                if (_trueRound <= 0 && candidate > 0) {
                    // 首回合直接接受
                    _trueRound = candidate;
                    _roundPending = 0;
                } else if (candidate > _trueRound) {
                    if (candidate == _trueRound + 1 && _roundTallySeen) {
                        _trueRound = candidate;
                        _roundPending = 0;
                        _roundTallySeen = false;
                    } else if (candidate > _roundPending) {
                        _roundPending = candidate;   // 未确认（可能是重载/刷新派对盒的额外 +1）
                    }
                }
                if (_roundTallySeen && _roundPending > _trueRound) {
                    // 结算已发生：接受此前挂起的真实回合
                    _trueRound = _roundPending;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                if (_trueRound <= 0) { _roundText = null; return; }
                int max = 0;
                try { if (gs != null) max = gs.MaxRounds; } catch { max = 0; }
                int shown = max > 0 ? Mathf.Min(_trueRound, max) : _trueRound;
                _roundText = max > 0
                    ? RoundWord() + shown + "/" + max
                    : RoundWord() + shown;
            } catch { _roundText = null; }
        }

        // 当前关卡显示名：自定义快照优先用 snapshotName；只有代码时显示「自定义关卡」；否则用本地化关卡名。
        private static string CurrentLevelName(out string code) {
            code = null;
            string level = null;
            try {
                GameState st = GameState.GetInstance();
                if (st != null) {
                    try {
                        level = st.currentSnapshotInfo.snapshotName;
                        code = st.currentSnapshotInfo.snapshotCode;
                    } catch { level = null; code = null; }
                    if (string.IsNullOrEmpty(level)) {
                        string lv = GameState.GetLevelLocalizedName(st.SelectedLevel);
                        if (!string.IsNullOrEmpty(lv) && lv.IndexOf("default Level name error", StringComparison.Ordinal) < 0) level = lv;
                    }
                }
            } catch { level = null; code = null; }
            if (string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(code)) level = SRApi.T("自定义关卡", "Custom level");
            return level;
        }

        // 关卡/模式行：「关卡名 · 模式」，不带前缀；自定义关卡显示实际快照名。
        private static void UpdateLevel(GameSettings gs) {
            try {
                string code;
                string level = CurrentLevelName(out code);
                string mode = (gs != null) ? ModeName(gs.GameMode) : null;
                if (!string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(mode)) _levelText = level + " · " + mode;
                else if (!string.IsNullOrEmpty(level)) _levelText = level;
                else if (!string.IsNullOrEmpty(mode)) _levelText = mode;
                else _levelText = null;
            } catch { _levelText = null; }
        }

        // 获胜：取游戏设置的获胜目标分，读不到时用 250。
        private static void UpdateWin(GameSettings gs) {
            int target = WinScoreFallback;
            try {
                if (gs != null) {
                    int ms = gs.MaxScore;
                    if (ms > 0) target = ms;
                }
            } catch { target = WinScoreFallback; }
            _winText = SRApi.T("获胜：", "Win: ") + target;
        }

        private static void UpdateRoundNotice() {
            try {
                int round = _trueRound;
                if (round <= 0 || round == _lastRound) return;
                _lastRound = round;
                if (!RoundNoticeOn()) return;
                _roundNoticeText = RoundWord() + round;
                _roundNoticeStart = Time.unscaledTime;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        // 树屋/大厅：只保留标题「大厅」与当前投票关卡；玩家列表随玩家面板一并删除。
        // 得票最多的关卡传送门（没有票就返回 null）。
        private static LevelPortal FindLeadingPortal(LobbyManager lm, out int votes) {
            votes = 0;
            try {
                if (lm == null) return null;
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                if (lsc == null) return null;
                LevelPortal best = null;
                int bestVotes = 0;
                if (lsc.portals != null) {
                    for (int i = 0; i < lsc.portals.Length; i++) {
                        LevelPortal p = lsc.portals[i];
                        if (p == null || p.Votes == null) continue;
                        if (p.Votes.Count > bestVotes) { bestVotes = p.Votes.Count; best = p; }
                    }
                }
                if (lsc.snapshotPortals != null) {
                    for (int i = 0; i < lsc.snapshotPortals.Length; i++) {
                        LevelPortal p = lsc.snapshotPortals[i];
                        if (p == null || p.Votes == null) continue;
                        if (p.Votes.Count > bestVotes) { bestVotes = p.Votes.Count; best = p; }
                    }
                }
                votes = bestVotes;
                return best;
            } catch { return null; }
        }

        private static string PortalName(LevelPortal p) {
            try {
                CustomLevelPortal cp = p as CustomLevelPortal;
                if (cp != null && cp.snapshotInfo != null && !string.IsNullOrEmpty(cp.snapshotInfo.snapshotName)) {
                    return Truncate(cp.snapshotInfo.snapshotName, 16);
                }
                if (p.TargetLevel == GameState.LevelName.RANDOM) return SRApi.T("随机", "Random");
                string lv = GameState.GetLevelLocalizedName(p.TargetLevel);
                if (string.IsNullOrEmpty(lv) || lv.IndexOf("default Level name error", StringComparison.Ordinal) >= 0) {
                    lv = p.TargetLevel.ToString();
                }
                return Truncate(lv, 16);
            } catch { return null; }
        }

        private static void UpdateExtraText() {
            _extra1Text = ReadExtraLine(_extra1);
            _extra2Text = ReadExtraLine(_extra2);
        }

        private static string ReadExtraLine(ConfigEntry<string> entry) {
            try {
                string s = entry != null ? entry.Value : null;
                if (string.IsNullOrEmpty(s)) return null;
                s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > 120) s = s.Substring(0, 120);
                return s.Length > 0 ? s : null;
            } catch { return null; }
        }

        private static string Truncate(string s, int max) {
            if (string.IsNullOrEmpty(s)) return s;
            if (max <= 3 || s.Length <= max) return s;
            return s.Substring(0, max - 2) + "..";
        }

        private static string ModeName(GameState.GameMode mode) {
            switch (mode) {
            case GameState.GameMode.FREEPLAY: return SRApi.T("自由", "Freeplay");
            case GameState.GameMode.CREATIVE: return SRApi.T("创造", "Creative");
            case GameState.GameMode.PARTY: return SRApi.T("派对", "Party");
            case GameState.GameMode.CHALLENGE: return SRApi.T("挑战", "Challenge");
            default: return null;
            }
        }

        private static float ReadFloat(ref FieldInfo f, object owner, string name, string feature) {
            if (owner == null) return -1f;
            if (f == null) {
                try { f = SRApi.Ref.Field(typeof(VersusControl), name, feature); } catch { f = null; }
            }
            if (f == null) return -1f;
            try {
                object v = f.GetValue(owner);
                return (v is float) ? (float)v : -1f;
            } catch { return -1f; }
        }

        // 挑战模式计时：读游戏 ChallengeControl 的私有字段 runTime / runStarted（反射缓存，读不到返回 -1/false）。
        private static void UpdateChallengeTimer(GameControl gc) {
            try {
                ChallengeControl cc = gc as ChallengeControl;
                float run = -1f;
                bool started = false;
                if (cc != null) {
                    run = ReadFloatOn(ref _fChallengeRunTime, typeof(ChallengeControl), cc, "runTime", "HUD（挑战计时）");
                    started = ReadBoolOn(ref _fChallengeRunStarted, typeof(ChallengeControl), cc, "runStarted", "HUD（挑战是否已开始）");
                }
                if (run < 0f) {
                    float elapsed = Mathf.Max(0f, Time.unscaledTime - _phaseStart);
                    _timerText = FormatClock(started ? Mathf.FloorToInt(elapsed) : 0);
                } else {
                    _timerText = FormatClock(Mathf.FloorToInt(started ? run : 0f));
                }
                _warnLevel = 0;
            } catch (Exception __ex) { SRApi.Guard.Log("HUD/挑战计时", __ex); _timerText = null; }
        }

        private static float ReadFloatOn(ref FieldInfo f, System.Type ownerType, object owner, string name, string feature) {
            if (owner == null || ownerType == null) return -1f;
            if (f == null) { try { f = SRApi.Ref.Field(ownerType, name, feature); } catch { f = null; } }
            if (f == null) return -1f;
            try { object v = f.GetValue(owner); return (v is float) ? (float)v : -1f; } catch { return -1f; }
        }

        private static bool ReadBoolOn(ref FieldInfo f, System.Type ownerType, object owner, string name, string feature) {
            if (owner == null || ownerType == null) return false;
            if (f == null) { try { f = SRApi.Ref.Field(ownerType, name, feature); } catch { f = null; } }
            if (f == null) return false;
            try { object v = f.GetValue(owner); return (v is bool) && (bool)v; } catch { return false; }
        }

        private static string FormatClock(int totalSeconds) {
            if (totalSeconds < 0) totalSeconds = 0;
            int m = totalSeconds / 60;
            int s = totalSeconds % 60;
            return m.ToString("00") + ":" + s.ToString("00");
        }

        private static string PhaseName(GameControl.GamePhase p) {
            switch (p) {
            case GameControl.GamePhase.START: return SRApi.T("准备", "Ready");
            case GameControl.GamePhase.PLAY: return SRApi.T("游玩", "Play");
            case GameControl.GamePhase.PLACE: return SRApi.T("建造", "Build");
            case GameControl.GamePhase.SUDDENDEATH: return SRApi.T("决胜", "Sudden death");
            case GameControl.GamePhase.END: return SRApi.T("结算", "End");
            case GameControl.GamePhase.WAIT: return SRApi.T("等待", "Wait");
            default: return SRApi.T("空闲", "Idle");
            }
        }

        private static void ResetGameState() {
            _gc = null;
            _phase = GameControl.GamePhase.NONE;
            _phaseStart = 0f;
            _eventSeen = false;
            _hasGame = false;
            _timerText = "00:00";
            _warnLevel = 0;
            _roundText = null;
            _levelText = null;
            _winText = null;
            _roundNoticeText = null;
            _roundNoticeStart = 0f;
            _lastRound = 0;
            _vsPlaceSeen = false;
            _inLobby = false;
            
            for (int i = 0; i < _lineLast.Length; i++) { _lineLast[i] = null; _lineShownAt[i] = 0f; }
            for (int i = 0; i < _hudHeights.Length; i++) _hudHeights[i] = 0f;
            for (int i = 0; i < _segWSmooth.Length; i++) _segWSmooth[i] = 0f;
            _heightFrame = -1;
            _trueRound = 0;
            _roundPending = 0;
            _roundTallySeen = false;
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

        // 纯色绘制用白贴图（必须用 GUI.color 着色，不能用 GUI.backgroundColor 乘在深色按钮贴图上）。
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

        // 只在"从无到有"时淡入（用于每秒变化的倒计时，避免每秒闪一次）。
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

        // 平铺模式的分段连接符（「 · 」）。
        // 平铺小段：文字 + 颜色（宽度在绘制时按样式实测）。
        private static void AddSeg(Color c, string text) {
            if (string.IsNullOrEmpty(text) || _segCount >= _segText.Length) return;
            _segText[_segCount] = text;
            _segColor[_segCount] = c;
            _segCount++;
        }

        // 回合提示：固定大号字 + 颜色交替闪烁 + 末尾淡出，约 1.5 秒（无缩放/位移）。
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

        private static void DrawHud() {
            if (_hudAlpha <= 0.004f) return;
            if (!SRApi.GateMaster) return;
            bool inGame = _hasGame && _phase != GameControl.GamePhase.NONE;
            if (!inGame) return;   // 大厅/树屋不画 HUD
            EnsureStyles();
            float hudA = _hudAlpha;
            float textA = Opacity() * hudA;
            float bgA = BgOpacity() * hudA;

            // 1) 拼行。
            int n = 0;
            _segCount = 0;
            {
                Color cPhase = Col(_colPhase, Color.white);
                Color cCountdown = Col(_colCountdown, Color.white);
                Color cRound = Col(_colRound, Color.white);
                Color cLevel = Col(_colLevel, Color.white);
                Color cWin = Col(_colWin, Color.white);
                Color cExtra1 = Col(_colExtra1, Color.white);
                Color cExtra2 = Col(_colExtra2, Color.white);

                if (DisplayModeValue() == DisplayMode.Inline) {
                    // 平铺：每个显示项作为独立小段（各自背景/颜色），后面按可用宽度自动换行。
                    if (ShowOn(_showPhase)) AddSeg(cPhase, PhaseName(_phase));
                    if (ShowOn(_showCountdown)) AddSeg(CountdownColor(cCountdown), _timerText);
                    if (ShowOn(_showRound) && !string.IsNullOrEmpty(_roundText)) AddSeg(cRound, _roundText);
                    if (ShowOn(_showLevel) && !string.IsNullOrEmpty(_levelText)) AddSeg(cLevel, _levelText);
                    if (ShowOn(_showWin) && !string.IsNullOrEmpty(_winText)) AddSeg(cWin, _winText);
                    if (!string.IsNullOrEmpty(_extra1Text)) AddSeg(cExtra1, _extra1Text);
                    if (!string.IsNullOrEmpty(_extra2Text)) AddSeg(cExtra2, _extra2Text);
                } else {
                    // 堆叠：每项一行。
                    if (ShowOn(_showPhase)) {
                        string s = PhaseName(_phase);
                        float f = SlotFadeChange(0, s);
                        AddLineFade(ref n, s, null, LinePhase, WithA(cPhase, textA * f));
                    }
                    if (ShowOn(_showCountdown)) {
                        float f = SlotFadeAppear(1, _timerText);
                        Color c = CountdownColor(cCountdown);
                        float blink = 1f;
                        if (_warnLevel == 2 && BlinkOn()) blink = (Mathf.Sin(Time.unscaledTime * 12f) > 0f) ? 1f : 0.25f;
                        AddLineFade(ref n, _timerText, null, LineTimer, WithA(c, textA * f * blink));
                    }
                    if (ShowOn(_showRound) && !string.IsNullOrEmpty(_roundText)) {
                        float f = SlotFadeChange(2, _roundText);
                        AddLineFade(ref n, _roundText, null, LineSmall, WithA(cRound, textA * f));
                    }
                    if (ShowOn(_showLevel) && !string.IsNullOrEmpty(_levelText)) {
                        float f = SlotFadeChange(3, _levelText);
                        AddLineFade(ref n, _levelText, null, LineSmall, WithA(cLevel, textA * f));
                    }
                    if (ShowOn(_showWin) && !string.IsNullOrEmpty(_winText)) {
                        float f = SlotFadeChange(4, _winText);
                        AddLineFade(ref n, _winText, null, LineSmall, WithA(cWin, textA * f));
                    }
                    if (!string.IsNullOrEmpty(_extra1Text)) {
                        float f = SlotFadeChange(5, _extra1Text);
                        AddLineFade(ref n, _extra1Text, null, LineSmall, WithA(cExtra1, textA * f));
                    }
                    if (!string.IsNullOrEmpty(_extra2Text)) {
                        float f = SlotFadeChange(6, _extra2Text);
                        AddLineFade(ref n, _extra2Text, null, LineSmall, WithA(cExtra2, textA * f));
                    }
                }
                AddRoundNoticeLine(ref n, textA);
            }
            if (n > _hudPlain.Length) n = _hudPlain.Length;
            if (n == 0 && _segCount == 0) return;   // 无任何内容（含平铺小段）时完全隐藏

            // 2) 尺寸：紧凑（padding 2、行高=文本高、行间 0）；平铺模式先把小段排成网格。
            float pad = SRApi.Ctl.Sc(2f);
            float segPad = SRApi.Ctl.Sc(3f);
            float segGap = SRApi.Ctl.Sc(3f);
            float rowGap = SRApi.Ctl.Sc(2f);
            float segFont = (_smallStyle != null ? _smallStyle.fontSize : 14);
            float segH = segFont + segPad * 2f;
            float segBlockH = 0f;
            if (_segCount > 0) {
                float maxRowW = Mathf.Max(SRApi.Ctl.Sc(120f), (float)Screen.width - pad * 2f);
                float rowW = 0f;
                int row = 0;
                _segRowsW = 0f;
                for (int i = 0; i < _segCount; i++) {
                    float w = Measure(_segText[i], _smallStyle) + segPad * 2f;
                    // 小段宽度平滑（文本每秒变化时避免整行重排跳变）。
                    if (_heightFrame != Time.frameCount) {
                        if (_segWSmooth[i] <= 0f) _segWSmooth[i] = w;
                        else _segWSmooth[i] = Mathf.MoveTowards(_segWSmooth[i], w, SRApi.Ctl.Sc(480f) * Time.unscaledDeltaTime);
                    }
                    _segW[i] = _segWSmooth[i] > 0f ? _segWSmooth[i] : w;
                    if (rowW > 0f && rowW + segGap + w > maxRowW) { row++; rowW = 0f; }
                    if (rowW > 0f) rowW += segGap;
                    _segRow[i] = row;
                    _segX[i] = rowW;
                    rowW += w;
                    if (rowW > _segRowsW) _segRowsW = rowW;
                }
                _segRowCount = row + 1;
                segBlockH = _segRowCount * segH + (_segRowCount - 1) * rowGap;
            } else {
                _segRowCount = 0;
                _segRowsW = 0f;
            }
            float contentW = 0f;
            for (int i = 0; i < n; i++) contentW = Mathf.Max(contentW, Measure(_hudPlain[i], LineStyle(_hudKinds[i])));
            float panelW = Mathf.Max(Mathf.Max(contentW, _segRowsW) + pad * 2f, SRApi.Ctl.Sc(60f));
            float textW = Mathf.Max(1f, panelW - pad * 2f);
            float contentH = 0f;
            for (int i = 0; i < n; i++) {
                GUIStyle st = LineStyle(_hudKinds[i]);
                float th;
                if (_hudKinds[i] == LineRoundNotice && st != null) {
                    th = st.fontSize;   // 大号提示按当前动画字号占高
                } else {
                    th = 0f;
                    try { th = SRApi.Ctl.TextHeight(_hudPlain[i], textW); } catch { th = 0f; }
                    if (th <= 0f) th = (st != null ? st.fontSize : 14);
                }
                // 行高平滑：文本变化导致的高度差用 MoveTowards 过渡，避免整块 HUD 突然重排跳变。
                if (_hudKinds[i] == LineRoundNotice) {
                    _hudHeights[i] = th;   // 回合提示只有 1.5s，高度直接到位
                } else if (_heightFrame != Time.frameCount) {
                    if (_hudHeights[i] <= 0f) _hudHeights[i] = th;
                    else _hudHeights[i] = Mathf.MoveTowards(_hudHeights[i], th, SRApi.Ctl.Sc(160f) * Time.unscaledDeltaTime);
                }
                contentH += _hudHeights[i];
            }
            _heightFrame = Time.frameCount;
            float panelH = contentH + pad * 2f + segBlockH + ((segBlockH > 0f && n > 0) ? rowGap : 0f);
            _hudW = panelW;
            _hudH = panelH;

            // 3) 布局（从所在边滑入 6px，ease-out）+ 拖动命中。
            float x, y;
            LayoutPosition(_hudW, _hudH, out x, out y);
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, (float)Screen.width - panelW));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, (float)Screen.height - panelH));
            float slide = SRApi.Ctl.Sc(6f) * (1f - EaseOut(_hudAppear));
            float sx = 0f, sy = 0f;
            switch (LayoutValue()) {
            case HudLayout.TopLeft: sx = -slide; sy = -slide; break;
            case HudLayout.TopRight: sx = slide; sy = -slide; break;
            case HudLayout.BottomLeft: sx = -slide; sy = slide; break;
            case HudLayout.BottomRight: sx = slide; sy = slide; break;
            default: sx = -slide * 0.5f; break; // Custom：轻微从左侧滑入
            }
            Rect panel = new Rect(x + sx, y + sy, panelW, panelH);
            HandleDrag(panel);

            Event ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            GUI.depth = 1;

            // 4) 半透明底。
            GUI.color = new Color(0f, 0f, 0f, bgA);
            GUI.DrawTexture(panel, _white);
            GUI.color = Color.white;

            // 5) 平铺小段网格：每段独立背景 + 内边距 + 颜色，按行依次淡入。
            float ty = panel.y + pad;
            if (_segCount > 0 && _segRowCount > 0) {
                for (int r = 0; r < _segRowCount; r++) {
                    for (int i = 0; i < _segCount; i++) {
                        if (_segRow[i] != r) continue;
                        float easeSeg = StaggerAlpha(i);
                        if (easeSeg <= 0.001f) continue;
                        Rect sr = new Rect(panel.x + pad + _segX[i], ty + StaggerOffset(i) * SRApi.Ctl.Sc(3f), _segW[i], segH);
                        GUI.color = new Color(1f, 1f, 1f, 0.10f * easeSeg);   // 小段自身底色（比整块底稍亮）
                        GUI.DrawTexture(sr, _white);
                        GUI.color = Color.white;
                        _smallStyle.normal.textColor = WithA(_segColor[i], textA * easeSeg);
                        GUI.Label(new Rect(sr.x + segPad, sr.y, sr.width - segPad * 2f, sr.height), _segText[i], _smallStyle);
                    }
                    ty += segH + rowGap;
                }
                if (n > 0) ty += rowGap;
            }

            // 6) 普通行：逐行 stagger 淡入 + 轻微上移；富文本行用 GUI.color 的 alpha。
            float tw = panel.width - pad * 2f;
            for (int i = 0; i < n; i++) {
                GUIStyle st = LineStyle(_hudKinds[i]);
                if (st == null) { ty += _hudHeights[i]; continue; }
                float lh = _hudHeights[i];
                float easeLine = StaggerAlpha(i + _segCount);
                int kind = _hudKinds[i];
                // 回合提示只闪烁，不做位移；普通行位移用独立的 ease-out 进度。
                float yOff = kind == LineRoundNotice ? 0f : StaggerOffset(i + _segCount) * SRApi.Ctl.Sc(3f);
                Rect lr = new Rect(panel.x + pad, ty + yOff, tw, lh);
                Color c = _hudColors[i];
                st.normal.textColor = WithA(c, c.a * easeLine);
                GUI.Label(lr, _hudRich[i], st);
                ty += lh;
            }
        }

        private static Color WithA(Color c, float a) { return new Color(c.r, c.g, c.b, Mathf.Clamp01(a)); }

        // 四角严格贴边：TL=(0,0)、TR=(W-w,0)、BL=(0,H-h)、BR=(W-w,H-h)；Custom 用运行期拖动位置。
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

        // 复用同一个 GUIContent：每帧最多 40 次测量，避免逐次分配（IMGUI 标准做法）。
        private static readonly GUIContent _measureContent = new GUIContent();
        private static float Measure(string text, GUIStyle style) {
            if (string.IsNullOrEmpty(text) || style == null) return 0f;
            try { _measureContent.text = text; return style.CalcSize(_measureContent).x; } catch { return 0f; }
        }

        // 拖动：仅在"设置窗口打开 + Layout=自定义"时允许；只更新运行期位置，不写配置。
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
