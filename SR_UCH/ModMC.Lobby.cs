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
public partial class ModMC {

// ==== 分区：Lobby（大厅自动激活 / R 码邀请码 / 房间码显示）====

		//====================================================================
		// 大厅：加入 usingMods 房自动激活 + 房主建房后标记 mod 房
		//====================================================================
		[HarmonyPatch(typeof(LobbyManager), "Awake")]
		internal class LobbyManagerCtorPatch
		{
			private static void Postfix(LobbyManager __instance)
			{
				TryAutoActivate();
				//房主建房后设置 usingMods 标志（列表 UI 显示 mod 图标）
				if (Active && Matchmaker.CurrentMatchmakingLobby != null)
				{
					try
					{
						if (GameSettings.GetInstance().StartAsHost)
						{
							Matchmaker.CurrentMatchmakingLobby.SetLobbyUsingMods(true);
						}
					}
					catch
					{
					}
				}
			}

			//加入 usingMods 房间自动激活：房主按「模组联机」建房后房间版本号是 usingMods_…，
			//其他玩家直接加入（房间码/邀请，没按按钮）时检测到 usingMods 版本 → 本客户端同样进入模组联机
			internal static void TryAutoActivate()
			{
				try
				{
					if (!Enabled || _modActive) return;
					if (Matchmaker.CurrentMatchmakingLobby == null) return;
					string ver = Matchmaker.CurrentMatchmakingLobby.GetLobbyVersion();
					if (ver != null && ver.StartsWith("usingMods"))
					{
						_autoActive = true;
						//保持原生 4 人（绝不撑大上限）
						PlayerManager.maxPlayers = 4;
					}
				}
				catch
				{
				}
			}
		}

		//====================================================================
		// 邀请码：5 位、第一位 R（对应「更多联机」M 码机制）
		//====================================================================
		internal class ModCode
		{
			public const char Marker = 'R';

			public const string Stars = "*****";

			public static float lastCodeInputFocus;

			public static bool IsValid(string code)
			{
				return code != null && code.Length == 5 && (code[0] == 'R' || code[0] == char.ToLower('R'));
			}

			public static string Fudge(string code)
			{
				return "R" + code;
			}

			public static string UnFudge(string code)
			{
				return code.Substring(1);
			}

			public static void FudgeJoin(PickableNetworkButton btn, string text)
			{
				if (Util_String.NullOrEmpty(text))
				{
					return;
				}
				btn.inputField.text = text;
				GameSettings.GetInstance().StartAsHost = false;
				GameSettings.GetInstance().StartLocal = false;
				Matchmaker.Instance.JoinLobby(UnFudge(btn.inputField.text), true, (UnityAction<bool>)delegate(bool success)
				{
					if (success && Matchmaker.CurrentMatchmakingLobby != null)
					{
						//加入 usingMods 房间立即自动激活（不等 LobbyManager.Awake）
						LobbyManagerCtorPatch.TryAutoActivate();
						AnalyticEvent.JoinMatchEvent(Matchmaker.CurrentMatchmakingLobby.GetLobbyGuid(), (AnalyticEvent.JoinMethod)1, Matchmaker.CurrentMatchmakingLobby.LobbyIsCrossplay(Application.platform));
					}
				});
			}

			public static string CleanCode(string code)
			{
				if (Util_String.NullOrEmpty(code))
				{
					return null;
				}
				string text = Regex.Replace(code.ToUpper(), "[^A-Za-z]", "");
				if (!IsValid(text))
				{
					return null;
				}
				return text;
			}

			//恢复输入框为原版 4 位（离开模组联机模式时调用）
			public static void CleanGUI()
			{
				GameObject val = GameObject.Find("CodeInputField");
				InputField val2 = (((UnityEngine.Object)(object)val != (UnityEngine.Object)null) ? val.GetComponent<InputField>() : null);
				if ((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null)
				{
					val2.characterLimit = 4;
					if ((UnityEngine.Object)(object)val2.placeholder != (UnityEngine.Object)null)
					{
						((Component)val2.placeholder).GetComponent<Text>().text = "ABCD";
					}
				}
			}
		}

