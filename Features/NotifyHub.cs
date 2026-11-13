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

        private static ConfigEntry<bool> _enabled, _native, _quiet, _onScreen, _writeRec;
        private static ConfigEntry<int> _maxVisible, _toastSeconds, _historyLimit, _corner;
        private static ConfigEntry<KeyCode> _openKey, _copyKey, _clearKey;

        private static readonly List<Notice> _history = new List<Notice>();
        private static readonly List<Notice> _visible = new List<Notice>();
        private static Action<string> _aRecNote;
        private static Action _aReopen;
        private static Func<float> _aFont;
        private static Func<bool> _aContrast;
        private static Func<float> _aExtra;
        private static int _bindTries;
        private static float _nextBind;

        // 历史页的查看状态（不写进配置，关面板后回到第一页）
        private static int _page, _detail = -1, _levelFilter;
        private static string _filter = "";
        private static bool _onlyReopen;

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
                "启用通知中心\n统一接管所有屏幕提示：分级、合并、静默、历史回看。",
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
            SRApi.LocKey(Sec, "Corner", "浮层停靠角", "Overlay corner");
            SRApi.LocDesc(Sec, "Corner",
                "浮层停靠角\n浮层停靠角：0 右下 / 1 右上 / 2 左下 / 3 左上。",
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
                    case "Toast Seconds": min = 2.0f; max = 15.0f; return true;
                    case "History Limit": min = 100.0f; max = 2000.0f; return true;
                    case "Corner": min = 0.0f; max = 3.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "统一接管所有屏幕提示：分级、合并、静默、历史回看。");
            _native = plugin.Config.Bind(Sec, "Use Native Toast", false, "退回游戏原生提示（本功能只记历史，不改屏幕显示）。");
            _onScreen = plugin.Config.Bind(Sec, "Show On Screen", true, "在局内显示一条可折叠的通知浮层；关掉后只保留历史页。");
            _quiet = plugin.Config.Bind(Sec, "Quiet In Match", true, "对局中静默低/中优先级提示（仍进历史）；高优先级照常弹。");
            _maxVisible = plugin.Config.Bind(Sec, "Max Visible", 3,
                new ConfigDescription("浮层最多同时显示几条（1-6）。", new AcceptableValueRange<int>(1, 6)));
            _toastSeconds = plugin.Config.Bind(Sec, "Toast Seconds", 4,
                new ConfigDescription("每条通知停留秒数（2-15），可读性页可以再加秒。", new AcceptableValueRange<int>(2, 15)));
            _historyLimit = plugin.Config.Bind(Sec, "History Limit", 400,
                new ConfigDescription("历史保留条数上限（100-2000），超出丢最旧。", new AcceptableValueRange<int>(100, 2000)));
            _writeRec = plugin.Config.Bind(Sec, "Write To Recorder", true, "把中/高优先级通知写进关卡记录器的时间线（软依赖）。");
            _corner = plugin.Config.Bind(Sec, "Corner", 0,
                new ConfigDescription("浮层停靠角：0 右下 / 1 右上 / 2 左下 / 3 左上。", new AcceptableValueRange<int>(0, 3)));
            _openKey = plugin.Config.Bind(Sec, "Open Key", KeyCode.None, "打开设置面板回看历史的快捷键。");
            _copyKey = plugin.Config.Bind(Sec, "Copy Key", KeyCode.None, "一键复制当前筛选结果的快捷键。");
            _clearKey = plugin.Config.Bind(Sec, "Clear Key", KeyCode.None, "清空历史的快捷键。");

            SRApi.LocSec(Sec, "通知中心", "Notification Hub");

            RegUi();
            SRApi.Nav(Sec, 118);
            SRApi.RegisterPage(Sec, "History", "通知历史", "History", 10, RenderHistoryPage);
            SRApi.RegisterPage(Sec, "HubOptions", "通知设置", "Settings", 20, RenderOptionsPage);
            SRApi.RegisterKey("通知中心-打开", _openKey, "press");
            SRApi.RegisterKey("通知中心-复制", _copyKey, "press");
            SRApi.RegisterKey("通知中心-清空", _clearKey, "press");
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

                if (_openKey.Value != KeyCode.None && Input.GetKeyDown(_openKey.Value)) SRApi.OpenMenu();
                if (_copyKey.Value != KeyCode.None && Input.GetKeyDown(_copyKey.Value)) CopyFiltered();
                if (_clearKey.Value != KeyCode.None && Input.GetKeyDown(_clearKey.Value)) ClearHistory();
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
                Notice merged = null;
                for (int i = _history.Count - 1; i >= 0 && now - _history[i].At < 3f; i--) {
                    if (_history[i].Key != key) continue;
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
                int lim = Mathf.Clamp(_historyLimit.Value, 100, 2000);
                while (_history.Count > lim) _history.RemoveAt(0);

                bool quiet = _quiet.Value && level < 2 && InMatch();
                if (!native && !quiet && _onScreen.Value) {
                    _visible.Add(n);
                    int max = Mathf.Clamp(_maxVisible.Value, 1, 6);
                    // 高优先级排最前：要动手的卡片永远在最上面。
                    _visible.Sort(delegate (Notice a, Notice b) { return b.Level.CompareTo(a.Level); });
                    while (_visible.Count > max) _visible.RemoveAt(_visible.Count - 1);
                }

                // 写进关卡记录器时间线：低优先级的纯确认不写，避免刷屏。
                if (_writeRec.Value && level >= 1 && _aRecNote != null) {
                    try { _aRecNote(SRApi.T("屏幕提示：", "Toast: ") + text); }
                    catch (Exception ex) { SRApi.Guard.Log("通知中心/写记录", ex); }
                }
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
            float s = AFont();
            bool hc = AContrast();
            _label.fontSize = Mathf.RoundToInt(12f * s);
            _small.fontSize = Mathf.RoundToInt(12f * s);
            _label.normal.textColor = hc ? Color.white : new Color(0.94f, 0.96f, 0.99f, 1f);

            float w = Mathf.Clamp(300f * s, 220f, 460f);
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
                    Rect box = new Rect(x, y, w, h);
                    Fill(box, new Color(0.06f, 0.08f, 0.11f, hc ? 0.97f : 0.86f));
                    // 左侧优先级色条：颜色之外还有「长短」这一重编码（高=最长）。
                    Color bar = n.Level == 2 ? new Color(1f, 0.55f, 0.22f, 1f)
                              : n.Level == 1 ? new Color(0.36f, 0.72f, 1f, 1f)
                              : new Color(0.55f, 0.60f, 0.66f, 1f);
                    Fill(new Rect(x, y, (n.Level == 2 ? 6f : 3f) * s, h), bar);

                    string head = n.Level == 2 ? SRApi.T("重要", "IMPORTANT")
                                : n.Level == 1 ? SRApi.T("提示", "INFO") : SRApi.T("已办", "DONE");
                    GUI.Label(new Rect(x + pad, y + pad * 0.5f, w - pad * 2f, 16f * s),
                              head + (n.Count > 1 ? "  x" + n.Count : ""), _small);
                    GUI.Label(new Rect(x + pad, y + pad * 0.5f + 16f * s, w - pad * 2f - 6f * s, h - pad - 16f * s),
                              n.Text, _label);
                    // 底部剩余时间条：长度随剩余比例缩短，一眼看出还来得及点。
                    float life = n.Until > n.At ? Mathf.Clamp01((n.Until - now) / (n.Until - n.At)) : 0f;
                    Fill(new Rect(x + (n.Level == 2 ? 6f : 3f) * s, y + h - 3f * s, (w - (n.Level == 2 ? 6f : 3f) * s) * life, 3f * s),
                         new Color(bar.r, bar.g, bar.b, 0.85f));
                    y += h + 4f * s;
                }
            } finally {
                GUI.depth = prevDepth;
                GUI.color = prevColor;
            }
        }

        // ============ 历史页 ============

        private static List<Notice> Filtered() {
            List<Notice> r = new List<Notice>();
            for (int i = _history.Count - 1; i >= 0; i--) {   // 新的在前
                Notice n = _history[i];
                if (_levelFilter == 1 && n.Level < 1) continue;
                if (_levelFilter == 2 && n.Level < 2) continue;
                if (_onlyReopen && !n.Reopenable) continue;
                if (!string.IsNullOrEmpty(_filter) && !Contains(n.Text, _filter) && !Contains(n.Tag, _filter)) continue;
                r.Add(n);
            }
            return r;
        }

        private static string LevelName(int lv) {
            return lv == 2 ? SRApi.T("重要", "High") : lv == 1 ? SRApi.T("一般", "Normal") : SRApi.T("确认", "Low");
        }

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderHistoryPage() {
            List<Notice> list = Filtered();
            int pages = Mathf.Max(1, Mathf.CeilToInt((float)list.Count / PageSize));
            if (_page >= pages) _page = pages - 1;
            if (_page < 0) _page = 0;

            Line("共 " + _history.Count + " 条历史，当前筛选 " + list.Count + " 条；第 " + (_page + 1) + " / " + pages + " 页。",
                 _history.Count + " notices, " + list.Count + " shown; page " + (_page + 1) + " / " + pages + ".");

            // 筛选状态是纯查看态，不写进 cfg：用按钮循环切换，避免为了两个临时开关造配置键。
            _filter = SRApi.ClearableTextField(_filter, 40, SRApi.Ctl.SearchBox);
            string lvLabel = _levelFilter == 0 ? SRApi.T("级别：全部", "Level: all")
                           : _levelFilter == 1 ? SRApi.T("级别：中 / 高", "Level: normal+")
                           : SRApi.T("级别：仅重要", "Level: high only");
            if (SRApi.Ctl.Button(lvLabel, SRApi.T("在 全部 / 中高 / 仅重要 之间切换", "Cycle all / normal+ / high only")))
                _levelFilter = (_levelFilter + 1) % 3;
            if (SRApi.Ctl.Button(_onlyReopen ? SRApi.T("仅可补点：开", "Reopenable only: on")
                                             : SRApi.T("仅可补点：关", "Reopenable only: off"),
                                 SRApi.T("只显示提案类、可从历史重新打开的条目", "Show only proposal notices that can be reopened")))
                _onlyReopen = !_onlyReopen;

            if (SRApi.Ctl.Button(SRApi.T("上一页", "Prev"), SRApi.T("往前翻一页", "Previous page")) && _page > 0) _page--;
            if (SRApi.Ctl.Button(SRApi.T("下一页", "Next"), SRApi.T("往后翻一页", "Next page")) && _page + 1 < pages) _page++;
            if (SRApi.Ctl.Button(SRApi.T("跳到最新", "Newest"), SRApi.T("回到第一页", "Back to the newest page"))) { _page = 0; _detail = -1; }
            if (SRApi.Ctl.Button(SRApi.T("复制当前筛选", "Copy shown"), SRApi.T("把当前筛选结果复制到剪贴板", "Copy the filtered notices"))) CopyFiltered();
            if (SRApi.Ctl.Button(SRApi.T("导出到文件", "Export"), SRApi.T("写入 BepInEx/config/SR_UCH/", "Write to BepInEx/config/SR_UCH/"))) ExportToFile();
            if (SRApi.Ctl.Button(SRApi.T("清空历史", "Clear"), SRApi.T("清空全部历史（不可撤销）", "Clear all history"))) ClearHistory();

            GUILayout.Space(SRApi.Ctl.Sc(6f));
            int from = _page * PageSize;
            int to = Mathf.Min(list.Count, from + PageSize);
            for (int i = from; i < to; i++) {
                Notice n = list[i];
                string label = "T+" + n.At.ToString("F1") + "  [" + LevelName(n.Level) + "]  " + OneLine(n.Text)
                             + (n.Count > 1 ? "  x" + n.Count : "");
                if (SRApi.Ctl.Button(label, SRApi.T("点击展开详情", "Click to expand details"))) {
                    _detail = (_detail == i) ? -1 : i;
                }
                if (_detail != i) continue;
                Line("完整内容：" + n.Text, "Full text: " + n.Text);
                Line("来源：" + (string.IsNullOrEmpty(n.Tag) ? "SR" : n.Tag) + "；等级：" + LevelName(n.Level)
                     + "；合并次数：" + n.Count + "；时刻 T+" + n.At.ToString("F1"),
                     "Source: " + (string.IsNullOrEmpty(n.Tag) ? "SR" : n.Tag) + "; level: " + LevelName(n.Level)
                     + "; merged: " + n.Count + "; at T+" + n.At.ToString("F1"));
                if (n.Reopenable && _aReopen != null) {
                    if (SRApi.Ctl.Button(SRApi.T("重新打开这条提案", "Reopen this proposal"),
                                         SRApi.T("把最近一条过期提案恢复到待审状态（混乱牌组提供）", "Restore the last expired proposal"))) {
                        try { _aReopen(); } catch (Exception ex) { SRApi.Guard.Log("通知中心/重新打开", ex); }
                    }
                }
            }
            if (list.Count == 0) Line("没有符合条件的记录。", "No notices match this filter.");
        }

        private static string OneLine(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\n", " ").Replace("\r", " ");
            return t.Length > 90 ? t.Substring(0, 90) + "..." : t;
        }

        private static void CopyFiltered() {
            try {
                GUIUtility.systemCopyBuffer = BuildText(Filtered());
            } catch (Exception ex) { SRApi.Guard.Log("通知中心/复制", ex); }
        }

        private static string BuildText(List<Notice> list) {
            StringBuilder sb = new StringBuilder();
            sb.Append(SRApi.T("SR_UCH 通知历史", "SR_UCH notice history")).Append('\n');
            for (int i = 0; i < list.Count; i++) {
                Notice n = list[i];
                sb.Append("T+").Append(n.At.ToString("F1"))
                  .Append(" [").Append(LevelName(n.Level)).Append("] ")
                  .Append(n.Count > 1 ? "(x" + n.Count + ") " : "")
                  .Append(string.IsNullOrEmpty(n.Tag) ? "" : "{" + n.Tag + "} ")
                  .Append(OneLine(n.Text)).Append('\n');
            }
            return sb.ToString();
        }

        private static void ExportToFile() {
            try {
                string dir = Path.Combine(Paths.ConfigPath, "SR_UCH");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "notices.txt");
                File.WriteAllText(path, BuildText(Filtered()), new UTF8Encoding(false));
                SRApi.LogInfo("通知中心/导出 -> " + path);
            } catch (Exception ex) { SRApi.Guard.Log("通知中心/导出", ex); }
        }

        private static void ClearHistory() {
            _history.Clear(); _visible.Clear(); _detail = -1; _page = 0;
        }

        // ============ 设置页 ============

        private void RenderOptionsPage() {
            CfgRow(_enabled);
            CfgRow(_native);
            CfgRow(_onScreen);
            CfgRow(_quiet);
            CfgRow(_maxVisible);
            CfgRow(_toastSeconds);
            CfgRow(_historyLimit);
            CfgRow(_writeRec);
            CfgRow(_corner);
            CfgRow(_openKey);
            CfgRow(_copyKey);
            CfgRow(_clearKey);

            Line("所有屏幕提示都经过一个核心钩子统一收口，包括核心自己的提示、Notify、NotifyT 与 NotifyTag；日志仍由核心照常写入。",
                 "Every toast goes through one core hook: core notices, Notify, NotifyT and NotifyTag. Logging stays with the core.");
            Line("「退回游戏原生提示」打开后，本功能只记历史不改屏幕；这是零风险回退开关。",
                 "'Use Native Toast' restores the game's own toasts while history is still kept; it is a zero-risk rollback switch.");
            Line("对局中静默只影响低/中优先级；提案、失败、拒绝这类要动手的提示永远会弹。静默的条目仍在历史里可查。",
                 "Quiet in match only affects low/normal priority; proposals, failures and refusals always show. Silenced items stay in history.");
            Line("提案过期后仍可在历史里点「重新打开」恢复待审；该按钮由混乱牌组提供，没装那个功能时不会出现。",
                 "Expired proposals can be reopened from history; the button comes from Chaos Deck and disappears if that feature is missing.");
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
