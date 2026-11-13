// 死亡鉴定书：一局结束时给一张「怎么死的」回顾卡片。
//
// 游戏侧证据（反编译快照 221,827 行，全部 public，无需 Harmony / 反射）：
//   Character.KillCharacter(string cause, bool deathFreezeOn, int causedByPlayerNumber, bool force)  :59681
//   setupDeath(...) 里 LastDeath = cause                                                          :61344
//   GameEventManager.SendEvent(new PlayerKilledEvent(associatedGamePlayer.LocalPlayer, cause))   :61389
//       ↑ 被 if (base.hasAuthority) 包着 → 只在本机角色死亡时发，纯本地，零网络
//   PlayerKilledEvent { readonly string Cause; readonly Player Player; }                          :151690
//   PlayerKilledEvent.Cause 就是 KillCharacter 的 cause（= Placeable.KillType / Name）
//   PiecePlacedEvent { readonly int PlayerNumber; readonly Placeable PlacedBlock; }               :151717
//   Placeable.Name / KillType / placedByPlayerNumber 全为 public                            :11995/11997/12131
//   GameControl.RoundNumber 是 public get                                                        :124768
//   NewMatchEvent :124879    GameEndEvent :125940（完成）/ :126033（放弃）
//
// 一条必须说清的限制（所以措辞是「疑似」而不是「凶手」）：
//   PlayerKilledEvent 不带 causedByPlayerNumber。那个参数只在 setupDeath 内部
//   用于给凶手加分（:61313），且 AwardPoint 在联机时发网络消息而不落本地
//   newPointBlocks（:142993）。所以精确归因拿不到。
//   本功能改用「按 Cause 匹配本局最近一次放置的同名方块」推出疑似元凶 —— 同类方块
//   有多块时可能认错，因此界面上明确写「疑似」，并且可以关掉这一项。
//
// 刻意不碰：Harmony 补丁、反射登记、msgType、相机、线程、网络。
//   全部数据都是值（string / int / float），不持任何 Unity 对象引用 —— 方块销毁不派
//   事件（历轮审查结论），持引用必然读到已回池的对象。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.deathreport", "DeathReport", "1.0", Provides = new string[] {
        "sr.brief.deaths", "sr.brief.obituary" })]
    public class DeathReport : ITweak {
        private const string Sec = "死亡鉴定书";

        private const int MaxDeaths = 256;
        private const int MaxPlacements = 512;

        // ---------- 配置 ----------
        private static ConfigEntry<bool> _enabled, _autoPop, _showSuspect, _showLive, _keepLast;
        private static ConfigEntry<int> _popSeconds;
        private static ConfigEntry<KeyCode> _toggleKey;

        // ---------- 本局数据（全为值） ----------
        private struct DeathRec {
            public int round;
            public string cause;
            public int suspect;     // -1 = 没匹配到
        }
        private struct PlaceRec {
            public int player;
            public string killType;
            public string name;
            public int round;
        }
        private static readonly List<DeathRec> _deaths = new List<DeathRec>();
        private static readonly List<PlaceRec> _places = new List<PlaceRec>();
        private static readonly Dictionary<string, int> _causeCount = new Dictionary<string, int>();
        private static readonly Dictionary<int, int> _suspectCount = new Dictionary<int, int>();

        private static int _roundFallback;
        private static int _selfKills;
        private static int _firstDeathRound = -1;
        private static string _lastReport;      // 保留上一局（设置页可看/复制）
        private static string _liveText;        // 局中一句
        private static float _showUntil;        // unscaledTime
        private static string _cardText;        // 局末卡片多行文本
        private static bool _showCard;
        private static GameEvent.IGameEventListener _listener;
        // 对局播报（软依赖）：鉴定书生成后主动交给它，避免局末事件监听顺序不确定。
        private static Action<string> _aCastEnd;
        private static int _bindTries;
        private static float _nextBind;

        // ================= 生命周期 =================

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
            SRApi.LocKey(Sec, "Enabled", "启用死亡鉴定书", "Enable death report");
            SRApi.LocDesc(Sec, "Enabled",
                "启用死亡鉴定书\n记录本局死亡并在结束时生成鉴定书。",
                "Enable death report\nRecord deaths this match and build a report card at the end.");
            SRApi.LocKey(Sec, "Auto Pop", "局末自动弹出", "Auto pop at match end");
            SRApi.LocDesc(Sec, "Auto Pop",
                "局末自动弹出\n一局结束时在局内简报里自动弹出卡片。",
                "Auto pop at match end\nPop the card in the match brief when the match ends.");
            SRApi.LocKey(Sec, "Pop Seconds", "卡片停留秒数", "Card seconds");
            SRApi.LocDesc(Sec, "Pop Seconds",
                "卡片停留秒数\n卡片停留秒数（5-120）。",
                "Card seconds\nHow long the card stays on screen. Range 5-120.");
            SRApi.LocKey(Sec, "Show Suspect", "显示疑似元凶", "Show likely culprit");
            SRApi.LocDesc(Sec, "Show Suspect",
                "显示疑似元凶\n显示「疑似元凶」（按死因匹配本局同名方块，可能认错）。",
                "Show likely culprit\nMatch the death cause to a same-named placed block; may be wrong.");
            SRApi.LocKey(Sec, "Show Live", "对局中显示阵亡数", "Show live deaths");
            SRApi.LocDesc(Sec, "Show Live",
                "对局中显示阵亡数\n对局中在简报里显示已阵亡次数。",
                "Show live deaths\nShow the death count in the match brief during play.");
            SRApi.LocKey(Sec, "Keep Last", "保留上一局", "Keep last report");
            SRApi.LocDesc(Sec, "Keep Last",
                "保留上一局\n保留上一局鉴定书，可在设置页复制。",
                "Keep last report\nKeep the previous match report so it can be copied on this page.");
            SRApi.LocKey(Sec, "Toggle Key", "显示/隐藏快捷键", "Toggle key");
            SRApi.LocDesc(Sec, "Toggle Key",
                "显示/隐藏快捷键\n手动显示 / 隐藏上一张鉴定书的快捷键。",
                "Toggle key\nShow / hide the last report card.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Pop Seconds": min = 5.0f; max = 120.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "记录本局死亡并在结束时生成鉴定书。");
            _autoPop = plugin.Config.Bind(Sec, "Auto Pop", true, "一局结束时在局内简报里自动弹出卡片。");
            _popSeconds = plugin.Config.Bind(Sec, "Pop Seconds", 25,
                new ConfigDescription("卡片停留秒数（5-120）。", new AcceptableValueRange<int>(5, 120)));
            _showSuspect = plugin.Config.Bind(Sec, "Show Suspect", true, "显示「疑似元凶」（按死因匹配本局同名方块，可能认错）。");
            _showLive = plugin.Config.Bind(Sec, "Show Live", true, "对局中在简报里显示已阵亡次数。");
            _keepLast = plugin.Config.Bind(Sec, "Keep Last", true, "保留上一局鉴定书，可在设置页复制。");
            _toggleKey = plugin.Config.Bind(Sec, "Toggle Key", KeyCode.None, "手动显示 / 隐藏上一张鉴定书的快捷键。");

            SRApi.LocSec(Sec, "死亡鉴定书", "Death Report");

            RegUi();
            SRApi.Nav(Sec, 110);
            SRApi.RegisterPage(Sec, RenderPage);
            SRApi.RegisterKey("死亡鉴定书-显示", _toggleKey, "press");
            SRApi.RegisterTick("死亡鉴定书-收口", Tick);

            // 局内简报的状态出口（BCL 委托，拆分构建下跨 DLL 安全）。
            Slots.Provide("sr.brief.deaths", (Func<string>)(() => LiveLine()));
            Slots.Provide("sr.brief.obituary", (Func<string>)(() => CardText()));

            if (_listener == null) {
                _listener = new Listener();
                GameEventManager.ChangeListener<GameEvent.PlayerKilledEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, true);
            }
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
            ResetMatch();
        }

        // ITweak 没有 Shutdown 契约，这里仍留对称入口，将来契约补齐可直接生效。
        public void Shutdown() {
            if (_listener != null) {
                GameEventManager.ChangeListener<GameEvent.PlayerKilledEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, false);
                _listener = null;
            }
            ResetMatch();
        }

        private static void Tick() {
            try {
                if (_bindTries < 8 && Time.unscaledTime >= _nextBind) {
                    _bindTries++;
                    _nextBind = Time.unscaledTime + 2f;
                    foreach (string n in Slots.Names) {
                        if (n == "sr.cast.end" && _aCastEnd == null) _aCastEnd = Slots.Require<Action<string>>(n);
                    }
                }
                if (!_enabled.Value) return;
                if (_toggleKey.Value != KeyCode.None && Input.GetKeyDown(_toggleKey.Value)) {
                    if (_keepLast.Value && !string.IsNullOrEmpty(_lastReport)) {
                        _cardText = _lastReport;
                        _showCard = !_showCard;
                        _showUntil = Time.unscaledTime + _popSeconds.Value;
                    }
                }
                if (_showCard && Time.unscaledTime > _showUntil) _showCard = false;
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/Tick", ex); }
        }

        // ================= 事件 =================

        private sealed class Listener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    if (!_enabled.Value) return;
                    if (e is GameEvent.PlayerKilledEvent) { OnDeath((GameEvent.PlayerKilledEvent)e); return; }
                    if (e is GameEvent.PiecePlacedEvent) { OnPlace((GameEvent.PiecePlacedEvent)e); return; }
                    if (e is GameEvent.StartPhaseEvent) {
                        // 只在 PLACE 阶段递增兜底回合号；优先读游戏自己的 RoundNumber。
                        if (((GameEvent.StartPhaseEvent)e).Phase == GameControl.GamePhase.PLACE) _roundFallback++;
                        return;
                    }
                    if (e is GameEvent.NewMatchEvent) { ResetMatch(); return; }
                    if (e is GameEvent.GameEndEvent) { BuildReport(); return; }
                } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/事件", ex); }
            }
        }

        // 刻意不监听 MsgPiecePlaced：那条消息每个客户端都收，且拿不到 KillType / Name，
        // 塞进 _places 只会是匹配不上 Cause 的废记录。MaxPlacements=512 + RemoveAt(0)
        // 会让这些废记录把本地 PiecePlacedEvent 带 KillType 的有效记录挤出去，
        // 「疑似元凶」大面积失效。全部放置记录只由 OnPlace 一处产生。

        private static void OnPlace(GameEvent.PiecePlacedEvent ppe) {
            try {
                if (ppe == null) return;
                Placeable p = ppe.PlacedBlock;
                string kt = null, nm = null;
                int by = -1;
                if (p != null) {
                    kt = p.KillType;
                    nm = p.Name;
                    by = p.placedByPlayerNumber;
                }
                int player = by >= 0 ? by : ppe.PlayerNumber;
                AddPlace(player, kt, nm, CurrentRound());
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/放置", ex); }
        }

        private static void AddPlace(int player, string killType, string name, int round) {
            if (_places.Count >= MaxPlacements) _places.RemoveAt(0);
            _places.Add(new PlaceRec { player = player, killType = killType, name = name, round = round });
        }

        private static void OnDeath(GameEvent.PlayerKilledEvent pke) {
            try {
                string cause = pke.Cause ?? "";
                int round = CurrentRound();
                int suspect = -1;
                if (_showSuspect.Value) suspect = MatchSuspect(cause);

                if (_deaths.Count >= MaxDeaths) _deaths.RemoveAt(0);
                _deaths.Add(new DeathRec { round = round, cause = cause, suspect = suspect });
                if (_firstDeathRound < 0) _firstDeathRound = round;

                int c;
                _causeCount.TryGetValue(cause, out c);
                _causeCount[cause] = c + 1;

                if (suspect >= 0) {
                    int s;
                    _suspectCount.TryGetValue(suspect, out s);
                    _suspectCount[suspect] = s + 1;
                    int me = LocalNumber();
                    if (me >= 0 && suspect == me) _selfKills++;
                }
                RefreshLive();
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/死亡", ex); }
        }

        // 按 Cause 匹配本局「最近一次放置的同名方块」，取它记的放置者。
        // 同类方块有多块时可能认错 —— 这就是界面写「疑似」的原因。
        private static int MatchSuspect(string cause) {
            if (string.IsNullOrEmpty(cause)) return -1;
            int round = CurrentRound();
            // 两轮：优先本回合放置的同名方块（最可能是刚害死人的那个），
            // 找不到再退回整局最近的一条。round 字段因此真正参与判定，不是死字段。
            for (int pass = 0; pass < 2; pass++) {
                for (int i = _places.Count - 1; i >= 0; i--) {
                    PlaceRec p = _places[i];
                    if (pass == 0 && p.round != round) continue;
                    if (string.Equals(p.killType, cause, StringComparison.Ordinal) ||
                        string.Equals(p.name, cause, StringComparison.Ordinal)) return p.player;
                }
            }
            return -1;
        }

        private static void ResetMatch() {
            _deaths.Clear();
            _places.Clear();
            _causeCount.Clear();
            _suspectCount.Clear();
            _roundFallback = 0;
            _selfKills = 0;
            _firstDeathRound = -1;
            _showCard = false;
            _cardText = null;
            _liveText = null;
        }

        private static int CurrentRound() {
            try {
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = lm != null ? lm.CurrentGameController : null;
                if (gc != null && gc.RoundNumber > 0) return gc.RoundNumber;
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/读回合", ex); }
            return _roundFallback > 0 ? _roundFallback : 1;
        }

        // 照抄 Chat.Log.cs 的现成写法：lobbySlots 里第一个 LocalPlayer 的 networkNumber。
        private static int LocalNumber() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.lobbySlots == null) return -1;
                for (int i = 0; i < lm.lobbySlots.Length; i++) {
                    LobbyPlayer lp = lm.lobbySlots[i] as LobbyPlayer;
                    if (lp != null && lp.LocalPlayer != null) return lp.networkNumber;
                }
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/本地玩家号", ex); }
            return -1;
        }

        // ================= 生成鉴定书 =================

        private static void BuildReport() {
            try {
                if (_deaths.Count == 0) {
                    _lastReport = SRApi.T("死亡鉴定书：全身而退，一次没死。",
                                          "Death report: survived the whole match without a single death.");
                } else {
                    List<string> top = TopCauses(3);
                    string worst = "";
                    string sus = "";
                    if (_showSuspect.Value) {
                        int who, n;
                        if (TopSuspect(out who, out n) && n > 0) {
                            string whoName = who == LocalNumber() && who >= 0
                                ? SRApi.T("你自己", "yourself")
                                : "#" + who;
                            sus = SRApi.T("\n最常坑你的：", "\nMost likely culprit: ") + whoName +
                                  SRApi.T("（" + n + " 次，疑似）", " (" + n + "x, suspected)");
                        }
                    }
                    if (_selfKills > 0)
                        worst = SRApi.T("\n自己坑自己：" + _selfKills + " 次",
                                        "\nSelf-inflicted: " + _selfKills + "x");
                    _lastReport =
                        SRApi.T("死亡鉴定书 · 共 ", "Death report · ") + _deaths.Count +
                        SRApi.T(" 次阵亡 / ", " deaths across ") + CurrentRound() +
                        SRApi.T(" 回合", " rounds") +
                        (top.Count > 0 ? SRApi.T("\n主要死因：", "\nTop causes: ") + string.Join("、", top.ToArray()) : "") +
                        (_firstDeathRound > 0 ? SRApi.T("\n首次阵亡：第 ", "\nFirst death: round ") + _firstDeathRound + SRApi.T(" 回合", "") : "") +
                        worst + sus +
                        "\n" + Verdict();
                }
                if (_keepLast.Value || _autoPop.Value) {
                    _cardText = _lastReport;
                    _showCard = _autoPop.Value;
                    _showUntil = Time.unscaledTime + Mathf.Clamp(_popSeconds.Value, 5, 120);
                }
                NotifyCastEnd(_lastReport);
            } catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/生成", ex); }
        }

        /// <summary>主动通知对局播报：不依赖 GameEndEvent 的监听先后顺序。</summary>
        private static void NotifyCastEnd(string report) {
            if (_aCastEnd == null || string.IsNullOrEmpty(report)) return;
            try { _aCastEnd(report); }
            catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/通知播报", ex); }
        }

        private static List<string> TopCauses(int take) {
            List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>(_causeCount);
            items.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            List<string> outp = new List<string>();
            for (int i = 0; i < items.Count && outp.Count < take; i++)
                outp.Add(CauseName(items[i].Key) + " ×" + items[i].Value);
            return outp;
        }

        private static bool TopSuspect(out int who, out int n) {
            who = -1; n = 0;
            foreach (KeyValuePair<int, int> kv in _suspectCount) {
                if (kv.Value > n) { n = kv.Value; who = kv.Key; }
            }
            return who >= 0 && n > 0;
        }

        private static string Verdict() {
            int d = _deaths.Count;
            if (d == 0) return SRApi.T("完美通关，连羽毛都没掉一根。", "Flawless. Not a feather lost.");
            if (_selfKills > 0 && _selfKills * 2 >= d)
                return SRApi.T("本局最佳陷阱工程师：你自己。", "Trap engineer of the match: you.");
            if (d >= 8) return SRApi.T("这局是拿命趟出来的。", "You paved this one with your own lives.");
            if (d >= 4) return SRApi.T("小有波折，但好歹走完了。", "A few bumps, but you got there.");
            return SRApi.T("稳。下局可以大胆点。", "Clean run. Maybe be bolder next time.");
        }

        private static void RefreshLive() {
            _liveText = SRApi.T("已阵亡 ", "Deaths: ") + _deaths.Count +
                        (_selfKills > 0 ? SRApi.T("（自坑 " + _selfKills + "）", " (self " + _selfKills + ")") : "");
        }

        // 局内简报读的两个槽位（文本已本地化，null = 不显示那一行）。
        public static string LiveLine() {
            if (!_enabled.Value || !_showLive.Value || _deaths.Count == 0) return null;
            return _liveText;
        }
        public static string CardText() {
            if (!_enabled.Value || !_showCard || string.IsNullOrEmpty(_cardText)) return null;
            return _cardText;
        }

        // ================= 死因名（游戏原串 → 中英可读名） =================

        private static string CauseName(string cause) {
            if (string.IsNullOrEmpty(cause)) return SRApi.T("未知", "Unknown");
            switch (cause) {
                case "Falling": return SRApi.T("坠落", "Falling");
                case "Drowning": return SRApi.T("溺水", "Drowning");
                case "Drowning_In_Lava": return SRApi.T("岩浆", "Lava");
                case "World": return SRApi.T("世界边界", "Out of bounds");
                case "UFO": return SRApi.T("飞碟", "UFO");
                case "SpikeBall": return SRApi.T("刺球", "Spike ball");
                case "BarbedWire": return SRApi.T("铁丝网", "Barbed wire");
                case "Crossbow": return SRApi.T("弩箭", "Crossbow");
                case "TennisBallLauncher": return SRApi.T("网球炮", "Tennis launcher");
                case "SpinningDeath": return SRApi.T("旋转锯", "Spinning saw");
                case "LinearSaw": return SRApi.T("直线锯", "Linear saw");
                case "FloatingPlatform": return SRApi.T("螺旋桨", "Propeller");
                case "FlippingBlock": return SRApi.T("翻转块", "Flipping block");
                case "Blackhole": return SRApi.T("黑洞", "Black hole");
                case "HockeyShooter": return SRApi.T("冰球炮", "Hockey shooter");
                case "PunchingPlant": return SRApi.T("拳击花", "Punching plant");
                case "PunchingBlock": return SRApi.T("拳击块", "Punching block");
                case "PressureTriggerSpikes": return SRApi.T("压力尖刺", "Pressure spikes");
                case "WreckingBall": return SRApi.T("链球", "Wrecking ball");
                case "Beehive": return SRApi.T("蜂巢", "Beehive");
                default: return cause;   // 未知死因原样显示，不编造翻译
            }
        }

        // ================= 设置页 =================

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            CfgRow(_enabled);
            CfgRow(_autoPop);
            CfgRow(_popSeconds);
            CfgRow(_showSuspect);
            CfgRow(_showLive);
            CfgRow(_keepLast);
            CfgRow(_toggleKey);

            SRApi.Ctl.SectionTitle(SRApi.T("- 最近一局 -", "- Last match -"));
            string txt = !string.IsNullOrEmpty(_lastReport) ? _lastReport
                : (_deaths.Count > 0 ? SRApi.T("对局还没结束", "Match still in progress")
                                     : SRApi.T("还没有记录", "No deaths recorded yet"));
            GUILayout.Label(txt, SRApi.Ctl.LabelWrap);
            if (!string.IsNullOrEmpty(_lastReport)) {
                // 纯本地、重复写同一串无害，不需要走 Tick 意图。
                SRApi.Ctl.HotkeyButton("deathreport.copy", SRApi.T("复制鉴定书", "Copy report"),
                                       SRApi.T("复制到系统剪贴板，方便贴出来", "Copy to clipboard"),
                                       delegate { try { GUIUtility.systemCopyBuffer = _lastReport; }
                                                  catch (Exception ex) { SRApi.Guard.Log("死亡鉴定书/复制", ex); } });
            }

            Line("「疑似元凶」按死因匹配本局最近一次放置的同名方块，同类方块有多块时可能认错；" +
                 "游戏的 PlayerKilledEvent 不带放置者编号，所以无法精确归因。不想要这一项可以关掉。",
                 "The 'suspected culprit' matches the most recent placed block with the same kill type; " +
                 "with several identical blocks it can be wrong. The game's PlayerKilledEvent does not carry the placer id, so exact attribution is impossible. Turn it off if you prefer.");
            Line("只记录值（死因 / 回合 / 玩家号），不持有任何游戏对象；换局自动清空。纯本地，不联网、不加补丁。",
                 "Only values are stored (cause / round / player number) — no game object references. Cleared on a new match. Fully local: no network, no patches.");
        }
    }
}
