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

        // 仅 VisibilityTimer 是 protected，走 SR.RefField；ChatCanvasGroup / ChatHolder / ChatMode 均为 public 字段，直接访问。
        private static FieldInfo _fVisTimer;   //ChatDisplay.VisibilityTimer（可见计时器，游戏自己的淡入淡出用它）

        // 滚轮被本功能吃掉的帧号，转发 SR 的通用滚轮仲裁（SR.Input 的 WheelFrame）。
        public static int WheelFrame { get { return SR.WheelFrame; } set { SR.WheelFrame = value; } }

        internal static void InitWindow(IFeatureHost plugin) {
            SelfRegWindow(plugin);
            // 向核心注册"是否正在打字"接点：SR 的键位轮询据此避让。
            SR.RegisterTypingProvider(() => ChatTyping);
        }

        private static void SelfRegWindow(IFeatureHost plugin) {
            // ChatCanvasGroup / ChatHolder / ChatMode 为 public 字段，已改为直接访问，不登记；仅 VisibilityTimer 保留登记。
            SR.RefOverride("ChatDisplay.VisibilityTimer", "聊天窗口（可见计时器：保持显示但不自己写 alpha，淡入淡出交给游戏）");

            // 本页是会话栏目的第 1 个子页，配置段仍为 Chat Window。
            SR.NavHide(Sec); // 本页不单独成侧栏栏目
            SR.RegisterPage("Chat", "ChatWindow", "对话窗口", "ChatWindows", 10, RenderPage);
            SR.LocEnum("Hold", "按住显示");
            SR.LocEnum("Toggle", "切换常驻");
            SR.RowFilters.Add(RowVisibleFilter);
            SR.RowExtras.Add(RowExtraFor);
            SR.RowSliders.Add(BoxScaleSlider);
            SR.RowSliders.Add(FontSizeSlider);
            SR.RowSliders.Add(PosSlider); //位置 X/Y：滑块范围按当前分辨率给

            SR.LocKey(Sec, "Enabled", "启用增强", "Enable tweaks");
            SR.LocDesc(Sec, "Enabled", "这一页的总开关. 关掉之后聊天窗口按原版跑, 字号缩放位置都不动.", "Master switch: when off, nothing on this page applies and the chat window runs exactly like vanilla (font size, scale and position are left untouched).");
            SR.LocKey(Sec, "No Auto Show", "关闭自动弹出", "Suppress auto show");
            SR.LocDesc(Sec, "No Auto Show", "来新消息不再自动把聊天框弹出来. 按住显示键还是能看, 自己按 Enter 打字也会弹.", "New messages no longer pop the chat window up; the hold/pin show key still reveals it, and typing (Enter) still shows it.");
            SR.LocKey(Sec, "No Fade", "关闭淡入淡出动画", "No fade in/out");
            SR.LocDesc(Sec, "No Fade", "聊天框出现和消失直接切, 不播原版那段淡入淡出. 和自动弹出那个互不干扰.", "The chat window appears/disappears instantly instead of the vanilla fade (independent of \"Suppress auto show\"). When off, the game's own fade in/out speed is used.");
            SR.LocKey(Sec, "Window Key", "显示窗口键", "Show window key");
            SR.LocDesc(Sec, "Window Key", "按这个键显示聊天窗口, 默认 Z. 想换键就点右边的框再按新键, 鼠标键也能绑, Esc 取消.", "Key that shows the chat window, default Z. To bind: click the key box on the right, then press the key you want (middle/side mouse buttons work too); Esc cancels and clears it. Hold = visible while held; Pin = tap to switch between pinned-on and vanilla.");
            SR.LocKey(Sec, "Window Mode", "显示方式", "Show mode");
            SR.LocDesc(Sec, "Window Mode", "配合显示键用. 按住型是按着才显示, 切换型是按一下在常驻显示和交回原版之间切.", "Works with the show key: Hold = visible while that key is held; Pin = tap the key to switch between pinned-on and vanilla.");
            SR.LocKey(Sec, "Box Scale", "聊天框缩放", "Chat box scale");
            SR.LocDesc(Sec, "Box Scale", "聊天框整体放大缩小, 0.5 到 2, 1 是原版. 也可以按住缩放键滚轮调, 每格 0.05.", "Overall chat box scale, 0.5 - 2 (1 = vanilla). Applied to the message holder's localScale; the scale key + wheel also adjusts it live (0.05 per notch).");
            SR.LocKey(Sec, "Box Scale Key", "缩放键", "Scale key");
            SR.LocDesc(Sec, "Box Scale Key", "按住它再滚滚轮就缩放聊天框, 默认 X, 每格 0.05. 换键方法和显示键一样.", "Key used with the wheel, default X. Hold it + wheel = chat box scale (0.05 per notch). Bound the same way as the show key (click the box, press a key, Esc clears). Use the key on the 'Chat font size' row for the font size.");
            SR.LocKey(Sec, "Font Size", "聊天文字大小", "Chat font size");
            SR.LocDesc(Sec, "Font Size", "聊天文字字号, 1 到 50, 默认 18. 也可以按住行尾那个键滚轮调, 每格 1. 现存消息的文字/名字/头像一起改, 输入框不归这里管.", "Chat font size, 1 - 50, default 18. The font key at the end of this row + wheel also adjusts it.\nExisting messages get their text, player name and portrait resized together (the input field is not touched).");
            SR.LocKey(Sec, "Font Key", "字号键", "Font key");
            SR.LocDesc(Sec, "Font Key", "按住它再滚滚轮调字号, 默认 C, 每格 1. 换键方法和显示键一样.", "Key that adjusts the chat font size, default C. Hold it + wheel = font size (1 per notch). Bound like the show key.");
            SR.LocKey(Sec, "Crisp Text", "启用清晰度", "Crisp text");
            SR.LocDesc(Sec, "Crisp Text", "把聊天那块画布对齐像素, 字看着没那么糊. 改的时候会重画一次文字.", "Text crispness: sets the chat canvas to pixel-perfect (sharper, less blur) and rebuilds the texts when it changes.");
            SR.LocKey(Sec, "Pos Enabled", "启用位置调整", "Enable position");
            SR.LocDesc(Sec, "Pos Enabled", "位置调整的总开关. 关掉会把我们加的偏移撤一次, 之后位置完全交给游戏, 游戏自己挪位置也不会打架.", "Master switch for position tweaks: when off, the offset we applied is removed once and the game fully controls the position again (its own moves are followed correctly, no fighting).");
            SR.LocKey(Sec, "Pos X", "位置 X", "Position X");
            SR.LocDesc(Sec, "Pos X", "聊天框往右挪多少像素, 0 是原版位置. 只在位置调整打开时有效.", "Horizontal offset in pixels, slider range 0 - current screen width; 0 = the game's default position, positive moves right. Only applies while \"Enable position\" is on.");
            SR.LocKey(Sec, "Pos Y", "位置 Y", "Position Y");
            SR.LocDesc(Sec, "Pos Y", "聊天框往下挪多少像素, 0 是原版位置. 只在位置调整打开时有效.", "Vertical offset in pixels, slider range -current screen height - 0; 0 = the game's default position, negative moves down. Only applies while \"Enable position\" is on.");

            //绑定顺序 = 页面显示顺序
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "总开关：关闭后本页所有改动都不生效。");
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

            SR.RegisterKey("聊天窗口-显示键", _key, "hold");
            SR.RegisterKey("聊天窗口-缩放键", _boxScaleKey, "hold");
            SR.RegisterKey("聊天窗口-字号键", _fontKey, "hold");
        }

        // 自绘页：在"启用位置调整"行前插入分区标题；锚点行必须在可见行上，否则标题永不显示。
        private static void RenderPage() {
            float col = Mathf.Max(SR.Ctl.Sc(180), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SR.Ctl.SectionEntries(Sec);
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (e.Definition.Key == "Pos Enabled") {
                    GUILayout.Space(SR.Ctl.Sc(6));
                    GUILayout.Label(SR.T("- 聊天框位置 -", "- Chat box position -"), SR.Ctl.SecHeader);
                }
                SR.Ctl.RenderEntryRow(e, true, col);
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

        // 三个游戏成员均为 public 字段，直接访问，省去每帧的反射取值与装箱；VisibilityTimer 为 protected，仍走 SR.RefField。
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
                if (_fVisTimer == null) _fVisTimer = SR.RefField(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                object o = _fVisTimer.GetValue(d);
                float cur = o is float ? (float)o : 0f;
                if (cur < 0.15f) _fVisTimer.SetValue(d, 0.15f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器续期", __ex); }
        }

        // 清零：让游戏按自己的速度淡出，而不是直接写 alpha = 0。
        private static void ZeroVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SR.RefField(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                _fVisTimer.SetValue(d, 0f);
            } catch (Exception __ex) { SRApi.Guard.Log("聊天窗口可见计时器清零", __ex); }
        }

        private static bool EnabledOn { get { return SR.GateMaster && _enabled != null && _enabled.Value; } }

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
                    force = SR.ComboKeyHeld(_key);
                } else {
                    if (SR.ComboKeyDown(_key)) _toggleOn = !_toggleOn;
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
                // 滚轮量取 SR.TakeWheelEvent()：Input.GetAxis("Mouse ScrollWheel") 在本环境恒为 0。
                float wheel = SR.TakeWheelEvent();
                if (Mathf.Abs(wheel) > 0.0001f) {
                    bool scaleKey = _boxScaleKey != null && _boxScaleKey.Value != KeyCode.None && SR.ComboKeyHeld(_boxScaleKey);
                    bool fontKey = _fontKey != null && _fontKey.Value != KeyCode.None && SR.ComboKeyHeld(_fontKey);
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
                        try { node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy); } catch { }
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
