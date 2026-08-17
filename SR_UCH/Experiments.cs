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
// ============================================================
// 实验栏目（Experiments）—— 按用户约定：全部功能保持单一源码文件，不拆分区文件。
// 文件内分区（按行号大致划分，新增功能请就近加入对应小节）：
//   1. Core          配置项 / 属性 / Initialize / NotifyExp / IsHostExp / FindLocalLobbyPlayer
//   2. 树屋问号       InjectQuestionUnlocks / ApplyQuestionMark / FillNextUnlock /
//                     RemoveQuestionMark / ClearQuestionMarks / AddAllQuestionMarks
//   3. 评分折扣       RefreshScoreboardHandicap / ApplyScoreDiscount / ApplyOwnHandicap / RestoreScoreDiscount
//   4. 位置同步       NetSync
//   5. 快速切换       LocalInBuild / SwitchToBuild / ForceSuicideState / QuickSwitchToPlay
//   6. 地图网格       GridAlwaysOnSkipDisable / GridToggleChanged
//   7. 自身增益       InFreeplayOnly / SelfInvincible* / SelfFly* / SelfCrouchMove*
//   8. 读取统计       ReadStatsText / Count / Float / FmtTime / FmtDist / CheatFlagText
//   9. 进度解锁       UnlockTimeSeconds / ProgressionDataReady / IsProgressionUnlocked(A/B) / ProgressionText
//   10. 重载关卡      ReloadLevel / ReloadLevelImpl / ReloadSceneRoutine / BroadcastSnapshot / RebuildBlocksFromHost
// 新功能文本请使用 Loc.T(zh, en) 提供中英文。
// ============================================================
public class Experiments : ITweak
	{
		private static MainPlugin _mp;

		private static ConfigEntry<bool> _netEnabled;

		private static ConfigEntry<int> _netHz;

		private static ConfigEntry<bool> _netAll;

		private static ConfigEntry<int> _scoreDiscount;

		private static ConfigEntry<bool> _moreDiscount;

		//重载关卡模式：KeepScore（保留方块和分数） / KeepBlocksOnly（仅保留方块，分数重置）
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

		private static ConfigEntry<ReloadMode> _reloadMode;

		private static ConfigEntry<bool> _gridAlwaysOn;

		private static ConfigEntry<bool> _treehouseMap;

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
			} catch { }
		}

		[HarmonyPatch(typeof(GameControl), "ProcessNextUnlocks")]
		[HarmonyPostfix]
		private static void ClearQuestionUnlocksAfterInject() {
			_questionUnlocks.Clear();
		}

		private static ConfigEntry<bool> _cheatFlag;

		private static ConfigEntry<bool> _selfInvincible;

		private static ConfigEntry<bool> _selfFly;

		private static ConfigEntry<bool> _selfCrouchMove;

		private static ConfigEntry<bool> _quickSwitchEnabled;

		private static ConfigEntry<KeyCode> _quickSwitchKey;

		private static ConfigEntry<float> _quickSwitchTime;

		private static bool _qsPlayToBuildArmed;

		private static float _qsPressTime; //快速切换键按下的时刻

		private static bool _qsBuildToPlayArmed;

		private static float _netTimer;

		private static string _statText = "";

		public static bool NetEnabled => _netEnabled != null && _netEnabled.Value;

		public static bool NetAll => _netAll != null && _netAll.Value;

		public static bool TreehouseMap => _treehouseMap != null && _treehouseMap.Value;

		public static bool QuestionMarkOn => _questionEnabled != null && _questionEnabled.Value;

		public static bool QuickSwitchOn => _quickSwitchEnabled != null && _quickSwitchEnabled.Value;

		public static int ScoreDiscount => (_scoreDiscount != null) ? _scoreDiscount.Value : 0;


		public static bool MoreDiscountOn => _moreDiscount != null && _moreDiscount.Value;

		//重载关卡是否保留分数（模式一 KeepScore = 保留；模式二 KeepBlocksOnly = 不保留）
		public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

		//局内修改分数折扣 → 立即刷新局内计分板（ScoreLine 的 handicap 显示）。
		//游戏只在开局 GraphScoreBoard.SetPlayerCharacter 时调用 ScoreLine.SetHandicap，
		//局内改 handicap（GamePlayer.CmdSetPlayerHandicap → SyncVar setter）不会刷新计分板。
		//patch set_NetworkHandicap：值变化后按 networkNumber 找到对应 ScoreLine 刷新。
		[HarmonyPatch(typeof(GamePlayer), "set_NetworkHandicap")]
		[HarmonyPostfix]
		private static void RefreshScoreboardHandicap(GamePlayer __instance) {
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
						FieldInfo f = AccessTools.Field(typeof(VersusControl), "graphScoreBoardInstance");
						if (f != null) board = f.GetValue(vc) as GraphScoreBoard;
					}
				} catch { }
				if (board == null) {
					board = UnityEngine.Object.FindObjectOfType<GraphScoreBoard>();
				}
				if (board == null) return;
				FieldInfo relField = AccessTools.Field(typeof(GraphScoreBoard), "scorelineRelation");
				if (relField == null) return;
				object rel = relField.GetValue(board);
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
					} catch { }
				}
				if (line == null) return;
				line.SetHandicap(__instance.Handicap);
			} catch { }
		}

		public static bool SelfInvincibleOn => _selfInvincible != null && _selfInvincible.Value;

		public static bool SelfFlyOn => _selfFly != null && _selfFly.Value;

		public static bool SelfCrouchMoveOn => _selfCrouchMove != null && _selfCrouchMove.Value;

		public static bool GridAlwaysOn => _gridAlwaysOn != null && _gridAlwaysOn.Value;

		public void Initialize(MainPlugin plugin)
		{
			_mp = plugin;
			_netEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Net Optimize", false, "位置同步：按设定频率主动上报自己的位置，让其他玩家看到你的移动更平滑、更跟手。\n原理：游戏默认只在关键事件时同步位置，开启后按固定频率（见下方同步频率）持续上报。\n仅对局内生效；本地派对/单机无网络时无效果。");
			_netHz = ((BaseUnityPlugin)plugin).Config.Bind<int>("实验", "Sync Frequency", 20, "同步频率（10 - 50 Hz，默认 20）：每秒钟上报多少次位置。\n越高 → 其他玩家看到的你越平滑跟手，但占用更多网络带宽、增加 CPU 开销；\n越低 → 更省流量，但对方看到的移动可能一顿一顿。联机延迟高时建议调低。");
			_netAll = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Sync All", false, "同步范围：开 = 把所有玩家的位置都按频率同步（需要房主权限，适合低延迟局域网/本地派对联机）；\n关 = 只同步你自己（默认，推荐，因为其他玩家的位置由他们各自的客户端上报）。");
			_scoreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<int>("实验", "Score Discount", 20, "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %（滑块 0-100，支持任意整数如 85 → handicap 15%；90 以上钳到 handicap 10 = 上限 90%；只影响自己）。\n默认 20（handicap 80%）；平衡板上自己那一行显示为对应百分比；可随时点“恢复原值”还原。");
			_moreDiscount = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "More Discount Values", false, "更多折扣数值：选中后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n⚠ 注意：游戏本身的得分平衡板 handicap 不支持任意百分比——它内部按四舍五入取整十倍数（如 85 → 90、84 → 80）。开启后自由输入的非整十数值只会修改显示，实际游戏结算仍按四舍五入后的整十倍数生效。");
			_reloadMode = ((BaseUnityPlugin)plugin).Config.Bind<ReloadMode>("实验", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
			_gridAlwaysOn = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Grid Always On", false, "地图网格：行动状态下也显示建造网格（游戏默认只在建造阶段显示）。\n随开随关：开启立即淡入，关闭立即淡出；任何模式都生效。开关在控制台“地图”栏目里。");
			_gridAlwaysOn.SettingChanged += GridToggleChanged;
			if (_gridAlwaysOn.Value)
			{
				GridToggleChanged(null, null); //上次会话开启过：立即补一次生效
			}
			_treehouseMap = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Treehouse Map", false, "树屋地图：在树屋大厅也能打开地图并传送自己");
			_questionEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Question Mark", false, "树屋问号总开关：给指定关卡的门添加问号（门内有解锁盒子；未解锁的关卡不能添加）。\n仅房主可操作；四个按钮（添加/删除/全部添加/清除全部）都受本开关控制。");
			_questionLevel = ((BaseUnityPlugin)plugin).Config.Bind<GameState.LevelName>("实验", "Question Level", (GameState.LevelName)0, "要添加问号的关卡（配合树屋问号使用）");
			_cheatFlag = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Cheat Flag", false, "作弊标识：显示当前存档是否被标记为作弊（使用过作弊码后无法解锁全部成就）");
			_selfInvincible = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Self Invincible", false, "自身无敌（仅自由模式有效）：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）");
			_selfFly = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Self Fly", false, "自身飞天（仅自由模式有效）：方向键自由飞行，按住 Shift 加速，不按键悬浮空中");
			_selfCrouchMove = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Self Crouch Move", false, "自身蹲移（仅自由模式有效）：蹲下时也能左右移动（A/D 或方向键）");
			_quickSwitchEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("实验", "Quick Switch", false, "快速切换：自由模式内按下切换键直接切换 行动↔建造 模式（无需按 B）。\n游戏默认：长按 B 键约 0.5 秒蓄力后切换。");
			_quickSwitchKey = ((BaseUnityPlugin)plugin).Config.Bind<KeyCode>("实验", "Quick Switch Key", (KeyCode)306, "快速切换键（默认 LeftCtrl；自由模式内按下它立即切换行动/建造模式）。\n游戏默认：长按 B 键约 0.5 秒蓄力后切换。");
			_quickSwitchTime = ((BaseUnityPlugin)plugin).Config.Bind<float>("实验", "Quick Switch Time", 0f, "切换最短按住（秒）：按下切换键后松开才切换（长按不触发）。\n0 = 松开立即切换（默认）；大于 0 时需按住至少该时长再松开才切换（防误触）。");
			Harmony.CreateAndPatchAll(typeof(Experiments), (string)null);
		}

		[HarmonyPatch(typeof(GameState), "Update")]
		[HarmonyPrefix]
		private static void NetSync()
		{
			//IL_009e: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Expected O, but got Unknown
			//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
			if (!ModManager.AllEnabled || !NetEnabled)
			{
				return;
			}
			int num = ((_netHz != null) ? _netHz.Value : 20);
			if (num < 10)
			{
				num = 10;
			}
			if (num > 30)
			{
				num = 30;
			}
			_netTimer += Time.deltaTime;
			if (_netTimer < 1f / (float)num)
			{
				return;
			}
			_netTimer = 0f;
			try
			{
				foreach (Player item in PlayerManager.GetInstance())
				{
					Player val = item;
					if (val == null)
					{
						continue;
					}
					Character playerCharacter = val.PlayerCharacter;
					if (!((UnityEngine.Object)(object)playerCharacter == (UnityEngine.Object)null) && (((NetworkBehaviour)playerCharacter).hasAuthority || NetAll))
					{
						try
						{
							playerCharacter.CallCmdPositionCharacter(((Component)playerCharacter).transform.position);
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
		}

		//本地玩家当前是否处于建造状态：对局中建造光标是 GamePlayer.CursorInstance
		//（PiecePlacementCursor，启用 = 建造中，行动中会被 Disable）。
		private static bool LocalInBuild()
		{
			try
			{
				foreach (Player p in PlayerManager.GetInstance())
				{
					if (p == null || p.AssociatedLobbyPlayer == null) continue;
					if (!p.AssociatedLobbyPlayer.IsLocalPlayer) continue;
					//对局：建造光标在 GamePlayer.CursorInstance
					if (p.AssociatedGamePlayer != null && p.AssociatedGamePlayer.CursorInstance != null)
					{
						return p.AssociatedGamePlayer.CursorInstance.Enabled;
					}
					if (p.AssociatedLobbyPlayer.CursorInstance != null)
					{
						return p.AssociatedLobbyPlayer.CursorInstance.Enabled;
					}
					return false;
				}
			}
			catch
			{
			}
			return false;
		}

		//行动 → 建造：直接调用游戏的切换（与 switchToPlay 对称：禁用角色 + 发事件 + 同步服务器），
		//不经过自杀机制，一次触发一次切换、不会重复。
		private static void SwitchToBuild(Character c)
		{
			try
			{
				c.Disable();
				GameEventManager.SendEvent(new FreePlayPlayerSwitchEvent(c.networkNumber, GameControl.GamePhase.PLACE));
				c.CallCmdSwitchFreeMode();
			}
			catch
			{
			}
		}

		[HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
		[HarmonyPrefix]
		private static void ForceSuicideState(Character __instance)
		{
			if (!ModManager.AllEnabled || _quickSwitchEnabled == null || !_quickSwitchEnabled.Value || (ModManager.UiOpen && ModManager.BlockInput) || !__instance.hasAuthority)
			{
				return;
			}
			try
			{
				if ((int)GameSettings.GetInstance().GameMode > 0)
				{
					return;
				}
			}
			catch
			{
				return;
			}
			//仅对局中生效（自由模式）；树屋/大厅不处理
			try
			{
				if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null)
				{
					_qsPlayToBuildArmed = false;
					return;
				}
			}
			catch
			{
				_qsPlayToBuildArmed = false;
				return;
			}
			//建造中：行动→建造的武装无意义，清掉
			if (LocalInBuild())
			{
				_qsPlayToBuildArmed = false;
				return;
			}
			KeyCode val = (KeyCode)((_quickSwitchKey == null) ? 306 : ((int)_quickSwitchKey.Value));
			//行动中：按下武装，松开才切换回建造；长按不触发（组合键：按住修饰键 + 主键）
			if (ModManager.ComboKeyDown(_quickSwitchKey))
			{
				_qsPlayToBuildArmed = true;
				_qsPressTime = Time.unscaledTime;
			}
			if (ModManager.ComboKeyUp(_quickSwitchKey))
			{
				bool armed = _qsPlayToBuildArmed;
				_qsPlayToBuildArmed = false;
				_qsBuildToPlayArmed = false;
				if (armed)
				{
					float t = (_quickSwitchTime != null) ? _quickSwitchTime.Value : 0f;
					if (t <= 0f || (Time.unscaledTime - _qsPressTime) >= t)
					{
						SwitchToBuild(__instance);
					}
				}
			}
		}

		[HarmonyPatch(typeof(PiecePlacementCursor), "Update")]
		[HarmonyPrefix]
		private static bool QuickSwitchToPlay(PiecePlacementCursor __instance)
		{
			if (!ModManager.AllEnabled)
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			if (_quickSwitchEnabled == null || !_quickSwitchEnabled.Value)
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			if (ModManager.UiOpen && ModManager.BlockInput)
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			try
			{
				if ((int)GameSettings.GetInstance().GameMode > 0)
				{
					_qsBuildToPlayArmed = false;
					return true;
				}
			}
			catch
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			//仅对局中生效（自由模式）；树屋/大厅不处理
			try
			{
				if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null)
				{
					_qsBuildToPlayArmed = false;
					return true;
				}
			}
			catch
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			//不在建造状态（光标被 Disable）：不处理，避免与行动→建造逻辑冲突
			if (!LocalInBuild())
			{
				_qsBuildToPlayArmed = false;
				return true;
			}
			KeyCode val = (KeyCode)((_quickSwitchKey == null) ? 306 : ((int)_quickSwitchKey.Value));
			//建造中：按下武装，松开才切换成行动；长按不触发（组合键：按住修饰键 + 主键）
			if (ModManager.ComboKeyDown(_quickSwitchKey))
			{
				_qsBuildToPlayArmed = true;
				_qsPressTime = Time.unscaledTime;
			}
			if (ModManager.ComboKeyUp(_quickSwitchKey))
			{
				bool armed = _qsBuildToPlayArmed;
				_qsBuildToPlayArmed = false;
				_qsPlayToBuildArmed = false;
				if (armed)
				{
					float t = (_quickSwitchTime != null) ? _quickSwitchTime.Value : 0f;
					if (t <= 0f || (Time.unscaledTime - _qsPressTime) >= t)
					{
						try
						{
							AccessTools.Method(typeof(PiecePlacementCursor), "switchToPlay", (Type[])null, (Type[])null).Invoke(__instance, null);
						}
						catch
						{
						}
					}
				}
			}
			return true; //长按期间不拦截原版输入
		}

		//建造网格常驻（Graphpaper = 建造阶段的网格背景，默认只在全局 PLACE 阶段显示）。
		//开启后自由模式行动阶段也保留网格：跳过 disableGrid（Update 每帧的 enableGrid 会兜底保持显示）
		[HarmonyPatch(typeof(Graphpaper), "disableGrid")]
		[HarmonyPrefix]
		private static bool GridAlwaysOnSkipDisable()
		{
			if (!ModManager.AllEnabled) return true;
			if (!GridAlwaysOn) return true;
			return false; //跳过禁用 → 网格常驻（任何模式）
		}

		//网格常驻随开随关：开 → 立即淡入网格；关 → 立即淡出（任何模式）
		private static void GridToggleChanged(object s, EventArgs e)
		{
			if (_gridAlwaysOn == null)
			{
				return;
			}
			bool on = _gridAlwaysOn.Value;
			foreach (Graphpaper gp in UnityEngine.Object.FindObjectsOfType<Graphpaper>())
			{
				if (gp == null) continue;
				if (on)
				{
					gp.enableGrid();
				}
				else
				{
					gp.disableGrid();
				}
			}
		}

		public static void ApplyScoreDiscount()
		{
			try
			{
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				int scoreDiscount = ScoreDiscount;
				if (scoreDiscount <= 0)
				{
					NotifyExp(ModManager.T("评分折扣未设置（0 = 关闭）", "Score discount not set (0 = off)"));
					return;
				}
				int target = Mathf.Clamp(100 - scoreDiscount, 10, 100);
				if (ApplyOwnHandicap(target))
				{
					NotifyExp(ModManager.T("评分折扣已应用: 自己 handicap ", "Score discount applied: own handicap ") + target + ModManager.T("%（平衡板可见，本局立即生效）", "% (visible on the scoreboard, takes effect this round)"));
				}
				else
				{
					NotifyExp(ModManager.T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player"));
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
						catch
						{
						}
					}
					return true;
				}
			}
			catch
			{
			}
			return false;
		}

		//恢复：handicap 回 100
		public static void RestoreScoreDiscount()
		{
			try
			{
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				if (ApplyOwnHandicap(100))
				{
					NotifyExp(ModManager.T("已恢复: 自己 handicap 100%", "Restored: own handicap 100%"));
				}
				else
				{
					NotifyExp(ModManager.T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player"));
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("恢复评分折扣失败: " + ex.Message));
			}
		}

		//自身增益（无敌/飞天/蹲移）只在自由模式有效
		private static bool InFreeplayOnly()
		{
			try
			{
				return GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY;
			}
			catch
			{
				return false;
			}
		}

		//自身无敌（仅自由模式）：拦截 KillCharacter（本地玩家免死）
		[HarmonyPatch(typeof(Character), "KillCharacter")]
		[HarmonyPrefix]
		private static bool SelfInvincibleBlockDeath(Character __instance, bool force)
		{
			if (force) return true; //强制死亡仍然生效（与游戏一致）
			if (!ModManager.AllEnabled) return true;
			if (!IsProgressionUnlockedB()) return true; //B 组未解锁：自动拦截
			if (_selfInvincible == null || !_selfInvincible.Value) return true;
			if (!InFreeplayOnly()) return true;
			try
			{
				if (__instance != null && __instance.hasAuthority) return false;
			}
			catch
			{
			}
			return true;
		}

		//自身无敌（仅自由模式）第二道闸：拦截 setupDeath（房主判定后广播的死亡）
		[HarmonyPatch(typeof(Character), "setupDeath")]
		[HarmonyPrefix]
		private static bool SelfInvincibleBlockSetupDeath(Character __instance)
		{
			if (!ModManager.AllEnabled) return true;
			if (!IsProgressionUnlockedB()) return true; //B 组未解锁：自动拦截
			if (_selfInvincible == null || !_selfInvincible.Value) return true;
			if (!InFreeplayOnly()) return true;
			try
			{
				if (__instance != null && __instance.hasAuthority) return false;
			}
			catch
			{
			}
			return true;
		}

		//自身飞天 + 自身蹲移：合并为一个 Character.FixedUpdate postfix（减少每角色每帧的 Harmony 调用）
		[HarmonyPatch(typeof(Character), "FixedUpdate")]
		[HarmonyPostfix]
		private static void SelfFlyCrouchTick(Character __instance)
		{
			SelfFlyTick(__instance);
			SelfCrouchMoveTick(__instance);
		}

		//自身飞天（仅自由模式）：方向键自由飞行，Shift 加速，不按键悬浮
		private static void SelfFlyTick(Character __instance)
		{
			if (!ModManager.AllEnabled) return;
			if (!IsProgressionUnlockedB()) return; //B 组未解锁：自动拦截
			if (_selfFly == null || !_selfFly.Value) return;
			if (!InFreeplayOnly()) return;
			if (__instance == null || !__instance.hasAuthority) return;
			try
			{
				if (__instance.Dead || __instance.Dying || __instance.Success) return;
				Rigidbody2D rb = __instance.GetComponent<Rigidbody2D>();
				if (rb == null) return;
				float mul = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2f : 1f;
				float vx = 0f, vy = 0f;
				if (Input.GetKey(KeyCode.UpArrow)) vy = 9f * mul;
				else if (Input.GetKey(KeyCode.DownArrow)) vy = -9f * mul;
				if (Input.GetKey(KeyCode.LeftArrow)) vx = -7f * mul;
				else if (Input.GetKey(KeyCode.RightArrow)) vx = 7f * mul;
				rb.velocity = new Vector2(vx, vy);
			}
			catch
			{
			}
		}

		//自身蹲移（仅自由模式）：蹲下时也能左右移动
		private static void SelfCrouchMoveTick(Character __instance)
		{
			if (!ModManager.AllEnabled) return;
			if (!IsProgressionUnlockedB()) return; //B 组未解锁：自动拦截
			if (_selfCrouchMove == null || !_selfCrouchMove.Value) return;
			if (!InFreeplayOnly()) return;
			if (__instance == null || !__instance.hasAuthority) return;
			try
			{
				FieldInfo f = AccessTools.Field(typeof(Character), "crouchingDown");
				if (f == null) return;
				bool crouching = (bool)f.GetValue(__instance);
				if (!crouching) return;
				Rigidbody2D rb = __instance.GetComponent<Rigidbody2D>();
				if (rb == null) return;
				float h = 0f;
				if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = -1f;
				else if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = 1f;
				float speed = __instance.RunSpeed * 0.9f;
				rb.velocity = new Vector2(h * speed, rb.velocity.y);
			}
			catch
			{
			}
		}

		private static void NotifyExp(string text)
		{
			try
			{
				UserMessageManager.Instance.UserMessage(text, false);
			}
			catch
			{
			}
			MainPlugin.ModLogger.LogInfo((object)("[实验] " + text));
		}

		public static string ReadStatsText()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null)
				{
					return ModManager.T("存档系统不可用", "Save system unavailable");
				}
				SaveFileData saveFileDataForMainUser = instance.GetSaveFileDataForMainUser();
				if (saveFileDataForMainUser == null)
				{
					return ModManager.T("存档不可用", "Save unavailable");
				}
				StringBuilder stringBuilder = new StringBuilder();
				stringBuilder.Append(ModManager.T("对局次数: ", "Games played: ") + Count(saveFileDataForMainUser, "GamesPlayed") + "\n");
				stringBuilder.Append(ModManager.T("在线对局: ", "Online games: ") + Count(saveFileDataForMainUser, "OnlineGamesPlayed") + "\n");
				stringBuilder.Append(ModManager.T("派对对局: ", "Party games: ") + Count(saveFileDataForMainUser, "PartyModeGamesPlayed") + "\n");
				stringBuilder.Append(ModManager.T("创造性对局: ", "Creative games: ") + Count(saveFileDataForMainUser, "CreativeModeGamesPlayed") + "\n");
				stringBuilder.Append(ModManager.T("沙盒对局: ", "Sandbox games: ") + Count(saveFileDataForMainUser, "SandboxModeGamesPlayed") + "\n");
				stringBuilder.Append(ModManager.T("游戏时长: ", "Play time: ") + FmtTime(Float(saveFileDataForMainUser, "TotalMatchTime")) + "\n");
				stringBuilder.Append(ModManager.T("奔跑长度: ", "Distance run: ") + FmtDist(Float(saveFileDataForMainUser, "DistanceRun")) + "\n");
				stringBuilder.Append(ModManager.T("总死亡: ", "Total deaths: ") + Count(saveFileDataForMainUser, "TotalDeaths") + "\n");
				stringBuilder.Append(ModManager.T("金币: ", "Coins: ") + Count(saveFileDataForMainUser, "CoinsCollected"));
				_statText = stringBuilder.ToString();
				return _statText;
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("读取统计失败: " + ex.Message));
				return ModManager.T("读取失败: ", "Read failed: ") + ex.Message;
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
			return num / 3600 + ModManager.T("时 ", "h ") + num % 3600 / 60 + ModManager.T("分 ", "m ") + num % 60 + ModManager.T("秒", "s");
		}

		private static string FmtDist(float units)
		{
			return Mathf.RoundToInt(units) + " m";
		}

		//进度解锁（A 组）：游戏时长 > 17时16分18秒 或 奔跑长度 > 52000 米时，解除
		//建造增强（无视碰撞）的禁用限制。
		public static readonly float UnlockTimeSeconds = 17f * 3600f + 16f * 60f + 18f; //17时16分18秒 = 62178 秒
		public static readonly float UnlockDistanceMeters = 52000f;

		//进度解锁（B 组）：游戏时长 > 52时 或 奔跑长度 > 100000 米时，解除
		//方块破坏、自身增益（无敌/飞天/蹲移）的禁用限制。
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
				if (instance == null) return ModManager.T("存档系统不可用", "Save system unavailable");
				SaveFileData data = instance.GetSaveFileDataForMainUser();
				if (data == null) return ModManager.T("存档不可用", "Save unavailable");
				float time = Float(data, "TotalMatchTime");
				float dist = Float(data, "DistanceRun");
				bool unlockedA = time > UnlockTimeSeconds || dist > UnlockDistanceMeters;
				bool unlockedB = time > UnlockBTimeSeconds || dist > UnlockBDistanceMeters;
				string s = ModManager.T("游戏时长: ", "Play time: ") + FmtTime(time) + " / " + FmtTime(UnlockBTimeSeconds)
					+ "\n" + ModManager.T("奔跑长度: ", "Distance run: ") + FmtDist(dist) + " / " + FmtDist(UnlockBDistanceMeters)
					+ "\n" + ModManager.T("A组（无视碰撞）: ", "Group A (ignore collision): ") + (unlockedA ? ModManager.T("✅ 已解锁", "✅ Unlocked") : ModManager.T("🔒 需时长 > 17时16分18秒 或 长度 > 52000米", "🔒 need >17h16m18s or >52000m"))
					+ "\n" + ModManager.T("B组（方块破坏/自身增益）: ", "Group B (destroy blocks / self buffs): ") + (unlockedB ? ModManager.T("✅ 已解锁", "✅ Unlocked") : ModManager.T("🔒 需时长 > 52时 或 长度 > 100000米", "🔒 need >52h or >100000m"));
				return s;
			}
			catch (Exception ex)
			{
				return ModManager.T("读取进度失败: ", "Read failed: ") + ex.Message;
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
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(ModManager.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(ModManager.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
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
					catch
					{
					}
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					val2 = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || val2.portals == null)
				{
					NotifyExp(ModManager.T("不在树屋大厅", "Not in the treehouse lobby"));
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
					NotifyExp(ModManager.T("树屋没有该关卡的门", "The treehouse has no portal for this level"));
					return;
				}
				bool flag = false;
				try
				{
					flag = val3.Locked;
				}
				catch
				{
				}
				if (flag)
				{
					NotifyExp(ModManager.T("该关卡尚未解锁，不能添加问号", "This level is not unlocked yet; cannot add a question mark"));
					return;
				}
				LobbyPlayer val5 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val5 == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到本地玩家", "Cannot find the local player"));
					return;
				}
				if (!FillNextUnlock(val2, val5))
				{
					NotifyExp(ModManager.T("所有物品已解锁，没有可获取的新物品，不添加问号", "Everything is already unlocked; no new items available, no question mark added"));
					return;
				}
				bool flag2 = false;
				try
				{
					flag2 = NetworkServer.active && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && LobbyManager.instance.IsHost;
				}
				catch
				{
				}
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
					NotifyExp(string.Format(ModManager.T("已给 {0} 添加问号", "Added question mark to {0}"), val));
				}
				else
				{
					MethodInfo methodInfo2 = AccessTools.Method(typeof(LevelSelectController), "SendUnlockMessageFromClient", (Type[])null, (Type[])null);
					if (methodInfo2 != null)
					{
						methodInfo2.Invoke(val2, new object[2] { val5, val });
					}
					NotifyExp(string.Format(ModManager.T("已请求房主给 {0} 添加问号", "Asked the host to add a question mark to {0}"), val));
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("添加问号失败: " + ex.Message));
			}
		}

		private static bool FillNextUnlock(LevelSelectController lsc, LobbyPlayer me)
		{
			//IL_0254: Unknown result type (might be due to invalid IL or missing references)
			//IL_025b: Expected I4, but got Unknown
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
				catch
				{
				}
				if (val == null)
				{
					return false;
				}
				IDictionary<LobbyPlayer, UnLockInfo> nextUnlocks = GameState.GetInstance().nextUnlocks;
				if (nextUnlocks == null)
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
									nextUnlocks[me] = array[i];
									_questionUnlocks[me] = array[i]; //自管记录：防被游戏清空
									return true;
								}
							}
						}
					}
				}
				catch
				{
				}
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
									nextUnlocks[me] = array2[j];
									_questionUnlocks[me] = array2[j]; //自管记录：防被游戏清空
									return true;
								}
							}
						}
					}
				}
				catch
				{
				}
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
									catch
									{
									}
									if ((values3[num] & num2) == 0)
									{
										nextUnlocks[me] = array3[k];
										_questionUnlocks[me] = array3[k]; //自管记录：防被游戏清空
										return true;
									}
								}
							}
						}
					}
				}
				catch
				{
				}
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
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(ModManager.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(ModManager.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
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
					catch
					{
					}
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					val2 = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || val2.portals == null)
				{
					NotifyExp(ModManager.T("不在树屋大厅", "Not in the treehouse lobby"));
					return;
				}
				if (!isHostExp)
				{
					//联机房客：游戏原生没有“删除问号”的网络消息（NetMsgTypes 只有 PortalHasUnlock 是添加），
					//SyncVar levelHasUnlock 只有房主能写；房主未装模组无法代执行 → 明确提示而非假生效。
					NotifyExp(ModManager.T("联机无法删除问号：游戏没有删除问号的网络消息，房主未装模组无法代执行", "Cannot remove question marks online: the game has no network message for removal, and the host cannot do it without the mod"));
					return;
				}
				LobbyPlayer val3 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val3 == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到本地玩家", "Cannot find the local player"));
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
				NotifyExp(flag ? string.Format(ModManager.T("已删除 {0} 的问号", "Removed the question mark from {0}"), val) : ModManager.T("该关卡没有问号", "This level has no question mark"));
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
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(ModManager.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(ModManager.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				LevelSelectController val = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch
					{
					}
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
				{
					val = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null || val.portals == null)
				{
					NotifyExp(ModManager.T("不在树屋大厅", "Not in the treehouse lobby"));
					return;
				}
				if (!isHostExp)
				{
					//联机房客：游戏原生没有“删除问号”的网络消息，SyncVar 只有房主能写 → 明确提示
					NotifyExp(ModManager.T("联机无法清除问号：游戏没有删除问号的网络消息，房主未装模组无法代执行", "Cannot clear question marks online: the game has no network message for removal, and the host cannot do it without the mod"));
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
				NotifyExp(ModManager.T("已一键清除所有问号", "Cleared all question marks"));
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
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				if (_questionEnabled == null || !_questionEnabled.Value)
				{
					NotifyExp(ModManager.T("请先勾选“树屋问号”开关", "Enable the \"Treehouse Question Marks\" option first"));
					return;
				}
				bool isHostExp = IsHostExp();
				if (!isHostExp && !ExRef.IgnoreHostLimit)
				{
					NotifyExp(ModManager.T("仅房主可用（可在附加功能开启“无视房主房客限制”）", "Host only (enable \"Ignore Host Limits\" in Add-ons to bypass)"));
					return;
				}
				LevelSelectController val = null;
				if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
				{
					try
					{
						val = LobbyManager.instance.CurrentLevelSelectController;
					}
					catch
					{
					}
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
				{
					val = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
				}
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null || val.portals == null)
				{
					NotifyExp(ModManager.T("不在树屋大厅", "Not in the treehouse lobby"));
					return;
				}
				LobbyPlayer val2 = FindLocalLobbyPlayer();
				if ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到本地玩家", "Cannot find the local player"));
					return;
				}
				bool flag = false;
				try
				{
					flag = NetworkServer.active && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && LobbyManager.instance.IsHost;
				}
				catch
				{
				}
				if (!FillNextUnlock(val, val2))
				{
					NotifyExp(ModManager.T("所有物品已解锁，没有可获取的新物品，不添加问号", "Everything is already unlocked; no new items available, no question mark added"));
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
					catch
					{
					}
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
				NotifyExp(flag ? string.Format(ModManager.T("已为全部 {0} 个已解锁关卡添加问号（跳过 {1} 个未解锁）", "Added question marks to all {0} unlocked levels (skipped {1} locked)"), num, num2) : string.Format(ModManager.T("已请求房主为全部 {0} 个已解锁关卡添加问号（跳过 {1} 个未解锁）", "Asked the host to add question marks to all {0} unlocked levels (skipped {1} locked)"), num, num2));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("全部添加问号失败: " + ex.Message));
			}
		}

		private static bool IsHostExp()
		{
			try
			{
				return NetworkServer.active && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && LobbyManager.instance.IsHost;
			}
			catch
			{
				return false;
			}
		}

		//（旧 BroadcastSnapshot 方法体已并入 RebuildBlocksFromHost 共享核心）

		//重载关卡：**真正重载当前关卡场景**（ReloadScene → 全员重新加载场景）。
		// - 方块：重载前把房主当前快照写入 QuickSaver.levelPortalXml（static）→ 重载后
		//   房主 OnSetupStartLevel 读到它 → 房主加载当前方块 + 原生 CompressAndSendSnapshotBytes
		//   广播给所有客户端（游戏原生 ClientRpc，无 mod 房客也重建，含玩家放置的/可移动的）
		// - 分数：重载走 PrepareToReloadScene → ModManager 保分 patch（房主端按 networkNumber 恢复；
		//   未装 mod 的房客端原生重载会清分——游戏架构限制）
		//⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效；仅房主有效（ReloadScene 是服务器操作）。
		public static void ReloadLevel()
		{
			ReloadLevelImpl(ModManager.T("重载关卡", "Reload Level"));
		}

		//共享核心（与重载关卡一致的模式限制 + 快照写入 levelPortalXml）
		private static void ReloadLevelImpl(string feature)
		{
			try
			{
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				//仅派对/创意局内生效
				if (!IsPartyOrCreative())
				{
					NotifyExp(feature + ModManager.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!IsHostExp())
				{
					NotifyExp(ModManager.T("仅房主有效：", "Host only: ") + feature + ModManager.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				GameState gs = GameState.GetInstance();
				if (gs == null)
				{
					NotifyExp(ModManager.T("游戏状态不可用", "Game state unavailable"));
					return;
				}
				//1) 生成房主当前快照 XML → 写入 QuickSaver.levelPortalXml（static）：
				//   重载后房主 OnSetupStartLevel 读它 → 加载当前方块 + 原生广播全员
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
				{
					NotifyExp(ModManager.T("生成快照失败（当前场景无方块数据）", "Failed to generate snapshot (no block data in the current scene)"));
					return;
				}
				try
				{
					System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
					if (lpx != null) lpx.SetValue(null, doc.OuterXml);
				}
				catch
				{
					NotifyExp(ModManager.T("写入快照失败", "Failed to write snapshot"));
					return;
				}
				//2) 发送 PrepareToReloadScene。
				//   - 模式一（保留方块和分数 = 允许补分）：置保分标志 → Setup 时备份分块列表 →
				//     重载完成后按原类型广播补分 → 全员下一回合结算显示真分数
				//   - 模式二（仅保留方块 = 跳过补分）：不置标志 → 重载后分数重置（重新对局）
				if (ReloadKeepsScore) ModManager.MarkPreserveScores();
				try
				{
					MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene
					{
						reloadToMode = GameSettings.GetInstance().GameMode,
						snapshotInfo = gs.currentSnapshotInfo
					};
					NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
				}
				catch
				{
				}
				if (LoadingInterstitialSplash.Instance != null)
				{
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				//3) 真正重载场景（全员重新加载；房主 OnSetupStartLevel 自动加载+广播当前方块）
				lm.StartCoroutine(ReloadSceneRoutine());
				NotifyExp(feature + ModManager.T("：全员重载关卡，方块保留，", ": reloading the level for everyone; blocks kept, ") +
					(ReloadKeepsScore ? ModManager.T("分数补回", "scores restored") : ModManager.T("分数重置", "scores reset")));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)(feature + "失败: " + ex.Message));
			}
		}

		//仅派对(PARTY)/创意(CREATIVE)局内生效
		private static bool IsPartyOrCreative()
		{
			try
			{
				GameState.GameMode m = GameSettings.GetInstance().GameMode;
				return m == GameState.GameMode.PARTY || m == GameState.GameMode.CREATIVE;
			}
			catch
			{
				return false;
			}
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
			//重载失败时：清掉写入的 levelPortalXml（防止污染下一次进关/回合的关卡布局）
			if (!ok)
			{
				try
				{
					System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
					if (lpx != null) lpx.SetValue(null, null);
				}
				catch
				{
				}
			}
		}

		//广播方块快照：房主重发当前关卡快照 → 全员按房主视角重建方块（修复方块消失/不同步）。
		public static void BroadcastSnapshot()
		{
			RebuildBlocksFromHost(ModManager.T("广播方块快照", "Broadcast Snapshot"));
		}

		//共享核心：生成房主当前快照 XML → CompressAndSendSnapshotBytes 广播 → 全员重建。
		//不重载场景 → 分数保留；方块按房主当前视角全部重建（含属性/胶水/玩家放置的）。
		//⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效。
		private static void RebuildBlocksFromHost(string feature)
		{
			try
			{
				if (!ModManager.AllEnabled)
				{
					NotifyExp(ModManager.T("本 Mod 总开关已关闭", "This mod's master switch is off"));
					return;
				}
				//仅派对/创意局内生效
				if (!IsPartyOrCreative())
				{
					NotifyExp(feature + ModManager.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!IsHostExp())
				{
					NotifyExp(ModManager.T("仅房主有效：", "Host only: ") + feature + ModManager.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					NotifyExp(ModManager.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				//生成房主当前视角的完整快照 XML（包含所有方块及属性：位置/旋转/缩放/胶水 parentID/mainID 等）
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null)
				{
					NotifyExp(ModManager.T("生成快照失败", "Failed to generate snapshot"));
					return;
				}
				string xml = doc.OuterXml;
				if (string.IsNullOrEmpty(xml))
				{
					NotifyExp(ModManager.T("快照为空", "Snapshot is empty"));
					return;
				}
				//用游戏原生机制广播：房主压缩快照 → RPC 发给全员 → 全员 LoadSnapshotFromXmlDocument 重建方块。
				//不重载场景 → ScoreKeeper.Setup() 不会执行 → 分数保留。
				byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
				NotifyExp(feature + ModManager.T("：正在广播快照（", ": broadcasting snapshot (") + xmlBytes.Length + ModManager.T(" 字节）…全员将按房主当前状态重建方块", " bytes)… everyone will rebuild blocks from the host's current state"));
				gc.CompressAndSendSnapshotBytes(xmlBytes, delegate {
					NotifyExp(feature + ModManager.T("完成：全员方块已重建，分数保留", " done: everyone rebuilt the blocks, scores kept"));
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
			catch
			{
			}
			return null;
		}

		public static string CheatFlagText()
		{
			try
			{
				StatTracker instance = StatTracker.Instance;
				if (instance == null)
				{
					return ModManager.T("存档系统不可用", "Save system unavailable");
				}
				SaveFileData saveFileDataForMainUser = instance.GetSaveFileDataForMainUser();
				if (saveFileDataForMainUser == null)
				{
					return ModManager.T("存档不可用", "Save unavailable");
				}
				bool flag = false;
				try
				{
					flag = saveFileDataForMainUser.IsCheater;
				}
				catch
				{
				}
				if (flag)
				{
					return ModManager.T("⚠ 已被标识为作弊\n使用过作弊码，无法解锁全部成就", "⚠ Flagged as a cheater\nCheat codes were used, achievements stay locked");
				}
				return ModManager.T("正常（未使用作弊码）", "Clean (no cheat codes used)");
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("读取作弊标识失败: " + ex.Message));
				return ModManager.T("读取失败: ", "Read failed: ") + ex.Message;
			}
		}
	}
	
}
