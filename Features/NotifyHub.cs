// 通知治理中心：把散在各功能里的屏幕提示统一收口。
//
// 为什么必须在核心挂一个钩子（本轮最重要的结构判断）：
//   SRApi.Notify / NotifyT / NotifyTag 是三条不同的注入通道，而核心自己也直接调 SR.Notify；
//   功能层又不能引用实现层（守卫会拦），也不该为了加个开关去改 60 多个调用点。
//   但它们全都汇到 SR.Notify.cs 的 NotifyImpl —— 所以在那里留一个可选的 Slots 钩子，
//   一个点就能覆盖全部来源，且未安装本功能时行为一字不变。
//   钩子返回 true = 治理中心已负责屏幕显示，核心不再调 ShowUserMessage；日志仍由核心照写。
//
// 收口后做的事（全部只在本机，不碰网络 / 相机 / 线程 / 补丁 / msgType / 反射登记）：
//   · 优先级分级：要动手的（提案 / 失败 / 拒绝）> 一般反馈 > 纯确认
//   · 同类合并：3 秒窗口内同来源同内容合成一条 ×N，不再互相顶掉
//   · 对局中静默：低/中优先级只进历史不弹屏，高优先级照弹
//   · 历史回看：筛选 + 分页 + 展开详情 + 复制 / 导出 + 一键清空
//   · 写进关卡记录器时间线（走 sr.rec.note 槽位，软依赖）
//   · 提案过期也能补点：历史里给「重新打开」按钮（走 sr.notify.reopenProposal 槽位）
//
// 浮层为什么自建 MonoBehaviour.OnGUI：SRApi.RegisterOverlay 的覆盖层只在面板打开/淡出期间绘制
//   （SR.Window.DrawGUI 在面板关闭时直接 return），局内看不见。与 Brief.cs 同一套范式。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.notifyhub", "通知中心", "1.0", Provides = new string[] { "sr.notify.hook", "sr.notify.priority" })]
    public class NotifyHub : ITweak {
        private const string Sec = "通知中心";
        private const string HookSlot = "sr.notify.hook";
        private const string RecSlot = "sr.rec.note";
        private const string ReopenSlot = "sr.notify.reopenProposal";
        private const int PageSize = 20;

        // 2 高（要动手 / 出错） 1 中（一般反馈） 0 低（纯确认）
        private static readonly string[] HighWords = {
            "提案", "待审", "过期", "失败", "拒绝", "错误", "不能", "无法", "仅房主", "超时", "断开", "损坏", "未生效",
            "proposal", "pending", "expired", "failed", "refused", "error", "cannot", "host only", "timed out", "rejected", "damaged"
        };
        private static readonly string[] LowWords = {
            "已复制", "已写入日志", "已保存", "已还原", "已清空", "已开始",
            "copied", "written to log", "saved", "restored", "cleared"
        };

        private sealed class Notice {
            public string Text, Tag, Key;
            public int Level;
            public float At, Until;
            public int Count = 1;
            public bool Reopenable;
        }

        private static ConfigEntry<bool> _enabled, _native, _quiet, _onScreen;
        private static ConfigEntry<int> _maxVisible, _toastSeconds, _corner, _cardW, _cardAlpha, _cardScale;
        private static bool _cornerOpen;   // 停靠角下拉的展开态（每帧从核心 EditOpen 语义走）

        private static readonly List<Notice> _history = new List<Notice>();
        private static readonly List<Notice> _visible = new List<Notice>();
        private static Action<string> _aRecNote;
        private static Action _aReopen;
        private static Func<float> _aFont;
        private static Func<bool> _aContrast;
        private static Func<float> _aExtra;
        private static int _bindTries;
        private static float _nextBind;

        private static Texture2D _white;
        private static GUIStyle _label, _small, _btn;
        private static HubUI _ui;

        // ============ 生命周期 ============

        private static bool _uiReg;

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        // 列宽与核心 Sc() 同坐标系；契约层未暴露 EntryNameWidth，故用固定 180 设计像素。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        // 本页配置行的显示名与悬浮说明：缺 LocKey 时中文界面回退英文键名，
        // 缺 LocDesc 时英文界面完全没有提示（内部条目不会回退 Bind 的中文描述）。
        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用通知中心", "Enable notification hub");
            SRApi.LocDesc(Sec, "Enabled",
                "启用通知中心\n统一接管所有屏幕提示：分级、合并、静默。",
                "Enable notification hub\nTake over all screen toasts: priority, merging, quiet mode and history.");
            SRApi.LocKey(Sec, "Use Native Toast", "使用原生提示", "Use native toast");
            SRApi.LocDesc(Sec, "Use Native Toast",
                "使用原生提示\n退回游戏原生提示（本功能只记历史，不改屏幕显示）。",
                "Use native toast\nFall back to the game's own toasts; history is still recorded, display is untouched.");
            SRApi.LocKey(Sec, "Show On Screen", "显示局内浮层", "Show on-screen overlay");
            SRApi.LocDesc(Sec, "Show On Screen",
                "显示局内浮层\n在局内显示一条可折叠的通知浮层；关掉后只保留历史页。",
                "Show on-screen overlay\nShow a foldable overlay during play; off keeps only the history page.");
            SRApi.LocKey(Sec, "Quiet In Match", "对局中静默", "Quiet in match");
            SRApi.LocDesc(Sec, "Quiet In Match",
                "对局中静默\n对局中静默低/中优先级提示（仍进历史）；高优先级照常弹。",
                "Quiet in match\nSuppress low/medium priority toasts during a match (still recorded); high priority still pops.");
            SRApi.LocKey(Sec, "Max Visible", "同时显示条数", "Max visible");
            SRApi.LocDesc(Sec, "Max Visible",
                "同时显示条数\n浮层最多同时显示几条（1-6）。",
                "Max visible\nHow many toasts show at once. Range 1-6.");
            SRApi.LocKey(Sec, "Toast Seconds", "停留秒数", "Toast seconds");
            SRApi.LocDesc(Sec, "Toast Seconds",
                "停留秒数\n每条通知停留秒数（2-15），可读性页可以再加秒。",
                "Toast seconds\nSeconds each toast stays. Range 2-15; readability can extend it.");
            SRApi.LocKey(Sec, "History Limit", "历史上限", "History limit");
            SRApi.LocDesc(Sec, "History Limit",
                "历史上限\n历史保留条数上限（100-2000），超出丢最旧。",
                "History limit\nMax history entries kept; oldest dropped. Range 100-2000.");
            SRApi.LocKey(Sec, "Write To Recorder", "写入关卡记录", "Write to recorder");
            SRApi.LocDesc(Sec, "Write To Recorder",
                "写入关卡记录\n把中/高优先级通知写进关卡记录器的时间线（软依赖）。",
                "Write to recorder\nWrite medium/high priority toasts into the level recorder timeline (soft dependency).");
            SRApi.LocKey(Sec, "Card Width", "卡片宽度", "Card width");
            SRApi.LocDesc(Sec, "Card Width",
                "卡片宽度\n浮层卡片像素宽度（120-340），默认 170。",
                "Card width\nOverlay card width in pixels. Range 120-340, default 170.");
            SRApi.LocKey(Sec, "Card Scale", "浮层缩放", "Overlay scale");
            SRApi.LocDesc(Sec, "Card Scale",
                "浮层缩放\n整个浮层按百分比缩放（80-160），默认 100。",
                "Overlay scale\nScale the whole overlay in percent. Range 80-160, default 100.");
            SRApi.LocKey(Sec, "Card Opacity", "卡片不透明度", "Card opacity");
            SRApi.LocDesc(Sec, "Card Opacity",
                "卡片不透明度\n浮层卡片背景的不透明度百分比（20-100），默认 86。",
                "Card opacity\nOverlay card background opacity in percent. Range 20-100, default 86.");
            SRApi.LocKey(Sec, "Corner", "停靠位置", "Overlay position");
            SRApi.LocDesc(Sec, "Corner",
                "浮层停靠角\n浮层停靠位置：0 右下 / 1 右上 / 2 左下 / 3 左上。",
                "Overlay corner\n0 bottom-right / 1 top-right / 2 bottom-left / 3 top-left.");
            SRApi.LocKey(Sec, "Open Key", "打开历史快捷键", "Open history key");
            SRApi.LocDesc(Sec, "Open Key",
                "打开历史快捷键\n打开设置面板回看历史的快捷键。",
                "Open history key\nOpen the settings panel on the history page.");
            SRApi.LocKey(Sec, "Copy Key", "复制结果快捷键", "Copy key");
            SRApi.LocDesc(Sec, "Copy Key",
                "复制结果快捷键\n一键复制当前筛选结果的快捷键。",
                "Copy key\nCopy the current filtered result.");
            SRApi.LocKey(Sec, "Clear Key", "清空历史快捷键", "Clear history key");
            SRApi.LocDesc(Sec, "Clear Key",
                "清空历史快捷键\n清空历史的快捷键。",
                "Clear history key\nClear the notification history.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Max Visible": min = 1.0f; max = 6.0f; return true;
                    case "Toast Seconds": min = 1.0f; max = 5.0f; return true;
                    case "History Limit": min = 100.0f; max = 2000.0f; return true;
                    case "Corner": min = 0.0f; max = 3.0f; return true;
                    case "Card Opacity": min = 20.0f; max = 100.0f; return true;
                    case "Card Scale": min = 80.0f; max = 160.0f; return true;
                    case "Card Width": min = 100.0f; max = 300.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "统一接管所有屏幕提示：分级、合并、静默。");
            _native = plugin.Config.Bind(Sec, "Use Native Toast", false, "退回游戏原生提示（本功能只记历史，不改屏幕显示）。");
            _onScreen = plugin.Config.Bind(Sec, "Show On Screen", true, "在局内显示一条可折叠的通知浮层；关掉后只保留历史页。");
            _quiet = plugin.Config.Bind(Sec, "Quiet In Match", false, "对局中静默低/中优先级提示（仍进历史）；高优先级照常弹。");
            _maxVisible = plugin.Config.Bind(Sec, "Max Visible", 3,
                new ConfigDescription("浮层最多同时显示几条（1-6）。", new AcceptableValueRange<int>(1, 6)));
            _toastSeconds = plugin.Config.Bind(Sec, "Toast Seconds", 2,
                new ConfigDescription("每条通知停留秒数（1-5）。", new AcceptableValueRange<int>(1, 5)));
            _cardW = plugin.Config.Bind(Sec, "Card Width", 200,
                new ConfigDescription("浮层卡片像素宽度（100-300）。", new AcceptableValueRange<int>(100, 300)));
            _cardScale = plugin.Config.Bind(Sec, "Card Scale", 100,
                new ConfigDescription("整个浮层的缩放百分比（80-160）。", new AcceptableValueRange<int>(80, 160)));
            _cardAlpha = plugin.Config.Bind(Sec, "Card Opacity", 86,
                new ConfigDescription("浮层卡片背景不透明度百分比（20-100）。", new AcceptableValueRange<int>(20, 100)));
            _corner = plugin.Config.Bind(Sec, "Corner", 0,
                new ConfigDescription("浮层停靠位置：0 右下 / 1 右上 / 2 左下 / 3 左上。", new AcceptableValueRange<int>(0, 3)));

            SRApi.LocSec(Sec, "通知", "Notifications");

            RegUi();
            // 底栏入口（设置右侧），不再占用侧栏栏目（用户要求）。
            SRApi.RegisterFooterButton(Sec, "通知", "Notify", null);
            SRApi.RegisterPage(Sec, "HubOptions", "通知设置", "Settings", 20, RenderOptionsPage);
            SRApi.RegisterTick("通知中心-绑定与过期", Tick);

            // 核心 NotifyImpl 会惰性查这个名字；Provide 之后所有来源的提示都会进 OnNotify。
            Slots.Provide(HookSlot, (Func<string, string, bool>)((t, g) => OnNotify(t, g)));
            // 对局播报复用同一套优先级口径（0 低 / 1 中 / 2 高），不再自己维护关键词表。
            Slots.Provide("sr.notify.priority", (Func<string, string, int>)((t, g) => Classify(t, g)));

            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
            if (_ui == null) {
                GameObject go = new GameObject("SR_UCH_NotifyHub");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _ui = go.AddComponent<HubUI>();
            }
        }

        // ITweak 没有 Shutdown 契约；留一个对称入口，将来契约补齐时可直接调用。
        public void Shutdown() {
            Slots.Retract(HookSlot, null);
            Slots.Retract("sr.notify.priority", null);
            if (_ui != null) { try { UnityEngine.Object.Destroy(_ui.gameObject); } catch (Exception ex) { SRApi.Guard.Log("通知中心/销毁对象", ex); } _ui = null; }
            if (_white != null) { try { UnityEngine.Object.Destroy(_white); } catch (Exception ex) { SRApi.Guard.Log("通知中心/销毁贴图", ex); } _white = null; }
        }

        // ============ 软依赖绑定（只查名字，不产生槽位缺失记录） ============

        private static void Tick() {
            try {
                if (_bindTries < 6 && Time.unscaledTime >= _nextBind) {
                    _bindTries++;
                    BindSlots();
                    _nextBind = Time.unscaledTime + 2f;
                }
                float now = Time.unscaledTime;
                for (int i = _visible.Count - 1; i >= 0; i--) if (_visible[i].Until <= now) _visible.RemoveAt(i);

            } catch (Exception ex) { SRApi.Guard.Log("通知中心/Tick", ex); }
        }

        private static void BindSlots() {
            foreach (string n in Slots.Names) {
                if (n == RecSlot && _aRecNote == null) _aRecNote = Slots.Require<Action<string>>(n);
                else if (n == ReopenSlot && _aReopen == null) _aReopen = Slots.Require<Action>(n);
                else if (n == "sr.a11y.fontScale" && _aFont == null) _aFont = Slots.Require<Func<float>>(n);
                else if (n == "sr.a11y.highContrast" && _aContrast == null) _aContrast = Slots.Require<Func<bool>>(n);
                else if (n == "sr.a11y.extraSeconds" && _aExtra == null) _aExtra = Slots.Require<Func<float>>(n);
            }
        }

        private static float AFont() { try { return _aFont != null ? Mathf.Clamp(_aFont(), 0.8f, 1.6f) : 1f; } catch (Exception ex) { SRApi.Guard.Log("通知中心/字号", ex); return 1f; } }
        private static bool AContrast() { try { return _aContrast != null && _aContrast(); } catch (Exception ex) { SRApi.Guard.Log("通知中心/对比", ex); return false; } }
        private static float AExtra() { try { return _aExtra != null ? Mathf.Clamp(_aExtra(), 0f, 20f) : 0f; } catch (Exception ex) { SRApi.Guard.Log("通知中心/加秒", ex); return 0f; } }

        // ============ 通知入口（核心 NotifyImpl 调用） ============

        private static bool _inNotify;

        /// <summary>返回 true = 已接管屏幕显示；false = 让核心照旧用游戏原生提示。</summary>
        private static bool OnNotify(string text, string tag) {
            if (_enabled == null || !_enabled.Value) return false;
            // 「退回游戏原生提示」只让出屏幕，不跳过收口：历史、合并、分级、写记录器照常进行。
            // 否则那个开关就变成「整个功能关掉」，与配置说明和历史回看的设计相矛盾。
            bool native = _native == null || _native.Value;
            // 防重入：写记录器 / 恢复提案的回调若将来也发提示，这里直接放行给原生通道，
            // 绝不形成 Notify -> OnNotify -> Notify 的无限递归。
            if (_inNotify) return false;
            if (string.IsNullOrEmpty(text)) return !native;         // 空提示不占历史
            _inNotify = true;
            try {
                float now = Time.unscaledTime;
                int level = Classify(text, tag);
                string key = (tag ?? "") + "|" + Norm(text);
                bool reopenable = Contains(text, "提案") || Contains(text, "proposal");
                float stay = Mathf.Clamp(_toastSeconds.Value, 2f, 15f) + AExtra();

                // 同类合并：3 秒窗口内同 key 只累加计数并续期，不再插入新行。
                // 必须全表扫：命中后会把该条的 At 刷新为 now，但它仍留在 List 的旧位置；
                // 一旦"时间已旧"的条目先被遇到就 break，后面刚续期的新条目反而扫不到（合并偶发失效）。
                Notice merged = null;
                for (int i = _history.Count - 1; i >= 0; i--) {
                    if (_history[i].Key != key) continue;
                    if (now - _history[i].At >= 3f) continue;
                    merged = _history[i]; break;
                }
                if (merged != null) {
                    merged.Count++;
                    merged.Until = now + stay;
                    merged.At = now;
                    return !native;
                }

                Notice n = new Notice();
                n.Text = text; n.Tag = tag ?? ""; n.Key = key; n.Level = level;
                n.At = now; n.Until = now + stay; n.Reopenable = reopenable;

                _history.Add(n);
                // 只保留最近的内部缓冲（用于合并去重），不再对外提供历史页。
                while (_history.Count > 64) _history.RemoveAt(0);

                bool quiet = _quiet.Value && level < 2 && InMatch();
                if (!native && !quiet && _onScreen.Value) {
                    _visible.Add(n);
                    int max = Mathf.Clamp(_maxVisible.Value, 1, 6);
                    // 高优先级排最前：要动手的卡片永远在最上面。
                    // 同级时新的排前面：以前只按 Level 排序（不稳定），超上限时 RemoveAt(末尾)
                    // 可能把刚到的消息丢掉 → 表现为"通知不灵敏、偶尔漏消息"。
                    _visible.Sort(delegate (Notice a, Notice b) {
                        int c = b.Level.CompareTo(a.Level);
                        return c != 0 ? c : b.At.CompareTo(a.At);
                    });
                    while (_visible.Count > max) _visible.RemoveAt(_visible.Count - 1);
                }

                // 写进关卡记录器时间线：低优先级的纯确认不写，避免刷屏。
                return !native;
            } catch (Exception ex) {
                SRApi.Guard.Log("通知中心/收口", ex);
                return false;   // 出错时退回原生显示，宁可多弹一条也不能丢提示
            } finally {
                _inNotify = false;
            }
        }

        private static int Classify(string text, string tag) {
            string all = text + " " + (tag ?? "");
            for (int i = 0; i < HighWords.Length; i++) if (Contains(all, HighWords[i])) return 2;
            for (int i = 0; i < LowWords.Length; i++) if (Contains(all, LowWords[i])) return 0;
            return 1;
        }

        private static bool Contains(string s, string w) {
            return !string.IsNullOrEmpty(s) && s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>归一化：去数字（倒计时会变）、截前 48 字，作为同类合并键。</summary>
        private static string Norm(string s) {
            StringBuilder sb = new StringBuilder(48);
            for (int i = 0; i < s.Length && sb.Length < 48; i++) {
                char c = s[i];
                if (char.IsDigit(c)) continue;
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>对局中判定：拿不到场景信息时返回 false（宁可不静默，也不吞掉提示）。</summary>
        private static bool InMatch() {
            try {
                if (SRApi.Env.InTreehouse) return false;
                string s = SRApi.Env.SceneName;
                if (string.IsNullOrEmpty(s)) return false;
                if (Contains(s, "menu") || Contains(s, "main") || Contains(s, "startup") || Contains(s, "splash")) return false;
                return true;
            } catch (Exception ex) { SRApi.Guard.Log("通知中心/判场景", ex); return false; }
        }

        // ============ 局内浮层 ============

        private static void EnsureStyles() {
            if (_white == null) {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.name = "SR_UCH_HubWhite";
                _white.hideFlags = HideFlags.HideAndDontSave;
                _white.SetPixel(0, 0, Color.white);
                _white.Apply(false, true);
            }
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label);
            _label.wordWrap = true; _label.clipping = TextClipping.Clip;
            _label.normal.textColor = new Color(0.94f, 0.96f, 0.99f, 1f);
            _small = new GUIStyle(_label);
            _small.fontStyle = FontStyle.Bold;
            _btn = new GUIStyle(GUI.skin.button);
        }

        private static void Fill(Rect r, Color c) {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        private static void DrawToast() {
            if (_enabled == null || !_enabled.Value || !_onScreen.Value || _visible.Count == 0 || Event.current == null) return;
            EnsureStyles();
            float s = AFont() * Mathf.Clamp(_cardScale.Value / 100f, 0.8f, 1.6f);
            bool hc = AContrast();
            _label.fontSize = Mathf.RoundToInt(12f * s);
            _small.fontSize = Mathf.RoundToInt(12f * s);
            _label.normal.textColor = hc ? Color.white : new Color(0.94f, 0.96f, 0.99f, 1f);

            float w = Mathf.Clamp(_cardW.Value * s, 100f, 300f);   // 默认 170 ≈ 原 300 的一半，可在设置里调
            float pad = 8f * s;
            float now = Time.unscaledTime;

            // 先算总高，再从停靠角反推起点；条目按优先级从高到低自上而下排。
            float[] heights = new float[_visible.Count];
            float total = 0f;
            for (int i = 0; i < _visible.Count; i++) {
                float th = Mathf.Max(18f * s, _label.CalcHeight(new GUIContent(_visible[i].Text), w - pad * 2f - 14f * s));
                heights[i] = th + pad * 2f + 4f * s;
                total += heights[i] + 4f * s;
            }
            int corner = Mathf.Clamp(_corner.Value, 0, 3);
            float x = (corner == 2 || corner == 3) ? 12f : Screen.width - w - 12f;
            float y = (corner == 0 || corner == 2) ? Screen.height - total - 12f : 12f;

            int prevDepth = GUI.depth;
            Color prevColor = GUI.color;
            try {
                GUI.depth = 3;   // 比局内简报(2) 更靠上；finally 还原，不污染全局
                for (int i = 0; i < _visible.Count; i++) {
                    Notice n = _visible[i];
                    float h = heights[i];
                    // 动画：出现时 0.18 秒淡入 + 从停靠侧滑入 8px；消失前 0.22 秒淡出。
                    float age = Mathf.Max(0f, now - n.At);
                    float left = Mathf.Max(0f, n.Until - now);
                    float anim = Mathf.Clamp01(age / 0.18f);
                    if (left < 0.22f) anim *= Mathf.Clamp01(left / 0.22f);
                    anim = anim * anim * (3f - 2f * anim);                 // smoothstep，起落都不生硬
                    float slide = (1f - Mathf.Clamp01(age / 0.18f)) * 8f * s;
                    float ax = x + ((corner == 2 || corner == 3) ? -slide : slide);
                    float aa = Mathf.Clamp01(anim);
                    Color prevGui = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, aa);
                    Rect box = new Rect(ax, y, w, h);
                    float cardA = Mathf.Clamp01(_cardAlpha.Value / 100f) * aa;
                    Fill(box, new Color(0.06f, 0.08f, 0.11f, hc ? Mathf.Min(1f, cardA + 0.08f) : cardA));
                    // 左侧优先级色条：颜色之外还有「长短」这一重编码（高=最长）。
                    Color bar = n.Level == 2 ? new Color(1f, 0.55f, 0.22f, 1f)
                              : n.Level == 1 ? new Color(0.36f, 0.72f, 1f, 1f)
                              : new Color(0.55f, 0.60f, 0.66f, 1f);
                    Fill(new Rect(ax, y, (n.Level == 2 ? 6f : 3f) * s, h), new Color(bar.r, bar.g, bar.b, aa));
                    // 高优先级加一圈同色描边：颜色之外再给一层"更醒目"的编码（不新增素材）。
                    if (n.Level == 2) {
                        Color oc = new Color(bar.r, bar.g, bar.b, 0.55f * aa);
                        float t = Mathf.Max(1f, 1f * s);
                        Fill(new Rect(box.x, box.y, box.width, t), oc);
                        Fill(new Rect(box.x, box.yMax - t, box.width, t), oc);
                        Fill(new Rect(box.x, box.y, t, box.height), oc);
                        Fill(new Rect(box.xMax - t, box.y, t, box.height), oc);
                    }

                    string head = n.Level == 2 ? SRApi.T("重要", "IMPORTANT")
                                : n.Level == 1 ? SRApi.T("提示", "INFO") : SRApi.T("已办", "DONE");
                    GUI.Label(new Rect(ax + pad, y + pad * 0.5f, w - pad * 2f, 16f * s),
                              head + (n.Count > 1 ? "  x" + n.Count : ""), _small);
                    // 文本区底部 +1px 缓冲：游戏字体最后一笔常被 CalcSize 少算 1px，
                    // 不加缓冲时"y/g/p"这类下伸笔画的最后一行会被裁掉。
                    GUI.Label(new Rect(ax + pad, y + pad * 0.5f + 16f * s,
                                       w - pad * 2f - 6f * s, h - pad - 16f * s + 1f),
                              n.Text, _label);
                    // 底部剩余时间条：长度随剩余比例缩短，一眼看出还来得及点。
                    float life = n.Until > n.At ? Mathf.Clamp01((n.Until - now) / (n.Until - n.At)) : 0f;
                    Fill(new Rect(ax + (n.Level == 2 ? 6f : 3f) * s, y + h - 3f * s, (w - (n.Level == 2 ? 6f : 3f) * s) * life, 3f * s),
                         new Color(bar.r, bar.g, bar.b, 0.85f * aa));
                    GUI.color = prevGui;
                    y += h + 4f * s;
                }
            } finally {
                GUI.depth = prevDepth;
                GUI.color = prevColor;
            }
        }

        // ============ 历史页 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private static string OneLine(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\n", " ").Replace("\r", " ");
            return t.Length > 90 ? t.Substring(0, 90) + "..." : t;
        }

        // ============ 设置页 ============

        private void RenderOptionsPage() {
            CfgRow(_enabled);
            CfgRow(_native);
            CfgRow(_onScreen);
            CfgRow(_quiet);
            CfgRow(_maxVisible);
            CfgRow(_toastSeconds);
            CfgRow(_cardW);
            CfgRow(_cardAlpha);
            CfgRow(_cardScale);
            // 停靠角改用下拉：0 右下 / 1 右上 / 2 左下 / 3 左上。
            {
                int[] vals = new int[] { 0, 1, 2, 3 };
                string[] labels = new string[] {
                    SRApi.T("停靠位置：右下", "Position: bottom-right"),
                    SRApi.T("停靠位置：右上", "Position: top-right"),
                    SRApi.T("停靠位置：左下", "Position: bottom-left"),
                    SRApi.T("停靠位置：左上", "Position: top-left")
                };
                int cur = Mathf.Clamp(_corner.Value, 0, 3);
                bool open = _cornerOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, SRApi.Ctl.Sc(180f));
                _cornerOpen = open;
                if (sel >= 0 && sel < vals.Length) _corner.Value = vals[sel];
            }

        }

        // 常驻组件：面板关闭时 DrawGUI 会早退，覆盖层走不到，所以必须自己挂 OnGUI。
        private sealed class HubUI : MonoBehaviour {
            private void OnGUI() {
                try { DrawToast(); }
                catch (Exception ex) { SRApi.Guard.Log("通知中心/绘制", ex); }
            }
        }
    }
}
