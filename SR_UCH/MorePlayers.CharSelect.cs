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

// ==== 分区：CharSelect（角色重复选择 / 多选克隆 / 装扮同步 / 选角流程）====

		internal class MultiPick
		{
			public const int multiMagicNumber = 10000;

			public static Character SpawnCharacter(Character to_clone, Vector3 position)
			{
				//IL_0002: Unknown result type (might be due to invalid IL or missing references)
				//IL_0003: Unknown result type (might be due to invalid IL or missing references)
				Character val = UnityEngine.Object.Instantiate<Character>(to_clone, position, Quaternion.identity);
				((Component)val).GetComponent<NetworkIdentity>().ForceSceneId(0);
				UnityEngine.Object.Destroy((UnityEngine.Object)(object)((Component)val).GetComponent<OGProtection>());
				val.picked = true;
				val.FindPlayerOnSpawn = true;
				((Component)val).gameObject.transform.parent = null;
				ArtMatcher[] componentsInChildren = ((Component)val).GetComponentsInChildren<ArtMatcher>();
				for (int i = 0; i < componentsInChildren.Length; i++)
				{
					if (componentsInChildren[i].outfits != null)
					{
						Outfit[] outfits = componentsInChildren[i].outfits;
						foreach (Outfit val2 in outfits)
						{
							if ((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null && (UnityEngine.Object)(object)val2.outputSpriteGameObject != (UnityEngine.Object)null)
							{
								UnityEngine.Object.Destroy((UnityEngine.Object)(object)val2.outputSpriteGameObject);
							}
						}
					}
					UnityEngine.Object.Destroy((UnityEngine.Object)(object)((Component)componentsInChildren[i]).gameObject);
				}
				NetworkServer.Spawn(((Component)val).gameObject);
				return val;
			}
		}

		internal class OGProtection : MonoBehaviour
		{
		}

		[HarmonyPatch(typeof(LevelSelectController), "RpcResetCharacter")]
		internal class LevelSelectControllerRpcResetCharacterCtorPatch
		{
			private static void Prefix(LevelSelectController __instance, GameObject characterObj)
			{
				Character component = characterObj.GetComponent<Character>();
				if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
				{
					__instance.MainCamera.RemoveTarget(component);
				}
			}

			private static void Postfix(GameObject characterObj)
			{
				NetworkServer.Destroy(characterObj);
			}
		}

		[HarmonyPatch(typeof(Character), "Awake")]
		internal class CharacterCtorPatch
		{
			private static void Prefix(Character __instance)
			{
				BoxCollider2D[] components = ((Component)__instance).gameObject.GetComponents<BoxCollider2D>();
				BoxCollider2D[] array = components;
				foreach (BoxCollider2D val in array)
				{
					((Behaviour)val).enabled = true;
				}
				__instance.RefreshScale();
			}
		}

		[HarmonyPatch(typeof(OutfitManager), "RebuildDatabase")]
		internal class OutfitManagerRebuildDatabaseTakenCtorPatch
		{
			private static bool Prefix(OutfitManager __instance)
			{
				//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
				//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
				//IL_01ba: Unknown result type (might be due to invalid IL or missing references)
				//IL_01c0: Invalid comparison between Unknown and I4
				//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
				//IL_01ca: Invalid comparison between Unknown and I4
				//IL_01ef: Unknown result type (might be due to invalid IL or missing references)
				//IL_020d: Unknown result type (might be due to invalid IL or missing references)
				//IL_024f: Unknown result type (might be due to invalid IL or missing references)
				__instance.characterOutfitsUnlocked.Clear();
				__instance.characterOutfitsAll.Clear();
				__instance.characterArtMatchers = UnityEngine.Object.FindObjectsOfType<ArtMatcher>();
				ArtMatcher[] characterArtMatchers = __instance.characterArtMatchers;
				foreach (ArtMatcher val in characterArtMatchers)
				{
					if ((UnityEngine.Object)(object)val.character != (UnityEngine.Object)null && ((UnityEngine.Object)val.character).name.Contains("Clone") && !((UnityEngine.Object)val.character).name.Contains("moep") && (UnityEngine.Object)(object)((Component)val.character).GetComponent<NetworkIdentity>() != (UnityEngine.Object)null)
					{
						((UnityEngine.Object)val.character).name = ((UnityEngine.Object)val.character).name + " moep " + ((object)((Component)val.character).GetComponent<NetworkIdentity>().netId/*cast due to constrained. prefix*/).ToString();
					}
					if (!__instance.characterOutfitsUnlocked.ContainsKey(val.character))
					{
						__instance.characterOutfitsUnlocked.Add(val.character, new List<Outfit>[Outfit.NumOutfitTypes]);
					}
					if (!__instance.characterOutfitsAll.ContainsKey(val.character))
					{
						__instance.characterOutfitsAll.Add(val.character, new List<Outfit>[Outfit.NumOutfitTypes]);
					}
					for (int j = 0; j < Outfit.NumOutfitTypes; j++)
					{
						__instance.characterOutfitsUnlocked[val.character][j] = new List<Outfit>();
						__instance.characterOutfitsAll[val.character][j] = new List<Outfit>();
					}
					if (val.outfits != null)
					{
						Outfit[] outfits = val.outfits;
						foreach (Outfit val2 in outfits)
						{
							if ((int)val2.outfitType != 4 && (int)val2.outfitType != 5)
							{
								__instance.characterOutfitsAll[val.character][(int)val2.outfitType].Add(val2);
								bool flag = val2.Unlocked;
								if (!flag && (UnityEngine.Object)(object)val.GetDefaultForcedOutfit(val2.outfitType) == (UnityEngine.Object)(object)val2)
								{
									val2.TempUnlocked = true;
									flag = true;
								}
								if (flag)
								{
									__instance.characterOutfitsUnlocked[val.character][(int)val2.outfitType].Add(val2);
								}
							}
						}
					}
					if ((UnityEngine.Object)(object)val.character != (UnityEngine.Object)null && (UnityEngine.Object)(object)val.character.AssociatedLobbyPlayer != (UnityEngine.Object)null)
					{
						val.character.SetOutfitsFromArray(val.character.AssociatedLobbyPlayer.characterOutfitsList);
					}
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(OutfitController), "Show")]
		internal class OutfitControllerupdateImagesCtorPatch
		{
			private static void Prefix(OutfitController __instance)
			{
				__instance.OutfitManager.RebuildDatabase();
			}
		}

		[HarmonyPatch(typeof(Character), "GetOutfitsAsArray")]
		internal class GetOutfitsAsArrayCtorPatch
		{
			private static void Postfix(Character __instance, ref int[] __result)
			{
				//IL_0015: Unknown result type (might be due to invalid IL or missing references)
				//IL_001a: Unknown result type (might be due to invalid IL or missing references)
				NetworkIdentity component = ((Component)__instance).GetComponent<NetworkIdentity>();
				int num;
				if (!((UnityEngine.Object)(object)component != (UnityEngine.Object)null))
				{
					num = 0;
				}
				else
				{
					NetworkInstanceId netId = component.netId;
					num = (int)netId.Value;
				}
				int num2 = num;
				if (num2 != 0)
				{
					Array.Resize(ref __result, __result.Length + 1);
					__result[__result.Length - 1] = num2;
				}
			}
		}

		[HarmonyPatch(typeof(Character), "SetOutfitsFromArray", new Type[] { typeof(SyncListInt) })]
		internal class SetOutfitsFromArraySyncListIntCtorPatch
		{
			private static bool Prefix(Character __instance, SyncListInt outfitsSyncList)
			{
				int[] array = new int[(((SyncList<int>)(object)outfitsSyncList).Count == 7) ? 7 : 6];
				for (int i = 0; i < array.Length; i++)
				{
					array[i] = ((((SyncList<int>)(object)outfitsSyncList).Count > i) ? ((SyncList<int>)(object)outfitsSyncList)[i] : (-1));
				}
				__instance.SetOutfitsFromArray(array);
				return false;
			}
		}

		[HarmonyPatch(typeof(Character), "SetOutfitsFromArray", new Type[] { typeof(int[]) })]
		internal class SetOutfitsFromArrayCtorPatch
		{
			private static bool Prefix(Character __instance, ref int[] outfitsArray)
			{
				//IL_0011: Unknown result type (might be due to invalid IL or missing references)
				if (outfitsArray.Length == 7)
				{
					GameObject val = ClientScene.FindLocalObject(new NetworkInstanceId((uint)outfitsArray[6]));
					Character val2 = (((UnityEngine.Object)(object)val != (UnityEngine.Object)null) ? val.GetComponent<Character>() : null);
					Array.Resize(ref outfitsArray, outfitsArray.Length - 1);
					if ((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null)
					{
						val2.SetOutfitsFromArray(outfitsArray);
						return false;
					}
				}
				return true;
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "IsCharacterTaken")]
		internal class LevelSelectControllerIsCharacterTakenCtorPatch
		{
			private static void Postfix(ref bool __result, LevelSelectController __instance)
			{
				//照原版：总是允许重复选角色（多人联机下别人已选的角色也可以选）
				//只受总开关/分页开关控制（原版无任何门控）
				if (!ModManager.AllEnabled || !Enabled)
				{
					return;
				}
				__result = false;
			}
		}

		[HarmonyPatch(typeof(Modifiers), "OnModifiersDynamicChange")]
		internal class ModifiersCtorPatch
		{
			private static bool Prefix(Modifiers __instance)
			{
				try
				{
					if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && (UnityEngine.Object)(object)LobbyManager.instance.CurrentLevelSelectController != (UnityEngine.Object)null)
					{
						__instance.modsApplied = __instance.modsPreview;
						LobbyManager.instance.CurrentLevelSelectController.RefreshCharacterPosition();
					}
					if ((UnityEngine.Object)(object)LobbyManager.instance != (UnityEngine.Object)null && ((UnityEngine.Object)(object)LobbyManager.instance.CurrentGameController != (UnityEngine.Object)null || (UnityEngine.Object)(object)LobbyManager.instance.CurrentLevelSelectController != (UnityEngine.Object)null))
					{
						Character[] array = UnityEngine.Object.FindObjectsOfType<Character>();
						Character[] array2 = array;
						foreach (Character val in array2)
						{
							val.RefreshScale();
						}
						Time.timeScale = __instance.GameSpeed;
					}
				}
				catch
				{
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "setupController")]
		internal class LevelSelectControllerSetupControllerCtorPatch
		{
			private static bool Prefix(LevelSelectController __instance, LobbyPlayer lobbyPl)
			{
				//IL_0053: Unknown result type (might be due to invalid IL or missing references)
				//IL_005b: Invalid comparison between Unknown and I4
				//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
				//IL_00aa: Invalid comparison between I4 and Unknown
				try
				{
					Player localPlayer = lobbyPl.LocalPlayer;
					Character.Animals[] associatedCharacters = localPlayer.UseController.GetAssociatedCharacters();
					if (!localPlayer.UseController.ControlsPlayer(localPlayer.Number))
					{
						localPlayer.UseController.AddPlayer(localPlayer.Number);
					}
					for (int num = associatedCharacters.Length - 1; num >= 0; num--)
					{
						if (associatedCharacters[num] != 0 && (int)lobbyPl.PickedAnimal == (int)associatedCharacters[num])
						{
							__instance.MainCamera.SetFrameSizes(__instance.CameraHeight);
							lobbyPl.PlayerStatus = (LobbyPlayer.Status)2;
							Character[] array = UnityEngine.Object.FindObjectsOfType<Character>();
							Character[] array2 = array;
							foreach (Character val in array2)
							{
								if ((int)associatedCharacters[num] == (int)val.CharacterSprite && !val.picked)
								{
									Cursor cursorInstance = localPlayer.AssociatedLobbyPlayer.CursorInstance;
									LobbyCursor val2 = (LobbyCursor)(object)((cursorInstance is LobbyCursor) ? cursorInstance : null);
									if ((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null)
									{
										SetF(localPlayer.AssociatedLobbyPlayer, "requestedCharacterInstance", null);
										((Cursor)val2).UseCamera = ((Component)__instance.MainCamera).GetComponent<Camera>();
										localPlayer.UseController.AddReceiver((InputReceiver)(object)val2);
									}
									localPlayer.AssociatedLobbyPlayer.RequestPickCharacter(val);
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					MainPlugin.ModLogger.LogWarning((object)("[多人联机] setupController: " + ex.Message));
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "CmdRequestPickCharacter")]
		internal class LobbyPlayerCmdRequestPickCharacterCtorPatch
		{
			private static bool Prefix(LobbyPlayer __instance, NetworkInstanceId characterInstanceId, Character.Animals animal)
			{
				//IL_0002: Unknown result type (might be due to invalid IL or missing references)
				//IL_0044: Unknown result type (might be due to invalid IL or missing references)
				//IL_0049: Unknown result type (might be due to invalid IL or missing references)
				//IL_004c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0052: Invalid comparison between Unknown and I4
				//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
				//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
				//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
				//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					GameObject val = NetworkServer.FindLocalObject(characterInstanceId);
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null && (UnityEngine.Object)(object)val.GetComponent<Character>() != (UnityEngine.Object)null)
					{
						Character component = val.GetComponent<Character>();
						//照原版：已选(picked)或初始角色(OGProtection)都克隆新角色 —— 初始角色永远保持未选中，
						//其他玩家在树屋仍能点击它重复选同一个动物（客户端 LobbyCursor 以 !Picked 拦截点击）
						if (component.picked || (UnityEngine.Object)(object)((Component)component).gameObject.GetComponent<OGProtection>() != (UnityEngine.Object)null)
						{
							Vector3 position = ((Component)component).transform.position;
							if ((int)__instance.PlayerStatus == 2 && (!Util_String.NullOrEmpty(GameState.GetInstance().currentSnapshotInfo.snapshotName) || GameState.GetInstance().lastLevelPlayed == GameState.GetLevelSceneName((GameState.LevelName)10)))
							{
								position = LobbyManager.instance.CurrentLevelSelectController.UndergroundCharacterPosition[__instance.networkNumber - 1].position;
							}
							Character val2 = MultiPick.SpawnCharacter(component, position);
							NetworkInstanceId netId = ((Component)val2).gameObject.GetComponent<NetworkIdentity>().netId;
							uint value = netId.Value;
							__instance.CallCmdAssignCharacter(value, __instance.networkNumber, __instance.localNumber, false);
							__instance.CallRpcRequestPickResponse((int)(value * 10000) + __instance.networkNumber, false);
						}
						else
						{
							__instance.CallCmdAssignCharacter(characterInstanceId.Value, __instance.networkNumber, __instance.localNumber, false);
							__instance.CallRpcRequestPickResponse(__instance.networkNumber, true);
						}
					}
					else
					{
						__instance.CallRpcRequestPickResponse(__instance.networkNumber, false);
					}
				}
				catch
				{
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "RpcRequestPickResponse")]
		internal class LobbyPlayerRpcRequestPickResponseCtorPatch
		{
			private static bool Prefix(LobbyPlayer __instance, ref int playerNetworkNumber, ref bool response)
			{
				//IL_002a: Unknown result type (might be due to invalid IL or missing references)
				if (!response && playerNetworkNumber > 10000)
				{
					int num = playerNetworkNumber / 10000;
					playerNetworkNumber %= 10000;
					GameObject val = ClientScene.FindLocalObject(new NetworkInstanceId((uint)num));
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null)
					{
						Character component = val.GetComponent<Character>();
						if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
						{
							SetF(__instance, "requestedCharacterInstance", component);
							response = true;
						}
					}
				}
				return true;
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "Start")]
		internal class LevelSelectControllerStartCtorPatch
		{
			private static void Prefix(LevelSelectController __instance)
			{
				Character[] array = UnityEngine.Object.FindObjectsOfType<Character>();
				Character[] array2 = array;
				foreach (Character val in array2)
				{
					((Component)val).gameObject.AddComponent<OGProtection>();
				}
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "SetupLobbyAfterWait")]
		internal class LevelSelectControllerSetupLobbyAfterWaitCtorPatch
		{
			private static void Prefix(LevelSelectController __instance)
			{
				Character[] array = UnityEngine.Object.FindObjectsOfType<Character>();
				int num = 0;
				Character[] array2 = array;
				foreach (Character val in array2)
				{
					if (val.Picked && !val.Sitting)
					{
						SetF(val, "frozen", false);
						__instance.MainCamera.AddTarget(val);
						num++;
						val.SetLobbyCollider(true);
					}
				}
				if (num > 0)
				{
					__instance.MainCamera.SetFrameSizes(__instance.CameraHeight);
				}
			}
		}

		[HarmonyPatch(typeof(LobbyPlayer), "DoCharacterPickedEvent")]
		internal class LobbyPlayerDoCharacterPickedEventCtorPatch
		{
			private static void Prefix(ref bool clearOutfit)
			{
				clearOutfit = false;
			}
		}

		[HarmonyPatch(typeof(LevelSelectController), "setupLobby")]
		internal class LevelSelectControllerSetupLobbyCtorPatch
		{
			private static void Postfix(LevelSelectController __instance)
			{
				//IL_005d: Unknown result type (might be due to invalid IL or missing references)
				if (Util_String.NullOrEmpty(GameState.GetInstance().currentSnapshotInfo.snapshotName) && GameState.GetInstance().lastLevelPlayed != GameState.GetLevelSceneName((GameState.LevelName)10))
				{
					return;
				}
				foreach (LobbyStartPoint startingPoint in __instance.StartingPoints)
				{
					Character componentInChildren = ((Component)startingPoint).GetComponentInChildren<Character>();
					componentInChildren.PositionCharacter(((Component)startingPoint).transform.position, true);
				}
			}
		}

		[HarmonyPatch(typeof(GraphScoreBoard), "MarkPlayerDisconnected")]
		internal class GraphScoreBoardMarkPlayerDisconnectedCtorPatch
		{
			private static bool Prefix()
			{
				//Disconnect Animal Enum based, reimplement in VersusControl.handleEvent
				return !Active;
			}
		}

		[HarmonyPatch(typeof(VersusControl), "handleEvent")]
		internal class VersusControlHandleEventCtorPatch
		{
			private static void Prefix(VersusControl __instance, GameEvent.GameEvent e)
			{
				//IL_003a: Unknown result type (might be due to invalid IL or missing references)
				//IL_0040: Expected O, but got Unknown
				if (e == null || !(((object)e).GetType() == typeof(GamePlayerRemovedEvent)))
				{
					return;
				}
				GamePlayerRemovedEvent val = (GamePlayerRemovedEvent)(object)((e is GamePlayerRemovedEvent) ? e : null);
				GraphScoreBoard val2 = (GraphScoreBoard)GetF(__instance, "graphScoreBoardInstance");
				if (!((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null))
				{
					Dictionary<int, ScoreLine> dictionary = (Dictionary<int, ScoreLine>)GetF(val2, "scorelineRelation");
					if (dictionary != null && dictionary.ContainsKey(val.PlayerNetworkNumber))
					{
						dictionary[val.PlayerNetworkNumber].SetDisconnected(true);
					}
				}
			}
		}

	}
}
