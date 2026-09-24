using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    //游戏内聊天窗口增强（精简版）：
    //  · 显示窗口：按键（默认 Z）按住显示，或按键切换常驻（方式下拉框和按键同一行）；
    //  · 关闭自动弹出 / 关闭淡入淡出动画；
    //  · 聊天框缩放（整体作用到消息容器的 localScale；滑块 + 缩放键（默认 X）：按住 + 滚轮 = 调缩放）；
    //  · 聊天文字大小（滑块 + 启用开关 + 字号键（默认 C）：按住 + 滚轮 = 调字号）；
    //  · 启用清晰度（画布 pixelPerfect + 重建文字，开关）；
    //  · 小分区「聊天框位置」：总开关 + 位置 X/Y。
    //
    //（尺寸/裁切/Z 滚动/文字清晰度系数/保留消息条数等按需求已删除：那套代码要改游戏自身的布局节点，副作用大于收益）
    //
    //实现：给 ChatDisplay.Update 挂 Postfix（游戏自己的显隐/淡出之后），所有私有成员用 SR.RefField 取。
    public class ChatWindow : ITweak {

        public enum WinMode { Hold, Toggle } //按住显示 / 切换常驻（都不按 = 游戏原版行为）

        private const string Sec = "Chat Window";
        private const string PosSubHeader = "— 聊天框位置 —";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _noAutoShow;
        private static ConfigEntry<bool> _noFade;
        private static ConfigEntry<KeyCode> _key;          //显示窗口
        private static ConfigEntry<WinMode> _mode;         //（合并到 显示窗口 行）
        private static ConfigEntry<float> _boxScale;       //聊天框缩放
        private static ConfigEntry<KeyCode> _boxScaleKey;  //（合并到 聊天框缩放 行）
        private static ConfigEntry<KeyCode> _fontKey;      //字号键（整行显示：按住 + 滚轮 = 调字号）
        private static ConfigEntry<int> _fontSize;         //聊天文字大小
        private static ConfigEntry<bool> _fontEnabled;     //（合并到 聊天文字大小 行）
        private static ConfigEntry<bool> _crisp;           //（合并到 清晰度系数 行）
        private static ConfigEntry<bool> _posEnabled;      //聊天框位置总开关
        private static ConfigEntry<float> _posX;
        private static ConfigEntry<float> _posY;
        private static ConfigEntry<bool> _keyDefaultsV2;   //内部：快捷键默认值迁移标记（不出现在页面上）

        private static bool _toggleOn;         //Toggle 模式下的常驻状态
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

        private static FieldInfo _fCanvasGroup;
        private static FieldInfo _fHolder;
        private static FieldInfo _fChatMode;
        private static FieldInfo _fVisTimer;   //ChatDisplay.VisibilityTimer（可见计时器，游戏自己的淡入淡出用它）

        //滚轮被本功能吃掉的帧号：自由相机的滚轮缩放据此让路
        public static int WheelFrame = -1;

        // ==== 分区：Chat Window ====

        public void Initialize(MainPlugin plugin) {
            SelfReg(plugin);
            try { Harmony.CreateAndPatchAll(typeof(ChatWindow), (string)null); }
            catch (Exception e) { MainPlugin.ModLogger.LogError("聊天窗口增强 补丁注册失败: " + e.Message); }
        }

        private static void SelfReg(MainPlugin plugin) {
            SR.RefOverride("ChatDisplay.ChatCanvasGroup", "聊天窗口（显示/隐藏）");
            SR.RefOverride("ChatDisplay.ChatHolder", "聊天窗口（消息容器）");
            SR.RefOverride("ChatDisplay.ChatMode", "聊天窗口（输入中判定）");
            SR.RefOverride("ChatDisplay.VisibilityTimer", "聊天窗口（可见计时器：保持显示但不自己写 alpha，淡入淡出交给游戏）");

            SR.LocSec(Sec, "聊天窗口", "Chat window");
            SR.Nav(Sec, 75); //侧栏栏目顺序 75（排在「会话内容」80 之前）
            SR.RegisterPage(Sec, RenderPage); //自绘页：给「聊天框位置」加小分区标题
            SR.LocEnum("Hold", "按住显示");
            SR.LocEnum("Toggle", "切换常驻");
            SR.RowFilters.Add(RowVisibleFilter);
            SR.RowExtras.Add(RowExtraFor);
            SR.RowSliders.Add(BoxScaleSlider);
            SR.RowSliders.Add(FontSizeSlider);

            SR.LocKey(Sec, "Enabled", "启用增强", "Enable tweaks");
            SR.LocDesc(Sec, "Enabled", "总开关：关掉后本页所有改动都不生效（聊天窗口按游戏原版运行：不改字号、不缩放、不动位置）。", "Master switch: when off, nothing on this page applies and the chat window runs exactly like vanilla (font size, scale and position are left untouched).");
            SR.LocKey(Sec, "No Auto Show", "关闭自动弹出", "Suppress auto show");
            SR.LocDesc(Sec, "No Auto Show", "新消息不再自动把聊天窗口弹出来；按住/常驻的显示键照常能看，自己按 Enter 打字时也会显示。", "New messages no longer pop the chat window up; the hold/pin show key still reveals it, and typing (Enter) still shows it.");
            SR.LocKey(Sec, "No Fade", "关闭淡入淡出动画", "No fade in/out");
            SR.LocDesc(Sec, "No Fade", "聊天窗口的出现/消失改成瞬切，不播放游戏原本的淡入淡出（与「关闭自动弹出」互相独立）。关掉时完全用游戏自己的淡入淡出速度。", "The chat window appears/disappears instantly instead of the vanilla fade (independent of \"Suppress auto show\"). When off, the game's own fade in/out speed is used.");
            SR.LocKey(Sec, "Window Key", "显示窗口键", "Show window key");
            SR.LocDesc(Sec, "Window Key", "显示聊天窗口的按键，默认 Z。绑定：点一下右侧键位框开始录制，再按要用的键即可（鼠标中键/侧键也支持），Esc 取消并清空。按住显示 = 按住期间显示；切换常驻 = 按一下在「常驻显示 / 交回游戏原版」之间切换。", "Key that shows the chat window, default Z. To bind: click the key box on the right, then press the key you want (middle/side mouse buttons work too); Esc cancels and clears it. Hold = visible while held; Pin = tap to switch between pinned-on and vanilla.");
            SR.LocKey(Sec, "Window Mode", "显示方式", "Show mode");
            SR.LocDesc(Sec, "Window Mode", "配合「显示窗口键」：按住显示 = 按住该键期间才显示；切换常驻 = 按一下该键在「常驻显示 / 交回游戏原版」之间切换。", "Works with the show key: Hold = visible while that key is held; Pin = tap the key to switch between pinned-on and vanilla.");
            SR.LocKey(Sec, "Box Scale", "聊天框缩放", "Chat box scale");
            SR.LocDesc(Sec, "Box Scale", "聊天框整体缩放，0.6 - 1.6（1 = 游戏原版）。作用在消息容器的 localScale 上；也可以用「缩放键 + 滚轮」实时调（每格 0.05）。", "Overall chat box scale, 0.6 - 1.6 (1 = vanilla). Applied to the message holder's localScale; the scale key + wheel also adjusts it live (0.05 per notch).");
            SR.LocKey(Sec, "Box Scale Key", "缩放键", "Scale key");
            SR.LocDesc(Sec, "Box Scale Key", "滚轮调节用的键，默认 X。按住它 + 滚轮 = 调聊天框缩放（每格 0.05）。绑定方式同「显示窗口键」（点键位框再按键，Esc 清空）。调字号用下面的「字号键」。", "Key used with the wheel, default X. Hold it + wheel = chat box scale (0.05 per notch). Bound the same way as the show key (click the box, press a key, Esc clears). Use 'Font Key' below for the font size.");
            SR.LocKey(Sec, "Font Key", "字号键", "Font key");
            SR.LocDesc(Sec, "Font Key", "调聊天文字大小的键，默认 C。按住它 + 滚轮 = 调字号（每格 1，范围 0 - 48，0 = 原版），需要「启用字号」打开。绑定方式同「显示窗口键」。", "Key that adjusts the chat font size, default C. Hold it + wheel = font size (1 per notch, 0 - 48, 0 = vanilla); requires 'Enable font size'. Bound the same way as the show key.");
            SR.LocKey(Sec, "Font Size", "聊天文字大小", "Chat font size");
            SR.LocDesc(Sec, "Font Size", "聊天文字字号，1 - 48；0 = 游戏原版（完全不动字号）。只在「启用字号」打开时生效，也能用「字号键 + 滚轮」调。\n写的是游戏自己的 GameSettings.ChatMessageFontSize，所以之后新收到的消息也按这个字号显示（头像尺寸会一起变）。", "Chat font size, 1 - 48; 0 = vanilla (font untouched). Applies only while \"Enable font size\" is on; the font key + wheel also adjusts it.\nIt writes the game's own GameSettings.ChatMessageFontSize, so new messages follow it too (portrait size changes with it).");
            SR.LocKey(Sec, "Font Enabled", "启用字号", "Enable font size");
            SR.LocDesc(Sec, "Font Enabled", "聊天文字大小的开关：关掉 = 完全用游戏原版字号，同时「字号键 + 滚轮」调字号也一起失效。", "Switch for the chat font size: off = vanilla size, and the font key + wheel shortcut is disabled as well.");
            SR.LocKey(Sec, "Crisp Text", "启用清晰度", "Crisp text");
            SR.LocDesc(Sec, "Crisp Text", "文字清晰度：把聊天所在的画布设为 pixelPerfect（像素对齐，减少发虚），并在变化时重建一次文字。\n本游戏的聊天画布挂的是自定义 SafeAreaScaler（没有 CanvasScaler），所以没有「清晰度系数」可调。", "Text crispness: sets the chat canvas to pixel-perfect (sharper, less blur) and rebuilds the texts when it changes.\nThis game's chat canvas uses a custom SafeAreaScaler (no CanvasScaler), so there is no clarity factor to tune.");
            SR.LocKey(Sec, "Pos Enabled", "启用位置调整", "Enable position");
            SR.LocDesc(Sec, "Pos Enabled", "位置调整总开关：关掉 = 撤销我们施加过的偏移一次，之后聊天框位置完全交回游戏（游戏自己换位置也能正确跟随，不打架）。", "Master switch for position tweaks: when off, the offset we applied is removed once and the game fully controls the position again (its own moves are followed correctly, no fighting).");
            SR.LocKey(Sec, "Pos X", "位置 X", "Position X");
            SR.LocDesc(Sec, "Pos X", "聊天框横向偏移（像素，-800 - 800；0 = 游戏原版位置）。只在「启用位置调整」打开时生效。", "Horizontal offset in pixels, -800 - 800 (0 = vanilla). Only applies while \"Enable position\" is on.");
            SR.LocKey(Sec, "Pos Y", "位置 Y", "Position Y");
            SR.LocDesc(Sec, "Pos Y", "聊天框纵向偏移（像素，-800 - 800；0 = 游戏原版位置）。只在「启用位置调整」打开时生效。", "Vertical offset in pixels, -800 - 800 (0 = vanilla). Only applies while \"Enable position\" is on.");

            //绑定顺序 = 页面显示顺序
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "总开关：关闭后本页所有改动都不生效。");
            _noAutoShow = plugin.Config.Bind(Sec, "No Auto Show", false, "关闭自动弹出：新消息不再自动显示聊天窗口。");
            _noFade = plugin.Config.Bind(Sec, "No Fade", false, "关闭淡入淡出动画：出现/消失都瞬切。");
            _key = plugin.Config.Bind(Sec, "Window Key", KeyCode.Z, "显示聊天窗口的按键（默认 Z）");
            _boxScale = plugin.Config.Bind(Sec, "Box Scale", 1f, new ConfigDescription(
                "聊天框整体缩放（0.6 - 1.6，1 = 游戏原版）。", new AcceptableValueRange<float>(0.6f, 1.6f)));
            _fontSize = plugin.Config.Bind(Sec, "Font Size", 0, new ConfigDescription(
                "聊天文字字号；0 = 游戏原版（8 - 48）。", new AcceptableValueRange<int>(0, 48)));
            //「启用字号」是「聊天文字大小」行的同伴控件（同排渲染），「启用清晰度」紧跟其后单独成行
            _fontEnabled = plugin.Config.Bind(Sec, "Font Enabled", true, "启用聊天文字大小调整。");
            _crisp = plugin.Config.Bind(Sec, "Crisp Text", true, "文字清晰度：pixelPerfect + 字号取整。");
            //字号键单独成行（放在「启用清晰度」后面）：按住它 + 滚轮 = 调字号（原来是"缩放键+Alt+滚轮"，已取消）
            _fontKey = plugin.Config.Bind(Sec, "Font Key", KeyCode.C, "按住 + 滚轮 = 调字号（默认 C）");
            _posEnabled = plugin.Config.Bind(Sec, "Pos Enabled", false, "位置调整总开关。");
            _posX = plugin.Config.Bind(Sec, "Pos X", 0f, new ConfigDescription(
                "聊天框横向偏移（像素，0 = 原版）。", new AcceptableValueRange<float>(-800f, 800f)));
            _posY = plugin.Config.Bind(Sec, "Pos Y", 0f, new ConfigDescription(
                "聊天框纵向偏移（像素，0 = 原版）。", new AcceptableValueRange<float>(-800f, 800f)));
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

            SR.RegisterKey("聊天窗口-显示键", _key, "hold");
            SR.RegisterKey("聊天窗口-缩放键", _boxScaleKey, "hold");
            SR.RegisterKey("聊天窗口-字号键", _fontKey, "hold");
        }

        //自绘页：在可见的「启用位置调整」行前加小分区标题（注意：不能拿被隐藏的行当锚点，否则标题永远不显示）
        private static void RenderPage() {
            float col = Mathf.Max(SR.Ctl.Sc(180), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SR.Ctl.SectionEntries(Sec);
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (e.Definition.Key == "Pos Enabled") {
                    GUILayout.Space(SR.Ctl.Sc(6));
                    GUILayout.Label(PosSubHeader, SR.Ctl.SecHeader);
                }
                SR.Ctl.RenderEntryRow(e, true, col);
            }
        }

        private static bool RowVisibleFilter(ConfigEntryBase e) {
            if (e.Definition.Section != Sec) return true;
            string k = e.Definition.Key;
            return k != "Window Mode" && k != "Box Scale Key" && k != "Font Enabled" && k != "Key Defaults v2";
        }

        private static string RowExtraFor(ConfigEntryBase e) {
            if (e.Definition.Section != Sec) return null;
            switch (e.Definition.Key) {
                case "Window Key": return "Window Mode";
                case "Box Scale": return "Box Scale Key";
                case "Font Size": return "Font Enabled";
                default: return null;
            }
        }

        private static bool BoxScaleSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0.6f; max = 1.6f; fmt = "0.00";
            return e.Definition.Section == Sec && e.Definition.Key == "Box Scale";
        }

        private static bool FontSizeSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 48f; fmt = "0";
            return e.Definition.Section == Sec && e.Definition.Key == "Font Size";
        }

        // ---- 反射取私有成员 ----
        private static CanvasGroup GroupOf(ChatDisplay d) {
            try {
                if (_fCanvasGroup == null) _fCanvasGroup = SR.RefField(typeof(ChatDisplay), "ChatCanvasGroup", "聊天窗口（显示/隐藏）");
                return _fCanvasGroup != null ? _fCanvasGroup.GetValue(d) as CanvasGroup : null;
            } catch { return null; }
        }

        private static RectTransform HolderOf(ChatDisplay d) {
            try {
                if (_fHolder == null) _fHolder = SR.RefField(typeof(ChatDisplay), "ChatHolder", "聊天窗口（消息容器）");
                return _fHolder != null ? _fHolder.GetValue(d) as RectTransform : null;
            } catch { return null; }
        }

        private static bool ChatInputActive(ChatDisplay d) {
            try {
                if (_fChatMode == null) _fChatMode = SR.RefField(typeof(ChatDisplay), "ChatMode", "聊天窗口（输入中判定）");
                if (_fChatMode == null) return false;
                object v = _fChatMode.GetValue(d);
                return v is bool && (bool)v;
            } catch { return false; }
        }

        //「可见计时器」续期：游戏 Update 里 VisibilityTimer>0 才会朝 alpha=1 淡入（速度 ChatMessagingFadeSpeed*10），
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
                //然后彻底不再插手 —— 这样"关掉 = 原版"才是真的（旧写法只是停止写入，字号/缩放会一直留到重启）。
                if (!EnabledOn) {
                    if (_touched) RestoreVanilla(holder, cg);
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
                    //「关闭淡入淡出」开着：直接写 alpha（瞬切）
                    cg.alpha = wantShow ? 1f : 0f;
                    cg.interactable = wantShow; cg.blocksRaycasts = wantShow;
                } else if (wantShow) {
                    //没开「关闭淡入淡出」：**不自己写 alpha**，而是把游戏的「可见计时器」续上一小段 →
                    //游戏自己朝 alpha=1 淡入并保持；松开后计时器归零 → 游戏按 ChatMessagingFadeSpeed 自己淡出。
                    //（旧写法在松开的那一帧直接 alpha=0，等于偷偷强制了瞬切 → 这就是"没开 No Fade 却没有淡出"。）
                    if (!cg.interactable) { cg.interactable = true; cg.blocksRaycasts = true; }
                    BumpVisibility(__instance);
                } else if (_noAutoShow != null && _noAutoShow.Value) {
                    //「关闭自动弹出」开着且当前不该显示：计时器清零 → 游戏自己淡出（不硬切），新消息也不会把它弹出来
                    if (cg.interactable) { cg.interactable = false; cg.blocksRaycasts = false; }
                    ZeroVisibility(__instance);
                }
                //其余情况：什么都不做 = 完全交给游戏（原版行为）

                //2) 滚轮：字号键 调字号 / 缩放键 调缩放（按住调节键即生效，不要求窗口当时可见）
                float wheel = 0f;
                try { wheel = Input.GetAxis("Mouse ScrollWheel"); } catch { wheel = 0f; }
                if (Mathf.Abs(wheel) > 0.0001f) {
                    bool scaleKey = _boxScaleKey != null && _boxScaleKey.Value != KeyCode.None && SR.ComboKeyHeld(_boxScaleKey);
                    bool fontKey = _fontKey != null && _fontKey.Value != KeyCode.None && SR.ComboKeyHeld(_fontKey);
                    bool fontWant = (_fontEnabled == null || _fontEnabled.Value) && fontKey;
                    if (fontWant) {
                        int step = wheel > 0f ? 1 : -1;
                        int nv = Mathf.Clamp((_fontSize != null ? _fontSize.Value : 0) + step, 0, 48);
                        if (_fontSize != null && _fontSize.Value != nv) _fontSize.Value = nv;
                        WheelFrame = Time.frameCount;
                    } else if (scaleKey) {
                        float step = wheel > 0f ? 0.05f : -0.05f;
                        float nv = Mathf.Clamp((_boxScale != null ? _boxScale.Value : 1f) + step, 0.6f, 1.6f);
                        if (_boxScale != null && Mathf.Abs(_boxScale.Value - nv) > 0.0001f) _boxScale.Value = nv;
                        WheelFrame = Time.frameCount;
                    }
                }

                //3) 缩放（整体 localScale）
                float scale = _boxScale != null ? Mathf.Clamp(_boxScale.Value, 0.6f, 1.6f) : 1f;
                if (holder != null && Mathf.Abs(holder.localScale.x - scale) > 0.0001f) holder.localScale = new Vector3(scale, scale, 1f);

                //4) 字号 / 清晰度 / 保留条数 / 位置
                if (holder != null) { ApplyFont(holder); ApplyClarity(holder); }
                ApplyPosition(cg);

                DiagOnce(holder, cg);
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口增强", __ex); }
        }

        //字号：0 = 恢复游戏原字号。写游戏自己的 GameSettings.ChatMessageFontSize（新消息也会套用）
        private static void ApplyFont(RectTransform holder) {
            bool on = _fontEnabled == null || _fontEnabled.Value;
            int want = on && _fontSize != null ? _fontSize.Value : 0;
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null) {
                    if (_origGameFontSize < 0) _origGameFontSize = gs.ChatMessageFontSize;
                    int target = want > 0 ? want : _origGameFontSize;
                    if (target > 0 && gs.ChatMessageFontSize != target) gs.ChatMessageFontSize = target;
                }
                int id = holder.GetInstanceID();
                if (id != _fontHolderId) { _fontHolderId = id; _origFontSize = 0; _origBestFit = false; }
                Text[] texts = holder.GetComponentsInChildren<Text>(true);
                for (int i = 0; i < texts.Length; i++) {
                    Text t = texts[i];
                    if (t == null) continue;
                    if (_origFontSize <= 0 && t.fontSize > 0) { _origFontSize = t.fontSize; _origBestFit = t.resizeTextForBestFit; }
                    if (want > 0) {
                        if (t.resizeTextForBestFit) t.resizeTextForBestFit = false;
                        if (t.fontSize != want) t.fontSize = want;
                    } else if (_origFontSize > 0) {
                        if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                        if (t.fontSize != _origFontSize) t.fontSize = _origFontSize;
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口字号", __ex); }
        }

        //清晰度：pixelPerfect（+ 有 CanvasScaler 时才写 dynamicPixelsPerUnit）；系数变化时重建字库
        private static void ApplyClarity(RectTransform holder) {
            try {
                Canvas canvas = holder.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                if (!_havePp) { _origPp = canvas.pixelPerfect; _havePp = true; } //先记原值，再改
                if (_crisp != null && canvas.pixelPerfect != _crisp.Value) canvas.pixelPerfect = _crisp.Value;
                //注：本游戏这块画布挂的是自定义 SafeAreaScaler（没有 CanvasScaler），
                //所以只做能生效的两件事：pixelPerfect（像素对齐）+ 字号取整（见 ApplyFont）。
                float v = 1f; //保留一个"变化检测"用的常量：开关切换时会重建一次字库
                if (Mathf.Abs(_lastClarity - v) > 0.001f) {
                    _lastClarity = v;
                    Text[] texts = holder.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++) { if (texts[i] != null) texts[i].SetAllDirty(); }
                }
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口清晰度", __ex); }
        }

        //总开关关掉时的一次性还原：游戏字号 / 每段文字的字号与 bestFit / 聊天框缩放 / 画布 pixelPerfect /
        //我们施加过的位置偏移，全部交回游戏原值，然后不再插手（对应「启用增强」的说明）。
        private static void RestoreVanilla(RectTransform holder, CanvasGroup cg) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs != null && _origGameFontSize > 0 && gs.ChatMessageFontSize != _origGameFontSize)
                    gs.ChatMessageFontSize = _origGameFontSize;
                if (holder != null) {
                    if (Mathf.Abs(holder.localScale.x - 1f) > 0.0001f) holder.localScale = Vector3.one;
                    if (_origFontSize > 0) {
                        Text[] texts = holder.GetComponentsInChildren<Text>(true);
                        for (int i = 0; i < texts.Length; i++) {
                            Text t = texts[i];
                            if (t == null) continue;
                            if (t.resizeTextForBestFit != _origBestFit) t.resizeTextForBestFit = _origBestFit;
                            if (t.fontSize != _origFontSize) t.fontSize = _origFontSize;
                        }
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
        //关掉开关时，只把我们的偏移减掉一次，然后完全不再碰位置 —— 之前写成"每帧还原到最初位置"，
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
                else if ((cur - _posLast).sqrMagnitude > 0.25f) { _posBase = cur - new Vector2(_posLastDx, _posLastDy); } //游戏自己动了 → 反推基准
                want = _posBase + new Vector2(dx, dy);
                if ((cur - want).sqrMagnitude > 0.0001f) node.anchoredPosition = want;
                _posLast = node.anchoredPosition;
                _posLastDx = dx; _posLastDy = dy;
                _posApplied = true;
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口位置", __ex); }
        }

        //一次性诊断
        private static void DiagOnce(RectTransform holder, CanvasGroup cg) {
            if (_diagDone || holder == null) return;
            if (holder.childCount == 0) return;
            _diagDone = true;
            try {
                Canvas canvas = holder.GetComponentInParent<Canvas>();
                Text[] texts = holder.GetComponentsInChildren<Text>(true);
                string fontInfo = "";
                if (texts.Length > 0 && texts[0] != null && texts[0].font != null) {
                    fontInfo = texts[0].font.name + " size=" + texts[0].fontSize + " bestFit=" + texts[0].resizeTextForBestFit;
                }
                GameSettings gs = GameSettings.GetInstance();
                MainPlugin.ModLogger.LogInfo("[ChatWindow] 诊断：holder=" + holder.name + " size=" + holder.rect.width + "x" + holder.rect.height
                    + " 子项=" + holder.childCount + " alpha=" + (cg != null ? cg.alpha.ToString() : "?")
                    + " canvas=" + (canvas != null ? canvas.name + "/" + canvas.renderMode + " scaleFactor=" + canvas.scaleFactor + " pixelPerfect=" + canvas.pixelPerfect : "?")
                    + " font=" + fontInfo + " scale=" + holder.localScale.x
                    + (gs != null ? (" | 游戏设置：ChatMessageFontSize=" + gs.ChatMessageFontSize + " maxVisibleMessages=" + gs.maxVisibleMessages) : ""));
            } catch (Exception __ex) { SR.Guard.Log("聊天窗口诊断", __ex); }
        }
    }
}
