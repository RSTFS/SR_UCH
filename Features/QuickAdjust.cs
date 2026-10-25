// 快速调整：分数折扣、记分板 handicap 刷新、快速切换、快速重试、树屋/局内自杀。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
// 分数折扣、记分板 handicap 刷新、快速切换、快速重试（含两个 Character 补丁）；ITweak 由 MainPlugin 反射初始化。
[SrFeature("sr.quickadjust", "QuickAdjust", "1.0")]
public class QuickAdjust : ITweak {

        private static ConfigEntry<int> _handicap;
        private static ConfigEntry<bool> _moreHandicap;
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
        SRApi.RegisterPage("Quick Adjust", Render);   // 本功能页自绘
        SRApi.LocKey("Quick Adjust", "Quick Switch", "快速切换", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch", "快速切换开关\n自由模式下长按切换键在行动与建造之间切换\n仅自由模式生效，对局中开关立即生效。", "quick switch toggle\nin freeplay, hold the switch key to swap between Action and Build\nfreeplay only; the toggle applies immediately in-game");
        SRApi.LocKey("Quick Adjust", "Quick Switch Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch Hold", "快速切换长按时间\n设置长按多少秒触发切换\n范围 0 到 2，0 为立即切换", "quick switch hold time\nsets how long the key must be held to trigger the swap\nrange 0-2, 0 = instant");
        SRApi.LocKey("Quick Adjust", "Quick Retry", "死后自动重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry", "死后自动重试开关\n挑战模式死亡后自动重开关卡，无需长按\n仅挑战模式生效", "auto retry toggle\nin challenge mode, restarts the run automatically after death without holding a key\nchallenge mode only");
        SRApi.LocKey("Quick Adjust", "Quick Retry Fast", "快速重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Fast", "快速重试开关\n挑战模式死亡后长按重试键到设定秒数自动重试\n仅挑战模式生效，长按时间在下方设置。", "quick retry toggle\nin challenge mode, hold the retry key for the set time after death to retry\nchallenge mode only; the hold time is set below");
        SRApi.LocKey("Quick Adjust", "Quick Retry Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Hold", "快速重试长按时间\n设置死亡后长按多久触发重试\n范围 0 到 2，0 为立即重试", "quick retry hold time\nsets how long to hold after death before retrying\nrange 0-2, 0 = instant");
        SRApi.LocKey("Quick Adjust", "Handicap", "评分百分比", null);
        SRApi.LocDesc("Quick Adjust", "Handicap", "当前评分百分比\n直接设置自己那行的评分百分比（10-100，整十）\n等效折扣 = 100 减该数值，100 即不折扣。", "current score percentage\nsets your own score percentage directly (10-100 in tens)\nequivalent discount = 100 minus this value, 100 = no discount");
        SRApi.LocKey("Quick Adjust", "More Handicap Values", "更多数值", null);
        SRApi.LocDesc("Quick Adjust", "More Handicap Values", "自由输入模式开关\n开启后 handicap 由整十滑块改为输入框\n范围 10 到 100 任意整数。", "free input mode switch\nturns the tens slider into an input box\nany integer from 10 to 100");
        // 预登记本页自动快捷键。
        SRApi.HotkeyAction("qa.apply_discount", "应用折扣", () => QuickAdjust.ApplyScoreDiscount());
        SRApi.HotkeyAction("qa.restore_discount", "还原折扣", () => QuickAdjust.RestoreScoreDiscount());
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
        GUIContent __dct = new GUIContent(SRApi.T("评分百分比", "Score percentage"),
            SRApi.T("当前评分百分比\n直接设置自己那行的评分百分比（10-100）\n等效折扣 = 100 减该数值，100 即不折扣。",
                "current score percentage\nsets your own score percentage directly (10-100)\nequivalent discount = 100 minus this value, 100 = no discount"));
        float __dcw = Mathf.Max(SRApi.Ctl.Sc(84), SRApi.Ctl.LabelWrap.CalcSize(new GUIContent(__dct.text)).x + SRApi.Ctl.Sc(8));
        GUILayout.Label(__dct, SRApi.Ctl.LabelWrap, GUILayout.Width(__dcw), GUILayout.Height(SRApi.Ctl.Sc(52)));
        int slideVal = Mathf.Clamp(discVal, 10, 100);
        if (QuickAdjust.MoreHandicapOn) {
            // 自由输入：隐藏右侧数值标签，输入框自己显示数值。
            string cur = slideVal.ToString();
            string nvText = SRApi.ClearableTextField(cur, SRApi.Ctl.SearchBox, GUILayout.Width(SRApi.Ctl.Sc(90)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            int parsed;
            if (nvText != cur && int.TryParse(nvText, out parsed)) {
                parsed = Mathf.Clamp(parsed, 10, 100);
                if (disc != null) SRApi.Ctl.SetValue(disc, parsed);
            }
        } else {
            Rect sr = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(200), SRApi.Ctl.Sc(28));
            float nv = SRApi.Ctl.DrawSlider(sr, slideVal, 10f, 100f, true);
            int nslide = Mathf.Clamp(Mathf.RoundToInt(nv / 10f) * 10, 10, 100);
            if (nslide != slideVal && disc != null) SRApi.Ctl.SetValue(disc, nslide);
            GUILayout.Label(nslide + "%", SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(56)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        }
        GUILayout.Space(SRApi.Ctl.Sc(10));
        // 「更多数值」开关放在滑块右边同一行。
        ConfigEntryBase more = SRApi.Ctl.FindEntry("Quick Adjust", "More Handicap Values");
        if (more != null) {
            SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("更多数值", "More values"),
                SRApi.T("自由输入模式开关\n开启后评分百分比由整十滑块改为输入框\n范围 10 到 100 任意整数。",
                        "free input mode switch\nturns the tens slider into an input box\nany integer from 10 to 100")),
                more, SRApi.Ctl.Sc(110), SRApi.Ctl.Sc(26));
            SRApi.Ctl.RenderControl(more);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("qa.apply_discount", SRApi.T("应用折扣", "Apply"), SRApi.T("把评分百分比设成上面的数值.\n滑块 10 到 100 整十走, 100 不折扣, 10 相当于减 90%.\n这一局立刻生效, 平衡板上能看到.", "Set your score percentage to the value above.\nSlider goes 10-100 in tens: 100 = no discount, 10 = 90% off.\nTakes effect immediately and shows on the balancer."), QuickAdjust.ApplyScoreDiscount, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(30))) {
            QuickAdjust.ApplyScoreDiscount();
        }
        GUILayout.Space(SRApi.Ctl.Sc(8));
        if (SRApi.Ctl.HotkeyButton("qa.restore_discount", SRApi.T("还原折扣", "Restore"), SRApi.T("把评分百分比恢复为 100%（不折扣）.\n等价于滑块拖到 100 再应用.\n只影响自己那一行.", "Restore your score percentage to 100% (no discount).\nSame as setting the slider to 100 and applying.\nAffects only your own row."), QuickAdjust.RestoreScoreDiscount, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(30))) {
            QuickAdjust.RestoreScoreDiscount();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(8));
        GUILayout.Space(SRApi.Ctl.Sc(6));

        SRApi.Ctl.SectionTitle(SRApi.T("- 快速重试 -", "- Quick retry -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qr = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("死后自动重试", "Auto retry on death"), SRApi.T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n对局中开关立即生效。", "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nTakes effect immediately.")), qr, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (qr != null) SRApi.Ctl.RenderControl(qr);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qrf = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Fast");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("快速重试", "Quick retry"), SRApi.T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n长按时间在下方设置，对局中开关立即生效。", "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nThe hold time is set below; takes effect immediately.")), qrf, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (qrf != null) SRApi.Ctl.RenderControl(qrf);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qrh = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Hold");
        // T(zh, en) 的两个实参都会被求值：先取局部变量，避免每帧算两次。
        string vanillaRetry = QuickAdjust.VanillaHoldBText(false);
        GUILayout.Label(new GUIContent(SRApi.T("等待秒数", "Hold s"),
            SRApi.T("长按 B 的等待阈值（Character.SuicideTime）：挑战模式重试游戏默认 " + vanillaRetry + "。",
              "Hold-B threshold (Character.SuicideTime): vanilla " + vanillaRetry + " for challenge retry.")),
            SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(84)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        if (qrh != null) {
            float hv = (float)qrh.BoxedValue;
            Rect hr = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(150), SRApi.Ctl.Sc(28));
            float hn = SRApi.Ctl.DrawSlider(hr, hv, 0f, 2f, false);
            hn = Mathf.Round(hn * 10f) / 10f;
            // 值变了就写
            if (Mathf.Abs(hn - hv) > 0.0001f) SRApi.Ctl.SetValue(qrh, hn);
            // 中英文字符串相同，无需 T()；直接拼一次。
            string hs = hn.ToString("0.0") + "s";
            GUILayout.Label(hs, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(44)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(6));

        SRApi.Ctl.SectionTitle(SRApi.T("- 快速切换 -", "- Quick switch -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qs = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Switch");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("快速切换", "Quick switch"), SRApi.T("仅自由模式：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n长按时间在下方设置，对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action<->Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nThe hold time is set below; takes effect immediately.")), qs, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (qs != null) SRApi.Ctl.RenderControl(qs);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qsh = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Switch Hold");
        string vanillaSwitch = QuickAdjust.VanillaHoldBText(true);
        GUILayout.Label(new GUIContent(SRApi.T("等待秒数", "Hold s"),
            SRApi.T("自由模式长按 B 的阈值：行动->建造 = Character.SuicideTime，游戏默认 " + vanillaSwitch + "；建造->行动 = PiecePlacementCursor.SwitchTime。",
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
            if (!SRApi.GateMaster || (SRApi.UiOpen && SRApi.BlockInput) || !__instance.hasAuthority) return;
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

        // 快速重试已发标记（闩锁）：按 networkNumber 记，见 ForceSuicidalState。
        private static readonly HashSet<int> _retrySentTo = new HashSet<int>();

        [HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
        [HarmonyPostfix]
        private static void ForceSuicideState(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.UiOpen && SRApi.BlockInput) || !__instance.hasAuthority)
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
        private static void OnStartClientQueueKeep(GamePlayer __instance) { QueueKeepIfLocal(__instance); }

        [HarmonyPatch(typeof(GamePlayer), "OnStartServer")]
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
                    _keepHandicap = target == 100 ? 0 : target;
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


