// 房主运维台：一页看全房间状态并一键复制；外加原生聊天窗里的 /sr 指令。
//
// 为什么在「仅房主安装」下它反而是最合理的形态：
//   房客屏幕没有任何 mod UI，所以诊断/管理信息天然只该出现在房主这一页。
//   而房客 → 房主唯一的通道是游戏原生聊天：MsgChat 由服务器 :38931 转发，
//   房主端 mod 能读到所有人发的文字；回复走 GameState.ChatSystem.NewChatMessage
//   （:84077 → :84104 发 ChatSent），未装 mod 的房客在原生的聊天窗里看得见。
//
// 游戏侧证据：
//   LobbyManager.NumActiveConnections :38720 / CurrentPing :38696 / AverageClientPing :38708
//   MsgChat { NetworkPlayerNumber, EmoteType, MessageText, isChatMessage } :46029
//   readMessage 对 ChatSent 解析为 MsgChat :39229；NetworkMessageReceivedEvent :151976
//   OnlineChatEmotes 设成 EmotesOnly / ChatAndEmotesOff 时文字消息会被游戏拒掉 :84079-84090
//
// 刻意不碰：Harmony 补丁、反射登记、新 msgType、真实分数、多线程。
// 所有汇总都是【只读】，聊天指令里唯一会写状态的 profile 只允许房主本人触发。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.hostconsole", "HostConsole", "1.0")]
    public class HostConsole : ITweak {
        private const string Sec = "房主运维台";
        private const int BindTriesMax = 6;
        private const float CmdCooldown = 1.5f;

        private static ConfigEntry<bool> _enabled, _allowCommands, _replyInChat, _showRoster;
        private static ConfigEntry<string> _prefix;
        private static ConfigEntry<KeyCode> _reportKey;

        private static Func<string> _fDream, _fChaos, _fRarest, _fRoster;
        private static int _bindTries;
        private static float _nextBind, _lastCmdAt;
        private static string _lastCmd = "", _status = "";
        private static GameEvent.IGameEventListener _listener;
        private static bool _ready;

        // ================= 生命周期 =================

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        // 列宽与核心 Sc() 同坐标系；契约层未暴露 EntryNameWidth，故用固定 180 设计像素。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        // 本页配置行的显示名与悬浮说明：缺 LocKey 时中文界面回退英文键名，
        // 缺 LocDesc 时英文界面完全没有提示（内部条目不会回退 Bind 的中文描述）。
        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用运维台", "Enable host console");
            SRApi.LocDesc(Sec, "Enabled",
                "启用运维台\n启用运维台汇总与聊天指令。",
                "Enable host console\nEnable the room report and chat commands.");
            SRApi.LocKey(Sec, "Allow Chat Commands", "允许聊天指令", "Allow chat commands");
            SRApi.LocDesc(Sec, "Allow Chat Commands",
                "允许聊天指令\n响应聊天窗里的 /sr 指令；只读指令所有人可用，profile 仅房主本人。",
                "Allow chat commands\nRespond to /sr commands in chat; read-only for everyone, profile commands for the host.");
            SRApi.LocKey(Sec, "Reply In Chat", "在聊天窗回复", "Reply in chat");
            SRApi.LocDesc(Sec, "Reply In Chat",
                "在聊天窗回复\n把指令结果发到原生聊天窗（未装 mod 的房客也能看见）。",
                "Reply in chat\nPost command results to the native chat window so guests without the mod can see them.");
            SRApi.LocKey(Sec, "Show Roster", "报告含房内清单", "Include roster");
            SRApi.LocDesc(Sec, "Show Roster",
                "报告含房内清单\n报告里包含房内 mod 清单。",
                "Include roster\nInclude the in-room mod list in the report.");
            SRApi.LocKey(Sec, "Command Prefix", "指令前缀", "Command prefix");
            SRApi.LocDesc(Sec, "Command Prefix",
                "指令前缀\n/sr",
                "Command prefix\nPrefix for chat commands, without a trailing space.");
            SRApi.LocKey(Sec, "Copy Report Key", "复制报告快捷键", "Copy report key");
            SRApi.LocDesc(Sec, "Copy Report Key",
                "复制报告快捷键\n一键复制房间报告。",
                "Copy report key\nCopy the room report in one keypress.");
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "启用运维台汇总与聊天指令。");
            _allowCommands = plugin.Config.Bind(Sec, "Allow Chat Commands", true,
                "响应聊天窗里的 /sr 指令；只读指令所有人可用，profile 仅房主本人。");
            _replyInChat = plugin.Config.Bind(Sec, "Reply In Chat", true, "把指令结果发到原生聊天窗（未装 mod 的房客也能看见）。");
            _showRoster = plugin.Config.Bind(Sec, "Show Roster", true, "报告里包含房内 mod 清单。");
            _prefix = plugin.Config.Bind(Sec, "Command Prefix", "/sr", "聊天指令前缀（不含空格）。");
            _reportKey = plugin.Config.Bind(Sec, "Copy Report Key", KeyCode.None, "一键复制房间报告。");

            SRApi.LocSec(Sec, "房主运维台", "Host Console");

            RegUi();
            SRApi.Nav(Sec, 120);
            SRApi.RegisterPage(Sec, "Console", "运维台", "Console", 10, RenderPage);
            SRApi.RegisterKey("房主运维台-复制报告", _reportKey, "press");
            SRApi.RegisterTick("房主运维台-绑定与快捷键", Tick);

            if (_listener == null) {
                _listener = new Listener();
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, true);
            }
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
            _ready = true;
        }

        public void Shutdown() {
            if (_listener != null) {
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
                if (_reportKey.Value != KeyCode.None && Input.GetKeyDown(_reportKey.Value))
                    CopyReport();
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/Tick", ex); }
        }

        private static void TryBind() {
            if (_fDream == null) _fDream = Slots.Require<Func<string>>("sr.brief.dream");
            if (_fChaos == null) _fChaos = Slots.Require<Func<string>>("sr.brief.chaos");
            if (_fRarest == null) _fRarest = Slots.Require<Func<string>>("sr.brief.rarest");
            if (_fRoster == null) _fRoster = Slots.Require<Func<string>>("sr.brief.roster");
        }

        private static string Safe(Func<string> f) {
            if (f == null) return null;
            try { return f(); } catch (Exception ex) { SRApi.Guard.Log("房主运维台/读槽位", ex); return null; }
        }

        // ================= 只读汇总 =================

        private static bool IsHost() {
            try { return NetworkServer.active; } catch (Exception ex) { SRApi.Guard.Log("房主运维台/判房主", ex); return false; }
        }

        private static int LocalNumber() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.lobbySlots == null) return -1;
                for (int i = 0; i < lm.lobbySlots.Length; i++) {
                    LobbyPlayer lp = lm.lobbySlots[i] as LobbyPlayer;
                    if (lp != null && lp.LocalPlayer != null) return lp.networkNumber;
                }
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/读本机玩家号", ex); }
            return -1;
        }

        private static string RoomLine() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return SRApi.T("不在房间中", "Not in a lobby");
                int conns = lm.NumActiveConnections;
                int ping = lm.CurrentPing;
                int avg = lm.AverageClientPing;
                int me = LocalNumber();
                return SRApi.T(IsHost() ? "房主" : "房客", IsHost() ? "Host" : "Guest") +
                       " · " + conns + SRApi.T(" 个连接", " connections") +
                       (me >= 0 ? SRApi.T(" · 本机 #", " · local #") + me : "") +
                       SRApi.T(" · Ping ", " · ping ") + ping + " ms" +
                       SRApi.T("（平均 ", " (avg ") + avg + " ms)";
            } catch (Exception ex) {
                SRApi.Guard.Log("房主运维台/读房间", ex);
                return SRApi.T("房间状态读取失败", "Failed to read room state");
            }
        }

        private static string GameLine() {
            string scene = "";
            string mode = "";
            int round = 0;
            try { scene = SRApi.Env.SceneName; } catch (Exception ex) { SRApi.Guard.Log("房主运维台/读场景", ex); }
            try {
                GameSettings st = GameSettings.GetInstance();
                if (st != null) mode = st.GameMode.ToString();
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/读模式", ex); }
            try {
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = lm != null ? lm.CurrentGameController : null;
                if (gc != null) round = gc.RoundNumber;
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/读回合", ex); }
            return SRApi.T("场景 ", "Scene ") + (string.IsNullOrEmpty(scene) ? "-" : scene) +
                   SRApi.T(" · 模式 ", " · mode ") + (string.IsNullOrEmpty(mode) ? "-" : mode) +
                   SRApi.T(" · 第 ", " · round ") + round;
        }

        internal static string BuildReport() {
            var sb = new StringBuilder();
            sb.Append("SR_UCH ").Append(SRApi.T("房主运维台 · 房间报告", "Host Console · room report"))
              .Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(RoomLine()).Append('\n');
            sb.Append(GameLine()).Append('\n');
            sb.Append(new string('-', 40)).Append('\n');
            AppendRow(sb, SRApi.T("时间控制", "Time control"), Safe(_fDream));
            AppendRow(sb, SRApi.T("混乱牌组", "Chaos deck"), Safe(_fChaos));
            AppendRow(sb, SRApi.T("品质光芒", "Piece glow"), Safe(_fRarest));
            if (_showRoster.Value) AppendRow(sb, SRApi.T("房内清单", "Mod roster"), Safe(_fRoster));
            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, string name, string value) {
            sb.Append(name).Append(": ").Append(string.IsNullOrEmpty(value) ? "-" : value.Replace('\n', ' ')).Append('\n');
        }

        private static void CopyReport() {
            string report = BuildReport();
            try {
                GUIUtility.systemCopyBuffer = report;
                SRApi.LogInfo("[房主运维台] 房间报告\n" + report);
                _status = SRApi.T("报告已复制并写入日志", "Report copied and written to the log");
                SRApi.Notify(_status);
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/复制报告", ex); }
        }

        // ================= 聊天指令 =================

        private sealed class Listener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    if (!_ready || !_enabled.Value || !_allowCommands.Value) return;
                    var nm = e as GameEvent.NetworkMessageReceivedEvent;
                    if (nm == null || nm.ReadMessage == null) return;
                    var chat = nm.ReadMessage as MsgChat;
                    if (chat == null || !chat.isChatMessage || string.IsNullOrEmpty(chat.MessageText)) return;
                    OnCommand(chat.NetworkPlayerNumber, chat.MessageText.Trim());
                } catch (Exception ex) { SRApi.Guard.Log("房主运维台/聊天指令", ex); }
            }
        }

        private static void OnCommand(int from, string text) {
            string pre = (_prefix != null ? _prefix.Value : "/sr") ?? "/sr";
            if (pre.Length == 0) pre = "/sr";
            if (!text.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return;
            string rest = text.Substring(pre.Length).Trim();
            if (rest.Length == 0) return;
            if (Time.unscaledTime - _lastCmdAt < CmdCooldown) return;
            _lastCmdAt = Time.unscaledTime;
            _lastCmd = "#" + from + " > " + text;

            string[] parts = rest.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";
            string reply;
            switch (cmd) {
                case "help":
                    reply = SRApi.T("/sr help | ping | rules | cards | report",
                                    "/sr help | ping | rules | cards | report");
                    break;
                case "ping":
                    reply = RoomLine();
                    break;
                case "rules":
                    reply = GameLine();
                    break;
                case "cards":
                    reply = SRApi.T("当前牌面：", "Active cards: ") + (Safe(_fChaos) ?? SRApi.T("（无）", "(none)"));
                    break;
                case "report":
                    CopyReport();
                    reply = SRApi.T("报告已复制到房主剪贴板并写入日志", "Report copied on the host and written to the log");
                    break;
                default:
                    reply = SRApi.T("未知指令，发 /sr help 看列表", "Unknown command; send /sr help");
                    break;
            }
            Reply(reply);
        }

        // 回复走原生聊天：未装 mod 的房客也能在原生聊天窗看见。
        // 游戏可能因 OnlineChatEmotes 拒绝文字消息（:84079-84090），所以房主端
        // 同时给一条 Notify，保证至少房主看得到结果。回复文本不以指令前缀开头，
        // 不会被自己再次当成指令。
        private static void Reply(string text) {
            try { SRApi.Notify(text); } catch (Exception ex) { SRApi.Guard.Log("房主运维台/本地提示", ex); }
            if (!_replyInChat.Value) return;
            try {
                // GameState.ChatSystem 是 public static ChatDisplay（:130063），
                // 由 GameState 初始化时 Instantiate（:130433）；树屋之外可能还没建好。
                if (GameState.ChatSystem == null) return;
                int me = LocalNumber();
                if (me < 0) me = 0;
                GameState.ChatSystem.NewChatMessage(text, EmoteMeanings.CHAT_Text, me);
            } catch (Exception ex) { SRApi.Guard.Log("房主运维台/聊天回复", ex); }
        }

        // ================= 页面 =================

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 配置 -", "- Settings -"));
            CfgRow(_enabled);
            CfgRow(_allowCommands);
            CfgRow(_replyInChat);
            CfgRow(_showRoster);
            CfgRow(_prefix);
            CfgRow(_reportKey);

            SRApi.Ctl.SectionTitle(SRApi.T("- 房间 -", "- Room -"));
            Line(RoomLine(), RoomLine());
            Line(GameLine(), GameLine());

            SRApi.Ctl.SectionTitle(SRApi.T("- 功能状态（只读汇总） -", "- Feature status (read-only) -"));
            Line(SRApi.T("时间控制：", "Time control: ") + (Safe(_fDream) ?? "-"), "Time control: " + (Safe(_fDream) ?? "-"));
            Line(SRApi.T("混乱牌组：", "Chaos deck: ") + (Safe(_fChaos) ?? "-"), "Chaos deck: " + (Safe(_fChaos) ?? "-"));
            Line(SRApi.T("品质光芒：", "Piece glow: ") + (Safe(_fRarest) ?? "-"), "Piece glow: " + (Safe(_fRarest) ?? "-"));
            if (_showRoster.Value)
                Line(SRApi.T("房内清单：", "Mod roster: ") + (Safe(_fRoster) ?? "-"), "Mod roster: " + (Safe(_fRoster) ?? "-"));

            SRApi.Ctl.SectionTitle(SRApi.T("- 操作 -", "- Actions -"));
            if (SRApi.Ctl.Button(SRApi.T("复制报告 + 写入日志", "Copy report + write to log"),
                                 SRApi.T("把整份房间报告复制到剪贴板", "Copy the whole room report")))
                CopyReport();
            if (!string.IsNullOrEmpty(_status)) Line(_status, _status);
            if (!string.IsNullOrEmpty(_lastCmd)) Line(SRApi.T("最近指令：", "Last command: ") + _lastCmd, "Last command: " + _lastCmd);

            SRApi.Ctl.SectionTitle(SRApi.T("- 聊天指令 -", "- Chat commands -"));
            Line("/sr help | ping | rules | cards | report",
                 "/sr help | ping | rules | cards | report");
            Line("房客用原生聊天窗发指令即可，房主端执行并回复到聊天窗；只有 profile 会写状态，且仅房主本人可用。1.5 秒限流。",
                 "Guests can send these from the vanilla chat window; the host executes and replies in chat. Only 'profile' writes state, host-only. 1.5s cooldown.");
            Line("若游戏设置里关了文字聊天，回复会被游戏拒掉；房主端仍会收到一条本地提示。自检结论请看「设置 / 自检」页。",
                 "If text chat is disabled in game settings, replies are rejected by the game; the host still gets a local toast. See Settings / Self-check for the health report.");
        }
    }
}
