using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
// 分数折扣 / 记分板 handicap 刷新 / 快速切换 / 快速重试（含两个 Character 补丁）；ITweak 由 MainPlugin 反射初始化。
public class QuickAdjust : ITweak {

    private static ConfigEntry<int> _scoreDiscount;
    private static ConfigEntry<bool> _moreDiscount;
    private static ConfigEntry<bool> _quickSwitchEnabled;
    private static ConfigEntry<float> _quickSwitchHold;
    private static ConfigEntry<bool> _quickRetryEnabled;
    private static ConfigEntry<bool> _quickRetryFast;
    private static ConfigEntry<float> _quickRetryHoldTime;
    //计分板刷新用的反射字段缓存（避免每次刷新重复 AccessTools.Field）。
    private static FieldInfo _gsbField;
    private static FieldInfo _relField;

    private static void SelfReg() {
        SR.LocSec("Quick Adjust", "快速调整", null);
        SR.Nav("Quick Adjust", 30); //侧栏栏目顺序 30
        SR.LocKey("Quick Adjust", "Quick Switch", "快速切换", null);
        SR.LocDesc("Quick Adjust", "Quick Switch", "快速切换（仅自由模式）：长按 B 到设定等待秒数切换 行动↔建造 模式（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Quick switch (freeplay only): hold B for the set seconds to swap Action<->Build (vanilla 0.5s).\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Switch Hold", "等待秒数", null);
        SR.LocDesc("Quick Adjust", "Quick Switch Hold", "快速切换的长按等待秒数（0-2，0=立即）。\n自由模式长按 B 达到该秒数切换行动/建造。", "Hold seconds for quick switch (0-2, 0=instant).\nIn Freeplay, hold B this long to swap Action/Build.");
        SR.LocKey("Quick Adjust", "Quick Retry", "死后自动重试", null);
        SR.LocDesc("Quick Adjust", "Quick Retry", "死后自动重试（仅挑战模式）：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。", "Auto retry on death (challenge only): auto-retries (restart the run) after death, no hold.\nUses the respawn-delay challenge logic.\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Retry Fast", "快速重试", null);
        SR.LocDesc("Quick Adjust", "Quick Retry Fast", "快速重试（仅挑战模式）：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Quick retry (challenge only): after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.");
        SR.LocKey("Quick Adjust", "Quick Retry Hold", "等待秒数", null);
        SR.LocDesc("Quick Adjust", "Quick Retry Hold", "快速重试的长按等待秒数（0-2，0=立即）。\n挑战模式死亡后长按 B 达到该秒数自动重试。", "Hold seconds for quick retry (0-2, 0=instant).\nIn Challenge, hold B this long after death to auto-retry.");
        SR.LocKey("Quick Adjust", "Score Discount", "分数折扣", null);
        SR.LocDesc("Quick Adjust", "Score Discount", "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %。\n默认滑块（0-90 整十倍数，默认 20 → handicap 80%）；勾选「更多折扣数值」后变为自由输入框（0-90 任意整数，0 = 关闭）。\n90 以上钳到 handicap 10 = 上限 90%；0 = 关闭。平衡板上自己那一行显示对应百分比；可随时点“恢复原值”还原。", "Score discount %: set your score-balancer handicap to 100-discount %.\nDefault: slider (0-90 in tens, default 20 → handicap 80%); tick More Discount Values for a free input box (0-90 any integer, 0 = off).\n90+ clamps to handicap 10 = 90% max; 0 = off. Your balancer row shows the percentage; use Restore to reset.");
        SR.LocKey("Quick Adjust", "More Discount Values", "更多折扣数值", null);
        SR.LocDesc("Quick Adjust", "More Discount Values", "更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。", "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).");
    }

