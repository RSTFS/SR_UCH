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

// ==== 分区：Menu（主菜单「模组联机」按钮：克隆 / 布局 / 标签 / 点击处理）====

		//====================================================================
		// 主菜单按钮：克隆「网络对战」→「模组联机」，放在「更多联机」正下方
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
				//克隆按钮自带 LoadingSpinner：原版每帧只控制「网络对战」自己的 spinner，
				//这里同步「模组联机」按钮的 spinner 与禁用状态（防止一直转圈）
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
					//防重复克隆
					if (GameObject.Find("main Buttons/Play ModMC") != null)
					{
						return;
					}
					//优先克隆「网络对战」（标准样式、无装饰），若被改名/缺失再找「更多联机」
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
							component.text = "模组联机";
						}
						CenterLabel(label);
					}
					Transform img = mc.transform.Find("Image");
					if (img != null)
					{
						img.localScale = new Vector3(0.8073f, 0.8073f, 1f);
					}
				}
				catch
				{
				}
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
				catch
				{
				}
			}
		}

		//每帧兜底：主菜单按钮统一一行布局 + 强制中文标签
		//场景A（更多联机存在）：本地游戏 / 网络对战 / 更多联机 / 模组联机 四按钮一行
		//场景B（更多联机不存在）：本地游戏 / 网络对战 / 模组联机 三按钮一行（模组联机顶替更多联机位置）
		internal class TabletMainMenuHomeScoochButtonsCtorPatch
		{
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
					//同一行：以「本地游戏」的 y 为基准，保持 z
					Vector3 basePos = play.transform.localPosition;
					bool hasMore = more != null;
					//以「本地游戏」按钮的实际宽度为基准计算间距（小间隙），让按钮挨在一起
					RectTransform rt = play.transform as RectTransform;
					float w = 300f;
					if (rt != null && rt.rect.width > 1f)
					{
						w = rt.rect.width;
					}
					float step = w + 10f;
					if (hasMore)
					{
						//场景A：四按钮一行，按按钮宽度紧贴排列
						play.transform.localPosition = new Vector3(-1.5f * step, basePos.y, basePos.z);
						online.transform.localPosition = new Vector3(-0.5f * step, basePos.y, basePos.z);
						more.transform.localPosition = new Vector3(0.5f * step, basePos.y, basePos.z);
						mc.transform.localPosition = new Vector3(1.5f * step, basePos.y, basePos.z);
					}
					else
					{
						//场景B：三按钮一行，模组联机顶替更多联机的位置
						play.transform.localPosition = new Vector3(-step, basePos.y, basePos.z);
						online.transform.localPosition = new Vector3(0f, basePos.y, basePos.z);
						mc.transform.localPosition = new Vector3(step, basePos.y, basePos.z);
					}
					mc.transform.localScale = new Vector3(1.015f, 1f, 1f);
					//标签兜底：场景A 四按钮短名（本地/网络/多人/模组）；场景B 三按钮全名
					if (hasMore)
					{
						SetLabel(play, "本地");
						SetLabel(online, "网络");
						SetLabel(more, "多人");
						SetLabel(mc, "模组");
					}
					else
					{
						SetLabel(play, "本地游戏");
						SetLabel(online, "网络对战");
						SetLabel(mc, "模组联机");
					}
				}
				catch
				{
				}
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
				catch
				{
				}
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
				catch
				{
				}
			}
		}

		//同步「模组联机」按钮的 LoadingSpinner 与禁用状态（原版每帧只控制「网络对战」自己的）
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
				catch
				{
				}
			}
		}

		//点击处理：Play ModMC = 激活模组联机（写 usingMods 版本号，保持 4 人）；
		//Play / Play Online = 若激活过则恢复原版版本号
		internal class TabletButtonOnAcceptCtorPatch
		{
			public static void Prefix(TabletButton __instance)
			{
				try
				{
					string name = ((UnityEngine.Object)((Component)__instance).gameObject).name;
					//GameSettings 实例字段必须用实例反射（GetF/SetF）
					GameSettings gs = null;
					try
					{
						gs = GameSettings.GetInstance();
					}
					catch
					{
					}
					if (name == "Play ModMC")
					{
						if (!Enabled)
						{
							return;
						}
						//若「更多联机」残留激活（先点了 Play More 再点本按钮），先复位它，
						//避免其补丁把 4 人上限撑大 / 邀请码显示成 M 码
						try
						{
							if (MorePlayers.Enabled)
							{
								MorePlayers.ResetToVanilla();
							}
						}
						catch
						{
						}
						_modActive = true;
						_autoActive = false;
						if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
						{
							SetF(gs, "versionNumber", ModVersionFull + "_" + _ogVersion);
							SetF(gs, "parsedMatchmakingNumber", ModMatchmakingNumber());
							SetF(gs, "parsedVersionNumberProd", null);
						}
						//模组联机保持原生 4 人
						PlayerManager.maxPlayers = 4;
					}
					else if (name == "Play" || name == "Play Online")
					{
						if (_modActive || _autoActive)
						{
							_modActive = false;
							_autoActive = false;
							if ((UnityEngine.Object)(object)gs != (UnityEngine.Object)null)
							{
								SetF(gs, "versionNumber", _ogVersion);
								SetF(gs, "parsedMatchmakingNumber", null);
								SetF(gs, "parsedVersionNumberProd", null);
							}
							PlayerManager.maxPlayers = 4;
						}
					}
				}
				catch (Exception ex)
				{
					MainPlugin.ModLogger.LogWarning((object)("[模组联机] 菜单按钮: " + ex.Message));
				}
			}
		}

	}
}
