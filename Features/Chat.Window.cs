// 会话 · 对话窗口页。游戏内聊天窗口的增强：显示方式（按住 / 常驻）、关闭自动弹出、
// 淡入淡出、缩放 / 字号 / 清晰度 / 位置。
// 整个会话栏目的总览看同目录的 Chat.cs。

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
        private static bool _wasShowing;       //上一帧是不是我们在"按住显示"（松开后要把续的计时器清一次）
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

        // R396 N3：ChatCanvasGroup / ChatHolder / ChatMode 在游戏里都是 **public 字段**，
        //  已改成直接访问、不再需要反射句柄（这三个字段是它们的遗留）。
        //  只有 VisibilityTimer 是 protected，那个继续走反射。
        private static FieldInfo _fVisTimer;   //ChatDisplay.VisibilityTimer（可见计时器，游戏自己的淡入淡出用它）

        //滚轮被本功能吃掉的帧号：转发 SR 的通用滚轮仲裁（见 SR.Input.cs 的 WheelFrame/MarkWheelUsed）。
        //保留这个名字是因为它原来就在这里，外部引用点（曾用于自由相机让路）不用改。
        public static int WheelFrame { get { return SR.WheelFrame; } set { SR.WheelFrame = value; } }

        // 分区：Chat Window

        internal static void InitWindow(IFeatureHost plugin) {
            SelfRegWindow(plugin);
            //向核心注册"是否正在打字"接点：SR 的键位轮询（SR.KeyBinds.ComboKeyDown/Held）据此避让，
            //核心不再编译期引用 ChatWindow。
            //补丁注册已随类一起交给 SR 统一处理（见 Chat.Initialize）
            SR.RegisterTypingProvider(() => ChatTyping);
        }

        private static void SelfRegWindow(IFeatureHost plugin) {
            // **R396 N3**：下面三个已改直接访问，从反射自检清单里移除（否则启动会报"未登记的反射依赖"）。
            //  ChatDisplay.VisibilityTimer 仍是 protected，保留登记。
            SR.RefOverride("ChatDisplay.VisibilityTimer", "聊天窗口（可见计时器：保持显示但不自己写 alpha，淡入淡出交给游戏）");

            //与对话内容合并到会话栏目：本页是第 1 个子页（配置段仍是 Chat Window，界面显示在会话栏目下）
            SR.NavHide(Sec); //不再单独成侧栏栏目
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
            // **Opt-49 文案纠错**：游戏自己的 ChatMessageFontSize 默认是 **20**（反编译 129034 `= 20`），
            //  不是 18。原来这句话把 18 说成"游戏原版"，等于让用户以为调回原版要填 18（填了反而变小）。
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
            //一次性默认值迁移：老版本这两项默认是 Alt / Ctrl。只把"仍是旧默认值"的配置改成新默认（Z / X），
            //之后用户自己再改成 Alt/Ctrl 也不会被覆盖（开关只生效一次）。
            _keyDefaultsV2 = plugin.Config.Bind(Sec, "Key Defaults v2", false, "内部：快捷键默认值已迁移（显示窗口 Z / 缩放键 X）");
            if (!_keyDefaultsV2.Value) {
                try {
                    if (_key.Value == KeyCode.LeftAlt || _key.Value == KeyCode.RightAlt) _key.Value = KeyCode.Z;
                    if (_boxScaleKey.Value == KeyCode.LeftControl || _boxScaleKey.Value == KeyCode.RightControl) _boxScaleKey.Value = KeyCode.X;
                    _keyDefaultsV2.Value = true;
                } catch (Exception __ex) { SR.Guard.Log("聊天窗口快捷键默认值迁移", __ex); }
            }
            //一次性默认值迁移 v3：字号默认值 0（=原版不动字号）-> 18（=游戏原版字号），范围也改成 1 - 50，
            //老配置里的 0 会被范围夹成 1（字变得很小），所以必须在这里改成 18。
            _defaultsV3 = plugin.Config.Bind(Sec, "Defaults v3", false, "内部：字号默认值已迁移到 18");
            if (!_defaultsV3.Value) {
                try {
                    if (_fontSize.Value < 1) _fontSize.Value = 18;
                    _defaultsV3.Value = true;
                } catch (Exception __ex) { SR.Guard.Log("聊天窗口字号默认值迁移", __ex); }
            }

            SR.RegisterKey("聊天窗口-显示键", _key, "hold");
            SR.RegisterKey("聊天窗口-缩放键", _boxScaleKey, "hold");
            SR.RegisterKey("聊天窗口-字号键", _fontKey, "hold");
        }

        //自绘页：在可见的启用位置调整行前加小分区标题（注意：不能拿被隐藏的行当锚点，否则标题永远不显示）
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
            //隐藏：两个"合并到别的行"的同伴控件 + 两个内部迁移标记
            //（启用字号现在单独成行，所以不再隐藏）
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

        //位置 X/Y 也画成滑块，范围取当前分辨率：X = 0 - 屏宽，Y = -屏高 - 0（0 = 游戏默认位置）
        private static bool PosSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            if (e.Definition.Section != Sec) return false;
            string k = e.Definition.Key;
            if (k == "Pos X") { min = 0f; max = Mathf.Max(800f, Screen.width); return true; }
            if (k == "Pos Y") { min = -Mathf.Max(800f, Screen.height); max = 0f; return true; }
            return false;
        }

        // R396 N3：这三个成员在游戏里**都是 public 字段**（反编译 83966 ChatHolder /
        //  83968 ChatCanvasGroup / 83982 ChatMode），原来却统一走 SR.RefField + FieldInfo.GetValue，
        //  而这三个访问点在 ChatDisplay.Update 后缀上**每帧执行**（Chat Window 默认开启 = 常驻开销），
        //  其中 bool / float 还要各装箱一次。
        //  只有 VisibilityTimer（83980）是 protected，那个继续走反射。
        //  -> 直接访问，去掉每帧的 GetValue + 装箱。
        private static CanvasGroup GroupOf(ChatDisplay d) {
            try { return d != null ? d.ChatCanvasGroup : null; } catch { return null; }
        }

        private static RectTransform HolderOf(ChatDisplay d) {
            try { return d != null ? d.ChatHolder : null; } catch { return null; }
        }

        private static bool ChatInputActive(ChatDisplay d) {
            try { return d != null && d.ChatMode; } catch { return false; }
        }

        //游戏自己的淡入淡出速度（GameSettings.ChatMessagingFadeSpeed，默认 0.8/秒；淡入是它的 10 倍）
        private static float FadeSpeed() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && gs.ChatMessagingFadeSpeed > 0.001f) return gs.ChatMessagingFadeSpeed;
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口淡出速度", __ex); }
            return 0.8f;
        }

        //游戏自己的聊天输入框正在输入中吗（ChatDisplay.ChatMode）-> 打字期间屏蔽所有快捷键。
        //给 SR 自己的键轮询（SR.ComboKeyDown/ComboKeyHeld）与外部模块用；缓存 ChatDisplay 实例，避免每帧 FindObjectOfType。
        //ChatMode 是私有字段、只能反射读 -> 结果按帧缓存（每帧十几次调用会重复读同一个字段）。
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

        //可见计时器续期：游戏 Update 里 VisibilityTimer>0 才会朝 alpha=1 淡入（速度 ChatMessagingFadeSpeed10），
        //并且每帧自减；我们只要在"该显示"时把它抬到一个很小的正数，淡入/淡出就完全由游戏自己的动画负责。
        private static void BumpVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SR.RefField(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                object o = _fVisTimer.GetValue(d);
                float cur = o is float ? (float)o : 0f;
                if (cur < 0.15f) _fVisTimer.SetValue(d, 0.15f);
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口可见计时器续期", __ex); }
        }

        //清零：让游戏按自己的速度淡出（而不是我们直接把 alpha 写成 0）
        private static void ZeroVisibility(ChatDisplay d) {
            try {
                if (_fVisTimer == null) _fVisTimer = SR.RefField(typeof(ChatDisplay), "VisibilityTimer", "聊天窗口（可见计时器）");
                if (_fVisTimer == null) return;
                _fVisTimer.SetValue(d, 0f);
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口可见计时器清零", __ex); }
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
                //总开关关掉：把我们改过的东西**还原一次**（游戏字号 / 文字尺寸 / 缩放 / 像素对齐 / 位置偏移），
                //然后彻底不再插手, 这样"关掉 = 原版"才是真的（旧写法只是停止写入，字号/缩放会一直留到重启）。
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
                    //关闭淡入淡出开着：直接写 alpha（瞬切）
                    cg.alpha = wantShow ? 1f : 0f;
                    cg.interactable = wantShow; cg.blocksRaycasts = wantShow;
                } else if (wantShow) {
                    //没开关闭淡入淡出：按**游戏自己的速度**淡入（fadeSpeed*10），并且持续把 alpha 推向 1。
                    //注意：不能只靠游戏的 VisibilityTimer, 游戏只有在"窗口里有消息 或 输入框开着"时才朝向 1，
                    //消息清空后按住显示键就完全不动（这就是"按住显示在 No Fade 关着时无效"的原因）。
                    if (!cg.interactable) { cg.interactable = true; cg.blocksRaycasts = true; }
                    BumpVisibility(__instance); //同时让游戏也认为"该显示"（有消息时它会一起朝 1 淡入）
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 1f, FadeSpeed() * 10f * Time.unscaledDeltaTime);
                    _wasShowing = true;
                } else if (_noAutoShow != null && _noAutoShow.Value) {
                    //关闭自动弹出开着且当前不该显示：自己按游戏速度淡出（不硬切），并把计时器清零让新消息也弹不出来
                    if (cg.interactable) { cg.interactable = false; cg.blocksRaycasts = false; }
                    ZeroVisibility(__instance);
                    cg.alpha = Mathf.MoveTowards(cg.alpha, 0f, FadeSpeed() * Time.unscaledDeltaTime);
                    _wasShowing = false;
                } else {
                    //其余情况：交给游戏自己。若上一帧是我们在"按住显示"，把续的计时器清零一次，
                    //让游戏立刻开始按自己的速度淡出（而不是因为我们续过 0.15 秒而多显示一会儿）。
                    if (_wasShowing) { _wasShowing = false; ZeroVisibility(__instance); }
                }

                //2) 滚轮：字号键 调字号 / 缩放键 调缩放（按住调节键即生效，不要求窗口当时可见）
                // **G6 修复**：改取 SR.TakeWheelEvent()。原来读 Input.GetAxis("Mouse ScrollWheel")
                // 该值在本环境恒为 0（_IMGUI_LESSONS.md 八.2 已定论）-> 下面 Mathf.Abs(wheel) > 0.0001f
                //  恒为假，字号/缩放的滚轮快捷键从未生效（同页滑块仍可用，所以只是"没坏"）。
                //  TakeWheelEvent 由每帧总执行的 ManagerUI.OnGUI 从事件 delta 记录。
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
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口增强", __ex); }
        }

        //字号：写游戏自己的 GameSettings.ChatMessageFontSize, 游戏新建消息时会用它设头像尺寸
        //（ChatUnit.SetChatUnitMessage: sizeDelta = 字号2），所以新消息的头像会跟着字号变大（这是游戏自己的规则）。
        //我们额外逐条改的是消息文字：正文 chatText、冒号 colonText、玩家名字（UGCNameTag.usernameText）；
        //现存头像一概不碰。注意绝不能遍历 holder 下所有 Text：游戏自己的聊天输入框（ChatInputField 挂在
        //ChatHolder 下）也带 Text，字号一大它那点固定高度就放不下，表现为"输入框里的字消失"。
        // **Opt-49（正确性 + 性能，真机诊断查出的根因）**：消息列表改为**直接读游戏自己的**
        //  `ChatDisplay.currentMessages`，不再靠"层级扫描 + 指纹缓存"。
        //  原来那套指纹是 `childCount + 最后一个子物体上挂的 ChatUnit`，但游戏在 DisplayNewMessage 里
        //  **每次都执行 `currentChatInputField.transform.SetAsLastSibling()`**（反编译 84113）——
        //  于是**输入框永远是 holder 的最后一个子物体**，它身上没有 ChatUnit -> `lastUnit` 恒为 null ->
        //  指纹条件 `(_unitCache.Length > 0 && !ReferenceEquals(末位, lastUnit))` **恒为真** ->
        //  每一帧都重建缓存（正是那段注释要避免的"每帧数组分配 + 递归遍历子树"），
        //  而"末位 ChatUnit"这个判断等于从来没生效过。
        //  `currentMessages` 由游戏自己维护（加消息时 Add；超过 maxVisibleMessages 时**先 Remove 再 Destroy**），
        //  永远等于"当前真实存在的消息" -> 不需要缓存、不会失效、不分配。
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
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口字号", __ex); }
        }

        // 对**一条消息**应用字号（want = 0 表示还原成游戏原字号），并让头像尺寸一起跟随。
        private static void ApplyUnitFont(ChatUnit u, int want) {
            if (u == null) return;
            ApplyTextSize(u.chatText, want);
            ApplyTextSize(u.colonText, want);
            if (u.nameTag != null) ApplyTextSize(u.nameTag.usernameText, want); //名字也跟着字号
            try {
                // **Opt-49**：头像尺寸是游戏**按字号算的方形**（SetChatUnitMessage: sizeDelta = 字号*2，反编译 84492），
                //  但它只在"建消息"那一次算。现存消息若不动头像，就会出现"文字变大了、头像和行高还是旧尺寸"
                //  -> 文字被行高裁掉（Text 默认 verticalOverflow=Truncate），看起来正是"字号调整没生效"。
                //  所以这里跟着一起改；want=0 时还原成游戏原字号那一档。
                int px = want > 0 ? want : _origGameFontSize;
                if (px > 0 && u.chatPortrait != null) {
                    Vector2 sz = new Vector2(px * 2f, px * 2f);
                    if ((u.chatPortrait.rectTransform.sizeDelta - sz).sqrMagnitude > 0.01f)
                        u.chatPortrait.rectTransform.sizeDelta = sz;
                }
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口头像尺寸", __ex); }
        }

        //单段消息文字：want > 0 用配置字号（同时关掉 best-fit），want = 0 还原游戏原字号/best-fit
        private static void ApplyTextSize(Text t, int want) {
            try {
                if (t == null) return;
                if (_origFontSize <= 0 && t.fontSize > 0) { _origFontSize = t.fontSize; _origBestFit = t.resizeTextForBestFit; }
                if (want > 0) {
                    if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                    if (t.fontSize != want) t.fontSize = want;
                } else if (_origFontSize > 0) {
                    if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                    //还原要用"游戏真正的原字号"：_origFontSize 是本方法第一次见到的字号，而那时
                    //gs.ChatMessageFontSize 已被我们改成 want，抓到的可能是 18 而不是原版 20。
                    int restore = _origGameFontSize > 0 ? _origGameFontSize : _origFontSize;
                    if (t.fontSize != restore) t.fontSize = restore;
                }
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口文字尺寸", __ex); }
        }

        //清晰度：pixelPerfect（+ 有 CanvasScaler 时才写 dynamicPixelsPerUnit）；系数变化时重建字库
        // **R396 N5**：Canvas 查找缓存。ApplyClarity 每帧被调（挂在 ChatDisplay.Update 后缀），
        //  而 GetComponentInParent<Canvas>() 是**无缓存的层级遍历**，发生在常驻路径上。
        //  holder 变了才重查（与 _fontHolderId 同样的失效机制）。
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
                if (!_havePp) { _origPp = canvas.pixelPerfect; _havePp = true; } //先记原值，再改
                if (_crisp != null && canvas.pixelPerfect != _crisp.Value) canvas.pixelPerfect = _crisp.Value;
                //注：本游戏这块画布挂的是自定义 SafeAreaScaler（没有 CanvasScaler），
                //所以只做能生效的两件事：pixelPerfect（像素对齐）+ 字号取整（见 ApplyFont）。
                //变化检测：v 必须跟着开关走。原来写成常量 1f，首次执行后 _lastClarity 就恒等于 1，
                //此后条件永远为假 -> 切换启用清晰度不会再重建字库（与这行原本的注释描述不符）。
                float v = (_crisp != null && _crisp.Value) ? 1f : 0f;
                if (Mathf.Abs(_lastClarity - v) > 0.001f) {
                    _lastClarity = v;
                    Text[] texts = holder.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++) { if (texts[i] != null) texts[i].SetAllDirty(); }
                }
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口清晰度", __ex); }
        }

        //总开关关掉时的一次性还原：游戏字号 / 每段文字的字号与 bestFit / 聊天框缩放 / 画布 pixelPerfect /
        //我们施加过的位置偏移，全部交回游戏原值，然后不再插手（对应启用增强的说明）。
        private static void RestoreVanilla(ChatDisplay inst, RectTransform holder, CanvasGroup cg) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && _origGameFontSize > 0 && gs.ChatMessageFontSize != _origGameFontSize)
                    gs.ChatMessageFontSize = _origGameFontSize;
                if (holder != null) {
                    if (Mathf.Abs(holder.localScale.x - 1f) > 0.0001f) holder.localScale = Vector3.one;
                    if (_origFontSize > 0) {
                        //同样只还原消息文字（输入框不归我们管）；名字与头像一起还原（见 ApplyUnitFont）
                        List<ChatUnit> msgs = inst != null ? inst.currentMessages : null;
                        if (msgs != null) for (int i = 0; i < msgs.Count; i++) ApplyUnitFont(msgs[i], 0);
                    }
                    Canvas canvas = holder.GetComponentInParent<Canvas>();
                    if (canvas != null && _havePp && canvas.pixelPerfect != _origPp) canvas.pixelPerfect = _origPp;
                }
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node != null && _posApplied)
                    node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy);
                _lastClarity = -1f; //下次再打开时重建一次字库
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口还原原版", __ex); }
            _posApplied = false; _posHaveBase = false;
            _touched = false;
        }

        //位置：**只在总开关打开时**才动锚点位置，而且以"当前实际位置"反推基准（游戏自己移动会被跟随，不会打架）。
        //关掉开关时，只把我们的偏移减掉一次，然后完全不再碰位置, 之前写成"每帧还原到最初位置"，
        //遇到游戏自己会换位置的场景（不同大厅/树屋/分辨率变化）就会错位。
        private static void ApplyPosition(CanvasGroup cg) {
            try {
                RectTransform node = cg != null ? cg.transform as RectTransform : null;
                if (node == null) return;
                bool on = _posEnabled != null && _posEnabled.Value;
                float dx = on && _posX != null ? _posX.Value : 0f;
                float dy = on && _posY != null ? _posY.Value : 0f;
                if (!on) {
                    if (_posApplied) { //撤掉我们最后一次施加的偏移，之后不再干预
                        try { node.anchoredPosition = node.anchoredPosition - new Vector2(_posLastDx, _posLastDy); } catch { }
                        _posApplied = false; _posHaveBase = false;
                    }
                    return;
                }
                Vector2 cur = node.anchoredPosition;
                Vector2 want = cur;
                if (!_posHaveBase) { _posBase = cur; _posHaveBase = true; }
                else if ((cur - _posLast).sqrMagnitude > 0.25f) { _posBase = cur - new Vector2(_posLastDx, _posLastDy); } //游戏自己动了 -> 反推基准
                want = _posBase + new Vector2(dx, dy);
                if ((cur - want).sqrMagnitude > 0.0001f) node.anchoredPosition = want;
                _posLast = node.anchoredPosition;
                _posLastDx = dx; _posLastDy = dy;
                _posApplied = true;
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口位置", __ex); }
        }

        // **Opt-49**：诊断升级。旧版只打印"子树里第一个 Text"（那多半是**输入框**的字，
        //  它的字号由游戏自己写、不归我们管），所以看到 `size=18` 也无法判断聊天文字到底改没改。
        //  现在打印**真实消息**（currentMessages[0]）上我们管的三个文字 + 头像尺寸 + 配置值/游戏值，
        //  并且"第一次有消息"和"每次字号变化"各记一条（最多 6 条）——
        //  改一次字号，下一行日志就能看出有没有落到文字上。
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
                SR.LogInfo("[Chat] 字号诊断：配置字号=" + want
                    + " 游戏字号=" + (gs != null ? gs.ChatMessageFontSize.ToString() : "?")
                    + " 消息数=" + (msgs != null ? msgs.Count.ToString() : "?")
                    + " holder缩放=" + holder.localScale.x
                    + " 像素对齐=" + (canvas != null ? canvas.pixelPerfect.ToString() : "?")
                    + " alpha=" + (cg != null ? cg.alpha.ToString() : "?")
                    + " || 首条消息 " + first);
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口诊断", __ex); }
        }
}
}
