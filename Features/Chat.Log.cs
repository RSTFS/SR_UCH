// 会话 · 对话内容页：全量聊天记录、过滤、文本与高度缓存、顶部工具行、底部发送行。
// 栏目总览见 Chat.cs。

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
[SrFeature("sr.chat", "Chat", "1.0")]
public partial class Chat : ITweak {
        public class ChatEntry {
            public string time;
            public string sender;
            public Color color;
            public string text;
            public bool isQuick; 
        }

        private static readonly List<ChatEntry> _log = new List<ChatEntry>();

        public static List<ChatEntry> Entries { get { return _log; } }

        private static ConfigEntry<bool> _chatFilterQuickEntry;
        private static bool _chatFilterQuick;   //过滤快捷消息（表情/预设消息）
        private static ConfigEntry<bool> _chatShowTimeEntry;
        // 聊天记录保留条数，超出后从最旧的开始淘汰。
        private static ConfigEntry<int> _chatLogLimitEntry;
        // 取值必须夹紧在 100-5000：AcceptableValueRange 只约束 UI，配置文件被手改成越界值时会导致聊天记录恒被清空。
        private static int LogLimit() {
            try { if (_chatLogLimitEntry != null) return Mathf.Clamp(_chatLogLimitEntry.Value, 100, 5000); } catch { }
            return 500;
        }
        private static bool _chatShowTime = true; //每条消息前显示具体时间
        private static string _chatTextCache;   //消息文本缓存（每秒重建一次，避免每帧拼接）
        // 文本是否已构建过：全被过滤时 BuildChatText 返回 null，不能用"缓存为 null"表示尚未构建。
        private static bool _chatBuilt;
        // 高度缓存：CalcHeight 是对全部消息的全量排版测量，文本每秒才重建一次，无需每帧重算。
        // _chatHeightW 记录计算时所用宽度，宽度变化即失效重算。
        private static float _chatHeightCache;
        private static float _chatHeightW = -1f;
        private static float _chatTextTimer;
        private static int _lastChatCount;      //上次渲染的条目数（有新消息时滚到底）
        private static int _chatTickFrame = -1; //计时器上次递减的帧号（按帧，不按 OnGUI 事件）
        private static bool _chatCacheFilter, _chatCacheShowTime;
        private static string _chatInputText = ""; //底部发送框内容

        public static void Clear() {
            _log.Clear();
            _lastChatCount = 0;   //清零后新消息才能立刻触发滚到底
        }

        // 从本页发送聊天，走游戏原生 ChatSent 消息。
        public static void SendChatText(string text) {
            if (string.IsNullOrEmpty(text)) return;
            try {
                int num = LocalNetworkNumber();
                if (num < 0) return; 
                if (GameState.ChatSystem != null) {
                    GameState.ChatSystem.NewChatMessage(text, EmoteMeanings.CHAT_Text, num);
                }
            } catch (Exception e) {
                SRApi.LogWarn("Chat: send failed: " + e.Message);
            }
        }

