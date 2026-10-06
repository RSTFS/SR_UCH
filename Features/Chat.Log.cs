// 会话 · 对话内容页。全量聊天记录、过滤、文本构建与高度缓存，
// 顶部工具行（清空 / 复制 / 两个开关）和底部发送行。
// 整个会话栏目的总览看同目录的 Chat.cs。

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
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
        private static bool _chatFilterQuick;   //过滤快捷消息（表情/预设消息不显示）
        private static ConfigEntry<bool> _chatShowTimeEntry;
        // **可配**：聊天记录保留条数（原来硬编码 500 条滚动淘汰）,  调大可翻更久的历史
        private static ConfigEntry<int> _chatLogLimitEntry;
        // R396 N9：原来直接返回配置值，而 AcceptableValueRange(100,5000) **只约束 UI**。
        //  cfg 被手改/旧版本残留越界值时，下面那行 if (_log.Count > LogLimit()) RemoveAt(0)
        //  每次都成立 -> **聊天记录永久为空**，界面毫无提示。
        //  写法照抄 PlayerTracker.SkipFrames（同项目已有先例）。
        private static int LogLimit() {
            try { if (_chatLogLimitEntry != null) return Mathf.Clamp(_chatLogLimitEntry.Value, 100, 5000); } catch { }
            return 500;
        }
        private static bool _chatShowTime = true; //每条消息前显示具体时间
        private static string _chatTextCache;   //消息文本缓存（每秒重建一次，避免每帧拼接）
        //文本是否已构建过：全过滤时 BuildChatText 返回 null，"缓存==null"会每帧成立 -> 每帧白遍历 500 条。
        //用独立标志区分还没构建与构建结果就是空，重构只由计时器/开关变化触发。
        private static bool _chatBuilt;
        //高度缓存：CalcHeight 是对最多 500 条消息的全量排版测量，而文本每秒才重建一次 ->
        //没必要每帧重算（OnGUI 每帧至少跑两次：Layout + Repaint）。
        //_chatHeightW 记录"算这个高度时用的宽度"，窗口缩放导致宽度变化时强制重算。
        private static float _chatHeightCache;
        private static float _chatHeightW = -1f;
        private static float _chatTextTimer;
        private static int _lastChatCount;      //上次渲染的条目数（判断是否有新消息 -> 滚到底）
        private static int _chatTickFrame = -1; //计时器上次递减的帧号（按帧而非按 OnGUI 事件）
        private static bool _chatCacheFilter, _chatCacheShowTime;
        private static string _chatInputText = ""; //底部发送框内容

        public static void Clear() {
            _log.Clear();
            _lastChatCount = 0;   //清空后新消息应能立刻触发"滚到底"（否则要等条数重新超过旧值）
        }

        //从会话内容页发送聊天，走游戏原生 ChatSent 消息。
        public static void SendChatText(string text) {
            if (string.IsNullOrEmpty(text)) return;
            try {
                int num = LocalNetworkNumber();
                if (num < 0) return; 
                if (GameState.ChatSystem != null) {
                    GameState.ChatSystem.NewChatMessage(text, EmoteMeanings.CHAT_Text, num);
                }
            } catch (Exception e) {
                SR.LogWarn("Chat: send failed: " + e.Message);
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
            } catch (Exception __ex) { SR.Guard.Log("查找本地玩家 networkNumber", __ex); }
            return -1;
        }

        internal static void SelfRegLog() {
                        SR.RegisterPage("Chat", "ChatLog", "对话内容", "ChatLog", 20, Render); //会话栏目第 2 页
            //顶部工具行 / 底部输入行必须画在滚动区之外（不随内容滚动）-> 注册成"页面外框"部件，
            //由管理器在选中本栏目时调用（核心不再写 section == "Chat" 这种判断）
            SR.RegisterPageChrome("Chat", RenderToolbar, RenderInputRow);
            //两个开关的文案（配置已在本文件 Bind 到 Chat 段；即使被通用条目行渲染也是中文）
            SR.LocKey("Chat", "Filter Quick Msgs", "过滤快捷消息", null);
            SR.LocDesc("Chat", "Filter Quick Msgs", "把表情和预设消息从聊天记录里藏起来, 只留玩家真正打的字.", "Hide quick messages (emotes/presets) from the log");
            SR.LocKey("Chat", "Show Time", "显示时间", null);
            SR.LocDesc("Chat", "Show Time", "在每条聊天记录前面显示一下时间.", "Show the timestamp before each message");
            // **预登记本页自动快捷键**：不用等页面首次渲染，否则重启后不打开会话页就按不出这些键。
            SR.HotkeyAction("chat.clear", "清空", () => { Clear(); _chatTextCache = null; _chatBuilt = false; });
            SR.HotkeyAction("chat.copy", "复制", () => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (System.Exception __ex) { SR.Guard.Log("复制聊天记录", __ex); } });
            SR.HotkeyAction("chat.send", "发送", () => { SendChatText(_chatInputText); _chatInputText = ""; });
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
                //"<名字>" 与正文之间用不换行空格：宽度不够时不会正好从 ">" 后面折到下一行（看着像正文跑到了下一行）
                sb.Append('<').Append(ce.sender).Append(">\u00A0").Append(ce.text).Append('\n');
            }
            return any ? sb.ToString() : null;
        }

        public static void Render() {
            //大型滚动区显示全部记录；每秒重建一次文本（跟随新消息），避免每帧拼接字符串。
            List<ChatEntry> entries = Entries;
            //每次 Render 都会被 Layout/Repaint 及若干输入事件调用多次：计时器必须按帧递减，
            //否则"每秒重建"实际每秒重建 N 次（BuildChatText/CalcHeight 高频重跑）。
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
                _chatHeightW = -1f; //文本变了 -> 高度作废，下次渲染重算
            }
            if (entries.Count == 0) {
                GUILayout.Label(SR.T("（暂无消息）", "(no messages)"), SR.Ctl.Label);
            } else if (string.IsNullOrEmpty(_chatTextCache)) {
                GUILayout.Label(SR.T("（已过滤全部消息）", "(all messages filtered)"), SR.Ctl.Label);
            } else {
                float w = Mathf.Max(SR.Ctl.Sc(140), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(36));
                float innerW = w - SR.Ctl.Sc(20);
                //聊天文本直接放外层滚动区，避免文本框与外层内容区双滚动条。
                //高度按文本 + 宽度缓存：文本每秒重建一次时作废（见上方 _chatHeightW = -1f），
                //宽度随窗口缩放变化时也会重算，其余帧直接复用。
                if (_chatHeightW != innerW || _chatHeightCache <= 0f) {
                    _chatHeightW = innerW;
                    _chatHeightCache = SR.Ctl.ChatLabel.CalcHeight(new GUIContent(_chatTextCache), innerW) + SR.Ctl.Sc(8);
                }
                float th = Mathf.Max(SR.Ctl.Sc(200), _chatHeightCache);
                //只读编辑框：文本可像编辑框那样选中、Ctrl+A / Ctrl+C 复制（游戏本身没有文本选择功能）。
                //IMGUI 的 TextArea 会返回编辑后的文本，这里直接忽略（不回写）-> 只读，但不影响选择/复制。
                GUILayout.TextArea(_chatTextCache, SR.Ctl.ChatArea, GUILayout.Width(innerW), GUILayout.Height(th));
                if (entries.Count > _lastChatCount) { SR.Ctl.ScrollToBottom(); } //新消息滚到底
                _lastChatCount = entries.Count;
            }
        }

        public static void RenderToolbar() {
            if (SR.SelectedPageId("Chat") != "ChatLog") return; //顶栏只属于对话内容页
            GUILayout.BeginHorizontal();
            //清空（可右键绑定快捷键）
            if (SR.Ctl.HotkeyButton("chat.clear", SR.T("清空", "Clear"), SR.T("清空聊天记录", "Clear the chat log"),
                () => { Clear(); _chatTextCache = null; _chatBuilt = false; },
                SR.Ctl.Sc(56), SR.Ctl.Sc(26))) {
                Clear();
                _chatTextCache = null;
                _chatBuilt = false; //清空后强制重建（否则要等下一个 1 秒计时点）
            }
            GUILayout.Space(SR.Ctl.Sc(4));
            //复制：把当前显示（应用了"过滤快捷消息"）的聊天记录整段复制到剪贴板, 原来这里是纯标签，没法选中复制
            if (SR.Ctl.HotkeyButton("chat.copy", SR.T("复制", "Copy"),
                SR.T("把当前显示的聊天记录复制到剪贴板（游戏里没有文本选择功能，用这个代替）", "Copy the currently shown chat log to the clipboard"),
                () => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SR.Guard.Log("复制聊天记录", __ex); } },
                SR.Ctl.Sc(56), SR.Ctl.Sc(26))) {
                try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SR.Guard.Log("复制聊天记录", __ex); }
            }
            GUILayout.Space(SR.Ctl.Sc(8));
            //两个开关：标签（左键恢复默认 / 右键绑快捷键）+ 复选框，与通用条目行同一套写法
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("过滤快捷消息", "Hide quick msgs"), SR.T("过滤快捷消息（表情/预设消息不显示）", "Hide quick msgs (emotes/presets)")),
                _chatFilterQuickEntry, SR.Ctl.Sc(120), SR.Ctl.Sc(26));
            if (_chatFilterQuickEntry != null) SR.Ctl.RenderControl(_chatFilterQuickEntry);
            GUILayout.Space(SR.Ctl.Sc(16));
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("显示具体时间", "Show time"), SR.T("每条消息前显示具体时间", "Show a timestamp before each message")),
                _chatShowTimeEntry, SR.Ctl.Sc(110), SR.Ctl.Sc(26));
            if (_chatShowTimeEntry != null) SR.Ctl.RenderControl(_chatShowTimeEntry);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
        }

        //底部发送行（固定在滚动区外）：Enter 发送。
        public static void RenderInputRow() {
            if (SR.SelectedPageId("Chat") != "ChatLog") return; //输入行只属于对话内容页
            GUILayout.Space(SR.Ctl.Sc(10));
            GUILayout.BeginHorizontal();
            //Enter/小键盘 Enter 必须在 TextField 绘制前拦截：IMGUI 单行 TextField 会消费
            //KeyDown Return（提交并失焦）；先捕获并 Use() 可保持焦点连续发送（空文本不发送）。
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && _chatInputText.Length > 0) {
                SendChatText(_chatInputText);
                _chatInputText = "";
                Event.current.Use();
            }
            GUI.SetNextControlName("SRUCH_ChatInput");
            _chatInputText = SR.ClearableTextField(_chatInputText, SR.Ctl.SearchBox, GUILayout.Height(SR.Ctl.Sc(26)));
            if (SR.Ctl.HotkeyButton("chat.send", SR.T("发送", "Send"), SR.T("把输入内容作为聊天消息发到游戏聊天里", "Send the text as a chat message"),
                () => { SendChatText(_chatInputText); _chatInputText = ""; },
                SR.Ctl.Sc(56), SR.Ctl.Sc(26))) {
                SendChatText(_chatInputText);
                _chatInputText = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
        }

        internal static void InitLog(IFeatureHost plugin) {
            SelfRegLog();
            //三个开关绑在 Chat 段（与"功能自包含"一致：本功能的配置跟本功能走；老配置由 ConfigMigration 从 Settings 搬过来）
            _chatFilterQuickEntry = plugin.Config.Bind("Chat", "Filter Quick Msgs", false, "会话内容页：过滤快捷消息（表情/预设消息不显示）");
            _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatFilterQuickEntry.SettingChanged += (s, e) => _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatShowTimeEntry = plugin.Config.Bind("Chat", "Show Time", true, "会话内容页：每条消息前显示具体时间。默认开启。");
            // **可配**：保留条数（原来硬编码 500）
            _chatLogLimitEntry = plugin.Config.Bind("Chat", "Log Limit", 500,
                new ConfigDescription("会话内容页保留多少条消息（100-5000，超出后滚动淘汰最旧的）。调大可翻更久的历史。",
                    new AcceptableValueRange<int>(100, 5000)));
            _chatShowTime = _chatShowTimeEntry.Value;
            _chatShowTimeEntry.SettingChanged += (s, e) => _chatShowTime = _chatShowTimeEntry.Value;
        }

        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage")]
        [HarmonyPostfix]
        static void OnMessage(object[] __args) {
            //Harmony 补丁不在 SR 页面 try/catch 覆盖范围内：异常会穿到游戏网络消息接收路径，
            //与同功能其它补丁一致，这里整段兜住只记日志。
            try {
                if (!SR.GateMaster) return;
                if (__args == null || __args.Length == 0) return;
                if (!(__args[0] is ChatMessageDetails)) return;
                ChatMessageDetails details = (ChatMessageDetails)__args[0];
                // **R396 N8**：isChatMessage 是死字段, ChatMessageDetails 的两个构造函数
                //  （反编译 84440 / 84455）都无条件 isChatMessage = true，全游戏无其它赋值点。
                //  -> 这道守卫拦不住任何东西，系统提示照样进日志：
                //    DisplayNewMessage 收到的一切都记，于是 SomeoneJoined_lobby（42015）、
                //    PlayerVotedToKick（39956）这类系统事件被显示成"某某: 有人加入了房间"
                //    （UserName 是发起者），而它们的 Message 字段颜色还与玩家消息不同。
                //  改按**语义**过滤：系统消息用 Animals.NONE + 非空 Message。
                if (details.Animal == Character.Animals.NONE && !string.IsNullOrEmpty(details.Message)) return;
                if (!details.isChatMessage) return;   //保留原守卫（将来游戏若修正该字段仍安全）
                bool isQuick = false;
                string text = details.Message;
                if (string.IsNullOrEmpty(text)) {
                    if (details.EmoteType == EmoteMeanings.CHAT_Text) return; //空文字消息不记录
                    //快捷消息：直接取游戏内气泡同一段文字（EmoteConverter -> I2 本地化，随游戏语言变化）。
                    text = EmoteSystem.EmoteConverter(details.EmoteType);
                    isQuick = true;
                }
                _log.Add(new ChatEntry {
                    time = DateTime.Now.ToString("HH:mm:ss"),
                    sender = string.IsNullOrEmpty(details.UserName) ? SR.T("未知", "Unknown") : details.UserName,
                    color = details.UserNameColor,
                    text = text,
                    isQuick = isQuick
                });
                if (_log.Count > LogLimit()) _log.RemoveAt(0); //保留最近 N 条（条数可配，滚动淘汰）
            } catch (Exception __ex) { SR.Guard.Log("聊天记录回调", __ex); }
        }
}
}
