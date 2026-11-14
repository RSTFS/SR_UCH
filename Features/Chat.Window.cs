// 会话 · 对话窗口页：聊天窗口显示方式、关闭自动弹出、淡入淡出、缩放/字号/清晰度/位置。

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class Chat : ITweak {

        public enum WinMode { Hold, Toggle } // 按住显示 / 切换常驻（都不按 = 游戏原版行为）

        private const string Sec = "Chat Window";

        private static ConfigEntry<bool> _enabled;      // 会话窗口总开关
        private static ConfigEntry<bool> _noAutoShow;
        private static ConfigEntry<bool> _noFade;
        private static ConfigEntry<KeyCode> _key;          // 显示窗口
        private static ConfigEntry<WinMode> _mode;         // 合并到“显示窗口”行
        private static ConfigEntry<float> _boxScale;       // 聊天框缩放
        private static ConfigEntry<KeyCode> _boxScaleKey;  // 合并到“聊天框缩放”行
        private static ConfigEntry<KeyCode> _fontKey;      // 字号键：按住 + 滚轮 = 调字号
        private static ConfigEntry<int> _fontSize;         // 聊天文字大小
        private static ConfigEntry<bool> _crisp;           // 合并到“清晰度系数”行
        private static ConfigEntry<bool> _posEnabled;      // 聊天框位置总开关
        private static ConfigEntry<float> _posX;
        private static ConfigEntry<float> _posY;
        private static ConfigEntry<bool> _keyDefaultsV2;   // 内部：快捷键默认值迁移标记（不出现在页面上）
        private static ConfigEntry<bool> _defaultsV3;      // 内部：字号默认值 0->18 迁移标记（不出现在页面上）

        private static bool _toggleOn;         // Toggle 模式下的常驻状态
        private static bool _wasShowing;       // 上一帧是否由本模组在“按住显示”（松开后需清一次续期的计时器）
        private static bool _diagDone;

        private static int _origFontSize;
        private static bool _origBestFit;
        private static int _fontHolderId;
        private static float _lastClarity = -1f;

        private static int _origGameFontSize = -1;
        private static bool _touched;          // 本轮是否改过游戏对象/设置（总开关关掉时还原一次）
        private static bool _havePp, _origPp;  // 首次看到的 canvas.pixelPerfect 原值

        private static Vector2 _posBase, _posLast;
        private static bool _posHaveBase, _posApplied;
        private static float _posLastDx, _posLastDy;

        private static FieldInfo _fVisTimer;   // ChatDisplay.VisibilityTimer（游戏淡入淡出用）
        // 位置偏移与缩放都在总开关关闭时还原，故须记录游戏原始值。

        public static int WheelFrame { get { return SRApi.WheelFrame; } set { SRApi.WheelFrame = value; } }

        internal static void InitWindow(IFeatureHost plugin) {
            SelfRegWindow(plugin);
            SRApi.RegisterTypingProvider(() => ChatTyping);
        }

        private static void SelfRegWindow(IFeatureHost plugin) {
            SRApi.LocKey(Sec, "Enabled", "会话窗口开关", null);
            SRApi.LocDesc(Sec, "Enabled", "会话窗口开关\n总的开关：关掉后本页所有聊天框改动都不生效\n聊天本身仍在游戏里正常显示", "Chat window switch\nmaster switch: when off none of this page's chat box changes apply\nchatting itself keeps working in the game");
            SRApi.Ref.Override("ChatDisplay.VisibilityTimer", "聊天窗口（可见计时器：保持显示但不自己写 alpha，淡入淡出交给游戏）");

            // 本页是会话栏目第 1 个子页，配置段仍为 Chat Window（不单独成侧栏栏目）。
            SRApi.NavHide(Sec); // 本页不单独成侧栏栏目
            SRApi.RegisterPage("Chat", "ChatWindow", "对话窗口", "ChatWindows", 10, RenderPage);
            SRApi.LocEnum("Hold", "按住显示");
            SRApi.LocEnum("Toggle", "切换常驻");
            SRApi.RowFilters.Add(RowVisibleFilter);
            SRApi.RowCompact.Add(delegate(ConfigEntryBase e) {
                return e != null && (e.Definition.Section == Sec || e.Definition.Section == "Chat");
            });
            SRApi.RowExtras.Add(RowExtraFor);
            SRApi.RowSliders.Add(BoxScaleSlider);
            SRApi.RowSliders.Add(FontSizeSlider);
            SRApi.RowSliders.Add(PosSlider); // 位置 X/Y：滑块范围按当前分辨率给
        // 位置 X/Y 用滑块渲染：范围取当前分辨率，0 = 游戏默认位置。

        // 不能登记 Remove(Sec, "Enabled")：本页仍 Bind 这个键，登了会每次启动删一次、永远存不下来。
            SRApi.LocKey(Sec, "Enabled", "启用增强", "Enable tweaks");
            SRApi.LocDesc(Sec, "Enabled", "聊天窗口增强总开关\n开启后本页各项修改才生效\n关闭即回到游戏原版行为", "Chat window master switch\nthe options here apply only while on\noff returns to vanilla behaviour");
            SRApi.LocKey(Sec, "No Auto Show", "关闭自动弹出", "Suppress auto show");
            SRApi.LocDesc(Sec, "No Auto Show", "新消息自动弹出（取反）\n开启后新消息不会自动弹出窗口\n按住显示键或按 Enter 仍可见", "Auto-popup (inverted)\nwhen on, new messages do not pop the window\nthe show key and Enter still reveal it");
            SRApi.LocKey(Sec, "No Fade", "关闭淡入淡出动画", "No fade in/out");
            SRApi.LocDesc(Sec, "No Fade", "淡入淡出（取反）\n开启后窗口出现与消失瞬切\n关闭时沿用游戏淡入淡出速度", "Fade (inverted)\nwhen on, the window appears and disappears instantly\noff keeps the game's fade speed");
            SRApi.LocKey(Sec, "Window Key", "显示窗口键", "Show window key");
            SRApi.LocDesc(Sec, "Window Key", "聊天窗口显示键\n按下显示窗口，方式由下一项决定\n点键框后按新键改绑，Esc 取消", "Chat window show key\npress to show the window; behaviour set by the next option\nclick the key box and press a key; Esc cancels");
            SRApi.LocKey(Sec, "Window Mode", "显示方式", "Show mode");
            SRApi.LocDesc(Sec, "Window Mode", "显示键的工作方式\n按住＝按住期间显示；切换＝按一下固定或还原\n需配合显示键", "Show key behaviour\nhold = visible while held; toggle = tap to pin or release\nworks with the show key");
            SRApi.LocKey(Sec, "Box Scale", "聊天框缩放", "Chat box scale");
            SRApi.LocDesc(Sec, "Box Scale", "聊天框缩放\n按比例缩放消息区（文字、玩家名、头像）\n范围 0.5-2；按住缩放键滚轮调整", "Chat box scale\nscales the message area including names and portraits\nrange 0.5-2; hold the scale key and scroll");
            SRApi.LocKey(Sec, "Box Scale Key", "缩放键", "Scale key");
            SRApi.LocDesc(Sec, "Box Scale Key", "缩放调节键\n按住它滚轮微调聊天框缩放\n每格 0.05；字号用字号调节键", "Scale modifier key\nhold and scroll to fine-tune the chat box scale\n0.05 per notch; use the font key for text size");
            SRApi.LocKey(Sec, "Font Size", "聊天文字大小", "Chat font size");
            SRApi.LocDesc(Sec, "Font Size", "聊天文字字号\n设置消息、玩家名与头像的文字尺寸\n范围 1-50；不影响输入框；按住字号键滚轮调整", "Chat text size\nsize of messages, player names and portraits\nrange 1-50; input field unaffected; hold the font key and scroll");
            SRApi.LocKey(Sec, "Font Key", "字号键", "Font key");
            SRApi.LocDesc(Sec, "Font Key", "字号调节键\n按住它滚轮调整文字尺寸\n每格 1", "Font modifier key\nhold and scroll to change the text size\n1 per notch");
            SRApi.LocKey(Sec, "Crisp Text", "启用清晰度", "Crisp text");
            SRApi.LocDesc(Sec, "Crisp Text", "文字清晰度\n画布像素对齐，减少模糊\n切换时消息文字重建一次", "Text crispness\npixel-perfect canvas to reduce blur\nmessage texts are rebuilt once when toggled");
            SRApi.LocKey(Sec, "Pos Enabled", "启用位置调整", "Enable position");
            SRApi.LocDesc(Sec, "Pos Enabled", "位置调整\n是否应用下面的 X / Y 偏移\n关闭时撤销已加偏移，位置交还游戏", "Position tweaks\napplies the X and Y offsets below\nwhen off, the offset is removed and the game owns the position");
            SRApi.LocKey(Sec, "Pos X", "位置 X", "Position X");
            SRApi.LocDesc(Sec, "Pos X", "水平偏移（像素）\n正值把聊天框向右移\n仅在位置调整开启时生效", "Horizontal offset (px)\npositive values move the window right\nonly applies while position tweaks are on");
            SRApi.LocKey(Sec, "Pos Y", "位置 Y", "Position Y");
            SRApi.LocDesc(Sec, "Pos Y", "垂直偏移（像素）\n负值把聊天框向下移\n仅在位置调整开启时生效", "Vertical offset (px)\nnegative values move the window down\nonly applies while position tweaks are on");

            _enabled = plugin.Config.Bind(Sec, "Enabled", false, "会话窗口总开关：关掉后本页的聊天框改动全部不生效。");
            _noAutoShow = plugin.Config.Bind(Sec, "No Auto Show", false, "关闭自动弹出：新消息不再自动显示聊天窗口。");
            _noFade = plugin.Config.Bind(Sec, "No Fade", false, "关闭淡入淡出动画：出现/消失都瞬切。");
            _key = plugin.Config.Bind(Sec, "Window Key", KeyCode.Z, "显示聊天窗口的按键（默认 Z）");
            _boxScale = plugin.Config.Bind(Sec, "Box Scale", 1f, new ConfigDescription(
                "聊天框整体缩放（0.5 - 2，1 = 游戏原版）。", new AcceptableValueRange<float>(0.5f, 2f)));
            _fontSize = plugin.Config.Bind(Sec, "Font Size", 18, new ConfigDescription(
                "聊天文字字号（1 - 50）。游戏自带的默认是 20，模组默认 18；填什么值我们就按什么值改，"
                + "现存消息也会一起改。", new AcceptableValueRange<int>(1, 50)));
            // 字号键是聊天文字大小行的同伴控件（同排 = 标签 + 滑块 + 按键），默认 C：按住 + 滚轮调字号。
            _crisp = plugin.Config.Bind(Sec, "Crisp Text", false, "文字清晰度：pixelPerfect。");
            _fontKey = plugin.Config.Bind(Sec, "Font Key", KeyCode.C, "按住 + 滚轮 = 调字号（默认 C）");
            _posEnabled = plugin.Config.Bind(Sec, "Pos Enabled", false, "位置调整总开关。");
            _posX = plugin.Config.Bind(Sec, "Pos X", 0f, new ConfigDescription(
                "聊天框横向偏移（像素；0 = 游戏默认位置，往右为正）。", new AcceptableValueRange<float>(0f, 8000f)));
            _posY = plugin.Config.Bind(Sec, "Pos Y", 0f, new ConfigDescription(
                "聊天框纵向偏移（像素；0 = 游戏默认位置，往下为负）。", new AcceptableValueRange<float>(-8000f, 0f)));
            _mode = plugin.Config.Bind(Sec, "Window Mode", WinMode.Hold, "按住显示 / 切换常驻。");
            _boxScaleKey = plugin.Config.Bind(Sec, "Box Scale Key", KeyCode.X, "按住 + 滚轮 = 调缩放（默认 X）");
            // 一次性迁移：只改仍是旧默认值的配置，仅生效一次。
            _keyDefaultsV2 = plugin.Config.Bind(Sec, "Key Defaults v2", false, "内部：快捷键默认值已迁移（显示窗口 Z / 缩放键 X）");
            if (!_keyDefaultsV2.Value) {
                try {
                    if (_key.Value == KeyCode.LeftAlt || _key.Value == KeyCode.RightAlt) _key.Value = KeyCode.Z;
                    if (_boxScaleKey.Value == KeyCode.LeftControl || _boxScaleKey.Value == KeyCode.RightControl) _boxScaleKey.Value = KeyCode.X;
                    _keyDefaultsV2.Value = true;
                } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口快捷键默认值迁移", __ex); }
            }
            _defaultsV3 = plugin.Config.Bind(Sec, "Defaults v3", false, "内部：字号默认值已迁移到 18");
            if (!_defaultsV3.Value) {
                try {
                    if (_fontSize.Value < 1) _fontSize.Value = 18;
                    _defaultsV3.Value = true;
                } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口字号默认值迁移", __ex); }
            }

            SRApi.RegisterKey("聊天窗口-显示键", _key, "hold");
            SRApi.RegisterKey("聊天窗口-缩放键", _boxScaleKey, "hold");
            SRApi.RegisterKey("聊天窗口-字号键", _fontKey, "hold");
        }

        // 分区标题插在“启用位置调整”行前；锚点行必须可见，否则标题永不显示。
        private static void RenderPage() {
            float col = Mathf.Max(SRApi.Ctl.Sc(180), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SRApi.Ctl.SectionEntries(Sec);
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (e.Definition.Key == "Pos Enabled") {
                    GUILayout.Space(SRApi.Ctl.Sc(6));
                    SRApi.Ctl.SectionTitle(SRApi.T("- 聊天框位置 -", "- Chat box position -"));
                }
                SRApi.Ctl.RenderEntryRow(e, true, col);
            }
        }

        private static bool RowVisibleFilter(ConfigEntryBase e) {
            if (e.Definition.Section != Sec) return true;
            string k = e.Definition.Key;
            return k != "Window Mode" && k != "Box Scale Key" && k != "Font Key"
                && k != "Key Defaults v2" && k != "Defaults v3";
        }

        private static string RowExtraFor(ConfigEntryBase e) {
            if (e.Definition.Section != Sec) return null;
            switch (e.Definition.Key) {
                case "Window Key": return "Window Mode";
                case "Box Scale": return "Box Scale Key";
                case "Font Size": return "Font Key"; //聊天文字大小 + 滑块条 + 按键
                default: return null;
            }
        }

        private static bool BoxScaleSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0.5f; max = 2f; fmt = "0.00";
            return e.Definition.Section == Sec && e.Definition.Key == "Box Scale";
        }

        private static bool FontSizeSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 1f; max = 50f; fmt = "0";
            return e.Definition.Section == Sec && e.Definition.Key == "Font Size";
        }

        private static bool PosSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            if (e.Definition.Section != Sec) return false;
            string k = e.Definition.Key;
            if (k == "Pos X") { min = 0f; max = Mathf.Max(800f, Screen.width); return true; }
            if (k == "Pos Y") { min = -Mathf.Max(800f, Screen.height); max = 0f; return true; }
            return false;
        }

        private static CanvasGroup GroupOf(ChatDisplay d) {
            try { return d != null ? d.ChatCanvasGroup : null; } catch { return null; }
        }

        private static RectTransform HolderOf(ChatDisplay d) {
            try { return d != null ? d.ChatHolder : null; } catch { return null; }
        }

        private static bool ChatInputActive(ChatDisplay d) {
            try {
                if (d == null || !d.ChatMode) return false;
                ChatInputField f = d.currentChatInputField;
                bool really = f != null && f.gameObject != null && f.gameObject.activeSelf
                    && f.inputField != null && f.inputField.isFocused;
                if (!really) {
                    if (Time.unscaledTime - _staleTypingAt > 5f) {
                        _staleTypingAt = Time.unscaledTime;
                        SRApi.LogWarn("[会话] ChatMode 残留但输入框未聚焦，已按未打字处理（快捷键恢复）");
                    }
                    return false;
                }
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口/打字判定", __ex); return false; }
        }
        private static float _staleTypingAt = -999f;

        // 淡入淡出速度取游戏自己的 GameSettings.ChatMessagingFadeSpeed（默认 0.8），淡入为其 10 倍。
        private static float FadeSpeed() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && gs.ChatMessagingFadeSpeed > 0.001f) return gs.ChatMessagingFadeSpeed;
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口淡出速度", __ex); }
            return 0.8f;
        }

        // 游戏聊天输入框是否正在输入（ChatDisplay.ChatMode）：打字期间屏蔽所有快捷键，结果按帧缓存。
        // 可见计时器是游戏淡入淡出的开关：只续期/清零，不直接写 alpha。
        private static ChatDisplay _typingProbe;
        private static bool _chatTypingCache;
        private static int _chatTypingFrame = -1;
        public static bool ChatTyping {
            get {
                try {
                    if (_chatTypingFrame == Time.frameCount) return _chatTypingCache;
                    _chatTypingFrame = Time.frameCount;
                    if (_typingProbe == null) _typingProbe = UnityEngine.Object.FindObjectOfType<ChatDisplay>();
                    _chatTypingCache = _typingProbe != null && ChatInputActive(_typingProbe);
                    return _chatTypingCache;
                } catch { return false; }
            }
        }

        // visibility 计时器只抬到很小的正数，淡入淡出交给游戏动画；清零则让游戏自己按速度淡出。
        private static void BumpVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SRApi.Ref.Field(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                object o = _fVisTimer.GetValue(d);
                float cur = o is float ? (float)o : 0f;
                if (cur < 0.15f) _fVisTimer.SetValue(d, 0.15f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器续期", __ex); }
        }

        private static void ZeroVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SRApi.Ref.Field(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                _fVisTimer.SetValue(d, 0f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器清零", __ex); }
        }

        private static bool EnabledOn { get { return SRApi.GateMaster; } }

        internal static bool Enabled { get { try { return _enabled == null || _enabled.Value; } catch { return true; } } }

        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPostfix]
        static void AfterChatUpdate(ChatDisplay __instance) {
        // 总开关关闭时把改过的字号/尺寸/缩放/像素对齐/位置偏移一次性还原，之后不再介入。
                if (!Enabled) return;   // 总开关关闭：完全不改游戏聊天框

            try {
                if (__instance == null) return;
                CanvasGroup cg = GroupOf(__instance);
                RectTransform holder = HolderOf(__instance);
                if (cg == null) return;
                if (!EnabledOn) {
                if (_touched || _posApplied || _origFontSize > 0 || _origGameFontSize > 0) RestoreVanilla(__instance, holder, cg);
                    return;
                }
                _touched = true;

                // 1) 显示/隐藏
                bool force = false;
                WinMode mode = _mode != null ? _mode.Value : WinMode.Hold;
                if (mode == WinMode.Hold) {
                    force = SRApi.ComboKeyHeld(_key);
                } else {
                    if (SRApi.ComboKeyDown(_key)) _toggleOn = !_toggleOn;
                    force = _toggleOn;
                }
                bool typing = ChatInputActive(__instance);
                bool wantShow = force || typing;
                if (_noFade != null && _noFade.Value) {
        // 滚轮只在按住调节键时才取：TakeWheelEvent 取用即清，无条件取会吞掉其它功能的滚轮。
                    // 关闭淡入淡出：直接写 alpha 瞬切。
                    cg.alpha = wantShow ? 1f : 0f;
                    cg.interactable = wantShow; cg.blocksRaycasts = wantShow;
                } else if (wantShow) {
                    // 未关闭淡入淡出：按游戏速度把 alpha 推向 1，并续期可见计时器让游戏也认为该显示。
                    if (!cg.interactable) { cg.interactable = true; cg.blocksRaycasts = true; }
                    BumpVisibility(__instance); // 同时让游戏也认为“该显示”
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 1f, FadeSpeed() * 10f * Time.unscaledDeltaTime);
                    _wasShowing = true;
                } else if (_noAutoShow != null && _noAutoShow.Value) {
                    // 关闭自动弹出且当前不该显示：按游戏速度淡出，并清零计时器使新消息也弹不出。
                    if (cg.interactable) { cg.interactable = false; cg.blocksRaycasts = false; }
                    ZeroVisibility(__instance);
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 0f, FadeSpeed() * Time.unscaledDeltaTime);
                    _wasShowing = false;
                } else {
                    // 其余情况交给游戏；若上一帧由本模组按住显示，清一次计时器让游戏开始淡出。
                    if (_wasShowing) { _wasShowing = false; ZeroVisibility(__instance); }
                }

                // 2) 滚轮：字号键调字号 / 缩放键调缩放；只有按住调节键时才取滚轮，否则会吞掉其它功能的滚轮。
                bool scaleKey = _boxScaleKey != null && _boxScaleKey.Value != KeyCode.None && SRApi.ComboKeyHeld(_boxScaleKey);
                bool fontKey = _fontKey != null && _fontKey.Value != KeyCode.None && SRApi.ComboKeyHeld(_fontKey);
                float wheel = (scaleKey || fontKey) ? SRApi.TakeWheelEvent() : 0f;
                if (Mathf.Abs(wheel) > 0.0001f) {
                    if (fontKey) {
                        int step = wheel > 0f ? 1 : -1;
                        int nv = Mathf.Clamp((_fontSize != null ? _fontSize.Value : 18) + step, 1, 50);
                        if (_fontSize != null && _fontSize.Value != nv) _fontSize.Value = nv;
                        WheelFrame = Time.frameCount;
                    } else if (scaleKey) {
                        float step = wheel > 0f ? 0.05f : -0.05f;
                        float nv = Mathf.Clamp((_boxScale != null ? _boxScale.Value : 1f) + step, 0.5f, 2f);
                        if (_boxScale != null && Mathf.Abs(_boxScale.Value - nv) > 0.0001f) _boxScale.Value = nv;
                        WheelFrame = Time.frameCount;
                    }
                }

                float scale = _boxScale != null ? Mathf.Clamp(_boxScale.Value, 0.5f, 2f) : 1f;
                if (holder != null && Mathf.Abs(holder.localScale.x - scale) > 0.0001f) holder.localScale = new Vector3(scale, scale, 1f);

                if (holder != null) { ApplyFont(__instance, holder); ApplyClarity(holder); }
                ApplyPosition(cg);

                DiagOnce(__instance, holder, cg);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口增强", __ex); }
        }

        // 字号：写入 GameSettings.ChatMessageFontSize；额外逐条改消息文字与玩家名字。
        // 不能遍历 holder 下全部 Text（输入框也带 Text，会看不见字）；消息列表读 currentMessages。
        private static void ApplyFont(ChatDisplay inst, RectTransform holder) {
            int want = _fontSize != null ? _fontSize.Value : 0;
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null) {
                    if (_origGameFontSize < 0) _origGameFontSize = gs.ChatMessageFontSize;
                    int target = want > 0 ? want : _origGameFontSize;
                    if (target > 0 && gs.ChatMessageFontSize != target) gs.ChatMessageFontSize = target;
                }
                int id = holder != null ? holder.GetInstanceID() : 0;
                if (id != _fontHolderId) { _fontHolderId = id; _origFontSize = 0; _origBestFit = false; }
                if (inst == null) return;
                List<ChatUnit> msgs = inst.currentMessages;
                if (msgs == null) return;
                for (int i = 0; i < msgs.Count; i++) ApplyUnitFont(msgs[i], want);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口字号", __ex); }
        }

        private static void ApplyUnitFont(ChatUnit u, int want) {
            if (u == null) return;
            ApplyTextSize(u.chatText, want);
            ApplyTextSize(u.colonText, want);
            if (u.nameTag != null) ApplyTextSize(u.nameTag.usernameText, want); // 名字也跟着字号
            try {
                int px = want > 0 ? want : _origGameFontSize;
                if (px > 0 && u.chatPortrait != null) {
                    Vector2 sz = new Vector2(px * 2f, px * 2f);
                    if ((u.chatPortrait.rectTransform.sizeDelta - sz).sqrMagnitude > 0.01f)
                        u.chatPortrait.rectTransform.sizeDelta = sz;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口头像尺寸", __ex); }
        }

        private static void ApplyTextSize(Text t, int want) {
            try {
                if (t == null) return;
                if (_origFontSize <= 0 && t.fontSize > 0) { _origFontSize = t.fontSize; _origBestFit = t.resizeTextForBestFit; }
                if (want > 0) {
                    if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                    if (t.fontSize != want) t.fontSize = want;
                } else if (_origFontSize > 0) {
                    if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                    int restore = _origGameFontSize > 0 ? _origGameFontSize : _origFontSize;
                    if (t.fontSize != restore) t.fontSize = restore;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口文字尺寸", __ex); }
        }

        private static Canvas _canvasCached;
        private static int _canvasHolderId = -1;
        private static Canvas CanvasOf(RectTransform holder) {
            try {
                int id = holder.GetInstanceID();
                if (id != _canvasHolderId) { _canvasCached = holder.GetComponentInParent<Canvas>(); _canvasHolderId = id; }
                return _canvasCached;
            } catch { return null; }
        }

        private static void ApplyClarity(RectTransform holder) {
            try {
        // 清晰度写 canvas.pixelPerfect；系数变化时让全部文字 SetAllDirty 重建字库。
                Canvas canvas = CanvasOf(holder);
                if (canvas == null) return;
                if (!_havePp) { _origPp = canvas.pixelPerfect; _havePp = true; }
                if (_crisp != null && canvas.pixelPerfect != _crisp.Value) canvas.pixelPerfect = _crisp.Value;
                float v = (_crisp != null && _crisp.Value) ? 1f : 0f;
                if (Mathf.Abs(_lastClarity - v) > 0.001f) {
                    _lastClarity = v;
                    Text[] texts = holder.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++) { if (texts[i] != null) texts[i].SetAllDirty(); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口清晰度", __ex); }
        }

        private static void RestoreVanilla(ChatDisplay inst, RectTransform holder, CanvasGroup cg) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && _origGameFontSize > 0 && gs.ChatMessageFontSize != _origGameFontSize)
                    gs.ChatMessageFontSize = _origGameFontSize;
                if (holder != null) {
                    if (Mathf.Abs(holder.localScale.x - 1f) > 0.0001f) holder.localScale = Vector3.one;
                    if (_origFontSize > 0) {
                        List<ChatUnit> msgs = inst != null ? inst.currentMessages : null;
                        if (msgs != null) for (int i = 0; i < msgs.Count; i++) ApplyUnitFont(msgs[i], 0);
                    }
                    Canvas canvas = holder.GetComponentInParent<Canvas>();
                    if (canvas != null && _havePp && canvas.pixelPerfect != _origPp) canvas.pixelPerfect = _origPp;
                }
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node != null && _posApplied)
                    node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy);
                _lastClarity = -1f; // 下次打开时重建字库
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口还原原版", __ex); }
            _posApplied = false; _posHaveBase = false;
            _origFontSize = 0; _origGameFontSize = 0;
            _touched = false;
        }

        private static void ApplyPosition(CanvasGroup cg) {
            try {
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node == null) return;
                bool on = _posEnabled != null && _posEnabled.Value;
                float dx = on && _posX != null ? _posX.Value : 0f;
                float dy = on && _posY != null ? _posY.Value : 0f;
                if (!on) {
                    if (_posApplied) { // 撤掉最后一次施加的偏移
                        try { node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                        _posApplied = false; _posHaveBase = false;
                    }
                    return;
                }
                Vector2 cur = node.anchoredPosition;
                Vector2 want = cur;
                if (!_posHaveBase) { _posBase = cur; _posHaveBase = true; }
                else if ((cur - _posLast).sqrMagnitude > 0.25f) { _posBase = cur - new Vector2(_posLastDx, _posLastDy); } // 游戏自己移动了，反推基准
                want = _posBase + new Vector2(dx, dy);
                if ((cur - want).sqrMagnitude > 0.0001f) node.anchoredPosition = want;
                _posLast = node.anchoredPosition;
                _posLastDx = dx; _posLastDy = dy;
                _posApplied = true;
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口位置", __ex); }
        }

        // 诊断：打印 currentMessages[0] 的文字与头像尺寸，最多 6 条。
        private static int _lastDiagWant = int.MinValue;
        private static int _diagCount;
        private static void DiagOnce(ChatDisplay inst, RectTransform holder, CanvasGroup cg) {
            if (holder == null) return;
            if (!_diagDone && holder.childCount == 0) return;
            int want = _fontSize != null ? _fontSize.Value : 0;
            if (_diagDone && (want == _lastDiagWant || _diagCount >= 6)) return;
            _diagDone = true; _lastDiagWant = want; _diagCount++;
            try {
                Canvas canvas = holder.GetComponentInParent<Canvas>();
                GameSettings gs = GameSettings.GetInstance();
                List<ChatUnit> msgs = inst != null ? inst.currentMessages : null;
                string first = "（当前无消息）";
                if (msgs != null && msgs.Count > 0 && msgs[0] != null) {
                    ChatUnit u = msgs[0];
                    first = "chatText=" + (u.chatText != null ? u.chatText.fontSize + "(bestFit=" + u.chatText.resizeTextForBestFit + ")" : "?")
                        + " colon=" + (u.colonText != null ? u.colonText.fontSize.ToString() : "?")
                        + " 名字=" + (u.nameTag != null && u.nameTag.usernameText != null ? (u.nameTag.usernameText.fontSize + (u.nameTag.gameObject.activeSelf ? "" : "隐藏")) : "?")
                        + " 头像=" + (u.chatPortrait != null ? u.chatPortrait.rectTransform.sizeDelta.x.ToString() : "?");
                }
                SRApi.LogInfo("[Chat] 字号诊断：配置字号=" + want
                    + " 游戏字号=" + (gs != null ? gs.ChatMessageFontSize.ToString() : "?")
                    + " 消息数=" + (msgs != null ? msgs.Count.ToString() : "?")
                    + " holder缩放=" + holder.localScale.x
                    + " 像素对齐=" + (canvas != null ? canvas.pixelPerfect.ToString() : "?")
                    + " alpha=" + (cg != null ? cg.alpha.ToString() : "?")
                    + " || 首条消息 " + first);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口诊断", __ex); }
        }
}
}