        private static int LocalNetworkNumber() {
            try {
                if (LobbyManager.instance == null) return -1;
                NetworkLobbyPlayer[] slots = LobbyManager.instance.lobbySlots;
                if (slots == null) return -1;
                for (int i = 0; i < slots.Length; i++) {
                    LobbyPlayer lp = slots[i] as LobbyPlayer;
                    if (lp != null && lp.LocalPlayer != null) return lp.networkNumber;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("查找本地玩家 networkNumber", __ex); }
            return -1;
        }

        internal static void SelfRegLog() {
                        SRApi.RegisterPage("Chat", "ChatLog", "对话内容", "ChatLog", 20, Render); //会话栏目第 2 页
            // 顶部工具行与底部输入行必须画在滚动区之外（不随内容滚动），故注册为页面外框部件。
            SRApi.RegisterPageChrome("Chat", RenderToolbar, RenderInputRow);
            // 两个开关的本地化文案（配置在本文件 Bind 到 Chat 段）。
            SRApi.LocKey("Chat", "Filter Quick Msgs", "过滤快捷消息", null);
            SRApi.LocDesc("Chat", "Filter Quick Msgs", "快捷消息过滤开关\n从聊天记录中隐藏表情与预设消息，只保留玩家输入\n仅影响聊天记录显示，不改变游戏聊天内容\n关", "quick-message filter\nhides emotes and preset messages from the chat log, keeping player-typed text only\ndisplay only; the game chat content is unchanged\nOFF");
            SRApi.LocKey("Chat", "Show Time", "显示时间", null);
            SRApi.LocDesc("Chat", "Show Time", "时间戳显示开关\n在每条聊天记录前显示时间\n仅作用于本模组的聊天记录面板\n开", "timestamp switch\nshows the time before each message in the chat log\napplies to this mod chat log panel only\nON");
            // 预登记本页自动快捷键：不预登记则重启后在首次打开本页前按键无效。
            SRApi.HotkeyAction("chat.clear", "清空", () => { Clear(); _chatTextCache = null; _chatBuilt = false; });
            SRApi.HotkeyAction("chat.copy", "复制", () => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (System.Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); } });
            SRApi.HotkeyAction("chat.send", "发送", () => { SendChatText(_chatInputText); _chatInputText = ""; });
        }

        private static string BuildChatText(List<ChatEntry> entries) {
            StringBuilder sb = new StringBuilder();
            bool any = false;
            for (int i = 0; i < entries.Count; i++) {
                ChatEntry ce = entries[i];
                if (_chatFilterQuick && ce.isQuick) continue;
                any = true;
                if (_chatShowTime) {
                    sb.Append(ce.time).Append(' ');
                }
                // 名字与正文之间用不换行空格，避免宽度不足时在 ">" 后折行。
                sb.Append('<').Append(ce.sender).Append(">\u00A0").Append(ce.text).Append('\n');
            }
            return any ? sb.ToString() : null;
        }

        public static void Render() {
            // 大型滚动区显示全部记录；文本每秒重建一次跟随新消息，避免每帧拼接字符串。
            List<ChatEntry> entries = Entries;
            // Render 每帧被 Layout/Repaint 及输入事件调用多次，计时器必须按帧递减，否则每秒会重建多次。
            if (_chatTickFrame != Time.frameCount) {
                _chatTickFrame = Time.frameCount;
                _chatTextTimer -= Time.unscaledDeltaTime;
            }
            if (_chatTextTimer <= 0f || !_chatBuilt
                || _chatCacheFilter != _chatFilterQuick || _chatCacheShowTime != _chatShowTime) {
                _chatTextTimer = 1f;
                _chatCacheFilter = _chatFilterQuick;
                _chatCacheShowTime = _chatShowTime;
                _chatTextCache = BuildChatText(entries);
                _chatBuilt = true;
                _chatHeightW = -1f; //文本变了，高度作废
            }
            if (entries.Count == 0) {
                GUILayout.Label(SRApi.T("（暂无消息）", "(no messages)"), SRApi.Ctl.Label);
            } else if (string.IsNullOrEmpty(_chatTextCache)) {
                GUILayout.Label(SRApi.T("（已过滤全部消息）", "(all messages filtered)"), SRApi.Ctl.Label);
            } else {
                float w = Mathf.Max(SRApi.Ctl.Sc(140), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(36));
                float innerW = w - SRApi.Ctl.Sc(20);
                // 文本直接放外层滚动区，避免文本框与外层内容区双滚动条。
                // 高度按文本 + 宽度缓存，其余帧复用。
                if (_chatHeightW != innerW || _chatHeightCache <= 0f) {
                    _chatHeightW = innerW;
                    _chatHeightCache = SRApi.Ctl.ChatLabel.CalcHeight(new GUIContent(_chatTextCache), innerW) + SRApi.Ctl.Sc(8);
                }
                float th = Mathf.Max(SRApi.Ctl.Sc(200), _chatHeightCache);
                // 只读编辑框：借助 TextArea 获得文本选中与 Ctrl+A / Ctrl+C 复制（游戏本身没有文本选择功能）。
                // 忽略 TextArea 返回的编辑结果，不回写。
                GUILayout.TextArea(_chatTextCache, SRApi.Ctl.ChatArea, GUILayout.Width(innerW), GUILayout.Height(th));
                if (entries.Count > _lastChatCount) { SRApi.Ctl.ScrollToBottom(); } //新消息滚到底
                _lastChatCount = entries.Count;
            }
        }

        public static void RenderToolbar() {
            if (SRApi.SelectedPageId("Chat") != "ChatLog") return; //顶栏只属于对话内容页
            GUILayout.BeginHorizontal();
            //清空（可右键绑定快捷键）
            if (SRApi.Ctl.HotkeyButton("chat.clear", SRApi.T("清空", "Clear"), SRApi.T("清空聊天记录", "Clear the chat log"),
                () => { Clear(); _chatTextCache = null; _chatBuilt = false; },
                SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
                Clear();
                _chatTextCache = null;
                _chatBuilt = false; //强制重建，不等下一个计时点
            }
            GUILayout.Space(SRApi.Ctl.Sc(4));
            // 复制当前显示的聊天记录（已应用过滤）到剪贴板。
            if (SRApi.Ctl.HotkeyButton("chat.copy", SRApi.T("复制", "Copy"),
                SRApi.T("把当前显示的聊天记录复制到剪贴板（游戏里没有文本选择功能，用这个代替）", "Copy the currently shown chat log to the clipboard"),
                () => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); } },
                SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
                try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); }
            }
            GUILayout.Space(SRApi.Ctl.Sc(8));
            // 两个开关：标签（左键恢复默认 / 右键绑快捷键）+ 复选框。
            SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("过滤快捷消息", "Hide quick msgs"), SRApi.T("过滤快捷消息（表情/预设消息不显示）", "Hide quick msgs (emotes/presets)")),
                _chatFilterQuickEntry, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(26));
            if (_chatFilterQuickEntry != null) SRApi.Ctl.RenderControl(_chatFilterQuickEntry);
            GUILayout.Space(SRApi.Ctl.Sc(16));
            SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("显示具体时间", "Show time"), SRApi.T("每条消息前显示具体时间", "Show a timestamp before each message")),
                _chatShowTimeEntry, SRApi.Ctl.Sc(110), SRApi.Ctl.Sc(26));
            if (_chatShowTimeEntry != null) SRApi.Ctl.RenderControl(_chatShowTimeEntry);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SRApi.Ctl.Sc(4));
        }

        //底部发送行（固定在滚动区外）：Enter 发送。
        public static void RenderInputRow() {
            if (SRApi.SelectedPageId("Chat") != "ChatLog") return; //输入行只属于对话内容页
            GUILayout.Space(SRApi.Ctl.Sc(10));
            GUILayout.BeginHorizontal();
            // Enter / 小键盘 Enter 必须在 TextField 绘制前拦截并 Use()：IMGUI 单行 TextField 会消费
            // KeyDown Return 导致提交并失焦；空文本不发送。
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && _chatInputText.Length > 0) {
                SendChatText(_chatInputText);
                _chatInputText = "";
                Event.current.Use();
            }
            GUI.SetNextControlName("SRUCH_ChatInput");
            _chatInputText = SRApi.ClearableTextField(_chatInputText, SRApi.Ctl.SearchBox, GUILayout.Height(SRApi.Ctl.Sc(26)));
            if (SRApi.Ctl.HotkeyButton("chat.send", SRApi.T("发送", "Send"), SRApi.T("把输入内容作为聊天消息发到游戏聊天里", "Send the text as a chat message"),
                () => { SendChatText(_chatInputText); _chatInputText = ""; },
                SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
                SendChatText(_chatInputText);
                _chatInputText = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(SRApi.Ctl.Sc(4));
        }

        internal static void InitLog(IFeatureHost plugin) {
            SelfRegLog();
            // 本功能的配置随本功能绑在 Chat 段。
            _chatFilterQuickEntry = plugin.Config.Bind("Chat", "Filter Quick Msgs", false, "会话内容页：过滤快捷消息（表情/预设消息不显示）");
            _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatFilterQuickEntry.SettingChanged += (s, e) => _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatShowTimeEntry = plugin.Config.Bind("Chat", "Show Time", true, "会话内容页：每条消息前显示具体时间。默认开启。");
            // 保留条数可配，默认 500。
            _chatLogLimitEntry = plugin.Config.Bind("Chat", "Log Limit", 500,
                new ConfigDescription("会话内容页保留多少条消息（100-5000，超出后滚动淘汰最旧的）。调大可翻更久的历史。",
                    new AcceptableValueRange<int>(100, 5000)));
            _chatShowTime = _chatShowTimeEntry.Value;
            _chatShowTimeEntry.SettingChanged += (s, e) => _chatShowTime = _chatShowTimeEntry.Value;
        }

        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage")]
        [HarmonyPostfix]
        static void OnMessage(object[] __args) {
            // Harmony 补丁不在 SR 页面的 try/catch 覆盖范围内，异常会穿到游戏的网络消息接收路径，故整段兜住只记日志。
            try {
                if (!SRApi.GateMaster) return;
                if (__args == null || __args.Length == 0) return;
                if (!(__args[0] is ChatMessageDetails)) return;
                ChatMessageDetails details = (ChatMessageDetails)__args[0];
                // 按游戏自己的判据区分玩家消息与系统消息：isChatMessage 为假的是系统事件/系统提示。
                if (!details.isChatMessage) return;
                // 系统事件常常没有发送者名（如 PlayerVotedToKick），同样不记录。
                if (string.IsNullOrEmpty(details.UserName)) return;
                bool isQuick = false;
                string text = details.Message;
                if (string.IsNullOrEmpty(text)) {
                    if (details.EmoteType == EmoteMeanings.CHAT_Text) return; //空文字消息不记录
                    // 快捷消息取游戏内气泡的同一段文字（EmoteConverter -> I2 本地化，随游戏语言变化）。
                    text = EmoteSystem.EmoteConverter(details.EmoteType);
                    isQuick = true;
                }
                _log.Add(new ChatEntry {
                    time = DateTime.Now.ToString("HH:mm:ss"),
                    sender = string.IsNullOrEmpty(details.UserName) ? SRApi.T("未知", "Unknown") : details.UserName,
                    color = details.UserNameColor,
                    text = text,
                    isQuick = isQuick
                });
                if (_log.Count > LogLimit()) _log.RemoveAt(0); //保留最近 N 条（条数可配，滚动淘汰）
            } catch (Exception __ex) { SRApi.Guard.Log("聊天记录回调", __ex); }
        }
}
}
