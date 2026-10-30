// 会话 · 对话窗口页：游戏内聊天窗口的增强 —— 显示方式（按住 / 常驻）、关闭自动弹出、
// 淡入淡出、缩放 / 字号 / 清晰度 / 位置。栏目总览见 Chat.cs。

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

        public enum WinMode { Hold, Toggle } //按住显示 / 切换常驻（都不按 = 游戏原版行为）

        private const string Sec = "Chat Window";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _noAutoShow;
        private static ConfigEntry<bool> _noFade;
        private static ConfigEntry<KeyCode> _key;          //显示窗口
        private static ConfigEntry<WinMode> _mode;         //（合并到 显示窗口 行）
        private static ConfigEntry<float> _boxScale;       //聊天框缩放
        private static ConfigEntry<KeyCode> _boxScaleKey;  //（合并到 聊天框缩放 行）
        private static ConfigEntry<KeyCode> _fontKey;      //字号键（整行显示：按住 + 滚轮 = 调字号）
        private static ConfigEntry<int> _fontSize;         //聊天文字大小
        private static ConfigEntry<bool> _crisp;           //（合并到 清晰度系数 行）
        private static ConfigEntry<bool> _posEnabled;      //聊天框位置总开关
        private static ConfigEntry<float> _posX;
        private static ConfigEntry<float> _posY;
        private static ConfigEntry<bool> _keyDefaultsV2;   //内部：快捷键默认值迁移标记（不出现在页面上）
        private static ConfigEntry<bool> _defaultsV3;      //内部：字号默认值 0->18 迁移标记（不出现在页面上）

        private static bool _toggleOn;         //Toggle 模式下的常驻状态
        private static bool _wasShowing;       //上一帧是否由本模组在"按住显示"（松开后需清一次续期的计时器）
        private static bool _diagDone;

        private static int _origFontSize;
        private static bool _origBestFit;
        private static int _fontHolderId;
        private static float _lastClarity = -1f;

        private static int _origGameFontSize = -1;
        private static bool _touched;          //本轮是否已经改动过游戏对象/游戏设置（用于总开关关掉时还原一次）
        private static bool _havePp, _origPp;  //首次看到的 canvas.pixelPerfect 原值

        //位置偏移（总开关关掉时还原）
        private static Vector2 _posBase, _posLast;
        private static bool _posHaveBase, _posApplied;
        private static float _posLastDx, _posLastDy;

        // 仅 VisibilityTimer 是 protected，走 SRApi.Ref.Field；ChatCanvasGroup / ChatHolder / ChatMode 均为 public 字段，直接访问。
        private static FieldInfo _fVisTimer;   //ChatDisplay.VisibilityTimer（可见计时器，游戏自己的淡入淡出用它）

        // 滚轮被本功能吃掉的帧号，转发 SR 的通用滚轮仲裁（SR 输入层 的 WheelFrame）。
        public static int WheelFrame { get { return SRApi.WheelFrame; } set { SRApi.WheelFrame = value; } }

        internal static void InitWindow(IFeatureHost plugin) {
            SelfRegWindow(plugin);
            // 向核心注册"是否正在打字"接点：SR 的键位轮询据此避让。
            SRApi.RegisterTypingProvider(() => ChatTyping);
        }

        private static void SelfRegWindow(IFeatureHost plugin) {
            // ChatCanvasGroup / ChatHolder / ChatMode 为 public 字段，已改为直接访问，不登记；仅 VisibilityTimer 保留登记。
            SRApi.Ref.Override("ChatDisplay.VisibilityTimer", "聊天窗口（可见计时器：保持显示但不自己写 alpha，淡入淡出交给游戏）");

            // 本页是会话栏目的第 1 个子页，配置段仍为 Chat Window。
            SRApi.NavHide(Sec); // 本页不单独成侧栏栏目
            SRApi.RegisterPage("Chat", "ChatWindow", "对话窗口", "ChatWindows", 10, RenderPage);
            SRApi.LocEnum("Hold", "按住显示");
            SRApi.LocEnum("Toggle", "切换常驻");
            SRApi.RowFilters.Add(RowVisibleFilter);
            // 会话页：控件紧贴标签（该页标签长短差异大，统一标签列会把控件推到很远）。
            SRApi.RowCompact.Add(delegate(ConfigEntryBase e) {
                return e != null && (e.Definition.Section == Sec || e.Definition.Section == "Chat");
            });
            SRApi.RowExtras.Add(RowExtraFor);
            SRApi.RowSliders.Add(BoxScaleSlider);
            SRApi.RowSliders.Add(FontSizeSlider);
            SRApi.RowSliders.Add(PosSlider); //位置 X/Y：滑块范围按当前分辨率给

            SRApi.LocKey(Sec, "Enabled", "启用增强", "Enable tweaks");
            SRApi.LocDesc(Sec, "Enabled", "本页功能总开关\n决定聊天窗口增强是否应用\n关闭后本页所有修改都不再生效，窗口回到游戏原版行为。", "master switch for this page\nenables or disables all chat-window enhancements on this page\nwhen off, no change from this page applies and the window behaves like vanilla");
            SRApi.LocKey(Sec, "No Auto Show", "关闭自动弹出", "Suppress auto show");
            SRApi.LocDesc(Sec, "No Auto Show", "自动弹出开关（取反）\n开启后新消息不再自动弹出聊天窗口\n按住显示键仍可查看，按 Enter 输入也会弹出。", "auto-show switch (inverted)\nnew messages no longer pop the chat window open\nthe show key still reveals it, and pressing Enter to type still opens it");
            SRApi.LocKey(Sec, "No Fade", "关闭淡入淡出动画", "No fade in/out");
            SRApi.LocDesc(Sec, "No Fade", "淡入淡出开关（取反）\n开启后窗口出现与消失直接瞬切\n关闭时沿用游戏自身淡入淡出速度；与自动弹出互不影响。", "fade switch (inverted)\nthe window appears and disappears instantly\nwhen off, the game own fade speed is used; independent of auto-show");
            SRApi.LocKey(Sec, "Window Key", "显示窗口键", "Show window key");
            SRApi.LocDesc(Sec, "Window Key", "聊天窗口显示键\n按下该键显示聊天窗口，行为由显示方式决定\n点右侧键框后按新键即可改绑，支持鼠标键，Esc 取消。", "show key for the chat window\nshows the chat window; behavior follows the window mode option\nclick the key box, then press a new key to rebind (mouse buttons supported); Esc cancels");
            SRApi.LocKey(Sec, "Window Mode", "显示方式", "Show mode");
            SRApi.LocDesc(Sec, "Window Mode", "显示键的工作方式\n按住＝按住期间显示；切换＝按一下在常驻显示与交还原版之间切换\n需配合窗口显示键使用。", "behavior of the show key\nHold = visible while held; Toggle = tap to switch between pinned-on and vanilla\nused together with the show key option");
            SRApi.LocKey(Sec, "Box Scale", "聊天框缩放", "Chat box scale");
            SRApi.LocDesc(Sec, "Box Scale", "聊天框整体缩放比例\n按比例缩放消息区，文字、玩家名与头像一起\n范围 0.5–2；可按住缩放键滚动滚轮实时调整，每格 0.05。", "overall chat box scale\nscales the message area, including text, player names and portraits\nrange 0.5-2; hold the scale key and scroll the wheel to adjust live (0.05 per notch)");
            SRApi.LocKey(Sec, "Box Scale Key", "缩放键", "Scale key");
            SRApi.LocDesc(Sec, "Box Scale Key", "缩放调节键\n按住它滚动滚轮调整聊天框缩放，每格 0.05\n改绑方式同显示键；字号请用字号调节键。", "scale modifier key\nhold it and scroll the wheel to adjust chat box scale (0.05 per notch)\nrebind like the show key; use the font key for font size");
            SRApi.LocKey(Sec, "Font Size", "聊天文字大小", "Chat font size");
            SRApi.LocDesc(Sec, "Font Size", "聊天文字字号\n设置消息文字、玩家名与头像的尺寸\n范围 1–50；输入框字号不受影响；可按住字号键滚动滚轮调整，每格 1。", "chat font size\nsets the size of message text, player names and portraits\nrange 1-50; the input field is not affected; hold the font key and scroll to adjust (1 per notch)");
            SRApi.LocKey(Sec, "Font Key", "字号键", "Font key");
            SRApi.LocDesc(Sec, "Font Key", "字号调节键\n按住它滚动滚轮调整字号，每格 1\n改绑方式同显示键。", "font size modifier key\nhold it and scroll the wheel to adjust the font size (1 per notch)\nrebind like the show key");
            SRApi.LocKey(Sec, "Crisp Text", "启用清晰度", "Crisp text");
            SRApi.LocDesc(Sec, "Crisp Text", "文字清晰度开关\n将聊天画布设为像素对齐，减少模糊\n切换时会重建消息文字一次。", "text crispness switch\nsets the chat canvas pixel-perfect to reduce blur\nmessage texts are rebuilt once when toggled");
            SRApi.LocKey(Sec, "Pos Enabled", "启用位置调整", "Enable position");
            SRApi.LocDesc(Sec, "Pos Enabled", "位置调整总开关\n决定是否应用下方的 X/Y 偏移\n关闭时会撤销已加偏移一次，位置完全交还游戏。", "master switch for position tweaks\napplies or removes the X/Y offsets below\nwhen turned off, the applied offset is removed once and the game controls the position again");
            SRApi.LocKey(Sec, "Pos X", "位置 X", "Position X");
            SRApi.LocDesc(Sec, "Pos X", "水平偏移量，像素\n正值把聊天框向右移\n仅在位置调整开启时生效；范围 0 到屏幕宽度。", "horizontal offset in pixels\npositive values move the chat box right\nonly applies while position tweaks are on; range 0 to screen width");
            SRApi.LocKey(Sec, "Pos Y", "位置 Y", "Position Y");
            SRApi.LocDesc(Sec, "Pos Y", "垂直偏移量，像素\n负值把聊天框向下移\n仅在位置调整开启时生效；范围负屏高到 0。", "vertical offset in pixels\nnegative values move the chat box down\nonly applies while position tweaks are on; range -screen height to 0");

            //绑定顺序 = 页面显示顺序
            _enabled = plugin.Config.Bind(Sec, "Enabled", false, "总开关：关闭后本页所有改动都不生效。");
            _noAutoShow = plugin.Config.Bind(Sec, "No Auto Show", false, "关闭自动弹出：新消息不再自动显示聊天窗口。");
            _noFade = plugin.Config.Bind(Sec, "No Fade", false, "关闭淡入淡出动画：出现/消失都瞬切。");
            _key = plugin.Config.Bind(Sec, "Window Key", KeyCode.Z, "显示聊天窗口的按键（默认 Z）");
            _boxScale = plugin.Config.Bind(Sec, "Box Scale", 1f, new ConfigDescription(
                "聊天框整体缩放（0.5 - 2，1 = 游戏原版）。", new AcceptableValueRange<float>(0.5f, 2f)));
            _fontSize = plugin.Config.Bind(Sec, "Font Size", 18, new ConfigDescription(
                "聊天文字字号（1 - 50）。游戏自带的默认是 20，模组默认 18；填什么值我们就按什么值改，"
                + "现存消息也会一起改。", new AcceptableValueRange<int>(1, 50)));
            //字号键是聊天文字大小行的同伴控件（同排 = 标签 + 滑块 + 按键）
            _crisp = plugin.Config.Bind(Sec, "Crisp Text", true, "文字清晰度：pixelPerfect。");
            //字号键（默认 C）：按住 + 滚轮 = 调字号
            _fontKey = plugin.Config.Bind(Sec, "Font Key", KeyCode.C, "按住 + 滚轮 = 调字号（默认 C）");
            _posEnabled = plugin.Config.Bind(Sec, "Pos Enabled", false, "位置调整总开关。");
            _posX = plugin.Config.Bind(Sec, "Pos X", 0f, new ConfigDescription(
                "聊天框横向偏移（像素；0 = 游戏默认位置，往右为正）。", new AcceptableValueRange<float>(0f, 8000f)));
            _posY = plugin.Config.Bind(Sec, "Pos Y", 0f, new ConfigDescription(
                "聊天框纵向偏移（像素；0 = 游戏默认位置，往下为负）。", new AcceptableValueRange<float>(-8000f, 0f)));
            //合并到同一行的条目
            _mode = plugin.Config.Bind(Sec, "Window Mode", WinMode.Hold, "按住显示 / 切换常驻。");
            _boxScaleKey = plugin.Config.Bind(Sec, "Box Scale Key", KeyCode.X, "按住 + 滚轮 = 调缩放（默认 X）");
            // 一次性默认值迁移：只改仍是旧默认值（Alt / Ctrl）的配置，开关仅生效一次。
            _keyDefaultsV2 = plugin.Config.Bind(Sec, "Key Defaults v2", false, "内部：快捷键默认值已迁移（显示窗口 Z / 缩放键 X）");
            if (!_keyDefaultsV2.Value) {
                try {
                    if (_key.Value == KeyCode.LeftAlt || _key.Value == KeyCode.RightAlt) _key.Value = KeyCode.Z;
                    if (_boxScaleKey.Value == KeyCode.LeftControl || _boxScaleKey.Value == KeyCode.RightControl) _boxScaleKey.Value = KeyCode.X;
                    _keyDefaultsV2.Value = true;
                } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口快捷键默认值迁移", __ex); }
            }
            // 一次性默认值迁移：字号范围改为 1 - 50 后，老配置的 0 会被夹成 1，需改成 18。
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

        // 自绘页：在"启用位置调整"行前插入分区标题；锚点行必须在可见行上，否则标题永不显示。
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
            // 隐藏两个"合并到别的行"的同伴控件与两个内部迁移标记。
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

        // 位置 X/Y 画成滑块，范围取当前分辨率，0 = 游戏默认位置。
        private static bool PosSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            if (e.Definition.Section != Sec) return false;
            string k = e.Definition.Key;
            if (k == "Pos X") { min = 0f; max = Mathf.Max(800f, Screen.width); return true; }
            if (k == "Pos Y") { min = -Mathf.Max(800f, Screen.height); max = 0f; return true; }
            return false;
        }

        // 三个游戏成员均为 public 字段，直接访问，省去每帧的反射取值与装箱；VisibilityTimer 为 protected，仍走 SRApi.Ref.Field。
        private static CanvasGroup GroupOf(ChatDisplay d) {
            try { return d != null ? d.ChatCanvasGroup : null; } catch { return null; }
        }

        private static RectTransform HolderOf(ChatDisplay d) {
            try { return d != null ? d.ChatHolder : null; } catch { return null; }
        }

        private static bool ChatInputActive(ChatDisplay d) {
            try { return d != null && d.ChatMode; } catch { return false; }
        }

        // 游戏自己的淡入淡出速度（GameSettings.ChatMessagingFadeSpeed，默认 0.8/秒，淡入为其 10 倍）。
        private static float FadeSpeed() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && gs.ChatMessagingFadeSpeed > 0.001f) return gs.ChatMessagingFadeSpeed;
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口淡出速度", __ex); }
            return 0.8f;
        }

        // 游戏聊天输入框是否正在输入（ChatDisplay.ChatMode）：打字期间屏蔽所有快捷键，供 SR 键位轮询与外部模块使用。
        // 结果按帧缓存，并缓存 ChatDisplay 实例，避免每帧 FindObjectOfType 与重复反射读取。
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

        // 可见计时器续期：游戏在 VisibilityTimer>0 时朝 alpha=1 淡入并每帧自减；这里只在"该显示"时把它抬到一个很小的正数，淡入淡出交给游戏动画。
        private static void BumpVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SRApi.Ref.Field(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                object o = _fVisTimer.GetValue(d);
                float cur = o is float ? (float)o : 0f;
                if (cur < 0.15f) _fVisTimer.SetValue(d, 0.15f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器续期", __ex); }
        }

        // 清零：让游戏按自己的速度淡出，而不是直接写 alpha = 0。
        private static void ZeroVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SRApi.Ref.Field(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                _fVisTimer.SetValue(d, 0f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器清零", __ex); }
        }

        private static bool EnabledOn { get { return SRApi.GateMaster && _enabled != null && _enabled.Value; } }

        [HarmonyPatch(typeof(ChatDisplay), "Update")]
        [HarmonyPostfix]
        static void AfterChatUpdate(ChatDisplay __instance) {
            try {
                if (__instance == null) return;
                CanvasGroup cg = GroupOf(__instance);
                RectTransform holder = HolderOf(__instance);
                if (cg == null) return;
                // 总开关关闭：把改动过的游戏字号 / 文字尺寸 / 缩放 / 像素对齐 / 位置偏移一次性还原，之后不再介入。
                if (!EnabledOn) {
                    if (_touched) RestoreVanilla(__instance, holder, cg);
                    return;
                }
                _touched = true;

                //1) 显示/隐藏
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
                    // 关闭淡入淡出开启：直接写 alpha 瞬切。
                    cg.alpha = wantShow ? 1f : 0f;
                    cg.interactable = wantShow; cg.blocksRaycasts = wantShow;
                } else if (wantShow) {
                    // 未关闭淡入淡出：按游戏速度（fadeSpeed*10）把 alpha 持续推向 1；游戏只在有消息或输入框打开时才朝向 1，故须同时续期可见计时器。
                    if (!cg.interactable) { cg.interactable = true; cg.blocksRaycasts = true; }
                    BumpVisibility(__instance); // 同时让游戏也认为"该显示"
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 1f, FadeSpeed() * 10f * Time.unscaledDeltaTime);
                    _wasShowing = true;
                } else if (_noAutoShow != null && _noAutoShow.Value) {
                    // 关闭自动弹出开启且当前不该显示：按游戏速度淡出，并清零计时器使新消息也弹不出。
                    if (cg.interactable) { cg.interactable = false; cg.blocksRaycasts = false; }
                    ZeroVisibility(__instance);
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 0f, FadeSpeed() * Time.unscaledDeltaTime);
                    _wasShowing = false;
                } else {
                    // 其余情况交给游戏：若上一帧由本模组在"按住显示"，清一次续期的计时器，让游戏立刻开始淡出。
                    if (_wasShowing) { _wasShowing = false; ZeroVisibility(__instance); }
                }

                // 2) 滚轮：字号键调字号 / 缩放键调缩放（按住调节键即生效，不要求窗口当时可见）。
                // 只有按住调节键时才取滚轮：TakeWheelEvent 是"取用即清"的全局通道，
                // 无条件取会吞掉其它功能的滚轮（方块破坏的滚轮选块因此失效）。
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

                //3) 缩放（整体 localScale）
                float scale = _boxScale != null ? Mathf.Clamp(_boxScale.Value, 0.5f, 2f) : 1f;
                if (holder != null && Mathf.Abs(holder.localScale.x - scale) > 0.0001f) holder.localScale = new Vector3(scale, scale, 1f);

                //4) 字号 / 清晰度 / 保留条数 / 位置
                if (holder != null) { ApplyFont(__instance, holder); ApplyClarity(holder); }
                ApplyPosition(cg);

                DiagOnce(__instance, holder, cg);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口增强", __ex); }
        }

        // 字号：写入游戏自己的 GameSettings.ChatMessageFontSize（游戏新建消息时按它算头像尺寸）。
        // 额外逐条改消息文字与玩家名字；不可遍历 holder 下的全部 Text —— 聊天输入框也带 Text，会因固定高度放不下而看不见字。
        // 消息列表直接读游戏自己的 ChatDisplay.currentMessages：由游戏维护，恒等于当前真实存在的消息，无需缓存也不分配。
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

        // 对一条消息应用字号（want = 0 表示还原游戏原字号），头像尺寸一并跟随。
        private static void ApplyUnitFont(ChatUnit u, int want) {
            if (u == null) return;
            ApplyTextSize(u.chatText, want);
            ApplyTextSize(u.colonText, want);
            if (u.nameTag != null) ApplyTextSize(u.nameTag.usernameText, want); //名字也跟着字号
            try {
                // 头像尺寸是游戏按字号*2 算出的方形，且只在建消息时算一次；现存消息不同步改会导致文字被行高裁掉。
                int px = want > 0 ? want : _origGameFontSize;
                if (px > 0 && u.chatPortrait != null) {
                    Vector2 sz = new Vector2(px * 2f, px * 2f);
                    if ((u.chatPortrait.rectTransform.sizeDelta - sz).sqrMagnitude > 0.01f)
                        u.chatPortrait.rectTransform.sizeDelta = sz;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口头像尺寸", __ex); }
        }

        // 单段消息文字：want > 0 用配置字号并关闭 best-fit，want = 0 还原游戏原字号与 best-fit。
        private static void ApplyTextSize(Text t, int want) {
            try {
                if (t == null) return;
                if (_origFontSize <= 0 && t.fontSize > 0) { _origFontSize = t.fontSize; _origBestFit = t.resizeTextForBestFit; }
                if (want > 0) {
                    if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                    if (t.fontSize != want) t.fontSize = want;
                } else if (_origFontSize > 0) {
                    if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                    // 还原须用游戏真正的原字号：_origFontSize 抓到的可能已是配置值而非原值。
                    int restore = _origGameFontSize > 0 ? _origGameFontSize : _origFontSize;
                    if (t.fontSize != restore) t.fontSize = restore;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口文字尺寸", __ex); }
        }

        // 清晰度：写 canvas.pixelPerfect；系数变化时让全部文字 SetAllDirty 重建字库。
        // Canvas 查找按 holder 缓存：本方法每帧执行，GetComponentInParent 是无缓存的层级遍历。
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
                Canvas canvas = CanvasOf(holder);
                if (canvas == null) return;
                if (!_havePp) { _origPp = canvas.pixelPerfect; _havePp = true; }
                if (_crisp != null && canvas.pixelPerfect != _crisp.Value) canvas.pixelPerfect = _crisp.Value;
                // 本游戏该画布挂的是自定义 SafeAreaScaler（无 CanvasScaler），故只做 pixelPerfect 与字号取整两件能生效的事。
                // 变化检测值必须跟随开关，否则切换清晰度不会再重建字库。
                float v = (_crisp != null && _crisp.Value) ? 1f : 0f;
                if (Mathf.Abs(_lastClarity - v) > 0.001f) {
                    _lastClarity = v;
                    Text[] texts = holder.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++) { if (texts[i] != null) texts[i].SetAllDirty(); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口清晰度", __ex); }
        }

        // 总开关关闭时的一次性还原：游戏字号 / 每段文字的字号与 bestFit / 缩放 / pixelPerfect / 位置偏移全部交回游戏原值。
        private static void RestoreVanilla(ChatDisplay inst, RectTransform holder, CanvasGroup cg) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && _origGameFontSize > 0 && gs.ChatMessageFontSize != _origGameFontSize)
                    gs.ChatMessageFontSize = _origGameFontSize;
                if (holder != null) {
                    if (Mathf.Abs(holder.localScale.x - 1f) > 0.0001f) holder.localScale = Vector3.one;
                    if (_origFontSize > 0) {
                        // 只还原消息文字，输入框不归本模组管；名字与头像一并还原。
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
            _touched = false;
        }

        // 位置：仅在总开关打开时动锚点，以"当前实际位置"反推基准，游戏自身的移动会被跟随；开关关闭时只减去一次本模组的偏移。
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

        // 诊断：打印当前消息（currentMessages[0]）上受管的文字与头像尺寸及配置值/游戏值；"首次有消息"与"每次字号变化"各记一条，最多 6 条。
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
