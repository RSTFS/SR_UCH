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
        SRApi.LocSec("Quick Adjust", "快速调整", null);
        SRApi.Nav("Quick Adjust", 30); // 侧栏栏目顺序 30
        SRApi.RegisterPage("Quick Adjust", Render);   // 本功能页自绘
        SRApi.LocKey("Quick Adjust", "Quick Switch", "快速切换", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch", "快速切换开关\n自由模式下长按切换键在行动与建造之间切换\n仅自由模式生效，对局中开关立即生效；关。", "quick switch toggle\nin freeplay, hold the switch key to swap between Action and Build\nfreeplay only; the toggle applies immediately in-game; OFF");
        SRApi.LocKey("Quick Adjust", "Quick Switch Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Switch Hold", "快速切换长按时间\n设置长按多少秒触发切换\n范围 0 到 2，0 为立即切换", "quick switch hold time\nsets how long the key must be held to trigger the swap\nrange 0-2, 0 = instant");
        SRApi.LocKey("Quick Adjust", "Quick Retry", "死后自动重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry", "死后自动重试开关\n挑战模式死亡后自动重开关卡，无需长按\n仅挑战模式生效", "auto retry toggle\nin challenge mode, restarts the run automatically after death without holding a key\nchallenge mode only");
        SRApi.LocKey("Quick Adjust", "Quick Retry Fast", "快速重试", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Fast", "快速重试开关\n挑战模式死亡后长按重试键到设定秒数自动重试\n仅挑战模式生效，长按时间在下方设置；关。", "quick retry toggle\nin challenge mode, hold the retry key for the set time after death to retry\nchallenge mode only; the hold time is set below; OFF");
        SRApi.LocKey("Quick Adjust", "Quick Retry Hold", "等待秒数", null);
        SRApi.LocDesc("Quick Adjust", "Quick Retry Hold", "快速重试长按时间\n设置死亡后长按多久触发重试\n范围 0 到 2，0 为立即重试", "quick retry hold time\nsets how long to hold after death before retrying\nrange 0-2, 0 = instant");
        SRApi.LocKey("Quick Adjust", "Score Discount", "分数折扣", null);
        SRApi.LocDesc("Quick Adjust", "Score Discount", "分数折扣百分比\n把自己的 handicap 设为 100 减折扣值\n范围 0 到 90，0 为关闭；20。", "score discount percentage\nsets your handicap to 100 minus the discount\nrange 0-90, 0 = off; 20");
        SRApi.LocKey("Quick Adjust", "More Discount Values", "更多折扣数值", null);
        SRApi.LocDesc("Quick Adjust", "More Discount Values", "折扣数值输入模式开关\n开启后折扣由整十滑块改为自由输入框\n范围 0 到 90 任意整数，0 为关闭", "discount input mode switch\nturns the tens slider into a free input box\nany integer from 0 to 90, 0 = off");
        // 预登记本页自动快捷键。
        SRApi.HotkeyAction("qa.apply_discount", "应用折扣", () => QuickAdjust.ApplyScoreDiscount());
        SRApi.HotkeyAction("qa.restore_discount", "恢复原值", () => QuickAdjust.RestoreScoreDiscount());
    }

    public static void Render() {
        try {
        // 每对 BeginHorizontal/EndHorizontal 之间都有会抛异常的控件，任一抛出会使 IMGUI 布局栈失衡，
        // 故整段包 try/catch。
        SRApi.Ctl.SectionTitle(SRApi.T("- 分数折扣 -", "- Score discount -"));
        // 折扣默认整十滑块，开启更多折扣数值后改为自由输入框。
        GUILayout.BeginHorizontal();
        ConfigEntryBase disc = SRApi.Ctl.FindEntry("Quick Adjust", "Score Discount");
        int discVal = QuickAdjust.ScoreDiscount;
        bool moreOn = QuickAdjust.MoreDiscountOn;
        // 标签宽度自适应：取"文本宽度+余量"与 64 的较大值，避免英文标签被截断。
        GUIContent __dct = new GUIContent(SRApi.T("折扣 %", "Discount %"));
        float __dcw = Mathf.Max(SRApi.Ctl.Sc(64), SRApi.Ctl.LabelWrap.CalcSize(__dct).x + SRApi.Ctl.Sc(8));
        GUILayout.Label(__dct, SRApi.Ctl.LabelWrap, GUILayout.Width(__dcw), GUILayout.Height(SRApi.Ctl.Sc(52)));
        if (moreOn) {
            string cur = discVal.ToString();
            string nv = SRApi.ClearableTextField(cur, SRApi.Ctl.SearchBox, GUILayout.Width(SRApi.Ctl.Sc(80)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            int parsed;
            if (nv != cur && int.TryParse(nv, out parsed)) {
                parsed = Mathf.Clamp(parsed, 0, 90);
                if (disc != null) SRApi.Ctl.SetValue(disc, parsed);
            }
        } else {
            int slideVal = Mathf.Clamp((discVal + 5) / 10 * 10, 0, 90);
            Rect sr = GUILayoutUtility.GetRect(SRApi.Ctl.Sc(220), SRApi.Ctl.Sc(28));
            float nv = SRApi.Ctl.DrawSlider(sr, slideVal, 0f, 90f, true);
            int nslide = Mathf.RoundToInt(nv / 10f) * 10;
            // 值变了就写
            if (nslide != slideVal && disc != null) SRApi.Ctl.SetValue(disc, nslide);
            GUILayout.Label(nslide + "%", SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(52)), GUILayout.Height(SRApi.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("qa.apply_discount", SRApi.T("应用折扣", "Apply"), SRApi.T("把自己那行的 handicap 设成 100 减折扣.\n滑块按 0 到 90 整十走: 0 是关掉, 90 会把 handicap 压到 10, 也就是最多减 90%.\n这一局立刻生效, 平衡板上能看到; 点恢复原值就回到 100%.", "Set your handicap to 100 minus the discount.\nSlider goes 0-90 in tens: 0 = off, 90 sets the handicap to 10 (a 90% cap).\nTakes effect immediately and shows on the balancer. Restore sets it back to 100%."), QuickAdjust.ApplyScoreDiscount, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) {
            QuickAdjust.ApplyScoreDiscount();
        }
        GUILayout.Space(SRApi.Ctl.Sc(8));
        if (SRApi.Ctl.HotkeyButton("qa.restore_discount", SRApi.T("恢复原值", "Restore"), SRApi.T("把自己的 handicap 恢复为 100%（清除折扣）", "Restore your handicap to 100% (clears the discount)"), QuickAdjust.RestoreScoreDiscount, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) {
            QuickAdjust.RestoreScoreDiscount();
        }
        GUILayout.Space(SRApi.Ctl.Sc(8));
        ConfigEntryBase more = SRApi.Ctl.FindEntry("Quick Adjust", "More Discount Values");
        if (more != null) {
            string moreTip = SRApi.T("更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n",
                "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n");
            // 宽度交给 IMGUI 按文字自适应。
            bool nm = GUILayout.Toggle(moreOn, new GUIContent(SRApi.T("更多折扣数值", "More values"), moreTip), GUILayout.Height(SRApi.Ctl.Sc(30)));
            if (nm != moreOn) SRApi.Ctl.SetValue(more, nm);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(6));

        SRApi.Ctl.SectionTitle(SRApi.T("- 快速重试 -", "- Quick retry -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qr = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("死后自动重试", "Auto retry on death"), SRApi.T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。", "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nOFF by default; takes effect immediately.")), qr, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (qr != null) SRApi.Ctl.RenderControl(qr);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qrf = SRApi.Ctl.FindEntry("Quick Adjust", "Quick Retry Fast");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("快速重试", "Quick retry"), SRApi.T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qrf, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
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
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("快速切换", "Quick switch"), SRApi.T("仅自由模式：长按 B 到设定等待秒数切换 行动<->建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action<->Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qs, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
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
						s += SRApi.T("（建造->行动 " + st + "s）", " (build->action " + st + "s)");
					}
					break;
				}
			} catch (Exception __ex) { SRApi.Guard.Log("读取 SwitchTime 默认值", __ex); }
			return s;
		}

		public static int ScoreDiscount => (_scoreDiscount != null) ? Mathf.Clamp(_scoreDiscount.Value, 0, 90) : 0;

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

		public static void ApplyScoreDiscount()
		{
			try
			{
				if (!SRApi.GateMaster)
				{
					SRApi.Notify(SRApi.MsgMasterOff);
					return;
				}
				int scoreDiscount = ScoreDiscount;
				if (scoreDiscount <= 0)
				{
					SRApi.Notify(SRApi.T("评分折扣为 0，未应用", "Score discount is 0, not applied"));
					return;
				}
				int target = Mathf.Clamp(100 - scoreDiscount, 10, 100);
				if (ApplyOwnHandicap(target))
				{
					SRApi.Notify(SRApi.T("评分折扣已应用：自己 handicap ", "Score discount applied: own handicap ") + target);
				}
				else
				{
					SRApi.Notify(SRApi.T("需要在房间内（大厅或树屋）才能修改评分折扣", "Change the score discount while in a room (lobby or treehouse)"));
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
					SRApi.Notify(SRApi.T("已恢复：自己 handicap 100%", "Restored: own handicap 100%"));
				}
				else
				{
					SRApi.Notify(SRApi.T("需要在房间内（大厅或树屋）才能恢复评分折扣", "Restore the score discount while in a room (lobby or treehouse)"));
				}
			}
			catch (Exception ex)
			{
				SRApi.LogWarn((object)("恢复评分折扣失败: " + ex.Message));
			}
		}
}

}


