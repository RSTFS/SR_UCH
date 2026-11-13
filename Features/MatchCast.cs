// 对局播报：把「只有房主屏幕上看得见」的状态，送到每个房客都有的原生聊天窗。
//
// 为什么仅房主安装就有效（游戏侧证据）：
//   ChatDisplay.NewChatMessage(msg, CHAT_Text, num)  :84077
//     -> NetworkManager.singleton.client.Send(NetMsgTypes.ChatSent, msgChat)  :84104
//   LobbyManager 注册 ChatSent 到 distributeServerMessage               :38931
//   distributeServerMessage = NetworkServer.SendToAll(...)               :39125
//   所以消息由服务器转发给所有客户端，没装 mod 的房客在原生聊天窗里就能看见，
//   不需要任何新 msgType、Harmony 补丁或反射登记。
//
// 已知边界：
//   · OnlineChatEmotes = EmotesOnly / ChatAndEmotesOff 时游戏会拒掉文字消息
//     （:84079-84090），此时降级成房主本机一条 Notify，不重试、不刷屏。
//   · 离线 / 无联机连接时 client.Send 没有目的地，只发本机提示。
//   · Chaos 是每回合随机抽牌，并没有预先生成的「整副牌序」，所以开局公告
//     公布的是本局启用的牌池，每回合再播真正抽到的那张 —— 这是最接近的可实现语义。
//
// 限流口径与通知中心同源：3 秒同类合并窗口、优先级分级（复用 sr.notify.priority）、
//   最小间隔、每回合上限。缺通知中心 DLL 时固定按「一般」处理，零 miss、零硬依赖。
//
// 刻意不碰：真实分数、相机、服务端补丁、新 msgType、多线程；不生成 Unity 对象。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;   // GameEventManager / IGameEventListener 都在 namespace GameEvent 内（反编译 151389）
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.cast", "MatchCast", "1.0", Provides = new string[] { "sr.cast.round", "sr.cast.end" })]
    public class MatchCast : ITweak {
        private const string Sec = "对局播报";
        private const string PoolSlot = "sr.cast.pool";
        private const string PrioritySlot = "sr.notify.priority";
        private const string RarestSlot = "sr.brief.rarest";
        private const float MergeWindow = 3f;   // 与通知中心 OnNotify 的同类合并窗口一致

        private static ConfigEntry<bool> _enabled, _announcePool, _announceRound, _announceEnd, _includeRarest;
        private static ConfigEntry<int> _minInterval, _maxPerRound, _maxChars;
        private static ConfigEntry<string> _prefix;

        private static MatchCastListener _listener;
        private static Func<string> _aPool, _aRarest;
        private static Func<string, string, int> _aPriority;
        private static int _bindTries;
        private static float _nextBind;

        private static float _lastSentAt = -999f, _lastKeyAt = -999f;
        private static string _lastKey = "";
        private static int _roundSent, _roundIndex;
        private static string _status = "";

        // ============ 生命周期 ============

        private static bool _uiReg;

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用对局播报", "Enable match cast");
            SRApi.LocDesc(Sec, "Enabled",
                "启用对局播报\n把房主才看得见的状态发到游戏原生聊天窗，房客零安装也能看见。",
                "Enable match cast\nSend host-only status to the vanilla chat window; guests need no mod.");
            SRApi.LocKey(Sec, "Announce Pool", "开局公布牌池", "Announce card pool");
            SRApi.LocDesc(Sec, "Announce Pool",
                "开局公布牌池\n开局发一条本局启用的混乱牌池（含可选的最稀有道具）。",
                "Announce card pool\nPost the enabled Chaos card pool at match start (optionally with the rarest piece).");
            SRApi.LocKey(Sec, "Announce Round", "每回合播抽牌", "Announce each draw");
            SRApi.LocDesc(Sec, "Announce Round",
                "每回合播抽牌\n每次抽牌后发一条本回合牌面；不预公布顺序，因为牌是逐回合随机抽的。",
                "Announce each draw\nPost the drawn cards each round; there is no fixed deck order to preview.");
            SRApi.LocKey(Sec, "Announce End", "局末发战报", "Announce match report");
            SRApi.LocDesc(Sec, "Announce End",
                "局末发战报\n局末把死亡鉴定书压成一行发到聊天窗。",
                "Announce match report\nPost a one-line summary of the death report at match end.");
            SRApi.LocKey(Sec, "Include Rarest", "附带最稀有道具", "Include rarest piece");
            SRApi.LocDesc(Sec, "Include Rarest",
                "附带最稀有道具\n开局公告里附一句本盒最稀有道具（品质光芒提供，缺功能时自动省略）。",
                "Include rarest piece\nAppend the rarest piece to the opening post (from Piece Glow; skipped if missing).");
            SRApi.LocKey(Sec, "Min Interval", "最小间隔（秒）", "Min interval (s)");
            SRApi.LocDesc(Sec, "Min Interval",
                "最小间隔（秒）\n两条播报之间的最小秒数（1-30）；高优先级可越过但仍留 0.5 秒硬间隔。",
                "Min interval (s)\nSeconds between posts. Range 1-30; high priority may bypass it but keeps a 0.5s floor.");
            SRApi.LocKey(Sec, "Max Per Round", "每回合上限", "Max per round");
            SRApi.LocDesc(Sec, "Max Per Round",
                "每回合上限\n每回合最多发几条（1-8），防止提案、重抽连点刷屏。",
                "Max per round\nHow many posts one round may produce. Range 1-8.");
            SRApi.LocKey(Sec, "Prefix", "文案前缀", "Post prefix");
            SRApi.LocDesc(Sec, "Prefix",
                "文案前缀\n每条播报开头的系统口吻标记，留空则不加。",
                "Post prefix\nSystem-style marker at the start of each post; empty disables it.");
            SRApi.LocKey(Sec, "Max Chars", "单条字数上限", "Max characters");
            SRApi.LocDesc(Sec, "Max Chars",
                "单条字数上限\n超过就截断并加省略号（40-200），不塞长文本进聊天窗。",
                "Max characters\nTruncate longer posts with an ellipsis. Range 40-200.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Min Interval": min = 1.0f; max = 30.0f; return true;
                    case "Max Per Round": min = 1.0f; max = 8.0f; return true;
                    case "Max Chars": min = 40.0f; max = 200.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "把房主才看得见的状态发到游戏原生聊天窗，房客零安装也能看见。");
            _announcePool = plugin.Config.Bind(Sec, "Announce Pool", true, "开局发一条本局启用的混乱牌池（含可选的最稀有道具）。");
            _announceRound = plugin.Config.Bind(Sec, "Announce Round", true, "每次抽牌后发一条本回合牌面。");
            _announceEnd = plugin.Config.Bind(Sec, "Announce End", true, "局末把死亡鉴定书压成一行发到聊天窗。");
            _includeRarest = plugin.Config.Bind(Sec, "Include Rarest", false, "开局公告里附一句本盒最稀有道具。");
            _minInterval = plugin.Config.Bind(Sec, "Min Interval", 3,
                new ConfigDescription("两条播报之间的最小秒数（1-30）。", new AcceptableValueRange<int>(1, 30)));
            _maxPerRound = plugin.Config.Bind(Sec, "Max Per Round", 3,
                new ConfigDescription("每回合最多发几条（1-8）。", new AcceptableValueRange<int>(1, 8)));
            _prefix = plugin.Config.Bind(Sec, "Prefix", "[SR] ", "每条播报开头的系统口吻标记，留空则不加。");
            _maxChars = plugin.Config.Bind(Sec, "Max Chars", 110,
                new ConfigDescription("超过就截断并加省略号（40-200）。", new AcceptableValueRange<int>(40, 200)));

            SRApi.LocSec(Sec, "对局播报", "Match Cast");
            RegUi();
            SRApi.Nav(Sec, 122);
            SRApi.RegisterPage(Sec, "Cast", "对局播报", "Cast", 10, RenderPage);
            SRApi.RegisterTick("对局播报-软绑定", Tick);

            // 抽牌 / 局末由生产方主动调用，不靠 StartPhaseEvent / GameEndEvent 抢事件顺序。
            Slots.Provide("sr.cast.round", (Action<string>)(a => OnRound(a)));
            Slots.Provide("sr.cast.end", (Action<string>)(a => OnEnd(a)));

            // 只监听开局：牌池是静态配置，不受 Chaos.OnNewMatch 清状态影响，无时序歧义。
            if (_listener == null) {
                _listener = new MatchCastListener();
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, true);
            }
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
        }

        // ITweak 没有 Shutdown 契约；留对称入口，将来契约补齐时可直接调用。
        public void Shutdown() {
            if (_listener != null) {
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, false);
                _listener = null;
            }
            Slots.Retract("sr.cast.round", null);
            Slots.Retract("sr.cast.end", null);
        }

        private sealed class MatchCastListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                if (e is GameEvent.NewMatchEvent) OnNewMatch();
            }
        }

        // ============ 软依赖（只查名字，不产生槽位缺失记录） ============

        private static void Tick() {
            if (_bindTries >= 8 || Time.unscaledTime < _nextBind) return;
            _bindTries++;
            foreach (string n in Slots.Names) {
                if (n == PoolSlot && _aPool == null) _aPool = Slots.Require<Func<string>>(n);
                else if (n == PrioritySlot && _aPriority == null) _aPriority = Slots.Require<Func<string, string, int>>(n);
                else if (n == RarestSlot && _aRarest == null) _aRarest = Slots.Require<Func<string>>(n);
            }
            _nextBind = Time.unscaledTime + 2f;
        }

        private static string Safe(Func<string> f) {
            try { return f != null ? f() : null; }
            catch (Exception ex) { SRApi.Guard.Log("对局播报/读槽位", ex); return null; }
        }

        // 优先级直接复用通知中心的 Classify：同一个分级口径，缺 DLL 时按「一般」。
        private static int Priority(string text, string kind) {
            try {
                if (_aPriority != null) return Mathf.Clamp(_aPriority(text, "对局播报"), 0, 2);
            } catch (Exception ex) { SRApi.Guard.Log("对局播报/优先级", ex); }
            return 1;
        }

        // ============ 三个播报时机 ============

        private static void OnNewMatch() {
            _roundSent = 0;
            _roundIndex = 0;
            if (!_enabled.Value || !_announcePool.Value) return;
            string pool = Safe(_aPool);
            if (string.IsNullOrEmpty(pool)) pool = SRApi.T("（牌池功能未安装）", "(card pool unavailable)");
            string extra = "";
            if (_includeRarest.Value) {
                string r = Safe(_aRarest);
                if (!string.IsNullOrEmpty(r))
                    extra = SRApi.T("；本盒最稀有：", "; rarest: ") + r;
            }
            Cast(SRApi.T("本局牌池：", "This match's card pool: ") + pool + extra, "open", true);
        }

        private static void OnRound(string cards) {
            if (!_enabled.Value || !_announceRound.Value) return;
            if (string.IsNullOrEmpty(cards)) return;
            // 由 Chaos 抽牌后主动调用，所以这一刻就是回合锚点：在这里重置额度，
            // 不需要监听 StartPhaseEvent，也就没有「谁先收到事件」的时序坑。
            _roundSent = 0;
            _roundIndex++;
            Cast(SRApi.T("第 ", "Round ") + _roundIndex + SRApi.T(" 回合抽牌：", " cards: ") + cards, "round", true);
        }

        private static void OnEnd(string report) {
            if (!_enabled.Value || !_announceEnd.Value) return;
            if (string.IsNullOrEmpty(report)) return;
            Cast(SRApi.T("本局战报：", "Match report: ") + Compact(report), "end", false);
        }

        /// <summary>多行鉴定书压成一行：只取前两行，换成顿号，不塞长文本。</summary>
        private static string Compact(string s) {
            string[] parts = (s ?? "").Replace("\r", "").Split('\n');
            var kept = new List<string>();
            for (int i = 0; i < parts.Length && kept.Count < 2; i++) {
                string t = parts[i].Trim();
                if (t.Length > 0) kept.Add(t);
            }
            return string.Join(SRApi.T("；", "; "), kept.ToArray());
        }

        // ============ 限流 + 投递 ============

        private static void Cast(string text, string kind, bool roundBound) {
            if (!_enabled.Value) return;
            string body = OneLine(text);
            if (body.Length == 0) return;
            float now = Time.unscaledTime;
            int level = Priority(body, kind);
            string key = kind + "|" + Norm(body);

            // 同类合并：与通知中心同一个 3 秒窗口。
            if (now - _lastKeyAt < MergeWindow && key == _lastKey) return;
            int quota = Mathf.Clamp(_maxPerRound.Value, 1, 8);
            if (roundBound && _roundSent >= quota) return;
            float gap = Mathf.Clamp(_minInterval.Value, 1, 30);
            if (level >= 2) {
                if (now - _lastSentAt < 0.5f) return;
            } else if (now - _lastSentAt < gap) return;

            _lastSentAt = now; _lastKeyAt = now; _lastKey = key;
            if (roundBound) _roundSent++;
            Send(body);
        }

        private static void Send(string text) {
            string pre = _prefix != null ? (_prefix.Value ?? "") : "";
            string body = pre + text;
            int max = Mathf.Clamp(_maxChars.Value, 40, 200);
            if (body.Length > max) body = body.Substring(0, Mathf.Max(1, max - 1)) + "…";

            bool online = false;
            try { online = NetworkClient.active; }
            catch (Exception ex) { SRApi.Guard.Log("对局播报/判联机", ex); }

            if (online && !ChatDisabled()) {
                try {
                    if (GameState.ChatSystem != null) {
                        int me = LocalNumber();
                        if (me < 0) me = 0;
                        GameState.ChatSystem.NewChatMessage(body, EmoteMeanings.CHAT_Text, me);
                        _status = SRApi.T("最近发送：", "Last post: ") + body;
                        return;
                    }
                } catch (Exception ex) { SRApi.Guard.Log("对局播报/发送聊天", ex); }
            }

            // 降级：文字聊天被游戏设置关掉、离线、或 ChatSystem 还没建好时，
            // 只给房主本机一条提示，不重试也不刷屏。
            _status = SRApi.T("最近发送（仅本机）：", "Last post (local only): ") + body;
            try { SRApi.Notify(body); }
            catch (Exception ex) { SRApi.Guard.Log("对局播报/本机提示", ex); }
        }

        // :84079-84090 游戏会在 EmotesOnly / ChatAndEmotesOff 时拒掉文字消息。
        private static bool ChatDisabled() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return false;
                var v = gs.OnlineChatEmotes;
                return v == OnlineChatEmotes.EmotesOnly || v == OnlineChatEmotes.ChatAndEmotesOff;
            } catch (Exception ex) { SRApi.Guard.Log("对局播报/判聊天设置", ex); return false; }
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
            } catch (Exception ex) { SRApi.Guard.Log("对局播报/本机玩家号", ex); }
            return -1;
        }

        private static string OneLine(string s) {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Norm(string s) {
            var sb = new System.Text.StringBuilder(40);
            for (int i = 0; i < s.Length && sb.Length < 40; i++) {
                char c = s[i];
                if (char.IsDigit(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ============ 页面 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Settings -"));
            CfgRow(_enabled);
            CfgRow(_announcePool);
            CfgRow(_announceRound);
            CfgRow(_announceEnd);
            CfgRow(_includeRarest);
            CfgRow(_minInterval);
            CfgRow(_maxPerRound);
            CfgRow(_prefix);
            CfgRow(_maxChars);

            if (!string.IsNullOrEmpty(_status)) Line(_status, _status);

            SRApi.Ctl.SectionTitle(SRApi.T("- 说明 -", "- Notes -"));
            Line("播报走游戏原生 ChatSent 消息，服务器会转发给全场；房客不用装 mod，在自带聊天窗里就能看见。",
                 "Posts use the game's native ChatSent message, which the server relays to everyone. Guests need no mod and see them in the vanilla chat window.");
            Line("开局公布的是本局启用的牌池，不是固定牌序 —— 混乱牌组是逐回合随机抽的，每回合再播真正抽到的那张。",
                 "The opening post lists the enabled card pool, not a fixed deck order: Chaos Deck draws randomly each round, and each draw is posted afterwards.");
            Line("限流沿用通知中心口径：3 秒同类合并、优先级分级、最小间隔、每回合上限；删掉通知中心只是退回固定优先级。",
                 "Throttling follows the Notification Hub: 3s same-kind merge, shared priority, min interval and a per-round cap. Removing that feature falls back to a fixed priority.");
            Line("若游戏设置里关了文字聊天，或处于离线状态，播报会降级成房主本机一条提示，不会重试刷屏。",
                 "If text chat is disabled in game settings or you are offline, posts degrade to a single local toast on the host. No retries, no spam.");
            Line("这里只发文本，不改任何游戏规则、分数或实体；文案可加前缀，超长会截断。",
                 "Text only: no rules, scores or entities are touched. A configurable prefix is added and long posts are truncated.");
        }
    }
}