		//输入框/我的房间码显示 R 前缀（仅模组联机激活时生效）
		[HarmonyPatch(typeof(PickableNetworkButton), "Update")]
		internal class PickableNetworkButtonUpdateCtorPatch
		{
			private static bool Prefix(PickableNetworkButton __instance)
			{
				if (!Active)
				{
					return true;
				}
				PickableNetworkButton.NetworkButtonJobs job = __instance.job;
				if (job == PickableNetworkButton.NetworkButtonJobs.EnterLobbyCode)
				{
					if (__instance.inputField.isFocused)
					{
						ModCode.lastCodeInputFocus = 0.15f;
					}
					else if (ModCode.lastCodeInputFocus > 0f)
					{
						ModCode.lastCodeInputFocus -= Time.deltaTime;
					}
					__instance.inputField.characterLimit = 5;
					if ((UnityEngine.Object)(object)__instance.inputField.placeholder != (UnityEngine.Object)null)
					{
						((Component)__instance.inputField.placeholder).GetComponent<Text>().text = ModCode.Fudge("ABCD");
					}
					if (Input.GetKeyDown((KeyCode)13) && ModCode.IsValid(__instance.inputField.text) && ModCode.lastCodeInputFocus > 0f)
					{
						UserMessageManager.Instance.UserMessage(ModManager.T("正在加入: ", "Joining: ") + __instance.inputField.text, 2f, (UserMessageManager.UserMsgPriority)0, true);
						ModCode.FudgeJoin(__instance, __instance.inputField.text);
					}
					SetF(__instance, "currentlyShowing", true);
					return false;
				}
				if (job == PickableNetworkButton.NetworkButtonJobs.JoinLobbyByCode)
				{
					SetF(__instance, "currentlyShowing", ModCode.IsValid(__instance.inputField.text));
					return false;
				}
				if (job == PickableNetworkButton.NetworkButtonJobs.MyLobbyCode)
				{
					SetF(__instance, "currentlyShowing", GameSettings.GetInstance().UseUnityRelay);
					if (PickableNetworkButton.showCode && Matchmaker.CurrentMatchmakingLobby != null)
					{
						if ((UnityEngine.Object)(object)((PickableButton)__instance).buttonText != (UnityEngine.Object)null)
						{
							((PickableButton)__instance).buttonText.text = ModCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
							return false;
						}
					}
					else if ((UnityEngine.Object)(object)((PickableButton)__instance).buttonText != (UnityEngine.Object)null)
					{
						((PickableButton)__instance).buttonText.text = "*****";
						return false;
					}
					return false;
				}
				return true;
			}
		}

		//回车/点击输入框确认 → 用 R 码加入
		[HarmonyPatch(typeof(PickableNetworkButton), "OnAccept")]
		internal class PickableNetworkOnAcceptCtorPatch
		{
			private static bool Prefix(PickableNetworkButton __instance)
			{
				if (!Active)
				{
					return true;
				}
				PickableNetworkButton.NetworkButtonJobs job = __instance.job;
				if ((int)job != 38)
				{
					if ((int)job != 42)
					{
						if ((int)job == 50)
						{
							string text = ModCode.CleanCode(GUIUtility.systemCopyBuffer);
							ModCode.FudgeJoin(__instance, text);
							return false;
						}
						return true;
					}
					if (Matchmaker.CurrentMatchmakingLobby != null)
					{
						GUIUtility.systemCopyBuffer = ModCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
						UserMessageManager.Instance.UserMessage(ModManager.T("房间码已复制到剪贴板", "Room code copied to clipboard"), 2f, (UserMessageManager.UserMsgPriority)0, true);
					}
					return false;
				}
				ModCode.FudgeJoin(__instance, __instance.inputField.text);
				return false;
			}
		}

		//大厅选项：显示/隐藏房间码 → R 前缀（仅模组联机激活时生效）
		[HarmonyPatch(typeof(TabletLobbyOptionsScreen), "OnClickShowToggle")]
		internal class TabletLobbyOptionsScreenCtorPatch
		{
			private static bool Prefix(TabletLobbyOptionsScreen __instance)
			{
				if (!Active)
				{
					return true;
				}
				SetF(__instance, "lobbyCodeShown", !(bool)GetF(__instance, "lobbyCodeShown"));
				if ((bool)GetF(__instance, "lobbyCodeShown"))
				{
					__instance.lobbyCodeText.text = ModCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
				}
				else
				{
					__instance.lobbyCodeText.text = "*****";
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(TabletLobbyOptionsScreen), "OnClickCopyLobbyCode")]
		internal class TabletLobbyOptionsScreenOnClickCopyLobbyCodeCtorPatch
		{
			private static bool Prefix()
			{
				if (!Active)
				{
					return true;
				}
				QuickSaver.CopyStringToClipboard(ModCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode()));
				UserMessageManager.Instance.UserMessage("房间码已复制到剪贴板", 2f, (UserMessageManager.UserMsgPriority)0, true);
				return false;
			}
		}

		[HarmonyPatch(typeof(TabletLobbyOptionsScreen), "Awake")]
		internal class TabletLobbyOptionsScreenAwakeCtorPatch
		{
			private static void Prefix(TabletLobbyOptionsScreen __instance)
			{
				__instance.lobbyCodeText.text = "*****";
			}
		}

	}
}
