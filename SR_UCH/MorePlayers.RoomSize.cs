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

// ==== 分区：RoomSize（房间人数扩展 8-100：硬编码 4 替换 / 槽位数组扩容 / 大厅容量）====

		[HarmonyPatch]
		internal class Switch4ForMaxNumPatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(BeeSwarm), "Awake", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(ChallengeScoreboard), "CollectPlayerIds", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(Controller), "AddPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(Controller), "AssociateCharacter", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(ControllerDisconnect), "SetPromptForPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(GraphScoreBoard), "SetPlayerCount", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(InventoryBook), "AddPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(InventoryBook), "GetCursor", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(InventoryBook), "HasCursor", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(KeyboardInput), "Reset", (Type[])null, (Type[])null);
				yield return AccessTools.Constructor(typeof(KickTracker), (Type[])null, false);
				yield return AccessTools.Method(typeof(KickTracker), "ClearPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(KickTracker), "CountVotes", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(KickTracker), "VotesFromNetworkNumber", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(LobbyPointCounter), "handleEvent", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(PartyBox), "SetPlayerCount", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(PickableNetworkButton), "OnAccept", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(PlayerStatusDisplay), "SetSlotCount", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(StatTracker), "GetSaveFileDataForLocalPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(StatTracker), "OnLocalPlayerAdded", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(StatTracker), "SaveGameForAnimal", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(SteamLobbySearchList), "checkForListUpdates", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(SteamMatchmaker), "createSocialLobby", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(SwitchController), "Reset", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(TurnIndicator), "SetPlayerCount", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(UnityMatchmaker), "CheckHostConnectivity", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(VersusControl), "get_playersLeftToPlace", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				foreach (CodeInstruction inst in e)
				{
					if (inst.opcode == OpCodes.Ldc_I4_4)
					{
						inst.opcode = OpCodes.Ldc_I4;
						inst.operand = PlayerManager.maxPlayers;
					}
					yield return inst;
				}
			}
		}

		[HarmonyPatch]
		internal class SwitchFirst4ForMaxNumPatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(PartyBox), "AddPlayer", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(LobbySkillTracker), "RecalculateScores", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(PickableNetworkButton), "Update", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				int count = 0;
				foreach (CodeInstruction inst in e)
				{
					if (inst.opcode == OpCodes.Ldc_I4_4 && count == 0)
					{
						inst.opcode = OpCodes.Ldc_I4;
						inst.operand = PlayerManager.maxPlayers;
						count++;
					}
					yield return inst;
				}
			}
		}

		[HarmonyPatch]
		internal class SwitchSecond4ForMaxNumPatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(LobbySkillTracker), "UpdateLobbyInfo", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(UnityMatchmaker), "onLobbyJoined", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				int count = 0;
				foreach (CodeInstruction inst in e)
				{
					if (inst.opcode == OpCodes.Ldc_I4_4)
					{
						if (count == 1)
						{
							inst.opcode = OpCodes.Ldc_I4;
							inst.operand = PlayerManager.maxPlayers;
						}
						count++;
					}
					yield return inst;
				}
			}
		}

		[HarmonyPatch]
		internal class Switch5ForNumPlusOnePatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(SteamMatchmaker), "OnSteamLobbyJoinRequested", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(LobbyManager), "OnLobbyClientAddPlayerFailed", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(Matchmaker), "CleanUpPlayers", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				foreach (CodeInstruction inst in e)
				{
					if (inst.opcode == OpCodes.Ldc_I4_4)
					{
						inst.opcode = OpCodes.Ldc_I4;
						inst.operand = PlayerManager.maxPlayers + 1;
					}
					yield return inst;
				}
			}
		}

		[HarmonyPatch]
		internal class Switch3ForNumMinusOnePatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(Controller), "GetLastPlayerNumber", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(Controller), "GetLastPlayerNumberAfter", (Type[])null, (Type[])null);
				yield return AccessTools.Method(typeof(GraphScoreBoard), "SetPlayerCharacter", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				foreach (CodeInstruction inst in e)
				{
					if (inst.opcode == OpCodes.Ldc_I4_3)
					{
						inst.opcode = OpCodes.Ldc_I4;
						inst.operand = PlayerManager.maxPlayers - 1;
					}
					yield return inst;
				}
			}
		}

