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
    [SrFeature("sr.notifyhub", "通知中心", "1.0", Provides = new string[] { "sr.notify.hook" })]
    public class NotifyHub : ITweak {
        private const string Sec = "通知中心";
        private const string HookSlot = "sr.notify.hook";

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
            public int Count = 1;
            public float At;          // 最近一次到达时刻（合并窗口 / 排序用）
            public float Stay;        // 每次显示时长（秒）
            public float ShownAt;     // 首次真正上屏的时刻；0 = 还在排队（排队期间不开始过期）
            public float EnterAt;     // 入场动画起点；0 = 尚未开始（续期不重播入场）
            public float DyingAt;     // >0 = 正在退场（淡出 + 侧滑），到时从列表移除
            public float VisY;        // 绘制纵向位置（新卡插入/退场时平滑让位）
            public bool VisSet;       // VisY 是否已初始化
        }

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _maxVisible, _corner, _cardW, _cardAlpha, _cardScale, _fontSize;
        private static ConfigEntry<float> _toastSeconds;
        private static bool _toastFixing;  // 停留秒数归整到 0.5 步进时的递归保护

        private static readonly List<Notice> _history = new List<Notice>();
        private static readonly List<Notice> _visible = new List<Notice>();
        // 排队上限：Max Visible 只决定「一次画几条」，超出的排队等前一条退场后再上屏，
        // 因此快速连发也不会互相顶掉；只有排到 16 条还放不下才丢最旧的。
        private const int PendingCap = 16;
        private static int _animFrame = -1;   // 一帧内 OnGUI 会跑多趟：动画时间每帧只取一次
        private static float _animDt;
        // 鼠标悬停在浮层卡片上：暂停过期计时（看长提示时不会被自动吞掉）。
        // 由 DrawToast 每次绘制时写入，Tick 读取（OnGUI 每帧多趟，取最后一趟的结果即可）。
        private static bool _hoverAny;

        // 动画固定内置（用户要求删除「启用动画 / 动画时长」两项配置）。
        private static float AnimIn() { return 0.22f; }
        private static float AnimOut() { return 0.20f; }

        private static Texture2D _white;
        private static GUIStyle _label, _btn;
        private static HubUI _ui;

        // 自己 Provide 的委托实例：注销时按值匹配，避免误删之后注册的实现。
        private static readonly System.Collections.Generic.Dictionary<string, object> _provided =
            new System.Collections.Generic.Dictionary<string, object>();

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
            SRApi.LocKey(Sec, "Max Visible", "同时显示条数", "Max visible");
            SRApi.LocDesc(Sec, "Max Visible",
                "同时显示条数\n浮层最多同时显示几条（1-6）。",
                "Max visible\nHow many toasts show at once. Range 1-6.");
            SRApi.LocKey(Sec, "Toast Seconds", "停留秒数", "Toast seconds");
            SRApi.LocDesc(Sec, "Toast Seconds",
                "停留秒数\n每条通知停留多长时间。\n按 0.5 秒为一档。",
                "Toast seconds\nHow long each toast stays.\n0.5 s per step.");
            SRApi.LocKey(Sec, "Font Size", "文字大小", "Font size");
            SRApi.LocDesc(Sec, "Font Size",
                "文字大小\n正文文字的大小。\n越大越容易看清，但同样内容占的空间更多。",
                "Font size\nSize of the toast text.\nLarger is easier to read but takes more room.");
            SRApi.LocKey(Sec, "Card Width", "卡片宽度", "Card width");
            SRApi.LocDesc(Sec, "Card Width",
                "卡片宽度\n浮层卡片的像素宽度。\n越宽可容纳的正文越多。",
                "Card width\nOverlay card width in pixels.\nWider cards fit more text.");
            SRApi.LocKey(Sec, "Card Scale", "浮层缩放", "Overlay scale");
            SRApi.LocDesc(Sec, "Card Scale",
                "浮层缩放\n整个浮层按百分比缩放。\n百分比越大卡片越大。",
                "Overlay scale\nScale the whole overlay in percent.\nLarger values give larger cards.");
            SRApi.LocKey(Sec, "Card Opacity", "卡片不透明度", "Card opacity");
            SRApi.LocDesc(Sec, "Card Opacity",
                "卡片不透明度\n浮层卡片背景的不透明程度。",
                "Card opacity\nBackground opacity of overlay cards.");
            SRApi.LocKey(Sec, "Corner", "停靠位置", "Overlay position");
            SRApi.LocDesc(Sec, "Corner",
                "浮层停靠角\n浮层停靠位置：0 右下 / 1 右上 / 2 左下 / 3 左上。",
                "Overlay corner\n0 bottom-right / 1 top-right / 2 bottom-left / 3 top-left.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Max Visible": min = 1.0f; max = 6.0f; return true;
                    case "Toast Seconds": min = 0.5f; max = 5.0f; fmt = "0.0"; return true;
                    case "Corner": min = 0.0f; max = 3.0f; return true;
                    case "Card Opacity": min = 20.0f; max = 100.0f; return true;
                    case "Card Scale": min = 100.0f; max = 300.0f; return true;
                    case "Card Width": min = 100.0f; max = 500.0f; return true;
                    case "Font Size": min = 10.0f; max = 32.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "统一接管所有屏幕提示：分级、合并、静默。");
            // 旧配置项已删除：打开通知中心 = 用通知中心；关闭 = 交回游戏原生提示。
            try { SRApi.Migration.Remove(Sec, "Use Native Toast"); } catch (Exception __mr) { SRApi.Guard.Log("通知中心/清理旧键1", __mr); }
            try { SRApi.Migration.Remove(Sec, "Show On Screen"); } catch (Exception __mr2) { SRApi.Guard.Log("通知中心/清理旧键2", __mr2); }
            _maxVisible = plugin.Config.Bind(Sec, "Max Visible", 3,
                new ConfigDescription("浮层最多同时显示几条（1-6）。", new AcceptableValueRange<int>(1, 6)));
            _toastSeconds = plugin.Config.Bind(Sec, "Toast Seconds", 1.5f,
                new ConfigDescription("每条通知停留秒数（0.5-5，步进 0.5）。", new AcceptableValueRange<float>(0.5f, 5f)));
            _cardW = plugin.Config.Bind(Sec, "Card Width", 300,
                new ConfigDescription("浮层卡片像素宽度（100-500）。", new AcceptableValueRange<int>(100, 500)));
            _fontSize = plugin.Config.Bind(Sec, "Font Size", 17,
                new ConfigDescription("正文文字大小（10-32）。", new AcceptableValueRange<int>(10, 32)));
            // 旧配置项已删除，启动时从 cfg 清理。
            try { SRApi.Migration.Remove(Sec, "Quiet In Match"); } catch (Exception __mq) { SRApi.Guard.Log("通知中心/清理静默键", __mq); }
            try { SRApi.Migration.Remove(Sec, "Animate"); } catch (Exception __ma) { SRApi.Guard.Log("通知中心/清理动画键", __ma); }
            try { SRApi.Migration.Remove(Sec, "Anim Ms"); } catch (Exception __mm) { SRApi.Guard.Log("通知中心/清理动画时长键", __mm); }
            _cardScale = plugin.Config.Bind(Sec, "Card Scale", 200,
                new ConfigDescription("整个浮层的缩放百分比（100-300）。", new AcceptableValueRange<int>(100, 300)));
            _cardAlpha = plugin.Config.Bind(Sec, "Card Opacity", 64,
                new ConfigDescription("浮层卡片背景不透明度百分比（20-100）。", new AcceptableValueRange<int>(20, 100)));
            _corner = plugin.Config.Bind(Sec, "Corner", 0,
                new ConfigDescription("浮层停靠位置：0 右下 / 1 右上 / 2 左下 / 3 左上。", new AcceptableValueRange<int>(0, 3)));

            SRApi.LocSec(Sec, "通知", "Notifications");

            RegUi();
            // 底栏入口（设置右侧），不再占用侧栏栏目（用户要求）。
            SRApi.RegisterFooterButton(Sec, "通知", "Notify", null);
            // 本功能有 cfg 段但没有侧栏栏目：显式声明"不单独成栏目"，否则启动自检会把它
            // 当成"没登记归属的兜底段"告警（栏目审计同时也会跳过底栏覆盖的段，双保险）。
            SRApi.NavHide(Sec);
            SRApi.RegisterPage(Sec, "HubOptions", "通知设置", "Settings", 20, RenderOptionsPage);
            SRApi.RegisterTick("通知中心-绑定与过期", Tick);
            // 关掉通知中心时清空待显示队列：否则重新打开会把过期旧消息一次性弹出来。
            _enabled.SettingChanged += (s2, e2) => { if (!_enabled.Value) ClearPending(); };
            // 停留秒数按 0.5 步进：滑条/手输都收敛（防递归用标志位）。
            _toastSeconds.SettingChanged += (s2, e2) => {
                if (_toastFixing) return;
                try {
                    float v = Mathf.Clamp(_toastSeconds.Value, 0.5f, 5f);
                    float r = Mathf.Round(v * 2f) / 2f;
                    if (Mathf.Abs(r - v) > 0.001f) { _toastFixing = true; _toastSeconds.Value = r; _toastFixing = false; }
                } catch (Exception __ts) { SRApi.Guard.Log("通知中心/停留步进", __ts); }
            };

            // 核心 NotifyImpl 会惰性查这个名字；Provide 之后所有来源的提示都会进 OnNotify。
            Func<string, string, bool> __hook = (t, g) => OnNotify(t, g); _provided[HookSlot] = __hook; Slots.Provide(HookSlot, __hook);

            SRApi.OnShutdown("通知中心", Shutdown);
            if (_ui == null) {
                GameObject go = new GameObject("SR_UCH_NotifyHub");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _ui = go.AddComponent<HubUI>();
            }
        }

        // 关机/卸载入口（经 SRApi.OnShutdown 注册）：注销槽位、销毁自建对象与贴图。
        // 槽位必须传自己的委托，否则契约层 expected==null 时会无条件删除。
        public void Shutdown() {
            object impl;
            if (_provided.TryGetValue(HookSlot, out impl) && impl is Func<string, string, bool>) Slots.Retract(HookSlot, impl);
            if (_ui != null) { try { UnityEngine.Object.Destroy(_ui.gameObject); } catch (Exception ex) { SRApi.Guard.Log("通知中心/销毁对象", ex); } _ui = null; }
            if (_white != null) { try { UnityEngine.Object.Destroy(_white); } catch (Exception ex) { SRApi.Guard.Log("通知中心/销毁贴图", ex); } _white = null; }
        }

        // ============ 过期清理 ============

        private static void Tick() {
            try {
                float now = Time.unscaledTime;
                float outDur = AnimOut();
                // 鼠标停在浮层上时冻结所有卡片的时间轴（剩余时间条也一起停）：
                // 把 ShownAt 往前推 = 已显示时长保持不变，松手后继续从暂停处倒计时。
                float hoverDt = _hoverAny ? Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f) : 0f;
                for (int i = _visible.Count - 1; i >= 0; i--) {
                    Notice n = _visible[i];
                    if (n.DyingAt > 0f) {
                        if (now - n.DyingAt >= outDur) _visible.RemoveAt(i);   // 退场播完再真正移除
                        continue;
                    }
                    // 只有真正上屏过的才开始计时：排队期间不消耗停留时间，
                    // 否则连发一批消息时，排在后面的还没轮到就"过期"了（表现为漏消息）。
                    if (n.ShownAt <= 0f) continue;
                    if (hoverDt > 0f) { n.ShownAt += hoverDt; continue; }
                    if (now - n.ShownAt >= Mathf.Max(0.4f, n.Stay)) n.DyingAt = now;
                }
            } catch (Exception ex) { SRApi.Guard.Log("通知中心/Tick", ex); }
        }

        private static void ClearPending() {
            try { _visible.Clear(); _history.Clear(); } catch (Exception ex) { SRApi.Guard.Log("通知中心/清空队列", ex); }
        }

        // 队列排序 + 上限：高优先级在前，同级新的在前（Level 相同按到达时间倒序）。
        private static void SortTrim() {
            _visible.Sort(delegate (Notice a, Notice b) {
                int c = b.Level.CompareTo(a.Level);
                return c != 0 ? c : b.At.CompareTo(a.At);
            });
            while (_visible.Count > PendingCap) _visible.RemoveAt(_visible.Count - 1);
        }

        // ============ 通知入口（核心 NotifyImpl 调用） ============

        private static bool _inNotify;

        /// <summary>返回 true = 已接管屏幕显示；false = 让核心照旧用游戏原生提示。</summary>
        private static bool OnNotify(string text, string tag) {
            // 打开通知中心 = 由本功能接管；关闭 = 返回 false，由核心交回游戏原生提示。
            if (_enabled == null || !_enabled.Value) return false;
            // 防重入：外部回调若也发提示，这里直接放行给原生通道，
            // 绝不形成 Notify -> OnNotify -> Notify 的无限递归。
            if (_inNotify) return false;
            if (string.IsNullOrEmpty(text)) return true;         // 空提示不占队列，但已接管屏幕
            _inNotify = true;
            try {
                float now = Time.unscaledTime;
                int level = Classify(text, tag);
                string key = (tag ?? "") + "|" + Norm(text);
                float stay = Mathf.Clamp(_toastSeconds.Value, 0.5f, 5f);

                // 同类合并：3 秒窗口内同 key 只累加计数并续期，不再插入新行。
                Notice merged = null;
                for (int i = _history.Count - 1; i >= 0; i--) {
                    if (_history[i].Key != key) continue;
                    if (now - _history[i].At >= 3f) continue;
                    merged = _history[i]; break;
                }
                if (merged != null) {
                    merged.Count++;
                    merged.At = now;
                    merged.Stay = stay;
                    merged.Level = Mathf.Max(merged.Level, level);
                    // 已经不在队列里（上一轮已经显示完并移除了）：重新排队，保证同一条消息
                    // 在 3 秒窗口内再次出现时不会被"合并掉"而完全看不到。
                    bool queued = false;
                    for (int vi = 0; vi < _visible.Count; vi++) if (_visible[vi].Key == key) { queued = true; break; }
                    if (queued) {
                        bool wasDying = merged.DyingAt > 0f;
                        merged.DyingAt = 0f;
                        merged.ShownAt = now;      // 已在屏上：续满停留时间，不重播入场动画
                        if (wasDying) merged.EnterAt = 0f;   // 退场中被续期：重新淡入
                    } else {
                        merged.ShownAt = 0f;       // 重新排队：等轮到它再开始计时
                        merged.EnterAt = 0f;
                        merged.DyingAt = 0f;
                        merged.VisSet = false;
                        _visible.Add(merged);
                        SortTrim();
                    }
                    return true;
                }

                Notice n = new Notice();
                n.Text = text; n.Tag = tag ?? ""; n.Key = key; n.Level = level;
                n.At = now; n.Stay = stay;

                _history.Add(n);
                while (_history.Count > 64) _history.RemoveAt(0);

                _visible.Add(n);
                SortTrim();
                return true;
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
            _btn = new GUIStyle(GUI.skin.button);
        }

        private static void Fill(Rect r, Color c) {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        private static void DrawToast() {
            if (_enabled == null || !_enabled.Value || _visible.Count == 0 || Event.current == null) return;
            EnsureStyles();
            float s = Mathf.Clamp(_cardScale.Value / 160f, 0.6f, 1.8f);
            // 参考 imgui-notify 的观感：更大的字号/内边距，卡片最宽不超过屏幕 1/3。
            _label.fontSize = Mathf.RoundToInt((_fontSize != null ? _fontSize.Value : 17) * s);
            _label.normal.textColor = new Color(0.94f, 0.96f, 0.99f, 1f);

            float w = Mathf.Clamp(_cardW.Value * s, 120f, Mathf.Min(Screen.width / 3f, 700f));
            float pad = 10f * s;
            float gap = 6f * s;
            float now = Time.unscaledTime;
            float dIn = AnimIn();
            float dOut = AnimOut();

            // 一帧里 OnGUI 会跑多趟（Layout/Repaint/事件），动画时间只能取一次。
            if (_animFrame != Time.frameCount) {
                _animFrame = Time.frameCount;
                _animDt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
            }
            // 纵向位置过渡：指数逼近、帧率无关；关掉动画时直接把位置对齐目标。
            float k = (_animDt > 0.001f) ? (1f - Mathf.Exp(-14f * _animDt)) : 1f;

            // 一次最多画 Max Visible 条；超出的排队（退场中的必须继续画完，不能瞬间消失）。
            int cap = Mathf.Clamp(_maxVisible.Value, 1, 6);
            int n = _visible.Count;
            float[] heights = new float[n];
            bool[] draw = new bool[n];
            int drawnCount = 0;
            float total = 0f;
            for (int i = 0; i < n; i++) {
                Notice it = _visible[i];
                float th = Mathf.Max(22f * s, _label.CalcHeight(new GUIContent(it.Text), w - pad * 2f - 14f * s));
                heights[i] = th + pad * 2f + gap;
                if (it.DyingAt > 0f || drawnCount < cap) { draw[i] = true; drawnCount++; total += heights[i]; }
            }
            if (drawnCount == 0) return;
            // 被更高优先级挤到画面外的卡片退回排队：不消耗它的停留时间，等下次轮到再完整显示，
            // 否则"来了更重要的提示"会把正在显示的低优先级提示挤掉、而且它还没看满时间就过期了。
            for (int i = 0; i < n; i++) {
                if (draw[i]) continue;
                Notice it = _visible[i];
                if (it.DyingAt > 0f) continue;
                if (it.ShownAt > 0f) { it.ShownAt = 0f; it.EnterAt = 0f; it.VisSet = false; }
            }

            int corner = Mathf.Clamp(_corner.Value, 0, 3);
            float x = (corner == 2 || corner == 3) ? 12f : Screen.width - w - 12f;
            float yStart = (corner == 0 || corner == 2) ? Screen.height - total - 12f : 12f;
            bool fromLeft = (corner == 2 || corner == 3);

            int prevDepth = GUI.depth;
            Color prevColor = GUI.color;
            _hoverAny = false;   // 本趟重新判定；鼠标不在任何卡片上时 Tick 恢复正常倒计时
            try {
                GUI.depth = 3;   // 比局内简报(2) 更靠上；finally 还原，不污染全局
                float target = yStart;
                for (int i = 0; i < n; i++) {
                    if (!draw[i]) continue;
                    Notice nt = _visible[i];
                    float h = heights[i];
                    // 位置过渡：新卡插到上面 / 前一条退场，都会让目标 Y 变化；这里平滑过去，
                    // 而不是让整列卡片瞬移（这就是"过渡"的第二层）。
                    if (!nt.VisSet) { nt.VisY = target; nt.VisSet = true; }
                    else nt.VisY = (k >= 1f) ? target : Mathf.Lerp(nt.VisY, target, k);
                    float y = nt.VisY;

                    // 入场：淡入 + 从停靠侧滑入 10px（三次缓出）；退场：淡出 + 同侧滑出。
                    if (nt.EnterAt <= 0f) {
                        nt.EnterAt = now;
                        if (nt.ShownAt <= 0f) nt.ShownAt = now;
                    }
                    float aa = 1f;
                    {
                        float age = Mathf.Max(0f, now - nt.EnterAt);
                        float t = Mathf.Clamp01(age / dIn);
                        float ease = 1f - (1f - t) * (1f - t) * (1f - t);
                        aa = ease;
                    }
                    if (nt.DyingAt > 0f) aa *= Mathf.Clamp01(1f - (now - nt.DyingAt) / dOut);
                    aa = Mathf.Clamp01(aa);
                    float slide = (aa < 1f) ? (1f - aa) * 10f * s : 0f;
                    float ax = x + (fromLeft ? -slide : slide);

                    Color prevGui = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, aa);
                    Rect box = new Rect(ax, y, w, h);
                    // 悬停：暂停计时（Tick 读 _hoverAny），并给卡片加一圈浅描边提示"可以停住看"；
                    // 左键点击：立即开始退场（点击不会穿透到游戏——浮层在 IMGUI 的最上层）。
                    bool hovered = false;
                    Event ev = Event.current;
                    if (ev != null && nt.DyingAt <= 0f) {
                        hovered = box.Contains(ev.mousePosition);
                        if (hovered) {
                            _hoverAny = true;
                            if (ev.type == EventType.MouseDown && ev.button == 0) {
                                nt.DyingAt = now;
                                nt.Stay = 0f;
                            }
                        }
                    }
                    float cardA = Mathf.Clamp01(_cardAlpha.Value / 100f) * aa;
                    Fill(box, new Color(0.06f, 0.08f, 0.11f, cardA));
                    if (hovered) {
                        Color hc = new Color(0.95f, 0.97f, 1f, 0.55f * aa);
                        float ht = Mathf.Max(1f, 1f * s);
                        Fill(new Rect(box.x, box.y, box.width, ht), hc);
                        Fill(new Rect(box.x, box.yMax - ht, box.width, ht), hc);
                        Fill(new Rect(box.x, box.y, ht, box.height), hc);
                        Fill(new Rect(box.xMax - ht, box.y, ht, box.height), hc);
                    }
                    // 左侧优先级色条：颜色之外还有「长短」这一重编码（高=最长）。
                    Color bar = nt.Level == 2 ? new Color(1f, 0.55f, 0.22f, 1f)
                              : nt.Level == 1 ? new Color(0.36f, 0.72f, 1f, 1f)
                              : new Color(0.55f, 0.60f, 0.66f, 1f);
                    float barW = (nt.Level == 2 ? 6f : 3f) * s;
                    Fill(new Rect(ax, y, barW, h), new Color(bar.r, bar.g, bar.b, aa));
                    // 高优先级加一圈同色描边：颜色之外再给一层"更醒目"的编码（不新增素材）。
                    if (nt.Level == 2) {
                        Color oc = new Color(bar.r, bar.g, bar.b, 0.55f * aa);
                        float t = Mathf.Max(1f, 1f * s);
                        Fill(new Rect(box.x, box.y, box.width, t), oc);
                        Fill(new Rect(box.x, box.yMax - t, box.width, t), oc);
                        Fill(new Rect(box.x, box.y, t, box.height), oc);
                        Fill(new Rect(box.xMax - t, box.y, t, box.height), oc);
                    }

                    // 只显示通知正文（用户要求去掉「提示/已办/重要」这类前缀）。
                    // 只显示正文（用户要求去掉「xN」计数）。
                    // 文本区底部 +2px 缓冲：游戏字体最后一笔常被 CalcSize 少算，
                    // 不加缓冲时"y/g/p"这类下伸笔画的最后一行会被裁掉（用户实测 1px 仍偏紧）。
                    GUI.Label(new Rect(ax + pad, y + pad * 0.6f, w - pad * 2f - 6f * s, h - pad * 1.6f), nt.Text, _label);
                    // 底部剩余时间条：从满格开始缩短（排队期间还没上屏，不画消耗）。
                    float life = Mathf.Clamp01(1f - Mathf.Max(0f, now - nt.ShownAt) / Mathf.Max(0.4f, nt.Stay));
                    Fill(new Rect(ax + barW, y + h - 3f * s, (w - barW) * life, 3f * s),
                         new Color(bar.r, bar.g, bar.b, 0.85f * aa));
                    GUI.color = prevGui;
                    target += h;
                }
            } finally {
                GUI.depth = prevDepth;
                GUI.color = prevColor;
            }
        }

        // ============ 设置页 ============

        private void RenderOptionsPage() {
            CfgRow(_enabled);
            CfgRow(_maxVisible);
            CfgRow(_toastSeconds);
            CfgRow(_fontSize);
            CfgRow(_cardW);
            CfgRow(_cardAlpha);
            CfgRow(_cardScale);
            // 停靠位置：标签「位置」+ 右侧下拉（四种位置），默认右下。
            {
                string[] vals = new string[] { "0", "1", "2", "3" };
                string[] labels = new string[] {
                    SRApi.T("右下", "bottom-right"),
                    SRApi.T("右上", "top-right"),
                    SRApi.T("左下", "bottom-left"),
                    SRApi.T("左上", "top-left")
                };
                SRApi.Ctl.RowCombo(SRApi.T("位置", "Position"),
                    SRApi.T("通知浮层停在屏幕的哪个角。", "Which screen corner the toasts dock to."),
                    _corner, vals, labels, SRApi.Ctl.Sc(160f));
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
