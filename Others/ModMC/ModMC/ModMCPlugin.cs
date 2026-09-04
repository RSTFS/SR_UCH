using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModMC {
    //============================================================
    // Mod Lobby (ModMC) -- standalone "Modded Lobby" plugin
    // (English only, does not depend on SR_UCH)
    //  - Adds a "Mod Lobby" button to the main menu (4th button).
    //  - The mod lobby lists only rooms whose host also runs this
    //    mod (filtered by the "usingMods" version prefix); vanilla
    //    4 players; invite codes are 5 chars starting with 'R',
    //    separate from vanilla codes.
    //  - Compatible with the original Even More Players mod
    //    (github.com/batram/UCH-EvenMorePlayers): when it is
    //    installed, our per-frame layout postfix runs with a LOW
    //    priority (after EMP's), so the 4-button row stays correct;
    //    our OnAccept prefix runs with a HIGH priority (before EMP's)
    //    so switching between More Online and Mod Lobby resets state
    //    in the right order. EMP keeps its own "Play More" behavior.
    //  - Do NOT run together with SR_UCH (which already includes
    //    this feature) -- both would patch the same methods.
    //============================================================
    [BepInPlugin("com.gamingbeast.ModMC", "Mod Lobby (ModMC)", "1.0.0")]
    public partial class ModLobbyPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        //Master switch (Settings section in the BepInEx config file).
        private static ConfigEntry<bool> _enabled;
        public static bool Enabled => _enabled != null && _enabled.Value;

        //Whether the mod lobby is active: the user clicked the "Mod Lobby"
        //button (_modActive), or joined a usingMods room (_autoActive).
        private static bool _modActive;
        private static bool _autoActive;
        private static bool Active => Enabled && (_modActive || _autoActive);

        //Reset back to vanilla (version number / 4 players / input field).
        //Called when leaving the mod lobby (clicking Play / Play More /
        //Play Online), so the other online entrance takes over cleanly.
        public static void ResetToVanilla() {
            try {
                _modActive = false;
                _autoActive = false;
                GameSettings gs = null;
                try { gs = GameSettings.GetInstance(); } catch { }
                if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null) {
                    SetF(gs, "versionNumber", _ogVersion);
                    SetF(gs, "parsedMatchmakingNumber", null);
                    SetF(gs, "parsedVersionNumberProd", null);
                }
                PlayerManager.maxPlayers = 4;
                ModCode.CleanGUI();
            } catch (Exception ex) {
                ModLogger.LogWarning("[Mod Lobby] ResetToVanilla: " + ex.Message);
            }
        }

        //Vanilla version number snapshot (read at startup, restored on reset).
        private static string _ogVersion = "1.0.0.0";

        //Version prefix: usingMods (identical to SR_UCH, so standalone and
        //SR_UCH mod lobbies interop; the 0817 suffix is user-specified and
        //does not touch the game version appended afterwards).
        public static string ModVersionFull => "usingMods_0817";

        //Lobby patches (apply once, gated at runtime by Active).
        private static Harmony _harmony;
        //Menu patches (separate Harmony, always alive).
        private static Harmony _menuHarmony;

        private void Awake() {
            ModLogger = Logger;
            _enabled = Config.Bind(
                "Settings",
                "Enabled",
                false,
                "Add a \"Mod Lobby\" button to the main menu; entering it lists only rooms whose host also runs this mod (vanilla 4 players, invite codes start with R).");
            try {
                GameSettings gs = GameSettings.GetInstance();
                if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null) {
                    string version = (string)GetF(gs, "versionNumber");
                    if (!string.IsNullOrEmpty(version)) {
                        _ogVersion = version;
                    }
                }
            } catch { }
            if (string.IsNullOrEmpty(_ogVersion)) {
                _ogVersion = "1.0.0.0";
            }
            //Mod lobby keeps the vanilla 4-player cap (never inflated).
            PlayerManager.maxPlayers = 4;
            _harmony = new Harmony("ModMC.Lobby");
            _harmony.CreateClassProcessor(typeof(LobbyManagerCtorPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PickableNetworkButtonUpdateCtorPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PickableNetworkOnAcceptCtorPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenCtorPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenOnClickCopyLobbyCodeCtorPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenAwakeCtorPatch)).Patch();
            _menuHarmony = new Harmony("ModMC.Menu");
            MenuPatch.PatchMenu(_menuHarmony);
            ModLogger.LogInfo("[Mod Lobby] loaded, version prefix = " + ModVersionFull);
        }

        //====================================================================
        // Reflection helpers (GameSettings private instance fields)
        //====================================================================
        private static object GetF(object obj, string name) {
            try {
                FieldInfo fieldInfo = AccessTools.Field(obj.GetType(), name);
                return (fieldInfo != null) ? fieldInfo.GetValue(obj) : null;
            } catch {
                return null;
            }
        }

        private static void SetF(object obj, string name, object value) {
            try {
                FieldInfo fieldInfo = AccessTools.Field(obj.GetType(), name);
                if (fieldInfo != null) {
                    fieldInfo.SetValue(obj, value);
                }
            } catch { }
        }

        //MatchmakingNumber used to filter the lobby list: identical for all
        //mod-lobby players, so the server shows only mod rooms.
        internal static string ModMatchmakingNumber() {
            if (_ogVersion == "1.0.0.0") {
                try {
                    GameSettings gs = GameSettings.GetInstance();
                    if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null) {
                        string text = (string)GetF(gs, "versionNumber");
                        if (!string.IsNullOrEmpty(text)) {
                            _ogVersion = text;
                        }
                    }
                } catch { }
            }
            string[] array2 = _ogVersion.Split('.', StringSplitOptions.None);
            return ModVersionFull + "_" + array2[0] + "." + array2[1];
        }
    }
}