[HarmonyPatch(typeof(ChallengeScoreboard), MethodType.Constructor)]
		internal class ChallengeScoreboardCtorPatch
		{
			private static void Postfix(ChallengeScoreboard __instance)
			{
				if (Active)
				{
					SetF(__instance, "players", new ChallengeScoreboard.ChallengePlayer[PlayerManager.maxPlayers]);
				}
			}
		}

[HarmonyPatch(typeof(Tablet), MethodType.Constructor)]
		internal class TabletCtorPatch
		{
			private static void Postfix(Tablet __instance)
			{
				if (Active)
				{
					SetF(__instance, "untrackedCursors", new List<PickCursor>(PlayerManager.maxPlayers));
				}
			}
		}

[HarmonyPatch(typeof(Controller), MethodType.Constructor)]
		internal class ControllerCtorPatch
		{
			private static void Postfix(Controller __instance)
			{
				if (Active)
				{
					SetF(__instance, "associatedChars", new Character.Animals[PlayerManager.maxPlayers]);
				}
			}
		}

		[HarmonyPatch(typeof(Controller), "ClearPlayers")]
		internal class ControllerClearPlayersPatch
		{
			private static void Postfix(Controller __instance)
			{
				if (Active)
				{
					SetF(__instance, "associatedChars", new Character.Animals[PlayerManager.maxPlayers]);
				}
			}
		}

		[HarmonyPatch(typeof(Controller), "RemovePlayer")]
		internal class ControllerRemovePlayerPatch
		{
			private static bool Prefix(Controller __instance, int player)
			{
				if (!Active)
				{
					return true;
				}
				int num = ~(1 << player - 1);
				SetF(__instance, "Player", (int)GetF(__instance, "Player") & num);
				Character.Animals[] array = (Character.Animals[])GetF(__instance, "associatedChars");
				if (array != null && player - 1 >= 0 && player - 1 < array.Length)
				{
					array[player - 1] = (Character.Animals)0;
				}
				if ((int)GetF(__instance, "Player") == 0)
				{
					__instance.PossibleNetWorkNumber = 0;
				}
				return false;
			}
		}

