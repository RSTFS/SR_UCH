// 快速调整：分数折扣、记分板 handicap 刷新、快速切换、快速重试、树屋/局内自杀。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
// 分数折扣、记分板 handicap 刷新、快速切换、快速重试（含两个 Character 补丁）；ITweak 由 MainPlugin 反射初始化。
    // 重生相关逻辑并入 QuickAdjust（同一个 DLL / 同一页），所以不再单独注册成功能。
    internal static partial class RespawnTools {
        private const string Sec = "Quick Adjust";
        private static ConfigEntry<float> _delay;          // 重生延迟（0-3 秒）
        private static ConfigEntry<float> _invincible;     // 出生无敌（0-10 秒）
        private static ConfigEntry<bool> _cannonRespawn;   // 炮弹卡死自动重生
        private static readonly Dictionary<int, float> _deadSince = new Dictionary<int, float>();
        private static float _cannonStuckAt = -1f;

        // 顶层补丁类（RespawnDelayTimingPatch）读它。
        internal static float RespawnDelaySeconds { get { return DelaySeconds; } }

        // QuickAdjust 的自动重试用来判断"还要等多久"。
        internal static float DelaySeconds { get { try { return _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0f; } catch { return 0f; } } }

        // 模式门控一律走契约统一入口：它已含 EX「超级模式（无视模式限制）」豁免。
        // 直接读 GameSettings.GameMode 的功能在超级模式下会失效（用户要求：所有模式限制都要能被超级模式无视）。
        private static bool ModeOk() {
            try { return SRApi.GateModeAllows(SRApi.ModeMask.Challenge | SRApi.ModeMask.Freeplay); }
            catch (Exception __ex) { SRApi.Guard.Log("重生/模式判定", __ex); return false; }
        }

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Respawn Delay", "重生延迟", null);
            SRApi.LocDesc(Sec, "Respawn Delay", "重生延迟\n死亡后等多少秒再自动重生\n默认值就是游戏原版数值；0 = 立即重生", "Respawn delay\nseconds to wait after death before respawning automatically\ndefaults to the game's own value; 0 = respawn instantly");
            SRApi.LocKey(Sec, "Spawn Invincible", "出生无敌", null);
            SRApi.LocDesc(Sec, "Spawn Invincible", "出生无敌\n每次出生后无敌多少秒\n挑战/自由模式；0 = 不加无敌", "Spawn invincibility\nseconds of invincibility after each spawn\nchallenge and freeplay; 0 = none");
            SRApi.LocKey(Sec, "Cannon Auto Respawn", "炮弹卡死重生", null);
            SRApi.LocDesc(Sec, "Cannon Auto Respawn", "炮弹卡死重生\n死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "Respawn when stuck in a cannon\nrespawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay");
        }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                // 默认 -1 = "还没同步"：第一次进入游戏时会把**游戏原版数值**填进来（见 RespawnDelayTimingPatch）。
                // 用户明确要求：这个功能的默认值就是原版数值，而不是"0 = 原版"。
                _delay = plugin.Config.Bind(Sec, "Respawn Delay", 0.5f,
                    new ConfigDescription("重生延迟（0-3 秒）。默认值会自动取游戏原版数值；0 = 立即重生", new AcceptableValueRange<float>(-1f, 3f)));
                _invincible = plugin.Config.Bind(Sec, "Spawn Invincible", 1f, new ConfigDescription("出生无敌（0-10 秒）", new AcceptableValueRange<float>(0f, 10f)));
                _cannonRespawn = plugin.Config.Bind(Sec, "Cannon Auto Respawn", false, "炮弹卡死：死亡且卡在炮弹里超过 1.5 秒自动重生。");
                SRApi.RegisterTick("重生-轮询", Tick);
                try { Harmony.CreateAndPatchAll(typeof(RespawnTools)); }
                catch (Exception __h) { SRApi.Guard.Log("重生/补丁注册", __h); }
                // 重生延迟的计时阈值补丁单独注册（失败只丢那一条，并留一条能定位的警告）。
                try { Harmony.CreateAndPatchAll(typeof(RespawnDelayTimingPatch)); }
                catch (Exception __h2) { SRApi.LogWarn("重生延迟/计时阈值补丁跳过: " + __h2.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/初始化", __ex); }
        }

        private static Character LocalCharacter() {
            try {
                foreach (Character c in SRApi.FrameObjects<Character>()) {
                    if (c == null) continue;
                    if (c.hasAuthority) return c;
                    Player lp = c.LocalPlayer;
                    if (lp != null && lp.PlayerCharacter == c) return c;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/查找本地角色", __ex); }
            return null;
        }

        private static void Tick() {
            try {
                if (!SRApi.GateMaster || !ModeOk()) { _cannonStuckAt = -1f; return; }
                Character c = LocalCharacter();
                if (c == null) return;
                bool dead = c.Dead || c.Dying || c.LocallyDead;
                if (!dead) { _cannonStuckAt = -1f; return; }

                // 炮弹卡死：死了还留在炮弹里超过 1.5 秒 -> 重生
                if (_cannonRespawn != null && _cannonRespawn.Value && c.InCannon) {
                    if (_cannonStuckAt < 0f) _cannonStuckAt = Time.unscaledTime;
                    else if (Time.unscaledTime - _cannonStuckAt > 1.5f) {
                        _cannonStuckAt = -1f;
                        RespawnNow(c, "炮弹卡死");
                    }
                } else {
                    _cannonStuckAt = -1f;
                }

            } catch (Exception __ex) { SRApi.Guard.Log("重生/轮询", __ex); }
        }

        internal static void SetDelayFromVanilla(float v) {
            if (_delay == null) return;
            RespawnTools.SyncedVanillaDelay = v;
            _delay.Value = Mathf.Clamp(Mathf.Round(v * 10f) / 10f, 0f, 3f);
        }

        // 自由模式的重生延迟：见文件末尾 RespawnDelayTimingPatch。
        // 参考 RespawnControl（D:\CC\RespawnControl-main.zip）：自由模式的重生时刻由
        // Character.maxDeathDelay / minDeathDelay / minSkipDeathTime 决定，
        // 直接改这三个值就是原生延迟；不需要拦任何重生调用。

        // 重生延迟：游戏每次自动重都会走 Character.AttemptRespawn（反编译 61563）。
        // 延迟窗口内直接抑制它，窗口过后放行由游戏自己重生；这样"重生延迟"才真的作用于重生本身，
        // 而不只是自动重试的等待时间。
        [HarmonyPatch(typeof(Character), "AttemptRespawn")]
        [HarmonyPrefix]
        private static bool HoldRespawn(Character __instance, ref bool __result) {
            try {
                if (!SRApi.GateMaster || !ModeOk()) return true;
                float d = DelaySeconds;
                if (d <= 0.01f) return true;
                if (__instance == null) return true;
                Character __lc = LocalCharacter();
                if (__lc != null && __lc != __instance) return true;   // 只延迟本地角色
                int num = __instance.networkNumber;
                float since;
                if (!_deadSince.TryGetValue(num, out since)) { _deadSince[num] = Time.unscaledTime; __result = false; return false; }
                if (Time.unscaledTime - since < d) { __result = false; return false; }
                _deadSince.Remove(num);
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("重生/延迟抑制", __ex); return true; }
        }

        private static void RespawnNow(Character c, string why) {
            try {
                if (c == null) return;
                SRApi.LogInfo("[重生] " + why + "：发起重生");
                c.CallCmdRespawn();     // 游戏原生命令，房主/客户端均由服务器裁决
            } catch (Exception __ex) { SRApi.Guard.Log("重生/发起重生", __ex); }
        }

        // 出生无敌：Character.Enable 后给本地角色挂无敌计时（挑战/自由模式）
        [HarmonyPatch(typeof(Character), "Enable")]
        [HarmonyPostfix]
        private static void AfterEnable(Character __instance) {
            try {
                if (_invincible == null || !SRApi.GateMaster || !ModeOk()) return;
                float t = Mathf.Clamp(_invincible.Value, 0f, 10f);
                if (t <= 0.01f || __instance == null) return;
                Player lp = __instance.LocalPlayer;
                bool mine = __instance.hasAuthority || (lp != null && lp.PlayerCharacter == __instance);
                if (!mine) return;
                __instance.StartInvincibleTimer(t);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/出生无敌", __ex); }
        }

    }

[SrFeature("sr.quickadjust", "QuickAdjust", "1.0")]
public partial class QuickAdjust : ITweak {

        private static ConfigEntry<int> _handicap;
        private static ConfigEntry<bool> _moreHandicap;
    // 输入框草稿：编辑期间不写配置、不钳制，点"应用"（或回车）时才提交并钳制。
    private static string _draftText;
    private static int _draftFrame = -1;
    private static ConfigEntry<bool> _quickSwitchEnabled;
    private static ConfigEntry<float> _quickSwitchHold;
    // 我们改过的 SwitchTime 原值：功能关掉时逐个还原成 prefab 序列化值，做到"像没改过"。
    private static readonly Dictionary<PiecePlacementCursor, float> _switchTimeOrig = new Dictionary<PiecePlacementCursor, float>();
    private static void RestoreSwitchTime() {
        if (_switchTimeOrig.Count == 0) return;
        List<PiecePlacementCursor> done = null;
        foreach (KeyValuePair<PiecePlacementCursor, float> kv in _switchTimeOrig) {
            PiecePlacementCursor pc = kv.Key;
            if (pc != null && Mathf.Abs(pc.SwitchTime - kv.Value) > 0.001f) pc.SwitchTime = kv.Value;
            if (done == null) done = new List<PiecePlacementCursor>();
            done.Add(pc);
        }
        if (done != null) for (int i = 0; i < done.Count; i++) _switchTimeOrig.Remove(done[i]);
    }
    private static ConfigEntry<bool> _quickRetryEnabled;
    private static ConfigEntry<bool> _quickRetryFast;
    private static ConfigEntry<float> _quickRetryHoldTime;
    // 计分板刷新用的反射字段缓存。
    private static FieldInfo _gsbField;
    private static FieldInfo _relField;
    private static int _switchTimeFrame = -1; // SwitchTime 整表同步按帧只做一次
    // 长按 B 阈值文案按帧缓存：Render() 每帧至少被调两次，且计算需遍历 PlayerManager 拼字符串。
    // SwitchTime 是 prefab 序列化值，运行期不变，同帧内可直接复用。
    private static string _vanSwCache;    // forSwitch = true 的结果
    private static string _vanRetryCache; // forSwitch = false 的结果
    private static int _vanFrame = -1;

    private static void SelfReg() {
        // 本文件反射的游戏私有成员，改名时启动自检会在日志里告警。
        SRApi.Ref.Override("VersusControl.graphScoreBoardInstance", "评分折扣（取计分板实例）");
        SRApi.Ref.Override("GraphScoreBoard.scorelineRelation", "评分折扣（分数条位置）");
        SRApi.LocSec("Quick Adjust", "快速调整", null);
        SRApi.Nav("Quick Adjust", 30); // 侧栏栏目顺序 30
        SRApi.RegisterPage("Quick Adjust", "qa", "快速调整", "Quick adjust", 10, Render);   // 第 1 页：本功能页自绘
        SRApi.RegisterPage("Quick Adjust", "challenge", "重生", "Respawn", 20, RenderChallengePage);   // 第 2 页：挑战 + 重生两个分区
        SRApi.LocKey("Quick Adjust", "Quick Switch", "快速切换", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch", "快速切换\n自由模式下长按切换键在行动与建造间切换\n仅自由模式生效", "Quick switch\nin freeplay, hold the key to swap action and build\nfreeplay only");
        SRApi.LocKey("Quick Adjust", "Quick Switch Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch Hold", "快速切换长按时间\n长按多少秒触发切换\n范围 0-2，0 为立即", "Quick switch hold\nseconds to hold before switching\nrange 0-2; 0 = instant");
        SRApi.LocKey("Quick Adjust", "Quick Retry", "死后自动重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry", "死后自动重试\n挑战模式死亡后自动重开关卡\n仅挑战模式生效", "Auto retry\nrestarts the run automatically after death in challenge mode\nchallenge mode only");
        SRApi.LocKey("Quick Adjust", "Quick Retry Fast", "快速重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Fast", "快速重试\n死亡后长按重试键到设定秒数触发重试\n仅挑战模式；长按时间见下一项", "Quick retry\nhold the retry key for the set time after death\nchallenge mode only; hold time below");
        SRApi.LocKey("Quick Adjust", "Quick Retry Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Hold", "快速重试长按时间\n死亡后长按多久触发重试\n范围 0-2，0 为立即", "Quick retry hold\nseconds to hold after death\nrange 0-2; 0 = instant");
        SRApi.LocKey("Quick Adjust", "Handicap", "评分百分比", null);
        SRApi.LocDesc("Quick Adjust", "Handicap", "评分百分比\n直接设置自己那行的评分百分比\n范围 10-100 整十；100 即不折扣", "Score percentage\nsets your own row's score percentage\n10-100 in tens; 100 = no discount");
        SRApi.LocKey("Quick Adjust", "More Handicap Values", "更多数值", null);
        SRApi.LocDesc("Quick Adjust", "More Handicap Values", "自由输入百分比\n评分百分比改为输入框而非整十滑块\n范围 10-100 任意整数", "Free percentage input\nreplaces the tens slider with an input box\nany integer 10-100");
        // 预登记本页自动快捷键。
        SRApi.HotkeyAction("qa.apply_discount", "应用折扣", () => QuickAdjust.ApplyScoreDiscount());
        SRApi.HotkeyAction("qa.restore_discount", "重置为不折扣", () => QuickAdjust.RestoreScoreDiscount());
    }

    public static void Render() {
        try {
        // 每对 BeginHorizontal/EndHorizontal 之间都有会抛异常的控件，任一抛出会使 IMGUI 布局栈失衡，
        // 故整段包 try/catch。
        SRApi.Ctl.SectionTitle(SRApi.T("- 评分百分比 -", "- Score percentage -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase disc = SRApi.Ctl.FindEntry("Quick Adjust", "Handicap");
        int discVal = QuickAdjust.HandicapValue;
        // 悬浮说明：直接给标签挂 tooltip（本页自定义控件不走条目自动提示）。
        GUIContent __dct = SRApi.Ctl.Content(SRApi.T("评分百分比", "Score percentage"),
            SRApi.T("当前评分百分比\n直接设置自己那行的评分百分比（10-100）\n等效折扣 = 100 减该数值，100 即不折扣。",
                "current score percentage\nsets your own score percentage directly (10-100)\nequivalent discount = 100 minus this value, 100 = no discount"));
        float __dcw = Mathf.Max(SRApi.Ctl.Sc(84), SRApi.Ctl.LabelWrap.CalcSize(new GUIContent(__dct.text)).x + SRApi.Ctl.Sc(8));
        GUILayout.Label(__dct, SRApi.Ctl.LabelWrap, GUILayout.Width(__dcw), GUILayout.Height(SRApi.Ctl.Sc(52)));
        int slideVal = Mathf.Clamp(discVal, 10, 100);
        if (QuickAdjust.MoreHandicapOn) {
            // 自由输入：编辑期间只更新草稿，不写配置也不钳制（否则"85"打到"8"就被钳成 10）。
            string cur = _draftText ?? slideVal.ToString();
            string nvText = SRApi.ClearableTextField(cur, SRApi.Ctl.SearchBox, GUILayout.Width(SRApi.Ctl.Sc(90)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            if (nvText != cur) { _draftText = nvText; _draftFrame = Time.frameCount; }
            Event __ev = Event.current;
            if (__ev != null && __ev.type == EventType.KeyDown && (__ev.keyCode == KeyCode.Return || __ev.keyCode == KeyCode.KeypadEnter)) {
                QuickAdjust.CommitDraft(disc);
                __ev.Use();
            }
        } else {
            Rect sr = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(200), SRApi.Ctl.Sc(28));
            float nv = SRApi.Ctl.DrawSlider(sr, slideVal, 10f, 100f, true);
            int nslide = Mathf.Clamp(Mathf.RoundToInt(nv / 10f) * 10, 10, 100);
            if (nslide != slideVal && disc != null) SRApi.Ctl.SetValue(disc, nslide);
            _draftText = null;   // 滑块模式以配置为准
            GUILayout.Label(nslide + "%", SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(56)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("qa.apply_discount", SRApi.T("应用折扣", "Apply"), SRApi.T("把评分百分比设成上面的数值.\n滑块 10 到 100 整十走, 100 不折扣, 10 相当于减 90%.\n这一局立刻生效, 平衡板上能看到.", "Set your score percentage to the value above.\nSlider goes 10-100 in tens: 100 = no discount, 10 = 90% off.\nTakes effect immediately and shows on the balancer."), QuickAdjust.ApplyScoreDiscount, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(30))) {
            QuickAdjust.CommitDraft(SRApi.Ctl.FindEntry("Quick Adjust", "Handicap"));
            QuickAdjust.ApplyScoreDiscount();
        }
        GUILayout.Space(SRApi.Ctl.Sc(8));
        if (SRApi.Ctl.HotkeyButton("qa.restore_discount", SRApi.T("还原折扣", "Restore"), SRApi.T("把评分百分比恢复为 100%（不折扣）.\n等价于滑块拖到 100 再应用.\n只影响自己那一行.", "Restore your score percentage to 100% (no discount).\nSame as setting the slider to 100 and applying.\nAffects only your own row."), QuickAdjust.RestoreScoreDiscount, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(30))) {
            QuickAdjust.RestoreScoreDiscount();
        }
        // 「更多数值」开关：紧接在「还原折扣」右边同一行（不再占滑块那一行）。
        GUILayout.Space(SRApi.Ctl.Sc(10));
        ConfigEntryBase more = SRApi.Ctl.FindEntry("Quick Adjust", "More Handicap Values");
        if (more != null) {
            SRApi.Ctl.RestoreLabel(SRApi.Ctl.Content(SRApi.T("更多数值", "More values"),
                SRApi.T("自由输入模式开关\n开启后评分百分比由整十滑块改为输入框\n范围 10 到 100 任意整数。",
                        "free input mode switch\nturns the tens slider into an input box\nany integer from 10 to 100")),
                more, SRApi.Ctl.Sc(110), SRApi.Ctl.Sc(26));
            SRApi.Ctl.RenderControl(more);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        SRApi.Ctl.SectionTitle(SRApi.T("- 快速切换 -", "- Quick switch -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qs = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Switch");
        SRApi.Ctl.RestoreLabel(SRApi.Ctl.Content(SRApi.T("快速切换", "Quick switch"), SRApi.T("仅自由模式：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n长按时间在下方设置，对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action<->Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nThe hold time is set below; takes effect immediately.")), qs, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (qs != null) SRApi.Ctl.RenderControl(qs);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        ConfigEntryBase qsh = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Switch Hold");
        string vanillaSwitch = QuickAdjust.VanillaHoldBText(true);
        GUILayout.Label(SRApi.Ctl.Content(SRApi.T("等待秒数", "Hold s"), SRApi.T("自由模式长按 B 的阈值：行动->建造 = Character.SuicideTime，游戏默认 " + vanillaSwitch + "；建造->行动 = PiecePlacementCursor.SwitchTime。",
              "Freeplay hold-B thresholds: action->build = Character.SuicideTime, vanilla " + vanillaSwitch + "; build->action = PiecePlacementCursor.SwitchTime.")),
            SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(84)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        if (qsh != null) {
            float shv = (float)qsh.BoxedValue;
            Rect shr = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(150), SRApi.Ctl.Sc(28));
            float shn = SRApi.Ctl.DrawSlider(shr, shv, 0f, 2f, false);
            shn = Mathf.Round(shn * 10f) / 10f;
            // 值变了就写
            if (Mathf.Abs(shn - shv) > 0.0001f) SRApi.Ctl.SetValue(qsh, shn);
            // 中英文相同，拼一次即可。
            string shs = shn.ToString("0.0") + "s";
            GUILayout.Label(shs, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(44)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(6));

        } catch (Exception __ex) { SRApi.Guard.Log("快速调整页 渲染", __ex); }
    }

    // 第 2 页：挑战（自动重试 + 快速重试 + 等待秒数）。原来在「关卡 → 挑战」，现归到快速调整。
        // 本页标签列宽：把本页所有标签一次性量出来取最大值，所有行共用。
        // 不用核心的两帧收敛版（换页第一帧会差一截、看起来没对齐），这里确定性更强。
        private static float _pageLabelW;
        private static string _pageLabelLang;
        internal static float PageLabelWidth() {
            try {
                string lang = SRApi.T("z", "e");
                if (_pageLabelW > 0f && lang == _pageLabelLang) return _pageLabelW;
                _pageLabelLang = lang;
                string[] labels = new string[] {
                    SRApi.T("死后自动重试", "Auto retry on death"),
                    SRApi.T("快速重试", "Quick retry"),
                    SRApi.T("等待秒数", "Hold s"),
                    SRApi.T("死亡无计分板", "No death scoreboard"),
                    SRApi.T("重生延迟", "Respawn delay"),
                    SRApi.T("出生无敌", "Spawn invincibility"),
                    SRApi.T("炮弹卡死重生", "Cannon auto respawn"),
                };
                float w = SRApi.Ctl.Sc(60f);
                for (int i = 0; i < labels.Length; i++)
                    w = Mathf.Max(w, SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(labels[i])).x + SRApi.Ctl.Sc(10f));
                _pageLabelW = Mathf.Min(w, SRApi.Ctl.Sc(220f));
                return _pageLabelW;
            } catch { return SRApi.Ctl.Sc(90f); }
        }

        // 行统一入口：自动带上本页标签列宽
        internal static void PageRow(string label, string tip, ConfigEntryBase e) {
            SRApi.Ctl.LabeledRow(label, tip, e, PageLabelWidth() / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f)));
        }
        internal static void PageSlider(string label, string tip, ConfigEntryBase e, float min, float max, string fmt) {
            // 契约签名：label, tip, entry, min, max, fmt, labelWidth
            SRApi.Ctl.RowSlider(label, tip, e, min, max, fmt, PageLabelWidth() / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f)));
        }

    public static void RenderChallengePage() {
        try {
            SRApi.Ctl.SectionTitle("- " + SRApi.T("挑战", "Challenge") + " -",
                SRApi.T("挑战模式的自动重试与长按重试；两套都只影响挑战模式。",
                        "Auto retry and hold-to-retry for challenge mode; both only affect challenge runs."));
            GUILayout.Space(SRApi.Ctl.Sc(2));

            ConfigEntryBase qr = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry");
            if (qr != null) PageRow(SRApi.T("死后自动重试", "Auto retry on death"),
                SRApi.T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n对局中开关立即生效。",
                        "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nTakes effect immediately."),
                qr);   // 标签宽度交给核心按本页标签收敛，不再写死

            ConfigEntryBase qrf = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Fast");
            if (qrf != null) PageRow(SRApi.T("快速重试", "Quick retry"),
                SRApi.T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n长按时间在下方设置，对局中开关立即生效。",
                        "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nThe hold time is set below; takes effect immediately."),
                qrf);   // 同上：写死会让本页各行左边缘不齐

            ConfigEntryBase qrh = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Hold");
            if (qrh != null) PageSlider(SRApi.T("等待秒数", "Hold s"),
                SRApi.T("长按 B 的等待阈值（Character.SuicideTime）：挑战模式重试游戏默认 0.5 秒。\n范围 0-2，0.1 步进",
                        "Hold-B threshold (Character.SuicideTime): vanilla 0.5s for challenge retry.\nrange 0-2 in 0.1 steps"),
                qrh, 0f, 2f, "0.0");
            // 分区二：重生（与挑战合并到同一页，分区在挑战下面）
            RespawnTools.RenderRespawnSection();
        } catch (Exception __ex) { SRApi.Guard.Log("快速调整/挑战页", __ex); }
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
            // 走契约 IFeatureHost.Config：把 host 硬转成 BaseUnityPlugin 会在非 BaseUnityPlugin 宿主下抛
            // InvalidCastException（被 MainPlugin 兜住，但本功能静默消失）。
            ConfigFile cfg = plugin.Config;
        _handicap = cfg.Bind<int>("Quick Adjust", "Handicap", 100, "当前 handicap：直接设置自己那行的 handicap（范围 10-100，整十；等效折扣 = 100 - 该值）。");
        _moreHandicap = cfg.Bind<bool>("Quick Adjust", "More Handicap Values", false, "自由输入数值：选中后 handicap 滑块变为输入框（10-100 任意整数）。");
            _quickSwitchEnabled = cfg.Bind<bool>("Quick Adjust", "Quick Switch", false, "快速切换（仅自由模式）：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
            _quickSwitchHold = cfg.Bind<float>("Quick Adjust", "Quick Switch Hold", 0.5f, "快速切换的长按等待秒数（0-2，0=立即；自由模式长按 B 达到该秒数切换行动/建造）。");
            _quickRetryEnabled = cfg.Bind<bool>("Quick Adjust", "Quick Retry", false, "死后自动重试（仅挑战模式）：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。");
            _quickRetryFast = cfg.Bind<bool>("Quick Adjust", "Quick Retry Fast", false, "快速重试（仅挑战模式）：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
            _quickRetryHoldTime = cfg.Bind<float>("Quick Adjust", "Quick Retry Hold", 0.5f, "快速重试的长按等待秒数（0-2，0=立即；挑战模式死亡后长按 B 达到该秒数自动重试）。");
        // 本类自带 [HarmonyPatch] 但 ITweak 为反射发现，须手动 CreateAndPatchAll；单个目标失效只丢本类功能。
            try { RespawnTools.Initialize(plugin); } catch (Exception __rs) { SRApi.Guard.Log("快速调整/重生初始化", __rs); }
            try { SRApi.RegisterTick("评分百分比-保持", TickKeepHandicap); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try { Harmony.CreateAndPatchAll(typeof(QuickAdjust), (string)null); }
            catch (Exception e) { SRApi.LogError("快速调整 补丁注册失败: " + e.Message); }
            // 不再注册 GamePlayer.OnStartClient / OnStartServer 补丁：这两个成员在当前游戏版本里
            // 根本不存在（每次启动都刷两条"补丁跳过"警告）。评分百分比改为 TickKeepHandicap
            // 每帧按"本地值 != 目标值"重下发，不依赖任何生命周期钩子。
    }

        public static bool QuickSwitchOn => _quickSwitchEnabled != null && _quickSwitchEnabled.Value;

        public static float QuickSwitchHold => (_quickSwitchHold != null) ? Mathf.Clamp(_quickSwitchHold.Value, 0f, 2f) : 0.5f;

        public static bool QuickRetryOn => _quickRetryEnabled != null && _quickRetryEnabled.Value;

        public static bool QuickRetryFastOn => _quickRetryFast != null && _quickRetryFast.Value;

        public static float QuickRetryHold => (_quickRetryHoldTime != null) ? Mathf.Clamp(_quickRetryHoldTime.Value, 0f, 2f) : 0.5f;

        // 提交草稿：只有在这里才钳制（10-100），并把最终值写回配置。
        public static void CommitDraft(ConfigEntryBase entry) {
            try {
                string txt = _draftText;
                _draftText = null;
                if (string.IsNullOrEmpty(txt)) return;
                int v;
                if (!int.TryParse(txt.Trim(), out v)) { SRApi.Notify(SRApi.T("评分百分比必须是数字", "Score percentage must be a number")); return; }
                int clamped = Mathf.Clamp(v, 10, 100);
                if (clamped != v) SRApi.Notify(SRApi.T("评分百分比已钳制到 ", "Score percentage clamped to ") + clamped + "%");
                if (entry != null) SRApi.Ctl.SetValue(entry, clamped);
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.CommitDraft", __ex); }
        }

        public static int HandicapValue => (_handicap != null) ? Mathf.Clamp(_handicap.Value, 10, 100) : 100;
        public static bool MoreHandicapOn => _moreHandicap != null && _moreHandicap.Value;


        // 局内改 handicap 不会刷新计分板（游戏只在开局 SetPlayerCharacter 时调 ScoreLine.SetHandicap），
        // 故 patch set_NetworkHandicap + OnDeserialize，仅在值变化时刷新。
        private static readonly Dictionary<GamePlayer, int> _lastHandicapShown = new Dictionary<GamePlayer, int>();



        // 重生延迟判定（读「重生」页写的配置条目，失败按"不等待"处理）
        private static readonly Dictionary<int, float> _deadSinceMap = new Dictionary<int, float>();
        // 快速重试已发标记（闩锁）：按 networkNumber 记，见 ForceSuicidalState。
        private static readonly HashSet<int> _retrySentTo = new HashSet<int>();

        // 评分百分比的"保持"不再依赖 GamePlayer 生成钩子（那些成员在本版本游戏里不存在）：
        // TickKeepHandicap 每帧比对本地值与配置值，不一致就重下发（≤1 次/秒）。

        private static float _keepLastAt = -999f;

        // 已应用的折扣目标（10~100；0 = 未应用）。切换关卡 / 重建 GamePlayer 后由 Tick 自动补回。
        private static int _keepHandicap;

}
}
