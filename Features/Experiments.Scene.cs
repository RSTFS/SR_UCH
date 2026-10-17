// 实验 · 场景页：加载遮罩、温柔加载、树屋立即开始。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		// 场景页：树屋 / 加载与切场景
		public static void RenderPage3() {
			try {
				// 树屋：立即开始
			GUILayout.Space(SRApi.Ctl.Sc(6));
			GUILayout.Label(new GUIContent("- " + SRApi.T("树屋", "Treehouse") + " -",
				SRApi.T("在树屋大厅里直接开始关卡.",
					   "Start the level immediately from the treehouse.")),
				SRApi.Ctl.SecHeader);
			SR.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
				return e.Definition.Key.StartsWith("Lobby ");
			});
				if (LobbyTools.Note != null && LobbyTools.Note.Length > 0) {
					GUILayout.Space(SRApi.Ctl.Sc(2));
					DrawStatus(LobbyTools.Note);
				}

				// 加载与切场景
				GUILayout.Space(SRApi.Ctl.Sc(6));
				GUILayout.Label(new GUIContent("- " + SRApi.T("加载与切场景", "Loading and scene switch") + " -",
					SRApi.T("跳过加载动画. 温柔加载在切换场景时先卸载再回收.",
						 "Skip the loading animation. Gentle load unloads and collects before switching.")),
					SRApi.Ctl.SecHeader);
				SR.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
					return e.Definition.Key.StartsWith("Splash ") || e.Definition.Key == "Gentle Load";
				});
				if (SplashTools.Note != null && SplashTools.Note.Length > 0) {
					GUILayout.Space(SRApi.Ctl.Sc(2));
					DrawStatus(SplashTools.Note);
				}
				GUILayout.Space(SRApi.Ctl.Sc(6));
			} catch (Exception __ex) { SRApi.Guard.Log("场景页 渲染", __ex); }
		}

		
}


    // 加载遮罩 + 温柔加载。SRHost 为协程挂载点（SR 核心不是 MonoBehaviour）。
    internal sealed class SRHost : MonoBehaviour { }

    [SrFeature("sr.splashtools", "SplashTools", "1.0")]

    public class SplashTools : ITweak {
        private static ConfigEntry<bool> _skipLoad;
        private static ConfigEntry<bool> _gentleLoad;


        internal static string Note = "";

        private static void SelfReg() {
            // Scene 段条目全部渲染在场景页，必须 NavHide，否则兜底分支会再加一个独立侧栏栏目。
            SRApi.NavHide("Scene");
            SRApi.LocKey("Scene", "Splash Skip Load", "跳过加载动画", "Skip load animation");
            SRApi.LocDesc("Scene", "Splash Skip Load",
                "加载画面一出现就跳掉, 不用干等.",
                "Skip the loading splash immediately.");
            SRApi.LocKey("Scene", "Gentle Load", "温柔加载", "Gentle load");
            SRApi.LocDesc("Scene", "Gentle Load",
                "切场景时先卸载再回收垃圾, 长时间玩会顺很多. 切到主菜单时自动让路, 那边游戏自己要走另一套.",
                "Use the game's own DoGentleSceneLoad for scene switches (unload + GC before loading).");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _skipLoad = plugin.Config.Bind("Scene", "Splash Skip Load", false, "加载画面一出现就立刻跳过。");
                _gentleLoad = plugin.Config.Bind("Scene", "Gentle Load", false,
                    "切场景时走游戏自己的 DoGentleSceneLoad（卸载+GC，能减轻卡顿）。");
                SRApi.RegisterTick("加载遮罩", Tick);
                SRApi.RegisterSceneHook(Reset);
                try { Harmony.CreateAndPatchAll(typeof(SplashPatch)); }
                catch (Exception __ex) { SRApi.LogWarn("加载遮罩 补丁注册失败: " + __ex.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("加载遮罩 初始化", __ex); }
        }

        private static void Reset() {
            Note = "";
        }

        private static void Tick() {
            try {
                SplashPatch.Tick();
                // 门控不得挡住 ApplySkip：总开关关闭时也须走到 ClearSkip 分支清掉游戏标志位。
                ApplySkip();
            } catch (Exception __ex) { SRApi.Guard.Log("加载遮罩 Tick", __ex); }
        }

        // 游戏 Skip() 只把 SkipBool 置真且无取消路径，一旦开过就永远跳。
        // 故关闭时反射把 SkipBool 写回 false，恢复正常的按时间推进。
        private static System.Reflection.FieldInfo _fiSkip;
        private static bool _fiSkipTried;
        private static void ApplySkip() {
            try {
                // 总开关参与判定；为 false 时走 ClearSkip 把游戏标志清回去。
                bool want = SRApi.GateMaster && _skipLoad != null && _skipLoad.Value;
                LoadingInterstitialSplash sp = FindSplash();
                if (sp == null) return;
                if (want) {
                    if (sp.State == UISplashScreen.STATE.FADING_IN || sp.State == UISplashScreen.STATE.SHOW
                        || sp.State == UISplashScreen.STATE.FADING_OUT)
                        sp.Skip();
                } else {
                    ClearSkip(sp);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("跳过加载动画", __ex); }
        }

        // 清掉 SkipBool（protected 字段，无公开的关闭接口）。
        private static void ClearSkip(UISplashScreen sp) {
            try {
                if (!_fiSkipTried) {
                    _fiSkipTried = true;
                    _fiSkip = SRApi.Ref.Field(typeof(UISplashScreen), "SkipBool", "场景：关闭跳过加载动画需清此标志");
                }
                if (_fiSkip == null) return;
                object v = _fiSkip.GetValue(sp);
                if (v is bool && (bool)v) _fiSkip.SetValue(sp, false);
            } catch (Exception __ex) { SRApi.Guard.Log("清 SkipBool", __ex); }
        }

        // Instance 为空时才回退全场景扫描，回退路径 15 帧节流。
        private static int _splashScanAt = -9999;
        private static LoadingInterstitialSplash FindSplash() {
            try { LoadingInterstitialSplash inst = LoadingInterstitialSplash.Instance; if (inst != null) return inst; } catch { }
            if (Time.frameCount - _splashScanAt < 15) return null;
            _splashScanAt = Time.frameCount;
            try { return UnityEngine.Object.FindObjectOfType<LoadingInterstitialSplash>(); } catch { return null; }
        }

        internal static bool GentleOn() { return _gentleLoad != null && _gentleLoad.Value && SRApi.GateMaster; }
    }

    // 温柔加载：前置 SceneManagerWrapper 的两个入口，改走游戏自己的 DoGentleSceneLoad。
    internal static class SplashPatch {
        private static SRHost _host;
        private static float _runningUntil = -1f;
        private static string _target = "";

        private static void EnsureHost() {
            if (_host != null) return;
            GameObject go = new GameObject("SR_GentleLoadHost");
            _host = go.AddComponent<SRHost>();
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        // Prefix 返回 false = 抑制游戏自己的加载，抑制后必须保证接管失败时能回落，否则场景请求被吞掉。
        // 约定：Start 返回 true = 已接管；false = 未接管，Prefix 放行原版。
        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadScene")]
        [HarmonyPrefix]
        static bool PreLoadScene(string sceneName) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        if (!Should(sceneName)) return true;
                        return !Start(sceneName);
            } catch (Exception __ex) { SRApi.Guard.Log("温和加载(同步)", __ex); return true; }
        }

        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadSceneAsync")]
        [HarmonyPrefix]
        static bool PreLoadSceneAsync(string sceneName) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        if (!Should(sceneName)) return true;
                        return !Start(sceneName);
            } catch (Exception __ex) { SRApi.Guard.Log("温和加载(异步)", __ex); return true; }
        }

        private static bool Should(string sceneName) {
            try {
                if (!SplashTools.GentleOn()) return false;
                if (string.IsNullOrEmpty(sceneName)) return false;
                if (sceneName == "MainMenu") return false;   // MainMenu 走 AbortGameInProgress 特例，必须让路
                return true;
            } catch { return false; }
        }

        // 返回 true = 已接管（Prefix 可抑制原版）；false = 未接管（Prefix 必须放行）。
        private static bool Start(string sceneName) {
            try {
                EnsureHost();
                if (_host == null) {
                    SRApi.LogWarn("[场景] 温柔加载宿主不可用，本次请求回落原版（" + sceneName + "）");
                    return false;
                }
                if (_runningUntil > 0f && Time.realtimeSinceStartup < _runningUntil) {
                    // 不能静默返回：Prefix 已决定抑制原版，放弃即请求被吞掉。
                    SRApi.LogWarn("[场景] 温柔加载已在进行中，本次请求回落原版（" + sceneName + "）");
                    return false;
                }
                _target = sceneName;
                _host.StartCoroutine(SceneManagerWrapper.DoGentleSceneLoad(sceneName));
                // 协程无 isDone：场景已切换到目标即视为完成，5 秒后自动解锁。
                _runningUntil = Time.realtimeSinceStartup + 5f;
                SRApi.LogInfo("[场景] 温柔加载：" + sceneName);
                return true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("温柔加载", __ex);
                return false;   // 异常也回落，不吞掉场景请求
            }
        }

        internal static void Tick() {
            try {
                if (_runningUntil > 0f) {
                    if (Time.realtimeSinceStartup >= _runningUntil) _runningUntil = -1f;
                    else if (SR.Env.SceneName == _target) _runningUntil = -1f;
                }
            } catch { }
        }
    }
    // 树屋：立即开始。
    [SrFeature("sr.lobbytools", "LobbyTools", "1.0")]
    public class LobbyTools : ITweak {
        private static ConfigEntry<KeyCode> _launchKey;

        internal static string Note = "";

        private static void SelfReg() {
            SRApi.LocKey("Scene", "Lobby Launch Key", "立即开始键", "Launch now key");
            SRApi.LocDesc("Scene", "Lobby Launch Key",
                "按这个键跳过等待直接开始关卡, 自动选票最多的那扇门. 要房主才能用.",
                "Press to start the level immediately (host only).");

        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _launchKey = plugin.Config.Bind("Scene", "Lobby Launch Key", KeyCode.None, "按此键立即开始（None=不绑，需房主）。");
                SRApi.RegisterTick("树屋工具", Tick);
                SRApi.RegisterSceneHook(Reset);
            } catch (Exception __ex) { SRApi.Guard.Log("树屋工具 初始化", __ex); }
        }

        private static void Reset() { Note = ""; }

        private static LevelSelectController Lsc() {
            try { return LobbyManager.instance.CurrentLevelSelectController; } catch { return null; }
        }

        private static void Tick() {
            try {
                if (!SRApi.GateMaster) return;
                if (!SR.Env.InTreehouse) return;
                LevelSelectController lsc = Lsc();
                if (lsc == null) return;
                if (_launchKey != null && _launchKey.Value != KeyCode.None && SR.ComboKeyDown(_launchKey)) {
                    try { DoLaunch(lsc); } catch (Exception __ex) { SRApi.Guard.Log("立即开始", __ex); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("树屋工具 Tick", __ex); }
        }

        private static void DoLaunch(LevelSelectController lsc) {
            // 判 SRApi.CanControlLocally（离线时为真）；LaunchLevel 是纯本地调用，与广播能力无关。
            if (!SRApi.CanControlLocally) { SRApi.Notify(SRApi.T("仅房主可开始关卡", "Host only")); return; }
            LevelPortal[] portals = lsc.portals;
            if (portals == null || portals.Length == 0) { SRApi.Notify(SRApi.T("未找到可用关卡门", "No level portal available")); return; }
            int best = -1, bestV = -1;
            for (int i = 0; i < portals.Length; i++) {
                if (portals[i] == null) continue;
                int v = portals[i].Votes != null ? portals[i].Votes.Count : 0;
                if (v > bestV) { bestV = v; best = i; }
            }
            // 零票视为未选定关卡，不启动。
            if (best < 0 || bestV <= 0 || portals[best] == null) {
                SRApi.Notify(SRApi.T("尚未选定关卡，请先投票", "No level selected yet - vote first"));
                return;
            }
            lsc.LaunchLevel(portals[best]);
            SRApi.Notify(SRApi.T("关卡已开始", "Level started"));
        }










    }
}
