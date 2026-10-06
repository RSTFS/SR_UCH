// 实验 · 场景页。加载遮罩、温柔加载、树屋立即开始。
// 整个实验栏目的总览看同目录的 Experiments.cs。

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

		// **场景页**：关卡背景 / 树屋 / 加载与切场景
		public static void RenderPage3() {
			try {
				// 关卡背景
				GUILayout.Label(new GUIContent("- " + SR.T("关卡背景", "Level background") + " -",
					SR.T("给关卡换一个背景, 原版关卡的对局里也生效. 选无表示不换.",
						 "Give the level a background; works in official matches too. Pick 'None' to disable.")),
					SR.Ctl.SecHeader);
				SR.RenderSectionEntries("Level", delegate (ConfigEntryBase e) {
					return e.Definition.Key == "Level Background";
				});
				if (LevelBackground.Note != null && LevelBackground.Note.Length > 0) {
					GUILayout.Space(SR.Ctl.Sc(2));
					DrawStatus(LevelBackground.Note);
				}

			// 树屋：立即开始（**R413**：自动投票已按用户要求整块删除）
			GUILayout.Space(SR.Ctl.Sc(6));
			GUILayout.Label(new GUIContent("- " + SR.T("树屋", "Treehouse") + " -",
				SR.T("在树屋大厅里直接开始关卡.",
					   "Start the level immediately from the treehouse.")),
				SR.Ctl.SecHeader);
			SR.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
				return e.Definition.Key.StartsWith("Lobby ");
			});
				if (LobbyTools.Note != null && LobbyTools.Note.Length > 0) {
					GUILayout.Space(SR.Ctl.Sc(2));
					DrawStatus(LobbyTools.Note);
				}

				// 加载与切场景
				GUILayout.Space(SR.Ctl.Sc(6));
				GUILayout.Label(new GUIContent("- " + SR.T("加载与切场景", "Loading and scene switch") + " -",
					SR.T("跳过加载动画. 温柔加载在切换场景时先卸载再回收.",
						 "Skip the loading animation. Gentle load unloads and collects before switching.")),
					SR.Ctl.SecHeader);
				SR.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
					return e.Definition.Key.StartsWith("Splash ") || e.Definition.Key == "Gentle Load";
				});
				if (SplashTools.Note != null && SplashTools.Note.Length > 0) {
					GUILayout.Space(SR.Ctl.Sc(2));
					DrawStatus(SplashTools.Note);
				}
				GUILayout.Space(SR.Ctl.Sc(6));
			} catch (Exception __ex) { SR.Guard.Log("场景页 渲染", __ex); }
		}

		
}


    // 一、加载遮罩（2.3）+ 温柔加载（2.4-#25）
    // Coroutine 宿主（MonoBehaviour）：SR 核心不是 MonoBehaviour，起协程需要一个挂载点
    internal sealed class SRHost : MonoBehaviour { }

    public class SplashTools : ITweak {
        private static ConfigEntry<bool> _skipLoad;
        private static ConfigEntry<bool> _gentleLoad;


        internal static string Note = "";

        private static void SelfReg() {
            // Scene段的条目全部渲染在场景页（Experiments.RenderPage3），
            //  所以这里必须 NavHide, 否则 RebuildSections 的"兜底"分支（既没 Nav 也没 NavHide
            //  的 cfg 段自动进侧栏，SR.Core.cs:940-947）会把它加成一个独立侧栏栏目，
            //  出现场景页里有一份、侧栏又有一份的双入口。
            SR.NavHide("Scene");
            SR.LocKey("Scene", "Splash Skip Load", "跳过加载动画", "Skip load animation");
            SR.LocDesc("Scene", "Splash Skip Load",
                "加载画面一出现就跳掉, 不用干等.",
                "Skip the loading splash immediately.");
            SR.LocKey("Scene", "Gentle Load", "温柔加载", "Gentle load");
            SR.LocDesc("Scene", "Gentle Load",
                "切场景时先卸载再回收垃圾, 长时间玩会顺很多. 切到主菜单时自动让路, 那边游戏自己要走另一套.",
                "Use the game's own DoGentleSceneLoad for scene switches (unload + GC before loading).");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _skipLoad = plugin.Config.Bind("Scene", "Splash Skip Load", false, "加载画面一出现就立刻跳过。");
                _gentleLoad = plugin.Config.Bind("Scene", "Gentle Load", false,
                    "切场景时走游戏自己的 DoGentleSceneLoad（卸载+GC，能减轻卡顿）。");
                SR.RegisterTick("加载遮罩", Tick);
                SR.RegisterSceneHook(Reset);
                try { Harmony.CreateAndPatchAll(typeof(SplashPatch)); }
                catch (Exception __ex) { SR.LogWarn("加载遮罩 补丁注册失败: " + __ex.Message); }
            } catch (Exception __ex) { SR.Guard.Log("加载遮罩 初始化", __ex); }
        }

        private static void Reset() {
            Note = "";
        }

        private static void Tick() {
            try {
                SplashPatch.Tick();
                if (!SR.GateMaster) return;
                ApplySkip();
            } catch (Exception __ex) { SR.Guard.Log("加载遮罩 Tick", __ex); }
        }

        // **问题 3跳过加载动画不能随开随关的修法**：
        //  游戏的 Skip()(116972) 只设 SkipBool = true，没有任何"取消"路径,
        //  SkipBool 只在 Start()(116867) 里被清一次，而 Start 只跑一次。
        //  -> 一旦开过就**永远跳**，把开关关掉也回不来。
        //  现在：开 -> 调 Skip()；关 -> **自己把 SkipBool 写回 false**（反射 protected 字段），
        //  恢复成正常的按时间推进。
        private static System.Reflection.FieldInfo _fiSkip;
        private static bool _fiSkipTried;
        private static void ApplySkip() {
            try {
                bool want = _skipLoad != null && _skipLoad.Value;
                LoadingInterstitialSplash sp = FindSplash();
                if (sp == null) return;
                if (want) {
                    if (sp.State == UISplashScreen.STATE.FADING_IN || sp.State == UISplashScreen.STATE.SHOW
                        || sp.State == UISplashScreen.STATE.FADING_OUT)
                        sp.Skip();
                } else {
                    ClearSkip(sp);
                }
            } catch (Exception __ex) { SR.Guard.Log("跳过加载动画", __ex); }
        }

        //清掉 SkipBool（protected 字段，没有公开的关闭接口）
        private static void ClearSkip(UISplashScreen sp) {
            try {
                if (!_fiSkipTried) {
                    _fiSkipTried = true;
                    _fiSkip = SR.RefField(typeof(UISplashScreen), "SkipBool", "场景：关闭跳过加载动画需清此标志");
                }
                if (_fiSkip == null) return;
                object v = _fiSkip.GetValue(sp);
                if (v is bool && (bool)v) _fiSkip.SetValue(sp, false);
            } catch (Exception __ex) { SR.Guard.Log("清 SkipBool", __ex); }
        }

        // **P2-24**：Instance 是静态属性，读它几乎零开销；只有它为空时才回退全场景扫描。
        //  而那个回退原来**每帧都可能跑**（正在加载的那段时间 Instance 恰好是空的），
        //  这里加 15 帧节流 —— 找到实例最迟也就晚 1/4 秒，不影响观感。
        private static int _splashScanAt = -9999;
        private static LoadingInterstitialSplash FindSplash() {
            try { LoadingInterstitialSplash inst = LoadingInterstitialSplash.Instance; if (inst != null) return inst; } catch { }
            if (Time.frameCount - _splashScanAt < 15) return null;
            _splashScanAt = Time.frameCount;
            try { return UnityEngine.Object.FindObjectOfType<LoadingInterstitialSplash>(); } catch { return null; }
        }

        internal static bool GentleOn() { return _gentleLoad != null && _gentleLoad.Value && SR.GateMaster; }
    }

    // **温柔加载**：前置 SceneManagerWrapper 的两个入口，改走游戏自己的 DoGentleSceneLoad
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

        // R396 A2：Prefix 返回 false = 抑制游戏自己的加载。**抑制之后必须保证"我起不来"时能回落**，
        //  否则场景请求被吞掉、游戏永远停在当前场景（R395 查出的阻断级问题）。
        //  回落约定：Start 返回 true = 温柔加载已接管；false = 没能接管 -> Prefix 放行原版。
        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadScene")]
        [HarmonyPrefix]
        static bool PreLoadScene(string sceneName) {
            if (!Should(sceneName)) return true;
            return !Start(sceneName);
        }

        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadSceneAsync")]
        [HarmonyPrefix]
        static bool PreLoadSceneAsync(string sceneName) {
            if (!Should(sceneName)) return true;
            return !Start(sceneName);
        }

        private static bool Should(string sceneName) {
            try {
                if (!SplashTools.GentleOn()) return false;
                if (string.IsNullOrEmpty(sceneName)) return false;
                if (sceneName == "MainMenu") return false;   //游戏走 AbortGameInProgress 特例，必须让路
                return true;
            } catch { return false; }
        }

        // 返回 true = 温柔加载已接管（Prefix 可以抑制原版）；false = 没能接管（Prefix 必须放行）
        private static bool Start(string sceneName) {
            try {
                EnsureHost();
                if (_host == null) {
                    SR.LogWarn("[场景] 温柔加载宿主不可用，本次请求回落原版（" + sceneName + "）");
                    return false;
                }
                if (_runningUntil > 0f && Time.realtimeSinceStartup < _runningUntil) {
                    // **不能静默 return**：Prefix 已经决定抑制原版了，这里放弃 = 请求被吞掉 = 游戏卡死
                    SR.LogWarn("[场景] 温柔加载已在进行中，本次请求回落原版（" + sceneName + "）");
                    return false;
                }
                _target = sceneName;
                _host.StartCoroutine(SceneManagerWrapper.DoGentleSceneLoad(sceneName));
                //协程没有 isDone；用场景是否已变成目标判完成，5 秒后自动解锁
                _runningUntil = Time.realtimeSinceStartup + 5f;
                SR.LogInfo("[场景] 温柔加载：" + sceneName);
                return true;
            } catch (Exception __ex) {
                SR.Guard.Log("温柔加载", __ex);
                return false;   // **异常也回落，绝不吞掉场景请求**
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
    // 树屋：立即开始
    // **R413**：自动投票（配置项 / 下拉框 / 记票反射 / 瞬移分支）已按用户要求整块删除，只留立即开始。
    public class LobbyTools : ITweak {
        private static ConfigEntry<KeyCode> _launchKey;        // #2

        internal static string Note = "";

        private static void SelfReg() {
            SR.LocKey("Scene", "Lobby Launch Key", "立即开始键", "Launch now key");
            SR.LocDesc("Scene", "Lobby Launch Key",
                "按这个键跳过等待直接开始关卡, 自动选票最多的那扇门. 要房主才能用.",
                "Press to start the level immediately (host only).");

        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _launchKey = plugin.Config.Bind("Scene", "Lobby Launch Key", KeyCode.None, "按此键立即开始（None=不绑，需房主）。");
                SR.RegisterTick("树屋工具", Tick);
                SR.RegisterSceneHook(Reset);
            } catch (Exception __ex) { SR.Guard.Log("树屋工具 初始化", __ex); }
        }

        private static void Reset() { Note = ""; }

        private static LevelSelectController Lsc() {
            try { return LobbyManager.instance.CurrentLevelSelectController; } catch { return null; }
        }

        private static void Tick() {
            try {
                if (!SR.GateMaster) return;
                if (!SR.Env.InTreehouse) return;
                LevelSelectController lsc = Lsc();
                if (lsc == null) return;
                if (_launchKey != null && _launchKey.Value != KeyCode.None && SR.ComboKeyDown(_launchKey)) {
                    try { DoLaunch(lsc); } catch (Exception __ex) { SR.Guard.Log("立即开始", __ex); }
                }
            } catch (Exception __ex) { SR.Guard.Log("树屋工具 Tick", __ex); }
        }

        private static void DoLaunch(LevelSelectController lsc) {
            // **R396 S2**：原来用 SR.CanDoHost()，它判的是"有无广播能力"（CanDo 的 Cap.HostOnly 分支
            //  查 Env.HasServer = NetworkServer.active）-> **离线/本地树屋恒 false**，
            //  于是单机玩（UCH 最主流玩法）里立即开始永远只弹"只有房主能做这个操作"，
            //  而 LaunchLevel(134288) 明明是纯本地调用。
            //  改判 Env.IsHost（离线时为 true，语义=本机即权威）。
            if (!SR.CanControlLocally) { SR.Notify(SR.T("仅房主可开始关卡", "Host only")); return; }
            LevelPortal[] portals = lsc.portals;
            if (portals == null || portals.Length == 0) { SR.Notify(SR.T("未找到可用关卡门", "No level portal available")); return; }
            int best = -1, bestV = -1;
            for (int i = 0; i < portals.Length; i++) {
                if (portals[i] == null) continue;
                int v = portals[i].Votes != null ? portals[i].Votes.Count : 0;
                if (v > bestV) { bestV = v; best = i; }
            }
            // **R412**：一票都没有 = 没有选中关卡 -> 不生效（原来会进"得票最多的门"= 实际是第一个门）
            if (best < 0 || bestV <= 0 || portals[best] == null) {
                SR.Notify(SR.T("尚未选定关卡，请先投票", "No level selected yet - vote first"));
                return;
            }
            lsc.LaunchLevel(portals[best]);
            SR.Notify(SR.T("关卡已开始", "Level started"));
        }










    }
}
