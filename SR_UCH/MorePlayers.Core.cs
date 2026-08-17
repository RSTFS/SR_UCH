using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using InControl;
using Moserware.Skills;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class MorePlayers : ITweak {

// ==== 分区：Core（配置 / 激活门控 / Initialize / 反射助手 / 重打补丁）====

		private static MainPlugin _mp;

		private static ConfigEntry<bool> _enabled;

		private static ConfigEntry<int> _playerLimit;

		private static ConfigEntry<bool> _fullDebug;

		private static ConfigEntry<bool> _shuffleScoreBalancer;

		private static Harmony _harmony;

		private static string _ogVersion = "1.0.0.0";

		public static bool Enabled => _enabled != null && _enabled.Value;

		public static int PlayerLimit => (_playerLimit != null) ? _playerLimit.Value : 17;

		public static bool FullDebug => _fullDebug != null && _fullDebug.Value;

		public static bool ShuffleScoreBalancer => _shuffleScoreBalancer != null && _shuffleScoreBalancer.Value;

		//供「模组联机」（ModMC）调用：把多人模式复位回原版（版本号/4 人/卸载补丁），
		//避免两个联机入口的状态互相污染（点“模组联机”前先复位“更多联机”）
		public static void ResetToVanilla()
		{
			try
			{
				_modActive = false;
				GameSettings gs = null;
				try
				{
					gs = GameSettings.GetInstance();
				}
				catch
				{
				}
				if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
				{
					SetF(gs, "versionNumber", _ogVersion);
					SetF(gs, "parsedMatchmakingNumber", null);
					SetF(gs, "parsedVersionNumberProd", null);
				}
				PlayerManager.maxPlayers = 4;
				if (_harmony != null)
				{
					_harmony.UnpatchSelf();
				}
				MoreCode.CleanGUI();
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("[更多联机] ResetToVanilla: " + ex.Message));
			}
		}

		public static string ModVersionFull => "modded_10-0-1";

		//多人模式是否生效：按过主菜单“多人联机”按钮（_modActive），
		//或当前房间实际超过 4 人（加入的 mod 房间会自动激活，见 LobbyManagerStartAutoActivateCtorPatch）
		private static bool Active => ModManager.AllEnabled && Enabled && (_modActive || MoreThan4Players());

		//多人模式是否已激活：启动/本地游戏/网络对战 = 原版 4 人；
		//只有按主菜单“多人联机”按钮后才是多人模式（多人补丁全部随 Active 门控）
		private static bool _modActive;

		//当前大厅实际玩家数是否超过 4（决定是否启用多人平衡板等）
		internal static bool MoreThan4Players()
		{
			try
			{
				if (LobbyManager.instance == null)
				{
					return PlayerManager.maxPlayers > 4;
				}
				int num = 0;
				foreach (LobbyPlayer lobbyPlayer in LobbyManager.instance.GetLobbyPlayers())
				{
					if (lobbyPlayer != null)
					{
						num++;
					}
				}
				return num > 4;
			}
			catch
			{
				return PlayerManager.maxPlayers > 4;
			}
		}

		public void Initialize(MainPlugin plugin)
		{
			//IL_0168: Unknown result type (might be due to invalid IL or missing references)
			//IL_0172: Expected O, but got Unknown
			_mp = plugin;
			_enabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("更多玩家", "Enabled", false, "更多联机总开关：开启后主菜单“更多联机”可容纳 8-100 人（本地游戏/网络对战仍为原版 4 人）。");
			_playerLimit = ((BaseUnityPlugin)plugin).Config.Bind<int>("更多玩家", "玩家上限", 17, "最多允许的玩家数（原版 4 人；本 Mod 可放宽到 8-100）。\n默认 17；改动后点击主菜单“更多联机”按钮生效。");
			_fullDebug = ((BaseUnityPlugin)plugin).Config.Bind<bool>("更多玩家", "完整调试", false, "输出更多调试日志（排查问题时再开）");
			_shuffleScoreBalancer = ((BaseUnityPlugin)plugin).Config.Bind<bool>("更多玩家", "平衡板置顶", true, "树屋平衡板上把最后修改的玩家显示在最上面（多人超过 4 人时更清晰）");
			int num = Mathf.Clamp(_playerLimit.Value, 2, 100);
			if (_playerLimit.Value != num)
			{
				_playerLimit.Value = num;
			}
			try
			{
				GameSettings gs = GameSettings.GetInstance();
				if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
				{
					string version = (string)GetF(gs, "versionNumber");
					if (!string.IsNullOrEmpty(version))
					{
						_ogVersion = version;
					}
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(_ogVersion))
			{
				_ogVersion = "1.0.0.0";
			}
			//启动保持原版 4 人：本地游戏/网络对战不受影响；只有按“多人联机”按钮才启用多人上限
			PlayerManager.maxPlayers = 4;
			_harmony = new Harmony("SR_UCH.MorePlayers");
			_harmony.CreateClassProcessor(typeof(Switch4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SwitchFirst4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SwitchSecond4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(Switch5ForNumPlusOnePatch)).Patch();
			_harmony.CreateClassProcessor(typeof(Switch3ForNumMinusOnePatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ChallengeScoreboardCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerClearPlayersPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerRemovePlayerPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameStateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GraphScoreBoardCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPointCounterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPointCounterResetCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(NetworkLobbyManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbySkillTrackerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlShuffleStartPositionCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameSettingsCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(InventoryBookCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerDisconnectCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(InputManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(NativeInputEnableXInputInputManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelPortalCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(StatTrackerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameSparksQueryLobbyCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LivesDisplayControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PlayerStatusDisplaySetSlotCountCtorPatch)).Patch();
#pragma warning disable CS0612 // NetworkManagerCtorPatch 标有 [Obsolete]（移植自原版，提示 Unity API 过时；补丁本身有效）
			_harmony.CreateClassProcessor(typeof(NetworkManagerCtorPatch)).Patch();
#pragma warning restore CS0612
			_harmony.CreateClassProcessor(typeof(PickableNetworkButtonCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlReceiveEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlReceiveEventDropInputPlayerNumber0Patch)).Patch();
			_harmony.CreateClassProcessor(typeof(StartGameStateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapStartCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapLineSetNameCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerDoCharacterPickedEventCtorPatch4Handicap)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerRpcRequestPickResponseCtorPatch4Handicap)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkButtonUpdateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkOnAcceptCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenOnClickCopyLobbyCodeCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenAwakeCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerRpcResetCharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(CharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(OutfitManagerRebuildDatabaseTakenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(OutfitControllerupdateImagesCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GetOutfitsAsArrayCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SetOutfitsFromArraySyncListIntCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SetOutfitsFromArrayCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerIsCharacterTakenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ModifiersCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerCmdRequestPickCharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerRpcRequestPickResponseCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerStartCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupLobbyAfterWaitCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerDoCharacterPickedEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupLobbyCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GraphScoreBoardMarkPlayerDisconnectedCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletMainMenuHomeUpdateCtorPatch)).Patch();
			MenuPatch.PatchMenu();
			MainPlugin.ModLogger.LogInfo((object)("[多人联机] 已启用，玩家上限 = " + num));
		}

		private static object GetF(object obj, string name)
		{
			try
			{
				FieldInfo fieldInfo = AccessTools.Field(obj.GetType(), name);
				return (fieldInfo != null) ? fieldInfo.GetValue(obj) : null;
			}
			catch
			{
				return null;
			}
		}

		private static void SetF(object obj, string name, object value)
		{
			try
			{
				FieldInfo fieldInfo = AccessTools.Field(obj.GetType(), name);
				if (fieldInfo != null)
				{
					fieldInfo.SetValue(obj, value);
				}
			}
			catch
			{
			}
		}

		private static object GetS(Type t, string name)
		{
			try
			{
				FieldInfo fieldInfo = AccessTools.Field(t, name);
				return (fieldInfo != null) ? fieldInfo.GetValue(null) : null;
			}
			catch
			{
				return null;
			}
		}

		private static void SetS(Type t, string name, object value)
		{
			try
			{
				FieldInfo fieldInfo = AccessTools.Field(t, name);
				if (fieldInfo != null)
				{
					fieldInfo.SetValue(null, value);
				}
			}
			catch
			{
			}
		}

		internal static string ModMatchmakingNumber()
		{
			if (_ogVersion == "1.0.0.0")
			{
				try
				{
					GameSettings gs = GameSettings.GetInstance();
					if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
					{
						string text = (string)GetF(gs, "versionNumber");
						if (!string.IsNullOrEmpty(text))
						{
							_ogVersion = text;
						}
					}
				}
				catch
				{
				}
			}
			string[] array = ModVersionFull.Split('-', StringSplitOptions.None);
			string[] array2 = _ogVersion.Split('.', StringSplitOptions.None);
			return array[0] + "-" + array[1] + "_" + array2[0] + "." + array2[1];
		}

		internal static void ReapplyPatches()
		{
			if (_harmony == null)
			{
				_harmony = new Harmony("SR_UCH.MorePlayers");
			}
			_harmony.CreateClassProcessor(typeof(Switch4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SwitchFirst4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SwitchSecond4ForMaxNumPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(Switch5ForNumPlusOnePatch)).Patch();
			_harmony.CreateClassProcessor(typeof(Switch3ForNumMinusOnePatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ChallengeScoreboardCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerClearPlayersPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerRemovePlayerPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameStateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GraphScoreBoardCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPointCounterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPointCounterResetCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(NetworkLobbyManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbySkillTrackerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlShuffleStartPositionCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameSettingsCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(InventoryBookCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ControllerDisconnectCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(InputManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(NativeInputEnableXInputInputManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelPortalCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(StatTrackerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameSparksQueryLobbyCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LivesDisplayControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PlayerStatusDisplaySetSlotCountCtorPatch)).Patch();
#pragma warning disable CS0612 // NetworkManagerCtorPatch 标有 [Obsolete]（移植自原版，提示 Unity API 过时；补丁本身有效）
			_harmony.CreateClassProcessor(typeof(NetworkManagerCtorPatch)).Patch();
#pragma warning restore CS0612
			_harmony.CreateClassProcessor(typeof(PickableNetworkButtonCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlReceiveEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GameControlReceiveEventDropInputPlayerNumber0Patch)).Patch();
			_harmony.CreateClassProcessor(typeof(StartGameStateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapStartCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(HandicapLineSetNameCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerDoCharacterPickedEventCtorPatch4Handicap)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerRpcRequestPickResponseCtorPatch4Handicap)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkButtonUpdateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkOnAcceptCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenOnClickCopyLobbyCodeCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenAwakeCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerRpcResetCharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(CharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(OutfitManagerRebuildDatabaseTakenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(OutfitControllerupdateImagesCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GetOutfitsAsArrayCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SetOutfitsFromArraySyncListIntCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(SetOutfitsFromArrayCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerIsCharacterTakenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(ModifiersCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupControllerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerCmdRequestPickCharacterCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerRpcRequestPickResponseCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerStartCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupLobbyAfterWaitCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LobbyPlayerDoCharacterPickedEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(LevelSelectControllerSetupLobbyCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(GraphScoreBoardMarkPlayerDisconnectedCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(VersusControlHandleEventCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletMainMenuHomeUpdateCtorPatch)).Patch();
		}

	}
}
