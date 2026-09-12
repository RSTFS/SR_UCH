using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    public class ChatLog : ITweak {
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
        private static ConfigEntry<bool> _hideChatEntry;
        private static bool _hideChat;          //隐藏游戏内聊天窗口（会话内容页仍照常记录）
        private static ConfigEntry<bool> _chatShowTimeEntry;
        private static bool _chatShowTime = true; //每条消息前显示具体时间
        private static string _chatTextCache;   //消息文本缓存（每秒重建一次，避免每帧拼接）
        private static float _chatTextTimer;
        private static int _lastChatCount;      //上次渲染的条目数（判断是否有新消息 → 滚到底）
        private static bool _chatCacheFilter, _chatCacheShowTime;
        private static string _chatInputText = ""; //底部发送框内容

        public static bool HideWindow { get { return _hideChat; } }

        public static void Clear() {
            _log.Clear();
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
                MainPlugin.ModLogger.LogWarning("ChatLog: send failed: " + e.Message);
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

        private static void SelfReg() {
            SR.LocSec("Chat", "会话内容", null);
            SR.Nav("Chat", 80); //侧栏栏目顺序 80
            //三个开关的文案（配置已在本文件 Bind 到 Chat 段；即使被通用条目行渲染也是中文）
            SR.LocKey("Chat", "Filter Quick Msgs", "过滤快捷消息", null);
            SR.LocDesc("Chat", "Filter Quick Msgs", "过滤快捷消息（表情/预设消息不显示）", "Hide quick messages (emotes/presets) from the log");
            SR.LocKey("Chat", "Hide Chat Window", "隐藏聊天窗口", null);
            SR.LocDesc("Chat", "Hide Chat Window", "隐藏游戏内聊天窗口（消息气泡/输入框），本页仍照常记录", "Hide the in-game chat window; the log page still records everything");
            SR.LocKey("Chat", "Show Time", "显示时间", null);
            SR.LocDesc("Chat", "Show Time", "每条消息前显示具体时间", "Show the timestamp before each message");
        }

        private static string BuildChatText(List<ChatLog.ChatEntry> entries) {
            StringBuilder sb = new StringBuilder();
            bool any = false;
            for (int i = 0; i < entries.Count; i++) {
                ChatLog.ChatEntry ce = entries[i];
                if (_chatFilterQuick && ce.isQuick) continue;
                any = true;
                if (_chatShowTime) {
                    sb.Append(ce.time).Append(' ');
                }
                sb.Append('<').Append(ce.sender).Append("> ").Append(ce.text).Append('\n');
            }
            return any ? sb.ToString() : null;
        }

        public static void Render() {
            //大型滚动区显示全部记录；每秒重建一次文本（跟随新消息），避免每帧拼接字符串。
            List<ChatLog.ChatEntry> entries = ChatLog.Entries;
            _chatTextTimer -= Time.unscaledDeltaTime;
            if (_chatTextTimer <= 0f || _chatTextCache == null
                || _chatCacheFilter != _chatFilterQuick || _chatCacheShowTime != _chatShowTime) {
                _chatTextTimer = 1f;
                _chatCacheFilter = _chatFilterQuick;
                _chatCacheShowTime = _chatShowTime;
                _chatTextCache = BuildChatText(entries);
            }
            if (entries.Count == 0) {
                GUILayout.Label(SR.T("（暂无消息）", "(no messages)"), SR.Ctl.Label);
            } else if (string.IsNullOrEmpty(_chatTextCache)) {
                GUILayout.Label(SR.T("（已过滤全部消息）", "(all messages filtered)"), SR.Ctl.Label);
            } else {
                float w = Mathf.Max(SR.Ctl.Sc(140), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(36));
                float innerW = w - SR.Ctl.Sc(20);
                //聊天文本直接放外层滚动区，避免文本框与外层内容区双滚动条。
                float th = Mathf.Max(SR.Ctl.Sc(200), SR.Ctl.ChatLabel.CalcHeight(new GUIContent(_chatTextCache), innerW) + SR.Ctl.Sc(8));
                GUILayout.Label(_chatTextCache, SR.Ctl.ChatLabel, GUILayout.Width(innerW), GUILayout.Height(th));
                if (entries.Count > _lastChatCount) { SR.Ctl.ScrollToBottom(); } //新消息滚到底
                _lastChatCount = entries.Count;
            }
        }

        public static void RenderToolbar() {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SR.T("清空", "Clear"), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(56)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                ChatLog.Clear();
                _chatTextCache = null;
            }
            GUILayout.Space(SR.Ctl.Sc(8));
            if (GUILayout.Button(_chatFilterQuick ? "✓" : "", _chatFilterQuick ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(26)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                _chatFilterQuick = !_chatFilterQuick;
                if (_chatFilterQuickEntry != null) _chatFilterQuickEntry.Value = _chatFilterQuick; //配置持久化
            }
            GUILayout.Label(SR.T("过滤快捷消息", "Hide quick msgs"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.Space(SR.Ctl.Sc(16));
            if (GUILayout.Button(_chatShowTime ? "✓" : "", _chatShowTime ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(26)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                _chatShowTime = !_chatShowTime;
                if (_chatShowTimeEntry != null) _chatShowTimeEntry.Value = _chatShowTime; //配置持久化
            }
            GUILayout.Label(SR.T("显示具体时间", "Show time"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.Space(SR.Ctl.Sc(16));
            if (GUILayout.Button(_hideChat ? "✓" : "", _hideChat ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(26)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                _hideChat = !_hideChat;
                if (_hideChatEntry != null) _hideChatEntry.Value = _hideChat; //配置持久化
            }
            GUILayout.Label(SR.T("隐藏聊天窗口", "Hide chat window"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
        }

        //底部发送行（固定在滚动区外）：Enter 发送。
        public static void RenderInputRow() {
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
            _chatInputText = GUILayout.TextField(_chatInputText, SR.Ctl.SearchBox, GUILayout.Height(SR.Ctl.Sc(26)));
            if (GUILayout.Button(new GUIContent(SR.T("发送", "Send"), SR.T("把输入内容作为聊天消息发到游戏聊天里", "Send the text as a chat message")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(56)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                SendChatText(_chatInputText);
                _chatInputText = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            //三个开关绑在 Chat 段（与"功能自包含"一致：本功能的配置跟本功能走；老配置由 ConfigMigration 从 Settings 搬过来）
            _chatFilterQuickEntry = plugin.Config.Bind("Chat", "Filter Quick Msgs", false, "会话内容页：过滤快捷消息（表情/预设消息不显示）");
            _chatFilterQuick = _chatFilterQuickEntry.Value;
            _chatFilterQuickEntry.SettingChanged += (s, e) => _chatFilterQuick = _chatFilterQuickEntry.Value;
            _hideChatEntry = plugin.Config.Bind("Chat", "Hide Chat Window", false, "隐藏游戏内聊天窗口（消息气泡/输入框不显示），会话内容页仍照常记录聊天。默认关闭。");
            _hideChat = _hideChatEntry.Value;
            _hideChatEntry.SettingChanged += (s, e) => _hideChat = _hideChatEntry.Value;
            _chatShowTimeEntry = plugin.Config.Bind("Chat", "Show Time", true, "会话内容页：每条消息前显示具体时间。默认开启。");
            _chatShowTime = _chatShowTimeEntry.Value;
            _chatShowTimeEntry.SettingChanged += (s, e) => _chatShowTime = _chatShowTimeEntry.Value;
            Harmony.CreateAndPatchAll(typeof(ChatLog));
        }

        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPrefix]
        static bool HideChatUpdate(ChatDisplay __instance) {
            if (!SR.GateMaster || !SR.HideChatWindow) return true;
            try {
                if (__instance.ChatCanvasGroup != null) __instance.ChatCanvasGroup.alpha = 0f;
                __instance.ChatMode = false;
                if (__instance.currentChatInputField != null && __instance.currentChatInputField.gameObject.activeSelf)
                    __instance.currentChatInputField.gameObject.SetActive(false);
                return false;
            } catch {
                return true;
            }
        }

        [HarmonyPatch(typeof(ChatDisplay), "ReceiveEvent")]
        [HarmonyPrefix]
        static bool HideChatInput(InputEvent e) {
            if (!SR.GateMaster || !SR.HideChatWindow) return true;
            return false;
        }

        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage")]
        [HarmonyPostfix]
        static void OnMessage(object[] __args) {
            if (!SR.GateMaster) return;
            if (__args == null || __args.Length == 0) return;
            if (!(__args[0] is ChatMessageDetails)) return;
            ChatMessageDetails details = (ChatMessageDetails)__args[0];
            if (!details.isChatMessage) return;
            bool isQuick = false;
            string text = details.Message;
            if (string.IsNullOrEmpty(text)) {
                if (details.EmoteType == EmoteMeanings.CHAT_Text) return; //空文字消息不记录
                //快捷消息：直接取游戏内气泡同一段文字（EmoteConverter → I2 本地化，随游戏语言变化）。
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
            if (_log.Count > 500) _log.RemoveAt(0); //保留最近 500 条（滚动淘汰）
        }
    }
}
