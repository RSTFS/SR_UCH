using System;
using System.Collections.Generic;
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

        public static void Clear() {
            _log.Clear();
        }

        //从会话内容页发送聊天：走游戏原生 ChatSent 消息
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
            } catch { }
            return -1;
        }

        public void Initialize(MainPlugin plugin) {
            Harmony.CreateAndPatchAll(typeof(ChatLog));
        }

        //隐藏游戏内聊天窗口
        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPrefix]
        static bool HideChatUpdate(ChatDisplay __instance) {
            if (!ModManager.AllEnabled || !ModManager.HideChatWindow) return true;
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
            if (!ModManager.AllEnabled || !ModManager.HideChatWindow) return true;
            return false;
        }

        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage")]
        [HarmonyPostfix]
        static void OnMessage(object[] __args) {
            if (!ModManager.AllEnabled) return;
            if (__args == null || __args.Length == 0) return;
            if (!(__args[0] is ChatMessageDetails)) return;
            ChatMessageDetails details = (ChatMessageDetails)__args[0];
            if (!details.isChatMessage) return;
            bool isQuick = false;
            string text = details.Message;
            if (string.IsNullOrEmpty(text)) {
                if (details.EmoteType == EmoteMeanings.CHAT_Text) return; //空文字消息不记录
                text = "[" + ModManager.T(EmoteNameZh(details.EmoteType), details.EmoteType.ToString()) + "]";
                isQuick = true;
            }
            _log.Add(new ChatEntry {
                time = DateTime.Now.ToString("HH:mm:ss"),
                sender = string.IsNullOrEmpty(details.UserName) ? ModManager.T("未知", "Unknown") : details.UserName,
                color = details.UserNameColor,
                text = text,
                isQuick = isQuick
            });
            if (_log.Count > 100) _log.RemoveAt(0);
        }

        private static string EmoteNameZh(EmoteMeanings emote) {
            switch (emote) {
                case EmoteMeanings.EMOTE_Amazing: return "太棒了";
                case EmoteMeanings.EMOTE_BeRightBack: return "马上回来";
                case EmoteMeanings.EMOTE_Bomb: return "炸弹";
                case EmoteMeanings.EMOTE_GlueHere: return "放这";
                case EmoteMeanings.EMOTE_Goodbye: return "再见";
                case EmoteMeanings.EMOTE_GoodGame: return "好游戏";
                case EmoteMeanings.EMOTE_GoodIdea: return "好主意";
                case EmoteMeanings.EMOTE_GreatRun: return "跑得好";
                case EmoteMeanings.EMOTE_Hahaha: return "哈哈哈";
                case EmoteMeanings.EMOTE_Hello: return "你好";
                case EmoteMeanings.EMOTE_Higher: return "高点";
                case EmoteMeanings.EMOTE_HurryUp: return "快点";
                case EmoteMeanings.EMOTE_Impossible: return "不可能";
                case EmoteMeanings.EMOTE_Lower: return "低点";
                case EmoteMeanings.EMOTE_No: return "不";
                case EmoteMeanings.EMOTE_Nooo: return "不不";
                case EmoteMeanings.EMOTE_NoProblem: return "没问题";
                case EmoteMeanings.EMOTE_NotThatOne: return "不是那个";
                case EmoteMeanings.EMOTE_Okay: return "好的";
                case EmoteMeanings.EMOTE_OMG: return "天哪";
                case EmoteMeanings.EMOTE_Ouch: return "哎哟";
                case EmoteMeanings.EMOTE_OverHere: return "这边";
                case EmoteMeanings.EMOTE_Rematch: return "再来一局";
                case EmoteMeanings.EMOTE_SoClose: return "差一点";
                case EmoteMeanings.EMOTE_Sorry: return "抱歉";
                case EmoteMeanings.EMOTE_Thanks: return "谢谢";
                case EmoteMeanings.EMOTE_Thinking: return "思考";
                case EmoteMeanings.EMOTE_TooEasy: return "太简单";
                case EmoteMeanings.EMOTE_UhOh: return "哎呀";
                case EmoteMeanings.EMOTE_WaitingForAFriend: return "等朋友";
                case EmoteMeanings.EMOTE_WellDone: return "干得好";
                case EmoteMeanings.EMOTE_WellPlayed: return "打得好";
                case EmoteMeanings.EMOTE_Whoops: return "失误了";
                case EmoteMeanings.EMOTE_Wow: return "哇";
                case EmoteMeanings.EMOTE_Yeah: return "耶";
                case EmoteMeanings.EMOTE_Yes: return "是";
                case EmoteMeanings.EMOTE_NotThere: return "不在这";
                case EmoteMeanings.EMOTE_MoreTraps: return "更多陷阱";
                case EmoteMeanings.EMOTE_NiceOutfit: return "衣服不错";
                default: return emote.ToString();
            }
        }
    }
}
