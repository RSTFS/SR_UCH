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
		//高频消息集中缓存：运行时生成一次，避免同一文本重复进 #US 字符串堆、减小 DLL 体积。
		internal static class Msgs {
			private static string _masterOff;
			private static string _notTreehouse;
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
			internal static string NoLocal() {
				if (_noLocal == null) _noLocal = SR.T("找不到本地玩家", "Cannot find the local player");
				return _noLocal;
			}
			internal static string NotLobby() {
				if (_notLobby == null) _notLobby = SR.T("不在大厅或找不到本地玩家", "Not in a lobby or cannot find the local player");
				return _notLobby;
			}
		}

		private static ConfigEntry<GameState.LevelName> _questionLevel;

		private static ConfigEntry<bool> _questionEnabled;
		internal static ConfigEntry<bool> GcAfterLoadEntry;

		private static string _statTextCache = "";
		private static string _cheatFlagText = "";
		private static float _statsAutoTimer; //读取统计页每秒自动刷新
		private static bool _cacheLangEn;     //缓存生成时的语言（切换语言后强制刷新缓存）

		//树屋问号自管解锁记录：游戏在 checkForAvailableUnlocks / RpcSetNextLevel 会清空
		//nextUnlocks、丢掉 mod 手填条目（关卡就没有解锁盒子），故在 ProcessNextUnlocks 前补回。
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

		private static string _statText = "";

		public static bool QuestionMarkOn => _questionEnabled != null && _questionEnabled.Value;

		private static void SelfReg() {
			SR.RowEnumFilters.Add(LevelEnumFilter); //通用条目行扩展点：问号关卡下拉过滤 空白/随机/原型
			SR.LocSec("Experiments", "实验", null);
			SR.Nav("Experiments", 90); //侧栏栏目顺序 90
			SR.LocKey("Experiments", "GC After Load", "加载后清理", "GC after load");
			SR.LocKey("Experiments", "Question Mark", "树屋问号", null);
			SR.LocDesc("Experiments", "Question Mark", "树屋问号总开关：给指定关卡的门添加问号（未解锁的关卡不能添加）。\n仅房主可操作；四个按钮（添加/删除/全部添加/清除全部）都受本开关控制。", "Treehouse question mark master switch: add a question mark to a level's portal (locked levels can't).\nHost-only; all four buttons (add/remove/add all/clear all) are gated by this switch.");
			SR.LocKey("Experiments", "Question Level", "问号关卡", null);
			SR.LocDesc("Experiments", "Question Level", "要添加问号的关卡（配合 添加问号 / 删除问号 使用）", "Which level to mark with a question mark (used with Add / Remove)");
		}

		//问号关卡下拉：过滤 空白/随机/原型 等不可选关卡
		private static bool LevelEnumFilter(ConfigEntryBase e, string enumName, int value) {
			if (e.Definition.Section != "Experiments" || e.Definition.Key != "Question Level") return true;
			return enumName != "BLANKLEVEL" && value < (int)GameState.LevelName.RANDOM;
		}

		public static void Render() {
			GUIStyle warn = new GUIStyle(SR.Ctl.LabelWrap);
			warn.normal.textColor = new Color(1f, 0.85f, 0.3f, 1f);
			GUILayout.Label(SR.T("⚠ 实验区的功能处于测试阶段，可能会导致游戏稳定性下降以及更多的 bug。",
				"⚠ Experimental features are in testing; they may reduce stability and cause more bugs."), warn);
			GUILayout.Space(SR.Ctl.Sc(4));
			//加载后清理：仅进/换关卡时执行一次（同关卡回合切换跳过）。
			GUILayout.Label(SR.T("— 加载后清理 —", "— Cleanup after load —"), SR.Ctl.SecHeader);
			GUILayout.BeginHorizontal();
			ConfigEntryBase gc = SR.Ctl.FindEntry("Experiments", "GC After Load");
			SR.Ctl.RestoreLabel(new GUIContent(SR.T("加载后清理", "GC after load"),
				SR.T("进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。\n同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。",
				"Runs GC + asset unload once on level load (skipped on same-level round reloads), reducing in-match stutter.")),
				gc, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
			if (gc != null) SR.Ctl.RenderControl(gc);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(6));

			GUILayout.Label(SR.T("— 树屋问号 —", "— Treehouse question mark —"), SR.Ctl.SecHeader);
			GUILayout.BeginHorizontal();
			ConfigEntryBase qm = SR.Ctl.FindEntry("Experiments", "Question Mark");
			SR.Ctl.RestoreLabel(new GUIContent(SR.T("树屋问号", "Question mark"), SR.T("给指定关卡的门添加问号（未解锁的关卡不能添加）。\n仅房主可操作；四个按钮都受“树屋问号”总开关控制。", "Add a question mark to a level's portal (locked levels can't).\nHost-only; all four buttons are gated by the master switch.")), qm, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
			if (qm != null) SR.Ctl.RenderControl(qm);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			GUILayout.BeginHorizontal();
			ConfigEntryBase ql = SR.Ctl.FindEntry("Experiments", "Question Level");
			SR.Ctl.RestoreLabel(new GUIContent(SR.T("问号关卡", "Level"), SR.T("要添加问号的关卡", "Which level to mark")), ql, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
			if (ql != null) SR.Ctl.RenderControl(ql);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(new GUIContent(SR.T("添加问号", "Add ?"), SR.T("给上方选中的关卡添加问号（进入该地图有解锁盒子）", "Mark the selected level with a question mark")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				Experiments.ApplyQuestionMark();
			}
			if (GUILayout.Button(new GUIContent(SR.T("删除问号", "Remove ?"), SR.T("删除上方选中的关卡的门上的问号", "Remove the question mark on the selected level")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				Experiments.RemoveQuestionMark();
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(new GUIContent(SR.T("全部添加问号", "Add all ?"), SR.T("为当前所有已解锁的关卡添加问号（未解锁的自动跳过）", "Add a question mark to every unlocked level")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				Experiments.AddAllQuestionMarks();
			}
			if (GUILayout.Button(new GUIContent(SR.T("一键清除全部问号", "Clear all ?"), SR.T("清空树屋里所有关卡门上的问号", "Clear every question mark in the treehouse")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				Experiments.ClearQuestionMarks();
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(4));

			GUILayout.Label(SR.T("— 角色声音 —", "— Character sound —"), SR.Ctl.SecHeader);
			GUILayout.BeginHorizontal();
			ConfigEntryBase mown = SR.Ctl.FindEntry("Experiments", "Mute Own");
			SR.Ctl.RestoreLabel(new GUIContent(SR.T("关闭自己的声音", "Mute own"),
				SR.T("静音自己角色的角色音效（走路/跳跃/落地/掉落等，由角色发出的声音）。\n只影响自己，其他人不受影响。", "Mutes your own character's sounds (walk/jump/land/fall etc. emitted by the character).\nOnly affects you; others are unaffected.")),
				mown, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
			if (mown != null) SR.Ctl.RenderControl(mown);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			GUILayout.BeginHorizontal();
			ConfigEntryBase moth = SR.Ctl.FindEntry("Experiments", "Mute Others");
			SR.Ctl.RestoreLabel(new GUIContent(SR.T("关闭其它玩家的声音", "Mute others"),
				SR.T("静音其它玩家角色的角色音效（自己听不到，不影响对方）。\n对方自己的客户端不受影响。", "Mutes other players' character sounds (you won't hear them; their clients are unaffected).")),
				moth, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
			if (moth != null) SR.Ctl.RenderControl(moth);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(4));

			GUILayout.Label(SR.T("— 读取统计 —", "— Read stats —"), SR.Ctl.SecHeader);
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(new GUIContent(SR.T("刷新统计", "Refresh"), SR.T("读取主用户的存档统计（对局/时长/奔跑长度等）", "Read the main user's save stats")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				_statTextCache = Experiments.ReadStatsText();
				_statsAutoTimer = 0f;
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			//页面打开时每秒自动刷新，便于看局内实时累计（奔跑长度/金币/死亡等）。
			_statsAutoTimer += Time.unscaledDeltaTime;
			//语言切换后强制刷新缓存。
			if (_cacheLangEn != SR.Ctl.LangEn) {
				_cacheLangEn = SR.Ctl.LangEn;
				_statTextCache = "";
				_cheatFlagText = "";
			}
			if (_statsAutoTimer >= 1f) {
				_statsAutoTimer = 0f;
				_statTextCache = Experiments.ReadStatsText();
			}
			if (_statTextCache == null || _statTextCache.Length == 0) {
				_statTextCache = Experiments.ReadStatsText();
			}
			SR.Ctl.WrapLabel(_statTextCache);
			GUILayout.Label(SR.T("（对局结束后结算；奔跑长度/金币/死亡等在对局中实时累计）",
				"(settled after a match ends; distance/coins/deaths accumulate live in-match)"), SR.Ctl.Label);
			GUILayout.Space(SR.Ctl.Sc(4));
			GUILayout.Space(SR.Ctl.Sc(6));

			GUILayout.Label(SR.T("— 作弊标识 —", "— Cheat flag —"), SR.Ctl.SecHeader);
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(new GUIContent(SR.T("刷新标识", "Refresh flag"), SR.T("读取当前存档是否被标记为作弊", "Read whether this save is flagged as a cheater")), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(30)))) {
				_cheatFlagText = Experiments.CheatFlagText();
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SR.Ctl.Sc(2));
			if (_cheatFlagText == null || _cheatFlagText.Length == 0) {
				_cheatFlagText = Experiments.CheatFlagText();
			}
			SR.Ctl.WrapLabel(_cheatFlagText);
			GUILayout.Space(SR.Ctl.Sc(4));
		}

		public void Initialize(MainPlugin plugin)
		{
			SelfReg();
			_questionEnabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Experiments", "Question Mark", false, "树屋问号总开关：给指定关卡的门添加问号（门内有解锁盒子；未解锁的关卡不能添加）。\n仅房主可操作；四个按钮（添加/删除/全部添加/清除全部）都受本开关控制。");
			_questionLevel = ((BaseUnityPlugin)plugin).Config.Bind<GameState.LevelName>("Experiments", "Question Level", (GameState.LevelName)0, "要添加问号的关卡（配合树屋问号使用）");
			GcAfterLoadEntry = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Experiments", "GC After Load", false, "进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。");
			Harmony.CreateAndPatchAll(typeof(Experiments), (string)null);
		}

		internal static void NotifyExp(string text)
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

		public static void ApplyQuestionMark()
		{
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
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
					//关键：同步 UnlockInLevel —— RpcSetNextLevel 仅在 UnlockInLevel==nextLevel
					//时保留 nextUnlocks，否则关卡里不生成解锁盒子。
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
			_questionUnlocks[me] = item; //防被游戏清空
			return true;
		}

		//纯挑选：用本端主用户存档挑一个该关卡尚未解锁的物品；不写 nextUnlocks，
		//供各客户端按关卡自行预填（互不干扰）。
		private static bool TryPickUnlock(LevelSelectController lsc, out UnLockInfo result)
		{
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
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
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
					//房客删问号：游戏无删除网络消息（NetMsgTypes 只有添加），SyncVar 仅房主可写 → 只能明确提示。
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
				//同步删除自管记录，防止进关卡后仍注入解锁盒子。
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
					//房客清除问号：同样没有删除网络消息，SyncVar 仅房主可写 → 只能明确提示。
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
				_questionUnlocks.Clear(); //同步清除自管记录
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
			try
			{
				if (!SR.GateMaster)
				{
					NotifyExp(Msgs.MasterOff());
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
					//跳过自定义/空白/随机等门（与问号关卡下拉框过滤一致）。
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
						val.UnlockInLevel = val3.TargetLevel; //同 Apply：同步 UnlockInLevel
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

		internal static bool IsHostExp()
		{
			//统一房主判定，见 SR.Gate.Service.cs；离线/无连接也算房主。
			return SR.IsHost;
		}

		private static LobbyPlayer FindLocalLobbyPlayer()
		{
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
