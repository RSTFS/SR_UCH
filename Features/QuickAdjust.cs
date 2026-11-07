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
    internal static class RespawnTools {
        private const string Sec = "Quick Adjust";
        private static ConfigEntry<float> _delay;          // 重生延迟（0-3 秒）
        private static ConfigEntry<float> _invincible;     // 出生无敌（0-10 秒）
        private static ConfigEntry<bool> _cannonRespawn;   // 炮弹卡死自动重生
        private static ConfigEntry<bool> _noScoreboard;    // 挑战死亡不弹记分板
        private static readonly Dictionary<int, float> _deadSince = new Dictionary<int, float>();
        private static float _cannonStuckAt = -1f;

        // QuickAdjust 的自动重试用来判断"还要等多久"。
        internal static float DelaySeconds { get { try { return _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0f; } catch { return 0f; } } }

        private static bool ModeOk() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return false;
                return gs.GameMode == GameState.GameMode.CHALLENGE || gs.GameMode == GameState.GameMode.FREEPLAY;
            } catch (Exception __ex) { SRApi.Guard.Log("重生/模式判定", __ex); return false; }
        }

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Respawn Delay", "重生延迟", null);
            SRApi.LocDesc(Sec, "Respawn Delay", "重生延迟\n死亡后等多少秒再自动重生\n挑战/自由模式；0 = 立即", "Respawn delay\nseconds to wait after death before respawning automatically\nchallenge and freeplay; 0 = instant");
            SRApi.LocKey(Sec, "Spawn Invincible", "出生无敌", null);
            SRApi.LocDesc(Sec, "Spawn Invincible", "出生无敌\n每次出生后无敌多少秒\n挑战/自由模式；0 = 不加无敌", "Spawn invincibility\nseconds of invincibility after each spawn\nchallenge and freeplay; 0 = none");
            SRApi.LocKey(Sec, "Cannon Auto Respawn", "炮弹卡死自动重生", null);
            SRApi.LocDesc(Sec, "Cannon Auto Respawn", "炮弹卡死自动重生\n死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "Respawn when stuck in a cannon\nrespawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay");
            SRApi.LocKey(Sec, "No Death Scoreboard", "挑战死亡不弹记分板", null);
            SRApi.LocDesc(Sec, "No Death Scoreboard", "挑战死亡不弹记分板\n挑战模式死亡时不显示记分板\n只在死亡且尚未通关时生效", "No scoreboard on death\nskips the scoreboard when you die in challenge mode\nonly while dead and not yet succeeded");
        }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _delay = plugin.Config.Bind(Sec, "Respawn Delay", 0.5f, new ConfigDescription("重生延迟（0-3 秒）", new AcceptableValueRange<float>(0f, 3f)));
                _invincible = plugin.Config.Bind(Sec, "Spawn Invincible", 1f, new ConfigDescription("出生无敌（0-10 秒）", new AcceptableValueRange<float>(0f, 10f)));
                _cannonRespawn = plugin.Config.Bind(Sec, "Cannon Auto Respawn", false, "炮弹卡死：死亡且卡在炮弹里超过 1.5 秒自动重生。");
                _noScoreboard = plugin.Config.Bind(Sec, "No Death Scoreboard", false, "挑战模式死亡时不弹记分板。");
                SRApi.RegisterTick("重生-轮询", Tick);
                try { Harmony.CreateAndPatchAll(typeof(RespawnTools)); }
                catch (Exception __h) { SRApi.Guard.Log("重生/补丁注册", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/初始化", __ex); }
        }

        // 单独的"死亡不弹记分板"行：由「挑战」分区调用
        internal static void RenderNoScoreboardRow() {
            try {
                ConfigEntryBase ns = SRApi.Ctl.FindEntry(Sec, "No Death Scoreboard");
                if (ns != null) SRApi.Ctl.LabeledRow(SRApi.T("挑战死亡不弹记分板", "No death scoreboard"),
                    SRApi.T("挑战模式死亡时不显示记分板\n只在死亡且尚未通关时生效", "skips the scoreboard when you die in challenge only while dead and not yet succeeded"), ns);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/记分板行", __ex); }
        }

        // 由「挑战」页调用：重生分区（跟在挑战分区下面）
        internal static void RenderRespawnSection() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("重生", "Respawn") + " -",
                    SRApi.T("重生延迟、出生无敌、炮弹卡死自动重生（挑战/自由通用）。",
                            "Respawn delay, spawn invincibility and cannon-stuck respawn (challenge and freeplay)."));
                if (_delay != null) SRApi.Ctl.RowSlider(SRApi.T("重生延迟", "Respawn delay"),
                    SRApi.T("死亡后等多少秒再自动重生\n游戏原生长按阈值是 0.5 秒\n挑战/自由模式", "seconds to wait after death\nvanilla hold threshold is 0.5s\nchallenge and freeplay"),
                    _delay, 0f, 3f, "0.0");
                if (_invincible != null) SRApi.Ctl.RowSlider(SRApi.T("出生无敌", "Spawn invincibility"),
                    SRApi.T("每次出生后的无敌秒数\n游戏原生重生后是 1 秒\n挑战/自由模式", "seconds of invincibility after each spawn\nvanilla gives 1s after respawn\nchallenge and freeplay"),
                    _invincible, 0f, 10f, "0.0");
                ConfigEntryBase cr = SRApi.Ctl.FindEntry(Sec, "Cannon Auto Respawn");
                if (cr != null) SRApi.Ctl.LabeledRow(SRApi.T("炮弹卡死自动重生", "Cannon auto respawn"),
                    SRApi.T("死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "respawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay"), cr);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/分区渲染", __ex); }
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

        // QuickAdjust 的自动重试调用：返回 true 表示还在延迟窗口内，本次不要发重试。
        internal static bool ShouldWaitForDelay(int networkNumber) {
            try {
                float d = DelaySeconds;
                if (d <= 0.01f) return false;
                float since;
                if (!_deadSince.TryGetValue(networkNumber, out since)) { _deadSince[networkNumber] = Time.unscaledTime; return true; }
                if (Time.unscaledTime - since < d) return true;
                _deadSince.Remove(networkNumber);
                return false;
            } catch (Exception __ex) { SRApi.Guard.Log("重生/延迟判定", __ex); return false; }
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

        // 挑战死亡不弹记分板：只在"挑战模式 + 本地角色死亡且未通关"时跳过
        [HarmonyPatch(typeof(BaseScoreboard), "Show")]
        [HarmonyPrefix]
        private static bool SkipDeathScoreboard() {
            try {
                if (_noScoreboard == null || !_noScoreboard.Value || !SRApi.GateMaster) return true;
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null || gs.GameMode != GameState.GameMode.CHALLENGE) return true;
                Character c = LocalCharacter();
                if (c == null) return true;
                bool dead = c.Dead || c.Dying || c.LocallyDead;
                return !(dead && !c.Success);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/跳过记分板", __ex); return true; }
        }
    }

[SrFeature("sr.quickadjust", "QuickAdjust", "1.0")]
public class QuickAdjust : ITweak {

        private static ConfigEntry<int> _handicap;
        private static ConfigEntry<bool> _moreHandicap;
    // 输入框草稿：编辑期间不写配置、不钳制，点"应用"（或回车）时才提交并钳制。
    private static string _draftText;
    private static int _draftFrame = -1;
    private static ConfigEntry<bool> _quickSwitchEnabled;
    private static ConfigEntry<float> _quickSwitchHold;
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
    public static void RenderChallengePage() {
        try {
            SRApi.Ctl.SectionTitle("- " + SRApi.T("挑战", "Challenge") + " -",
                SRApi.T("挑战模式的自动重试与长按重试；两套都只影响挑战模式。",
                        "Auto retry and hold-to-retry for challenge mode; both only affect challenge runs."));
            GUILayout.Space(SRApi.Ctl.Sc(2));

            ConfigEntryBase qr = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry");
            if (qr != null) SRApi.Ctl.LabeledRow(SRApi.T("死后自动重试", "Auto retry on death"),
                SRApi.T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n对局中开关立即生效。",
                        "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nTakes effect immediately."),
                qr, 140f);

            ConfigEntryBase qrf = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Fast");
            if (qrf != null) SRApi.Ctl.LabeledRow(SRApi.T("快速重试", "Quick retry"),
                SRApi.T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n长按时间在下方设置，对局中开关立即生效。",
                        "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nThe hold time is set below; takes effect immediately."),
                qrf, 140f);

            ConfigEntryBase qrh = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Hold");
            if (qrh != null) SRApi.Ctl.RowSlider(SRApi.T("等待秒数", "Hold s"),
                SRApi.T("长按 B 的等待阈值（Character.SuicideTime）：挑战模式重试游戏默认 0.5 秒。\n范围 0-2，0.1 步进",
                        "Hold-B threshold (Character.SuicideTime): vanilla 0.5s for challenge retry.\nrange 0-2 in 0.1 steps"),
                qrh, 0f, 2f, "0.0");
            // 挑战分区的最后一条：死亡不弹记分板
            RespawnTools.RenderNoScoreboardRow();
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
    }

        public static bool QuickSwitchOn => _quickSwitchEnabled != null && _quickSwitchEnabled.Value;

        public static float QuickSwitchHold => (_quickSwitchHold != null) ? Mathf.Clamp(_quickSwitchHold.Value, 0f, 2f) : 0.5f;

        public static bool QuickRetryOn => _quickRetryEnabled != null && _quickRetryEnabled.Value;

        public static bool QuickRetryFastOn => _quickRetryFast != null && _quickRetryFast.Value;

        public static float QuickRetryHold => (_quickRetryHoldTime != null) ? Mathf.Clamp(_quickRetryHoldTime.Value, 0f, 2f) : 0.5f;

        // 长按 B 阈值：自由/挑战模式固定 Character.SuicideTime = 0.5s；
        // 建造态切回行动用 PiecePlacementCursor.SwitchTime（取 prefab 序列化值，读不到按 0.5s）。
        public static string VanillaHoldBText(bool forSwitch) {
            if (_vanFrame == Time.frameCount) {
                string hit = forSwitch ? _vanSwCache : _vanRetryCache;
                if (hit != null) return hit;
            }
            string s = ComputeVanilla(forSwitch);
            if (forSwitch) _vanSwCache = s; else _vanRetryCache = s;
            _vanFrame = Time.frameCount;
            return s;
        }

        private static string ComputeVanilla(bool forSwitch) {
            string s = "0.5s";
            if (!forSwitch) return s;
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.AssociatedGamePlayer == null) continue;
                    PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                    if (pc == null || pc.SwitchTime <= 0f) continue;
                    if (Mathf.Abs(pc.SwitchTime - 0.5f) > 0.05f) {
                        string st = pc.SwitchTime.ToString("0.0");
                        s += SRApi.T("（建造->行动 " + st + "s）", " (build->action " + st + "s)");
                    }
                    break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("读取 SwitchTime 默认值", __ex); }
            return s;
        }

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



        [HarmonyPatch(typeof(GamePlayer), "set_NetworkHandicap")]
        [HarmonyPostfix]
        private static void RefreshScoreboardHandicap(GamePlayer __instance) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        // 服务端/显式设置走 set_NetworkHandicap，房客端由 OnDeserialize 兜底。
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(服务端)", __ex); }
        }

        // 房客端 SyncVar 反序列化直接写 Handicap 字段，不走 set_NetworkHandicap，须在此补刷。
        [HarmonyPatch(typeof(GamePlayer), "OnDeserialize")]
        [HarmonyPostfix]
        private static void RefreshScoreboardHandicapClient(GamePlayer __instance) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(房客)", __ex); }
        }

        // 统一入口：handicap 与上次记录不同才刷新。
        private static void RefreshScoreboardHandicapIfChanged(GamePlayer __instance) {
            try {
                if (__instance == null) return;
                int h = __instance.Handicap;
                int prev;
                if (_lastHandicapShown.TryGetValue(__instance, out prev) && prev == h) return;
                // 先刷新、成功后再记录：Core 在计分板未就绪时会提前返回，先记录会导致该值不再重试。
                if (!RefreshScoreboardHandicapCore(__instance)) return;
                _lastHandicapShown[__instance] = h;
                // 超限整体清空（场景切换后 GamePlayer 重建，旧引用残留）。
                if (_lastHandicapShown.Count > 64) _lastHandicapShown.Clear();
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
        }

        private static bool RefreshScoreboardHandicapCore(GamePlayer __instance) {
            try {
                if (__instance == null) return false;
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return false;
                GameControl gc = lm.CurrentGameController as GameControl;
                if (gc == null) return false;
                GraphScoreBoard board = null;
                try {
                    VersusControl vc = gc as VersusControl;
                    if (vc != null) {
                        if (_gsbField == null) _gsbField = SRApi.Ref.Field(typeof(VersusControl), "graphScoreBoardInstance", "评分折扣（取计分板实例）");
                        if (_gsbField != null) board = _gsbField.GetValue(vc) as GraphScoreBoard;
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
                if (board == null) {
                    board = UnityEngine.Object.FindObjectOfType<GraphScoreBoard>();
                }
                if (board == null) return false;
                if (_relField == null) _relField = SRApi.Ref.Field(typeof(GraphScoreBoard), "scorelineRelation", "评分折扣（分数条位置）");
                if (_relField == null) return false;
                object rel = _relField.GetValue(board);
                if (rel == null) return false;
                IDictionary<int, ScoreLine> relDict = rel as IDictionary<int, ScoreLine>;
                if (relDict == null) return false;
                ScoreLine line = null;
                int num = 0;
                try { num = __instance.NetworknetworkNumber; } catch { num = __instance.networkNumber; }
                if (!relDict.TryGetValue(num, out line)) {
                    try {
                        line = relDict[__instance.localNumber];
                    } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.GetValue", __ex); }
                }
                if (line == null) return false;
                line.SetHandicap(__instance.Handicap);
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.SetHandicap", __ex); return false; }
        }

        // UpdateHoldBIndicator 是设置 SuicideTime 的地方，Postfix 覆盖为配置值，并同步建造态 SwitchTime。
        [HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
        [HarmonyPostfix]
        private static void OverrideHoldTime(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.Typing) || !IsLocalCharacter(__instance)) return;
            try
            {
                // 模式门控统一走 GateModeAllows（已含 IgnoreModeLimit 豁免）。
                if (QuickSwitchOn && SRApi.GateModeAllows(SRApi.ModeMask.Freeplay))
                {
                    __instance.SuicideTime = QuickSwitchHold; // 自由模式长按 B 的切换等待
                    // 建造态切回行动用 PiecePlacementCursor.SwitchTime，须每帧同步。
                    // 一帧内多个 Character 都会进这里，整表扫描按帧只做一次。
                    if (_switchTimeFrame != Time.frameCount) {
                        _switchTimeFrame = Time.frameCount;
                        foreach (Player p in PlayerManager.GetInstance()) {
                            if (p == null || p.AssociatedGamePlayer == null) continue;
                            PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                            if (pc != null) pc.SwitchTime = QuickSwitchHold;
                        }
                    }
                }
                else if (QuickRetryFastOn && SRApi.GateModeAllows(SRApi.ModeMask.Challenge))
                {
                    __instance.SuicideTime = QuickRetryHold;
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.OverrideHoldTime", __ex); }
        }

        // 重生延迟判定（读「重生」页写的配置条目，失败按"不等待"处理）
        private static readonly Dictionary<int, float> _deadSinceMap = new Dictionary<int, float>();
        private static bool ShouldWaitRespawnDelay(int num) {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry("Quick Adjust", "Respawn Delay");
                float d = 0f;
                try { if (e != null) d = System.Convert.ToSingle(e.BoxedValue); } catch (Exception __cv) { SRApi.Guard.Log("快速调整/读重生延迟", __cv); }
                if (d <= 0.01f) { _deadSinceMap.Remove(num); return false; }
                float since;
                if (!_deadSinceMap.TryGetValue(num, out since)) { _deadSinceMap[num] = Time.unscaledTime; return true; }
                if (Time.unscaledTime - since < d) return true;
                _deadSinceMap.Remove(num);
                return false;
            } catch (Exception __ex) { SRApi.Guard.Log("快速调整/重生延迟判定", __ex); return false; }
        }

        // 是否是"我这边的角色"：房主端 hasAuthority，房客端 hasAuthority 可能为 false，
        // 但 LocalPlayer.PlayerCharacter 指向的就是自己的角色。以前只判 hasAuthority，
        // 于是"在别人房间死后不自动重试"。两个补丁都用这个判定。
        private static bool IsLocalCharacter(Character c) {
            try {
                if (c == null) return false;
                if (c.hasAuthority) return true;
                Player lp = c.LocalPlayer;
                return lp != null && lp.PlayerCharacter == c;
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust/本地角色判定", __ex); return false; }
        }

        // 快速重试已发标记（闩锁）：按 networkNumber 记，见 ForceSuicidalState。
        private static readonly HashSet<int> _retrySentTo = new HashSet<int>();

        [HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
        [HarmonyPostfix]
        private static void ForceSuicideState(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.Typing) || !IsLocalCharacter(__instance))
            {
                return;
            }
            // 模式门控统一走 GateModeAllows（已含 IgnoreModeLimit 豁免，模式取不到时一律不放行）。
            if (SRApi.GateModeAllows(SRApi.ModeMask.Challenge))
            {
                bool dead = !__instance.Success && (__instance.Dead || __instance.Dying || __instance.LocallyDead);
                int num;
                try { num = __instance.networkNumber; }
                catch { return; }
                if (!dead)
                {
                    // 不再处于死亡状态（复活/回合重置/换关）：解锁本角色闩锁。
                    _retrySentTo.Remove(num);
                    return;
                }
                // 一次性闩锁：WantsToRetry 是 SyncVar，从发出到服务器回写为真的这段时间本方法每帧都跑。
                // 按 networkNumber 记录（同局多个 Character，单个 static bool 会被队友清掉而退化成每帧发包）。
                if (_retrySentTo.Contains(num)) return;
                if (!QuickRetryOn || __instance.WantsToRetry) return;
                // 重生延迟：还在延迟窗口内就先不发送。
                // 直接读配置条目（不引用「重生」功能的类型）：拆分构建下两者在不同 DLL，编译期不能互相引用。
                if (ShouldWaitRespawnDelay(num)) return;
                _retrySentTo.Add(num);
                if (_retrySentTo.Count > 64) _retrySentTo.Clear();
                try
                {
                    MsgPlayerWantsToRetry msg = new MsgPlayerWantsToRetry { networkNumber = num };
                    UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PlayerWantsToRetry, msg);
                }
                catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
                return;
            }
            if (!SRApi.GateModeAllows(SRApi.ModeMask.Freeplay))
            {
                return;
            }
            // 自由模式：等待时间实际由 OverrideHoldTime 设置。
            if (!QuickSwitchOn) return;
            // 仅对局中生效，树屋/大厅不处理。
            try
            {
                if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
            }
            catch
            {
                return;
            }
            try { __instance.SuicideTime = QuickSwitchHold; } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
            // 建造态 SwitchTime 同样设为配置值。
            try {
                // 按帧只做一次：一帧内多个 Character 回调都会进这里。
                if (_switchTimeFrame != Time.frameCount) {
                    _switchTimeFrame = Time.frameCount;
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.AssociatedGamePlayer == null) continue;
                        PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                        if (pc != null) pc.SwitchTime = QuickSwitchHold;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
        }

        // 持久化（优化版）：新 GamePlayer 生成时记一个"待补发"时间点，Tick 到点只补一次。
        // 不常驻轮询，也不反复改数值；切关卡/重开一局都会重新触发一次。
        private static float _keepApplyAt = -1f;

        [HarmonyPatch(typeof(GamePlayer), "OnStartClient")]
        [HarmonyPostfix]
        private static void OnStartClientQueueKeep(GamePlayer __instance) { QueueKeepIfLocal(__instance); }

        [HarmonyPatch(typeof(GamePlayer), "OnStartServer")]
        [HarmonyPostfix]
        private static void OnStartServerQueueKeep(GamePlayer __instance) { QueueKeepIfLocal(__instance); }

        private static void QueueKeepIfLocal(GamePlayer gp) {
            try { if (_keepHandicap > 0 && gp != null && gp.isLocalPlayer) _keepApplyAt = Time.unscaledTime + 0.8f; }
            catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        private static void TickKeepHandicap() {
            try {
                if (_keepHandicap <= 0 || _keepApplyAt < 0f || Time.unscaledTime < _keepApplyAt) return;
                _keepApplyAt = -1f;
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null) continue;
                    LobbyPlayer lp = null;
                    try { lp = p.AssociatedLobbyPlayer; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    if (lp == null || !lp.IsLocalPlayer) continue;
                    try { if (lp.Networkhandicap != _keepHandicap) lp.SetPlayerHandicap(_keepHandicap); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    try { if (p.AssociatedGamePlayer != null && p.AssociatedGamePlayer.Handicap != _keepHandicap) p.AssociatedGamePlayer.CallCmdSetPlayerHandicap(_keepHandicap); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.TickKeepHandicap", __ex); }
        }

        // 已应用的折扣目标（10~100；0 = 未应用）。切换关卡 / 重建 GamePlayer 后由 Tick 自动补回。
        private static int _keepHandicap;

        // 每 0.5 秒检查一次本地 GamePlayer 的 handicap，被新关卡重置就重新下发（原生 Command）。
        public static void ApplyScoreDiscount()
        {
            try
            {
                if (!SRApi.GateMaster)
                {
                    SRApi.Notify(SRApi.MsgMasterOff);
                    return;
                }
                int target = Mathf.Clamp(HandicapValue, 10, 100);
                if (ApplyOwnHandicap(target))
                {
                    // 核实：读回本地 LobbyPlayer 的 handicap，值不对就明确提示（而不是静默成功）。
                    try {
                        LobbyPlayer __lp = FindLocalLobbyPlayer();
                        if (__lp != null && __lp.handicap != target) {
                            SRApi.LogWarn("[快速调整] 已下发 " + target + "%，但本地读回 " + __lp.handicap + "%（可能被服务器或规则覆盖）");
                        }
                    } catch (Exception __v) { SRApi.Guard.Log("QuickAdjust/核实 handicap", __v); }
                    // 100 也要记住：100% = 不折扣，但它是当前生效值。以前把它归零等于放弃保持，
                    // 角色重建/重生后可能被大厅里的旧值盖回去（audit C-9）。
                    _keepHandicap = target;
                    SRApi.Notify(SRApi.T("评分百分比：", "Score percentage: ") + target + "%");
                }
                else
                {
                    SRApi.Notify(SRApi.T("评分百分比应用失败", "Failed to apply score percentage"));
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("评分折扣失败: " + ex.Message));
            }
        }

        // 同时写 LobbyPlayer（平衡板/下一局）与 GamePlayer（本局立即生效）。
        // 原生方案：直接改当前房间本地玩家的 LobbyPlayer.handicap（服务器权威值，重载场景/回合都保留）。
        // 换房间 = 新的 LobbyPlayer（默认 100），折扣自然重置；不做任何每帧/每局自动重放。
        // 与树屋评分板（游戏里的 handicap 组件）完全同一套写法：
        // 1) 发原生的 PlayerHandicapSet 消息（服务器会转发给全员，客户端据此播动画/音效）；
        // 2) 调本地 LobbyPlayer.SetPlayerHandicap（原生 Command 写 SyncVar，整个房间/后续回合都保留）。
        private static bool ApplyOwnHandicap(int value)
        {
            try
            {
                value = Mathf.Clamp(value, 10, 100);
                LobbyPlayer lp = FindLocalLobbyPlayer();
                if (lp == null) return false;
                SendNativeHandicapMessage(lp, value);
                lp.SetPlayerHandicap(value);
                // 局内：同步当前 GamePlayer，让平衡板立刻刷新（原生评分板只在树屋，无此步）。
                SyncLocalGamePlayerHandicap(lp, value);
                return true;
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.ApplyOwnHandicap", __ex); }
            return false;
        }

        // 原生广播：与 game 的 handicap.sendHandicapMessage 一致（Action=NONE 只同步数值，不触发动画/音效）。
        private static void SendNativeHandicapMessage(LobbyPlayer lp, int value)
        {
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null || !lm.client.isConnected) return;
                MsgPlayerHandicapSet msg = new MsgPlayerHandicapSet();
                msg.Handicap = value;
                msg.NetworkPlayerNumber = lp.networkNumber;
                msg.Action = handicap.HandicapAction.NONE;
                lm.client.Send(NetMsgTypes.PlayerHandicapSet, msg);
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust/SendNativeHandicapMessage", __ex); }
        }

        // 取本地 LobbyPlayer：优先“本地 Player 的 AssociatedLobbyPlayer”（树屋评分板就是这么取角色对应的平衡板行），
        // 退而求其次用 PlayerTracker / 场景内 IsLocalPlayer 的 LobbyPlayer。
        private static LobbyPlayer FindLocalLobbyPlayer()
        {
            try
            {
                foreach (Player p in PlayerManager.GetInstance())
                {
                    try
                    {
                        if (p != null && p.AssociatedLobbyPlayer != null && p.AssociatedLobbyPlayer.IsLocalPlayer)
                            return p.AssociatedLobbyPlayer;
                    }
                    catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(Player)", __ex); }
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer", __ex); }
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null)
                {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++)
                    {
                        try
                        {
                            NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                            LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(info.NetworkNumber);
                            if (lp != null && lp.IsLocalPlayer) return lp;
                        }
                        catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(tracker)", __ex); }
                    }
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(tracker)", __ex); }
            try
            {
                foreach (LobbyPlayer lp in UnityEngine.Object.FindObjectsOfType<LobbyPlayer>())
                {
                    if (lp != null && lp.IsLocalPlayer) return lp;
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(scene)", __ex); }
            return null;
        }

        // 已在本局里：同步 GamePlayer，让平衡板立刻刷新（不改的话等下一局才变）。
        private static void SyncLocalGamePlayerHandicap(LobbyPlayer lp, int value)
        {
            try
            {
                foreach (Player p in PlayerManager.GetInstance())
                {
                    if (p == null || p.AssociatedLobbyPlayer != lp) continue;
                    if (p.AssociatedGamePlayer != null)
                    {
                        try { p.AssociatedGamePlayer.CallCmdSetPlayerHandicap(value); }
                        catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
                    }
                    break;
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.SyncLocalGamePlayerHandicap", __ex); }
        }

        public static void RestoreScoreDiscount()
        {
            try
            {
                if (!SRApi.GateMaster)
                {
                    SRApi.Notify(SRApi.MsgMasterOff);
                    return;
                }
                if (ApplyOwnHandicap(100))
                {
                    _keepHandicap = 0;
                    SRApi.Notify(SRApi.T("评分百分比：100%", "Score percentage: 100%"));
                }
                else
                {
                    SRApi.Notify(SRApi.T("评分百分比还原失败", "Failed to restore score percentage"));
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("恢复评分折扣失败: " + ex.Message));
            }
        }
}

}


