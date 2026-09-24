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

		internal static ConfigEntry<bool> GcAfterLoadEntry;

		private static string _statTextCache = "";
		private static string _cheatFlagText = "";
		private static float _statsAutoTimer; //读取统计页每秒自动刷新
		private static bool _cacheLangEn;     //缓存生成时的语言（切换语言后强制刷新缓存）

		private static void SelfReg() {
			SR.LocSec("Experiments", "实验", null);
			SR.Nav("Experiments", 90); //侧栏栏目顺序 90
			SR.LocKey("Experiments", "GC After Load", "加载后清理", "GC after load");
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
			if (SR.Ctl.HotkeyButton("exp.refresh_stats", SR.T("刷新统计", "Refresh"), SR.T("读取主用户的存档统计（对局/时长/奔跑长度等）", "Read the main user's save stats"), () => { _statTextCache = Experiments.ReadStatsText(); _statsAutoTimer = 0f; }, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) {
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
			if (SR.Ctl.HotkeyButton("exp.refresh_flag", SR.T("刷新标识", "Refresh flag"), SR.T("读取当前存档是否被标记为作弊", "Read whether this save is flagged as a cheater"), () => _cheatFlagText = Experiments.CheatFlagText(), SR.Ctl.Sc(140), SR.Ctl.Sc(30))) {
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
			GcAfterLoadEntry = ((BaseUnityPlugin)plugin).Config.Bind<bool>("Experiments", "GC After Load", false, "进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。");
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
				return stringBuilder.ToString();
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
