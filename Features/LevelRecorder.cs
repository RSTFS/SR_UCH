// 关卡记录器：按时间线完整记录一局里发生的事，并与布局预设双向联动。
//
// 游戏侧证据（反编译快照 221,827 行，全部 public，零 Harmony / 零反射 / 零新 msgType）：
//   NewMatchEvent { GameMode, Level, LevelCode }                          :151511
//   StartPhaseEvent / EndPhaseEvent { Phase }                             :151526 / :151535
//   GameEndEvent { GameMode, LevelName, Online, GameCompleted, RoundsPlayed } :151757
//   PiecePlacedEvent { PlayerNumber, PlacedBlock }                        :151717
//   Placeable.ID / Name / KillType / placedByPlayerNumber 全 public        :11993/11995/11997/12131
//   PlayerKilledEvent { Cause, Player }；Player.Number 是 public get      :151690 / :73428
//   NetworkPlayerDisconnectEvent { NetworkNumber, Kicked }                 :151964
//   NetworkMessageReceivedEvent { Message, ReadMessage }                   :151976
//   readMessage 已解析：MsgPiecePlaced :45746 / MsgPieceDestroyed :45784 /
//     MsgPointAwarded :45921 / MsgChat :46029 / MsgCharacterSuccess :45868 /
//     MsgNetworkClientDisconnected :46072 —— 直接 cast ReadMessage，不重复读流。
//   服务器对以上消息都注册了 distributeServerMessage（:38919-38931, :39123），
//   所以【只房主安装】时，房主端能收到全场的放置 / 得分 / 聊天 / 到达 / 离房。
//
// 刻意不碰：胜负判定、真实分数、相机、服务端补丁、新增 msgType、多线程。
// 全部数据都是值（string / int / float）；不持任何 Unity 对象引用 —— 方块销毁
// 不派事件（历轮审查结论），持引用必然读到已回池的对象。
// 死亡事件只在 hasAuthority 时派发（:61389），因此死亡记录以本机角色为准，
// 不冒充「全场死亡」。这条限制在页面与导出文本里都写明。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.recorder", "LevelRecorder", "1.0", Provides = new string[] {
        "sr.rec.note", "sr.rec.summary", "sr.rec.copy" })]
    public partial class LevelRecorder : ITweak {
        private const string Sec = "关卡记录";
        private const int MaxSnaps = 24;
        private const int MaxSnapChars = 2000000;   // 与布局预设导入上限同量级
        private const int BindTriesMax = 6;

        internal enum Kind { Match, Phase, Place, Remove, Death, Point, Chat, Rule, Preset, Snapshot, System, End }

        internal struct Rec {
            public int seq, round, player, pid, snap;
            public float at;
            public Kind kind;
            public string text, detail;
        }
        internal struct Snap {
            public int round, blocks;
            public float at;
            public string xml, label;
        }

        // ---------- 配置 ----------
        private static ConfigEntry<bool> _enabled, _snapEachRound, _autoExport, _logPhaseEnd, _hideCommands;
        private static ConfigEntry<int> _maxEvents;
        private static ConfigEntry<KeyCode> _copyKey, _exportKey;

        // ---------- 本局数据（全为值） ----------
        private static readonly List<Rec> _recs = new List<Rec>();
        private static readonly List<Snap> _snaps = new List<Snap>();
        private static readonly Dictionary<int, string> _nameById = new Dictionary<int, string>();
        private static readonly HashSet<string> _seenPlace = new HashSet<string>();
        // 局末归档：换局后仍能回看上一局（页面可切）。
        private static List<Rec> _archRecs = new List<Rec>();
        private static List<Snap> _archSnaps = new List<Snap>();
        private static string _archTitle = "";

        private static int _seq, _roundFallback;
        private static float _matchStart;
        private static string _matchTitle = "";
        private static GameEvent.IGameEventListener _listener;
        private static bool _ready;

        // ---------- 跨 DLL 槽位（BCL 委托，拆分构建安全） ----------
        private static Func<string[]> _fSnapshot, _fListPresets;
        private static Func<string, string, bool> _fSavePreset;
        private static Action<string> _aLoadPreset;
        private static Func<string> _fChaosDesc;
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
            SRApi.LocKey(Sec, "Enabled", "启用关卡记录", "Enable level recorder");
            SRApi.LocDesc(Sec, "Enabled",
                "启用关卡记录\n记录本局的阶段、放置、得分、聊天与布局变化。",
                "Enable level recorder\nRecord phases, placements, scores, chat and layout changes for this match.");
            SRApi.LocKey(Sec, "Snapshot Each Round", "每回合布局快照", "Snapshot each round");
            SRApi.LocDesc(Sec, "Snapshot Each Round",
                "每回合布局快照\n每个摆放阶段自动抓一份关卡布局快照（可在列表里存为布局预设）。",
                "Snapshot each round\nAuto-capture a layout snapshot each build phase; can be saved as a building preset.");
            SRApi.LocKey(Sec, "Auto Export", "局末自动导出", "Auto export");
            SRApi.LocDesc(Sec, "Auto Export",
                "局末自动导出\n一局结束时自动把记录写成文本文件。",
                "Auto export\nWrite the whole record to a text file when the match ends.");
            SRApi.LocKey(Sec, "Log Phase End", "记录阶段结束", "Log phase ends");
            SRApi.LocDesc(Sec, "Log Phase End",
                "记录阶段结束\n连阶段结束事件也记录（默认关，避免刷屏）。",
                "Log phase ends\nAlso record phase-end events. Off by default to avoid noise.");
            SRApi.LocKey(Sec, "Hide Commands", "隐藏 /sr 指令", "Hide /sr commands");
            SRApi.LocDesc(Sec, "Hide Commands",
                "隐藏 /sr 指令\n聊天里的 /sr 指令不计入记录。",
                "Hide /sr commands\nExclude /sr chat commands from the record.");
            SRApi.LocKey(Sec, "Max Events", "单局事件上限", "Max events");
            SRApi.LocDesc(Sec, "Max Events",
                "单局事件上限\n单局最多保留多少条事件，超出丢弃最旧的。",
                "Max events\nMax events kept per match; oldest dropped. Range 200-8000.");
            SRApi.LocKey(Sec, "Copy Key", "复制整局快捷键", "Copy key");
            SRApi.LocDesc(Sec, "Copy Key",
                "复制整局快捷键\n一键复制整局记录到剪贴板。",
                "Copy key\nCopy the whole match record to the clipboard.");
            SRApi.LocKey(Sec, "Export Key", "导出整局快捷键", "Export key");
            SRApi.LocDesc(Sec, "Export Key",
                "导出整局快捷键\n一键把整局记录写成文本文件。",
                "Export key\nWrite the whole match record to a text file.");
            SRApi.LocKey(Sec, "View Archive", "查看上一局归档", "View archive");
            SRApi.LocDesc(Sec, "View Archive",
                "查看上一局归档\n查看上一局归档，而不是当前局。局末自动归档，换局不清空。",
                "View archive\nBrowse last match's archive instead of the current one; archived at match end and kept across matches.");
            SRApi.LocKey(Sec, "Export Details", "导出附带详情", "Export details");
            SRApi.LocDesc(Sec, "Export Details",
                "导出附带详情\n复制 / 导出时附带每条的详情（坐标、方块 ID 等）。",
                "Export details\nInclude per-entry details (coords, block id) in copy / export.");
            SRApi.LocKey(Sec, "Key Events Only", "只看关键事件", "Key events only");
            SRApi.LocDesc(Sec, "Key Events Only",
                "只看关键事件\n只看关键事件（隐藏聊天、得分、阶段、移除、系统）。",
                "Key events only\nHide chat, score, phase, removal and system entries.");
            SRApi.LocKey(Sec, "Page Size", "每页条数", "Page size");
            SRApi.LocDesc(Sec, "Page Size",
                "每页条数\n时间线每页显示多少条（10-80）。",
                "Page size\nEntries shown per timeline page. Range 10-80.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Max Events": min = 200.0f; max = 8000.0f; return true;
                    case "Page Size": min = 10.0f; max = 80.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "记录本局的阶段、放置、得分、聊天与布局变化。");
            _snapEachRound = plugin.Config.Bind(Sec, "Snapshot Each Round", true,
                "每个摆放阶段自动抓一份关卡布局快照（可在列表里存为布局预设）。");
            _autoExport = plugin.Config.Bind(Sec, "Auto Export", false, "一局结束时自动把记录写成文本文件。");
            _logPhaseEnd = plugin.Config.Bind(Sec, "Log Phase End", false, "连阶段结束事件也记录（默认关，避免刷屏）。");
            _hideCommands = plugin.Config.Bind(Sec, "Hide Commands", true, "聊天里的 /sr 指令不计入记录。");
            _maxEvents = plugin.Config.Bind(Sec, "Max Events", 2000,
                new ConfigDescription("单局最多保留多少条事件，超出丢弃最旧的。", new AcceptableValueRange<int>(200, 8000)));
            _copyKey = plugin.Config.Bind(Sec, "Copy Key", KeyCode.None, "一键复制整局记录到剪贴板。");
            _exportKey = plugin.Config.Bind(Sec, "Export Key", KeyCode.None, "一键把整局记录写成文本文件。");

            BindViewConfig(plugin);
            SRApi.LocSec(Sec, "关卡记录", "Level Recorder");
            RegUi();
            SRApi.Nav(Sec, 115);
            SRApi.RegisterPage(Sec, "Timeline", "时间线", "Timeline", 10, RenderTimelinePage);
            SRApi.RegisterPage(Sec, "Layouts", "布局快照", "Layouts", 20, RenderLayoutsPage);
            SRApi.RegisterPage(Sec, "Options", "记录设置", "Settings", 30, RenderOptionsPage);
            SRApi.RegisterKey("关卡记录-复制", _copyKey, "press");
            SRApi.RegisterKey("关卡记录-导出", _exportKey, "press");
            SRApi.RegisterTick("关卡记录-绑定与快捷键", Tick);

            // 反向通知：布局预设在发起载入时调它；记录器不在时 Require 返回 null，静默降级。
            Slots.Provide("sr.rec.note", (Action<string>)(s => AddNote(s)));
            Slots.Provide("sr.rec.summary", (Func<string>)(() => Summary()));
            Slots.Provide("sr.rec.copy", (Action)(() => CopyText(BuildText(CurrentRecs(), CurrentSnaps(), false))));

            if (_listener == null) {
                _listener = new Listener();
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.EndPhaseEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.PlayerKilledEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NetworkPlayerDisconnectEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, true);
            }
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
            _ready = true;
        }

        public void Shutdown() {
            if (_listener != null) {
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.EndPhaseEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.PlayerKilledEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NetworkPlayerDisconnectEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, false);
                _listener = null;
            }
            _ready = false;
        }

        private static void Tick() {
            try {
                if (!_ready || !_enabled.Value) return;
                if (_bindTries < BindTriesMax && Time.unscaledTime >= _nextBind) {
                    _bindTries++;
                    TryBind();
                    _nextBind = Time.unscaledTime + 2f;
                }
                if (_copyKey.Value != KeyCode.None && Input.GetKeyDown(_copyKey.Value))
                    CopyText(BuildText(CurrentRecs(), CurrentSnaps(), false));
                if (_exportKey.Value != KeyCode.None && Input.GetKeyDown(_exportKey.Value))
                    ExportToFile();
            } catch (Exception ex) { SRApi.Guard.Log("关卡记录/Tick", ex); }
        }

        private static void TryBind() {
            if (_fSnapshot == null) _fSnapshot = Slots.Require<Func<string[]>>("sr.rec.snapshot");
            if (_fSavePreset == null) _fSavePreset = Slots.Require<Func<string, string, bool>>("sr.rec.savePreset");
            if (_fListPresets == null) _fListPresets = Slots.Require<Func<string[]>>("sr.rec.listPresets");
            if (_aLoadPreset == null) _aLoadPreset = Slots.Require<Action<string>>("sr.rec.loadPreset");
            if (_fChaosDesc == null) _fChaosDesc = Slots.Require<Func<string>>("sr.brief.chaos");
            if (_fSnapshot != null && _fSavePreset != null && _fListPresets != null &&
                _aLoadPreset != null && _fChaosDesc != null) _bindTries = BindTriesMax;
        }

        // ================= 事件 =================

        private sealed class Listener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    if (!_ready || !_enabled.Value) return;
                    if (e is GameEvent.NewMatchEvent) { OnNewMatch((GameEvent.NewMatchEvent)e); return; }
                    if (e is GameEvent.StartPhaseEvent) { OnStartPhase((GameEvent.StartPhaseEvent)e); return; }
                    if (e is GameEvent.EndPhaseEvent) { OnEndPhase((GameEvent.EndPhaseEvent)e); return; }
                    if (e is GameEvent.GameEndEvent) { OnGameEnd((GameEvent.GameEndEvent)e); return; }
                    if (e is GameEvent.PiecePlacedEvent) { OnPlaceLocal((GameEvent.PiecePlacedEvent)e); return; }
                    if (e is GameEvent.PlayerKilledEvent) { OnDeath((GameEvent.PlayerKilledEvent)e); return; }
                    if (e is GameEvent.NetworkPlayerDisconnectEvent) { OnDisconnect((GameEvent.NetworkPlayerDisconnectEvent)e); return; }
                    if (e is GameEvent.NetworkMessageReceivedEvent) { OnNet((GameEvent.NetworkMessageReceivedEvent)e); return; }
                } catch (Exception ex) { SRApi.Guard.Log("关卡记录/事件", ex); }
            }
        }

        private static void OnNewMatch(GameEvent.NewMatchEvent e) {
            ResetMatch();
            _matchTitle = (e != null ? e.GameMode.ToString() : "?") + " / " +
                          (e != null ? e.Level.ToString() : "?") +
                          (e != null && !string.IsNullOrEmpty(e.LevelCode) ? " (" + e.LevelCode + ")" : "");
            Add(Kind.Match, 1, -1, SRApi.T("对局开始：", "Match started: ") + _matchTitle, "", -1, -1);
            CaptureSnapshot(SRApi.T("开局", "Match start"), 1);
        }

        private static void OnStartPhase(GameEvent.StartPhaseEvent e) {
            if (e == null) return;
            string ph = e.Phase.ToString();
            if (e.Phase == GameControl.GamePhase.PLACE) {
                _roundFallback++;
                int r = CurrentRound();
                Add(Kind.Phase, r, -1, SRApi.T("摆放阶段开始", "Placement phase started"), "", -1, -1);
                // 混乱牌组牌面走既有槽位，不解析 MsgGameRuleSet（避免与带外信号混淆）。
                try {
                    string cards = _fChaosDesc != null ? _fChaosDesc() : null;
                    if (!string.IsNullOrEmpty(cards))
                        Add(Kind.Rule, r, -1, SRApi.T("本回合牌面：", "Active cards: ") + cards, "", -1, -1);
                } catch (Exception ex) { SRApi.Guard.Log("关卡记录/读牌面", ex); }
                if (_snapEachRound.Value) CaptureSnapshot(SRApi.T("第 ", "Round ") + r, r);
            } else {
                Add(Kind.Phase, CurrentRound(), -1, SRApi.T("进入阶段：", "Phase: ") + ph, "", -1, -1);
            }
        }

        private static void OnEndPhase(GameEvent.EndPhaseEvent e) {
            if (e == null || !_logPhaseEnd.Value) return;
            Add(Kind.Phase, CurrentRound(), -1, SRApi.T("阶段结束：", "Phase ended: ") + e.Phase.ToString(), "", -1, -1);
        }

        private static void OnGameEnd(GameEvent.GameEndEvent e) {
            int rounds = e != null ? e.RoundsPlayed : CurrentRound();
            bool done = e != null && e.GameCompleted;
            Add(Kind.End, CurrentRound(), -1,
                SRApi.T(done ? "一局完成 · 共 " : "一局结束（未完成）· 共 ", done ? "Match completed · " : "Match ended (incomplete) · ") +
                rounds + SRApi.T(" 回合", " rounds"), "", -1, -1);
            CaptureSnapshot(SRApi.T("终局", "Match end"), CurrentRound());
            Archive();
            if (_autoExport.Value) ExportToFile();
        }

        // 双通道去重：本地 PiecePlacedEvent 带方块名，网络 MsgPiecePlaced 带 ID + 坐标。
        // 两者谁先到都可能，用 (回合|玩家|方块ID) 做键；名字另存字典，渲染时再补，
        // 这样无论顺序如何，一条放置只出现一次且尽量带上名字。
        private static void OnPlaceLocal(GameEvent.PiecePlacedEvent e) {
            if (e == null) return;
            Placeable p = e.PlacedBlock;
            int pid = p != null ? p.ID : -1;
            int by = p != null ? p.placedByPlayerNumber : -1;
            int player = by >= 0 ? by : e.PlayerNumber;
            string name = p != null ? (string.IsNullOrEmpty(p.Name) ? p.KillType : p.Name) : null;
            Vector3 pos = p != null ? p.transform.position : Vector3.zero;
            TryAddPlace(CurrentRound(), player, pid, name, pos);
        }

        private static void OnPlaceNet(MsgPiecePlaced m) {
            TryAddPlace(CurrentRound(), m.PlayerNumber, m.PieceID, null, m.PiecePosition);
        }

        private static void TryAddPlace(int round, int player, int pid, string name, Vector3 pos) {
            string key = round.ToString(CultureInfo.InvariantCulture) + "|" + player + "|" + pid;
            if (pid >= 0 && !string.IsNullOrEmpty(name) && !_nameById.ContainsKey(pid))
                _nameById[pid] = name;
            if (_seenPlace.Contains(key)) return;
            _seenPlace.Add(key);
            string nm = pid >= 0 ? NameOf(pid) : null;
            string txt = SRApi.T("放置 ", "Placed ") + (string.IsNullOrEmpty(nm) ? "#" + pid : nm);
            string det = SRApi.T("方块 ID ", "Block ID ") + pid + " @ (" +
                         pos.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                         pos.y.ToString("0.0", CultureInfo.InvariantCulture) + ")";
            Add(Kind.Place, round, player, txt, det, pid, -1);
        }

        private static string NameOf(int pid) {
            string s;
            return _nameById.TryGetValue(pid, out s) ? s : null;
        }

        private static void OnDeath(GameEvent.PlayerKilledEvent e) {
            if (e == null) return;
            int num = -1;
            try { if (e.Player != null) num = e.Player.Number; } catch (Exception ex) { SRApi.Guard.Log("关卡记录/读阵亡玩家号", ex); }
            string cause = e.Cause ?? "";
            Add(Kind.Death, CurrentRound(), num,
                SRApi.T("阵亡 · ", "Died · ") + (string.IsNullOrEmpty(cause) ? SRApi.T("未知死因", "unknown cause") : cause),
                SRApi.T("死亡事件仅由本机角色派发，不代表全场死亡", "Death events are emitted only for the local character"), -1, -1);
        }

        private static void OnDisconnect(GameEvent.NetworkPlayerDisconnectEvent e) {
            if (e == null) return;
            Add(Kind.System, CurrentRound(), e.NetworkNumber,
                e.Kicked ? SRApi.T("被踢出房间", "Kicked from the room") : SRApi.T("离开房间", "Left the room"), "", -1, -1);
        }

        // 只读已经被游戏 readMessage 解析好的 MessageBase；绝不自己再 ReadMessage<T>()，
        // 否则会和同帧其它监听者抢同一个网络流。
        private static void OnNet(GameEvent.NetworkMessageReceivedEvent e) {
            if (e == null || e.ReadMessage == null) return;
            object m = e.ReadMessage;
            int r = CurrentRound();
            if (m is MsgPointAwarded) {
                var x = (MsgPointAwarded)m;
                Add(Kind.Point, r, x.PlayerNumber, SRApi.T("得分 · ", "Point · ") + x.PointType.ToString(),
                    x.AlwaysAward ? SRApi.T("必定得分", "always award") : "", -1, -1);
                return;
            }
            if (m is MsgPiecePlaced) { OnPlaceNet((MsgPiecePlaced)m); return; }
            if (m is MsgPieceDestroyed) {
                var x = (MsgPieceDestroyed)m;
                Add(Kind.Remove, r, x.MachineNetworkNumber, SRApi.T("移除方块 #", "Removed block #") + x.BlockID, "", x.BlockID, -1);
                return;
            }
            if (m is MsgChat) {
                var x = (MsgChat)m;
                if (!x.isChatMessage || string.IsNullOrEmpty(x.MessageText)) return;
                string t = x.MessageText.Trim();
                if (_hideCommands.Value && t.StartsWith("/sr", StringComparison.OrdinalIgnoreCase)) return;
                Add(Kind.Chat, r, x.NetworkPlayerNumber, SRApi.T("聊天：", "Chat: ") + t, "", -1, -1);
                return;
            }
            if (m is MsgCharacterSuccess) {
                var x = (MsgCharacterSuccess)m;
                Add(Kind.System, r, x.NetworkPlayerNumber, SRApi.T("到达终点", "Reached the goal"), "", -1, -1);
                return;
            }
            if (m is MsgNetworkClientDisconnected) {
                var x = (MsgNetworkClientDisconnected)m;
                Add(Kind.System, r, x.PlayerNetworkNumber, SRApi.T("连接断开", "Connection lost"), "", -1, -1);
            }
        }

        // 布局预设发起载入时由 sr.rec.note 调进来。
        private static void AddNote(string name) {
            Add(Kind.Preset, CurrentRound(), -1,
                SRApi.T("发起载入布局预设：", "Requested layout preset: ") + (name ?? ""), "", -1, -1);
        }

        // ================= 快照 =================

        private static void CaptureSnapshot(string label, int round) {
            try {
                if (_fSnapshot == null) return;
                string[] s = _fSnapshot();
                if (s == null || s.Length < 2 || string.IsNullOrEmpty(s[0])) return;
                string xml = s[0];
                if (xml.Length > MaxSnapChars) return;
                int blocks;
                if (!int.TryParse(s[1], out blocks)) blocks = 0;
                if (_snaps.Count >= MaxSnaps) {
                    _snaps.RemoveAt(0);
                    for (int i = 0; i < _recs.Count; i++) {
                        Rec q = _recs[i];
                        // 指向被删掉的第 0 份要断链（否则会错位指到新的第 0 份）。
                        if (q.snap >= 0) { q.snap = q.snap == 0 ? -1 : q.snap - 1; _recs[i] = q; }
                    }
                }
                int idx = _snaps.Count;
                _snaps.Add(new Snap {
                    round = round, at = Time.unscaledTime - _matchStart, xml = xml,
                    blocks = blocks, label = s.Length > 2 ? s[2] : label
                });
                Add(Kind.Snapshot, round, -1,
                    SRApi.T("布局快照 · ", "Layout snapshot · ") + label +
                    SRApi.T("（", " (") + blocks + SRApi.T(" 块）", " blocks)"),
                    SRApi.T("可在「布局快照」页存为预设", "Can be saved as a preset on the Layouts page"), -1, idx);
            } catch (Exception ex) { SRApi.Guard.Log("关卡记录/快照", ex); }
        }

        // ================= 记录存取 =================

        private static void Add(Kind k, int round, int player, string text, string detail, int pid, int snap) {
            int cap = _maxEvents != null ? _maxEvents.Value : 2000;
            if (cap < 200) cap = 200;
            if (_recs.Count >= cap) _recs.RemoveAt(0);
            _seq++;
            _recs.Add(new Rec {
                seq = _seq, round = round, at = Time.unscaledTime - _matchStart,
                kind = k, player = player, pid = pid, snap = snap,
                text = text ?? "", detail = detail ?? ""
            });
        }

        private static void ResetMatch() {
            _recs.Clear();
            _snaps.Clear();
            _nameById.Clear();
            _seenPlace.Clear();
            _seq = 0;
            _roundFallback = 0;
            _matchStart = Time.unscaledTime;
            _matchTitle = "";
        }

        private static void Archive() {
            _archRecs = new List<Rec>(_recs);
            _archSnaps = new List<Snap>(_snaps);
            _archTitle = _matchTitle + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static int CurrentRound() {
            try {
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = lm != null ? lm.CurrentGameController : null;
                if (gc != null && gc.RoundNumber > 0) return gc.RoundNumber;
            } catch (Exception ex) { SRApi.Guard.Log("关卡记录/读回合", ex); }
            return _roundFallback > 0 ? _roundFallback : 1;
        }

        private static bool ViewArchive { get { return _viewArchive != null && _viewArchive.Value; } }
        private static List<Rec> CurrentRecs() { return ViewArchive ? _archRecs : _recs; }
        private static List<Snap> CurrentSnaps() { return ViewArchive ? _archSnaps : _snaps; }

        internal static string KindName(Kind k) {
            switch (k) {
                case Kind.Match: return SRApi.T("开局", "Match");
                case Kind.Phase: return SRApi.T("阶段", "Phase");
                case Kind.Place: return SRApi.T("放置", "Place");
                case Kind.Remove: return SRApi.T("移除", "Remove");
                case Kind.Death: return SRApi.T("阵亡", "Death");
                case Kind.Point: return SRApi.T("得分", "Point");
                case Kind.Chat: return SRApi.T("聊天", "Chat");
                case Kind.Rule: return SRApi.T("规则", "Rule");
                case Kind.Preset: return SRApi.T("预设", "Preset");
                case Kind.Snapshot: return SRApi.T("快照", "Snapshot");
                case Kind.System: return SRApi.T("系统", "System");
                default: return SRApi.T("结束", "End");
            }
        }

        internal static string Clock(float at) {
            int s = Mathf.Max(0, (int)at);
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                   (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // ================= 导出 / 复制 =================

        internal static string BuildText(List<Rec> recs, List<Snap> snaps, bool onlyFiltered) {
            var sb = new StringBuilder();
            sb.Append("SR_UCH ").Append(SRApi.T("关卡记录", "Level Recorder")).Append(" · ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(ViewArchive ? _archTitle : _matchTitle).Append(" · ")
              .Append(SRApi.T("事件 ", "events ")).Append(recs.Count)
              .Append(SRApi.T(" · 快照 ", " · snapshots ")).Append(snaps.Count).Append('\n');
            sb.Append(new string('-', 48)).Append('\n');
            List<Rec> src = onlyFiltered ? Filtered(recs) : recs;
            for (int i = 0; i < src.Count; i++) {
                Rec r = src[i];
                sb.Append('[').Append(Clock(r.at)).Append("] R").Append(r.round).Append(' ')
                  .Append(KindName(r.kind)).Append(' ');
                if (r.player >= 0) sb.Append('#').Append(r.player).Append(' ');
                sb.Append(Flatten(r.text));
                if (!string.IsNullOrEmpty(r.detail) && _logDetail != null && _logDetail.Value)
                    sb.Append("  | ").Append(Flatten(r.detail));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Flatten(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        internal static void CopyText(string text) {
            try {
                GUIUtility.systemCopyBuffer = text ?? "";
                SRApi.NotifyT("关卡记录已复制（{0} 字）", "Level record copied ({0} chars)", (text ?? "").Length);
            } catch (Exception ex) { SRApi.Guard.Log("关卡记录/复制", ex); }
        }

        internal static string ExportToFile() {
            string text = BuildText(CurrentRecs(), CurrentSnaps(), false);
            try {
                string dir = Path.Combine(Paths.ConfigPath, "SR_UCH", "records");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                SRApi.NotifyT("关卡记录已导出：{0}", "Level record exported: {0}", path);
                return path;
            } catch (Exception ex) {
                SRApi.Guard.Log("关卡记录/导出", ex);
                return null;
            }
        }

        internal static string Summary() {
            if (!_enabled.Value) return null;
            List<Rec> r = CurrentRecs();
            if (r.Count == 0) return SRApi.T("关卡记录：暂无事件", "Level Recorder: no events yet");
            return SRApi.T("关卡记录 · ", "Level Recorder · ") + r.Count +
                   SRApi.T(" 条 / 第 ", " events / round ") + CurrentRound() +
                   SRApi.T(" 回合 / ", " / ") + CurrentSnaps().Count + SRApi.T(" 份快照", " snapshots");
        }
    }
}
