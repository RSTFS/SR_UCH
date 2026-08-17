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

// ==== 分区：Menu（主菜单「更多联机」按钮 / 三按钮汉化 / 布局 / 在线指示）====

		internal class MenuPatch
		{
			//菜单补丁用独立的 Harmony ID：它们不在“重打/卸载”范围里，
			//按“网络对战”卸载补丁后按钮处理器仍然存活，再次按“多人联机”才能恢复生效。
			public static void PatchMenu()
			{
				Harmony harmony = new Harmony("SR_UCH.MorePlayers.Menu");
				//IL_0048: Unknown result type (might be due to invalid IL or missing references)
				//IL_0055: Expected O, but got Unknown
				//IL_009f: Unknown result type (might be due to invalid IL or missing references)
				//IL_00ac: Expected O, but got Unknown
				//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
				//IL_0107: Expected O, but got Unknown
				//IL_0156: Unknown result type (might be due to invalid IL or missing references)
				//IL_0164: Expected O, but got Unknown
				MethodInfo method = typeof(TabletMainMenuHome).GetMethod("Initialize");
				MethodInfo method2 = typeof(TabletMainMenuHomeCtorPatch).GetMethod("Postfix");
				if (method != null && method2 != null)
				{
					harmony.Patch((MethodBase)method, (HarmonyMethod)null, new HarmonyMethod(method2), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				MethodInfo method3 = typeof(TabletMainMenuHome).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
				MethodInfo method4 = typeof(TabletMainMenuHomeScoochButtonsCtorPatch).GetMethod("Postfix");
				if (method3 != null && method4 != null)
				{
					harmony.Patch((MethodBase)method3, (HarmonyMethod)null, new HarmonyMethod(method4), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				MethodInfo method5 = typeof(TabletButton).GetMethod("OnAccept");
				MethodInfo method6 = typeof(TabletButtonOnAcceptCtorPatch).GetMethod("Prefix");
				if (method5 != null && method6 != null)
				{
					harmony.Patch((MethodBase)method5, new HarmonyMethod(method6), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				MethodInfo method7 = typeof(TabletMainMenuOnlineIndicator).GetMethod("SetPlayOnlineButtonState", BindingFlags.Instance | BindingFlags.NonPublic);
				MethodInfo method8 = typeof(TabletMainMenuOnlineIndicatorCtorPatch).GetMethod("Prefix");
				if (method7 != null && method8 != null)
				{
					harmony.Patch((MethodBase)method7, new HarmonyMethod(method8), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
			}
		}

		internal class TabletButtonOnAcceptCtorPatch
		{
			public static void Prefix(TabletButton __instance)
			{
				try
				{
					string name = ((UnityEngine.Object)((Component)__instance).gameObject).name;
					//注意：这些字段都是 GameSettings 的实例字段，必须用实例反射（GetF/SetF）；
					//SetS 是静态读写，对实例字段会抛异常 → 版本号伪造会静默失败（联机列表不过滤）
					GameSettings gs = null;
					try
					{
						gs = GameSettings.GetInstance();
					}
					catch
					{
					}
					if (name == "Play More")
					{
						if (!Enabled)
						{
							return;
						}
						//若「模组联机」（ModMC）残留激活（先点了 Play ModMC 再点本按钮），先复位它，
						//避免其 R 码补丁/版本号继续生效（两个联机入口互斥，只保留当前一个）
						try
						{
							if (ModMC.Enabled)
							{
								ModMC.ResetToVanilla();
							}
						}
						catch
						{
						}
						_modActive = true;
						if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
						{
							SetF(gs, "versionNumber", ModVersionFull + "_" + _ogVersion);
							SetF(gs, "parsedMatchmakingNumber", ModMatchmakingNumber());
							SetF(gs, "parsedVersionNumberProd", null);
						}
						PlayerManager.maxPlayers = Mathf.Clamp(PlayerLimit, 2, 100);
						if (_harmony != null)
						{
							_harmony.UnpatchSelf();
						}
						ReapplyPatches();
					}
					else if (name == "Play Online")
					{
						_modActive = false;
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
					else if (name == "Play")
					{
						//本地游戏：不要多人联机 —— 恢复原版（版本号 + 4 人 + 卸载多人补丁）
						_modActive = false;
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
					}
				}
				catch (Exception ex)
				{
					MainPlugin.ModLogger.LogWarning((object)("[多人联机] 菜单按钮: " + ex.Message));
				}
			}
		}

		internal class TabletMainMenuHomeCtorPatch
		{
			public static void Postfix(TabletMainMenuHome __instance)
			{
				//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
				//IL_0127: Unknown result type (might be due to invalid IL or missing references)
				//IL_0166: Unknown result type (might be due to invalid IL or missing references)
				//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					if (!Enabled)
					{
						return;
					}
					GameObject val = GameObject.Find("main Buttons/Play More");
					if ((UnityEngine.Object)(object)val != (UnityEngine.Object)null)
					{
						return;
					}
					GameObject val2 = GameObject.Find("main Buttons/Play");
					if (!((UnityEngine.Object)(object)val2 == (UnityEngine.Object)null))
					{
						TabletTextLabel component = ((Component)val2.transform.Find("Text Label")).GetComponent<TabletTextLabel>();
						component.text = "本地游戏";
						CenterLabel(val2.transform.Find("Text Label"));
						val2.transform.Find("Image").localScale = new Vector3(0.7073f, 0.7073f, 1f);
						GameObject val3 = GameObject.Find("main Buttons/Play Online");
						if (!((UnityEngine.Object)(object)val3 == (UnityEngine.Object)null))
						{
							TabletTextLabel component2 = ((Component)val3.transform.Find("Text Label")).GetComponent<TabletTextLabel>();
							component2.text = "网络对战";
							CenterLabel(val3.transform.Find("Text Label"));
							val3.transform.Find("Image").localScale = new Vector3(0.8073f, 0.8073f, 1f);
							GameObject val4 = UnityEngine.Object.Instantiate<GameObject>(val3);
							((UnityEngine.Object)val4).name = "Play More";
							val4.transform.SetParent(val3.transform.parent);
							val4.transform.localScale = Vector3.one;
							TabletTextLabel component3 = ((Component)val4.transform.Find("Text Label")).GetComponent<TabletTextLabel>();
							component3.text = "更多联机";
							CenterLabel(val4.transform.Find("Text Label"));
							Transform val5 = val4.transform.Find("Image");
							val5.localScale = new Vector3(-0.5073f, 0.5073f, 1f);
							val5.position += new Vector3(-0.06f, 0.5073f, 0f);
							GameObject val6 = UnityEngine.Object.Instantiate<GameObject>(((Component)val5).gameObject, val5.parent);
							((UnityEngine.Object)val6).name = "Image1";
							val6.transform.position = val5.position - new Vector3(0.6f, 1.2f, 0f);
							val6.transform.localScale = new Vector3(-0.5073f, 0.5073f, 1f);
							GameObject val7 = UnityEngine.Object.Instantiate<GameObject>(((Component)val5).gameObject, val5.parent);
							((UnityEngine.Object)val7).name = "Image2";
							val7.transform.position = val5.position - new Vector3(-0.6f, 1.2f, 0f);
							val7.transform.localScale = new Vector3(-0.5073f, 0.5073f, 1f);
						}
					}
				}
				catch
				{
				}
			}

			private static void CenterLabel(Transform textLabel)
			{
				//IL_003a: Unknown result type (might be due to invalid IL or missing references)
				//IL_0050: Unknown result type (might be due to invalid IL or missing references)
				//IL_0066: Unknown result type (might be due to invalid IL or missing references)
				//IL_0078: Unknown result type (might be due to invalid IL or missing references)
				//IL_0082: Unknown result type (might be due to invalid IL or missing references)
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
				catch
				{
				}
			}
		}

		internal class TabletMainMenuHomeScoochButtonsCtorPatch
		{
			public static void Postfix(PickableMainMenuButton __instance)
			{
				//IL_004c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0056: Unknown result type (might be due to invalid IL or missing references)
				//IL_005b: Unknown result type (might be due to invalid IL or missing references)
				//IL_007b: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
				//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
				//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
				//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
				//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					if (Enabled)
					{
						GameObject val = GameObject.Find("main Buttons/Play More");
						if (!((UnityEngine.Object)(object)val == (UnityEngine.Object)null))
						{
							//「模组联机」存在时，四按钮一行布局由 ModMC 统一负责，这里只跳过位置设置，缩放与标签保留
							bool modmcPresent = GameObject.Find("main Buttons/Play ModMC") != null;
							GameObject val2 = GameObject.Find("main Buttons/Play");
							if (!modmcPresent)
							{
								val2.transform.localPosition = new Vector3(-320f, val2.transform.localPosition.y, val2.transform.localPosition.z);
								val.transform.localPosition = new Vector3(320f, val.transform.localPosition.y, val.transform.localPosition.z);
							}
							val2.transform.localScale = new Vector3(1.015f, 1f, 1f);
							GameObject val3 = GameObject.Find("main Buttons/Play Online");
							val3.transform.localScale = new Vector3(1.015f, 1f, 1f);
							val.transform.localScale = new Vector3(1.015f, 1f, 1f);
							if (!modmcPresent)
							{
								//每帧强制三按钮中文（TabletMainMenuHomeCtorPatch 只在 Play More 不存在时汉化，
								//Play More 已存在时直接 return，会漏掉 Play/Play Online —— 这里兜底；
								//模组联机存在时标签由 ModMC 统一负责，避免每帧写回覆盖其短名）
								((Component)val2.transform.Find("Text Label")).GetComponent<TabletTextLabel>().text = "本地游戏";
								((Component)val3.transform.Find("Text Label")).GetComponent<TabletTextLabel>().text = "网络对战";
								((Component)val.transform.Find("Text Label")).GetComponent<TabletTextLabel>().text = "更多联机";
								CenterText(val2.transform.Find("Text Label"));
								CenterText(val3.transform.Find("Text Label"));
								CenterText(val.transform.Find("Text Label"));
							}
						}
					}
				}
				catch
				{
				}
			}

			private static void CenterText(Transform textLabel)
			{
				//IL_003a: Unknown result type (might be due to invalid IL or missing references)
				//IL_0050: Unknown result type (might be due to invalid IL or missing references)
				//IL_0066: Unknown result type (might be due to invalid IL or missing references)
				//IL_0078: Unknown result type (might be due to invalid IL or missing references)
				//IL_0082: Unknown result type (might be due to invalid IL or missing references)
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
				catch
				{
				}
			}
		}

		internal class TabletMainMenuOnlineIndicatorCtorPatch
		{
			public static void Prefix(bool spinnerActive, bool buttonActive)
			{
				//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
				//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					if (!Enabled)
					{
						return;
					}
					GameObject val = GameObject.Find("main Buttons/Play More");
					if (!((UnityEngine.Object)(object)val == (UnityEngine.Object)null))
					{
						Transform val2 = val.transform.Find("LoadingSpinner");
						if ((UnityEngine.Object)(object)val2 != (UnityEngine.Object)null)
						{
							((Component)val2).gameObject.SetActive(spinnerActive);
						}
						TabletDisableGroup component = val.GetComponent<TabletDisableGroup>();
						if ((UnityEngine.Object)(object)component != (UnityEngine.Object)null && ((TabletStyledObject)component).Disabled != !buttonActive)
						{
							((TabletStyledObject)component).SetDisabled(!buttonActive);
							TabletButton component2 = val.GetComponent<TabletButton>();
							Image component3 = ((Component)val.transform.Find("Image1")).GetComponent<Image>();
							Image component4 = ((Component)val.transform.Find("Image2")).GetComponent<Image>();
							((Graphic)component3).color = ((Graphic)component2.labelImage).color;
							((Graphic)component4).color = ((Graphic)component2.labelImage).color;
						}
					}
				}
				catch
				{
				}
			}
		}

		[HarmonyPatch(typeof(TabletMainMenuHome), "Update")]
		internal class TabletMainMenuHomeUpdateCtorPatch
		{
			private static void Postfix(TabletMainMenuHome __instance)
			{
				try
				{
					//versionNumber 是实例字段：必须实例读取（GetF），GetS 静态读会失败
					GameSettings gs = GameSettings.GetInstance();
					string ver = (UnityEngine.Object)(object)gs != (UnityEngine.Object)null
						? (string)GetF(gs, "versionNumber")
						: null;
					bool flag = ver != null && !ver.StartsWith(GameState.GetLocalizationVersionNumber());
					SetF(__instance, "showingPleaseUpdate", flag);
					((Component)__instance.pleaseUpdateButton).gameObject.SetActive(flag);
				}
				catch
				{
				}
			}
		}

	}
}
