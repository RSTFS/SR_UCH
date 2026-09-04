using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GCAfterLoad {
    //============================================================
    // GC After Load -- standalone "cleanup after level load" plugin
    // (English only, does not depend on SR_UCH)
    //  - Runs one GC.Collect + Resources.UnloadUnusedAssets after
    //    entering / switching levels, reducing in-match stutter.
    //  - Skipped on same-level round reloads (scene name unchanged),
    //    so round tallies are not slowed down.
    //  - The cleanup is delayed 1s past the loading fade-out, so the
    //    fade-out -> gameplay transition is not blocked (GC.Collect
    //    is synchronous and can take tens to hundreds of ms).
    //  - Toggle in the BepInEx config file (or Config Manager, F1):
    //      [Settings] Enabled = true
    //  - Do NOT run together with SR_UCH (which already includes
    //    this feature) -- both patches would apply.
    //============================================================
    [BepInPlugin("com.gamingbeast.GCAfterLoad", "GC After Load", "1.0.0")]
    public class GCAfterLoadPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        private static ConfigEntry<bool> _enabled;
        private static string _lastCleanedScene = "";
        private static float _pendingCleanupAt = -1f;
        private static string _pendingCleanupScene = "";

        private void Awake() {
            ModLogger = Logger;
            _enabled = Config.Bind(
                "Settings",
                "Enabled",
                false,
                "Run one GC.Collect + Resources.UnloadUnusedAssets after entering/switching levels (skipped on same-level round reloads), reducing in-match stutter.");
            Harmony.CreateAndPatchAll(typeof(GCAfterLoadPlugin));
            Logger.LogInfo("[GCAfterLoad] loaded, Enabled=" + _enabled.Value);
        }

        private void Update() {
            //delayed cleanup: run 1s after the loading fade-out (not blocking the transition)
            if (_pendingCleanupAt >= 0f && Time.unscaledTime >= _pendingCleanupAt) {
                _pendingCleanupAt = -1f;
                try {
                    _lastCleanedScene = _pendingCleanupScene;
                    System.GC.Collect();
                    Resources.UnloadUnusedAssets();
                } catch { }
            }
        }

        //Level load finished: schedule a one-time cleanup for this scene
        [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
        [HarmonyPostfix]
        private static void OnLoadEnd() {
            if (_enabled == null || !_enabled.Value) return;
            try {
                string sc = SceneManager.GetActiveScene().name;
                if (sc == _lastCleanedScene) return; //same-level round reload: skip
                _pendingCleanupScene = sc;
                _pendingCleanupAt = Time.unscaledTime + 1f;
            } catch { }
        }
    }
}
