using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChallengeRetry {
    //============================================================
    // Challenge Retry -- standalone plugin (English only, does not
    // depend on SR_UCH).
    //  In Challenge mode:
    //   - Auto Retry: automatically retry (restart the run) as soon
    //     as you die, without holding B.
    //   - Fast Retry: after death, hold B for the set Hold Seconds to
    //     retry fast (overrides the vanilla 0.5s hold).
    //  Toggle in the BepInEx config file (or Config Manager, F1):
    //    [Challenge Retry]  Auto Retry / Fast Retry / Hold Seconds
    //  Hold Seconds is a plain number box (not a slider), clamped 0.1-2.
    //  Do NOT run together with SR_UCH (which already includes this
    //  feature under its Experiments page).
    //============================================================
    [BepInPlugin("com.gamingbeast.challenge_retry", "Challenge Retry", "1.0.0")]
    public class ChallengeRetryPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        private static ConfigEntry<bool> _autoRetry;
        private static ConfigEntry<bool> _fastRetry;
        private static ConfigEntry<float> _hold;

        private void Awake() {
            ModLogger = Logger;
            _autoRetry = Config.Bind(
                "Challenge Retry", "Auto Retry", false,
                "In Challenge mode, automatically retry (restart the run) as soon as you die, without holding B.");
            _fastRetry = Config.Bind(
                "Challenge Retry", "Fast Retry", false,
                "In Challenge mode, after death hold B for the set Hold Seconds to retry fast.");
            _hold = Config.Bind(
                "Challenge Retry", "Hold Seconds", 0.5f,
                "Hold-B wait seconds for Fast Retry (a plain number box; 0 - 2 (0 = instant)).");
            Harmony.CreateAndPatchAll(typeof(ChallengeRetryPlugin));
            ModLogger.LogInfo("[Challenge Retry] loaded");
        }

        private static float HoldTime() { return Mathf.Clamp(_hold.Value, 0f, 2f); }

        private static bool InChallengeMode() {
            try {
                return GameSettings.GetInstance().GameMode == GameState.GameMode.CHALLENGE;
            } catch { return false; }
        }

        //Fast Retry: shorten the hold-B suicide wait in Challenge mode
        [HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
        [HarmonyPostfix]
        static void OverrideHoldTime(Character __instance) {
            if (__instance == null || !__instance.hasAuthority) return;
            if (!_fastRetry.Value) return;
            if (!InChallengeMode()) return;
            try { __instance.SuicideTime = HoldTime(); } catch { }
        }

        //Auto Retry: when dead in Challenge mode, ask for a retry immediately
        [HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
        [HarmonyPostfix]
        static void ForceAutoRetry(Character __instance) {
            if (__instance == null || !__instance.hasAuthority) return;
            if (!_autoRetry.Value) return;
            if (!InChallengeMode()) return;
            try {
                bool dead = !__instance.Success && (__instance.Dead || __instance.Dying || __instance.LocallyDead);
                if (dead && !__instance.WantsToRetry) {
                    MsgPlayerWantsToRetry msg = new MsgPlayerWantsToRetry { networkNumber = __instance.networkNumber };
                    UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PlayerWantsToRetry, msg);
                }
            } catch { }
        }
    }
}
