using System;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class ModMC : ITweak {

// ==== 分区：Core（配置 / 激活门控 / Initialize / 反射助手 / 联机列表版本号）====

		//模组联机总开关（配置项在「模组联机」分区，ModManager 自动发现）
		private static ConfigEntry<bool> _enabled;
		public static bool Enabled => _enabled != null && _enabled.Value;

		//模组联机是否已激活：点了主菜单「模组联机」按钮（_modActive），
		//或加入了 usingMods 房间（_autoActive，见 LobbyManagerCtorPatch.TryAutoActivate）
		private static bool _modActive;
		private static bool _autoActive;
		private static bool Active => ModManager.AllEnabled && Enabled && (_modActive || _autoActive);

		//供「更多联机」（MorePlayers）调用：把模组联机复位回原版（版本号/4 人/输入框），
		//避免两个联机入口的状态互相污染（点“更多联机”前先复位“模组联机”）
		public static void ResetToVanilla()
		{
			try
			{
				_modActive = false;
				_autoActive = false;
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
				ModCode.CleanGUI();
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("[模组联机] ResetToVanilla: " + ex.Message));
			}
		}

		//原版版本号快照（启动时读取，恢复时用）
		private static string _ogVersion = "1.0.0.0";

		//版本前缀：usingMods（用户指定，区别于 MorePlayers 的 modded），版本号 0817（用户指定，不碰后面拼接的游戏版本号）
		public static string ModVersionFull => "usingMods_0817";

		//大厅补丁（进入大厅后生效，靠 Active 门控，无需重打/卸载）
		private static Harmony _harmony;
		//菜单补丁（独立 Harmony：不受大厅补丁影响，一直存活）
		private static Harmony _menuHarmony;

		public void Initialize(MainPlugin plugin)
		{
			_enabled = ((BaseUnityPlugin)plugin).Config.Bind<bool>("模组联机", "Enabled", false, "模组联机总开关：主菜单出现「模组联机」按钮，进入只显示装了本 mod 房间的联机列表（原生 4 人，邀请码首位 R）。");
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
			//模组联机保持原生 4 人：不撑大玩家上限
			PlayerManager.maxPlayers = 4;
			_harmony = new Harmony("SR_UCH.ModMC");
			_harmony.CreateClassProcessor(typeof(LobbyManagerCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkButtonUpdateCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(PickableNetworkOnAcceptCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenOnClickCopyLobbyCodeCtorPatch)).Patch();
			_harmony.CreateClassProcessor(typeof(TabletLobbyOptionsScreenAwakeCtorPatch)).Patch();
			_menuHarmony = new Harmony("SR_UCH.ModMC.Menu");
			MenuPatch.PatchMenu(_menuHarmony);
			MainPlugin.ModLogger.LogInfo((object)("[模组联机] 已启用，版本前缀 = " + ModVersionFull));
		}

		//====================================================================
		// 反射助手（GameSettings 私有实例字段）
		//====================================================================
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

		//联机列表过滤用的 MatchmakingNumber：所有模组联机玩家一致（usingMods 前缀），
		//服务端 GetLobbyList 按它过滤 → 列表只显示装了本 mod 的房间
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
			string[] array2 = _ogVersion.Split('.', StringSplitOptions.None);
			return ModVersionFull + "_" + array2[0] + "." + array2[1];
		}

	}
}
