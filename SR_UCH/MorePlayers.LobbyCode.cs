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
public partial class MorePlayers {

// ==== 分区：LobbyCode（M 码邀请码 / 大厅选项 / 输入框前缀）====

		internal class MoreCode
		{
			public const char Marker = 'M';

			public const string Stars = "*****";

			public static float lastCodeInputFocus;

			public static bool IsValid(string code)
			{
				return code != null && code.Length == 5 && (code[0] == 'M' || code[0] == char.ToLower('M'));
			}

			public static string Fudge(string code)
			{
				return "M" + code;
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
					//IL_0024: Unknown result type (might be due to invalid IL or missing references)
					if (success && Matchmaker.CurrentMatchmakingLobby != null)
					{
						//加入 mod 房间立即自动激活（不等 LobbyManager.Awake）
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

		[HarmonyPatch(typeof(PickableNetworkButton), "Update")]
		internal class PickableNetworkButtonUpdateCtorPatch
		{
			private static bool Prefix(PickableNetworkButton __instance)
			{
				//IL_0015: Unknown result type (might be due to invalid IL or missing references)
				//IL_001a: Unknown result type (might be due to invalid IL or missing references)
				//IL_001b: Unknown result type (might be due to invalid IL or missing references)
				//IL_001e: Unknown result type (might be due to invalid IL or missing references)
				//IL_0021: Expected I4, but got Unknown
				if (!Active)
				{
					return true;
				}
				PickableNetworkButton.NetworkButtonJobs job = __instance.job;
				if (job == PickableNetworkButton.NetworkButtonJobs.EnterLobbyCode)
				{
					if (__instance.inputField.isFocused)
					{
						MoreCode.lastCodeInputFocus = 0.15f;
					}
					else if (MoreCode.lastCodeInputFocus > 0f)
					{
						MoreCode.lastCodeInputFocus -= Time.deltaTime;
					}
					__instance.inputField.characterLimit = 5;
					if ((UnityEngine.Object)(object)__instance.inputField.placeholder != (UnityEngine.Object)null)
					{
						((Component)__instance.inputField.placeholder).GetComponent<Text>().text = MoreCode.Fudge("ABCD");
					}
					if (Input.GetKeyDown((KeyCode)13) && MoreCode.IsValid(__instance.inputField.text) && MoreCode.lastCodeInputFocus > 0f)
					{
						UserMessageManager.Instance.UserMessage(ModManager.T("正在加入: ", "Joining: ") + __instance.inputField.text, 2f, (UserMessageManager.UserMsgPriority)0, true);
						MoreCode.FudgeJoin(__instance, __instance.inputField.text);
					}
					SetF(__instance, "currentlyShowing", true);
					return false;
				}
				if (job == PickableNetworkButton.NetworkButtonJobs.JoinLobbyByCode)
				{
					SetF(__instance, "currentlyShowing", MoreCode.IsValid(__instance.inputField.text));
					return false;
				}
				if (job == PickableNetworkButton.NetworkButtonJobs.MyLobbyCode)
				{
					SetF(__instance, "currentlyShowing", GameSettings.GetInstance().UseUnityRelay);
					if (PickableNetworkButton.showCode && Matchmaker.CurrentMatchmakingLobby != null)
					{
						if ((UnityEngine.Object)(object)((PickableButton)__instance).buttonText != (UnityEngine.Object)null)
						{
							((PickableButton)__instance).buttonText.text = MoreCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
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

		[HarmonyPatch(typeof(PickableNetworkButton), "OnAccept")]
		internal class PickableNetworkOnAcceptCtorPatch
		{
			private static bool Prefix(PickableNetworkButton __instance)
			{
				//IL_0015: Unknown result type (might be due to invalid IL or missing references)
				//IL_001a: Unknown result type (might be due to invalid IL or missing references)
				//IL_001b: Unknown result type (might be due to invalid IL or missing references)
				//IL_001e: Invalid comparison between Unknown and I4
				//IL_0028: Unknown result type (might be due to invalid IL or missing references)
				//IL_002b: Invalid comparison between Unknown and I4
				//IL_0037: Unknown result type (might be due to invalid IL or missing references)
				//IL_003a: Invalid comparison between Unknown and I4
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
							string text = MoreCode.CleanCode(GUIUtility.systemCopyBuffer);
							MoreCode.FudgeJoin(__instance, text);
							return false;
						}
						return true;
					}
					if (Matchmaker.CurrentMatchmakingLobby != null)
					{
						GUIUtility.systemCopyBuffer = MoreCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
						UserMessageManager.Instance.UserMessage(ModManager.T("房间码已复制到剪贴板", "Room code copied to clipboard"), 2f, (UserMessageManager.UserMsgPriority)0, true);
					}
					return false;
				}
				MoreCode.FudgeJoin(__instance, __instance.inputField.text);
				return false;
			}
		}

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
					__instance.lobbyCodeText.text = MoreCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode());
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
				QuickSaver.CopyStringToClipboard(MoreCode.Fudge(Matchmaker.CurrentMatchmakingLobby.GetLobbyCode()));
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