    public static void Render() {
        GUILayout.Label(SR.T("— 分数折扣 —", "— Score discount —"), SR.Ctl.SecHeader);
        //折扣默认整十滑块（与其它滑块条同用 DrawSlider），开启「更多折扣数值」后改自由输入框。
        GUILayout.BeginHorizontal();
        ConfigEntryBase disc = SR.Ctl.FindEntry("Quick Adjust", "Score Discount");
        int discVal = QuickAdjust.ScoreDiscount;
        bool moreOn = QuickAdjust.MoreDiscountOn;
        GUILayout.Label(SR.T("折扣 %", "Discount %"), SR.Ctl.LabelWrap, GUILayout.Width(SR.Ctl.Sc(64)), GUILayout.Height(SR.Ctl.Sc(52)));
        if (moreOn) {
            string cur = discVal.ToString();
            string nv = GUILayout.TextField(cur, SR.Ctl.SearchBox, GUILayout.Width(SR.Ctl.Sc(80)), GUILayout.Height(SR.Ctl.Sc(26)));
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
            if (SR.Ctl.SliderCommitted && nslide != slideVal && disc != null) SR.Ctl.SetValue(disc, nslide);
            GUILayout.Label(SR.T(nslide + "%", nslide + "%"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(52)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent(SR.T("应用折扣", "Apply"), SR.T("把自己 handicap 设为 100-折扣 %。\n滑块模式（0-90 整十）：0 = 关闭；90 → handicap 10（上限 90%）。\n本局立即生效，平衡板上可见；恢复原值可还原为 100%。", "Set your handicap to 100-discount %.\nSlider mode (0-90 in tens): 0 = off; 90 → handicap 10 (90% cap).\nTakes effect immediately and shows on the balancer; Restore sets it back to 100%.")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
            QuickAdjust.ApplyScoreDiscount();
        }
        GUILayout.Space(SR.Ctl.Sc(8));
        if (GUILayout.Button(new GUIContent(SR.T("恢复原值", "Restore"), SR.T("把自己的 handicap 恢复为 100%（清除折扣）", "Restore your handicap to 100% (clears the discount)")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
            QuickAdjust.RestoreScoreDiscount();
        }
        GUILayout.Space(SR.Ctl.Sc(8));
        ConfigEntryBase more = SR.Ctl.FindEntry("Quick Adjust", "More Discount Values");
        if (more != null) {
            string moreTip = SR.T("更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n",
                "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n");
            bool nm = GUILayout.Toggle(moreOn, new GUIContent(SR.T("更多折扣数值", "More values"), moreTip), GUILayout.Width(SR.Ctl.Sc(160)), GUILayout.Height(SR.Ctl.Sc(30)));
            if (nm != moreOn) SR.Ctl.SetValue(more, nm);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("— 快速重试 —", "— Quick retry —"), SR.Ctl.SecHeader);
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
        GUILayout.Label(new GUIContent(SR.T("等待秒数", "Hold s"),
            SR.T("长按 B 的等待阈值（Character.SuicideTime）：挑战模式「重试」游戏默认 " + QuickAdjust.VanillaHoldBText(false) + "。",
              "Hold-B threshold (Character.SuicideTime): vanilla " + QuickAdjust.VanillaHoldBText(false) + " for challenge retry.")),
            SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(84)), GUILayout.Height(SR.Ctl.Sc(26)));
        if (qrh != null) {
            float hv = (float)qrh.BoxedValue;
            Rect hr = GUILayoutUtility.GetRect(SR.Ctl.Sc(150), SR.Ctl.Sc(28));
            float hn = SR.Ctl.DrawSlider(hr, hv, 0f, 2f, false);
            hn = Mathf.Round(hn * 10f) / 10f;
            if (SR.Ctl.SliderCommitted && Mathf.Abs(hn - hv) > 0.0001f) SR.Ctl.SetValue(qrh, hn);
            GUILayout.Label(SR.T(hn.ToString("0.0") + "s", hn.ToString("0.0") + "s"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(44)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("— 快速切换 —", "— Quick switch —"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        ConfigEntryBase qs = SR.Ctl.FindEntry("Quick Adjust", "Quick Switch");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("快速切换", "Quick switch"), SR.T("仅自由模式：长按 B 到设定等待秒数切换 行动↔建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action↔Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qs, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (qs != null) SR.Ctl.RenderControl(qs);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase qsh = SR.Ctl.FindEntry("Quick Adjust", "Quick Switch Hold");
        GUILayout.Label(new GUIContent(SR.T("等待秒数", "Hold s"),
            SR.T("自由模式长按 B 的阈值：行动→建造 = Character.SuicideTime，游戏默认 " + QuickAdjust.VanillaHoldBText(true) + "；建造→行动 = PiecePlacementCursor.SwitchTime。",
              "Freeplay hold-B thresholds: action→build = Character.SuicideTime, vanilla " + QuickAdjust.VanillaHoldBText(true) + "; build→action = PiecePlacementCursor.SwitchTime.")),
            SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(84)), GUILayout.Height(SR.Ctl.Sc(26)));
        if (qsh != null) {
            float shv = (float)qsh.BoxedValue;
            Rect shr = GUILayoutUtility.GetRect(SR.Ctl.Sc(150), SR.Ctl.Sc(28));
            float shn = SR.Ctl.DrawSlider(shr, shv, 0f, 2f, false);
            shn = Mathf.Round(shn * 10f) / 10f;
            if (SR.Ctl.SliderCommitted && Mathf.Abs(shn - shv) > 0.0001f) SR.Ctl.SetValue(qsh, shn);
            GUILayout.Label(SR.T(shn.ToString("0.0") + "s", shn.ToString("0.0") + "s"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(44)), GUILayout.Height(SR.Ctl.Sc(26)));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));

        GUILayout.Label(SR.T("— 快速自杀 —", "— Quick suicide —"), SR.Ctl.SecHeader);
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
    }

    public void Initialize(MainPlugin plugin) {
        SelfReg();
			_scoreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<int>("Quick Adjust", "Score Discount", 20, "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %（滑块 0-100，支持任意整数如 85 → handicap 15%；90 以上钳到 handicap 10 = 上限 90%；只影响自己）。\n默认 20（handicap 80%）；平衡板上自己那一行显示为对应百分比；可随时点“恢复原值”还原。");
			_moreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Quick Adjust", "More Discount Values", false, "更多折扣数值：选中后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。");
			_quickSwitchEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Quick Adjust", "Quick Switch", false, "快速切换（仅自由模式）：长按 B 到设定等待秒数切换 行动↔建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickSwitchHold = ((BaseUnityPlugin)plugin).Config.Bind<float>("Quick Adjust", "Quick Switch Hold", 0.5f, "快速切换的长按等待秒数（0-2，0=立即；自由模式长按 B 达到该秒数切换行动/建造）。");
			_quickRetryEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Quick Adjust", "Quick Retry", false, "死后自动重试（仅挑战模式）：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。");
			_quickRetryFast = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Quick Adjust", "Quick Retry Fast", false, "快速重试（仅挑战模式）：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickRetryHoldTime = ((BaseUnityPlugin)plugin).Config.Bind<float>("Quick Adjust", "Quick Retry Hold", 0.5f, "快速重试的长按等待秒数（0-2，0=立即；挑战模式死亡后长按 B 达到该秒数自动重试）。");
        //本类自带 [HarmonyPatch]，ITweak 是反射发现，必须手动 CreateAndPatchAll 才生效。
        Harmony.CreateAndPatchAll(typeof(QuickAdjust), (string)null);
    }

		public static bool QuickSwitchOn => _quickSwitchEnabled != null && _quickSwitchEnabled.Value;

		public static float QuickSwitchHold => (_quickSwitchHold != null) ? Mathf.Clamp(_quickSwitchHold.Value, 0f, 2f) : 0.5f;

		public static bool QuickRetryOn => _quickRetryEnabled != null && _quickRetryEnabled.Value;

		public static bool QuickRetryFastOn => _quickRetryFast != null && _quickRetryFast.Value;

		public static float QuickRetryHold => (_quickRetryHoldTime != null) ? Mathf.Clamp(_quickRetryHoldTime.Value, 0f, 2f) : 0.5f;

		//长按 B 阈值：自由/挑战模式固定 Character.SuicideTime=0.5s；建造态切回行动用
		//PiecePlacementCursor.SwitchTime（游戏从不赋值，取 prefab 序列化值，读不到则报 0.5s）。
		public static string VanillaHoldBText(bool forSwitch) {
			string s = "0.5s";
			if (!forSwitch) return s;
			try {
				foreach (Player p in PlayerManager.GetInstance()) {
					if (p == null || p.AssociatedGamePlayer == null) continue;
					PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
					if (pc == null || pc.SwitchTime <= 0f) continue;
					if (Mathf.Abs(pc.SwitchTime - 0.5f) > 0.05f) {
						s += SR.T("（建造→行动 " + pc.SwitchTime.ToString("0.0") + "s）", " (build→action " + pc.SwitchTime.ToString("0.0") + "s)");
					}
					break;
				}
			} catch (Exception __ex) { SR.Guard.Log("读取 SwitchTime 默认值", __ex); }
			return s;
		}

		public static int ScoreDiscount => (_scoreDiscount != null) ? _scoreDiscount.Value : 0;

		public static bool MoreDiscountOn => _moreDiscount != null && _moreDiscount.Value;

		//局内改 handicap 不会刷新计分板：游戏只在开局 SetPlayerCharacter 时调 ScoreLine.SetHandicap。
		//故 patch set_NetworkHandicap + OnDeserialize，仅在值变化时刷新（按实例记上次值，防累积）。
		private static readonly Dictionary<GamePlayer, int> _lastHandicapShown = new Dictionary<GamePlayer, int>();

		[HarmonyPatch(typeof(GamePlayer), "set_NetworkHandicap")]
		[HarmonyPostfix]
		private static void RefreshScoreboardHandicap(GamePlayer __instance) {
			//服务端/显式设置走 set_NetworkHandicap；房客端由 OnDeserialize 兜底。
			RefreshScoreboardHandicapIfChanged(__instance);
		}

		//房客端 SyncVar 反序列化直接写 Handicap 字段、不走 set_NetworkHandicap，故上面的补丁不触发，须在此检测变化补刷。
		[HarmonyPatch(typeof(GamePlayer), "OnDeserialize")]
		[HarmonyPostfix]
		private static void RefreshScoreboardHandicapClient(GamePlayer __instance) {
			RefreshScoreboardHandicapIfChanged(__instance);
		}

		//统一入口：handicap 与上次记录不同才刷新（setter 与反序列化都走这里）。
		private static void RefreshScoreboardHandicapIfChanged(GamePlayer __instance) {
			try {
				if (__instance == null) return;
				int h = __instance.Handicap;
				int prev;
				if (_lastHandicapShown.TryGetValue(__instance, out prev) && prev == h) return;
				_lastHandicapShown[__instance] = h;
				//场景切换后 GamePlayer 重建、旧引用残留，超限整体清空。
				if (_lastHandicapShown.Count > 64) _lastHandicapShown.Clear();
				RefreshScoreboardHandicapCore(__instance);
			} catch (Exception __ex) { SR.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
		}

		private static void RefreshScoreboardHandicapCore(GamePlayer __instance) {
			try {
				if (__instance == null) return;
				LobbyManager lm = LobbyManager.instance;
				if (lm == null) return;
				GameControl gc = lm.CurrentGameController as GameControl;
				if (gc == null) return;
				GraphScoreBoard board = null;
				try {
					VersusControl vc = gc as VersusControl;
					if (vc != null) {
						if (_gsbField == null) _gsbField = AccessTools.Field(typeof(VersusControl), "graphScoreBoardInstance");
						if (_gsbField != null) board = _gsbField.GetValue(vc) as GraphScoreBoard;
					}
				} catch (Exception __ex) { SR.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
				if (board == null) {
					board = UnityEngine.Object.FindObjectOfType<GraphScoreBoard>();
				}
				if (board == null) return;
				if (_relField == null) _relField = AccessTools.Field(typeof(GraphScoreBoard), "scorelineRelation");
				if (_relField == null) return;
				object rel = _relField.GetValue(board);
				if (rel == null) return;
				IDictionary<int, ScoreLine> relDict = rel as IDictionary<int, ScoreLine>;
				if (relDict == null) return;
				ScoreLine line = null;
				int num = 0;
				try { num = __instance.NetworknetworkNumber; } catch { num = __instance.networkNumber; }
				if (!relDict.TryGetValue(num, out line)) {
					//SyncVar 未就绪时按槽位兜底（localNumber 即槽位）。
					try {
						line = relDict[__instance.localNumber];
					} catch (Exception __ex) { SR.Guard.Log("QuickAdjust.GetValue", __ex); }
				}
				if (line == null) return;
				line.SetHandicap(__instance.Handicap);
			} catch (Exception __ex) { SR.Guard.Log("QuickAdjust.SetHandicap", __ex); }
		}

		//UpdateHoldBIndicator 是设置 SuicideTime 的地方，Postfix 覆盖为用户值；同时每帧同步建造态 SwitchTime（双向生效，含首次进关）。
		[HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
		[HarmonyPostfix]
		private static void OverrideHoldTime(Character __instance)
		{
			if (!SR.GateMaster || (SR.UiOpen && SR.BlockInput) || !__instance.hasAuthority) return;
			try
			{
				//模式门控统一走 GateModeAllows（已接入 IgnoreModeLimit 豁免）。
				if (QuickSwitchOn && SR.GateModeAllows(SR.ModeMask.Freeplay))
				{
					__instance.SuicideTime = QuickSwitchHold; //自由模式长按 B 切换等待
					//建造态切回行动用 PiecePlacementCursor.SwitchTime，须每帧同步。
					foreach (Player p in PlayerManager.GetInstance()) {
						if (p == null || p.AssociatedGamePlayer == null) continue;
						PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
						if (pc != null) pc.SwitchTime = QuickSwitchHold;
					}
				}
				else if (QuickRetryFastOn && SR.GateModeAllows(SR.ModeMask.Challenge))
				{
					__instance.SuicideTime = QuickRetryHold;
				}
			}
			catch (Exception __ex) { SR.Guard.Log("QuickAdjust.OverrideHoldTime", __ex); }
		}

		[HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
		[HarmonyPostfix]
		private static void ForceSuicideState(Character __instance)
		{
			if (!SR.GateMaster || (SR.UiOpen && SR.BlockInput) || !__instance.hasAuthority)
			{
				return;
			}
			GameState.GameMode gm;
			try
			{
				gm = GameSettings.GetInstance().GameMode;
			}
			catch
			{
				return;
			}
			//挑战模式：死后自动重试；已接入 IgnoreModeLimit 豁免。
			if (gm == GameState.GameMode.CHALLENGE || SR.IgnoreModeLimit)
			{
				bool dead = !__instance.Success && (__instance.Dead || __instance.Dying || __instance.LocallyDead);
				if (dead && QuickRetryOn && !__instance.WantsToRetry)
				{
					try
					{
						MsgPlayerWantsToRetry msg = new MsgPlayerWantsToRetry { networkNumber = __instance.networkNumber };
						UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PlayerWantsToRetry, msg);
					}
					catch (Exception __ex) { SR.Guard.Log("QuickAdjust.Send", __ex); }
				}
				return;
			}
			if ((int)gm > 0)
			{
				return;
			}
			//自由模式：等待时间实际由 OverrideHoldTime 设置。
			if (!QuickSwitchOn) return;
			//仅对局中生效，树屋/大厅不处理。
			try
			{
				if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
			}
			catch
			{
				return;
			}
			try { __instance.SuicideTime = QuickSwitchHold; } catch (Exception __ex) { SR.Guard.Log("QuickAdjust.Send", __ex); }
			//建造态 SwitchTime 同样设为用户值。
			try {
				foreach (Player p in PlayerManager.GetInstance()) {
					if (p == null || p.AssociatedGamePlayer == null) continue;
					PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
					if (pc != null) pc.SwitchTime = QuickSwitchHold;
				}
			} catch (Exception __ex) { SR.Guard.Log("QuickAdjust.Send", __ex); }
		}

		public static void ApplyScoreDiscount()
		{
			try
			{
				if (!SR.GateMaster)
				{
					Experiments.NotifyExp(Experiments.Msgs.MasterOff());
					return;
				}
				int scoreDiscount = ScoreDiscount;
				if (scoreDiscount <= 0)
				{
					Experiments.NotifyExp(SR.T("评分折扣未设置（0 = 关闭）", "Score discount not set (0 = off)"));
					return;
				}
				int target = Mathf.Clamp(100 - scoreDiscount, 10, 100);
				if (ApplyOwnHandicap(target))
				{
					Experiments.NotifyExp(SR.T("评分折扣已应用: 自己 handicap ", "Score discount applied: own handicap ") + target + SR.T("%（平衡板可见，本局立即生效）", "% (visible on the scoreboard, takes effect this round)"));
				}
				else
				{
					Experiments.NotifyExp(Experiments.Msgs.NotLobby());
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("评分折扣失败: " + ex.Message));
			}
		}

		//同时写 LobbyPlayer（平衡板/下一局）与 GamePlayer（本局立即生效）。
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
						catch (Exception __ex) { SR.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
					}
					return true;
				}
			}
			catch (Exception __ex) { SR.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
			return false;
		}

		public static void RestoreScoreDiscount()
		{
			try
			{
				if (!SR.GateMaster)
				{
					Experiments.NotifyExp(Experiments.Msgs.MasterOff());
					return;
				}
				if (ApplyOwnHandicap(100))
				{
					Experiments.NotifyExp(SR.T("已恢复: 自己 handicap 100%", "Restored: own handicap 100%"));
				}
				else
				{
					Experiments.NotifyExp(Experiments.Msgs.NotLobby());
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("恢复评分折扣失败: " + ex.Message));
			}
		}
}

public class TreehouseSuicide : ITweak {
    private static MainPlugin _mp;
    private static ConfigEntry<KeyCode> _keybind;
    public static bool Enabled = true;
    public void Initialize(MainPlugin plugin) {
        _mp = plugin;
        ConfigEntry<bool> enabled = _mp.Config.Bind("Quick Adjust", "Enabled", false, "总开关");
        Enabled = enabled.Value;
        enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
        _keybind = _mp.Config.Bind(
            "Quick Adjust",
            "Keybind",
            KeyCode.Alpha0,
            "自杀键（组合键设置：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键，即可设为组合键，如 Shift+0）");
        //配置兼容：旧默认 P 迁移到 Shift+0（用户自定义过的不动）。
        if (_keybind.Value == KeyCode.P)
        {
            _keybind.Value = KeyCode.Alpha0;
        }
        //未设置过修饰键时默认 Shift（默认 Shift+0）。
        SR.RegisterShiftKey("快捷自杀", _keybind, "hold");
        if (SR.KeyComboMod(_keybind) == SR.ComboMod.None)
        {
            SR.SetDefaultCombo(_keybind, SR.ComboMod.Shift);
        }
        Harmony.CreateAndPatchAll(typeof(TreehouseSuicide), (string)null);
    }

    [HarmonyPatch(typeof(Character), "FixedUpdate")]
    [HarmonyPostfix]
    private static void CharacterPatch(Character __instance) {
        if (!SR.GateMaster) return;
        if (!Enabled) return;
        if (SR.UiOpen && SR.BlockInput) return;
        if (!__instance.hasAuthority) return;
        //组合键判定：修饰键在键位捕捉时设置（默认 Shift）。
        if (SR.ComboKeyDown(_keybind) && !__instance.Frozen) {
            __instance.KillCharacter("Suicide", false, __instance.networkNumber);
        }
    }
}
}
