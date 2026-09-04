using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChatHider {
    //============================================================
    // ChatHider — standalone "Hide Chat" plugin (English only)
    //  - Built-in toggle: Hide Chat Window (default OFF), a standard
    //    BepInEx config entry;
    //  - Controlled in-game by Config Manager (BepInEx Configuration
    //    Manager): F1 → Chat Hider → Hide Chat Window, applies instantly.
    //  - Hides the in-game chat UI (bubbles / input field); messages
    //    are still sent & received, only the UI is hidden.
    //============================================================
    [BepInPlugin("com.gamingbeast.ChatHider", "Chat Hider", "1.0.0")]
    public class ChatHiderPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        private static ConfigEntry<bool> _hideEntry;

        //Hide toggle: changes made in Config Manager take effect instantly
        public static bool HideChat { get { return _hideEntry != null && _hideEntry.Value; } }

        private void Awake() {
            ModLogger = Logger;
            _hideEntry = Config.Bind(
                "Settings",
                "Hide Chat Window",
                false,
                "Hide the in-game chat window (message bubbles / input field are hidden).\nChat messages are still sent and received; only the UI is hidden.\nEdit anytime in Config Manager - takes effect immediately.");
            Harmony.CreateAndPatchAll(typeof(ChatHiderPlugin));
            Logger.LogInfo("[ChatHider] loaded, HideChat=" + HideChat);
        }

        //Hide the in-game chat window: zero the bubble alpha and close the input field every frame
        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPrefix]
        static bool HideChatUpdate(ChatDisplay __instance) {
            if (!HideChat) return true;
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

        //Hide input: swallow chat input events (Enter / T opening the input field, etc.)
        [HarmonyPatch(typeof(ChatDisplay), "ReceiveEvent")]
        [HarmonyPrefix]
        static bool HideChatInput(InputEvent e) {
            if (!HideChat) return true;
            return false;
        }
    }
}
