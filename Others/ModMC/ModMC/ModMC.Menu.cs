using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ModMC {
public partial class ModLobbyPlugin {

// ==== Partition: Menu (main-menu "Mod Lobby" button: clone / layout / label / click) ====

		//====================================================================
		// Main-menu button: clone "Play Online" -> "Play Mod Lobby", placed
		// as the 4th button of the row.
		//====================================================================
		internal class MenuPatch
		{
			public static void PatchMenu(Harmony menuHarmony)
			{
				MethodInfo method = typeof(TabletMainMenuHome).GetMethod("Initialize");
				MethodInfo method2 = typeof(TabletMainMenuHomeCtorPatch).GetMethod("Postfix");
				if (method != null && method2 != null)
				{
					menuHarmony.Patch((MethodBase)method, (HarmonyMethod)null, new HarmonyMethod(method2), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				MethodInfo method3 = typeof(TabletMainMenuHome).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
				MethodInfo method4 = typeof(TabletMainMenuHomeScoochButtonsCtorPatch).GetMethod("Postfix");
				if (method3 != null && method4 != null)
				{
					menuHarmony.Patch((MethodBase)method3, (HarmonyMethod)null, new HarmonyMethod(method4), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				MethodInfo method5 = typeof(TabletButton).GetMethod("OnAccept");
				MethodInfo method6 = typeof(TabletButtonOnAcceptCtorPatch).GetMethod("Prefix");
				if (method5 != null && method6 != null)
				{
					menuHarmony.Patch((MethodBase)method5, new HarmonyMethod(method6), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				//The cloned button carries its own LoadingSpinner; vanilla only
				//controls the "Play Online" spinner each frame, so mirror the
				//state onto the "Play Mod Lobby" button too (prevents spin-forever).
				MethodInfo method7 = typeof(TabletMainMenuOnlineIndicator).GetMethod("SetPlayOnlineButtonState", BindingFlags.Instance | BindingFlags.NonPublic);
				MethodInfo method8 = typeof(TabletMainMenuOnlineIndicatorCtorPatch).GetMethod("Prefix");
				if (method7 != null && method8 != null)
				{
					menuHarmony.Patch((MethodBase)method7, new HarmonyMethod(method8), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
			}
		}

		internal class TabletMainMenuHomeCtorPatch
		{
			public static void Postfix(TabletMainMenuHome __instance)
			{
				try
				{
					if (!Enabled)
					{
						return;
					}
					//Prevent duplicate clones.
					if (GameObject.Find("main Buttons/Play ModMC") != null)
					{
						return;
					}
					//Prefer cloning "Play Online" (standard look); fall back to
					//"Play More" if it was renamed or is missing.
					GameObject src = GameObject.Find("main Buttons/Play Online");
					if (src == null)
					{
						src = GameObject.Find("main Buttons/Play More");
					}
					if (src == null)
					{
						return;
					}
					GameObject mc = UnityEngine.Object.Instantiate<GameObject>(src);
					((UnityEngine.Object)mc).name = "Play ModMC";
					mc.transform.SetParent(src.transform.parent);
					mc.transform.localScale = Vector3.one;
					Transform label = mc.transform.Find("Text Label");
					if (label != null)
					{
						TabletTextLabel component = ((Component)label).GetComponent<TabletTextLabel>();
						if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
						{
							component.text = "Mod Lobby";
						}
						CenterLabel(label);
					}
					Transform img = mc.transform.Find("Image");
					if (img != null)
					{
						img.localScale = new Vector3(0.8073f, 0.8073f, 1f);
					}
				}
				catch { }
			}

			private static void CenterLabel(Transform textLabel)
			{
				try
				{
					Text component = ((Component)textLabel).GetComponent<Text>();
					if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
					{
						component.alignment = (TextAnchor)4;
					}
					RectTransform val = (RectTransform)(object)((textLabel is RectTransform) ? textLabel : null);
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null)
					{
						val.anchorMin = new Vector2(0.5f, 0f);
						val.anchorMax = new Vector2(0.5f, 1f);
						val.pivot = new Vector2(0.5f, 0.5f);
						val.anchoredPosition = new Vector2(0f, val.anchoredPosition.y);
					}
				}
				catch { }
			}
		}

		//Per-frame fallback: one row of buttons + labels.
		//Scenario A ("Play More" exists, e.g. Even More Players installed):
		//  Local / Online / More / Mod  (4 buttons in one row)
		//Scenario B (no "Play More"): Local Game / Online Play / Mod Lobby
		//  (3 buttons in one row, Mod Lobby takes the More slot).
		//[HarmonyPriority(Priority.Low)] makes this postfix run AFTER Even More
		//Players' own per-frame layout postfix (default priority), so the final
		//row is always ours and the button order stays correct with EMP installed.
		internal class TabletMainMenuHomeScoochButtonsCtorPatch
		{
			[HarmonyPriority(Priority.Low)]
			public static void Postfix(PickableMainMenuButton __instance)
			{
				try
				{
					if (!Enabled)
					{
						return;
					}
					GameObject mc = GameObject.Find("main Buttons/Play ModMC");
					if (mc == null)
					{
						return;
					}
					GameObject play = GameObject.Find("main Buttons/Play");
					GameObject online = GameObject.Find("main Buttons/Play Online");
					GameObject more = GameObject.Find("main Buttons/Play More");
					if (play == null || online == null)
					{
						return;
					}
					//Same row: anchored on "Play" y, keep z.
					Vector3 basePos = play.transform.localPosition;
					bool hasMore = more != null;
					//Spacing based on the actual "Play" button width (small gap).
					RectTransform rt = play.transform as RectTransform;
					float w = 300f;
					if (rt != null && rt.rect.width > 1f)
					{
						w = rt.rect.width;
					}
					float step = w + 10f;
					if (hasMore)
					{
						//Scenario A: 4 buttons, tightly packed.
						play.transform.localPosition = new Vector3(-1.5f * step, basePos.y, basePos.z);
						online.transform.localPosition = new Vector3(-0.5f * step, basePos.y, basePos.z);
						more.transform.localPosition = new Vector3(0.5f * step, basePos.y, basePos.z);
						mc.transform.localPosition = new Vector3(1.5f * step, basePos.y, basePos.z);
					}
					else
					{
						//Scenario B: 3 buttons, Mod Lobby takes the More slot.
						play.transform.localPosition = new Vector3(-step, basePos.y, basePos.z);
						online.transform.localPosition = new Vector3(0f, basePos.y, basePos.z);
						mc.transform.localPosition = new Vector3(step, basePos.y, basePos.z);
					}
					mc.transform.localScale = new Vector3(1.015f, 1f, 1f);
					//Label fallback: scenario A short names, scenario B full names.
					if (hasMore)
					{
						SetLabel(play, "Local");
						SetLabel(online, "Online");
						SetLabel(more, "More");
						SetLabel(mc, "Mod");
					}
					else
					{
						SetLabel(play, "Local Game");
						SetLabel(online, "Online Play");
						SetLabel(mc, "Mod Lobby");
					}
				}
				catch { }
			}

			private static void SetLabel(GameObject btn, string text)
			{
				try
				{
					Transform label = btn.transform.Find("Text Label");
					if (label == null)
					{
						return;
					}
					TabletTextLabel component = ((Component)label).GetComponent<TabletTextLabel>();
					if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
					{
						component.text = text;
					}
					CenterText(label);
				}
				catch { }
			}

			private static void CenterText(Transform textLabel)
			{
				try
				{
					Text component = ((Component)textLabel).GetComponent<Text>();
					if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null)
					{
						component.alignment = (TextAnchor)4;
					}
					RectTransform val = (RectTransform)(object)((textLabel is RectTransform) ? textLabel : null);
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null)
					{
						val.anchorMin = new Vector2(0.5f, 0f);
						val.anchorMax = new Vector2(0.5f, 1f);
						val.pivot = new Vector2(0.5f, 0.5f);
						val.anchoredPosition = new Vector2(0f, val.anchoredPosition.y);
					}
				}
				catch { }
			}
		}

		//Mirror the LoadingSpinner / disabled state onto "Play Mod Lobby"
		//(vanilla only controls "Play Online" each frame).
		internal class TabletMainMenuOnlineIndicatorCtorPatch
		{
			public static void Prefix(bool spinnerActive, bool buttonActive)
			{
				try
				{
					if (!Enabled)
					{
						return;
					}
					GameObject mc = GameObject.Find("main Buttons/Play ModMC");
					if ((UnityEngine.Object)(object)mc == (UnityEngine.Object)null)
					{
						return;
					}
					Transform spinner = mc.transform.Find("LoadingSpinner");
					if ((UnityEngine.Object)(object)spinner != (UnityEngine.Object)null && ((Component)spinner).gameObject.activeSelf != spinnerActive)
					{
						((Component)spinner).gameObject.SetActive(spinnerActive);
					}
					TabletDisableGroup component = mc.GetComponent<TabletDisableGroup>();
					if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null && ((TabletStyledObject)component).Disabled != !buttonActive)
					{
						((TabletStyledObject)component).SetDisabled(!buttonActive);
					}
				}
				catch { }
			}
		}

		//Click handling: Play ModMC = activate the mod lobby (write the
		//usingMods version, keep 4 players); Play / Play Online / Play More =
		//if the mod lobby was active, reset back to vanilla first.
		//[HarmonyPriority(Priority.High)] makes this prefix run BEFORE Even More
		//Players' OnAccept prefix (default priority), so when the user clicks
		//"Play More" our state resets first and EMP's activation then takes over
		//cleanly (the two online entrances are mutually exclusive). For "Play
		//Online" both resets are idempotent, so the order does not matter.
		internal class TabletButtonOnAcceptCtorPatch
		{
			[HarmonyPriority(Priority.High)]
			public static void Prefix(TabletButton __instance)
			{
				try
				{
					string name = ((UnityEngine.Object)((Component)__instance).gameObject).name;
					//GameSettings instance fields must be accessed via instance reflection.
					GameSettings gs = null;
					try
					{
						gs = GameSettings.GetInstance();
					}
					catch { }
					if (name == "Play ModMC")
					{
						if (!Enabled)
						{
							return;
						}
						//If "More Online" (EMP / SR_UCH) is still active, take over:
						//our version/maxPlayers writes override its state.
						_modActive = true;
						_autoActive = false;
						if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
						{
							SetF(gs, "versionNumber", ModVersionFull + "_" + _ogVersion);
							SetF(gs, "parsedMatchmakingNumber", ModMatchmakingNumber());
							SetF(gs, "parsedVersionNumberProd", null);
						}
						//Mod lobby keeps the vanilla 4-player cap.
						PlayerManager.maxPlayers = 4;
					}
					else if (name == "Play" || name == "Play Online" || name == "Play More")
					{
						if (_modActive || _autoActive)
						{
							ResetToVanilla();
						}
					}
				}
				catch (Exception ex)
				{
					ModLogger.LogWarning("[Mod Lobby] menu button: " + ex.Message);
				}
			}
		}

	}
}
