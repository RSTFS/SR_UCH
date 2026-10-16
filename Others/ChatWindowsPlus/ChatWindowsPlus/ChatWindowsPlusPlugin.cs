using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ChatWindowsPlus {

    //=====================================================================
    // Chat Windows Plus - standalone in-game chat window tweaks (English only).
    //
    // This DLL draws NO UI of its own and adds NO hotkeys of its own: the
    // settings live in BepInEx\config\com.gamingbeast.ChatWindowsPlus.cfg and
    // are edited with BepInEx Configuration Manager (F1 -> Chat Windows Plus).
    //
    // The config entry shapes are chosen for that editor on purpose:
    //   * two-state options are plain bools -> checkboxes (no dropdown);
    //   * Box Scale / Font Size / Position X / Y are numeric entries WITHOUT
    //     AcceptableValueRange -> text edit boxes (not sliders); the valid range
    //     is written into each description and clamped in code;
    //   * Show Key / Scale Key are KeyCode entries -> key bind fields.
    //
    // Features
    //   Show window      : hold the key (default Z) to show the chat window,
    //                      or tap it to pin the window on/off ("Pin Window");
    //   Suppress auto show / No fade in-out;
    //   Chat box scale   : overall scale of the message holder (0.60 - 1.60),
    //                      Scale Key (default X) + wheel;
    //   Chat font size   : 1 - 50, default 18 (= the game's own size), Font Key (default C) + wheel.
    //                      Only message text is resized: the game's chat input field and the portraits stay as-is;
    //   Chat font size   : 0 = vanilla, 1 - 48 (writes the game's own
    //                      GameSettings.ChatMessageFontSize so new messages follow);
    //   Crisp text       : chat canvas pixel-perfect;
    //   Chat box position: enable switch + X / Y pixel offset.
    //
    // Turning the master switch back off restores the
    // game's own font size / scale / pixel-perfect / position once.
    //=====================================================================
    [BepInPlugin(Guid, "Chat Windows Plus", "1.0.0")]
    public class ChatWindowsPlusPlugin : BaseUnityPlugin {

        public const string Guid = "com.gamingbeast.ChatWindowsPlus";
        private const string Sec = "Chat Window";

        public static ManualLogSource Log;

        // ---- config ----
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _noAutoShow;
        private ConfigEntry<bool> _noFade;
        private ConfigEntry<KeyCode> _showKey;
        private ConfigEntry<bool> _pinWindow;      // hold/pin mode: a checkbox instead of a dropdown
        private ConfigEntry<float> _boxScale;      // no range -> edit box in Configuration Manager
        private ConfigEntry<KeyCode> _scaleKey;
        private ConfigEntry<KeyCode> _fontKey;     // own key for the font size (default C)
        private ConfigEntry<int> _fontSize;        // no range -> edit box in Configuration Manager
        private ConfigEntry<bool> _crisp;
        private ConfigEntry<bool> _posEnabled;
        private ConfigEntry<float> _posX;
        private ConfigEntry<float> _posY;

        // ---- chat state ----
        private static bool _toggleOn;      // pin mode: window pinned on?
        private static bool _wasShowing;    // were we the ones holding the window visible? (drop our timer bump once)
        private static int _origFontSize;
        private static bool _origBestFit;
        private static int _origGameFontSize = -1;
        private static int _fontHolderId;
        private static float _lastClarity = -1f;
        private static Vector2 _posBase, _posLast;
        private static bool _posHaveBase, _posApplied;
        private static float _posLastDx, _posLastDy;
        private static bool _touched;          // have we modified anything yet?
        private static bool _havePp, _origPp;  // first-seen canvas.pixelPerfect
        private static FieldInfo _fCanvasGroup, _fHolder, _fChatMode, _fVisTimer;

        // ---- config write coalescing (Configuration Manager edits are flushed once per second) ----
        private bool _cfgDirty;
        private float _cfgSavedAt;

        private static ChatWindowsPlusPlugin _instance;

        private void Awake() {
            _instance = this;
            Log = Logger;
            Config.SaveOnConfigSet = false;

            _enabled = Bind(Sec, "Enabled", true,
                "Master switch for every tweak in this plugin.\nOFF = the chat window runs exactly like vanilla; the game's own font size, scale and position are restored once.", 12);
            _noAutoShow = Bind(Sec, "Suppress Auto Show", false,
                "New messages no longer pop the chat window up.\nThe show key still reveals it, and typing (Enter) still shows it.", 11);
            _noFade = Bind(Sec, "No Fade", false,
                "The chat window appears/disappears instantly instead of the vanilla fade.\nIndependent from 'Suppress Auto Show'.", 10);
            _showKey = Bind(Sec, "Show Key", KeyCode.Z,
                "Key that shows the chat window (default Z).\nOFF 'Pin Window' = visible while the key is held. ON 'Pin Window' = tap the key to pin the window on/off.", 9);
            _pinWindow = Bind(Sec, "Pin Window", false,
                "OFF (default) = Hold mode: the chat window is visible only while the show key is held.\nON = Pin mode: tapping the show key pins the window on; tap again to hand it back to the game.", 8);
            _boxScale = Bind(Sec, "Box Scale", 1f,
                "Overall chat box scale. Valid 0.50 - 2.00, 1 = vanilla (values outside are clamped).\nApplied to the message holder's localScale. The scale key + wheel also adjusts it (0.05 per notch).", 7);
            _scaleKey = Bind(Sec, "Scale Key", KeyCode.X,
                "Modifier used with the mouse wheel for the chat box scale (default X).\nHold it + wheel = box scale (0.05 per notch).\n('Font Key' + wheel adjusts the chat font size instead.)", 6);
            _fontKey = Bind(Sec, "Font Key", KeyCode.C,
                "Own modifier for the chat font size (default C).\nHold it + wheel = chat font size, 1 per notch.", 4);
            _fontSize = Bind(Sec, "Font Size", 18,
                "Chat font size, 1 - 50, default 18 (= the game's own size).\nOnly the message texts are resized (text, colon, player name); the portrait is not touched by us (new messages get theirs from the game's own rule).\nAdjust it live with 'Font Key' (default C) + wheel.", 5);
            _crisp = Bind(Sec, "Crisp Text", true,
                "Sets the chat canvas to pixel-perfect (sharper text) and rebuilds the texts when it changes.\nThis game's chat canvas has no CanvasScaler, so there is no scaling factor to tune.", 3);
            _posEnabled = Bind(Sec, "Enable Position", false,
                "Master switch for the position offsets. OFF = the offset we applied is removed once and the game fully controls the position again.", 2);
            _posX = Bind(Sec, "Position X", 0f,
                "Horizontal offset in pixels, 0 = the game's default position, positive moves right.\nValid 0 - current screen width (values outside are clamped). Only applies while 'Enable Position' is on.", 1);
            _posY = Bind(Sec, "Position Y", 0f,
                "Vertical offset in pixels, 0 = the game's default position, negative moves down.\nValid -current screen height - 0 (values outside are clamped). Only applies while 'Enable Position' is on.", 0);

            try { Harmony.CreateAndPatchAll(typeof(ChatWindowsPlusPlugin)); }
            catch (Exception e) { Logger.LogError("Harmony patch failed: " + e.Message); }

            // Two mods fighting over the same chat window looks like flicker: warn about it once.
            try {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.gamingbeast.SR_UCH"))
                    Logger.LogWarning("SR_UCH is also installed and it has its own 'Chat Window' page. "
                        + "Keep only one of them enabled, otherwise both write the chat scale/font every frame.");
            } catch { }

            Logger.LogInfo("Chat Windows Plus loaded (settings: BepInEx Configuration Manager). Show key: "
                + _showKey.Value + " | scale key: " + _scaleKey.Value + " | font key: " + _fontKey.Value
                + " | pin mode: " + _pinWindow.Value);
        }

        // Bind + flag the config as dirty on every change (saves are coalesced in Update)
        // order = 该条目在界面里的显示顺序；Configuration Manager 按这个 Order 排序（否则按 key 字母序）。
        // ConfigurationManagerAttributes 是**按类型名**识别的，所以本地定义一份同名类即可，无需引用它。
        private ConfigEntry<T> Bind<T>(string section, string key, T value, string desc, int order) {
            ConfigEntry<T> e = Config.Bind(section, key, value,
                new ConfigDescription(desc, null, new object[] { new ConfigurationManagerAttributes { Order = order } }));
            try { e.SettingChanged += (s, a) => { _cfgDirty = true; }; } catch { }
            return e;
        }

        private void Update() {
            // flush config writes at most once per second (Configuration Manager writes on every edit)
            if (_cfgDirty && Time.unscaledTime - _cfgSavedAt > 1f) {
                _cfgDirty = false;
                _cfgSavedAt = Time.unscaledTime;
                try { Config.Save(); } catch (Exception e) { Logger.LogWarning("Config save failed: " + e.Message); }
            }
        }

        //-----------------------------------------------------------------
        // Chat window behaviour (Harmony postfix on the game's own update)
        //-----------------------------------------------------------------
        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPostfix]
        static void AfterChatUpdate(ChatDisplay __instance) {
            try {
                if (_instance == null || __instance == null) return;
                CanvasGroup cg = GroupOf(__instance);
                RectTransform holder = HolderOf(__instance);
                if (cg == null) return;
                // Master switch off: put the game's own values back once, then leave everything alone
                // (so "off = vanilla" is actually true and not just "we stop writing").
                if (_instance._enabled == null || !_instance._enabled.Value) {
                    if (_touched) RestoreVanilla(holder, cg);
                    return;
                }
                _touched = true;

                // 1) show / hide
                ConfigEntry<KeyCode> showKey = _instance._showKey;
                bool force;
                if (_instance._pinWindow != null && _instance._pinWindow.Value) {
                    if (KeyDown(showKey)) _toggleOn = !_toggleOn;
                    force = _toggleOn;
                } else {
                    force = KeyHeld(showKey);
                }
                bool typing = ChatInputActive(__instance);
                bool wantShow = force || typing;
                if (_instance._noFade != null && _instance._noFade.Value) {
                    // 'No Fade' ON: write alpha directly (instant show/hide)
                    cg.alpha = wantShow ? 1f : 0f;
                    cg.interactable = wantShow; cg.blocksRaycasts = wantShow;
                } else if (wantShow) {
                    // 'No Fade' OFF: fade in at the game's own speed and keep pushing alpha to 1.
                    // Relying on the game's VisibilityTimer alone is not enough: the game only fades toward 1 while
                    // it has messages (or the input field is open), so holding the show key with an empty window
                    // did nothing. We still top the timer up so the game agrees the window should be visible.
                    if (!cg.interactable) { cg.interactable = true; cg.blocksRaycasts = true; }
                    BumpVisibility(__instance);
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 1f, FadeSpeed() * 10f * Time.unscaledDeltaTime);
                    _wasShowing = true;
                } else if (_instance._noAutoShow != null && _instance._noAutoShow.Value) {
                    // 'Suppress Auto Show' ON and the window should not be shown: fade out at the game's own speed
                    // and zero the timer so new messages cannot pop it back up.
                    if (cg.interactable) { cg.interactable = false; cg.blocksRaycasts = false; }
                    ZeroVisibility(__instance);
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 0f, FadeSpeed() * Time.unscaledDeltaTime);
                    _wasShowing = false;
                } else {
                    // Leave it to the game; if we were the ones holding it visible, drop our timer bump once so the
                    // game starts fading out right away instead of lingering for the bumped time.
                    if (_wasShowing) { _wasShowing = false; ZeroVisibility(__instance); }
                }

                // 2) mouse wheel: Font Key + wheel = font size, Scale Key + wheel = box scale
                //    (the held modifier key is the gate, so this works even while the window is faded out)
                float wheel = 0f;
                try { wheel = Input.GetAxis("Mouse ScrollWheel"); } catch { wheel = 0f; }
                if (Mathf.Abs(wheel) > 0.0001f) {
                    bool fontKeyHeld = KeyHeld(_instance._fontKey);
                    bool scaleKeyHeld = KeyHeld(_instance._scaleKey);
                    if (fontKeyHeld) {
                        int nv = Mathf.Clamp(FontSize() + (wheel > 0f ? 1 : -1), 1, 50);
                        if (_instance._fontSize != null && _instance._fontSize.Value != nv) _instance._fontSize.Value = nv;
                    } else if (scaleKeyHeld) {
                        float nv = Mathf.Clamp(BoxScale() + (wheel > 0f ? 0.05f : -0.05f), 0.5f, 2f);
                        if (_instance._boxScale != null && Mathf.Abs(_instance._boxScale.Value - nv) > 0.0001f) _instance._boxScale.Value = nv;
                    }
                }

                // 3) box scale
                float scale = BoxScale();
                if (holder != null && Mathf.Abs(holder.localScale.x - scale) > 0.0001f) holder.localScale = new Vector3(scale, scale, 1f);

                // 4) font size / crisp text / position
                if (holder != null) { ApplyFont(holder); ApplyClarity(holder); }
                ApplyPosition(cg);
            } catch (Exception e) { Warn("chat window update", e); }
        }

        // The numeric entries carry no AcceptableValueRange (so Configuration Manager shows edit boxes), hence the clamps.
        private static float BoxScale() {
            if (_instance == null || _instance._boxScale == null) return 1f;
            return Mathf.Clamp(_instance._boxScale.Value, 0.5f, 2f);
        }

        // Game's own fade speed (GameSettings.ChatMessagingFadeSpeed, 0.8/s by default; fade-in is 10x that)
        private static float FadeSpeed() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && gs.ChatMessagingFadeSpeed > 0.001f) return gs.ChatMessagingFadeSpeed;
            } catch { }
            return 0.8f;
        }

        private static int FontSize() {
            if (_instance == null || _instance._fontSize == null) return 18;
            return Mathf.Clamp(_instance._fontSize.Value, 1, 50);
        }

        // Font size. Writes the game's own GameSettings.ChatMessageFontSize: the game then sizes the portrait of new
        // messages from it (ChatUnit.SetChatUnitMessage -> fontSize * 2). On top of that the message texts are resized
        // every frame (which also covers newly created messages): chatText, colonText and the player name.
        // Existing portraits are never touched by us.
        private static void ApplyFont(RectTransform holder) {
            try {
                int want = FontSize();
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null) {
                    if (_origGameFontSize < 0) _origGameFontSize = gs.ChatMessageFontSize;
                    int target = want > 0 ? want : _origGameFontSize;
                    if (target > 0 && gs.ChatMessageFontSize != target) gs.ChatMessageFontSize = target;
                }
                int id = holder.GetInstanceID();
                if (id != _fontHolderId) { _fontHolderId = id; _origFontSize = 0; _origBestFit = false; }
                ChatUnit[] units = holder.GetComponentsInChildren<ChatUnit>(true);
                for (int i = 0; i < units.Length; i++) {
                    ChatUnit u = units[i];
                    if (u == null) continue;
                    ApplyTextSize(u.chatText, want);
                    ApplyTextSize(u.colonText, want);
                    if (u.nameTag != null) ApplyTextSize(u.nameTag.usernameText, want); // the player name follows too
                }
            } catch (Exception e) { Warn("chat font size", e); }
        }

        // One message text: want > 0 uses the configured size (best-fit off), want = 0 restores the game's own values
        private static void ApplyTextSize(Text t, int want) {
            try {
                if (t == null) return;
                if (_origFontSize <= 0 && t.fontSize > 0) { _origFontSize = t.fontSize; _origBestFit = t.resizeTextForBestFit; }
                if (want > 0) {
                    if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                    if (t.fontSize != want) t.fontSize = want;
                } else if (_origFontSize > 0) {
                    if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                    if (t.fontSize != _origFontSize) t.fontSize = _origFontSize;
                }
            } catch (Exception e) { Warn("chat text size", e); }
        }

        // Crisp text: canvas pixel-perfect (this canvas has no CanvasScaler) + rebuild texts on change
        private static void ApplyClarity(RectTransform holder) {
            try {
                Canvas canvas = holder.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                if (!_havePp) { _origPp = canvas.pixelPerfect; _havePp = true; } // baseline, captured before we touch it
                bool want = _instance._crisp == null || _instance._crisp.Value;
                if (canvas.pixelPerfect != want) canvas.pixelPerfect = want;
                float v = 1f;
                if (Mathf.Abs(_lastClarity - v) > 0.001f) {
                    _lastClarity = v;
                    Text[] texts = holder.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++) { if (texts[i] != null) texts[i].SetAllDirty(); }
                }
            } catch (Exception e) { Warn("chat crisp text", e); }
        }

        // Position: only touched while 'Enable Position' is on; the base is derived from the current
        // position, so the game's own moves are followed instead of fought.
        private static void ApplyPosition(CanvasGroup cg) {
            try {
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node == null) return;
                bool on = _instance._posEnabled != null && _instance._posEnabled.Value;
                float dx = on && _instance._posX != null ? Mathf.Clamp(_instance._posX.Value, 0f, Mathf.Max(800f, Screen.width)) : 0f;
                float dy = on && _instance._posY != null ? Mathf.Clamp(_instance._posY.Value, -Mathf.Max(800f, Screen.height), 0f) : 0f;
                if (!on) {
                    if (_posApplied) { // remove the offset we applied once, then never touch the position again
                        try { node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy); } catch { }
                        _posApplied = false; _posHaveBase = false;
                    }
                    return;
                }
                Vector2 cur = node.anchoredPosition;
                if (!_posHaveBase) { _posBase = cur; _posHaveBase = true; }
                else if ((cur - _posLast).sqrMagnitude > 0.25f) { _posBase = cur - new Vector2(_posLastDx, _posLastDy); }
                Vector2 want = _posBase + new Vector2(dx, dy);
                if ((cur - want).sqrMagnitude > 0.0001f) node.anchoredPosition = want;
                _posLast = node.anchoredPosition;
                _posLastDx = dx; _posLastDy = dy;
                _posApplied = true;
            } catch (Exception e) { Warn("chat position", e); }
        }

        // One-shot restore used when the master switch is turned off: game font size, text sizes,
        // chat box scale, canvas pixel-perfect and our position offset all go back to the game's values.
        private static void RestoreVanilla(RectTransform holder, CanvasGroup cg) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && _origGameFontSize > 0 && gs.ChatMessageFontSize != _origGameFontSize)
                    gs.ChatMessageFontSize = _origGameFontSize;
                if (holder != null) {
                    if (Mathf.Abs(holder.localScale.x - 1f) > 0.0001f) holder.localScale = Vector3.one;
                    if (_origFontSize > 0) {
                        // Same as ApplyFont: only the message texts are restored, the game's input field is left alone
                        ChatUnit[] units = holder.GetComponentsInChildren<ChatUnit>(true);
                        for (int i = 0; i < units.Length; i++) {
                            ChatUnit u = units[i];
                            if (u == null) continue;
                            ApplyTextSize(u.chatText, 12);
                            ApplyTextSize(u.colonText, 12);
                        }
                    }
                    Canvas canvas = holder.GetComponentInParent<Canvas>();
                    if (canvas != null && _havePp && canvas.pixelPerfect != _origPp) canvas.pixelPerfect = _origPp;
                }
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node != null && _posApplied)
                    node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy);
                _lastClarity = -1f; // next enable rebuilds once
            } catch (Exception e) { Warn("restore vanilla", e); }
            _posApplied = false; _posHaveBase = false;
            _touched = false;
        }

        // ---- game internals (private members, cached reflection) ----
        private static CanvasGroup GroupOf(ChatDisplay d) {
            try {
                if (_fCanvasGroup == null) _fCanvasGroup = typeof(ChatDisplay).GetField("ChatCanvasGroup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _fCanvasGroup != null ? _fCanvasGroup.GetValue(d) as CanvasGroup : null;
            } catch { return null; }
        }

        private static RectTransform HolderOf(ChatDisplay d) {
            try {
                if (_fHolder == null) _fHolder = typeof(ChatDisplay).GetField("ChatHolder", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _fHolder != null ? _fHolder.GetValue(d) as RectTransform : null;
            } catch { return null; }
        }

        private static bool ChatInputActive(ChatDisplay d) {
            try {
                if (_fChatMode == null) _fChatMode = typeof(ChatDisplay).GetField("ChatMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fChatMode == null) return false;
                object v = _fChatMode.GetValue(d);
                return v is bool && (bool)v;
            } catch { return false; }
        }

        // The game fades the chat window toward alpha = 1 while ChatDisplay.VisibilityTimer > 0
        // (speed = GameSettings.ChatMessagingFadeSpeed * 10) and toward 0 otherwise; the timer also ticks down
        // every frame. Topping it up while we want the window shown therefore gives us the game's own
        // fade-in / fade-out animation without ever writing alpha ourselves.
        private static void BumpVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = typeof(ChatDisplay).GetField("VisibilityTimer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fVisTimer == null) return;
                object o = _fVisTimer.GetValue(d);
                float cur = o is float ? (float)o : 0f;
                if (cur < 0.15f) _fVisTimer.SetValue(d, 0.15f);
            } catch (Exception e) { Warn("chat visibility timer", e); }
        }

        // Let the game fade the window out at its own speed (instead of us cutting alpha to 0)
        private static void ZeroVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = typeof(ChatDisplay).GetField("VisibilityTimer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fVisTimer == null) return;
                _fVisTimer.SetValue(d, 0f);
            } catch (Exception e) { Warn("chat visibility timer", e); }
        }

        private static bool KeyHeld(ConfigEntry<KeyCode> e) {
            if (e == null || e.Value == KeyCode.None) return false;
            try { return Input.GetKey(e.Value); } catch { return false; }
        }

        private static bool KeyDown(ConfigEntry<KeyCode> e) {
            if (e == null || e.Value == KeyCode.None) return false;
            try { return Input.GetKeyDown(e.Value); } catch { return false; }
        }

        private static void Warn(string what, Exception e) {
            if (Log != null) Log.LogWarning("[" + what + "] " + e.Message);
        }
    }

    // Configuration Manager 按类型名 "ConfigurationManagerAttributes" 反射读取 Order —— 本地定义即可，
    // 不需要引用 ConfigurationManager.dll（类名与字段名必须和它的约定一致）。
    public class ConfigurationManagerAttributes {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public string Category;
        public bool? ShowRangeAsPercent;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? ReadOnly;
        public string DispName;
        public string Description;
    }
}