[HarmonyPatch(typeof(GameState), MethodType.Constructor)]
		internal class GameStateCtorPatch
		{
			private static void Postfix(GameState __instance)
			{
				if (Active)
				{
					__instance.PlayerScores = new int[PlayerManager.maxPlayers];
				}
			}
		}

		[HarmonyPatch(typeof(GraphScoreBoard), "SetPlayerCount")]
		internal class GraphScoreBoardCtorPatch
		{
			private static bool Prefix(GraphScoreBoard __instance, int numberPlayers)
			{
				//IL_0067: Unknown result type (might be due to invalid IL or missing references)
				//IL_007b: Unknown result type (might be due to invalid IL or missing references)
				//IL_0080: Unknown result type (might be due to invalid IL or missing references)
				//IL_0097: Unknown result type (might be due to invalid IL or missing references)
				//IL_009c: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
				//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
				//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
				//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
				if (!Active)
				{
					return true;
				}
				if (__instance.ScorePositions != null && __instance.ScorePositions.Length < PlayerManager.maxPlayers)
				{
					Array.Resize(ref __instance.ScorePositions, PlayerManager.maxPlayers);
				}
				SetF(__instance, "playerScoreLines", new ScoreLine[numberPlayers]);
				for (int i = 0; i != numberPlayers; i++)
				{
					Vector3 val = ((Transform)__instance.ScorePositions[0]).position + new Vector3(0f, 1.25f, 0f) - new Vector3(0f, (float)i * 1.25f, 0f);
					GameObject val2 = UnityEngine.Object.Instantiate<GameObject>(((Component)__instance.scoreLinePrefab).gameObject, val, Quaternion.identity);
					val2.transform.SetParent((Transform)(object)__instance.mainParent);
					val2.transform.localScale = new Vector3(1f, 0.5f, 1f);
					ScoreLine[] array = (ScoreLine[])GetF(__instance, "playerScoreLines");
					array[i] = val2.GetComponent<ScoreLine>();
					array[i].scoreBoardParent = __instance;
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "Awake")]
		internal class LevelSelectControllerCtorPatch
		{
			private static void Postfix(LevelSelectController __instance)
			{
				__instance.JoinedPlayers = (LobbyPlayer[])(object)new LobbyPlayer[PlayerManager.maxPlayers];
				if (__instance.PlayerJoinIndicators != null && __instance.PlayerJoinIndicators.Length < PlayerManager.maxPlayers)
				{
					int num = __instance.PlayerJoinIndicators.Length;
					Array.Resize(ref __instance.PlayerJoinIndicators, PlayerManager.maxPlayers);
					for (int i = num; i < __instance.PlayerJoinIndicators.Length; i++)
					{
						__instance.PlayerJoinIndicators[i] = __instance.PlayerJoinIndicators[i % num];
					}
				}
				if (__instance.CursorSpawnPoint != null && __instance.CursorSpawnPoint.Length < PlayerManager.maxPlayers)
				{
					int num2 = __instance.CursorSpawnPoint.Length;
					Array.Resize(ref __instance.CursorSpawnPoint, PlayerManager.maxPlayers);
					for (int j = num2; j < __instance.CursorSpawnPoint.Length; j++)
					{
						__instance.CursorSpawnPoint[j] = __instance.CursorSpawnPoint[j % num2];
					}
				}
				if (__instance.UndergroundCharacterPosition != null && __instance.UndergroundCharacterPosition.Length < PlayerManager.maxPlayers)
				{
					int num3 = __instance.UndergroundCharacterPosition.Length;
					Array.Resize(ref __instance.UndergroundCharacterPosition, PlayerManager.maxPlayers);
					for (int k = num3; k < __instance.UndergroundCharacterPosition.Length; k++)
					{
						__instance.UndergroundCharacterPosition[k] = __instance.UndergroundCharacterPosition[k % num3];
					}
				}
			}
		}

[HarmonyPatch(typeof(LobbyPointCounter), MethodType.Constructor)]
		internal class LobbyPointCounterCtorPatch
		{
			private static void Postfix(LobbyPointCounter __instance)
			{
				if (Active)
				{
					SetF(__instance, "playerJoinedGame", new bool[PlayerManager.maxPlayers]);
					SetF(__instance, "playerPlayedGame", new bool[PlayerManager.maxPlayers]);
					SetF(__instance, "playerAFK", new bool[PlayerManager.maxPlayers]);
				}
			}
		}

		[HarmonyPatch(typeof(LobbyPointCounter), "Reset")]
		internal class LobbyPointCounterResetCtorPatch
		{
			private static void Postfix(LobbyPointCounter __instance)
			{
				if (Active)
				{
					SetF(__instance, "playerPlayedGame", new bool[PlayerManager.maxPlayers]);
				}
			}
		}

[HarmonyPatch(typeof(UnityEngine.Networking.NetworkLobbyManager), MethodType.Constructor)]
		internal class NetworkLobbyManagerCtorPatch
		{
			private static void Postfix(NetworkLobbyManager __instance)
			{
				if (Active)
				{
					__instance.maxPlayers = PlayerManager.maxPlayers;
				}
			}
		}

		[HarmonyPatch(typeof(LobbySkillTracker), "Start")]
		internal class LobbySkillTrackerCtorPatch
		{
			private static void Postfix(LobbySkillTracker __instance)
			{
				if (Active)
				{
					SetF(__instance, "ratings", new Rating[PlayerManager.maxPlayers]);
				}
			}
		}

[HarmonyPatch(typeof(VersusControl), MethodType.Constructor)]
		internal class VersusControlCtorPatch
		{
			private static void Postfix(VersusControl __instance)
			{
				if (Active)
				{
					SetF(__instance, "winOrder", new GamePlayer[PlayerManager.maxPlayers]);
					SetF(__instance, "RemainingPlacements", new int[PlayerManager.maxPlayers]);
				}
			}
		}

		[HarmonyPatch(typeof(VersusControl), "ShuffleStartPosition")]
		internal class VersusControlShuffleStartPositionCtorPatch
		{
			private static bool Prefix(VersusControl __instance)
			{
				//IL_001e: Unknown result type (might be due to invalid IL or missing references)
				//IL_0024: Expected O, but got Unknown
				if (!Active)
				{
					return true;
				}
				List<int> list = new List<int>();
				GameControl obj = (GameControl)__instance;
				List<GamePlayer> list2 = (List<GamePlayer>)GetF(obj, "PlayerQueue");
				if (list2 == null)
				{
					return false;
				}
				for (int i = 0; i < list2.Count; i++)
				{
					list.Add(i % 4 + 1);
				}
				string text = "";
				for (int j = 0; j < list2.Count; j++)
				{
					int index = UnityEngine.Random.Range(0, list.Count);
					text += list[index];
					list.RemoveAt(index);
				}
				__instance.NetworkRandomStartPositionString = text;
				return false;
			}
		}

		[HarmonyPatch(typeof(LobbyManager), "Awake")]
		internal class LobbyManagerCtorPatch
		{
			private static void Postfix(LobbyManager __instance)
			{
				TryAutoActivate();
				if (Active)
				{
					((NetworkLobbyManager)__instance).maxPlayers = PlayerManager.maxPlayers;
					((NetworkLobbyManager)__instance).maxPlayersPerConnection = PlayerManager.maxPlayers;
					if (((NetworkLobbyManager)__instance).lobbySlots != null && ((NetworkLobbyManager)__instance).lobbySlots.Length < PlayerManager.maxPlayers)
					{
						Array.Resize(ref ((NetworkLobbyManager)__instance).lobbySlots, PlayerManager.maxPlayers);
					}
				}
			}

			//加入 mod 房间自动激活：房主按“多人联机”建房后房间版本号是 modded_…，
			//其他玩家直接加入（房间码/邀请，没按按钮）时检测到 modded 版本 → 本客户端同样进入多人模式
			//（否则这些客户端 Active=false：不能重复选角色、平衡板不工作、槽位仍是 4）
			internal static void TryAutoActivate()
			{
				try
				{
					if (!Enabled || _modActive) return;
					if (Matchmaker.CurrentMatchmakingLobby == null) return;
					string ver = Matchmaker.CurrentMatchmakingLobby.GetLobbyVersion();
					if (ver != null && ver.StartsWith("modded"))
					{
						_modActive = true;
						PlayerManager.maxPlayers = Mathf.Clamp(PlayerLimit, 2, 100);
					}
				}
				catch
				{
				}
			}
		}

		[HarmonyPatch(typeof(GameSettings), "GetInstance")]
		internal class GameSettingsCtorPatch
		{
			private static void Postfix(GameSettings __result)
			{
				//IL_008f: Unknown result type (might be due to invalid IL or missing references)
				//IL_0094: Unknown result type (might be due to invalid IL or missing references)
				if (!Active)
				{
					return;
				}
				__result.MaxPlayers = PlayerManager.maxPlayers;
				if (__result.PlayerColors != null && __result.PlayerColors.Length < PlayerManager.maxPlayers)
				{
					int num = __result.PlayerColors.Length;
					Array.Resize(ref __result.PlayerColors, PlayerManager.maxPlayers);
					for (int i = num; i < __result.PlayerColors.Length; i++)
					{
						__result.PlayerColors[i] = new Color(UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f));
					}
				}
			}
		}

		[HarmonyPatch(typeof(InventoryBook), "AddPlayer")]
		internal class InventoryBookCtorPatch
		{
			private static void Prefix(InventoryBook __instance)
			{
				if (Active && __instance.cursorSpawnLocation != null && __instance.cursorSpawnLocation.Length < PlayerManager.maxPlayers)
				{
					int num = __instance.cursorSpawnLocation.Length;
					Array.Resize(ref __instance.cursorSpawnLocation, PlayerManager.maxPlayers);
					for (int i = num; i < __instance.cursorSpawnLocation.Length; i++)
					{
						__instance.cursorSpawnLocation[i] = __instance.cursorSpawnLocation[0];
					}
				}
			}
		}

		[HarmonyPatch(typeof(ControllerDisconnect), "Start")]
		internal class ControllerDisconnectCtorPatch
		{
			private static void Prefix(ControllerDisconnect __instance)
			{
				if (!Active)
				{
					return;
				}
				if (__instance.ConnectPrompts != null && __instance.ConnectPrompts.Length < PlayerManager.maxPlayers)
				{
					int num = __instance.ConnectPrompts.Length;
					Array.Resize(ref __instance.ConnectPrompts, PlayerManager.maxPlayers);
					for (int i = num; i < PlayerManager.maxPlayers; i++)
					{
						__instance.ConnectPrompts[i] = __instance.ConnectPrompts[0];
					}
				}
				List<InputReceiver>[] array = (List<InputReceiver>[])GetF(__instance, "orphanedReceivers");
				if (array != null && array.Length != PlayerManager.maxPlayers)
				{
					int num2 = array.Length;
					Array.Resize(ref array, PlayerManager.maxPlayers);
					for (int j = num2; j < array.Length; j++)
					{
						array[j] = new List<InputReceiver>();
					}
					SetF(__instance, "orphanedReceivers", array);
				}
				SetF(__instance, "showingPrompts", new bool[PlayerManager.maxPlayers]);
				SetF(__instance, "orphanedCharacters", new Character.Animals[PlayerManager.maxPlayers][]);
			}
		}

		[HarmonyPatch(typeof(InputManager), "get_EnableNativeInput")]
		internal class InputManagerCtorPatch
		{
			private static void Postfix(ref bool __result)
			{
				__result = true;
			}
		}

		[HarmonyPatch(typeof(InputManager), "get_NativeInputEnableXInput")]
		internal class NativeInputEnableXInputInputManagerCtorPatch
		{
			private static void Postfix(ref bool __result)
			{
				__result = false;
			}
		}

		[HarmonyPatch(typeof(LevelPortal), "Awake")]
		internal class LevelPortalCtorPatch
		{
			private static void Prefix(LevelPortal __instance)
			{
				if (!Active)
				{
					return;
				}
				VoteArrow[] componentsInChildren = ((Component)__instance).GetComponentsInChildren<VoteArrow>();
				if (componentsInChildren == null || componentsInChildren.Length == PlayerManager.maxPlayers)
				{
					return;
				}
				int num = componentsInChildren.Length;
				for (int i = num; i < PlayerManager.maxPlayers; i++)
				{
					Type type = ((object)componentsInChildren[3]).GetType();
					Component val = ((Component)componentsInChildren[3]).gameObject.AddComponent(type);
					VoteArrow obj = (VoteArrow)(object)((val is VoteArrow) ? val : null);
					FieldInfo[] fields = type.GetFields();
					FieldInfo[] array = fields;
					foreach (FieldInfo fieldInfo in array)
					{
						fieldInfo.SetValue(obj, fieldInfo.GetValue(componentsInChildren[3]));
					}
				}
			}
		}

