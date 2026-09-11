using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {

public class Experiments : ITweak
	{
		//高频重复消息集中缓存（运行时首次调用生成一次，编译期 #US 堆只存一份字面量，
		//避免同一文本在多处重复进字符串堆，减小 DLL 体积）
		internal static class Msgs {
			private static string _masterOff;
			private static string _notTreehouse;
			private static string _questionLock;
			private static string _noLocal;
			private static string _notLobby;
			internal static string MasterOff() {
				if (_masterOff == null) _masterOff = SR.T("本 Mod 总开关已关闭", "This mod's master switch is off");
				return _masterOff;
			}
			internal static string NotTreehouse() {
				if (_notTreehouse == null) _notTreehouse = SR.T("不在树屋大厅", "Not in the treehouse lobby");
				return _notTreehouse;
			}
			internal static string QuestionLock() {
				if (_questionLock == null) _questionLock = SR.T("树屋问号需 A 组进度解锁：游戏时长 > 17时16分18秒 或 奔跑长度 > 52000米（实验页查看进度）", "Question marks need Group A unlock: playtime > 17h16m18s or distance > 52000m (see Experiments page)");
				return _questionLock;
			}
			internal static string NoLocal() {
				if (_noLocal == null) _noLocal = SR.T("找不到本地玩家", "Cannot find the local player");
				return _noLocal;
			}
			internal static string NotLobby() {
				if (_notLobby == null) _notLobby = SR.T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player");
				return _notLobby;
			}
		}

		private static ConfigEntry<int> _scoreDiscount;

		private static ConfigEntry<bool> _moreDiscount;

		//重载关卡模式：KeepScore（保留方块和分数） / KeepBlocksOnly（仅保留方块，分数重置）
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

		private static ConfigEntry<ReloadMode> _reloadMode;

		private static ConfigEntry<bool> _gridAlwaysOn;

		private static ConfigEntry<GameState.LevelName> _questionLevel;

		private static ConfigEntry<bool> _questionEnabled;

		//树屋问号自管解锁记录：添加问号时记住 (玩家 → 要给的解锁)，进关卡时
		//强制注入 GameState.nextUnlocks。游戏原版在 checkForAvailableUnlocks（进树屋/
		//关卡切换时清空 nextUnlocks）和 RpcSetNextLevel（UnlockInLevel 不匹配时清空）会
		//丢掉 mod 手填的条目 → 关卡里没有解锁盒子。这里在 ProcessNextUnlocks（进关卡后
		//遍历 nextUnlocks 发 UnlockAvailable → 生成盒子）前补回。
		private static readonly Dictionary<LobbyPlayer, UnLockInfo> _questionUnlocks = new Dictionary<LobbyPlayer, UnLockInfo>();

		[HarmonyPatch(typeof(GameControl), "ProcessNextUnlocks")]
		[HarmonyPrefix]
		private static void InjectQuestionUnlocks() {
			try {
				if (_questionUnlocks.Count == 0) return;
				IDictionary<LobbyPlayer, UnLockInfo> next = GameState.GetInstance().nextUnlocks;
				if (next == null) return;
				foreach (KeyValuePair<LobbyPlayer, UnLockInfo> kv in _questionUnlocks) {
					if (kv.Key == null || kv.Value == null) continue;
					if (!next.ContainsKey(kv.Key)) next[kv.Key] = kv.Value;
				}
			} catch (Exception __ex) { SR.Guard.Log("Experiments.GetInstance", __ex); }
		}

		[HarmonyPatch(typeof(GameControl), "ProcessNextUnlocks")]
		[HarmonyPostfix]
		private static void ClearQuestionUnlocksAfterInject() {
			_questionUnlocks.Clear();
		}

		private static ConfigEntry<bool> _quickSwitchEnabled;

		private static ConfigEntry<float> _quickSwitchHold;

		private static ConfigEntry<bool> _quickRetryEnabled;

		private static ConfigEntry<bool> _quickRetryFast;

		private static ConfigEntry<float> _quickRetryHoldTime;

		private static string _statText = "";
		private static bool _reloadBusy; //重载关卡进行中（防重复触发）
		private static float _lastSnapshotAt = -999f; //广播快照冷却
		private static float _reloadLoadedAt = -1f; //重载场景加载完成(FadeOut)时刻，用于确定何时可清 levelPortalXml
		private static float _reloadStartedAt = -1f; //发起 ReloadScene 时刻（超时兜底）

		public static bool QuestionMarkOn => _questionEnabled != null && _questionEnabled.Value;

		public static bool QuickSwitchOn => _quickSwitchEnabled != null && _quickSwitchEnabled.Value;

		public static float QuickSwitchHold => (_quickSwitchHold != null) ? Mathf.Clamp(_quickSwitchHold.Value, 0f, 2f) : 0.5f;

		public static bool QuickRetryOn => _quickRetryEnabled != null && _quickRetryEnabled.Value;

		public static bool QuickRetryFastOn => _quickRetryFast != null && _quickRetryFast.Value;

		public static float QuickRetryHold => (_quickRetryHoldTime != null) ? Mathf.Clamp(_quickRetryHoldTime.Value, 0f, 2f) : 0.5f;

		//「等待秒数」滑块的**游戏默认值**标注（快速调整页显示用）：
		//  · 长按 B 的等待阈值是 Character.SuicideTime —— 游戏里自由模式（切建造）与挑战模式（重试）
		//    都固定 0.5s（其它模式另有两档：有命可复活 0.5s / "放弃" 2.2s）。
		//  · 建造态长按 B 切回行动用的是 PiecePlacementCursor.SwitchTime —— 游戏代码从不给它赋值
		//    （prefab 序列化值），所以运行时从本地玩家光标上读实际值；读不到就只报已知的 0.5s。
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

		//重载关卡是否保留分数（模式一 KeepScore = 保留；模式二 KeepBlocksOnly = 不保留）
		public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

		//局内修改分数折扣 → 立即刷新局内计分板（ScoreLine 的 handicap 显示）。
		//游戏只在开局 GraphScoreBoard.SetPlayerCharacter 时调用 ScoreLine.SetHandicap，
		//局内改 handicap（GamePlayer.CmdSetPlayerHandicap → SyncVar setter）不会刷新计分板。
		//patch set_NetworkHandicap + OnDeserialize：只在 handicap 真正变化时刷新一次
		//（按实例记上次值；字典防累积）。核心刷新（找计分板/反射字段）也做静态缓存。
		private static readonly Dictionary<GamePlayer, int> _lastHandicapShown = new Dictionary<GamePlayer, int>();

		//计分板刷新用字段反射缓存（避免每次刷新重复 AccessTools.Field）
		private static FieldInfo _gsbField;
		private static FieldInfo _relField;

		[HarmonyPatch(typeof(GamePlayer), "set_NetworkHandicap")]
		[HarmonyPostfix]
		private static void RefreshScoreboardHandicap(GamePlayer __instance) {
			//服务器端/显式设置路径：set_NetworkHandicap 触发；房客端由 OnDeserialize 兜底
			RefreshScoreboardHandicapIfChanged(__instance);
		}

		//房客端计分板刷新兜底：SyncVar 反序列化（OnDeserialize）直接写 Handicap 字段、
		//不走 set_NetworkHandicap，所以房客收到广播时上面的补丁不触发 → 计分板不刷新。
		//这里在每次网络反序列化后检测 Handicap 变化，变了才刷新一次。
		[HarmonyPatch(typeof(GamePlayer), "OnDeserialize")]
		[HarmonyPostfix]
		private static void RefreshScoreboardHandicapClient(GamePlayer __instance) {
			RefreshScoreboardHandicapIfChanged(__instance);
		}

		//统一入口：handicap 与上次记录的值不同才真正刷新一次（setter 与反序列化都走这里）
		private static void RefreshScoreboardHandicapIfChanged(GamePlayer __instance) {
			try {
				if (__instance == null) return;
				int h = __instance.Handicap;
				int prev;
				if (_lastHandicapShown.TryGetValue(__instance, out prev) && prev == h) return;
				_lastHandicapShown[__instance] = h;
				//场景切换后 GamePlayer 会重建，旧引用残留；超限就整体清空，避免无限累积
				if (_lastHandicapShown.Count > 64) _lastHandicapShown.Clear();
				RefreshScoreboardHandicapCore(__instance);
			} catch (Exception __ex) { SR.Guard.Log("Experiments.RefreshScoreboardHandicapCore", __ex); }
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
					//VersusControl.graphScoreBoardInstance（对局计分板）
					VersusControl vc = gc as VersusControl;
					if (vc != null) {
						if (_gsbField == null) _gsbField = AccessTools.Field(typeof(VersusControl), "graphScoreBoardInstance");
						if (_gsbField != null) board = _gsbField.GetValue(vc) as GraphScoreBoard;
					}
				} catch (Exception __ex) { SR.Guard.Log("Experiments.RefreshScoreboardHandicapCore", __ex); }
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
					//SyncVar 未就绪时按 GamePlayer 在计分板中的槽位兜底（localNumber 与槽位一致）
					try {
						line = relDict[__instance.localNumber];
					} catch (Exception __ex) { SR.Guard.Log("Experiments.GetValue", __ex); }
				}
				if (line == null) return;
				line.SetHandicap(__instance.Handicap);
			} catch (Exception __ex) { SR.Guard.Log("Experiments.SetHandicap", __ex); }
		}

		public static bool GridAlwaysOn => _gridAlwaysOn != null && _gridAlwaysOn.Value;

		public void Initialize(MainPlugin plugin)
		{
			_scoreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<int>("快速调整", "Score Discount", 20, "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %（滑块 0-100，支持任意整数如 85 → handicap 15%；90 以上钳到 handicap 10 = 上限 90%；只影响自己）。\n默认 20（handicap 80%）；平衡板上自己那一行显示为对应百分比；可随时点“恢复原值”还原。");
			_moreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<bool>("快速调整", "More Discount Values", false, "更多折扣数值：选中后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。");
			_reloadMode = ((BaseUnityPlugin)plugin).Config.Bind<ReloadMode>("关卡", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
			_gridAlwaysOn = ((BaseUnityPlugin)plugin).Config.Bind<bool>("地图", "Grid Always On", false, "地图网格：建造（放置方块）阶段任何模式都显示网格；本开关让**自由模式对局**在行动阶段也保持网格（游戏默认行动阶段不显示）。\n其它模式行动阶段不会因本开关点亮网格。\n随开随关：在自由对局里开启立即淡入，关闭立即淡出。开关在控制台“地图”栏目里。");
			_gridAlwaysOn.SettingChanged += GridToggleChanged;
			if (_gridAlwaysOn.Value)
			{
				GridToggleChanged(null, null); //上次会话开启过：立即补一次生效
			}
			_questionEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Question Mark", false, "树屋问号总开关：给指定关卡的门添加问号（门内有解锁盒子；未解锁的关卡不能添加）。\n仅房主可操作；四个按钮（添加/删除/全部添加/清除全部）都受本开关控制。\n🔒 A 组进度解锁：需游戏时长 > 17时16分18秒 或 奔跑长度 > 52000米（未达标时开关不可开启）。");
			_questionLevel = ((BaseUnityPlugin)plugin).Config.Bind<GameState.LevelName>("实验", "Question Level", (GameState.LevelName)0, "要添加问号的关卡（配合树屋问号使用）");
			_quickSwitchEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("快速调整", "Quick Switch", false, "快速切换（仅自由模式）：长按 B 到设定等待秒数切换 行动↔建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickSwitchHold = ((BaseUnityPlugin)plugin).Config.Bind<float>("快速调整", "Quick Switch Hold", 0.5f, "快速切换的长按等待秒数（0-2，0=立即；自由模式长按 B 达到该秒数切换行动/建造）。");
			_quickRetryEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("快速调整", "Quick Retry", false, "死后自动重试（仅挑战模式）：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。");
			_quickRetryFast = ((BaseUnityPlugin)plugin).Config.Bind<bool>("快速调整", "Quick Retry Fast", false, "快速重试（仅挑战模式）：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。");
			_quickRetryHoldTime = ((BaseUnityPlugin)plugin).Config.Bind<float>("快速调整", "Quick Retry Hold", 0.5f, "快速重试的长按等待秒数（0-2，0=立即；挑战模式死亡后长按 B 达到该秒数自动重试）。");
			Harmony.CreateAndPatchAll(typeof(Experiments), (string)null);
		}

		//修改长按 B 等待时间：UpdateHoldBIndicator 正是设置 SuicideTime 的地方，在其 Postfix 覆盖
		//为用户值；同时每帧把建造状态的 SwitchTime 也设为用户值（双向切换都生效，含第一次进关卡）。
		[HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
		[HarmonyPostfix]
		private static void OverrideHoldTime(Character __instance)
		{
			if (!SR.GateMaster || (SR.UiOpen && SR.BlockInput) || !__instance.hasAuthority) return;
			try
			{
				//模式门控统一走 GateModeAllows（已接入 IgnoreModeLimit 豁免；原为硬判模式，缺陷 3）
				if (QuickSwitchOn && SR.GateModeAllows(SR.ModeMask.Freeplay))
				{
					__instance.SuicideTime = QuickSwitchHold; //自由模式长按 B 切换等待（行动→建造）
					//建造状态长按 B 切换回行动用的是 PiecePlacementCursor.SwitchTime，每帧同步
					foreach (Player p in PlayerManager.GetInstance()) {
						if (p == null || p.AssociatedGamePlayer == null) continue;
						PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
						if (pc != null) pc.SwitchTime = QuickSwitchHold;
					}
				}
				else if (QuickRetryFastOn && SR.GateModeAllows(SR.ModeMask.Challenge))
				{
					__instance.SuicideTime = QuickRetryHold; //挑战模式长按 B 重试等待
				}
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.OverrideHoldTime", __ex); }
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
			//挑战模式：死后自动重试（无需长按）。已接入 IgnoreModeLimit 豁免（原硬判模式，缺陷 3）
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
					catch (Exception __ex) { SR.Guard.Log("Experiments.Send", __ex); }
				}
				return;
			}
			if ((int)gm > 0)
			{
				return;
			}
			//自由模式：快速切换开关控制长按 B 等待（实际等待时间由 OverrideHoldTime 设置）
			if (!QuickSwitchOn) return;
			//仅对局中生效（自由模式）；树屋/大厅不处理
			try
			{
				if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
			}
			catch
			{
				return;
			}
			try { __instance.SuicideTime = QuickSwitchHold; } catch (Exception __ex) { SR.Guard.Log("Experiments.Send", __ex); }
			//建造状态长按 B 切换回行动用的是 PiecePlacementCursor.SwitchTime，也设为用户等待秒数
			try {
				foreach (Player p in PlayerManager.GetInstance()) {
					if (p == null || p.AssociatedGamePlayer == null) continue;
					PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
					if (pc != null) pc.SwitchTime = QuickSwitchHold;
				}
			} catch (Exception __ex) { SR.Guard.Log("Experiments.Send", __ex); }
		}

		//建造网格（Graphpaper = 建造阶段的网格背景）。
		//游戏原生靠 StartPhaseEvent 随阶段开关：PLACE→enableGrid、PLAY→disableGrid；
		//但“派对盒规则”对局(PARTY+partyBoxMode≠Disabled)会整段跳过、从不开关网格，
		//于是派对盒里原生既没有建造网格、也不会自动关掉已被点亮的网格。
		//本 mod 接管该事件，统一决定显隐：
		//  • 建造(PLACE)阶段：任何模式都显示网格（用于对齐方块，与开关无关）；
		//  • 行动(PLAY)阶段：默认按游戏原生隐藏；仅当“Grid Always On”开启且允许常驻
		//    （自由模式对局；或“无视模式限制”放开）时才保持显示。
		private static bool _gridPlacePhase;   // 最近一次阶段是否为建造(PLACE)

		//当前是否为自由模式**对局**（大厅/树屋不算，避免误开）
		private static bool GridFreePlayNow()
		{
			try {
				return LobbyManager.instance != null
					&& LobbyManager.instance.CurrentGameController is FreePlayControl;
			} catch { return false; }
		}

		//“行动阶段常驻”是否被允许：仅自由模式对局（“无视模式限制”开启 → 放开到任何模式）
		private static bool GridKeepAllowed()
		{
			if (SR.IgnoreModeLimit) return true;
			return GridFreePlayNow();
		}

		//接管阶段事件：按「阶段 + 模式 + 开关」决定网格显隐，覆盖原生（含派对盒）。
		[HarmonyPatch(typeof(Graphpaper), "handleEvent")]
		[HarmonyPrefix]
		private static bool GridAlwaysHandle(Graphpaper __instance, global::GameEvent.GameEvent e)
		{
			try
			{
				if (!SR.GateMaster) return true;
				StartPhaseEvent spe = e as StartPhaseEvent;
				if (spe == null) return true; // 非阶段事件 → 交给原方法（本就不处理）
				bool place = spe.Phase == GameControl.GamePhase.PLACE;
				bool play = spe.Phase == GameControl.GamePhase.PLAY;
				if (!place && !play) return true; // 其它阶段 → 交给原方法
				_gridPlacePhase = place;
				//建造阶段任何模式都显示；行动阶段仅当“Grid Always On && 允许常驻”时保持
				bool on = place
					|| (_gridAlwaysOn != null && _gridAlwaysOn.Value && GridKeepAllowed());
				if (on) __instance.enableGrid(); else __instance.disableGrid();
				return false; // 已接管，跳过原方法
			}
			catch { return true; }
		}

		//开关随开随关：建造阶段总显示；行动阶段按“Grid Always On && 允许常驻”决定
		private static void GridToggleChanged(object s, EventArgs e)
		{
			try
			{
				if (_gridAlwaysOn == null) return;
				bool on;
				if (_gridPlacePhase) on = true;                       // 正在建造 → 保持网格
				else on = _gridAlwaysOn.Value && GridKeepAllowed();   // 行动/未知阶段
				foreach (Graphpaper gp in UnityEngine.Object.FindObjectsOfType<Graphpaper>())
				{
					if (gp == null) continue;
					if (on) gp.enableGrid(); else gp.disableGrid();
				}
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.GridKeepAllowed", __ex); }
		}

		public static void ApplyScoreDiscount()
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				int scoreDiscount = ScoreDiscount;
				if (scoreDiscount <= 0)
				{
					NotifyExp(SR.T("评分折扣未设置（0 = 关闭）", "Score discount not set (0 = off)"));
					return;
				}
				int target = Mathf.Clamp(100 - scoreDiscount, 10, 100);
				if (ApplyOwnHandicap(target))
				{
					NotifyExp(SR.T("评分折扣已应用: 自己 handicap ", "Score discount applied: own handicap ") + target + SR.T("%（平衡板可见，本局立即生效）", "% (visible on the scoreboard, takes effect this round)"));
				}
				else
				{
					NotifyExp(Msgs.NotLobby());
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("评分折扣失败: " + ex.Message));
			}
		}

		//把本地玩家的 handicap 设为指定值：LobbyPlayer（平衡板/下一局）+ GamePlayer（本局立即生效）
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
						catch (Exception __ex) { SR.Guard.Log("Experiments.CallCmdSetPlayerHandicap", __ex); }
					}
					return true;
				}
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.CallCmdSetPlayerHandicap", __ex); }
			return false;
		}

		//恢复：handicap 回 100
		public static void RestoreScoreDiscount()
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				if (ApplyOwnHandicap(100))
				{
					NotifyExp(SR.T("已恢复: 自己 handicap 100%", "Restored: own handicap 100%"));
				}
				else
				{
					NotifyExp(Msgs.NotLobby());
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("恢复评分折扣失败: " + ex.Message));
			}
		}

		private static void NotifyExp(string text)
		{
			try
			{
				UserMessageManager.Instance.UserMessage(text, false);
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.UserMessage", __ex); }
			MainPlugin.ModLogger.LogInfo((object)("[实验] " + text));
		}

		public static string ReadStatsText()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null)
				{
					return SR.T("存档系统不可用", "Save system unavailable");
				}
				SaveFileData saveFileDataForMainUser = instance.GetSaveFileDataForMainUser();
				if (saveFileDataForMainUser == null)
				{
					return SR.T("存档不可用", "Save unavailable");
				}
				StringBuilder stringBuilder = new StringBuilder();
				stringBuilder.Append(SR.T("对局次数: ", "Games played: ") + Count(saveFileDataForMainUser, "GamesPlayed") + "\n");
				stringBuilder.Append(SR.T("在线对局: ", "Online games: ") + Count(saveFileDataForMainUser, "OnlineGamesPlayed") + "\n");
				stringBuilder.Append(SR.T("派对对局: ", "Party games: ") + Count(saveFileDataForMainUser, "PartyModeGamesPlayed") + "\n");
				stringBuilder.Append(SR.T("创造性对局: ", "Creative games: ") + Count(saveFileDataForMainUser, "CreativeModeGamesPlayed") + "\n");
				stringBuilder.Append(SR.T("沙盒对局: ", "Sandbox games: ") + Count(saveFileDataForMainUser, "SandboxModeGamesPlayed") + "\n");
				stringBuilder.Append(SR.T("游戏时长: ", "Play time: ") + FmtTime(Float(saveFileDataForMainUser, "TotalMatchTime")) + "\n");
				stringBuilder.Append(SR.T("奔跑长度: ", "Distance run: ") + FmtDist(Float(saveFileDataForMainUser, "DistanceRun")) + "\n");
				stringBuilder.Append(SR.T("总死亡: ", "Total deaths: ") + Count(saveFileDataForMainUser, "TotalDeaths") + "\n");
				stringBuilder.Append(SR.T("金币: ", "Coins: ") + Count(saveFileDataForMainUser, "CoinsCollected"));
				_statText = stringBuilder.ToString();
				return _statText;
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("读取统计失败: " + ex.Message));
				return SR.T("读取失败: ", "Read failed: ") + ex.Message;
			}
		}

		private static int Count(SaveFileData data, string key)
		{
			try
			{
				return data.GetStat<StatCount>(key).count;
			}
			catch
			{
				return 0;
			}
		}

		private static float Float(SaveFileData data, string key)
		{
			try
			{
				return data.GetStat<StatFloat>(key).value;
			}
			catch
			{
				return 0f;
			}
		}

		private static string FmtTime(float seconds)
		{
			int num = Mathf.RoundToInt(seconds);
			return num / 3600 + SR.T("时 ", "h ") + num % 3600 / 60 + SR.T("分 ", "m ") + num % 60 + SR.T("秒", "s");
		}

		private static string FmtDist(float units)
		{
			return Mathf.RoundToInt(units) + " m";
		}

		//进度解锁（A 组）：游戏时长 > 17时16分18秒 或 奔跑长度 > 52000 米时，解除
		//建造增强（无视碰撞）、树屋问号的禁用限制。
		public static readonly float UnlockTimeSeconds = 17f * 3600f + 16f * 60f + 18f; //17时16分18秒 = 62178 秒
		public static readonly float UnlockDistanceMeters = 52000f;

		//进度解锁（B 组）：游戏时长 > 52时 或 奔跑长度 > 100000 米时，解除
		//方块破坏的禁用限制。
		public static readonly float UnlockBTimeSeconds = 52f * 3600f; //52时 = 187200 秒
		public static readonly float UnlockBDistanceMeters = 100000f;

		//进度数据是否就绪（StatTracker 与主用户存档都可用时才算）。
		//未就绪时 IsProgressionUnlocked/IsProgressionUnlockedB 会保守返回 false，
		//但强制复位等操作应跳过，避免在存档加载完成前误伤已解锁用户。
		public static bool ProgressionDataReady()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null) return false;
				return instance.GetSaveFileDataForMainUser() != null;
			}
			catch
			{
				return false;
			}
		}

		public static bool IsProgressionUnlocked()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null) return false;
				SaveFileData data = instance.GetSaveFileDataForMainUser();
				if (data == null) return false;
				float time = Float(data, "TotalMatchTime");
				float dist = Float(data, "DistanceRun");
				return time > UnlockTimeSeconds || dist > UnlockDistanceMeters;
			}
			catch
			{
				return false;
			}
		}

		public static bool IsProgressionUnlockedB()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null) return false;
				SaveFileData data = instance.GetSaveFileDataForMainUser();
				if (data == null) return false;
				float time = Float(data, "TotalMatchTime");
				float dist = Float(data, "DistanceRun");
				return time > UnlockBTimeSeconds || dist > UnlockBDistanceMeters;
			}
			catch
			{
				return false;
			}
		}

		//当前进度文本（实验页显示：A/B 两组当前时长/距离，距解锁还差多少）
		public static string ProgressionText()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null) return SR.T("存档系统不可用", "Save system unavailable");
				SaveFileData data = instance.GetSaveFileDataForMainUser();
				if (data == null) return SR.T("存档不可用", "Save unavailable");
				float time = Float(data, "TotalMatchTime");
				float dist = Float(data, "DistanceRun");
				bool unlockedA = time > UnlockTimeSeconds || dist > UnlockDistanceMeters;
				bool unlockedB = time > UnlockBTimeSeconds || dist > UnlockBDistanceMeters;
				string s = SR.T("游戏时长: ", "Play time: ") + FmtTime(time) + " / " + FmtTime(UnlockBTimeSeconds)
					+ "\n" + SR.T("奔跑长度: ", "Distance run: ") + FmtDist(dist) + " / " + FmtDist(UnlockBDistanceMeters)
					+ "\n" + SR.T("A组（无视碰撞/自由放置/树屋问号）: ", "Group A (ignore collision / free placement / question marks): ") + (unlockedA ? SR.T("✅ 已解锁", "✅ Unlocked") : SR.T("🔒 需时长 > 17时16分18秒 或 长度 > 52000米", "🔒 need >17h16m18s or >52000m"))
					+ "\n" + SR.T("B组（方块破坏）: ", "Group B (destroy blocks): ") + (unlockedB ? SR.T("✅ 已解锁", "✅ Unlocked") : SR.T("🔒 需时长 > 52时 或 长度 > 100000米", "🔒 need >52h or >100000m"));
				return s;
			}
			catch (Exception ex)
			{
				return SR.T("读取进度失败: ", "Read failed: ") + ex.Message;
			}
		}

		public static void ApplyQuestionMark()
		{
			//IL_003f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_028b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0222: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				if (!IsProgressionUnlocked())
				{
					NotifyExp(Msgs.QuestionLock());
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(SR.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(SR.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				GameState.LevelName val = (GameState.LevelName)((_questionLevel != null) ? ((int)_questionLevel.Value) : 0);
				LevelSelectController val2 = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val2 = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					val2 = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || val2.portals == null)
				{
					NotifyExp(Msgs.NotTreehouse());
					return;
				}
				LevelPortal val3 = null;
				LevelPortal[] portals = val2.portals;
				foreach (LevelPortal val4 in portals)
				{
					if (!((UnityEngine.Object)(object)val4 == (UnityEngine.Object)null) && val4.TargetLevel == val)
					{
						val3 = val4;
						break;
					}
				}
				if ((UnityEngine.Object)(object)val3 == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("树屋没有该关卡的门", "The treehouse has no portal for this level"));
					return;
				}
				bool flag = false;
				try
				{
					flag = val3.Locked;
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				if (flag)
				{
					NotifyExp(SR.T("该关卡尚未解锁，不能添加问号", "This level is not unlocked yet; cannot add a question mark"));
					return;
				}
				LobbyPlayer val5 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val5 == (UnityEngine.Object)null)
				{
					NotifyExp(Msgs.NoLocal());
					return;
				}
				if (!FillNextUnlock(val2, val5))
				{
					NotifyExp(SR.T("所有物品已解锁，没有可获取的新物品，不添加问号", "Everything is already unlocked; no new items available, no question mark added"));
					return;
				}
				bool flag2 = false;
				try
				{
					flag2 = NetworkServer.active && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && LobbyManager.instance.IsHost;
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				if (flag2)
				{
					MethodInfo methodInfo = AccessTools.Method(typeof(LevelSelectController), "SetUnlockForPlayer", (Type[])null, (Type[])null);
					if (methodInfo != null)
					{
						methodInfo.Invoke(val2, new object[2] { val5, val });
					}
					//关键：同步 UnlockInLevel（游戏原版走 SendUnlockMessageFromClient 会设置它）。
					//进入关卡时 RpcSetNextLevel 依赖 UnlockInLevel==nextLevel 才不清空 nextUnlocks，
					//否则 ProcessNextUnlocks 拿不到解锁物品 → 关卡里不生成解锁盒子。
					val2.UnlockInLevel = val;
					NotifyExp(string.Format(SR.T("已给 {0} 添加问号", "Added question mark to {0}"), val));
				}
				else
				{
					MethodInfo methodInfo2 = AccessTools.Method(typeof(LevelSelectController), "SendUnlockMessageFromClient", (Type[])null, (Type[])null);
					if (methodInfo2 != null)
					{
						methodInfo2.Invoke(val2, new object[2] { val5, val });
					}
					NotifyExp(string.Format(SR.T("已请求房主给 {0} 添加问号", "Asked the host to add a question mark to {0}"), val));
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("添加问号失败: " + ex.Message));
			}
		}

		private static bool FillNextUnlock(LevelSelectController lsc, LobbyPlayer me)
		{
			UnLockInfo item;
			if (!TryPickUnlock(lsc, out item)) return false;
			IDictionary<LobbyPlayer, UnLockInfo> nextUnlocks = GameState.GetInstance().nextUnlocks;
			if (nextUnlocks == null) return false;
			nextUnlocks[me] = item;
			_questionUnlocks[me] = item; //自管记录：防被游戏清空
			return true;
		}

		//纯挑选：用主用户（本端玩家自己）存档挑一个"该关卡中自己还没解锁"的物品。
		//不写 nextUnlocks/_questionUnlocks，供树屋按关卡预填（每个客户端为自己挑，互不干扰）。
		private static bool TryPickUnlock(LevelSelectController lsc, out UnLockInfo result)
		{
			//IL_0254: Unknown result type (might be due to invalid IL or missing references)
			//IL_025b: Expected I4, but got Unknown
			result = null;
			try
			{
				SaveFileData val = null;
				try
				{
					StatTracker instance = StatTracker.Instance;
					if (instance != null)
					{
						val = instance.GetSaveFileDataForMainUser();
					}
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.GetSaveFileDataForMainUser", __ex); }
				if (val == null)
				{
					return false;
				}
				try
				{
					FieldInfo fieldInfo = AccessTools.Field(typeof(LevelSelectController), "CharacterUnlocks");
					if (fieldInfo != null)
					{
						UnLockInfo[] array = fieldInfo.GetValue(lsc) as UnLockInfo[];
						bool[] values = val.GetStat<StatBoolArray>("CharactersUnlocked").values;
						if (array != null && values != null)
						{
							for (int i = 0; i < array.Length && i < values.Length; i++)
							{
								if ((UnityEngine.Object)(object)array[i] != (UnityEngine.Object)null && !values[i])
								{
									result = array[i];
									return true;
								}
							}
						}
					}
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.GetValue", __ex); }
				try
				{
					FieldInfo fieldInfo2 = AccessTools.Field(typeof(LevelSelectController), "LevelUnlocks");
					if (fieldInfo2 != null)
					{
						UnLockInfo[] array2 = fieldInfo2.GetValue(lsc) as UnLockInfo[];
						bool[] values2 = val.GetStat<StatBoolArray>("LevelsUnlocked").values;
						if (array2 != null && values2 != null)
						{
							for (int j = 0; j < array2.Length && j < values2.Length; j++)
							{
								if ((UnityEngine.Object)(object)array2[j] != (UnityEngine.Object)null && !values2[j])
								{
									result = array2[j];
									return true;
								}
							}
						}
					}
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.GetValue", __ex); }
				try
				{
					FieldInfo fieldInfo3 = AccessTools.Field(typeof(LevelSelectController), "OutfitUnlocks");
					if (fieldInfo3 != null)
					{
						UnLockInfo[] array3 = fieldInfo3.GetValue(lsc) as UnLockInfo[];
						int[] values3 = val.GetStat<StatCountArray>("OutfitsUnlocked").values;
						if (array3 != null && values3 != null)
						{
							for (int k = 0; k < array3.Length; k++)
							{
								if ((UnityEngine.Object)(object)array3[k] == (UnityEngine.Object)null)
								{
									continue;
								}
								int num = (int)array3[k].AssociatedCharacter;
								if (num >= 0 && num < values3.Length)
								{
									int num2 = 0;
									try
									{
										num2 = array3[k].OutfitMaskNumber;
									}
									catch (Exception __ex) { SR.Guard.Log("Experiments.GetValue", __ex); }
									if ((values3[num] & num2) == 0)
									{
										result = array3[k];
										return true;
									}
								}
							}
						}
					}
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.GetValue", __ex); }
				return false;
			}
			catch
			{
				return false;
			}
		}

		public static void RemoveQuestionMark()
		{
			//IL_0011: Unknown result type (might be due to invalid IL or missing references)
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_0152: Unknown result type (might be due to invalid IL or missing references)
			//IL_0154: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_0203: Unknown result type (might be due to invalid IL or missing references)
			//IL_0208: Unknown result type (might be due to invalid IL or missing references)
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				if (!IsProgressionUnlocked())
				{
					NotifyExp(Msgs.QuestionLock());
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(SR.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(SR.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				GameState.LevelName val = (GameState.LevelName)((_questionLevel != null) ? ((int)_questionLevel.Value) : 0);
				LevelSelectController val2 = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val2 = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					val2 = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || val2.portals == null)
				{
					NotifyExp(Msgs.NotTreehouse());
					return;
				}
				if (!isHostExp)
				{
					//联机房客：游戏原生没有“删除问号”的网络消息（NetMsgTypes 只有 PortalHasUnlock 是添加），
					//SyncVar levelHasUnlock 只有房主能写；房主未装模组无法代执行 → 明确提示而非假生效。
					NotifyExp(SR.T("联机无法删除问号：游戏没有删除问号的网络消息，房主未装模组无法代执行", "Cannot remove question marks online: the game has no network message for removal, and the host cannot do it without the mod"));
					return;
				}
				LobbyPlayer val3 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val3 == (UnityEngine.Object)null)
				{
					NotifyExp(Msgs.NoLocal());
					return;
				}
				FieldInfo fieldInfo = AccessTools.Field(typeof(LevelSelectController), "unlockQuestionMarks");
				if (fieldInfo == null)
				{
					return;
				}
				object value = fieldInfo.GetValue(val2);
				if (value == null)
				{
					return;
				}
				IDictionary<uint, GameState.LevelName> dictionary = (IDictionary<uint, GameState.LevelName>)value;
				bool flag = false;
				List<uint> list = new List<uint>(dictionary.Keys);
				foreach (uint item in list)
				{
					if (dictionary.TryGetValue(item, out var value2) && value2 == val)
					{
						dictionary.Remove(item);
						flag = true;
					}
				}
				bool flag2 = false;
				foreach (KeyValuePair<uint, GameState.LevelName> item2 in dictionary)
				{
					if (item2.Value == val)
					{
						flag2 = true;
						break;
					}
				}
				if (!flag2)
				{
					LevelPortal[] portals = val2.portals;
					foreach (LevelPortal val4 in portals)
					{
						if (!((UnityEngine.Object)(object)val4 == (UnityEngine.Object)null) && val4.TargetLevel == val)
						{
							val4.NetworklevelHasUnlock = false;
							break;
						}
					}
				}
				//删除本地玩家的问号解锁记录（防止进关卡后仍注入解锁盒子）
				List<LobbyPlayer> removeKeys = null;
				foreach (KeyValuePair<LobbyPlayer, UnLockInfo> item3 in _questionUnlocks)
				{
					if (item3.Key != null && item3.Key.playerNodeID == (val3 != null ? val3.playerNodeID : 0))
					{
						if (removeKeys == null) removeKeys = new List<LobbyPlayer>();
						removeKeys.Add(item3.Key);
					}
				}
				if (removeKeys != null)
				{
					foreach (LobbyPlayer rk in removeKeys) _questionUnlocks.Remove(rk);
				}
				NotifyExp(flag ? string.Format(SR.T("已删除 {0} 的问号", "Removed the question mark from {0}"), val) : SR.T("该关卡没有问号", "This level has no question mark"));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("删除问号失败: " + ex.Message));
			}
		}

		public static void ClearQuestionMarks()
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				if (!IsProgressionUnlocked())
				{
					NotifyExp(Msgs.QuestionLock());
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(SR.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(SR.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				LevelSelectController val = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
				{
					val = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null || val.portals == null)
				{
					NotifyExp(Msgs.NotTreehouse());
					return;
				}
				if (!isHostExp)
				{
					//联机房客：游戏原生没有“删除问号”的网络消息，SyncVar 只有房主能写 → 明确提示
					NotifyExp(SR.T("联机无法清除问号：游戏没有删除问号的网络消息，房主未装模组无法代执行", "Cannot clear question marks online: the game has no network message for removal, and the host cannot do it without the mod"));
					return;
				}
				FieldInfo fieldInfo = AccessTools.Field(typeof(LevelSelectController), "unlockQuestionMarks");
				if (fieldInfo == null)
				{
					return;
				}
				object value = fieldInfo.GetValue(val);
				if (value == null)
				{
					return;
				}
				IDictionary<uint, GameState.LevelName> dictionary = (IDictionary<uint, GameState.LevelName>)value;
				dictionary.Clear();
				_questionUnlocks.Clear(); //清除问号记录：不再注入解锁
				LevelPortal[] portals = val.portals;
				foreach (LevelPortal val2 in portals)
				{
					if (!((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null))
					{
						val2.NetworklevelHasUnlock = false;
					}
				}
				NotifyExp(SR.T("已一键清除所有问号", "Cleared all question marks"));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("清除问号失败: " + ex.Message));
			}
		}

		public static void AddAllQuestionMarks()
		{
			//IL_01e6: Unknown result type (might be due to invalid IL or missing references)
			//IL_019a: Unknown result type (might be due to invalid IL or missing references)
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				if (!IsProgressionUnlocked())
				{
					NotifyExp(Msgs.QuestionLock());
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(SR.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(SR.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				LevelSelectController val = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
				{
					val = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null || val.portals == null)
				{
					NotifyExp(Msgs.NotTreehouse());
					return;
				}
				LobbyPlayer val2 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					NotifyExp(Msgs.NoLocal());
					return;
				}
				bool flag = false;
				try
				{
					flag = NetworkServer.active && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && LobbyManager.instance.IsHost;
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
				if (!FillNextUnlock(val, val2))
				{
					NotifyExp(SR.T("所有物品已解锁，没有可获取的新物品，不添加问号", "Everything is already unlocked; no new items available, no question mark added"));
					return;
				}
				int num = 0;
				int num2 = 0;
				LevelPortal[] portals = val.portals;
				foreach (LevelPortal val3 in portals)
				{
					if ((UnityEngine.Object)(object)val3 == (UnityEngine.Object)null)
					{
						continue;
					}
					//跳过不合适的门：自定义关卡门 / 空白 / 随机 / 原型（与问号关卡下拉框过滤一致）
					if (val3 is CustomLevelPortal)
					{
						num2++;
						continue;
					}
					GameState.LevelName qlevel = val3.TargetLevel;
					if (qlevel == GameState.LevelName.BLANKLEVEL || (int)qlevel >= (int)GameState.LevelName.RANDOM)
					{
						num2++;
						continue;
					}
					bool flag2 = false;
					try
					{
						flag2 = val3.Locked;
					}
					catch (Exception __ex) { SR.Guard.Log("Experiments.NotifyExp", __ex); }
					if (flag2)
					{
						num2++;
						continue;
					}
					if (flag)
					{
						MethodInfo methodInfo = AccessTools.Method(typeof(LevelSelectController), "SetUnlockForPlayer", (Type[])null, (Type[])null);
						if (methodInfo != null)
						{
							methodInfo.Invoke(val, new object[2] { val2, val3.TargetLevel });
						}
						val.UnlockInLevel = val3.TargetLevel; //同 Apply：同步 UnlockInLevel 防止进关后 nextUnlocks 被清空
					}
					else
					{
						MethodInfo methodInfo2 = AccessTools.Method(typeof(LevelSelectController), "SendUnlockMessageFromClient", (Type[])null, (Type[])null);
						if (methodInfo2 != null)
						{
							methodInfo2.Invoke(val, new object[2] { val2, val3.TargetLevel });
						}
					}
					num++;
				}
				NotifyExp(flag ? string.Format(SR.T("已为全部 {0} 个已解锁关卡添加问号（跳过 {1} 个未解锁）", "Added question marks to all {0} unlocked levels (skipped {1} locked)"), num, num2) : string.Format(SR.T("已请求房主为全部 {0} 个已解锁关卡添加问号（跳过 {1} 个未解锁）", "Asked the host to add question marks to all {0} unlocked levels (skipped {1} locked)"), num, num2));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("全部添加问号失败: " + ex.Message));
			}
		}

		private static bool IsHostExp()
		{
			//统一房主判定（原为 NetworkServer.active && LobbyManager.IsHost）。见 SR.Gate.Service.cs。
			//行为变更：离线/无连接现在也算房主（与 DestroyBlocks 原语义一致）。
			return SR.IsHost;
		}

		//（旧 BroadcastSnapshot 方法体已并入 RebuildBlocksFromHost 共享核心）

		//重载关卡：**真正重载当前关卡场景**（ReloadScene → 全员重新加载场景）。
		// - 方块：重载前把房主当前快照写入 QuickSaver.levelPortalXml（static）→ 重载后
		//   房主 OnSetupStartLevel 读到它 → 房主加载当前方块 + 原生 CompressAndSendSnapshotBytes
		//   广播给所有客户端（游戏原生 ClientRpc，无 mod 房客也重建，含玩家放置的/可移动的）
		// - 分数：重载走 PrepareToReloadScene → SR 保分 patch（房主端按 networkNumber 恢复；
		//   未装 mod 的房客端原生重载会清分——游戏架构限制）
		//⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效；仅房主有效（ReloadScene 是服务器操作）。
		public static void ReloadLevel()
		{
			ReloadLevelImpl(SR.T("重载关卡", "Reload Level"));
		}

		//共享核心（与重载关卡一致的模式限制 + 快照写入 levelPortalXml）
		private static void ReloadLevelImpl(string feature)
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				//仅派对/创意局内生效
				if (!IsPartyOrCreative())
				{
					NotifyExp(feature + SR.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!IsHostExp())
				{
					NotifyExp(SR.T("仅房主有效：", "Host only: ") + feature + SR.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				GameState gs = GameState.GetInstance();
				if (gs == null)
				{
					NotifyExp(SR.T("游戏状态不可用", "Game state unavailable"));
					return;
				}
				//防重入：真正开干(生成快照/写 levelPortalXml/发 PrepareToReloadScene)之前就锁住，
				//连点/重入直接挡掉，而不是做完这些动作后才在末尾拦。
				if (_reloadBusy) { NotifyExp(SR.T("正在重载，请稍候", "Reload in progress")); return; }
				//C1: 刚广播过快照（3 秒内）先别重载场景，等广播的方块重建落定再动
				if (Time.unscaledTime - _lastSnapshotAt < 3f) {
					NotifyExp(SR.T("快照刚广播，请稍候再重载", "A snapshot was just broadcast; wait a moment before reloading"));
					return;
				}
				_reloadBusy = true;
				//1) 生成房主当前快照 XML → 写入 QuickSaver.levelPortalXml（static）：
				//   重载后房主 OnSetupStartLevel 读它 → 加载当前方块 + 原生广播全员
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
				{
					NotifyExp(SR.T("生成快照失败（当前场景无方块数据）", "Failed to generate snapshot (no block data in the current scene)"));
					return;
				}
				try
				{
					System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
					if (lpx != null) lpx.SetValue(null, doc.OuterXml);
				}
				catch
				{
					NotifyExp(SR.T("写入快照失败", "Failed to write snapshot"));
					return;
				}
				//2) 发送 PrepareToReloadScene。
				//   - 模式一（保留方块和分数 = 允许补分）：置保分标志 → Setup 时备份分块列表 →
				//     重载完成后按原类型广播补分 → 全员下一回合结算显示真分数
				//   - 模式二（仅保留方块 = 跳过补分）：不置标志 → 重载后分数重置（重新对局）
				if (ReloadKeepsScore) SR.MarkPreserveScores();
				try
				{
					MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene
					{
						reloadToMode = GameSettings.GetInstance().GameMode,
						snapshotInfo = gs.currentSnapshotInfo
					};
					NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.SendToAll", __ex); }
				if (LoadingInterstitialSplash.Instance != null)
				{
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				//3) 真正重载场景（全员重新加载；房主 OnSetupStartLevel 自动加载+广播当前方块）
				lm.StartCoroutine(ReloadSceneRoutine());
				NotifyExp(feature + SR.T("：全员重载关卡，方块保留，", ": reloading the level for everyone; blocks kept, ") +
					(ReloadKeepsScore ? SR.T("分数补回", "scores restored") : SR.T("分数重置", "scores reset")));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)(feature + "失败: " + ex.Message));
			}
		}

		//仅派对(PARTY)/创意(CREATIVE)局内生效
		private static bool IsPartyOrCreative()
		{
			//统一模式门控（IgnoreModeLimit 豁免已内置；原重载/广播硬判模式，缺陷 3）
			return SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative);
		}

		private static IEnumerator ReloadSceneRoutine()
		{
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame(); //等加载画面 FadeIn 生效
			bool ok = false;
			try
			{
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
				{
					lm.ReloadScene(GameSettings.GetInstance().GameMode);
					ok = true;
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("重载场景失败: " + ex.Message));
			}
			_reloadStartedAt = Time.unscaledTime;
			_reloadLoadedAt = -1f; //等待重载场景真正加载完成（LoadingInterstitialSplash.FadeOut）来置位
			//重载成功后，等「新场景加载完成(FadeOut)」再多等 2 秒，确保房主 OnSetupStartLevel
			//已读到 levelPortalXml 里的方块，才清掉它并复位 busy——不再用固定的 6 秒硬等，
			//慢房主/大地图也不会因 6 秒不够而在 OnSetupStartLevel 读取前被误清（丢方块）。
			//若 FadeOut 迟迟不触发（异常/无加载画面），25 秒硬上限兜底，避免一直锁死无法再重载。
			if (ok)
			{
				while (_reloadBusy)
				{
					if (_reloadLoadedAt >= 0f && Time.unscaledTime - _reloadLoadedAt >= 2f) break;
					if (Time.unscaledTime - _reloadStartedAt >= 25f) break;
					yield return null;
				}
			}
			ClearPortalXml();
			_reloadBusy = false;
		}

		//重载场景已加载完成（LoadingInterstitialSplash.FadeOut，由 SR.Reload.OnLoadEnd 调用）：
		//记录时刻，供 ReloadSceneRoutine 确定何时清 levelPortalXml。
		public static void OnReloadSceneLoaded()
		{
			_reloadLoadedAt = Time.unscaledTime;
		}

		//清掉写入的 QuickSaver.levelPortalXml（static）
		private static void ClearPortalXml()
		{
			try
			{
				System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
				if (lpx != null) lpx.SetValue(null, null);
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.Field", __ex); }
		}

		//广播方块快照：房主重发当前关卡快照 → 全员按房主视角重建方块（修复方块消失/不同步）。
		public static void BroadcastSnapshot()
		{
			RebuildBlocksFromHost(SR.T("广播方块快照", "Broadcast Snapshot"));
		}

		//共享核心：生成房主当前快照 XML → CompressAndSendSnapshotBytes 广播 → 全员重建。
		//不重载场景 → 分数保留；方块按房主当前视角全部重建（含属性/胶水/玩家放置的）。
		//⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效。
		private static void RebuildBlocksFromHost(string feature)
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
					return;
				}
				//仅派对/创意局内生效
				if (!IsPartyOrCreative())
				{
					NotifyExp(feature + SR.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!IsHostExp())
				{
					NotifyExp(SR.T("仅房主有效：", "Host only: ") + feature + SR.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					NotifyExp(SR.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				//C1: 重载场景进行中不放广播快照（防与场景重载打架）
				if (_reloadBusy) { NotifyExp(SR.T("正在重载，请稍候", "Reload in progress")); return; }
				//B1: 冷却判断放在最前：冷却期内不再白做一次全量序列化（原来在生成 XML 之后才判断）
				float _now = Time.unscaledTime;
				if (_now - _lastSnapshotAt < 3f) { NotifyExp(SR.T("快照广播冷却中，请稍候", "Snapshot broadcast on cooldown")); return; }
				_lastSnapshotAt = _now;
				//生成房主当前视角的完整快照 XML（包含所有方块及属性：位置/旋转/缩放/胶水 parentID/mainID 等）
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null)
				{
					NotifyExp(SR.T("生成快照失败", "Failed to generate snapshot"));
					return;
				}
				string xml = doc.OuterXml;
				if (string.IsNullOrEmpty(xml))
				{
					NotifyExp(SR.T("快照为空", "Snapshot is empty"));
					return;
				}
				//用游戏原生机制广播：房主压缩快照 → RPC 发给全员 → 全员 LoadSnapshotFromXmlDocument 重建方块。
				//不重载场景 → ScoreKeeper.Setup() 不会执行 → 分数保留。
				//大快照提示 + 序列化耗时（3 秒冷却已在方法开头判断）
				if (xml.Length > 1500000) NotifyExp(SR.T("快照较大（" + (xml.Length / 1000) + " KB），广播可能略卡", "Large snapshot (" + (xml.Length / 1000) + " KB); may hitch"));
				System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch(); _sw.Start();
				byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
				_sw.Stop();
				MainPlugin.ModLogger.LogInfo("[快照] 序列化耗时 " + _sw.ElapsedMilliseconds + "ms");
				NotifyExp(feature + SR.T("：正在广播快照（", ": broadcasting snapshot (") + xmlBytes.Length + SR.T(" 字节）…全员将按房主当前状态重建方块", " bytes)… everyone will rebuild blocks from the host's current state"));
				gc.CompressAndSendSnapshotBytes(xmlBytes, delegate {
					NotifyExp(feature + SR.T("完成：全员方块已重建，分数保留", " done: everyone rebuilt the blocks, scores kept"));
				});
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)(feature + "失败: " + ex.Message));
			}
		}

		private static LobbyPlayer FindLocalLobbyPlayer()
		{
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
			try
			{
				LobbyManager instance = LobbyManager.instance;
				if ((UnityEngine.Object)(object)instance != (UnityEngine.Object)null && (UnityEngine.Object)(object)instance.PlayerTracker != (UnityEngine.Object)null)
				{
					for (int i = 0; i < instance.PlayerTracker.NumPlayers; i++)
					{
						LobbyPlayer lobbyPlayer = instance.PlayerTracker.GetLobbyPlayer(instance.PlayerTracker.GetPlayerInfoByIndex(i).NetworkNumber);
						if ((UnityEngine.Object)(object)lobbyPlayer != (UnityEngine.Object)null && lobbyPlayer.IsLocalPlayer)
						{
							return lobbyPlayer;
						}
					}
				}
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.GetLobbyPlayer", __ex); }
			return null;
		}

		public static string CheatFlagText()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null)
				{
					return SR.T("存档系统不可用", "Save system unavailable");
				}
				SaveFileData saveFileDataForMainUser = instance.GetSaveFileDataForMainUser();
				if (saveFileDataForMainUser == null)
				{
					return SR.T("存档不可用", "Save unavailable");
				}
				bool flag = false;
				try
				{
					flag = saveFileDataForMainUser.IsCheater;
				}
				catch (Exception __ex) { SR.Guard.Log("Experiments.T", __ex); }
				if (flag)
				{
					return SR.T("⚠ 已被标识为作弊\n使用过作弊码，无法解锁全部成就", "⚠ Flagged as a cheater\nCheat codes were used, achievements stay locked");
				}
				return SR.T("正常（未使用作弊码）", "Clean (no cheat codes used)");
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("读取作弊标识失败: " + ex.Message));
				return SR.T("读取失败: ", "Read failed: ") + ex.Message;
			}
		}
	}
	
}
