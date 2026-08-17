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

// ==== 分区：Handicap（平衡板置顶 / 洗牌填充）====

		internal class HandiShuffle
		{
			public static void ShowLine(HandicapLine line, bool show)
			{
				if (!((UnityEngine.Object)(object)line == (UnityEngine.Object)null))
				{
					if ((UnityEngine.Object)(object)line.ScorelineStretcher != (UnityEngine.Object)null)
					{
						line.ScorelineStretcher.SetActive(show);
					}
					if ((UnityEngine.Object)(object)line.AnimalName != (UnityEngine.Object)null)
					{
						((Component)line.AnimalName).gameObject.SetActive(show);
					}
					if ((UnityEngine.Object)(object)line.HandicapNumber != (UnityEngine.Object)null)
					{
						((Component)line.HandicapNumber).gameObject.SetActive(show);
					}
				}
			}

			public static void reCap()
			{
				//照原版：只受 shuffleScoreBalancer 控制（加总开关/分页开关）；不依赖人数/激活状态
				if (ModManager.AllEnabled && Enabled && ShuffleScoreBalancer)
				{
					GameObject val = GameObject.Find("Handicapper");
					if (!((UnityEngine.Object)(object)val == (UnityEngine.Object)null))
					{
						handicap component = val.GetComponent<handicap>();
						pushHandicaps(component, -1);
					}
				}
			}

			public static void pushHandicaps(handicap h, int networkPlayerNumber)
			{
				//IL_0145: Unknown result type (might be due to invalid IL or missing references)
				//IL_014b: Invalid comparison between Unknown and I4
				//照原版：只受 shuffleScoreBalancer 控制（加总开关/分页开关）；不依赖人数/激活状态
				if (!ModManager.AllEnabled || !Enabled || !ShuffleScoreBalancer || (UnityEngine.Object)(object)h == (UnityEngine.Object)null || h.HandicapLineSlots == null || h.HandicapLineSlots.Length < 4)
				{
					return;
				}
				try
				{
					HandicapLine componentInChildren = ((Component)h.HandicapLineSlots[0]).GetComponentInChildren<HandicapLine>();
					HandicapLine componentInChildren2 = ((Component)h.HandicapLineSlots[1]).GetComponentInChildren<HandicapLine>();
					HandicapLine componentInChildren3 = ((Component)h.HandicapLineSlots[2]).GetComponentInChildren<HandicapLine>();
					HandicapLine componentInChildren4 = ((Component)h.HandicapLineSlots[3]).GetComponentInChildren<HandicapLine>();
					HandicapLine[] array = (HandicapLine[])(object)new HandicapLine[4] { componentInChildren, componentInChildren2, componentInChildren3, componentInChildren4 };
					int[] source = new int[4] { componentInChildren.PlayerNetworkNumber, componentInChildren2.PlayerNetworkNumber, componentInChildren3.PlayerNetworkNumber, componentInChildren4.PlayerNetworkNumber };
					source = source.Where(delegate(int c)
					{
						//IL_0034: Unknown result type (might be due to invalid IL or missing references)
						//IL_003a: Invalid comparison between Unknown and I4
						LobbyPlayer val2 = (((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null) ? LobbyManager.instance.GetLobbyPlayer(c) : null);
						return c != networkPlayerNumber && c != -1 && ((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null || (int)val2.PickedAnimal > 0);
					}).ToArray();
					if (source.Length < 4 && (UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null)
					{
						//第一遍：优先放改过分的玩家（handicap != 100）
						foreach (LobbyPlayer lobbyPlayer in LobbyManager.instance.GetLobbyPlayers())
						{
							if (source.Length >= 4)
							{
								break;
							}
							if ((UnityEngine.Object)(object)lobbyPlayer != (UnityEngine.Object)null && (int)lobbyPlayer.PickedAnimal > 0 && lobbyPlayer.handicap != 100 && lobbyPlayer.networkNumber != networkPlayerNumber && !source.Contains(lobbyPlayer.networkNumber))
							{
								source = source.Append(lobbyPlayer.networkNumber).ToArray();
							}
						}
						//第二遍：其余槽位用任意已选角色的玩家补齐（全员默认 100 时板子也不会空）
						foreach (LobbyPlayer lobbyPlayer in LobbyManager.instance.GetLobbyPlayers())
						{
							if (source.Length >= 4)
							{
								break;
							}
							if ((UnityEngine.Object)(object)lobbyPlayer != (UnityEngine.Object)null && (int)lobbyPlayer.PickedAnimal > 0 && lobbyPlayer.networkNumber != networkPlayerNumber && !source.Contains(lobbyPlayer.networkNumber))
							{
								source = source.Append(lobbyPlayer.networkNumber).ToArray();
							}
						}
					}
					if (source.Length < 4)
					{
						int[] array2 = new int[4] { -1, -1, -1, -1 };
						for (int num = 0; num < source.Length; num++)
						{
							array2[num] = source[num];
						}
						source = array2;
					}
					Dictionary<int, float> dictionary = new Dictionary<int, float>();
					HandicapLine[] array3 = array;
					foreach (HandicapLine val in array3)
					{
						if (!((UnityEngine.Object)(object)val == (UnityEngine.Object)null))
						{
							if (!dictionary.ContainsKey(val.PlayerNetworkNumber) && val.PlayerNetworkNumber != -1)
							{
								dictionary.Add(val.PlayerNetworkNumber, (float)GetF(val, "currentHandicapFloat"));
							}
							val.PlayerNetworkNumber = -1;
						}
					}
					int[] array4 = new int[4]
					{
						networkPlayerNumber,
						source[0],
						source[1],
						source[2]
					};
					if (networkPlayerNumber == -1)
					{
						array4 = source;
					}
					int num3 = 0;
					int[] array5 = array4;
					foreach (int num5 in array5)
					{
						bool flag = num5 == -1;
						array[num3].PlayerNetworkNumber = num5;
						ShowLine(array[num3], !flag);
						if (!flag)
						{
							bool skipTransition = num5 != networkPlayerNumber || networkPlayerNumber == -1;
							updateHandicapLine(array[num3], num5, skipTransition, dictionary);
						}
						num3++;
					}
				}
				catch (Exception ex)
				{
					MainPlugin.ModLogger.LogWarning((object)("[多人联机] 平衡板更新失败: " + ex.Message));
				}
			}

			private static void updateHandicapLine(HandicapLine line, int networkPlayerNumber, bool skipTransition, Dictionary<int, float> ogNums)
			{
				//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
				//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
				//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
				//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
				//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
				//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					LobbyPlayer val = (((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null) ? LobbyManager.instance.GetLobbyPlayer(networkPlayerNumber) : null);
					if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
					{
						line.PlayerNetworkNumber = -1;
						ShowLine(line, show: false);
						return;
					}
					line.AnimalName.text = val.playerName;
					int handicap = val.handicap;
					SetF(line, "targetHandicap", handicap);
					line.HandicapNumber.text = handicap + "%";
					if (skipTransition)
					{
						float num = (float)handicap / 100f;
						SetF(line, "currentHandicapFloat", num);
						if ((UnityEngine.Object)(object)line.ScorelineStretcher != (UnityEngine.Object)null)
						{
							Vector3 val2 = (Vector3)GetF(line, "initialScale");
							line.ScorelineStretcher.transform.localScale = new Vector3(val2.x * num, val2.y, val2.z);
						}
					}
					else if (ogNums.ContainsKey(networkPlayerNumber))
					{
						SetF(line, "currentHandicapFloat", ogNums[networkPlayerNumber]);
					}
				}
				catch
				{
				}
			}
		}

		[HarmonyPatch(typeof(handicap), "handleEvent")]
		internal class HandicapHandleEventCtorPatch
		{
			private static void Postfix(handicap __instance, GameEvent.GameEvent e)
			{
				//IL_0050: Unknown result type (might be due to invalid IL or missing references)
				//IL_0057: Expected O, but got Unknown
				if (e == null)
				{
					return;
				}
				if (((object)e).GetType() == typeof(NetworkMessageReceivedEvent))
				{
					NetworkMessageReceivedEvent val = (NetworkMessageReceivedEvent)(object)((e is NetworkMessageReceivedEvent) ? e : null);
					short msgType = val.Message.msgType;
					if (msgType == NetMsgTypes.PlayerHandicapSet)
					{
						MsgPlayerHandicapSet val2 = (MsgPlayerHandicapSet)val.ReadMessage;
						HandiShuffle.pushHandicaps(__instance, val2.NetworkPlayerNumber);
					}
					if (msgType == NetMsgTypes.SetCustomPortalInfo || msgType == NetMsgTypes.CommunicateCharacterOutfits)
					{
						HandiShuffle.pushHandicaps(__instance, -1);
					}
				}
				else
				{
					HandiShuffle.pushHandicaps(__instance, -1);
				}
			}
		}

		[HarmonyPatch(typeof(handicap), "Start")]
		internal class HandicapStartCtorPatch
		{
			private static void Postfix(handicap __instance)
			{
				//照原版：只受 shuffleScoreBalancer 控制（加总开关/分页开关）
				if (!ModManager.AllEnabled || !Enabled || !ShuffleScoreBalancer)
				{
					return;
				}
				try
				{
					GameObject val = GameObject.Find("Handicapper/Canvas/Title");
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null)
					{
						val.GetComponent<Text>().text = "更多联机平衡板";
					}
					if (__instance.HandicapLineSlots != null)
					{
						Transform[] handicapLineSlots = __instance.HandicapLineSlots;
						foreach (Transform val2 in handicapLineSlots)
						{
							HandicapLine componentInChildren = ((Component)val2).GetComponentInChildren<HandicapLine>();
							if (!(bool)GetF(componentInChildren, "currentlyShown") || (int)GetF(componentInChildren, "targetHandicap") == -999)
							{
								componentInChildren.PlayerNetworkNumber = -1;
								HandiShuffle.ShowLine(componentInChildren, show: false);
							}
						}
					}
					GameEventManager.ChangeListener<CharacterPickedEvent>((IGameEventListener)(object)__instance, true);
					GameEventManager.ChangeListener<LobbyPlayerRemovedEvent>((IGameEventListener)(object)__instance, true);
					GameEventManager.ChangeListener<LobbyPlayerCreatedEvent>((IGameEventListener)(object)__instance, true);
					GameEventManager.ChangeListener<LocalPlayerAddedEvent>((IGameEventListener)(object)__instance, true);
					GameEventManager.ChangeListener<CharacterVoteEvent>((IGameEventListener)(object)__instance, true);
					GameEventManager.ChangeListener<GameEndEvent>((IGameEventListener)(object)__instance, true);
				}
				catch
				{
				}
			}
		}

		[HarmonyPatch(typeof(HandicapLine), "SetName")]
		internal class HandicapLineSetNameCtorPatch
		{
			private static bool Prefix(HandicapLine __instance, Character.Animals animal, bool altSkin)
			{
				//照原版：只受 shuffleScoreBalancer 控制（加总开关/分页开关）
				if (!ModManager.AllEnabled || !Enabled || !ShuffleScoreBalancer)
				{
					return true;
				}
				LobbyPlayer val = (((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null) ? LobbyManager.instance.GetLobbyPlayer(__instance.PlayerNetworkNumber) : null);
				if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null)
				{
					return true;
				}
				__instance.AnimalName.text = val.playerName;
				return false;
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "CmdSetPlayerHandicap")]
		internal class LobbyPlayerHandleEventCtorPatch
		{
			private static void Postfix(LobbyPlayer __instance, int newHandicap)
			{
				HandiShuffle.reCap();
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "DoCharacterPickedEvent")]
		internal class LobbyPlayerDoCharacterPickedEventCtorPatch4Handicap
		{
			private static void Postfix(ref bool clearOutfit)
			{
				HandiShuffle.reCap();
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "RpcRequestPickResponse")]
		internal class LobbyPlayerRpcRequestPickResponseCtorPatch4Handicap
		{
			private static void Postfix()
			{
				HandiShuffle.reCap();
			}
		}

	}
}