[HarmonyPatch(typeof(StatTracker), MethodType.Constructor)]
		internal class StatTrackerCtorPatch
		{
			private static void Postfix(StatTracker __instance)
			{
				__instance.saveFiles = (SaveFileData[])(object)new SaveFileData[PlayerManager.maxPlayers];
				__instance.saveStatuses = (StatTracker.SaveFileStatus[])(object)new StatTracker.SaveFileStatus[PlayerManager.maxPlayers];
			}
		}

		[HarmonyPatch(typeof(GameSparksQuery), "DoGetLobbyData")]
		internal class GameSparksQueryLobbyCtorPatch
		{
			private static void Prefix(ref bool reserveSlot)
			{
				reserveSlot = false;
			}
		}

		[HarmonyPatch(typeof(LivesDisplayController), "Initialize")]
		internal class LivesDisplayControllerCtorPatch
		{
			private static void Prefix(LivesDisplayController __instance)
			{
				if (Active && __instance.livesDisplayBoxes != null && __instance.livesDisplayBoxes.Count < PlayerManager.maxPlayers)
				{
					int count = __instance.livesDisplayBoxes.Count;
					for (int i = count; i <= PlayerManager.maxPlayers; i++)
					{
						__instance.livesDisplayBoxes.Add(__instance.livesDisplayBoxes[0]);
					}
				}
			}
		}

		[HarmonyPatch(typeof(PlayerStatusDisplay), "SetupSlot")]
		[HarmonyPatch(typeof(PlayerStatusDisplay), "SetSlot")]
		[HarmonyPatch(typeof(PlayerStatusDisplay), "SetSlotCount")]
		internal class PlayerStatusDisplaySetSlotCountCtorPatch
		{
			private static void Prefix(PlayerStatusDisplay __instance)
			{
				if (Active && __instance.Slots != null && __instance.Slots.Length < PlayerManager.maxPlayers)
				{
					int num = __instance.Slots.Length;
					Array.Resize(ref __instance.Slots, PlayerManager.maxPlayers);
					for (int i = num; i < PlayerManager.maxPlayers; i++)
					{
						__instance.Slots[i] = __instance.Slots[0];
					}
				}
			}
		}

		[System.Obsolete]
		[HarmonyPatch(typeof(NetworkManager), "StartServer", new Type[]
		{
			typeof(ConnectionConfig),
			typeof(int)
		})]
		internal class NetworkManagerCtorPatch
		{
			private static void Prefix(NetworkManager __instance, int maxConnections)
			{
				__instance.maxConnections = PlayerManager.maxPlayers;
			}
		}

		[HarmonyPatch(typeof(PickableNetworkButton), "SetSearchResultInfo")]
		internal class PickableNetworkButtonCtorPatch
		{
			private static void Postfix(PickableNetworkButton __instance, Matchmaker.LobbyListInfo lobbyInfo)
			{
				__instance.NumPlayersText.text = lobbyInfo.Players + "/?";
			}
		}

		[HarmonyPatch(typeof(GameControl), "Awake")]
		internal class GameControlCtorPatch
		{
			private static void Postfix(GameControl __instance)
			{
				if (Active)
				{
					bool[] array = (bool[])GetF(__instance, "showScoreButtons");
					if (array != null && array.Length < PlayerManager.maxPlayers)
					{
						Array.Resize(ref array, PlayerManager.maxPlayers);
						SetF(__instance, "showScoreButtons", array);
					}
				}
			}
		}

		[HarmonyPatch(typeof(GameControl), "ReceiveEvent")]
		internal class GameControlReceiveEventCtorPatch
		{
			private static void Prefix(GameControl __instance, InputEvent e)
			{
				//转译器把原版 ReceiveEvent 开头的 inputPlayerNumber=0 NOP 掉了；
				//非多人模式时这里补回归零，保证原版输入逻辑正常
				if (!Active)
				{
					SetF(__instance, "inputPlayerNumber", 0);
					return;
				}
				SetF(__instance, "inputPlayerNumber", 0);
				for (int i = 0; i < PlayerManager.maxPlayers && i < 95; i++)
				{
					if ((e.PlayerBitMask & (1 << i)) == 1 << i)
					{
						SetF(__instance, "inputPlayerNumber", i + 1);
						break;
					}
				}
			}
		}

		[HarmonyPatch]
		internal class GameControlReceiveEventDropInputPlayerNumber0Patch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				yield return AccessTools.Method(typeof(GameControl), "ReceiveEvent", (Type[])null, (Type[])null);
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> e)
			{
				bool done = false;
				foreach (CodeInstruction inst in e)
				{
					if (!done && inst.opcode != OpCodes.Ldarg_1)
					{
						inst.opcode = OpCodes.Nop;
					}
					else
					{
						done = true;
					}
					yield return inst;
				}
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "SetupLobbyAfterWait")]
		[HarmonyPatch(typeof(StartGameState), "Awake")]
		internal class StartGameStateCtorPatch
		{
			private static void Postfix()
			{
				if (Active && FullDebug)
				{
					LogFilter.currentLogLevel = 0;
					Debug.unityLogger.logEnabled = true;
					Debug.Log((object)"[多人联机] full debug enabled");
					Application.SetStackTraceLogType((LogType)0, (StackTraceLogType)2);
					Application.SetStackTraceLogType((LogType)1, (StackTraceLogType)2);
					Application.SetStackTraceLogType((LogType)2, (StackTraceLogType)2);
					Application.SetStackTraceLogType((LogType)3, (StackTraceLogType)2);
					Application.SetStackTraceLogType((LogType)4, (StackTraceLogType)2);
				}
			}
		}

	}
}
