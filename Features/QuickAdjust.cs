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

    private static ConfigEntry<int> _scoreDiscount;
    private static ConfigEntry<bool> _moreDiscount;
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
        SR.LocSec("Quick Adjust", "快速调整", null);
        SRApi.Nav("Quick Adjust", 30); // 侧栏栏目顺序 30
        SR.RegisterPage("Quick Adjust", Render);   // 本功能页自绘
        SR.LocKey("Quick Adjust", "Quick Switch", "快速切换", null);
        SR.LocDesc("Quick Adjust", "Quick Switch", "只在自由模式有用. 长按 B 到设定秒数, 在行动和建造之间切, 原版是半秒. 默认关, 局里开关立刻生效.", "Quick switch (freeplay only): hold B for the set seconds to swap Action<->Build (vanilla 0.5s).\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Switch Hold", "等待秒数", null);
        SR.LocDesc("Quick Adjust", "Quick Switch Hold", "长按 B 要按几秒才切, 0 到 2, 填 0 立刻切.", "Hold seconds for quick switch (0-2, 0=instant).\nIn Freeplay, hold B this long to swap Action/Build.");
        SR.LocKey("Quick Adjust", "Quick Retry", "死后自动重试", null);
        SR.LocDesc("Quick Adjust", "Quick Retry", "只在挑战模式有用. 死了自动重开关卡, 不用长按. 做法是把重生延迟压到挑战模式的最短时间. 默认关.", "Auto retry on death (challenge only): auto-retries (restart the run) after death, no hold.\nUses the respawn-delay challenge logic.\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Retry Fast", "快速重试", null);
        SR.LocDesc("Quick Adjust", "Quick Retry Fast", "只在挑战模式有用. 死了以后长按 B 到设定秒数就自动重试, 原版是半秒. 默认关.", "Quick retry (challenge only): after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Retry Hold", "等待秒数", null);
        SR.LocDesc("Quick Adjust", "Quick Retry Hold", "长按 B 要按几秒才重试, 0 到 2, 填 0 立刻重试.", "Hold seconds for quick retry (0-2, 0=instant).\nIn Challenge, hold B this long after death to auto-retry.");
        SR.LocKey("Quick Adjust", "Score Discount", "分数折扣", null);
        SR.LocDesc("Quick Adjust", "Score Discount", "把自己的分数 handicap 调低, 填 20 就等于打 80 分. 最多减到 10, 填 0 是关闭. 平衡板上自己那行会显示对应百分比, 点恢复原值就能还原.", "Score discount %: set your score-balancer handicap to 100-discount %.\nDefault: slider (0-90 in tens, default 20 -> handicap 80%); tick More Discount Values for a free input box (0-90 any integer, 0 = off).\n90+ clamps to handicap 10 = 90% max; 0 = off. Your balancer row shows the percentage; use Restore to reset.");
        SR.LocKey("Quick Adjust", "More Discount Values", "更多折扣数值", null);
        SR.LocDesc("Quick Adjust", "More Discount Values", "默认关. 打开后折扣从滑块变成输入框, 0 到 90 任意整数.", "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).");
        // 预登记本页自动快捷键。
        SRApi.HotkeyAction("qa.apply_discount", "应用折扣", () => QuickAdjust.ApplyScoreDiscount());
        SRApi.HotkeyAction("qa.restore_discount", "恢复原值", () => QuickAdjust.RestoreScoreDiscount());
    }

    public static void Render() {
        try {
        // 每对 BeginHorizontal/EndHorizontal 之间都有会抛异常的控件，任一抛出会使 IMGUI 布局栈失衡，
        // 故整段包 try/catch。
        GUILayout.Label(SR.T("- 分数折扣 -", "- Score discount -"), SR.Ctl.SecHeader);
        // 折扣默认整十滑块，开启更多折扣数值后改为自由输入框。
        GUILayout.BeginHorizontal();
        ConfigEntryBase disc = SR.Ctl.FindEntry("Quick Adjust", "Score Discount");
        int discVal = QuickAdjust.ScoreDiscount;
        bool moreOn = QuickAdjust.MoreDiscountOn;
        // 标签宽度自适应：取"文本宽度+余量"与 64 的较大值，避免英文标签被截断。
        GUIContent __dct = new GUIContent(SR.T("折扣 %", "Discount %"));
        float __dcw = Mathf.Max(SR.Ctl.Sc(64), SR.Ctl.LabelWrap.CalcSize(__dct).x + SR.Ctl.Sc(8));
        GUILayout.Label(__dct, SR.Ctl.LabelWrap, GUILayout.Width(__dcw), GUILayout.Height(SR.Ctl.Sc(52)));
        if (moreOn) {
            string cur = discVal.ToString();
            string nv = SR.ClearableTextField(cur, SR.Ctl.SearchBox, GUILayout.Width(SR.Ctl.Sc(80)), GUILayout.Height(SR.Ctl.Sc(26)));
            int parsed;
            if (nv != cur && int.TryParse(nv, out parsed)) {
                parsed = Mathf.Clamp(parsed, 0, 90);
                if (disc != null) SR.Ctl.SetValue(disc, parsed);
            }
        } else {
            int slideVal = Mathf.Clamp((discVal + 5) / 10 * 10, 0, 90);
            Rect sr = GUILayoutUtility.GetRect(SR.Ctl.Sc(220), SR.Ctl.Sc(28));
            float nv = SR.Ctl.DrawSlider(sr, slideVal, 0f, 90f, true);
            int nslide = Mathf.RoundToInt(nv / 10f) * 10;
            // 值变了就写
            if (nslide != slideVal && disc != null) SR.Ctl.SetValue(disc, nslide);
            GUILayout.Label(nslide + "%", SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(52)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyButton("qa.apply_discount", SR.T("应用折扣", "Apply"), SR.T("把自己那行的 handicap 设成 100 减折扣.\n滑块按 0 到 90 整十走: 0 是关掉, 90 会把 handicap 压到 10, 也就是最多减 90%.\n这一局立刻生效, 平衡板上能看到; 点恢复原值就回到 100%.", "Set your handicap to 100 minus the discount.\nSlider goes 0-90 in tens: 0 = off, 90 sets the handicap to 10 (a 90% cap).\nTakes effect immediately and shows on the balancer. Restore sets it back to 100%."), QuickAdjust.ApplyScoreDiscount, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) {
            QuickAdjust.ApplyScoreDiscount();
        }
        GUILayout.Space(SR.Ctl.Sc(8));
        if (SR.Ctl.HotkeyButton("qa.restore_discount", SR.T("恢复原值", "Restore"), SR.T("把自己的 handicap 恢复为 100%（清除折扣）", "Restore your handicap to 100% (clears the discount)"), QuickAdjust.RestoreScoreDiscount, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) {
            QuickAdjust.RestoreScoreDiscount();
        }
        GUILayout.Space(SR.Ctl.Sc(8));
        ConfigEntryBase more = SR.Ctl.FindEntry("Quick Adjust", "More Discount Values");
        if (more != null) {
            string moreTip = SR.T("更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n",
                "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n");
            // 宽度交给 IMGUI 按文字自适应。
            bool nm = GUILayout.Toggle(moreOn, new GUIContent(SR.T("更多折扣数值", "More values"), moreTip), GUILayout.Height(SR.Ctl.Sc(30)));
            if (nm != moreOn) SR.Ctl.SetValue(more, nm);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("- 快速重试 -", "- Quick retry -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        ConfigEntryBase qr = SR.Ctl.FindEntry("Quick Adjust", "Quick Retry");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("死后自动重试", "Auto retry on death"), SR.T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。", "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nOFF by default; takes effect immediately.")), qr, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (qr != null) SR.Ctl.RenderControl(qr);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qrf = SR.Ctl.FindEntry("Quick Adjust", "Quick Retry Fast");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("快速重试", "Quick retry"), SR.T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qrf, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (qrf != null) SR.Ctl.RenderControl(qrf);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qrh = SR.Ctl.FindEntry("Quick Adjust", "Quick Retry Hold");
        // T(zh, en) 的两个实参都会被求值：先取局部变量，避免每帧算两次。
        string vanillaRetry = QuickAdjust.VanillaHoldBText(false);
        GUILayout.Label(new GUIContent(SR.T("等待秒数", "Hold s"),
            SR.T("长按 B 的等待阈值（Character.SuicideTime）：挑战模式重试游戏默认 " + vanillaRetry + "。",
              "Hold-B threshold (Character.SuicideTime): vanilla " + vanillaRetry + " for challenge retry.")),
            SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(84)), GUILayout.Height(SR.Ctl.Sc(26)));
        if (qrh != null) {
            float hv = (float)qrh.BoxedValue;
            Rect hr = GUILayoutUtility.GetRect(SR.Ctl.Sc(150), SR.Ctl.Sc(28));
            float hn = SR.Ctl.DrawSlider(hr, hv, 0f, 2f, false);
            hn = Mathf.Round(hn * 10f) / 10f;
            // 值变了就写
            if (Mathf.Abs(hn - hv) > 0.0001f) SR.Ctl.SetValue(qrh, hn);
            // 中英文字符串相同，无需 T()；直接拼一次。
            string hs = hn.ToString("0.0") + "s";
            GUILayout.Label(hs, SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(44)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("- 快速切换 -", "- Quick switch -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        ConfigEntryBase qs = SR.Ctl.FindEntry("Quick Adjust", "Quick Switch");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("快速切换", "Quick switch"), SR.T("仅自由模式：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action<->Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qs, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (qs != null) SR.Ctl.RenderControl(qs);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qsh = SR.Ctl.FindEntry("Quick Adjust", "Quick Switch Hold");
        string vanillaSwitch = QuickAdjust.VanillaHoldBText(true);
        GUILayout.Label(new GUIContent(SR.T("等待秒数", "Hold s"),
            SR.T("自由模式长按 B 的阈值：行动->建造 = Character.SuicideTime，游戏默认 " + vanillaSwitch + "；建造->行动 = PiecePlacementCursor.SwitchTime。",
              "Freeplay hold-B thresholds: action->build = Character.SuicideTime, vanilla " + vanillaSwitch + "; build->action = PiecePlacementCursor.SwitchTime.")),
            SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(84)), GUILayout.Height(SR.Ctl.Sc(26)));
        if (qsh != null) {
            float shv = (float)qsh.BoxedValue;
            Rect shr = GUILayoutUtility.GetRect(SR.Ctl.Sc(150), SR.Ctl.Sc(28));
            float shn = SR.Ctl.DrawSlider(shr, shv, 0f, 2f, false);
            shn = Mathf.Round(shn * 10f) / 10f;
            // 值变了就写
            if (Mathf.Abs(shn - shv) > 0.0001f) SR.Ctl.SetValue(qsh, shn);
            // 中英文相同，拼一次即可。
            string shs = shn.ToString("0.0") + "s";
            GUILayout.Label(shs, SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(44)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("- 快速自杀 -", "- Quick suicide -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        ConfigEntryBase se = SR.Ctl.FindEntry("Quick Adjust", "Enabled");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("快速自杀", "Quick suicide"), SR.T("总开关：开启后按 组合键（自杀键）快速自杀（树屋和局内都有效，只杀自己，默认 Shift+0）", "Master switch: press the suicide combo to die instantly (treehouse and in-match, only yourself, default Shift+0)")), se, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (se != null) SR.Ctl.RenderControl(se);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase sk = SR.Ctl.FindEntry("Quick Adjust", "Keybind");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("自杀键", "Suicide key"), SR.T("自杀键（组合键）：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键即可设为组合键（默认 Shift+0）。\n树屋和局内都有效，只杀自己。", "Suicide key (combo): click the button then hold Shift/Ctrl/Alt while pressing the main key (default Shift+0).\nWorks in the treehouse and in-match; only kills yourself.")), sk, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (sk != null) SR.Ctl.RenderControl(sk);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));
        GUILayout.Space(SR.Ctl.Sc(6));
        } catch (Exception __ex) { SRApi.Guard.Log("快速调整页 渲染", __ex); }
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
			// 走契约 IFeatureHost.Config：把 host 硬转成 BaseUnityPlugin 会在非 BaseUnityPlugin 宿主下抛
			// InvalidCastException（被 MainPlugin 兜住，但本功能静默消失）。
			ConfigFile cfg = plugin.Config;
			_scoreDiscount = cfg.Bind<int>("Quick Adjust", "Score Discount", 20, "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %（范围 0-90：滑块模式整十，勾选更多折扣数值后可填任意整数如 85 -> handicap 15%；上限 90 = handicap 10；0 = 关闭；只影响自己）。\n默认 20（handicap 80%）；平衡板上自己那一行显示为对应百分比；可随时点恢复原值还原。");
			_moreDiscount = cfg.Bind<bool>("Quick Adjust", "More Discount Values", false, "更多折扣数值：选中后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。");
			_quickSwitchEnabled = cfg.Bind<bool>("Quick Adjust", "Quick Switch", false, "快速切换（仅自由模式）：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickSwitchHold = cfg.Bind<float>("Quick Adjust", "Quick Switch Hold", 0.5f, "快速切换的长按等待秒数（0-2，0=立即；自由模式长按 B 达到该秒数切换行动/建造）。");
			_quickRetryEnabled = cfg.Bind<bool>("Quick Adjust", "Quick Retry", false, "死后自动重试（仅挑战模式）：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。");
			_quickRetryFast = cfg.Bind<bool>("Quick Adjust", "Quick Retry Fast", false, "快速重试（仅挑战模式）：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickRetryHoldTime = cfg.Bind<float>("Quick Adjust", "Quick Retry Hold", 0.5f, "快速重试的长按等待秒数（0-2，0=立即；挑战模式死亡后长按 B 达到该秒数自动重试）。");
        // 本类自带 [HarmonyPatch] 但 ITweak 为反射发现，须手动 CreateAndPatchAll；单个目标失效只丢本类功能。
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
						s += SR.T("（建造->行动 " + st + "s）", " (build->action " + st + "s)");
					}
					break;
				}
			} catch (Exception __ex) { SRApi.Guard.Log("读取 SwitchTime 默认值", __ex); }
			return s;
		}

		public static int ScoreDiscount => (_scoreDiscount != null) ? _scoreDiscount.Value : 0;

		public static bool MoreDiscountOn => _moreDiscount != null && _moreDiscount.Value;

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
				if (QuickSwitchOn && SR.GateModeAllows(SR.ModeMask.Freeplay))
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
				else if (QuickRetryFastOn && SR.GateModeAllows(SR.ModeMask.Challenge))
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
			if (SR.GateModeAllows(SR.ModeMask.Challenge))
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
			if (!SR.GateModeAllows(SR.ModeMask.Freeplay))
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

		public static void ApplyScoreDiscount()
		{
			try
			{
				if (!SRApi.GateMaster)
				{
					SRApi.Notify(SR.Msg.MasterOff());
					return;
				}
				int scoreDiscount = ScoreDiscount;
				if (scoreDiscount <= 0)
				{
					SRApi.Notify(SR.T("评分折扣为 0，未应用", "Score discount is 0, not applied"));
					return;
				}
				int target = Mathf.Clamp(100 - scoreDiscount, 10, 100);
				if (ApplyOwnHandicap(target))
				{
					SRApi.Notify(SR.T("评分折扣已应用：自己 handicap ", "Score discount applied: own handicap ") + target + SR.T("%，本局生效", "%, effective this round"));
				}
				else
				{
					SRApi.Notify(SR.Msg.NotLobby());
				}
			}
			catch (Exception ex)
			{
				SRApi.LogWarn((object)("评分折扣失败: " + ex.Message));
			}
		}

		// 同时写 LobbyPlayer（平衡板/下一局）与 GamePlayer（本局立即生效）。
		private static bool ApplyOwnHandicap(int value)
		{
			try
			{
				foreach (Player p in PlayerManager.GetInstance())
				{
					if (p == null || p.AssociatedLobbyPlayer == null) continue;
					if (!p.AssociatedLobbyPlayer.IsLocalPlayer) continue;
					p.AssociatedLobbyPlayer.SetPlayerHandicap(value);
					if (p.AssociatedGamePlayer != null)
					{
						try
						{
							p.AssociatedGamePlayer.CallCmdSetPlayerHandicap(value);
						}
						catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
					}
					return true;
				}
			}
			catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
			return false;
		}

		public static void RestoreScoreDiscount()
		{
			try
			{
				if (!SRApi.GateMaster)
				{
					SRApi.Notify(SR.Msg.MasterOff());
					return;
				}
				if (ApplyOwnHandicap(100))
				{
					SRApi.Notify(SR.T("已恢复：自己 handicap 100%", "Restored: own handicap 100%"));
				}
				else
				{
					SRApi.Notify(SR.Msg.NotLobby());
				}
			}
			catch (Exception ex)
			{
				SRApi.LogWarn((object)("恢复评分折扣失败: " + ex.Message));
			}
		}
}

[SrFeature("sr.treehousesuicide", "TreehouseSuicide", "1.0")]

public class TreehouseSuicide : ITweak {
    private static IFeatureHost _mp;   // 只用来读 Config（契约类型）
    private static ConfigEntry<KeyCode> _keybind;
    public static bool Enabled = true;
    public void Initialize(IFeatureHost plugin) {
        _mp = plugin;
        ConfigEntry<bool> enabled = _mp.Config.Bind("Quick Adjust", "Enabled", false, "总开关");
        Enabled = enabled.Value;
        enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
        _keybind = _mp.Config.Bind(
            "Quick Adjust",
            "Keybind",
            KeyCode.Alpha0,
            "自杀键（组合键设置：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键，即可设为组合键，如 Shift+0）");
        // 配置兼容：旧默认 P 一次性迁移到 Shift+0；无隐藏标记会导致每次启动都覆盖已绑定的设置。
        ConfigEntry<bool> pMigrated = _mp.Config.Bind("Internal", "P Suicide Migrated", false,
            "内部用: 自杀键从 P 改成 Shift+0 的迁移有没有跑过.");
        if (!pMigrated.Value)
        {
            if (_keybind.Value == KeyCode.P) _keybind.Value = KeyCode.Alpha0;
            pMigrated.Value = true;
        }
        // 未设置过修饰键时默认 Shift。
        SR.RegisterKey("快捷自杀", _keybind, "hold");
        if (SR.KeyComboMod(_keybind) == SR.ComboMod.None)
        {
            SR.SetDefaultCombo(_keybind, SR.ComboMod.Shift);
        }
        // 单个补丁目标失效只丢本类功能。
        try { Harmony.CreateAndPatchAll(typeof(TreehouseSuicide), (string)null); }
        catch (Exception e) { SRApi.LogError("快速自杀 补丁注册失败: " + e.Message); }
    }

    // FixedUpdate 一帧可能跑多次，而 ComboKeyDown 是边沿判定（GetKeyDown 在整个渲染帧内为真），
    // 同一帧的多个物理步会各判一次，故按帧去重。
    private static int _suicideFrame = -1;

    [HarmonyPatch(typeof(Character), "FixedUpdate")]
    [HarmonyPostfix]
    private static void CharacterPatch(Character __instance) {
        // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
        try {
                if (!SRApi.GateMaster) return;
                if (!Enabled) return;
                if (SRApi.UiOpen && SRApi.BlockInput) return;
                if (!__instance.hasAuthority) return;
                // 本帧已判过输入（无论是否触发），后续物理步不再判定
                if (_suicideFrame == Time.frameCount) return;
                _suicideFrame = Time.frameCount;
                // 组合键判定：修饰键在键位捕捉时设置（默认 Shift）。
                if (SR.ComboKeyDown(_keybind) && !__instance.Frozen) {
                    __instance.KillCharacter("Suicide", false, __instance.networkNumber);
                }
        } catch (Exception __ex) { SRApi.Guard.Log("自杀键", __ex); }
    }
}
}


