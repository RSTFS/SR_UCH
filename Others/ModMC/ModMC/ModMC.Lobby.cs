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

namespace ModMC {
public partial class ModLobbyPlugin {

// ==== Partition: Lobby (auto-activate / R invite code / room code display) ====

		//====================================================================
		// Lobby: joining a usingMods room auto-activates; the host marks the
		// lobby as a mod lobby after creating it.
		//====================================================================
		[HarmonyPatch(typeof(LobbyManager), "Awake")]
		internal class LobbyManagerCtorPatch
		{
			private static void Postfix(LobbyManager __instance)
			{
				TryAutoActivate();
				//Host created a lobby: flag it as a mod lobby (list UI shows the mod icon).
				if (Active && Matchmaker.CurrentMatchmakingLobby != null)
				{
					try
					{
						if (GameSettings.GetInstance().StartAsHost)
						{
							Matchmaker.CurrentMatchmakingLobby.SetLobbyUsingMods(true);
						}
					}
					catch { }
				}
			}

			//Joining a usingMods room auto-activates: the host started the lobby
			//via "Mod Lobby", so the room version is usingMods_...; other players
			//who join directly (room code / invite, without pressing the button)
			//detect the usingMods version -> this client enters the mod lobby too.
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
						//Keep the vanilla 4-player cap (never inflated).
						PlayerManager.maxPlayers = 4;
					}
				}
				catch { }
			}
		}

		//====================================================================
		// Invite code: 5 chars, first char 'R' (the "More Online" M-code
		// mechanism, mirrored).
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
						//Auto-activate immediately on joining a usingMods room
						//(do not wait for LobbyManager.Awake).
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

			//Restore the input field to the vanilla 4 chars (called when leaving
			//the mod lobby).
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

		//Input field / "my room code" display gets the R prefix (mod lobby only).
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
						UserMessageManager.Instance.UserMessage("Joining: " + __instance.inputField.text, 2f, (UserMessageManager.UserMsgPriority)0, true);
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

		//Enter / click confirm on the code input -> join with the R code.
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
						UserMessageManager.Instance.UserMessage("Room code copied to clipboard", 2f, (UserMessageManager.UserMsgPriority)0, true);
					}
					return false;
				}
				ModCode.FudgeJoin(__instance, __instance.inputField.text);
				return false;
			}
		}

		//Lobby options: show/hide room code -> R prefix (mod lobby only).
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
				UserMessageManager.Instance.UserMessage("Room code copied to clipboard", 2f, (UserMessageManager.UserMsgPriority)0, true);
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
