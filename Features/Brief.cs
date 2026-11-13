// 局内简报：把「时间控制 / 混乱牌组 / 品质光芒」三个功能的状态汇总到对局画面里，
// 尤其把「房客提案」从藏在设置页里救到房主眼前。
//
// 为什么不用 SRApi.RegisterOverlay（本轮最重要的结构判断）：
//   SR.Ext.DrawOverlays() 在 SR.Window.DrawGUI() 内部调用（Window.cs:801），
//   而 DrawGUI() 在面板关闭且淡出完成时直接 return（Window.cs:269-274）。
//   所以 RegisterOverlay 的覆盖层只在面板打开/淡出期间绘制 —— 面板一关就没了。
//   要真正的「局内」浮层，只能像 HUD.cs 那样自建常驻 MonoBehaviour 挂 OnGUI。
//
// 跨 DLL 通信：拆分构建下每个 feat rsp 是独立 DLL，只引用 SR_UCH.dll。
//   因此本文件绝不直接引用 Chaos / TimeControl / PartyGlow 的类型，
//   一律走契约层 Slots（名字 + BCL 委托签名）。三个提供方各自 Provide。
//
// 刻意不碰：Harmony 补丁、反射登记、msgType、相机、线程、网络。
//   按钮点击不在 OnGUI 里直接发网络消息：只置一个带帧号的意图，由 Tick 执行，
//   避开 IMGUI 一帧多趟（Layout → 输入 → Repaint）重复触发与绘制中途发消息。

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.brief", "Brief", "1.0")]
    public class Brief : ITweak {
        private const string Sec = "局内简报";

        // 槽位名：形状全部是 BCL 委托，跨 DLL 不需要共享类型。
        private const string SChaos   = "sr.brief.chaos";     // Func<string> 当前牌面，null=无
        private const string SPending = "sr.brief.pending";   // Func<string> 待审提案（含倒计时），null=无
        private const string SAccept  = "sr.brief.accept";    // Action 采纳
        private const string SIgnore  = "sr.brief.ignore";    // Action 忽略
        private const string SRarest  = "sr.brief.rarest";    // Func<string> 本盒最稀有道具，null=无
        private const string SRoster  = "sr.brief.roster";    // Func<string> 房内 mod 清单，null=无

        private static ConfigEntry<bool> _enabled, _folded, _showRoster, _showRarest;
        private static ConfigEntry<int> _corner, _alpha, _scale, _x, _y;
        private static ConfigEntry<KeyCode> _foldKey;

        private static Func<string> _fChaos, _fPending, _fRarest, _fRoster;
        private static Action _aAccept, _aIgnore;
        private static int _bindTries;
        private static float _nextBind;
        // 可读性适配（软依赖）：只查槽位名，缺 DLL 时零 miss、零副作用。
        private static Func<float> _aFont;
        private static Func<bool> _aContrast;
        private static int _aTries;
        private static float _aNext;

        // 按钮意图：OnGUI 只写，Tick 只执行一次。
        private static int _wantAction;        // 0=无 1=采纳 2=忽略 3=折叠
        private static int _wantFrame = -1, _doneFrame = -1;

        // 拖拽：运行时浮点，松手才落盘（避免每帧写配置）。
        private static float _rx = -1f, _ry = -1f;
        private static bool _dragging;
        private static Vector2 _dragOff;

        private static Texture2D _white;
        private static GUIStyle _label, _title, _pending, _btn, _btnHot;
        private static BriefUI _ui;

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
            SRApi.LocKey(Sec, "Enabled", "启用局内简报", "Enable match brief");
            SRApi.LocDesc(Sec, "Enabled",
                "启用局内简报\n对局画面里显示一条简报；没有任何内容时自动隐藏。",
                "Enable match brief\nShow a compact in-game status board; hides itself when there is nothing to show.");
            SRApi.LocKey(Sec, "Folded", "折叠简报", "Folded");
            SRApi.LocDesc(Sec, "Folded",
                "折叠简报\n折叠简报（有待审提案时强制展开）。",
                "Folded\nCollapse the board; a pending proposal always forces it open.");
            SRApi.LocKey(Sec, "Fold Key", "折叠快捷键", "Fold key");
            SRApi.LocDesc(Sec, "Fold Key",
                "折叠快捷键\n折叠 / 展开的快捷键。",
                "Fold key\nToggle folded / expanded.");
            SRApi.LocKey(Sec, "Corner", "停靠角", "Dock corner");
            SRApi.LocDesc(Sec, "Corner",
                "停靠角\n停靠角：0 右上 / 1 左上 / 2 右下 / 3 左下。拖动标题栏可自定义位置。",
                "Dock corner\n0 top-right / 1 top-left / 2 bottom-right / 3 bottom-left. Drag the title bar for a custom spot.");
            SRApi.LocKey(Sec, "Alpha", "背景不透明度", "Background alpha");
            SRApi.LocDesc(Sec, "Alpha",
                "背景不透明度\n背景不透明度百分比（20-100）。",
                "Background alpha\nBackground opacity in percent. Range 20-100.");
            SRApi.LocKey(Sec, "Scale", "整体缩放", "Overall scale");
            SRApi.LocDesc(Sec, "Scale",
                "整体缩放\n整体缩放百分比（70-160）。",
                "Overall scale\nWhole board scale in percent. Range 70-160.");
           SRApi.LocKey(Sec, "Show Rarest", "显示最稀有道具", "Show rarest item");
            SRApi.LocDesc(Sec, "Show Rarest",
                "显示最稀有道具\n显示派对盒里最稀有的道具品质。",
                "Show rarest item\nShow the rarest item tier in the current party box.");
            SRApi.LocKey(Sec, "Show Roster", "显示房内清单", "Show mod roster");
            SRApi.LocDesc(Sec, "Show Roster",
                "显示房内清单\n显示房内 mod 清单（默认关，联机时较占地方）。",
                "Show mod roster\nShow the in-room mod list. Off by default; takes space in online play.");
            SRApi.LocKey(Sec, "Show Deaths", "显示阵亡次数", "Show death count");
            SRApi.LocDesc(Sec, "Show Deaths",
                "显示阵亡次数\n显示本局已阵亡次数（死亡鉴定书提供）。",
                "Show death count\nShow deaths this match, provided by Death Report.");
            SRApi.LocKey(Sec, "Force Obituary", "局末自动展开鉴定书", "Force obituary card");
            SRApi.LocDesc(Sec, "Force Obituary",
                "局末自动展开鉴定书\n一局结束的鉴定书卡片自动展开浮层（一局一次，错过就没了）。",
                "Force obituary card\nAuto-expand the end-of-match report card (once per match).");
            SRApi.LocKey(Sec, "X", "自定义 X", "Custom X");
            SRApi.LocDesc(Sec, "X",
                "自定义 X\n自定义 X；-1 = 跟随停靠角。",
                "Custom X\nCustom X pixel. -1 = follow the dock corner.");
            SRApi.LocKey(Sec, "Y", "自定义 Y", "Custom Y");
            SRApi.LocDesc(Sec, "Y",
                "自定义 Y\n自定义 Y；-1 = 跟随停靠角。",
                "Custom Y\nCustom Y pixel. -1 = follow the dock corner.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Corner": min = 0.0f; max = 3.0f; return true;
                    case "Alpha": min = 20.0f; max = 100.0f; return true;
                    case "Scale": min = 70.0f; max = 160.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "对局画面里显示一条简报；没有任何内容时自动隐藏。");
            _folded = plugin.Config.Bind(Sec, "Folded", false, "折叠简报（有待审提案时强制展开）。");
            _foldKey = plugin.Config.Bind(Sec, "Fold Key", KeyCode.None, "折叠 / 展开的快捷键。");
            _corner = plugin.Config.Bind(Sec, "Corner", 0, new ConfigDescription("停靠角：0 右上 / 1 左上 / 2 右下 / 3 左下。拖动标题栏可自定义位置。", new AcceptableValueRange<int>(0, 3)));
            _alpha = plugin.Config.Bind(Sec, "Alpha", 82, new ConfigDescription("背景不透明度百分比（20-100）。", new AcceptableValueRange<int>(20, 100)));
            _scale = plugin.Config.Bind(Sec, "Scale", 100, new ConfigDescription("整体缩放百分比（70-160）。", new AcceptableValueRange<int>(70, 160)));
            _showRarest = plugin.Config.Bind(Sec, "Show Rarest", true, "显示派对盒里最稀有的道具品质。");
            _showRoster = plugin.Config.Bind(Sec, "Show Roster", false, "显示房内 mod 清单（默认关，联机时较占地方）。");
            _x = plugin.Config.Bind(Sec, "X", -1, "自定义 X；-1 = 跟随停靠角。");
            _y = plugin.Config.Bind(Sec, "Y", -1, "自定义 Y；-1 = 跟随停靠角。");

            SRApi.LocSec(Sec, "局内简报", "Match Brief");

            RegUi();
            SRApi.Nav(Sec, 108);
            SRApi.RegisterPage(Sec, RenderPage);
            SRApi.RegisterKey("局内简报-折叠", _foldKey, "press");
            SRApi.RegisterTick("局内简报-绑定与动作", Tick);

            // 首次绑定延后：MainPlugin 反射发现各 ITweak 的顺序不保证，
            // 等所有 Initialize 跑完再 Require，一次成功、不污染自检的缺失槽位计数。
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;

            if (_ui == null) {
                GameObject go = new GameObject("SR_UCH_Brief");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _ui = go.AddComponent<BriefUI>();
            }
        }

        // ITweak 只有 Initialize（没有 Shutdown 契约）；插件进程生命周期内常驻对象随游戏一起销毁。
        // 这里仍然留一个对称的清理入口，供将来契约补 Shutdown 时直接调用。
        public void Shutdown() {
            if (_ui != null) { try { UnityEngine.Object.Destroy(_ui.gameObject); } catch (Exception ex) { SRApi.Guard.Log("局内简报/销毁对象", ex); } _ui = null; }
            if (_white != null) { try { UnityEngine.Object.Destroy(_white); } catch (Exception ex) { SRApi.Guard.Log("局内简报/销毁贴图", ex); } _white = null; }
        }

        // ============ 槽位绑定与按钮动作 ============

        private static void Tick() {
            try {
                // 重试上限由 _bindTries 控制；缺一个 DLL 不会无限刷 Slots 的 miss 计数。
                BindA11y();
                if (_bindTries < 6 && Time.unscaledTime >= _nextBind) {
                    _bindTries++;
                    TryBind();
                    _nextBind = Time.unscaledTime + 2f;
                }
                if (_foldKey.Value != KeyCode.None && Input.GetKeyDown(_foldKey.Value)) _folded.Value = !_folded.Value;

                // OnGUI 在 Update 之后执行：置位发生在帧 F，Tick 要到帧 F+1 才看得到，
                // 所以不能判「== 当前帧」。改用「意图帧号 != 已处理帧号」，
                // 同时天然挡住 IMGUI 一帧多趟对同一次点击的重复置位。
                if (_wantAction != 0 && _doneFrame != _wantFrame) {
                    _doneFrame = _wantFrame;
                    int a = _wantAction;
                    _wantAction = 0;
                    if (a == 1 && _aAccept != null) _aAccept();
                    else if (a == 2 && _aIgnore != null) _aIgnore();
                    else if (a == 3) _folded.Value = !_folded.Value;
                }
            } catch (Exception ex) { SRApi.Guard.Log("局内简报/Tick", ex); }
        }

        private static void TryBind() {
            // 逐字段独立绑定：删掉某个功能 DLL 只让对应那一行消失，不影响其它行。
            // 以前要求 9 个全齐才置一个「已绑定」标志，那个标志既不控制显示、
            // 又会让注释撒谎；本轮移除，改由 _bindTries 统一控制重试上限。
            // Provide 会清除该名字的 miss 记录（Slots.cs:91），装齐时零残留。
            if (_fChaos   == null) _fChaos   = Slots.Require<Func<string>>(SChaos);
            if (_fPending == null) _fPending = Slots.Require<Func<string>>(SPending);
            if (_aAccept  == null) _aAccept  = Slots.Require<Action>(SAccept);
            if (_aIgnore  == null) _aIgnore  = Slots.Require<Action>(SIgnore);
            if (_fRarest  == null) _fRarest  = Slots.Require<Func<string>>(SRarest);
            if (_fRoster  == null) _fRoster  = Slots.Require<Func<string>>(SRoster);
            bool all = _fChaos != null && _fPending != null &&
                       _aAccept != null && _aIgnore != null && _fRarest != null && _fRoster != null;
            // 全齐就提前停：不再重复 Require，也不污染缺失槽位计数。
            if (all) _bindTries = 6;
        }

        private static string Safe(Func<string> f) {
            if (f == null) return null;
            try { string s = f(); return string.IsNullOrEmpty(s) ? null : s; }
            catch (Exception ex) { SRApi.Guard.Log("局内简报/读状态", ex); return null; }
        }

        // ============ 绘制（由常驻组件的 OnGUI 调用，屏幕坐标、无 GUI.matrix 缩放） ============

        // 可读性适配：字号倍率与高对比。本页自己的 Scale 仍然生效，两者相乘。
        private static void BindA11y() {
            if (_aTries >= 6 || Time.unscaledTime < _aNext) return;
            _aTries++; _aNext = Time.unscaledTime + 2f;
            foreach (string n in Slots.Names) {
                if (n == "sr.a11y.fontScale" && _aFont == null) _aFont = Slots.Require<Func<float>>(n);
                else if (n == "sr.a11y.highContrast" && _aContrast == null) _aContrast = Slots.Require<Func<bool>>(n);
            }
        }
        private static float AFont() {
            try { return _aFont != null ? Mathf.Clamp(_aFont(), 0.8f, 1.6f) : 1f; }
            catch (Exception ex) { SRApi.Guard.Log("局内简报/字号", ex); return 1f; }
        }
        private static bool AContrast() {
            try { return _aContrast != null && _aContrast(); }
            catch (Exception ex) { SRApi.Guard.Log("局内简报/对比", ex); return false; }
        }

        private static float S() { return Mathf.Clamp(_scale.Value / 100f * AFont(), 0.7f, 2.2f); }

        private static void EnsureStyles() {
            if (_white == null) {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.name = "SR_UCH_BriefWhite";
                _white.hideFlags = HideFlags.HideAndDontSave;
                _white.SetPixel(0, 0, Color.white);
                _white.Apply(false, true);
            }
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label);
            _label.fontSize = 13; _label.wordWrap = false; _label.clipping = TextClipping.Clip;
            _label.normal.textColor = new Color(0.93f, 0.95f, 0.98f, 1f);
            _title = new GUIStyle(_label);
            _title.fontStyle = FontStyle.Bold; _title.fontSize = 13;
            _title.normal.textColor = new Color(0.66f, 0.83f, 1f, 1f);
            _pending = new GUIStyle(_label);
            _pending.normal.textColor = new Color(1f, 0.90f, 0.55f, 1f);
            _pending.wordWrap = true;
            _btn = new GUIStyle(GUI.skin.button);
            _btn.fontSize = 12; _btn.padding = new RectOffset(4, 4, 2, 2);
            _btnHot = new GUIStyle(_btn);
            _btnHot.normal.textColor = new Color(1f, 0.95f, 0.7f, 1f);
        }

        private static void Fill(Rect r, Color c) {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        private static void DrawBrief() {
            if (_enabled == null || !_enabled.Value || Event.current == null) return;

            // 内容全部来自槽位；一条都没有就整块不画（不常驻空框）。
            string chaos = Safe(_fChaos);
            string rarest = _showRarest.Value ? Safe(_fRarest) : null;
            string roster = _showRoster.Value ? Safe(_fRoster) : null;
            string pending = Safe(_fPending);
            if (chaos == null && rarest == null && roster == null &&
                pending == null) return;

            EnsureStyles();
            float s = S();
            bool hc = AContrast();
            _label.normal.textColor = hc ? Color.white : new Color(0.93f, 0.95f, 0.98f, 1f);
            _title.normal.textColor = hc ? new Color(1f, 1f, 0.72f, 1f) : new Color(0.66f, 0.83f, 1f, 1f);
            _pending.normal.textColor = hc ? new Color(1f, 0.95f, 0.62f, 1f) : new Color(1f, 0.90f, 0.55f, 1f);
            // 样式是缓存实例，字号按缩放每趟同步一次（属性赋值，不新建对象）。
            _label.fontSize = Mathf.RoundToInt(13f * s);
            _title.fontSize = Mathf.RoundToInt(13f * s);
            _pending.fontSize = Mathf.RoundToInt(13f * s);
            _btn.fontSize = Mathf.RoundToInt(12f * s);
            _btnHot.fontSize = Mathf.RoundToInt(12f * s);
            float pad = 10f * s, rowH = 22f * s, headH = 26f * s;
            float w = 264f * s;
            float textW = w - pad * 2f - 12f * s;
            int rows = 0;
            if (chaos != null) rows++;
            if (rarest != null) rows++;
            if (roster != null) rows++;
            // 待审提案、局末鉴定书都强制展开：这两样都是「错过就没了」的一次性信息。
            // 鉴定书的强制展开可由配置关掉（有人只想安安静静打完一局）。
            bool forced = pending != null;
            bool folded = _folded.Value && !forced;
            float pendH = (!folded && pending != null) ? (rowH * 2f + 28f * s + 6f * s) : 0f;
            // CalcHeight 不依赖 GUILayout/GetLastRect，绝对 Rect 绘制下所有趟都可用。
            float obitH = 0f;
            float h = headH + pad + (folded ? 0f : rows * rowH) + pendH + obitH + pad;

            float x, y;
            if (_rx >= 0f && _ry >= 0f) { x = _rx; y = _ry; }
            else if (_x.Value >= 0 && _y.Value >= 0) { x = _x.Value; y = _y.Value; }
            else {
                int c = Mathf.Clamp(_corner.Value, 0, 3);
                x = (c == 1 || c == 3) ? 12f : Screen.width - w - 12f;
                y = (c == 2 || c == 3) ? Screen.height - h - 12f : 12f;
            }
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - w));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - h));
            Rect box = new Rect(x, y, w, h);
            Rect head = new Rect(x, y, w, headH);

            int prevDepth = GUI.depth;
            Color prevColor = GUI.color;
            try {
                GUI.depth = 2;   // 比 HUD.Draw.cs 的 1 更靠上；finally 还原，不污染全局
                Fill(box, hc ? new Color(0.02f, 0.02f, 0.03f, Mathf.Max(_alpha.Value / 100f, 0.96f))
                             : new Color(0.07f, 0.09f, 0.13f, _alpha.Value / 100f));
                Fill(new Rect(x, y, w, 2f * s), new Color(0.31f, 0.62f, 1f, 0.95f));

                // 标题栏：可拖拽；点击右侧按钮折叠。
                GUI.Label(new Rect(x + pad, y + 3f * s, w - 60f * s, headH - 4f * s),
                          SRApi.T("SR · 局内简报", "SR · Match Brief"), _title);
                if (GUI.Button(new Rect(x + w - 30f * s - pad, y + 3f * s, 30f * s, headH - 7f * s),
                               folded ? "+" : "-", _btn)) {
                    _wantAction = 3; _wantFrame = Time.frameCount;
                }
                // 右端 42*s 是折叠按钮，排除在拖拽热区外，否则点按钮会进入拖拽态。
                HandleDrag(new Rect(head.x, head.y, head.width - 42f * s, head.height), box);

                if (folded) return;

                float cy = y + headH + pad * 0.5f;
                // 标签用纯中英文本：特殊符号（◷ ✦ ◆ ◈ ▶ ▼）在游戏内置字体里常常缺字成方框。
                if (chaos != null)  { GUI.Label(new Rect(x + pad, cy, w - pad * 2f, rowH), SRApi.T("牌面：", "Card: ") + chaos, _label); cy += rowH; }
                if (rarest != null) { GUI.Label(new Rect(x + pad, cy, w - pad * 2f, rowH), SRApi.T("稀有：", "Rare: ") + rarest, _label); cy += rowH; }
                if (roster != null) { GUI.Label(new Rect(x + pad, cy, w - pad * 2f, rowH), SRApi.T("房间：", "Room: ") + roster, _label); cy += rowH; }

                // 待审提案在最上：要动手的卡片优先于只读卡片。
                if (pending != null) {
                    float py = cy + 4f * s;
                    Rect pr = new Rect(x + pad, py, w - pad * 2f, rowH * 2f + 4f * s);
                    Fill(pr, hc ? new Color(0.48f, 0.26f, 0.02f, 0.95f) : new Color(0.55f, 0.38f, 0.08f, 0.55f));
                    GUI.Label(new Rect(pr.x + 6f * s, pr.y + 3f * s, pr.width - 12f * s, rowH * 2f),
                              pending, _pending);
                    float by = py + rowH * 2f + 6f * s;
                    if (GUI.Button(new Rect(x + pad, by, 76f * s, 24f * s),
                                   SRApi.T("采纳", "Accept"), _btnHot)) {
                        _wantAction = 1; _wantFrame = Time.frameCount;
                    }
                    if (GUI.Button(new Rect(x + pad + 82f * s, by, 76f * s, 24f * s),
                                   SRApi.T("忽略", "Ignore"), _btn)) {
                        _wantAction = 2; _wantFrame = Time.frameCount;
                    }
                    cy += pendH;
                }

                // 死亡鉴定书：一局一次，暖棕卡片，只读。
            } finally {
                GUI.depth = prevDepth;
                GUI.color = prevColor;
            }
        }

        private static void HandleDrag(Rect dragArea, Rect box) {
            Event e = Event.current;
            if (e == null) return;
            Vector2 mp = e.mousePosition;
            // MouseDown / MouseUp 一律不 Use()：GUI.Button 在本函数之前绘制，需要完整收到
            // 按下与抬起才会返回 true。只在 MouseDrag 消费，拖窗口不会带跑页面滚动。
            if (e.type == EventType.MouseDown && e.button == 0 && dragArea.Contains(mp)) {
                _dragging = true;
                _dragOff = new Vector2(mp.x - box.x, mp.y - box.y);
                _rx = box.x; _ry = box.y;
            } else if (_dragging && e.type == EventType.MouseDrag) {
                _rx = mp.x - _dragOff.x;
                _ry = mp.y - _dragOff.y;
                e.Use();
            } else if (_dragging && e.type == EventType.MouseUp) {
                _dragging = false;
                _x.Value = Mathf.RoundToInt(_rx);
                _y.Value = Mathf.RoundToInt(_ry);
                try { SRApi.Ctl.MarkConfigDirty(_x.ConfigFile); }
                catch (Exception ex) { SRApi.Guard.Log("局内简报/保存位置", ex); }
            }
        }

        // ============ 设置页 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            CfgRow(_enabled);
            CfgRow(_folded);
            CfgRow(_foldKey);
            CfgRow(_corner);
            CfgRow(_alpha);
            CfgRow(_scale);
            CfgRow(_showRarest);
            CfgRow(_showRoster);

            Line("浮层只在有内容时出现；有待审提案时即使折叠也会强制展开。",
                 "The strip only appears when there is something to show. A pending proposal force-expands it.");
            Line("拖动标题栏可自定义位置；右上角按钮或快捷键折叠。位置写在配置里，重开游戏仍在。",
                 "Drag the title bar to reposition; use the corner button or hotkey to fold. Position persists in the config.");
            Line("状态一律走契约层槽位读取，本页不直接引用其他功能：删除某个功能只会让对应那一行消失。",
                 "All state comes through contract slots; removing a feature simply removes its line.");
        }

        // 常驻组件：面板关闭时 DrawGUI 会早退，所以必须自己挂 OnGUI。
        private sealed class BriefUI : MonoBehaviour {
            private void OnGUI() {
                try { DrawBrief(); }
                catch (Exception ex) { SRApi.Guard.Log("局内简报/绘制", ex); }
            }
        }
    }
}
